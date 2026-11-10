using System;

namespace Survival;

public readonly struct CombatButtonIntentSnapshot
{
	public bool IsHeld { get; init; }
	public float HoldDurationSeconds { get; init; }

	public DateTime? PressedUtc { get; init; }
	public double? PressedGlobalSeconds { get; init; }
	public double? PressedSandboxTimeNowDouble { get; init; }

	public DateTime? ReleasedUtc { get; init; }
	public double? ReleasedGlobalSeconds { get; init; }
	public double? ReleasedSandboxTimeNowDouble { get; init; }

	public Vector3? ViewDirectionOnPress { get; init; }
	/// <summary>Look direction at button release — used server-side vs press for swing/camera alignment.</summary>
	public Vector3? ViewDirectionOnRelease { get; init; }
	public Vector3? CameraPositionOnPress { get; init; }
	public Vector3? CameraPositionOnRelease { get; init; }
}

/// <summary>Post-release press cooldown only (attack and block use the same rules shape).</summary>
public readonly struct CombatChannelRules
{
	public float CooldownAfterValidReleaseSeconds { get; init; }
}

[Title( "Player Combat" )]
public partial class PlayerCombat : Component
{
	const string CombatClientLogVersion = "press-fire-v4";

	// --- Debug (inspector toggles at top) ---

	[Property, Group( "Combat — Debug" )] public bool ShowCombatInputDebug { get; set; } = true;

	[Property, Group( "Combat — Debug" )] public bool LogCombatNetworkingToConsole { get; set; }

	/// <summary>Owner: logs predicted primary-attack stamina (hold vs formula cost) when the swing window submits to the host.</summary>
	[Property, Group( "Combat — Debug" )] public bool LogAttackStaminaDebug { get; set; }

	[Property, Group( "Combat — Debug" )] public bool LogMeleeSweepHitsToConsole { get; set; }

	/// <summary>Draws attack-path overlay lines/spheres (blue / yellow / red). Gameplay sampling uses <see cref="MeleeAttackArcDegreeStep"/> regardless.</summary>
	[Property, Group( "Combat — Debug" ), Title( "Attack path overlay draw" )]
	public bool MeleeDebugDrawEnabled { get; set; }

	[Property, Group( "Combat — Debug" ), Title( "Overlay spheres at path samples" )]
	public bool MeleeDebugDrawSamplePointsEnabled { get; set; }

	/// <summary>Extra rays while turning mid-swing (one per degree of yaw at a fixed arc point). Usually off — use arc fan only.</summary>
	[Property, Group( "Combat — Debug" ), Title( "Rotation spoke overlay" )]
	public bool MeleeDebugDrawRotationSpokes { get; set; } = false;

	/// <summary>How long each overlay line/sphere stays on screen (seconds).</summary>
	[Property, Group( "Combat — Debug" ), Title( "Overlay persist (s)" )]
	public float MeleeDebugOverlayDuration { get; set; } = 1f;

	/// <summary>Duration passed to <see cref="DebugOverlay"/> for attack path overlay draws.</summary>
	internal float GetMeleeDebugOverlayDrawDuration() => Math.Max( 0.008f, MeleeDebugOverlayDuration );

	/// <summary>
	/// When networked and host, after a swing starts, broadcasts intent so clients can draw the same path overlay (visual only).
	/// </summary>
	[Property, Group( "Combat — Debug" ), Title( "Clients replicate swing path (slash ribbon + overlay)" )]
	public bool ClientMeleeSwingTraceDebug { get; set; } = true;

	[Property, Group( "Input" )] public string PrimaryAttackAction { get; set; } = "Attack1";
	[Property, Group( "Input" )] public string BlockAction { get; set; } = "Attack2";

	[Property, Group( "Combat — Block" )] public float BlockCooldownAfterRelease { get; set; } = 0.5f;

	[Property, Group( "Combat — Server validation" )] public float ServerEyeHeight { get; set; } = 64f;

	/// <summary>Scene <see cref="CombatAuthority"/> (e.g. on your network manager). If unset, <see cref="CombatAuthority.Instance"/> is used when enabled.</summary>
	[Property, Group( "Combat — Networking" )] public CombatAuthority HostCombatAuthority { get; set; }

	/// <summary>Authoritative weapon damage before camera/swing alignment (server multiplies that next).</summary>
	[Property, Group( "Combat — Melee" )] public float MeleeWeaponBaseDamage { get; set; } = 8f;

	/// <summary>
	/// Timing class used when nothing with a <c>weaponClass</c> is equipped (bare hands, entities, training dummies).
	/// Weapon-in-hand timings come from <c>data/melee_weapon_classes.json</c> via the equipped item's <c>weaponClass</c>.
	/// </summary>
	[Property, Group( "Combat — Melee" ), Title( "Fallback weapon class" )]
	public string FallbackWeaponClass { get; set; } = MeleeWeaponClassCatalog.DefaultClassId;

	/// <summary>
	/// A heavy strikes when the button comes up (never before the class heavy windup); held this long it lets go by
	/// itself, so a stuck / forgotten button cannot pin the pawn in the cocked pose.
	/// </summary>
	[Property, Group( "Combat — Timings" ), Title( "Heavy max hold (s)" ), Range( 0.6f, 10f ), Step( 0.1f )]
	public float HeavyMaxHoldSeconds { get; set; } = 3f;

	/// <summary>An Attack1 click while the previous swing is still sweeping (or a block / parry lockout runs) queues one follow-up, if it is at most this old when the chain frees up. 0 disables queueing.</summary>
	[Property, Group( "Combat — Timings" ), Title( "Queued attack max age (s)" ), Range( 0f, 2f ), Step( 0.05f )]
	public float QueuedAttackMaxAgeSeconds { get; set; } = 0.75f;

	string _resolvedTimingsEquippedId;
	int _resolvedTimingsCatalogVersion = -1;
	MeleeWeaponTimings _resolvedTimings;

	/// <summary>
	/// Timings for the weapon currently in hand: equipped item → <c>weaponClass</c> →
	/// <see cref="MeleeWeaponClassCatalog"/> profile (+ per-weapon overrides). Cached until the
	/// equipped item or the catalog changes.
	/// </summary>
	float _timingsFrameStamp = -1f;
	MeleeWeaponTimings _timingsThisFrame;

	public MeleeWeaponTimings GetMeleeWeaponTimings()
	{
		// Hot path: called per arc sample during a swing (hundreds/frame). Resolve at most once per
		// frame — inspector tuning still applies on the very next frame.
		if ( _timingsFrameStamp == Time.Now )
			return _timingsThisFrame;

		var equippedId = Components.Get<PlayerEquippedItem>()?.EquippedResourceId ?? string.Empty;

		// Inspector library present → resolve LIVE every call so play-mode tuning hits the next
		// swing immediately. The cache only serves the JSON-file path.
		var liveLibrary = MeleeWeaponTimingLibrary.Instance is { } lib && lib.IsValid();
		if ( !liveLibrary )
		{
			MeleeWeaponClassCatalog.EnsureLoaded();
			if ( _resolvedTimingsCatalogVersion == MeleeWeaponClassCatalog.Version
			     && string.Equals( _resolvedTimingsEquippedId, equippedId, StringComparison.OrdinalIgnoreCase ) )
			{
				_timingsThisFrame = _resolvedTimings;
				_timingsFrameStamp = Time.Now;
				return _resolvedTimings;
			}
		}

		string classId = null;
		MeleeTimingOverridesData overrides = null;
		if ( EquipmentCatalog.TryGet( equippedId, out var profile ) && profile is not null )
		{
			classId = profile.WeaponClass;
			overrides = profile.MeleeOverrides;
		}

		if ( string.IsNullOrWhiteSpace( classId ) )
			classId = FallbackWeaponClass;

		var resolved = MeleeWeaponClassCatalog.Resolve( classId, overrides );
		if ( !liveLibrary )
		{
			_resolvedTimings = resolved;
			_resolvedTimingsEquippedId = equippedId;
			_resolvedTimingsCatalogVersion = MeleeWeaponClassCatalog.Version;
		}

		_timingsThisFrame = resolved;
		_timingsFrameStamp = Time.Now;
		return resolved;
	}

	[Property, Group( "Combat — Melee (attack action)" )] public GameObject MeleeBladeTip { get; set; }

	[Property, Group( "Combat — Melee (attack action)" )] public GameObject MeleeBladeHeel { get; set; }

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Show windup direction telegraph" )]
	public bool ShowMeleeAttackWindupTelegraph { get; set; }

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Windup telegraph line thickness" )]
	public float MeleeWindupTelegraphThickness { get; set; } = 6f;

	/// <summary>Click → damage seconds for the equipped weapon class (the cocked pose hangs this long).</summary>
	public float GetMeleeWindupSeconds() =>
		GetMeleeWeaponTimings().WindupSeconds;

	/// <summary>Damage-swoop seconds for the equipped weapon class.</summary>
	public float GetMeleeActiveSeconds() =>
		GetMeleeWeaponTimings().ActiveSeconds;

	/// <summary>Blade reach in engine units for this attack type — class meters converted once per pawn (BodyHeight/1.8).</summary>
	public float GetMeleeAttackRangeUnits( byte attackType )
	{
		var t = GetMeleeWeaponTimings();
		var meters = attackType is MeleeAttackTypes.Stab or MeleeAttackTypes.Forward
			? t.ReachForwardMeters
			: t.ReachLateralMeters;
		return meters * GetPawnUnitsPerMeter();
	}

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Hit volume thickness" )]
	public float MeleeHitVolumeThickness { get; set; } = 2f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Sweep substep length" )]
	public float MeleeSweepSubstepLength { get; set; } = 12f;

	/// <summary>Combat damage multiplier on every swing (normally <see cref="MeleeCombatDamageMultiplier.Standard"/>).</summary>
	[Property, Group( "Combat — Melee" ), Title( "Base combat damage multiplier" )]
	public float MeleeBaseCombatDamageMultiplier { get; set; } = MeleeCombatDamageMultiplier.Standard;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Allow multiple hits per attack" )]
	public bool MeleeAllowMultipleHitsPerAttack { get; set; }

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Max targets hit" )]
	public int MeleeMaxTargetsHit { get; set; } = 1;

	/// <summary>Lateral slash span (°) — from the weapon class (`lateralArcDegrees` in melee_weapon_classes.json).</summary>
	public float GetMeleeLateralArcDegrees() => GetMeleeWeaponTimings().LateralArcDegrees;

	/// <summary>Overhead arc span (°) — from the weapon class (`forwardArcTotalDegrees`).</summary>
	public float GetMeleeForwardArcTotalDegrees() => GetMeleeWeaponTimings().ForwardArcTotalDegrees;

	/// <summary>Overhead arc start angle (°, 0 = forward, 90 = up) — from the weapon class (`forwardArcStartDegrees`).</summary>
	public float GetMeleeForwardArcStartDegrees() => GetMeleeWeaponTimings().ForwardArcStartDegrees;

	/// <summary>
	/// Spacing along the attack path and per degree of body turn (1 = one ray every 1° along the arc; 150° → ~151 samples).
	/// Drives core path sampling; overlay lines use <see cref="MeleeDebugDrawEnabled"/>.
	/// </summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Attack arc degree step" )]
	public float MeleeAttackArcDegreeStep { get; set; } = 1f;

	internal float GetMeleeAttackArcDegreeStep() => Math.Max( 1f, MeleeAttackArcDegreeStep );

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Tilt left (° start→end drop only)" )]
	public float MeleeAttackTiltDegreesLeft { get; set; } = 25f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Tilt right (° start→end drop only)" )]
	public float MeleeAttackTiltDegreesRight { get; set; } = 25f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Tilt forward (° right-plane offset)" )]
	public float MeleeAttackTiltDegreesForward { get; set; } = 10f;

	/// <summary>Baseline offset along combat-basis up from <see cref="ServerEyeHeight"/> for L/R slashes (basis pitch tips the whole arc).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Side slash start height" )]
	public float MeleeAttackZaxisStart { get; set; } = -10f;

	/// <summary>Max forward local reach as a fraction of the class forward reach.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward max reach (× attackRangeForward)" )]
	public float MeleeAttackForwardMaxReachFraction { get; set; } = 1f;

	/// <summary>Scales overhead +X reach vs lateral-matched path (1 = same; lower = shorter forward).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward path reach scale" )]
	public float MeleeAttackForwardPathReachScale { get; set; } = 0.94f;

	/// <summary>Cos scale at windup; ramps to <see cref="MeleeAttackForwardArcForwardScale"/> by mid-stroke.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward arc forward scale (start)" )]
	public float MeleeAttackForwardArcForwardScaleStart { get; set; } = 1f;

	/// <summary>Cos scale at mid-stroke; <see cref="MeleeAttackForwardArcForwardScaleStart"/> ramps into this. Read every overhead swing by <c>MeleeAttackPath</c>.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward arc forward scale" )]
	public float MeleeAttackForwardArcForwardScale { get; set; } = 1f;

	/// <summary>Scales sin (up/down) component — higher exaggerates up-then-down.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward arc vertical scale" )]
	public float MeleeAttackForwardArcVerticalScale { get; set; } = 1.02f;


	/// <summary>Overhead arc pivot: combat-local +X from body origin at stroke start (head/shoulder, not weapon reach).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward pivot forward local (start)" )]
	public float MeleeAttackForwardPivotForwardLocal { get; set; } = 8f;

	/// <summary>Overhead arc pivot forward at stroke end (lerps with start over the swing).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward pivot forward local (end)" )]
	public float MeleeAttackForwardPivotForwardLocalEnd { get; set; } = 10f;

	/// <summary>Added to <see cref="ServerEyeHeight"/> for arc pivot height (0 = eye level; negative ≈ neck/shoulder beside head).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward pivot up from eye" )]
	public float MeleeAttackForwardPivotUpFromEye { get; set; } = 0f;

	/// <summary>Combat-local right offset for arc pivot only (blade still uses plane right offset).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward pivot right offset" )]
	public float MeleeAttackForwardPivotRightOffset { get; set; } = 0f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward reach start multiplier" )]
	public float MeleeAttackForwardReachStartMultiplier { get; set; } = 1f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward reach active multiplier" )]
	public float MeleeAttackForwardReachActiveMultiplier { get; set; } = 1f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward reach end multiplier" )]
	public float MeleeAttackForwardReachEndMultiplier { get; set; } = 1f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward plane right offset" )]
	public float MeleeAttackForwardPlaneRightOffset { get; set; } = 12f;

	float _meleeSwingForwardOffsetMeters = 0.2f;

	/// <summary>Pushes every melee arc away from the body along the swing basis forward. Designer meters → pawn units via BodyHeight/1.8.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Swing forward offset (m)" )]
	public float MeleeSwingForwardOffsetMeters
	{
		get => _meleeSwingForwardOffsetMeters;
		set
		{
			_meleeSwingForwardOffsetMeters = value;
			_meleeSwingForwardOffsetUnitsCache = float.NaN;
		}
	}

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Swing min pitch (° look-up cap)" )]
	public float MeleeSwingMinPitchDegrees { get; set; } = -50f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Swing max pitch (° look-down cap)" )]
	public float MeleeSwingMaxPitchDegrees { get; set; } = 50f;

	/// <summary>How long a pawn this swing hits flails (Mark 2026-11-04: 100 ms). 0 = no hit reaction.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Victim hit reaction (s)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float MeleeHitReactionSeconds { get; set; } = 0.1f;


	[Property, Group( "Combat — Melee (attack action)" )] public float MeleeBladeHeelFraction { get; set; } = 0.22f;

	/// <summary>Filled when the host finishes the phased sweep (after windup/active/recovery).</summary>
	public MeleeSweepOutcomeSummary LastMeleeSweepSummary { get; private set; }

	[Property, Group( "Combat — Swing from look" )] public float SwingAxisDeadzone { get; set; } = 0.02f;

	/// <summary>
	/// When true, inverts left/right attack selection (southpaw). Forward overhead is unchanged.
	/// Only read when resolving attack type at attack start — not re-read mid-swing.
	/// </summary>
	[Property, Group( "Combat — Swing from look" )] public bool SouthpawSwing { get; set; }

	/// <summary>If true, treat negative <see cref="Input.MouseDelta"/>.y (mouse moved up the screen) as look-up for <see cref="SwingDirs.Up"/>.</summary>
	[Property, Group( "Combat — Swing from look" )] public bool SwingInvertLookYForUp { get; set; } = true;

	/// <summary>
	/// Minimum |accumulated evidence| (pixels) on an axis before it can steer the teardrop; raise to reduce jitter.
	/// Compared against the time-decayed pixel accumulator, not a single frame's <see cref="Input.MouseDelta"/>.
	/// </summary>
	[Property, Group( "Combat — Swing from look" )] public float SwingLiveMicroMotionPixels { get; set; } = 0.9f;

	/// <summary>
	/// Stronger axis must exceed the weaker by this ratio in the decayed evidence before the cardinal may change (stops flicker when |x|≈|y|).
	/// </summary>
	[Property, Group( "Combat — Swing from look" )] public float SwingLiveAxisWinRatio { get; set; } = 1.42f;

	/// <summary>
	/// Minimum |dominant-axis evidence| (pixels, time-decayed) toward a new cardinal before the teardrop / live dir actually flips.
	/// This is a pure pixel gate — the integration is framerate-independent via <see cref="SwingEvidenceDecaySeconds"/>.
	/// </summary>
	[Property, Group( "Combat — Swing from look" )] public float SwingFlipCommitPixels { get; set; } = 7f;

	/// <summary>
	/// Time constant (seconds) for the exponential decay of the per-channel pixel evidence accumulator.
	/// Larger = more inertia / less flicker, smaller = more responsive. ~0.15–0.25s feels good for mouse aim.
	/// </summary>
	[Property, Group( "Combat — Swing from look" )] public float SwingEvidenceDecaySeconds { get; set; } = 0.18f;

	/// <summary>Screen-space length (pixels) below which mouse delta is ignored for swing evidence / teardrop (not post-release drag).</summary>
	[Property, Group( "Combat — Swing from look" )] public float SwingMouseEvidenceDeadzonePixels { get; set; } = 2.25f;

	/// <summary>Max pixels of movement applied to swing evidence in one frame (same direction as raw delta; stops spikes from flipping cardinals).</summary>
	[Property, Group( "Combat — Swing from look" )] public float SwingMouseEvidenceMaxStepPixels { get; set; } = 26f;

	/// <summary>
	/// Real-time minimum hold (milliseconds) between cardinal flips. Hard anti-oscillation guard; lateral L↔R flips use 1.5× this.
	/// Framerate-independent: at any FPS the cardinal can change at most ~(1000 / this) times per second.
	/// </summary>
	[Property, Group( "Combat — Swing from look" )] public float SwingMinFlipHoldMs { get; set; } = 60f;

	/// <summary>
	/// Extra commit pixels required when flipping <b>Left ↔ Right</b> (on top of <see cref="SwingFlipCommitPixels"/>), so lateral wobble doesn’t snap instantly back and forth.
	/// </summary>
	[Property, Group( "Combat — Swing from look" )] public float SwingLateralFlipExtraPixels { get; set; } = 12f;

	/// <summary>
	/// When already on <b>L/R</b>, treat upward diagonals as <see cref="SwingDirs.Up"/> if <c>|up| ≥ |horizontal| × ratio</c>
	/// — lower than <see cref="SwingLiveAxisWinRatio"/> so uppercuts (e.g. left → top-right) don’t stick on <b>R</b>.
	/// </summary>
	[Property, Group( "Combat — Swing from look" )] public float SwingUpFromSideWinRatio { get; set; } = 1.04f;

	/// <summary>
	/// Scale for <see cref="SwingFlipCommitPixels"/> when flipping <b>L/R → Up</b> (&lt; 1 = easier / faster to commit to an upper swing).
	/// </summary>
	[Property, Group( "Combat — Swing from look" )] public float SwingFlipCommitUpFromSideMul { get; set; } = 0.42f;

	[Property, Group( "Combat — Server validation" )]
	public bool EnableServerPrimaryAttackValidation { get; set; } = true;

	public AttackReleaseResult LastServerAttackResult { get; private set; }

	/// <summary>Client-only predicted hit target after swing window (same trace path as host; no damage).</summary>
	public Guid LastLocalPredictedHitForVfx { get; private set; }

	public CombatButtonIntentSnapshot Block => _block.Snapshot;

	readonly CombatChannel _block = new();

	ushort _attackIntentSequence;
	string _combatNetDiag = "—";

	bool _combatAuthorityDiagComplete;
	AttackReleaseIntent _pendingSwingVisualIntent;
	bool _hasPendingSwingVisualIntent;

	/// <summary>
	/// Per-hold sum of raw <see cref="Input.MouseDelta"/> (screen pixels) while the block button is down.
	/// Pure mouse delta — independent of pawn movement, strafing, or camera world rotation, so L/R/U
	/// classification reflects *only* what the player flicked with the mouse during the hold.
	/// </summary>
	Vector2 _blockLookAccum;
	bool _wasBlockButtonDownLastFrame;

	/// <summary>Live L/R/U from mouse evidence — block preview uses screen-aligned cardinals; attack stores mirrored L/R for combat.</summary>
	byte _primaryLiveSwingDir = SwingDirs.Up;

	/// <summary>anim_test: the pawn keeps PlayerCombat running (the stance reads it) but reads no input.</summary>
	internal bool InputSuppressedForTests { get; set; }

	/// <summary>anim_test: set the live aim side as mouse movement would (no input is read on the test pawn).</summary>
	internal void TestSetLiveSwingDir( byte dir )
	{
		_primaryLiveSwingDir = dir;
	}

	/// <summary>Attack type the next Attack1 click would start (the live cursor solve) — the animation rests the sword on that side's shoulder.</summary>
	public byte LivePrimaryAttackType =>
		ResolveAttackTypeFromCursorDir( _primaryLiveSwingDir );
	byte _blockLiveSwingDir = SwingDirs.Up;

	/// <summary>Time-decayed signed pixel evidence (screen-space; y positive = mouse moved down) — framerate-independent input to the classifier.</summary>
	Vector2 _primarySwingEvidence;
	Vector2 _blockSwingEvidence;
	/// <summary><see cref="RealTime.GlobalNow"/> of the last committed cardinal flip; gated by <see cref="SwingMinFlipHoldMs"/>.</summary>
	double _primaryLastFlipRealSeconds;
	double _blockLastFlipRealSeconds;

	/// <summary>Previous combat yaw while blocking — used to rotate swing evidence so look does not morph guard.</summary>
	float _blockGuardPrevYaw;
	bool _blockGuardYawTracking;

	Vector2 _blockReleaseSwingXz;
	float _blockReleaseSwingVerticalHint;
	byte _blockReleaseSwingDir;

	/// <summary>Direction the click locked (L / R / U) — the swing commits on the click, so this is the swing direction.</summary>
	byte _lockedPrimaryAttackSwingDir;
	/// <summary>Attack1 clicked while the chain was busy — fires one follow-up when the chain frees (aged by <see cref="QueuedAttackMaxAgeSeconds"/>), in the direction locked at that click.</summary>
	bool _hasQueuedAttackPress;
	double _queuedAttackPressedAtSandbox;
	byte _queuedAttackSwingDir;

	// Owner: the swing this client last clicked — held past the light windup it turns heavy (owner predicts the
	// longer hang; the host decides from whether the release reached it before its own windup ended).
	bool _primaryWasDownLastFrame;
	bool _ownerSwingTracking;
	ushort _ownerSwingSeq;
	double _ownerSwingClickAt;
	bool _ownerSwingHolding;
	bool _ownerSwingHeavy;
	bool _ownerHeavyFlashPlayed;

	// Host: the last swing whose owner let go before the light windup ended (that swing stays light).
	ushort _hostReleasedSeq;
	bool _hostReleasedValid;
	/// <summary>Owner expects the host melee action to still be running (online clients without local runtime).</summary>
	bool _ownerExpectsHostMeleeBusy;

	/// <summary><see cref="Time.NowDouble"/> when <see cref="_ownerExpectsHostMeleeBusy"/> was last set true.</summary>
	double _ownerExpectsHostMeleeBusySince;

	/// <summary>
	/// Safety: if RpcOwner sweep-complete / reject is dropped, unlock Attack1 after this many seconds
	/// (covers full windup+active+recovery with margin).
	/// </summary>
	const float OwnerExpectsHostMeleeBusyTimeoutSeconds = 2.75f;

	/// <summary>When set, non-local attack/block paths use intent yaw instead of pawn body rotation.</summary>
	float? _meleeIntentBasisYawOverride;
	/// <summary>Look yaw (PlayerController.EyeAngles, synced from the owner) when the swing committed — the turn since then carries the arc.</summary>
	float _meleeSwingLastEyeYaw;
	/// <summary>Turn since the swing committed, summed frame by frame so ±180° (all the way round) never wraps.</summary>
	float _meleeSwingTurnAccum;
	float _meleeSwingTurnUpdatedAt;
	bool _meleeSwingTurnTracked;
	bool _meleeIntentPitchCaptured;
	float _meleeIntentStartPitchDegrees;

	bool _windupTelegraphActive;
	byte _windupTelegraphAttackType;
	float _windupTelegraphBasisYaw;
	bool _lastSentWindupTelegraphActive;
	byte _lastSentWindupTelegraphAttackType;
	float _lastSentWindupTelegraphBasisYaw;
	bool _lastSentWindupTelegraphValid;
	bool _lastBroadcastWindupTelegraphActive;
	byte _lastBroadcastWindupTelegraphAttackType;
	float _lastBroadcastWindupTelegraphBasisYaw;
	bool _lastBroadcastWindupTelegraphValid;

	internal bool IsLocalCombatDriver()
	{
		if ( GameObject.IsProxy )
			return false;

		if ( GameObject.Network is { Active: true } n )
		{
			if ( n.Owner is null )
			{
				if ( !Networking.IsHost )
					return false;
			}
			else if ( !n.IsOwner )
				return false;
		}

		// Only pawns with an enabled PlayerController read global Input (not training dummies / combat-only props).
		var controller = GameObject.Components.Get<PlayerController>();
		return controller is not null && controller.Enabled;
	}

	protected override void OnUpdate()
	{
		if ( GameObject.IsValid() )
		{
			// Proxy pawns on the listen server are driven by CombatAuthority.TickSceneCombatVisualizations
			// (avoids double-ticking when both run). Local / offline pawns tick here.
			if ( !GameObject.IsProxy || !Networking.IsActive )
				MaybeTickServerMeleeAttackAction();

			TickMeleeAttackLookLockFromSync();

			if ( IsLocalCombatDriver() )
			{
				// CombatAuthority already ticks companions scene-wide; avoid double-advance (sparse arcs).
				if ( CombatAuthority.Instance is null || !CombatAuthority.Instance.IsValid() )
					TickAllRemoteCombatVisualizationsInScene();
				TickWindupTelegraphNetworkState();
			}
		}

		if ( !Active || !GameObject.IsValid() )
			return;

		MaybeWarnCombatAuthorityMisconfigured();

		// Host-simulated client pawns are proxies — their authority timers tick from
		// CombatAuthority.TickSceneCombatVisualizations(driveHostProxyAuthority: true), not here
		// (avoids double-ticking when both run).
		if ( IsServerSideForMeleeAuthority() && !GameObject.IsProxy )
		{
			ServerTickMeleeBlockTimers();
			ServerTickCombatRecovery();
		}

		TickLocalCombatRecoveryPresentation();
		TickMeleePhaseReadyDebug();

		if ( IsServerSideForMeleeAuthority() && !IsLocalCombatDriver() && !( GameObject.IsProxy && !Networking.IsHost ) )
			TickAuthoritativeMeleeBlockState();

		if ( !IsLocalCombatDriver() )
			return;

		// anim_test pawn: everything else runs as for a player, but it never reads the editor's input.
		if ( InputSuppressedForTests )
			return;

		// Dev: console `swing` / `block` hold the attack / block action for a few frames (GameHacks) — same path as a real press.
		GameHacks.ApplySimulatedInput( PrimaryAttackAction, BlockAction, SpecialAttackAction );

		var menuController = Components.Get<PlayerGameMenuController>();
		if ( menuController is not null && menuController.IsMenuOpen )
		{
			CancelBowCharge();
			_bowAdsActive = false;
			return;
		}

		// Vehicle driver: hands on the wheel, no attacks or blocks (passengers keep everything).
		if ( Components.Get<PlayerMovement>() is { IsSeatedDriver: true } or { PodLocked: true } )
		{
			CancelBowCharge();
			_bowAdsActive = false;
			return;
		}

		// Shove is a player ability, not a weapon one — it runs before the melee-item gate below and
		// works bare-handed. (Equipment used to disable this whole component when no melee item was
		// held, which killed the shove, the hit reaction, and the jump/grapple locks along with it.)
		TickOwnerShoveInput();

		// Bow / ranged hold-to-charge — separate from melee; classic crosshair (no teardrop).
		TickOwnerRangedInput();
		TickOwnerBolaInput();

		// Everything past here is the sword and the shield: swing input, block, telegraphs. A melee weapon in
		// the main hand opens both; a shield in the offhand (`Block` action) opens the guard on its own
		// (Mark 2026-11-02: "shield blocks alone"), so the swing paths below stay behind `hasMeleeWeapon`.
		var equipped = Components.Get<PlayerEquippedItem>();
		var hasMeleeWeapon = equipped is null || equipped.HasAction( EquippedItemActions.PrimaryMelee );
		if ( !hasMeleeWeapon && !HasOffhandBlockItem() )
			return;

		_combatNetDiag = $"netActive={GameObject.Network is { Active: true }} isHost={Networking.IsHost}";

		if ( hasMeleeWeapon )
		{
			// Click = attack (Mark 2026-11-04): the swing commits on the click — the sword snaps to its cocked
			// pose and the damage swoop opens one class windup later. Holding the button does nothing more.
			// A click while the previous swing is still sweeping (or a block / parry lockout runs) queues
			// exactly one follow-up in the direction locked at that click; it fires the moment the chain frees.
			// The click is the button going down (a rising edge of Input.Down): the same as Input.Pressed for a real
			// mouse, and correct for the `swing` dev hold, whose Input.SetAction reports Pressed on every held frame.
			var primaryDown = Input.Down( PrimaryAttackAction );
			var clicked = primaryDown && !_primaryWasDownLastFrame;
			_primaryWasDownLastFrame = primaryDown;
			// Dev toggle: static right → left → overhead combo + Q special, the mouse picks nothing (PlayerCombat.StaticCombo.cs).
			if ( StaticComboActive )
				TickOwnerStaticCombo( clicked );
			else if ( clicked && !IsBlockPreventingAttack() )
			{
				LockPreparedPrimaryAttackDirection();
				if ( IsMeleeAttackChainBusy() )
				{
					QueueAttackPress( _lockedPrimaryAttackSwingDir );
					LogCombatDiag( "CLIENT / OWNER", $"Attack click queued — {FormatMeleePhaseBusyReason()}" );
				}
				else
					TryFirePrimaryAttack( _lockedPrimaryAttackSwingDir, "click" );
			}

			MaybeFireQueuedAttackPress();
			TickOwnerHeavyHold();
		}

		_block.Step( BlockAction, CanStartBlock, CanContinueBlock, GetViewDirectionForIntent, GetCameraPositionForIntent, GetBlockRules(), OnOwnerValidBlockRelease, allowHeldStart: true );

		var blockStartedThisFrame = _block.Down && !_wasBlockButtonDownLastFrame;
		if ( blockStartedThisFrame )
		{
			_blockLookAccum = default;
			OnBlockPressCommitGuardDirection();
			CancelAllAttackActivity();
			ResetOwnerStaticCombo();
		}

		_wasBlockButtonDownLastFrame = _block.Down;

		TickSwingLookAccumulatorsAfterCombatStep();

		TickAuthoritativeMeleeBlockState();
		TickCombatStateMachine();

		if ( ShowCombatInputDebug )
			DrawCombatInputDebug();

		if ( ShouldDrawMeleeBlockVisualization() )
			DrawMeleeBlockGuardVisualization();

		DrawWindupTelegraphIfNeeded();
	}

	void TickSwingLookAccumulatorsAfterCombatStep()
	{
		var dt = MathF.Max( 0f, Time.Delta );
		var decay = SwingEvidenceDecaySeconds > 1e-4f ? MathF.Exp( -dt / SwingEvidenceDecaySeconds ) : 0f;
		var rawFrame = Input.MouseDelta;
		var frame = FilterSwingMouseEvidenceDelta( rawFrame );

		// Primary: the live teardrop keeps following the mouse; each click locks its own direction.
		_primarySwingEvidence = _primarySwingEvidence * decay + frame;
		ApplyLiveSwingFromEvidence( _primarySwingEvidence, ref _primaryLiveSwingDir, ref _primaryLastFlipRealSeconds,
			invertAttackLateral: true );

		// Block: rotate decayed evidence with view yaw so look spin does not flip L/R/U; morph only on teardrop intent.
		var blockHeld = LocalBlockInputActive();
		if ( blockHeld )
		{
			var yaw = GetBlockCombatBasisYaw();
			if ( !_blockGuardYawTracking )
			{
				_blockGuardPrevYaw = yaw;
				_blockGuardYawTracking = true;
			}

			var yawDelta = NormalizeDegreesDelta( yaw - _blockGuardPrevYaw );
			_blockGuardPrevYaw = yaw;
			if ( MathF.Abs( yawDelta ) > 1e-4f )
				_blockSwingEvidence = RotateSwingEvidenceDegrees( _blockSwingEvidence, -yawDelta );

			_blockSwingEvidence = _blockSwingEvidence * decay + frame;
			ApplyLiveSwingFromEvidence( _blockSwingEvidence, ref _heldBlockGuardDir, ref _blockLastFlipRealSeconds );
			_blockLiveSwingDir = _heldBlockGuardDir;
		}
		else
		{
			_blockGuardYawTracking = false;
			_blockSwingEvidence = _blockSwingEvidence * decay + frame;
			ApplyLiveSwingFromEvidence( _blockSwingEvidence, ref _blockLiveSwingDir, ref _blockLastFlipRealSeconds );
		}

		if ( Input.Down( BlockAction ) )
			_blockLookAccum += rawFrame;
	}

	/// <summary>Deadzone + per-frame cap on magnitude; direction preserved. Used only for swing cardinal evidence / crosshair, not drag sums.</summary>
	Vector2 FilterSwingMouseEvidenceDelta( Vector2 raw )
	{
		var lenSq = raw.LengthSquared;
		var dz = Math.Max( 0f, SwingMouseEvidenceDeadzonePixels );
		if ( lenSq < dz * dz )
			return default;

		var len = MathF.Sqrt( lenSq );
		var dir = raw / len;
		var cap = Math.Max( dz, SwingMouseEvidenceMaxStepPixels );
		var mag = MathF.Min( len, cap );
		return dir * mag;
	}

	static float NormalizeDegreesDelta( float delta )
	{
		while ( delta > 180f )
			delta -= 360f;
		while ( delta < -180f )
			delta += 360f;
		return delta;
	}

	static Vector2 RotateSwingEvidenceDegrees( Vector2 v, float deltaDegrees )
	{
		var rad = deltaDegrees * (MathF.PI / 180f);
		var c = MathF.Cos( rad );
		var s = MathF.Sin( rad );
		return new Vector2( v.x * c - v.y * s, v.x * s + v.y * c );
	}

	void LockPreparedPrimaryAttackDirection()
	{
		var dt = MathF.Max( 0f, Time.Delta );
		var decay = SwingEvidenceDecaySeconds > 1e-4f ? MathF.Exp( -dt / SwingEvidenceDecaySeconds ) : 0f;
		var e = _primarySwingEvidence * decay + FilterSwingMouseEvidenceDelta( Input.MouseDelta );
		_primarySwingEvidence = e;
		_lockedPrimaryAttackSwingDir = ClassifyAttackLiveSwingFrame( e, _primaryLiveSwingDir );
	}

	void QueueAttackPress( byte swingDir, byte comboStep = 0, bool special = false )
	{
		if ( QueuedAttackMaxAgeSeconds <= 1e-4f )
			return;

		_hasQueuedAttackPress = true;
		_queuedAttackPressedAtSandbox = Time.NowDouble;
		_queuedAttackSwingDir = swingDir;
		_queuedComboStep = comboStep;
		_queuedSpecial = special;
	}

	void ClearQueuedAttackPress() => _hasQueuedAttackPress = false;

	/// <summary>Owner: fire the single queued follow-up click once the melee chain is free.</summary>
	void MaybeFireQueuedAttackPress()
	{
		if ( !_hasQueuedAttackPress )
			return;

		if ( Time.NowDouble - _queuedAttackPressedAtSandbox > Math.Max( 0f, QueuedAttackMaxAgeSeconds ) )
		{
			_hasQueuedAttackPress = false;
			return;
		}

		if ( IsOwnerAttackGateBusy() || IsBlockPreventingAttack() )
			return;

		_hasQueuedAttackPress = false;
		TryFirePrimaryAttack( _queuedAttackSwingDir, "queued click", _queuedComboStep, _queuedSpecial );
	}

	/// <summary>
	/// Owner: commit one swing now — cocked pose on screen at once, strike predicted one class windup later,
	/// intent to the host (which re-validates once and owns the damage window).
	/// </summary>
	bool TryFirePrimaryAttack( byte swingDir, string source, byte comboStep = 0, bool special = false )
	{
		if ( IsActiveMainHandBroken() )
			return false;

		var vitals = Components.Get<PlayerVitals>();
		if ( vitals is not null && !vitals.CanAffordStamina( GetPrimaryAttackStaminaCost() ) )
			return false;

		if ( !EnableServerPrimaryAttackValidation )
		{
			_combatNetDiag = "validation OFF — no intent sent";
			LogCombatDiag( "CLIENT / OWNER", "EnableServerPrimaryAttackValidation is false — no intent sent." );
			return false;
		}

		var intent = BuildPrimaryAttackIntent( swingDir, GetViewDirectionForIntent(), GetCameraPositionForIntent(), comboStep, special );

		var animation = Components.Get<PlayerAnimation>();
		animation?.BeginMeleeSwingSnap( intent.AttackType );
		// Owner prediction of the host's windup; the host re-schedules with its own number when it accepts.
		animation?.ReleaseMeleeSwingSnap( intent.AttackType, special ? GetMeleeWeaponTimings().SpecialWindupSeconds : GetMeleeWindupSeconds() );

		if ( LogAttackStaminaDebug )
			Log.Info( $"[PlayerCombat/Stamina] predict cost={GetPrimaryAttackStaminaCost():0.#}" );

		_combatNetDiag = $"{source} → dispatch";
		LogCombatDiag( "CLIENT / OWNER",
			$"{CombatClientLogVersion} — {source} seq={intent.IntentSequence} dir={SwingDirs.Letter( intent.SwingDir )} — dispatch now" );

		DispatchPrimaryAttackReleaseToAuthority( intent );

		_ownerSwingTracking = true;
		_ownerSwingSeq = intent.IntentSequence;
		_ownerSwingClickAt = Time.NowDouble;
		_ownerSwingHeavy = false;
		_ownerHeavyFlashPlayed = false;
		OnOwnerSwingFired( comboStep, special );
		// A queued click fires later: it is only a heavy if the button is (still / again) held when it fires.
		// The Q special never turns heavy.
		_ownerSwingHolding = !special && Input.Down( PrimaryAttackAction );
		if ( !_ownerSwingHolding )
			SubmitPrimaryAttackReleased( _ownerSwingSeq );
		return true;
	}

	/// <summary>
	/// Owner: the button came up. Before the light windup ended the swing stays light; on a heavy it is the strike
	/// (never earlier than the heavy windup). Either way the host hears it (it decides on its own clock).
	/// </summary>
	void TickOwnerHeavyHold()
	{
		if ( !_ownerSwingTracking )
			return;

		if ( _ownerSwingHolding && !Input.Down( PrimaryAttackAction ) )
		{
			_ownerSwingHolding = false;
			SubmitPrimaryAttackReleased( _ownerSwingSeq );
			if ( _ownerSwingHeavy )
			{
				var strikeAt = Math.Max( Time.NowDouble, _ownerSwingClickAt + GetMeleeWeaponTimings().HeavyWindupSeconds );
				Components.Get<PlayerAnimation>()?.RescheduleOwnerStrike( strikeAt );
			}
		}

		// Heavy-ready glint on the owner's own clock: the swing went heavy and the heavy windup has run.
		if ( _ownerSwingHeavy && !_ownerHeavyFlashPlayed
		     && Time.NowDouble - _ownerSwingClickAt >= GetMeleeWeaponTimings().HeavyWindupSeconds )
		{
			_ownerHeavyFlashPlayed = true;
			PlayHeavyReadyFlashLocal();
		}

		if ( Time.NowDouble - _ownerSwingClickAt > Math.Max( HeavyMaxHoldSeconds, GetMeleeWeaponTimings().HeavyWindupSeconds ) + 0.5 )
			_ownerSwingTracking = false;
	}

	/// <summary>Owner: the held / queued swing was cancelled (right click raises the guard) — stop tracking it.</summary>
	void ClearOwnerSwingTracking() => _ownerSwingTracking = false;

	/// <summary>
	/// Owner prediction, asked by <see cref="PlayerAnimation"/> the moment the light strike would fire: still holding
	/// (and able to pay the heavy's extra stamina) → this swing is heavy and the cocked pose keeps hanging until the
	/// release (<see cref="TickOwnerHeavyHold"/> moves the strike there) or <see cref="HeavyMaxHoldSeconds"/>
	/// (<paramref name="strikeAtSandbox"/>).
	/// </summary>
	internal bool TryExtendOwnerSwingToHeavy( out double strikeAtSandbox )
	{
		strikeAtSandbox = 0;
		if ( !_ownerSwingTracking || _ownerSwingHeavy || !_ownerSwingHolding || _ownerSwingSpecial || !IsLocalCombatDriver() )
			return false;

		var vitals = Components.Get<PlayerVitals>();
		if ( vitals is not null && !vitals.CanAffordStamina( GetHeavyExtraStaminaCost() ) )
			return false;

		_ownerSwingHeavy = true;
		OnOwnerSwingTurnedHeavy();
		strikeAtSandbox = _ownerSwingClickAt + Math.Max( HeavyMaxHoldSeconds, GetMeleeWeaponTimings().HeavyWindupSeconds );
		return true;
	}

	void SubmitPrimaryAttackReleased( ushort seq )
	{
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
			HostNotePrimaryAttackReleased( seq );
		else
			RpcSubmitPrimaryAttackReleased( seq );
	}

	[Rpc.Host]
	void RpcSubmitPrimaryAttackReleased( ushort seq )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		HostNotePrimaryAttackReleased( seq );
	}

	/// <summary>Host: the owner let go of this swing's button before the light windup ended (also the combat_test entry).</summary>
	internal void HostNotePrimaryAttackReleased( ushort seq )
	{
		if ( _hostReleasedValid && (ushort)(seq - _hostReleasedSeq) >= 0x8000 )
			return;   // an older release arriving late changes nothing

		_hostReleasedSeq = seq;
		_hostReleasedValid = true;
	}

	/// <summary>
	/// A release for this swing or any later one counts: a later click means this swing's button came up first.
	/// Sequence numbers wrap, so "later" is the half of the ring ahead.
	/// </summary>
	internal bool HostWasPrimaryAttackReleased( ushort seq ) =>
		_hostReleasedValid && (ushort)(_hostReleasedSeq - seq) < 0x8000;

	/// <summary>A shield (or any OffHand item with the <see cref="EquippedItemActions.Block"/> action) opens the guard without a melee weapon out.</summary>
	bool HasOffhandBlockItem() =>
		Components.Get<PlayerEquipment>() is { } equipment && equipment.OffHandHasAction( EquippedItemActions.Block );

	/// <summary>Block and attack are mutually exclusive — holding guard rejects new/ongoing primary attack intents.</summary>
	bool IsBlockPreventingAttack() =>
		LocalBlockInputActive() || IsAuthoritativeMeleeBlocking;

	/// <summary>The guard is up as this peer sees it: the owner's held block, or the host-confirmed block on other peers. Drives the shield raise (<c>PlayerAnimation.Shield.cs</c>).</summary>
	public bool IsGuardShown => IsBlockPreventingAttack();

	// Consumed-awaiting-release stays a hard gate: a broken/eaten block requires an actual release
	// before re-guarding — held-start must not bypass that, only the cooldown/recovery wait.
	protected virtual bool CanStartBlock() =>
		!IsCombatActionLocked && !_meleeBlockConsumedAwaitingRelease;

	protected virtual bool CanContinueBlock() =>
		!IsCombatActionLocked && !_meleeBlockConsumedAwaitingRelease;

	bool LocalBlockInputActive() =>
		IsLocalCombatDriver() && _block.Down && !_meleeBlockConsumedAwaitingRelease;

	/// <summary>
	/// Camera for view / crosshair: first <see cref="CameraComponent"/> on this pawn or its descendants,
	/// else <see cref="CameraComponent"/> nested on <see cref="PlayerController"/> (same root as this component),
	/// else <see cref="Scene.Camera"/>.
	/// </summary>
	CameraComponent ResolveIntentCamera()
	{
		if ( TryFindFirstCameraInHierarchy( GameObject, out var desc ) && desc.IsValid() )
			return desc;

		var pc = GameObject.Components.Get<PlayerController>();
		if ( pc is not null )
		{
			var embedded = pc.Components.Get<CameraComponent>();
			if ( embedded.IsValid() )
				return embedded;
		}

		var sceneCam = Scene?.Camera;
		if ( sceneCam.IsValid() )
			return sceneCam;

		return default;
	}

	static bool TryFindFirstCameraInHierarchy( GameObject go, out CameraComponent cam )
	{
		cam = default;
		if ( !go.IsValid() )
			return false;

		var self = go.Components.Get<CameraComponent>();
		if ( self.IsValid() )
		{
			cam = self;
			return true;
		}

		foreach ( var ch in go.Children )
		{
			if ( TryFindFirstCameraInHierarchy( ch, out cam ) )
				return true;
		}

		return false;
	}

	protected virtual Vector3 GetViewDirectionForIntent()
	{
		var cam = ResolveIntentCamera();
		if ( cam.IsValid() )
			return cam.WorldRotation.Forward;

		return WorldRotation.Forward;
	}

	protected virtual Vector3 GetCameraPositionForIntent()
	{
		var cam = ResolveIntentCamera();
		if ( cam.IsValid() )
			return cam.WorldPosition;

		return WorldPosition;
	}

	Vector2 CameraRightWorldXz()
	{
		var yawRot = GetCameraYawRotation();
		var worldRight = yawRot * Vector3.Right;
		var rxz = new Vector2( worldRight.x, worldRight.z );
		if ( rxz.LengthSquared < 1e-10f )
			return new Vector2( 1f, 0f );
		return rxz.Normal;
	}

	void CardinalVectors( byte c, Vector3 viewForwardForUpSwing, out Vector2 xz, out float v )
	{
		switch ( c )
		{
			case SwingDirs.Up:
				SwingForwardWorldXzFromHorizontalView( viewForwardForUpSwing, out xz );
				v = 1f;
				return;
			case SwingDirs.Down:
				SwingForwardWorldXzFromHorizontalView( viewForwardForUpSwing, out xz );
				v = -1f;
				return;
			case SwingDirs.Left:
				xz = -CameraRightWorldXz();
				v = 0f;
				return;
			case SwingDirs.Right:
				xz = CameraRightWorldXz();
				v = 0f;
				return;
			default:
				SwingForwardWorldXzFromHorizontalView( viewForwardForUpSwing, out xz );
				v = 1f;
				return;
		}
	}

	/// <summary>Player-local combat basis for teardrop / swing evidence (flatten camera look to XZ).</summary>
	void SwingForwardWorldXzFromHorizontalView( Vector3 viewDir, out Vector2 xz )
	{
		var u = viewDir.LengthSquared > 1e-12f ? viewDir.Normal : Vector3.Forward;
		var fx = u.x;
		var fz = u.z;
		var len2 = fx * fx + fz * fz;
		if ( len2 < 1e-10f )
		{
			xz = new Vector2( 0f, 1f );
			return;
		}
		var il = 1f / MathF.Sqrt( len2 );
		xz = new Vector2( fx * il, fz * il );
	}

	/// <summary>
	/// Decayed pixel-evidence vector → L / R / U. Requires a **clear** dominant axis (<see cref="SwingLiveAxisWinRatio"/>) and
	/// |evidence| above a small floor; otherwise returns <paramref name="current"/> to limit flicker.
	/// Input is the time-decayed accumulator (screen pixels, y positive = mouse moved down), not a single frame's delta.
	/// </summary>
	byte ClassifyLiveSwingFrame( Vector2 mouseDelta, byte current, bool allowDown = false )
	{
		// Attacks read down as the jab; a guard has no down side, so there down still falls to the nearer side.
		var down = allowDown ? SwingDirs.Down : (mouseDelta.x > 0f ? SwingDirs.Right : SwingDirs.Left);
		var min = Math.Max( SwingLiveMicroMotionPixels, SwingAxisDeadzone * 0.25f );
		var yUp = SwingVerticalEvidence( mouseDelta );
		var dx = mouseDelta.x;
		var ax = MathF.Abs( dx );
		var ay = MathF.Abs( yUp );

		if ( ax < min && ay < min )
			return current;

		// From L/R, prefer Up on moderate upward diagonals (left → top-right reads as upper, not a side swing).
		var fromSide = current == SwingDirs.Left || current == SwingDirs.Right;
		if ( fromSide && yUp > min )
		{
			var upEase = Math.Clamp( SwingUpFromSideWinRatio, 1f, SwingLiveAxisWinRatio );
			if ( ay >= ax * upEase )
				return SwingDirs.Up;
		}

		var winRatio = Math.Max( 1.06f, SwingLiveAxisWinRatio );
		var hDominant = ay <= 1e-6f || ax >= ay * winRatio;
		var vDominant = ax <= 1e-6f || ay >= ax * winRatio;
		if ( !hDominant && !vDominant )
			return current;

		var preferHorizontalFirst = hDominant && ( !vDominant || ax >= ay );

		if ( preferHorizontalFirst )
		{
			if ( dx > min )
				return SwingDirs.Right;
			if ( dx < -min )
				return SwingDirs.Left;
			if ( yUp > min )
				return SwingDirs.Up;
			if ( yUp < -min )
				return down;
			return current;
		}

		if ( yUp > min )
			return SwingDirs.Up;
		if ( yUp < -min )
		{
			if ( allowDown )
				return SwingDirs.Down;
			if ( dx > min )
				return SwingDirs.Right;
			if ( dx < -min )
				return SwingDirs.Left;
			return down;
		}

		if ( dx > min )
			return SwingDirs.Right;
		if ( dx < -min )
			return SwingDirs.Left;
		return current;
	}

	/// <summary>Upward evidence (pixels); negative is down (the jab).</summary>
	float SwingVerticalEvidence( Vector2 mouseDelta ) =>
		SwingInvertLookYForUp ? -mouseDelta.y : mouseDelta.y;

	/// <summary>Signed pixels of the decayed evidence that support choosing <paramref name="cardinal"/> (same basis as <see cref="ClassifyLiveSwingFrame"/>).</summary>
	float ContributionTowardCardinal( byte cardinal, Vector2 mouseDelta )
	{
		var yUp = SwingVerticalEvidence( mouseDelta );
		var dx = mouseDelta.x;
		if ( cardinal == SwingDirs.Left )
			return MathF.Max( 0f, -dx );
		if ( cardinal == SwingDirs.Right )
			return MathF.Max( 0f, dx );
		if ( cardinal == SwingDirs.Down )
			return MathF.Max( 0f, -yUp );
		return MathF.Max( 0f, yUp );
	}

	/// <summary>
	/// Commit a cardinal flip only when (1) the decayed pixel evidence picks a new dominant axis (ratio rule),
	/// (2) the dominant axis has at least <see cref="SwingFlipCommitPixels"/> of |evidence| toward that cardinal,
	/// and (3) at least <see cref="SwingMinFlipHoldMs"/> have passed since the previous flip (1.5× for L↔R).
	/// All three are framerate-independent — no per-frame mouse delta thresholds remain.
	/// </summary>
	void ApplyLiveSwingFromEvidence( Vector2 evidence, ref byte currentDir, ref double lastFlipRealSeconds,
		bool invertAttackLateral = false )
	{
		var classifyCurrent = invertAttackLateral ? SwingDirs.MirrorLateral( currentDir ) : currentDir;
		var desire = ClassifyLiveSwingFrame( evidence, classifyCurrent, allowDown: invertAttackLateral );
		if ( invertAttackLateral )
			desire = SwingDirs.MirrorLateral( desire );
		if ( desire == currentDir )
			return;

		var holdSeconds = MathF.Max( 0f, SwingMinFlipHoldMs ) * 0.001f;
		if ( IsOpposingLateralSwing( currentDir, desire ) )
			holdSeconds *= 1.5f;

		var now = RealTime.GlobalNow;
		if ( now - lastFlipRealSeconds < holdSeconds )
			return;

		var evidenceCardinal = invertAttackLateral ? SwingDirs.MirrorLateral( desire ) : desire;
		var contrib = ContributionTowardCardinal( evidenceCardinal, evidence );
		if ( contrib < SwingFlipCommitThreshold( currentDir, desire ) )
			return;

		currentDir = desire;
		lastFlipRealSeconds = now;
	}

	byte ClassifyAttackLiveSwingFrame( Vector2 evidence, byte currentAttackDir ) =>
		SwingDirs.MirrorLateral( ClassifyLiveSwingFrame( evidence, SwingDirs.MirrorLateral( currentAttackDir ), allowDown: true ) );

	/// <summary>
	/// Screen-space teardrop direction for the unified crosshair (<see cref="PlayerCrosshair"/>);
	/// +x right, +y down. The live attack cursor; the block morph while the guard button is held.
	/// </summary>
	/// <summary>Crosshair attack arrow: hidden in static combo mode (the mouse picks nothing) unless the guard is held.</summary>
	public bool ShowsAttackTeardrop => !StaticComboActive || Input.Down( BlockAction );

	public Vector2 GetTeardropScreenDirection()
	{
		byte swingPreview;
		var mirrorTeardropForAttack = false;
		if ( Input.Down( BlockAction ) )
		{
			swingPreview = _blockLiveSwingDir;
		}
		else
		{
			swingPreview = _primaryLiveSwingDir;
			mirrorTeardropForAttack = true;
		}

		if ( mirrorTeardropForAttack )
			swingPreview = SwingDirs.MirrorLateral( swingPreview );

		return SwingCardinalToScreenTeardropDir( swingPreview );
	}

	/// <summary>
	/// Screen teardrop offset (+x right, +y down). Block: stored cardinal = screen side. Attack HUD mirrors stored combat dir.
	/// </summary>
	static Vector2 SwingCardinalToScreenTeardropDir( byte cardinal )
	{
		if ( cardinal == SwingDirs.Left )
			return new Vector2( -1f, 0f );
		if ( cardinal == SwingDirs.Right )
			return new Vector2( 1f, 0f );
		if ( cardinal == SwingDirs.Down )
			return new Vector2( 0f, 1f );
		return new Vector2( 0f, -1f );
	}

	Rotation GetCameraYawRotation()
	{
		// Non-local actors (training dummies, remote pawns on host) must not borrow Scene.Camera yaw.
		var cam = IsLocalCombatDriver() ? ResolveIntentCamera() : default;
		var yaw = cam.IsValid() ? cam.WorldRotation.Angles().yaw : WorldRotation.Angles().yaw;
		return new Angles( 0f, yaw, 0f ).ToRotation();
	}

	Rotation GetCameraAimRotation()
	{
		var cam = IsLocalCombatDriver() ? ResolveIntentCamera() : default;
		if ( cam.IsValid() )
		{
			var a = cam.WorldRotation.Angles();
			return new Angles( a.pitch, a.yaw, 0f ).ToRotation();
		}

		var body = WorldRotation.Angles();
		return new Angles( body.pitch, body.yaw, 0f ).ToRotation();
	}

	float GetCameraPitchDegrees()
	{
		var cam = IsLocalCombatDriver() ? ResolveIntentCamera() : default;
		if ( cam.IsValid() )
			return cam.WorldRotation.Angles().pitch;

		return WorldRotation.Angles().pitch;
	}

	/// <summary>Swing pitch clamped to the up/down caps — aims the arc so the blade crosses exactly where the crosshair sits.</summary>
	public float GetMeleeSwingPitchDegrees( byte attackType )
	{
		if ( _meleeIntentPitchCaptured )
			return _meleeIntentStartPitchDegrees;

		return ClampMeleeSwingPitchDegrees( GetMeleeCursorAlignedPitchDegrees( attackType ) );
	}

	float _meleeCursorPitchCacheDegrees;
	float _meleeCursorPitchCacheTime = -1f;
	byte _meleeCursorPitchCacheAttackType;

	/// <summary>
	/// Pitch that puts the mid-swing blade tip ON the camera ray: intersects the crosshair ray with the sphere of
	/// swing reach around the pitch pivot (cached per frame), so the tip projects onto the crosshair pixel in first
	/// and third person alike. Ray outside reach = closest approach; no camera = raw camera pitch.
	/// </summary>
	public float GetMeleeCursorAlignedPitchDegrees( byte attackType )
	{
		var cam = IsLocalCombatDriver() ? ResolveIntentCamera() : default;
		if ( !cam.IsValid() )
			return GetCameraPitchDegrees();

		if ( _meleeCursorPitchCacheTime == Time.Now && _meleeCursorPitchCacheAttackType == attackType )
			return _meleeCursorPitchCacheDegrees;

		var pivotWorld = WorldPosition + Vector3.Up * MeleeAttackPath.GetPitchPivotHeightLocal( this, attackType );
		var radius = MeleeAttackPath.GetMidSwingTipRadius( this, attackType );

		var camPos = cam.WorldPosition;
		var dir = cam.WorldRotation.Forward;
		var toCam = camPos - pivotWorld;
		var b = Vector3.Dot( toCam, dir );
		var disc = b * b - (toCam.LengthSquared - radius * radius);

		// Far ray-sphere intersection when the crosshair ray crosses the reach sphere; closest approach otherwise.
		var along = disc >= 0f ? -b + MathF.Sqrt( disc ) : MathF.Max( 0f, -b );
		var toAim = camPos + dir * along - pivotWorld;

		var pitch = along > 1e-3f && toAim.LengthSquared > 1e-6f
			? Rotation.LookAt( toAim.Normal ).Angles().pitch
			: GetCameraPitchDegrees();

		_meleeCursorPitchCacheTime = Time.Now;
		_meleeCursorPitchCacheAttackType = attackType;
		_meleeCursorPitchCacheDegrees = pitch;
		return pitch;
	}

	float _meleeSwingForwardOffsetUnitsCache = float.NaN;
	float _pawnUnitsPerMeterCache = float.NaN;

	/// <summary>Pawn-space units per designer meter (BodyHeight/1.8 — Citizen 72 ≈ 40 u/m). Cached once per pawn.</summary>
	public float GetPawnUnitsPerMeter()
	{
		if ( float.IsNaN( _pawnUnitsPerMeterCache ) )
		{
			var controller = GameObject.Components.Get<PlayerController>();
			var bodyHeight = controller is not null && controller.IsValid()
				? Math.Max( 24f, controller.BodyHeight )
				: 72f;
			_pawnUnitsPerMeterCache = bodyHeight / 1.8f;
		}

		return _pawnUnitsPerMeterCache;
	}

	/// <summary>Engine-unit forward push for melee arcs — <see cref="MeleeSwingForwardOffsetMeters"/> converted once per pawn (BodyHeight/1.8).</summary>
	public float GetMeleeSwingForwardOffsetUnits()
	{
		if ( float.IsNaN( _meleeSwingForwardOffsetUnitsCache ) )
			_meleeSwingForwardOffsetUnitsCache = Math.Max( 0f, _meleeSwingForwardOffsetMeters ) * GetPawnUnitsPerMeter();

		return _meleeSwingForwardOffsetUnitsCache;
	}

	float ClampMeleeSwingPitchDegrees( float rawPitchDegrees )
	{
		var min = Math.Min( MeleeSwingMinPitchDegrees, MeleeSwingMaxPitchDegrees );
		var max = Math.Max( MeleeSwingMinPitchDegrees, MeleeSwingMaxPitchDegrees );
		return Math.Clamp( rawPitchDegrees, min, max );
	}

	/// <summary>
	/// Pin the swing basis (yaw + pitch) from the submitted intent — every pawn, local owner included — and remember
	/// the look yaw at that moment. Turning during the swing (free in the windup, slowed during the strike, never limited —
	/// Mark 2026-11-05) carries the arc, the body facing and the damage with it: one basis for all
	/// of them, so the painted fan still matches the damage. A right-to-left swing turned 90° left reaches what was
	/// behind the shoulder at the click.
	/// </summary>
	internal void PushMeleeAttackBasisFromIntent( in AttackReleaseIntent intent, byte attackType )
	{
		_meleeIntentBasisYawOverride = ResolveIntentCombatBasisYaw( intent, attackType );
		_meleeIntentStartPitchDegrees = ClampMeleeSwingPitchDegrees( ResolveIntentViewPitchDegrees( intent ) );
		_meleeIntentPitchCaptured = true;
		// Disabled too: a proxy / scripted pawn still carries the synced EyeAngles.
		var controller = Components.Get<PlayerController>( FindMode.EverythingInSelf );
		_meleeSwingTurnTracked = controller is not null;
		_meleeSwingLastEyeYaw = controller is not null ? controller.EyeAngles.yaw : 0f;
		_meleeSwingTurnAccum = 0f;
		_meleeSwingTurnUpdatedAt = Time.Now;
	}

	internal void ClearMeleeAttackBasisFromIntent()
	{
		_meleeIntentBasisYawOverride = null;
		_meleeIntentPitchCaptured = false;
		_meleeSwingTurnTracked = false;
	}

	/// <summary>
	/// How far this pawn has turned since its swing committed — no limit (Mark 2026-11-05: "let players spin as much as
	/// they want, it should just be slow"; the slow is the look lock during the strike). Summed from the per-frame look
	/// change once per frame, so turning past 180° one way never wraps round to the other side.
	/// </summary>
	float GetSwingTurnDegrees()
	{
		if ( !_meleeSwingTurnTracked || Components.Get<PlayerController>( FindMode.EverythingInSelf ) is not { } controller )
			return 0f;

		if ( _meleeSwingTurnUpdatedAt != Time.Now )
		{
			_meleeSwingTurnUpdatedAt = Time.Now;
			var eye = controller.EyeAngles.yaw;
			_meleeSwingTurnAccum += NormalizeDegreesDelta( eye - _meleeSwingLastEyeYaw );
			_meleeSwingLastEyeYaw = eye;
		}

		return _meleeSwingTurnAccum;
	}

	/// <summary>The committed swing's yaw including the turn since the click; null when no swing is pinned.</summary>
	float? LiveSwingBasisYaw => _meleeIntentBasisYawOverride is { } pinned ? pinned + GetSwingTurnDegrees() : null;

	static float ResolveIntentCombatBasisYaw( in AttackReleaseIntent intent, byte attackType )
	{
		_ = attackType;
		if ( !float.IsNaN( intent.CombatBasisYawDegrees ) )
			return intent.CombatBasisYawDegrees;

		var forward = intent.ViewForwardOnRelease;
		if ( forward.LengthSquared < 1e-8f )
			forward = intent.ViewForwardOnPress;
		if ( forward.LengthSquared < 1e-8f )
			return 0f;

		var flat = forward.WithY( 0f );
		if ( flat.LengthSquared < 1e-8f )
			return Rotation.LookAt( forward.Normal ).Angles().yaw;

		return Rotation.LookAt( flat.Normal ).Angles().yaw;
	}

	static float ResolveIntentViewPitchDegrees( in AttackReleaseIntent intent )
	{
		if ( !float.IsNaN( intent.CombatBasisPitchDegrees ) )
			return intent.CombatBasisPitchDegrees;

		var forward = intent.ViewForwardOnRelease;
		if ( forward.LengthSquared < 1e-8f )
			forward = intent.ViewForwardOnPress;
		if ( forward.LengthSquared < 1e-8f )
			return 0f;

		return Rotation.LookAt( forward.Normal ).Angles().pitch;
	}

	/// <summary>Live aim for melee paths — camera yaw plus influenced pitch for every swing type.</summary>
	public Rotation GetMeleeCombatBasisRotation( byte attackType )
	{
		var yaw = LiveSwingBasisYaw ?? GetCameraAimRotation().Angles().yaw;
		return GetMeleeCombatBasisRotationForYaw( attackType, yaw );
	}

	/// <summary>Live aim for melee paths when attack type is unknown — yaw-only horizontal basis.</summary>
	public Rotation GetMeleeCombatBasisRotation() => GetCameraYawRotation();

	internal static byte NormalizeCardinalBlockDirection( byte dir ) =>
		dir is (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up) ? dir : SwingDirs.Up;

	/// <summary>Horizontal yaw of the live combat basis for this attack type.</summary>
	public float GetMeleeCombatBasisYaw( byte attackType ) =>
		GetMeleeCombatBasisRotation( attackType ).Angles().yaw;

	/// <summary>Combat basis with an explicit yaw; pitch comes from the shared cursor-aligned swing pitch.</summary>
	public Rotation GetMeleeCombatBasisRotationForYaw( byte attackType, float yawDegrees ) =>
		new Angles( GetMeleeSwingPitchDegrees( attackType ), yawDegrees, 0f ).ToRotation();

	CombatChannelRules GetBlockRules() => new CombatChannelRules { CooldownAfterValidReleaseSeconds = BlockCooldownAfterRelease };

	/// <summary>Stamina spent per swing (class <c>staminaCost</c>).</summary>
	public float GetPrimaryAttackStaminaCost() => GetMeleeWeaponTimings().StaminaCost;

	/// <summary>What turning a swing heavy costs on top of the light cost already paid on the click.</summary>
	public float GetHeavyExtraStaminaCost()
	{
		var t = GetMeleeWeaponTimings();
		return Math.Max( 0f, t.StaminaHeavyCost - t.StaminaCost );
	}

	double _shoveFacingUntilSandbox;
	float _shoveFacingYaw;

	/// <summary>Shove is instantaneous — hold the camera-facing visual through its combat lock window.</summary>
	internal void BeginShoveFacingLock()
	{
		_shoveFacingYaw = GetCameraYawRotation().Angles().yaw;
		_shoveFacingUntilSandbox = Time.NowDouble + Math.Max( 0.1f, RecoveryShoveCombatLockSeconds );
	}

	/// <summary>True while a committed swing is in flight (basis pinned) — movement freezes root rotation for its duration.</summary>
	internal bool IsSwingFacingPinned => _meleeIntentBasisYawOverride is not null;

	/// <summary>
	/// Yaw the pawn's VISUAL should face right now, when a combat action owns the facing. Pure data —
	/// no transform writes here. <see cref="PlayerAnimation"/> applies it to the renderer child only
	/// (never the physics root, which the controller + rigidbody interpolation fight over — that
	/// fight was the visible facing spazz). Sources in priority order: in-flight swing basis, the
	/// broadcast windup telegraph (any peer), the authoritative block guard (any peer), local
	/// attack before the host confirms it, then the shove window. The bow's archer stance is not here — it is a
	/// pose with its own sideways yaw, owned by PlayerAnimation (PlayerAnimation.Bow.cs).
	/// </summary>
	internal bool TryGetCombatFacingYaw( out float yawDegrees )
	{
		if ( LiveSwingBasisYaw is { } pinnedSwingYaw )
		{
			yawDegrees = pinnedSwingYaw;
			return true;
		}

		if ( _windupTelegraphActive )
		{
			yawDegrees = _windupTelegraphBasisYaw;
			return true;
		}

		if ( IsAuthoritativeMeleeBlocking || LocalBlockInputActive() )
		{
			yawDegrees = GetBlockCombatBasisYaw();
			return true;
		}

		// Owner on a pure client between its click and the host confirming the swing (the basis is not pinned yet).
		// (The bow's sideways archer stance is PlayerAnimation's own facing — PlayerAnimation.Bow.cs.)
		if ( IsLocalCombatDriver() && _ownerExpectsHostMeleeBusy )
		{
			yawDegrees = GetCameraYawRotation().Angles().yaw;
			return true;
		}

		if ( Time.NowDouble < _shoveFacingUntilSandbox )
		{
			yawDegrees = _shoveFacingYaw;
			return true;
		}

		yawDegrees = 0f;
		return false;
	}

	/// <summary>Owner → host intent for a swing committed on the click (press = release: there is no hold).</summary>
	AttackReleaseIntent BuildPrimaryAttackIntent( byte swingDir, Vector3 view, Vector3 camera, byte comboStep = 0, bool special = false )
	{
		_attackIntentSequence++;
		CardinalVectors( swingDir, view, out var swingXz, out var swingV );
		var attackType = special ? MeleeAttackTypes.Stab : ResolveAttackTypeFromCursorDir( swingDir );
		var now = RealTime.GlobalNow;

		return new AttackReleaseIntent
		{
			PressedGlobalSeconds = now,
			ReleasedGlobalSeconds = now,
			ClientCameraPressX = camera.x,
			ClientCameraPressY = camera.y,
			ClientCameraPressZ = camera.z,
			ClientCameraReleaseX = camera.x,
			ClientCameraReleaseY = camera.y,
			ClientCameraReleaseZ = camera.z,
			ViewForwardOnPress = view,
			ViewForwardOnRelease = view,
			ClientPlayerWorldPosition = WorldPosition,
			ClientPlayerWorldRotation = WorldRotation,
			IntentSequence = _attackIntentSequence,
			SwingFromX = swingXz.x,
			SwingFromY = swingXz.y,
			SwingVerticalHint = swingV,
			SwingDir = swingDir,
			AttackType = attackType,
			CombatBasisYawDegrees = GetMeleeCombatBasisYaw( attackType ),
			CombatBasisPitchDegrees = GetMeleeCursorAlignedPitchDegrees( attackType ),
			ComboStep = comboStep,
			IsSpecial = special
		};
	}

	void OnOwnerValidBlockRelease( CombatButtonIntentSnapshot snapshot )
	{
		var c = _blockLiveSwingDir;
		var vUp = snapshot.ViewDirectionOnRelease ?? snapshot.ViewDirectionOnPress ?? GetViewDirectionForIntent();
		CardinalVectors( c, vUp, out var bxz, out var bv );
		_blockReleaseSwingXz = bxz;
		_blockReleaseSwingVerticalHint = bv;
		_blockReleaseSwingDir = c;
	}

	void DispatchPrimaryAttackReleaseToAuthority( AttackReleaseIntent intent )
	{
		// No busy gate here: this dispatch belongs to the swing already animating on the owner.
		// Gating happens once, on the click (see TryFirePrimaryAttack / IsMeleeAttackChainBusy).

		// Offline: run validation on this machine only.
		// Online: always go through [Rpc.Host] once — avoids host "local dispatch" + any duplicate server path both debiting stamina.
		if ( GameObject.Network is not { Active: true } )
		{
			var auth = ResolveCombatAuthority();
			if ( auth is null )
			{
				var missing = new AttackReleaseResult
				{
					Accepted = false,
					Hit = false,
					DamageDealt = 0f,
					TargetGameObjectId = Guid.Empty,
					DebugCode = AttackReleaseDebugCode.RejectNoCombatAuthority,
					DebugDetail = "No CombatAuthority in scene (assign PlayerCombat.HostCombatAuthority or enable exactly one CombatAuthority)."
				};
				LastServerAttackResult = missing;
				_combatNetDiag = "offline: no CombatAuthority";
				LogCombatDiag( "SERVER (local dispatch)", FormatAttackResultLog( missing ) );
				return;
			}

			var result = auth.ValidateAndApplyPrimaryMelee( GameObject, intent );
			LastServerAttackResult = result;
			_combatNetDiag = $"offline: acc={result.Accepted} hit={result.Hit} dmg={result.DamageDealt:0.#} code={result.DebugCode}";
			LogCombatDiag( "SERVER (local dispatch)", FormatAttackResultLog( result ) );
			if ( result.Accepted )
				SetOwnerExpectsHostMeleeBusy( true );
			return;
		}

		SetOwnerExpectsHostMeleeBusy( true );
		_combatNetDiag = Networking.IsHost ? "host→Rpc.Host (single server pass)" : "RPC->host sent (await result)";
		LogCombatDiag( "CLIENT (dispatch)", "RpcSubmitPrimaryAttackRelease -> host (see editor Output)" );
		_pendingSwingVisualIntent = intent;
		_hasPendingSwingVisualIntent = true;
		RpcSubmitPrimaryAttackRelease( intent );
	}

	void SetOwnerExpectsHostMeleeBusy( bool expects )
	{
		_ownerExpectsHostMeleeBusy = expects;
		_ownerExpectsHostMeleeBusySince = expects ? Time.NowDouble : 0;
	}

	/// <summary>Clear client spam-gate after reject / sweep complete / host cancel / timeout.</summary>
	public void ClearOwnerMeleeBusyExpect( string reason = null )
	{
		var hadBusy = _ownerExpectsHostMeleeBusy;
		SetOwnerExpectsHostMeleeBusy( false );

		_hasPendingSwingVisualIntent = false;

		if ( hadBusy && !string.IsNullOrWhiteSpace( reason ) )
			LogCombatDiag( "CLIENT / OWNER", $"Cleared owner melee busy expect ({reason})" );
	}

	/// <summary>
	/// One gate for "a swing owns this pawn": the host swing (windup + damage swoop), a block / parry
	/// lockout and the hit reaction. While true a click queues exactly one follow-up instead of firing.
	/// The strike clip's settle frames are NOT busy (Mark 2026-11-04: the next windup starts the moment the
	/// damage ends) — the graph's <c>b_sword_interrupt</c> cuts them short when the next click cocks.
	/// </summary>
	bool IsMeleeAttackChainBusy()
	{
		if ( IsCombatActionLocked )
			return true;

		if ( ServerHasActiveMeleeAttackAction )
			return true;

		// Pure clients need this until RpcOwner sweep-complete. Listen-server host uses ServerHasActive above —
		// keeping ownerExpects after sweep end locked out the next press for ~a second / forever.
		if ( GameObject.Network is { Active: true }
		     && !Networking.IsHost
		     && _ownerExpectsHostMeleeBusy )
		{
			if ( _ownerExpectsHostMeleeBusySince > 0
			     && Time.NowDouble - _ownerExpectsHostMeleeBusySince > OwnerExpectsHostMeleeBusyTimeoutSeconds )
			{
				LogCombatDiag( "CLIENT / OWNER",
					$"ownerExpectsHostMeleeBusy timed out after {OwnerExpectsHostMeleeBusyTimeoutSeconds:0.##}s — unlocking Attack1" );
				ClearOwnerMeleeBusyExpect( "timeout" );
				return false;
			}

			return true;
		}

		return false;
	}

	[Rpc.Host]
	public void RpcSubmitPrimaryAttackRelease( AttackReleaseIntent intent )
	{
		var sx = new Vector2( intent.SwingFromX, intent.SwingFromY );
		var sv = CombatAuthority.ServerClampSwingVertical( intent.SwingVerticalHint );
		LogCombatDiag( "SERVER (Rpc.Host)",
			$"RpcSubmitPrimaryAttackRelease seq={intent.IntentSequence} camP=({intent.ClientCameraPressX:F1},{intent.ClientCameraPressY:F1},{intent.ClientCameraPressZ:F1}) camR=({intent.ClientCameraReleaseX:F1},{intent.ClientCameraReleaseY:F1},{intent.ClientCameraReleaseZ:F1}) {CombatAuthority.FormatSwingLog( sx, sv, intent.SwingDir )}" );
		if ( !Networking.IsHost )
			return;

		if ( !GameObject.IsValid() )
			return;

		// Strict: only this pawn's Network.Owner may drive this RPC. Never treat same Steam on two connections as the same client here.
		if ( Rpc.Caller is { } rpcCaller
		     && GameObject.Network is { Active: true, Owner: { } pawnOwner }
		     && rpcCaller.Id != pawnOwner.Id )
		{
			Log.Warning( $"[PlayerCombat] RpcSubmitPrimaryAttackRelease ignored on {GameObject.Name}: caller {ConnectionIdentity.Format( rpcCaller )} ≠ owner {ConnectionIdentity.Format( pawnOwner )} (by Connection.Id)." );
			return;
		}

		var auth = ResolveCombatAuthority();
		if ( auth is null )
		{
			var missing = new AttackReleaseResult
			{
				Accepted = false,
				Hit = false,
				DamageDealt = 0f,
				TargetGameObjectId = Guid.Empty,
				DebugCode = AttackReleaseDebugCode.RejectNoCombatAuthority,
				DebugDetail = "No CombatAuthority on host during RpcSubmitPrimaryAttackRelease."
			};
			LogCombatDiag( "SERVER (Rpc.Host)", FormatAttackResultLog( missing ) );
			LastServerAttackResult = missing;
			RpcReceiveAttackReleaseResult( missing );
			return;
		}

		var result = auth.ValidateAndApplyPrimaryMelee( GameObject, intent );
		LogCombatDiag( "SERVER (Rpc.Host)", FormatAttackResultLog( result ) );
		LastServerAttackResult = result;
		RpcReceiveAttackReleaseResult( result );
	}

	[Rpc.Owner]
	public void RpcReceiveAttackReleaseResult( AttackReleaseResult result )
	{
		LastServerAttackResult = result;
		_combatNetDiag = $"rpc owner: acc={result.Accepted} hit={result.Hit} dmg={result.DamageDealt:0.#}";
		LogCombatDiag( "CLIENT (Rpc.Owner)", FormatAttackResultLog( result ) );

		// Rejected (including RejectMeleeBusy): the swing is simply lost — the player must press again.
		if ( !result.Accepted )
			ClearOwnerMeleeBusyExpect( $"reject {result.DebugCode}" );

		// Backup: if HostOnly broadcast was missed, still show the local slash path on the attacking client.
		if ( result.Accepted
		     && _hasPendingSwingVisualIntent
		     && ClientMeleeSwingTraceDebug )
		{
			StartClientMeleeSwingTracePlayback( _pendingSwingVisualIntent );
		}

		_hasPendingSwingVisualIntent = false;
	}

	[Rpc.Owner]
	public void RpcOwnerMeleeSwingComplete( ushort intentSequence, bool anyHit, float totalDamageDealt, Guid firstHitTargetId ) =>
		ApplyAuthoritativeMeleeSweepSummary( intentSequence, anyHit, totalDamageDealt, firstHitTargetId );

	/// <summary>Host cancelled the swing without a normal sweep complete — unlock the owning client.</summary>
	[Rpc.Owner]
	public void RpcOwnerMeleeBusyCleared( string reason ) =>
		ClearOwnerMeleeBusyExpect( string.IsNullOrWhiteSpace( reason ) ? "host cancel" : reason );

	/// <summary>Applies host sweep outcome locally (offline) or from <see cref="RpcOwnerMeleeSwingComplete"/>.</summary>
	public void ApplyAuthoritativeMeleeSweepSummary( ushort intentSequence, bool anyHit, float totalDamageDealt, Guid firstHitTargetId )
	{
		LastMeleeSweepSummary = new MeleeSweepOutcomeSummary
		{
			IntentSequence = intentSequence,
			AnyHit = anyHit,
			TotalDamageDealt = totalDamageDealt,
			FirstHitTargetId = firstHitTargetId
		};

		LastServerAttackResult = new AttackReleaseResult
		{
			Accepted = true,
			Hit = anyHit,
			DamageDealt = totalDamageDealt,
			TargetGameObjectId = firstHitTargetId,
			DebugCode = anyHit ? AttackReleaseDebugCode.OkHit : AttackReleaseDebugCode.OkMiss,
			DebugDetail = $"melee sweep complete seq={intentSequence} totalDealt={totalDamageDealt:0.#}"
		};

		_combatNetDiag = $"rpc owner sweep: seq={intentSequence} anyHit={anyHit} total={totalDamageDealt:0.#}";
		LogCombatDiag( "CLIENT (Rpc.Owner)", FormatAttackResultLog( LastServerAttackResult ) );
		ClearOwnerMeleeBusyExpect( "sweep complete" );
	}

	CombatAuthority ResolveCombatAuthority()
	{
		// Never touch .GameObject on a possibly-stale serialized handle — use IsValid() first (avoids interop NRE after bad prefab/scene saves).
		if ( HostCombatAuthority is { } hostAuth && hostAuth.IsValid() )
			return hostAuth;

		if ( CombatAuthority.Instance is { } singleton && singleton.IsValid() )
			return singleton;

		var scene = Scene;
		if ( scene.IsValid() )
		{
			foreach ( var c in scene.GetAllComponents<CombatAuthority>() )
			{
				if ( c.IsValid() && c.Enabled )
					return c;
			}
		}

		return null;
	}

	/// <summary>One-time warning when this pawn would drive combat but no authority exists (misconfigured scene / prefab).</summary>
	void MaybeWarnCombatAuthorityMisconfigured()
	{
		if ( _combatAuthorityDiagComplete || !GameObject.IsValid() || GameObject.IsProxy || !Active )
			return;

		var cares = GameObject.Network is not { Active: true } || Networking.IsHost || GameObject.Network is { Active: true, IsOwner: true };
		if ( !cares )
			return;

		_combatAuthorityDiagComplete = true;

		if ( ResolveCombatAuthority() is not null )
			return;

		Log.Warning(
			$"[PlayerCombat] {GameObject.Name}: no CombatAuthority resolved (HostCombatAuthority unset/broken, Instance null, none in scene). " +
			"Add an enabled Survival.CombatAuthority (e.g. on NetworkManager) or assign HostCombatAuthority on the player prefab to a scene authority when editing a placed instance." );
	}

	void LogCombatDiag( string where, string body )
	{
		if ( !LogCombatNetworkingToConsole )
			return;

		var flat = body.Replace( "\r\n", " " ).Replace( '\n', ' ' ).Replace( '\r', ' ' );
		while ( flat.Contains( "  " ) )
			flat = flat.Replace( "  ", " " );
		Log.Info( $"[PlayerCombat] {where}: {flat}" );
	}

	static bool IsOpposingLateralSwing( byte a, byte b ) =>
		( a == SwingDirs.Left && b == SwingDirs.Right ) || ( a == SwingDirs.Right && b == SwingDirs.Left );

	static bool IsLateralToUp( byte cur, byte desire ) =>
		( cur == SwingDirs.Left || cur == SwingDirs.Right ) && desire == SwingDirs.Up;

	float SwingFlipCommitThreshold( byte current, byte desire )
	{
		var t = SwingFlipCommitPixels;
		if ( IsOpposingLateralSwing( current, desire ) )
			t += Math.Max( 0f, SwingLateralFlipExtraPixels );
		if ( IsLateralToUp( current, desire ) )
			t *= Math.Clamp( SwingFlipCommitUpFromSideMul, 0.08f, 1f );
		return t;
	}

	static string FormatAttackResultLog( AttackReleaseResult r ) =>
		$"accepted={r.Accepted} hit={r.Hit} dmg={r.DamageDealt:0.#} code={r.DebugCode} | {r.DebugDetail ?? "—"}";

	void DrawCombatInputDebug()
	{
		var y = 64f;
		const float debugScreenX = 48f;
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ), "[ PlayerCombat ]", size: 12f );
		y += 16f;
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ), FormatChannel( "Attack2 (block)", BlockAction, _block ), size: 14f );
		y += 18f;
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ),
			$"atk last click={SwingDirs.Letter( _lockedPrimaryAttackSwingDir )} live={SwingDirs.Letter( _primaryLiveSwingDir )} queued={(_hasQueuedAttackPress ? SwingDirs.Letter( _queuedAttackSwingDir ) : "—")}",
			size: 12f );
		y += 16f;
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ),
			$"swing blk: {SwingDirs.Letter( _blockLiveSwingDir )}  held={Input.Down( BlockAction )}  accum=({_blockLookAccum.x:F2},{_blockLookAccum.y:F2})  mouseΔ=({Input.MouseDelta.x:F2},{Input.MouseDelta.y:F2})",
			size: 12f );
		y += 16f;
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ),
			$"last block rel: {CombatAuthority.FormatSwingLog( _blockReleaseSwingXz, _blockReleaseSwingVerticalHint, _blockReleaseSwingDir )}",
			size: 12f );
		y += 16f;
		// Facing diagnostics: if faceTarget == body while the model LOOKS rotated, the rotation is
		// baked in the animation clip, not the facing write.
		var faceTarget = TryGetCombatFacingYaw( out var facingYaw ) ? $"{facingYaw:0.#}" : "—";
		var bodyYaw = Components.Get<PlayerAnimation>()?.GetBodyVisualYawDegrees() is { } by ? $"{by:0.#}" : "—";
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ),
			$"facing: target={faceTarget} body={bodyYaw} root={WorldRotation.Angles().yaw:0.#} cam={GetCameraAimRotation().Angles().yaw:0.#}",
			size: 12f );
		y += 16f;
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ), "[ last server result ]", size: 12f );
		y += 16f;
		var tgt = LastServerAttackResult.TargetGameObjectId;
		var tgtShort = tgt == Guid.Empty ? "—" : (tgt.ToString().Length >= 8 ? tgt.ToString()[..8] : tgt.ToString());
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ),
			$"accepted={LastServerAttackResult.Accepted} hit={LastServerAttackResult.Hit} dmg={LastServerAttackResult.DamageDealt:0.#} code={LastServerAttackResult.DebugCode} tgt={tgtShort}",
			size: 13f );
		y += 16f;
		var detail = LastServerAttackResult.DebugDetail ?? "—";
		if ( detail.Length > 72 )
			detail = detail[..72] + "…";
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ), detail, size: 12f );
		y += 18f;
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ), "[ net / diag ]", size: 12f );
		y += 16f;
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ), _combatNetDiag, size: 13f );
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
		{
			y += 16f;
			DebugOverlay.ScreenText( new Vector2( debugScreenX, y ),
				"Tip: CLIENT — host Rpc.Host logs in EDITOR Output.",
				size: 12f );
		}
	}

	static string FormatChannel( string label, string action, CombatChannel ch )
	{
		var state = ch.Down ? "HELD" : "up";
		var cd = ch.CooldownRemainingSeconds > 0f
			? $"  cd={ch.CooldownRemainingSeconds:0.###}s"
			: "";
		var view = ch.Snapshot.ViewDirectionOnPress is { } v
			? $"  view=({v.x:F2},{v.y:F2},{v.z:F2})"
			: "";
		var camPress = ch.Snapshot.CameraPositionOnPress is { } cp
			? $"  camP=({cp.x:F0},{cp.y:F0},{cp.z:F0})"
			: "";
		var camRel = ch.Snapshot.CameraPositionOnRelease is { } cr
			? $"  camR=({cr.x:F0},{cr.y:F0},{cr.z:F0})"
			: "";
		return $"{label} [{action}] {state}  hold={ch.Snapshot.HoldDurationSeconds:0.###}s{cd}{view}{camPress}{camRel}";
	}

	sealed class CombatChannel
	{
		public bool Down { get; private set; }
		public CombatButtonIntentSnapshot Snapshot { get; private set; }

		double? _nextPressAllowedAtGlobal;

		double? _pressGlobal;
		double? _pressSandbox;
		DateTime? _pressUtc;
		Vector3? _viewDirOnPress;
		Vector3? _cameraPositionOnPress;

		Action<CombatButtonIntentSnapshot> _onValidRelease;

		public float CooldownRemainingSeconds =>
			_nextPressAllowedAtGlobal is { } until && RealTime.GlobalNow < until
				? (float)( until - RealTime.GlobalNow )
				: 0f;

		public void Step( string actionName, Func<bool> canStart, Func<bool> canContinue, Func<Vector3> getViewDirection, Func<Vector3> getCameraPosition, CombatChannelRules rules, Action<CombatButtonIntentSnapshot> onValidRelease = null, bool allowHeldStart = false )
		{
			_onValidRelease = onValidRelease;

			var wantsDown = Input.Down( actionName );
			var pressed = Input.Pressed( actionName );
			var released = Input.Released( actionName );

			// allowHeldStart: a press swallowed by cooldown/recovery is not lost — keep holding and
			// the channel begins the first frame the gates open (block/attack start as fast as the
			// timings allow instead of demanding a perfectly timed re-press).
			if ( !Down && (pressed || (allowHeldStart && wantsDown)) )
			{
				if ( _nextPressAllowedAtGlobal is { } notBefore && RealTime.GlobalNow < notBefore )
					return;

				if ( !canStart() )
					return;

				BeginPress( getViewDirection, getCameraPosition );
			}

			if ( Down )
			{
				if ( !wantsDown || released || !canContinue() )
					FinishPress( getCameraPosition, getViewDirection, rules );
				else
				{
					var heldSoFar = _pressGlobal is { } pg0 ? (float)( RealTime.GlobalNow - pg0 ) : 0f;
					Snapshot = new CombatButtonIntentSnapshot
					{
						IsHeld = true,
						HoldDurationSeconds = heldSoFar,
						PressedUtc = _pressUtc,
						PressedGlobalSeconds = _pressGlobal,
						PressedSandboxTimeNowDouble = _pressSandbox,
						ReleasedUtc = null,
						ReleasedGlobalSeconds = null,
						ReleasedSandboxTimeNowDouble = null,
						ViewDirectionOnPress = _viewDirOnPress,
						ViewDirectionOnRelease = null,
						CameraPositionOnPress = _cameraPositionOnPress,
						CameraPositionOnRelease = null
					};
				}
			}
			else if ( !pressed && !released )
			{
				if ( Snapshot.ReleasedGlobalSeconds is null && Snapshot.PressedGlobalSeconds is null )
					Snapshot = default;
			}
		}

		void BeginPress( Func<Vector3> getViewDirection, Func<Vector3> getCameraPosition )
		{
			Down = true;
			_pressGlobal = RealTime.GlobalNow;
			_pressSandbox = Time.NowDouble;
			_pressUtc = DateTime.UtcNow;
			_viewDirOnPress = getViewDirection();
			_cameraPositionOnPress = getCameraPosition();
			Snapshot = new CombatButtonIntentSnapshot
			{
				IsHeld = true,
				HoldDurationSeconds = 0f,
				PressedUtc = _pressUtc,
				PressedGlobalSeconds = _pressGlobal,
				PressedSandboxTimeNowDouble = _pressSandbox,
				ReleasedUtc = null,
				ReleasedGlobalSeconds = null,
				ReleasedSandboxTimeNowDouble = null,
				ViewDirectionOnPress = _viewDirOnPress,
				ViewDirectionOnRelease = null,
				CameraPositionOnPress = _cameraPositionOnPress,
				CameraPositionOnRelease = null
			};
		}

		void FinishPress( Func<Vector3> getCameraPosition, Func<Vector3> getViewDirection, CombatChannelRules rules )
		{
			var releaseGlobal = RealTime.GlobalNow;
			var releaseSandbox = Time.NowDouble;
			var releaseUtc = DateTime.UtcNow;
			var cameraPositionOnRelease = getCameraPosition();
			var viewDirectionOnRelease = getViewDirection();

			Down = false;
			var held = _pressGlobal is { } pg ? (float)( releaseGlobal - pg ) : 0f;

			if ( rules.CooldownAfterValidReleaseSeconds > 0f )
				ScheduleNextPressAllowed( releaseGlobal + rules.CooldownAfterValidReleaseSeconds );

			Snapshot = new CombatButtonIntentSnapshot
			{
				IsHeld = false,
				HoldDurationSeconds = held,
				PressedUtc = _pressUtc,
				PressedGlobalSeconds = _pressGlobal,
				PressedSandboxTimeNowDouble = _pressSandbox,
				ReleasedUtc = releaseUtc,
				ReleasedGlobalSeconds = releaseGlobal,
				ReleasedSandboxTimeNowDouble = releaseSandbox,
				ViewDirectionOnPress = _viewDirOnPress,
				ViewDirectionOnRelease = viewDirectionOnRelease,
				CameraPositionOnPress = _cameraPositionOnPress,
				CameraPositionOnRelease = cameraPositionOnRelease
			};

			_onValidRelease?.Invoke( Snapshot );

			_pressGlobal = null;
			_pressSandbox = null;
			_pressUtc = null;
			_viewDirOnPress = null;
			_cameraPositionOnPress = null;
		}

		void ScheduleNextPressAllowed( double notBeforeGlobal )
		{
			_nextPressAllowedAtGlobal = _nextPressAllowedAtGlobal is { } ex
				? Math.Max( ex, notBeforeGlobal )
				: notBeforeGlobal;
		}
	}
}
