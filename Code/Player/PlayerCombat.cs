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
	const string CombatClientLogVersion = "swing-window-v2";

	// --- Debug (inspector toggles at top) ---

	[Property, Group( "Combat — Debug" )] public bool ShowCombatInputDebug { get; set; } = true;

	[Property, Group( "Combat — Debug" )] public bool LogCombatNetworkingToConsole { get; set; }

	/// <summary>Owner: logs predicted primary-attack stamina (hold vs formula cost) when the swing window submits to the host.</summary>
	[Property, Group( "Combat — Debug" )] public bool LogAttackStaminaDebug { get; set; }

	[Property, Group( "Combat — Debug" )] public bool LogMeleeSweepHitsToConsole { get; set; }

	/// <summary>Draws attack-path overlay lines/spheres (blue / yellow / red). Gameplay sampling uses <see cref="MeleeAttackArcDegreeStep"/> regardless.</summary>
	[Property, Group( "Combat — Debug" ), Title( "Attack path overlay draw" )]
	public bool MeleeDebugDrawEnabled { get; set; } = true;

	[Property, Group( "Combat — Debug" ), Title( "Overlay spheres at path samples" )]
	public bool MeleeDebugDrawSamplePointsEnabled { get; set; } = true;

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
	[Property, Group( "Combat — Debug" ), Title( "Clients replicate path overlay" )]
	public bool ClientMeleeSwingTraceDebug { get; set; } = true;

	[Property, Group( "Input" )] public string PrimaryAttackAction { get; set; } = "Attack1";
	[Property, Group( "Input" )] public string BlockAction { get; set; } = "Attack2";

	[Property, Group( "Combat — Attack" ), Title( "Attack cooldown after release (s)" ), Description( "Extra press cooldown after a valid release. Keep low — combat recovery paces light spam." )]
	public float AttackCooldownAfterRelease { get; set; } = 0f;
	[Property, Group( "Combat — Block" )] public float BlockCooldownAfterRelease { get; set; } = 0.5f;

	[Property, Group( "Combat — Server validation" )] public float ServerEyeHeight { get; set; } = 64f;

	/// <summary>Scene <see cref="CombatAuthority"/> (e.g. on your network manager). If unset, <see cref="CombatAuthority.Instance"/> is used when enabled.</summary>
	[Property, Group( "Combat — Networking" )] public CombatAuthority HostCombatAuthority { get; set; }

	/// <summary>Authoritative weapon damage before camera/swing alignment (server multiplies that next).</summary>
	[Property, Group( "Combat — Melee" )] public float MeleeWeaponBaseDamage { get; set; } = 8f;

	[Property, Group( "Combat — Stamina" ), Title( "Primary attack stamina (light)" )]
	public float PrimaryAttackStaminaLightCost { get; set; } = 8f;

	[Property, Group( "Combat — Stamina" ), Title( "Primary attack stamina (heavy)" )]
	public float PrimaryAttackStaminaHeavyCost { get; set; } = 15f;

	/// <summary>
	/// After primary <b>release</b>, the owner waits this long while summing raw mouse delta into post-release drag (feeds damage tier on the host).
	/// The attack intent is only sent to the host when this window ends — a large value feels like lag after you let go; use <c>0</c> for snappy (next-frame) dispatch.
	/// </summary>
	[Property, Group( "Combat — Melee" ), Title( "Post-release drag window (s)" )]
	public float SwingDamageWindowSeconds { get; set; } = 0.12f;

	/// <summary>Post-release drag (pixels) on the good or bad axis needed before the combat multiplier gets the bonus or penalty.</summary>
	[Property, Group( "Combat — Melee" )] public float SwingDragGoodPixels { get; set; } = 48f;

	/// <summary>Added to <see cref="MeleeCombatDamageMultiplier.Standard"/> when follow-through drag matches the attack direction.</summary>
	[Property, Group( "Combat — Melee" ), Title( "Swing drag good bonus (+)" )]
	public float MeleeSwingDragGoodBonus { get; set; } = 0.15f;

	/// <summary>Subtracted from the combat multiplier when follow-through drag opposes the attack direction.</summary>
	[Property, Group( "Combat — Melee" ), Title( "Swing drag bad penalty (−)" )]
	public float MeleeSwingDragBadPenalty { get; set; } = 0.15f;

	/// <summary>Clamp client-reported post-swing drag vector length (anti-cheat / overflow).</summary>
	[Property, Group( "Combat — Melee" )] public float SwingMaxPostDragSanityPixels { get; set; } = 2800f;

	[Property, Group( "Combat — Melee (attack action)" )] public GameObject MeleeBladeTip { get; set; }

	[Property, Group( "Combat — Melee (attack action)" )] public GameObject MeleeBladeHeel { get; set; }

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Light windup duration (s)" ), Description( "Light attack windup. Chart total light = windup + damage window + outcome recovery." )]
	public float MeleeWindupDuration { get; set; } = 0.22f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Heavy windup duration (s)" )]
	public float MeleeHeavyWindupDuration { get; set; } = 0.30f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Show windup direction telegraph" )]
	public bool ShowMeleeAttackWindupTelegraph { get; set; } = true;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Windup telegraph line thickness" )]
	public float MeleeWindupTelegraphThickness { get; set; } = 6f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Recovery duration (s)" ), Description( "Built-in sweep idle after active phases. Outcome combat recovery owns the real lock — keep this near 0." )]
	public float MeleeRecoveryDuration { get; set; } = 0f;

	/// <summary>Windup seconds for light or heavy attack timelines.</summary>
	public float GetMeleeWindupDuration( bool isHeavy ) =>
		Math.Max( 0f, isHeavy ? MeleeHeavyWindupDuration : MeleeWindupDuration );

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Attack range L/R" )]
	public float MeleeAttackRangeLeftRight { get; set; } = 76f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Attack range forward" )]
	public float MeleeAttackRangeForward { get; set; } = 76f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Hit volume thickness" )]
	public float MeleeHitVolumeThickness { get; set; } = 2f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Sweep substep length" )]
	public float MeleeSweepSubstepLength { get; set; } = 12f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Heavy attack hold threshold (s)" )]
	public float MeleeHeavyAttackHoldThreshold { get; set; } = 0.7f;

	/// <summary>Added to the combat damage multiplier when the attack is heavy (see <see cref="ComputeMeleeCombatDamageMultiplier"/>).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Heavy attack damage bonus (+)" )]
	public float MeleeHeavyAttackDamageBonus { get; set; } = 0.3f;

	/// <summary>Baseline combat multiplier before drag/heavy bonuses (normally <see cref="MeleeCombatDamageMultiplier.Standard"/>).</summary>
	[Property, Group( "Combat — Melee" ), Title( "Base combat damage multiplier" )]
	public float MeleeBaseCombatDamageMultiplier { get; set; } = MeleeCombatDamageMultiplier.Standard;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Allow multiple hits per attack" )]
	public bool MeleeAllowMultipleHitsPerAttack { get; set; }

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Max targets hit" )]
	public int MeleeMaxTargetsHit { get; set; } = 1;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Lateral arc total (°)" )]
	public float MeleeLateralArcTotalDegrees { get; set; } = 150f;

	/// <summary>
	/// Spacing along the attack path and per degree of body turn (1 = one ray every 1° along the arc; 150° → ~151 samples).
	/// Drives core path sampling; overlay lines use <see cref="MeleeDebugDrawEnabled"/>.
	/// </summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Attack arc degree step" )]
	public float MeleeAttackArcDegreeStep { get; set; } = 1f;

	internal float GetMeleeAttackArcDegreeStep() => Math.Max( 1f, MeleeAttackArcDegreeStep );

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Light EarlyActive duration (s) — blue" ), Description( "Light damage window = Early + Active + Late (default 0.10s)." )]
	public float MeleeEarlyActiveDuration { get; set; } = 0.02f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Light Active duration (s) — yellow" )]
	public float MeleeActiveDuration { get; set; } = 0.06f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Light LateActive duration (s) — red" )]
	public float MeleeLateActiveDuration { get; set; } = 0.02f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Heavy EarlyActive duration (s)" ), Description( "Heavy damage window = Early + Active + Late (default 0.15s)." )]
	public float MeleeHeavyEarlyActiveDuration { get; set; } = 0.03f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Heavy Active duration (s)" )]
	public float MeleeHeavyActiveDuration { get; set; } = 0.09f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Heavy LateActive duration (s)" )]
	public float MeleeHeavyLateActiveDuration { get; set; } = 0.03f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Tilt left (° start→end drop only)" )]
	public float MeleeAttackTiltDegreesLeft { get; set; } = 25f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Tilt right (° start→end drop only)" )]
	public float MeleeAttackTiltDegreesRight { get; set; } = 25f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Tilt forward (° right-plane offset)" )]
	public float MeleeAttackTiltDegreesForward { get; set; } = 10f;

	/// <summary>Baseline offset along world up from <see cref="ServerEyeHeight"/> for L/R slashes (yaw-only basis; pitch ignored).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Side slash start height" )]
	public float MeleeAttackZaxisStart { get; set; } = -10f;

	/// <summary>Max forward local reach as a fraction of <see cref="MeleeAttackRangeForward"/>.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward max reach (× attackRangeForward)" )]
	public float MeleeAttackForwardMaxReachFraction { get; set; } = 1f;

	/// <summary>Scales overhead +X reach vs lateral-matched path (1 = same; lower = shorter forward).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward path reach scale" )]
	public float MeleeAttackForwardPathReachScale { get; set; } = 0.94f;

	/// <summary>Overhead arc span (°) — default matches <see cref="MeleeLateralArcTotalDegrees"/>; end = start − total.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward arc total (°)" )]
	public float MeleeAttackForwardArcTotalDegrees { get; set; } = 158f;

	/// <summary>Start angle on vertical arc (0° = +X forward, 90° = +Y up). End = start − arc total. Higher = more up at windup.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward arc start (°)" )]
	public float MeleeAttackForwardArcStartDegrees { get; set; } = 146f;

	/// <summary>Cos scale at windup; ramps to <see cref="MeleeAttackForwardArcForwardScale"/> by mid-stroke.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward arc forward scale (start)" )]
	public float MeleeAttackForwardArcForwardScaleStart { get; set; } = 1f;

	/// <summary>Legacy cos scale (path uses lateral-matched reach; kept for prefab compatibility).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward arc forward scale" )]
	public float MeleeAttackForwardArcForwardScale { get; set; } = 1f;

	/// <summary>Scales sin (up/down) component — higher exaggerates up-then-down.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward arc vertical scale" )]
	public float MeleeAttackForwardArcVerticalScale { get; set; } = 1.02f;

	/// <summary>Legacy — no longer drives pivot; kept for prefab revert. See head pivot locals below.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward pivot forward start (× range, legacy)" )]
	public float MeleeAttackForwardPivotForwardStartFraction { get; set; } = 0.035f;

	/// <summary>Legacy — no longer drives pivot; kept for prefab revert. See head pivot locals below.</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward pivot forward end (× range, legacy)" )]
	public float MeleeAttackForwardPivotForwardEndFraction { get; set; } = 0.09f;

	/// <summary>Overhead arc pivot: combat-local +X from body origin at stroke start (head/shoulder, not weapon reach).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward pivot forward local (start)" )]
	public float MeleeAttackForwardPivotForwardLocal { get; set; } = 8f;

	/// <summary>Overhead arc pivot forward at stroke end (lerps with start over the swing).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward pivot forward local (end)" )]
	public float MeleeAttackForwardPivotForwardLocalEnd { get; set; } = 10f;

	/// <summary>Added to <see cref="ServerEyeHeight"/> for arc pivot height (negative ≈ neck/shoulder beside head).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward pivot up from eye" )]
	public float MeleeAttackForwardPivotUpFromEye { get; set; } = -8f;

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

	/// <summary>How much pitch change during the swing bends the arc (lean back → lean forward).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward lean pitch influence" )]
	public float MeleeAttackForwardLeanPitchInfluence { get; set; } = 0.55f;

	/// <summary>How much camera pitch steers the overhead arc (0 = yaw only, 1 = full pitch).</summary>
	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward pitch influence" )]
	public float MeleeAttackForwardPitchInfluence { get; set; } = 0.42f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward min pitch (° look-up cap)" )]
	public float MeleeAttackForwardMinPitchDegrees { get; set; } = -38f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Forward max pitch (° look-down cap)" )]
	public float MeleeAttackForwardMaxPitchDegrees { get; set; } = 38f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "EarlyActive damage penalty (−)" )]
	public float MeleeEarlyActiveDamagePenalty { get; set; } = 0.15f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "LateActive damage bonus (+)" )]
	public float MeleeLateActiveDamageBonus { get; set; } = 0.15f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Base stagger" )]
	public float MeleeBaseStagger { get; set; } = 0.45f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "EarlyActive stagger multiplier" )]
	public float MeleeEarlyActiveStaggerMultiplier { get; set; } = 0.33f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "Active stagger multiplier" )]
	public float MeleeActiveStaggerMultiplier { get; set; } = 1f;

	[Property, Group( "Combat — Melee (attack action)" ), Title( "LateActive stagger multiplier" )]
	public float MeleeLateActiveStaggerMultiplier { get; set; } = 0.33f;

	[Property, Group( "Combat — Melee (attack action)" )] public float MeleeVictimStaminaDrainOnHit { get; set; }

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

	public CombatButtonIntentSnapshot PrimaryAttack => _primary.Snapshot;
	public CombatButtonIntentSnapshot Block => _block.Snapshot;

	readonly CombatChannel _primary = new();
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

	byte _lockedPrimaryAttackSwingDir;
	bool _hasLockedPrimaryAttackDir;
	bool _wasPrimaryAttackButtonDownLastFrame;
	bool _primarySwingPhaseActive;
	/// <summary>End of post-release swing window, in <see cref="Time.NowDouble"/> (same clock as combat snapshots).</summary>
	double _primarySwingPhaseEndAtSandbox;
	Vector2 _primaryPostReleaseDragAccum;
	AttackReleaseIntent _pendingPrimarySwingIntent;
	/// <summary>Owner expects the host melee action to still be running (online clients without local runtime).</summary>
	bool _ownerExpectsHostMeleeBusy;

	/// <summary>When set, non-local attack/block paths use intent yaw instead of pawn body rotation.</summary>
	float? _meleeIntentBasisYawOverride;
	bool _meleeIntentForwardPitchCaptured;
	float _meleeIntentForwardStartPitchDegrees;

	bool _windupTelegraphActive;
	byte _windupTelegraphAttackType;
	float _windupTelegraphBasisYaw;
	bool _windupTelegraphHeavy;
	bool _lastSentWindupTelegraphActive;
	byte _lastSentWindupTelegraphAttackType;
	float _lastSentWindupTelegraphBasisYaw;
	bool _lastSentWindupTelegraphHeavy;
	bool _lastSentWindupTelegraphValid;
	bool _lastBroadcastWindupTelegraphActive;
	byte _lastBroadcastWindupTelegraphAttackType;
	float _lastBroadcastWindupTelegraphBasisYaw;
	bool _lastBroadcastWindupTelegraphHeavy;
	bool _lastBroadcastWindupTelegraphValid;

	bool IsLocalCombatDriver()
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

		var menuController = Components.Get<PlayerGameMenuController>();
		if ( menuController is not null && menuController.IsMenuOpen )
			return;

		// Shove is a player ability, not a weapon one — it runs before the melee-item gate below and
		// works bare-handed. (Equipment used to disable this whole component when no melee item was
		// held, which killed the shove, the hit reaction, and the jump/grapple locks along with it.)
		TickOwnerShoveInput();

		// Everything past here is the sword: swing input, block, telegraphs.
		var equipped = Components.Get<PlayerEquippedItem>();
		if ( equipped is not null && !equipped.HasAction( EquippedItemActions.PrimaryMelee ) )
			return;

		_combatNetDiag = $"netActive={GameObject.Network is { Active: true }} isHost={Networking.IsHost}";

		// Finish any expired swing window before processing this frame's input (uses sandbox time, not wall clock).
		MaybeCompletePrimarySwingPhase();

		// Attacks are strictly stateful: pressing while the swing animation / sweep / recovery is still
		// running does nothing at all (no buffer, no queued follow-up, no chained spam swing).
		if ( Input.Pressed( PrimaryAttackAction ) && IsMeleeAttackChainBusy() )
			LogCombatDiag( "CLIENT / OWNER", $"Attack press ignored — {FormatMeleePhaseBusyReason()}" );

		// Only a fresh press on an idle timeline starts an attack — holding through the busy window
		// deliberately does not auto-charge the next swing.
		_primary.Step( PrimaryAttackAction, CanStartPrimaryAttack, CanContinuePrimaryAttack, GetViewDirectionForIntent, GetCameraPositionForIntent, GetPrimaryAttackRules(), OnOwnerValidPrimaryAttackRelease );
		_block.Step( BlockAction, CanStartBlock, CanContinueBlock, GetViewDirectionForIntent, GetCameraPositionForIntent, GetBlockRules(), OnOwnerValidBlockRelease );

		var blockStartedThisFrame = _block.Down && !_wasBlockButtonDownLastFrame;
		if ( blockStartedThisFrame )
		{
			_blockLookAccum = default;
			OnBlockPressCommitGuardDirection();
			CancelAllAttackActivity();
		}

		_wasBlockButtonDownLastFrame = _block.Down;

		// Lock attack direction on press / first held frame. Do NOT cancel an in-flight swing window —
		// spam-clicking used to abort the light attack before it could dispatch (black telegraph, no swing).
		var primaryAttackHeld = Input.Down( PrimaryAttackAction );
		if ( !IsBlockPreventingAttack()
		     && !_primarySwingPhaseActive
		     && (Input.Pressed( PrimaryAttackAction ) || (primaryAttackHeld && !_wasPrimaryAttackButtonDownLastFrame)) )
		{
			LockPreparedPrimaryAttackDirection();
			// Only start the windup clip when the combat channel actually accepted the press.
			if ( !IsMeleeAttackChainBusy() && _primary.Down )
			{
				var windupType = ResolveAttackTypeFromCursorDir( _lockedPrimaryAttackSwingDir );
				Components.Get<PlayerAnimation>()?.BeginMeleeAttackWindupHold( windupType );
			}
		}

		Components.Get<PlayerAnimation>()?.TickMeleeAttackWindupHold(
			primaryAttackHeld && !_primarySwingPhaseActive && !IsBlockPreventingAttack() );

		_wasPrimaryAttackButtonDownLastFrame = primaryAttackHeld;

		if ( Input.Released( PrimaryAttackAction ) && !_primarySwingPhaseActive )
			_hasLockedPrimaryAttackDir = false;

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

		// Primary: locked on press; frozen while Attack1 is held and through post-release drag window.
		var primaryAttackHeld = Input.Down( PrimaryAttackAction );
		var attackDirectionFrozen = primaryAttackHeld || _primarySwingPhaseActive;
		if ( !attackDirectionFrozen )
		{
			_primarySwingEvidence = _primarySwingEvidence * decay + frame;
			ApplyLiveSwingFromEvidence( _primarySwingEvidence, ref _primaryLiveSwingDir, ref _primaryLastFlipRealSeconds,
				invertAttackLateral: true );
		}
		else if ( _hasLockedPrimaryAttackDir )
			_primaryLiveSwingDir = _lockedPrimaryAttackSwingDir;
		else if ( primaryAttackHeld )
			LockPreparedPrimaryAttackDirection();

		if ( _primarySwingPhaseActive && Time.NowDouble < _primarySwingPhaseEndAtSandbox )
			_primaryPostReleaseDragAccum += rawFrame;

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
		_hasLockedPrimaryAttackDir = true;
	}

	void CancelPrimarySwingPhase()
	{
		if ( !_primarySwingPhaseActive )
			return;
		_primarySwingPhaseActive = false;
		_primaryPostReleaseDragAccum = default;
		_hasLockedPrimaryAttackDir = false;
		_combatNetDiag = "swing window cancelled";
		LogCombatDiag( "CLIENT / OWNER", "Cancelled swing phase (new attack press)." );
	}

	void MaybeCompletePrimarySwingPhase()
	{
		if ( !_primarySwingPhaseActive )
			return;

		if ( Time.NowDouble < _primarySwingPhaseEndAtSandbox )
			return;

		_primarySwingPhaseActive = false;
		_hasLockedPrimaryAttackDir = false;

		var drag = _primaryPostReleaseDragAccum;
		var maxLen = Math.Max( 32f, SwingMaxPostDragSanityPixels );
		if ( drag.Length > maxLen )
			drag = drag.Normal * maxLen;

		var attackType = ResolveAttackTypeFromCursorDir( _pendingPrimarySwingIntent.SwingDir );
		var sent = _pendingPrimarySwingIntent with
		{
			PostSwingDragScreenX = drag.x,
			PostSwingDragScreenY = drag.y,
			ViewForwardOnRelease = GetViewDirectionForIntent(),
			CombatBasisYawDegrees = GetMeleeCombatBasisYaw( attackType ),
			CombatBasisPitchDegrees = GetCameraPitchDegrees()
		};

		_primaryPostReleaseDragAccum = default;

		LogCombatDiag( "CLIENT / OWNER",
			$"{CombatClientLogVersion} — Submit swing end seq={sent.IntentSequence} drag=({sent.PostSwingDragScreenX:F1},{sent.PostSwingDragScreenY:F1}) {CombatAuthority.FormatSwingLog( new Vector2( sent.SwingFromX, sent.SwingFromY ), sent.SwingVerticalHint, sent.SwingDir )}" );
		if ( LogAttackStaminaDebug )
		{
			var hold = Math.Max( 0f, (float)( sent.ReleasedGlobalSeconds - sent.PressedGlobalSeconds ) );
			var predicted = GetPrimaryAttackStaminaCostForHoldDuration( hold );
			var heavy = IsHeavyAttackForHoldDuration( hold );
			Log.Info( $"[PlayerCombat/Stamina] predict hold={hold:0.###}s cost={predicted:0.#} heavy={heavy} (light={PrimaryAttackStaminaLightCost:0.#} heavy={PrimaryAttackStaminaHeavyCost:0.#})" );
		}

		DispatchPrimaryAttackReleaseToAuthority( sent );
	}

	protected virtual bool CanStartPrimaryAttack() =>
		!IsMeleeAttackChainBusy() && !IsBlockPreventingAttack() && CanAffordPrimaryAttackOnPress();

	protected virtual bool CanContinuePrimaryAttack()
	{
		if ( IsMeleeAttackChainBusy() || IsBlockPreventingAttack() )
			return false;

		var vitals = Components.Get<PlayerVitals>();
		if ( vitals is null )
			return false;

		var hold = _primary.Snapshot.HoldDurationSeconds;
		return vitals.CanAffordStamina( GetPrimaryAttackStaminaCostForHoldDuration( hold ) );
	}

	/// <summary>Block and attack are mutually exclusive — holding guard rejects new/ongoing primary attack intents.</summary>
	bool IsBlockPreventingAttack() =>
		LocalBlockInputActive() || IsAuthoritativeMeleeBlocking;

	protected virtual bool CanStartBlock() =>
		!IsCombatActionLocked && _postBlockRecoveryRemaining <= 0.001f;

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
	byte ClassifyLiveSwingFrame( Vector2 mouseDelta, byte current )
	{
		var min = Math.Max( SwingLiveMicroMotionPixels, SwingAxisDeadzone * 0.25f );
		var yUp = SwingInvertLookYForUp ? -mouseDelta.y : mouseDelta.y;
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
				return dx > 0f ? SwingDirs.Right : SwingDirs.Left;
			return current;
		}

		if ( yUp > min )
			return SwingDirs.Up;
		if ( yUp < -min )
		{
			if ( dx > min )
				return SwingDirs.Right;
			if ( dx < -min )
				return SwingDirs.Left;
			return dx > 0f ? SwingDirs.Right : SwingDirs.Left;
		}

		if ( dx > min )
			return SwingDirs.Right;
		if ( dx < -min )
			return SwingDirs.Left;
		return current;
	}

	/// <summary>Signed pixels of the decayed evidence that support choosing <paramref name="cardinal"/> (same basis as <see cref="ClassifyLiveSwingFrame"/>).</summary>
	float ContributionTowardCardinal( byte cardinal, Vector2 mouseDelta )
	{
		var yUp = SwingInvertLookYForUp ? -mouseDelta.y : mouseDelta.y;
		var dx = mouseDelta.x;
		if ( cardinal == SwingDirs.Left )
			return MathF.Max( 0f, -dx );
		if ( cardinal == SwingDirs.Right )
			return MathF.Max( 0f, dx );
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
		var desire = ClassifyLiveSwingFrame( evidence, classifyCurrent );
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
		SwingDirs.MirrorLateral( ClassifyLiveSwingFrame( evidence, SwingDirs.MirrorLateral( currentAttackDir ) ) );

	/// <summary>
	/// Screen-space teardrop direction for the unified crosshair (<see cref="PlayerCrosshair"/>);
	/// +x right, +y down. Attack hold locks the direction; block morph applies when not attacking.
	/// </summary>
	public Vector2 GetTeardropScreenDirection()
	{
		var primaryAttackHeld = Input.Down( PrimaryAttackAction );
		var attackFrozen = primaryAttackHeld || _primarySwingPhaseActive;
		byte swingPreview;
		var mirrorTeardropForAttack = false;
		if ( attackFrozen && _hasLockedPrimaryAttackDir )
		{
			swingPreview = _lockedPrimaryAttackSwingDir;
			mirrorTeardropForAttack = true;
		}
		else if ( Input.Down( BlockAction ) )
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

	/// <summary>Camera pitch clamped and scaled for overhead arcs — not used for L/R slashes.</summary>
	public float GetMeleeForwardInfluencedPitchDegrees()
	{
		var min = Math.Min( MeleeAttackForwardMinPitchDegrees, MeleeAttackForwardMaxPitchDegrees );
		var max = Math.Max( MeleeAttackForwardMinPitchDegrees, MeleeAttackForwardMaxPitchDegrees );
		var pitch = _meleeIntentForwardPitchCaptured
			? _meleeIntentForwardStartPitchDegrees
			: Math.Clamp( GetCameraPitchDegrees(), min, max );
		var influence = Math.Clamp( MeleeAttackForwardPitchInfluence, 0f, 1f );
		return pitch * influence;
	}

	/// <summary>
	/// Remote / host-proxy pawns: pin aim from submitted intent so overlays match the attacker.
	/// Local owner: live camera yaw (and forward pitch capture) so turning during a swing paints debug coverage.
	/// </summary>
	internal void PushMeleeAttackBasisFromIntent( in AttackReleaseIntent intent, byte attackType )
	{
		if ( IsLocalCombatDriver() )
		{
			ClearMeleeAttackBasisFromIntent();
			CaptureForwardMeleeStartPitch( attackType );
			return;
		}

		_meleeIntentBasisYawOverride = ResolveIntentCombatBasisYaw( intent, attackType );
		if ( attackType == MeleeAttackTypes.Forward )
		{
			var min = Math.Min( MeleeAttackForwardMinPitchDegrees, MeleeAttackForwardMaxPitchDegrees );
			var max = Math.Max( MeleeAttackForwardMinPitchDegrees, MeleeAttackForwardMaxPitchDegrees );
			var pitch = Math.Clamp( ResolveIntentViewPitchDegrees( intent ), min, max );
			var influence = Math.Clamp( MeleeAttackForwardPitchInfluence, 0f, 1f );
			_meleeIntentForwardStartPitchDegrees = pitch * influence;
			_meleeIntentForwardPitchCaptured = true;
		}
		else
			_meleeIntentForwardPitchCaptured = false;
	}

	internal void ClearMeleeAttackBasisFromIntent()
	{
		_meleeIntentBasisYawOverride = null;
		_meleeIntentForwardPitchCaptured = false;
	}

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

	Rotation GetMeleeForwardCombatBasisRotation()
	{
		var aim = GetCameraAimRotation();
		var yaw = aim.Angles().yaw;
		var pitch = GetMeleeForwardInfluencedPitchDegrees();
		return new Angles( pitch, yaw, 0f ).ToRotation();
	}

	/// <summary>L/R combat basis: camera yaw projected on the horizontal plane — pitch ignored.</summary>
	Rotation GetMeleeLateralCombatBasisRotation() => GetCameraYawRotation();

	/// <summary>Live aim for melee paths — yaw-only for L/R; yaw + influenced pitch for overhead.</summary>
	public Rotation GetMeleeCombatBasisRotation( byte attackType )
	{
		if ( _meleeIntentBasisYawOverride is { } intentYaw )
			return GetMeleeCombatBasisRotationForYaw( attackType, intentYaw );

		return attackType == MeleeAttackTypes.Forward
			? GetMeleeForwardCombatBasisRotation()
			: GetMeleeLateralCombatBasisRotation();
	}

	/// <summary>Live aim for melee paths when attack type is unknown — yaw-only horizontal basis.</summary>
	public Rotation GetMeleeCombatBasisRotation() => GetMeleeLateralCombatBasisRotation();

	internal static byte NormalizeCardinalBlockDirection( byte dir ) =>
		dir is (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up) ? dir : SwingDirs.Up;

	/// <summary>Horizontal yaw of the live combat basis for this attack type.</summary>
	public float GetMeleeCombatBasisYaw( byte attackType ) =>
		GetMeleeCombatBasisRotation( attackType ).Angles().yaw;

	/// <summary>Combat basis with an explicit yaw (pitch rules unchanged for overhead).</summary>
	public Rotation GetMeleeCombatBasisRotationForYaw( byte attackType, float yawDegrees )
	{
		if ( attackType == MeleeAttackTypes.Forward )
			return new Angles( GetMeleeForwardInfluencedPitchDegrees(), yawDegrees, 0f ).ToRotation();

		return new Angles( 0f, yawDegrees, 0f ).ToRotation();
	}

	float _forwardMeleeStartPitchDegrees;
	bool _forwardMeleeStartPitchCaptured;

	public void CaptureForwardMeleeStartPitch( byte attackType )
	{
		if ( attackType != MeleeAttackTypes.Forward )
			return;

		_forwardMeleeStartPitchDegrees = GetMeleeForwardInfluencedPitchDegrees();
		_forwardMeleeStartPitchCaptured = true;
	}

	public void ClearForwardMeleeStartPitch()
	{
		_forwardMeleeStartPitchCaptured = false;
	}

	/// <summary>Pitch delta since forward attack start — bends arc when leaning back then forward.</summary>
	public float GetForwardMeleePitchLeanDegrees()
	{
		if ( !_forwardMeleeStartPitchCaptured )
			return 0f;

		var delta = GetMeleeForwardInfluencedPitchDegrees() - _forwardMeleeStartPitchDegrees;
		return delta * Math.Clamp( MeleeAttackForwardLeanPitchInfluence, 0f, 1.5f );
	}

	CombatChannelRules GetPrimaryAttackRules() => new CombatChannelRules { CooldownAfterValidReleaseSeconds = AttackCooldownAfterRelease };
	CombatChannelRules GetBlockRules() => new CombatChannelRules { CooldownAfterValidReleaseSeconds = BlockCooldownAfterRelease };

	/// <summary>Light vs heavy stamina from hold duration vs <see cref="MeleeHeavyAttackHoldThreshold"/>.</summary>
	public float GetPrimaryAttackStaminaCostForHoldDuration( float holdSeconds ) =>
		IsHeavyAttackForHoldDuration( holdSeconds )
			? Math.Max( 0f, PrimaryAttackStaminaHeavyCost )
			: Math.Max( 0f, PrimaryAttackStaminaLightCost );

	/// <summary>Press gate: afford a light tap; hold past heavy threshold requires heavy cost while charging.</summary>
	bool CanAffordPrimaryAttackOnPress()
	{
		var minCost = GetPrimaryAttackStaminaCostForHoldDuration( 0f );
		if ( minCost <= 0f && PrimaryAttackStaminaHeavyCost <= 0f )
			return true;

		var vitals = Components.Get<PlayerVitals>();
		if ( vitals is null )
			return false;

		return vitals.CanAffordStamina( minCost );
	}

	/// <summary>
	/// Legacy field on <see cref="AttackReleaseIntent"/>: kept at 0 so the host applies a single stamina drain from hold duration
	/// (<see cref="GetPrimaryAttackStaminaCostForHoldDuration"/>) on release instead of max prepay + settle.
	/// </summary>
	public float GetPrimaryAttackPressStaminaPrepayAmount() => 0f;

	void OnOwnerValidPrimaryAttackRelease( CombatButtonIntentSnapshot snapshot )
	{
		if ( !EnableServerPrimaryAttackValidation )
		{
			_combatNetDiag = "validation OFF — no intent sent";
			LogCombatDiag( "CLIENT / OWNER", "EnableServerPrimaryAttackValidation is false — no intent sent." );
			return;
		}

		if ( !TryBuildPrimaryAttackReleaseIntent( snapshot, out var intent ) )
			return;

		if ( _primarySwingPhaseActive )
		{
			_combatNetDiag = "dropped — swing window active";
			LogCombatDiag( "CLIENT / OWNER",
				$"{CombatClientLogVersion} — Dropped release seq={intent.IntentSequence} (swing window still active)" );
			return;
		}

		_pendingPrimarySwingIntent = intent;
		_primaryPostReleaseDragAccum = default;
		var w = SwingDamageWindowSeconds;
		if ( !float.IsFinite( w ) || w < 0f )
			w = 0.12f;
		var window = Math.Max( 0.0, (double)w );
		_primarySwingPhaseEndAtSandbox = Time.NowDouble + window;
		_primarySwingPhaseActive = true;

		var releaseHold = Math.Max( 0f, snapshot.HoldDurationSeconds );
		var releaseHeavy = IsHeavyAttackForHoldDuration( releaseHold );
		Components.Get<PlayerAnimation>()?.ReleaseMeleeAttackWindupHold( intent.AttackType, releaseHeavy );

		_combatNetDiag = $"swing window {window:0.###}s (drag→dmg)";
		LogCombatDiag( "CLIENT / OWNER",
			$"{CombatClientLogVersion} — Begin swing seq={intent.IntentSequence} locked={SwingDirs.Letter( intent.SwingDir )} held={snapshot.HoldDurationSeconds:0.###}s — damage after window" );
	}

	bool TryBuildPrimaryAttackReleaseIntent( CombatButtonIntentSnapshot snapshot, out AttackReleaseIntent intent )
	{
		intent = default;
		if ( snapshot.ViewDirectionOnPress is not { } vf
		     || snapshot.ViewDirectionOnRelease is not { } vr
		     || snapshot.CameraPositionOnPress is not { } cp
		     || snapshot.CameraPositionOnRelease is not { } cr
		     || snapshot.PressedGlobalSeconds is not { } pg
		     || snapshot.ReleasedGlobalSeconds is not { } rg )
		{
			_combatNetDiag = "snapshot incomplete — intent not sent";
			LogCombatDiag( "CLIENT / OWNER", "Release snapshot missing view/camera/times — intent not sent." );
			return false;
		}

		_attackIntentSequence++;
		var c = _hasLockedPrimaryAttackDir ? _lockedPrimaryAttackSwingDir : _primaryLiveSwingDir;
		CardinalVectors( c, vr, out var swingXz, out var swingV );

		var prepay = GetPrimaryAttackPressStaminaPrepayAmount();
		var attackType = ResolveAttackTypeFromCursorDir( c );

		intent = new AttackReleaseIntent
		{
			PressedGlobalSeconds = pg,
			ReleasedGlobalSeconds = rg,
			ClientCameraPressX = cp.x,
			ClientCameraPressY = cp.y,
			ClientCameraPressZ = cp.z,
			ClientCameraReleaseX = cr.x,
			ClientCameraReleaseY = cr.y,
			ClientCameraReleaseZ = cr.z,
			ViewForwardOnPress = vf,
			ViewForwardOnRelease = vr,
			ClientPlayerWorldPosition = WorldPosition,
			ClientPlayerWorldRotation = WorldRotation,
			IntentSequence = _attackIntentSequence,
			SwingFromX = swingXz.x,
			SwingFromY = swingXz.y,
			SwingVerticalHint = swingV,
			SwingDir = c,
			AttackType = attackType,
			StaminaPrepaidMax = prepay,
			PostSwingDragScreenX = 0f,
			PostSwingDragScreenY = 0f,
			CombatBasisYawDegrees = GetMeleeCombatBasisYaw( attackType ),
			CombatBasisPitchDegrees = GetCameraPitchDegrees()
		};
		return true;
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
		// Gating happens once, on press (see CanStartPrimaryAttack / IsMeleeAttackChainBusy).

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
				_ownerExpectsHostMeleeBusy = true;
			return;
		}

		_ownerExpectsHostMeleeBusy = true;
		_combatNetDiag = Networking.IsHost ? "host→Rpc.Host (single server pass)" : "RPC->host sent (await result)";
		LogCombatDiag( "CLIENT (dispatch)", "RpcSubmitPrimaryAttackRelease -> host (see editor Output)" );
		_pendingSwingVisualIntent = intent;
		_hasPendingSwingVisualIntent = true;
		RpcSubmitPrimaryAttackRelease( intent );
	}

	/// <summary>
	/// One gate for "a swing owns this pawn": the committed swing clip window (through its return frames),
	/// the host sweep, the owner drag window, and recovery / hit reaction. While true, Attack1 does nothing —
	/// there is no buffering, so the player must press again once the animation has finished.
	/// The press windup itself is not busy, or the attack being aimed could never fire.
	/// </summary>
	bool IsMeleeAttackChainBusy()
	{
		if ( IsCombatActionLocked )
			return true;

		if ( ServerHasActiveMeleeAttackAction )
			return true;

		if ( _primarySwingPhaseActive )
			return true;

		// Swing animation owns the pawn until its clip window (including return frames) ends.
		if ( Components.Get<PlayerAnimation>() is { IsMeleeSwingAnimBusy: true } )
			return true;

		// Pure clients need this until RpcOwner sweep-complete. Listen-server host uses ServerHasActive above —
		// keeping ownerExpects after sweep end locked out the next press for ~a second / forever.
		if ( GameObject.Network is { Active: true }
		     && !Networking.IsHost
		     && _ownerExpectsHostMeleeBusy )
			return true;

		return false;
	}

	[Rpc.Host]
	public void RpcSubmitPrimaryAttackRelease( AttackReleaseIntent intent )
	{
		var sx = new Vector2( intent.SwingFromX, intent.SwingFromY );
		var sv = CombatAuthority.ServerClampSwingVertical( intent.SwingVerticalHint );
		LogCombatDiag( "SERVER (Rpc.Host)",
			$"RpcSubmitPrimaryAttackRelease seq={intent.IntentSequence} drag=({intent.PostSwingDragScreenX:F0},{intent.PostSwingDragScreenY:F0}) camP=({intent.ClientCameraPressX:F1},{intent.ClientCameraPressY:F1},{intent.ClientCameraPressZ:F1}) camR=({intent.ClientCameraReleaseX:F1},{intent.ClientCameraReleaseY:F1},{intent.ClientCameraReleaseZ:F1}) {CombatAuthority.FormatSwingLog( sx, sv, intent.SwingDir )}" );
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
			_ownerExpectsHostMeleeBusy = false;

		// Backup: if HostOnly broadcast was missed, still show the local slash path on the attacking client.
		if ( result.Accepted
		     && _hasPendingSwingVisualIntent
		     && MeleeDebugDrawEnabled
		     && ClientMeleeSwingTraceDebug )
		{
			StartClientMeleeSwingTracePlayback( _pendingSwingVisualIntent );
		}

		_hasPendingSwingVisualIntent = false;
	}

	[Rpc.Owner]
	public void RpcOwnerMeleeSwingComplete( ushort intentSequence, bool anyHit, float totalDamageDealt, Guid firstHitTargetId ) =>
		ApplyAuthoritativeMeleeSweepSummary( intentSequence, anyHit, totalDamageDealt, firstHitTargetId );

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
		_ownerExpectsHostMeleeBusy = false;
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
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ), FormatChannel( "Attack1 (primary)", PrimaryAttackAction, _primary ), size: 14f );
		y += 18f;
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ), FormatChannel( "Attack2 (block)", BlockAction, _block ), size: 14f );
		y += 18f;
		DebugOverlay.ScreenText( new Vector2( debugScreenX, y ),
			$"atk lock={(_hasLockedPrimaryAttackDir ? SwingDirs.Letter( _lockedPrimaryAttackSwingDir ) : "—")} live={SwingDirs.Letter( _primaryLiveSwingDir )} phase={_primarySwingPhaseActive} postDrag={_primaryPostReleaseDragAccum.x:F0},{_primaryPostReleaseDragAccum.y:F0}",
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

		public void Step( string actionName, Func<bool> canStart, Func<bool> canContinue, Func<Vector3> getViewDirection, Func<Vector3> getCameraPosition, CombatChannelRules rules, Action<CombatButtonIntentSnapshot> onValidRelease = null )
		{
			_onValidRelease = onValidRelease;

			var wantsDown = Input.Down( actionName );
			var pressed = Input.Pressed( actionName );
			var released = Input.Released( actionName );

			if ( pressed && !Down )
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
