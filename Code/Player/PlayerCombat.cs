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
public class PlayerCombat : Component
{
	const string CombatClientLogVersion = "swing-window-v2";

	[Property] public bool ShowCombatInputDebug { get; set; }

	[Property, Group( "Combat — Debug" )] public bool LogCombatNetworkingToConsole { get; set; } = true;

	/// <summary>Owner: logs predicted primary-attack stamina (hold vs formula cost) when the swing window submits to the host.</summary>
	[Property, Group( "Combat — Debug" )] public bool LogAttackStaminaDebug { get; set; }
	[Property] public bool ShowSwingDirectionCrosshair { get; set; } = true;

	[Property, Group( "Input" )] public string PrimaryAttackAction { get; set; } = "Attack1";
	[Property, Group( "Input" )] public string BlockAction { get; set; } = "Attack2";

	[Property, Group( "Combat — Attack" )] public float AttackCooldownAfterRelease { get; set; } = 0.5f;
	[Property, Group( "Combat — Block" )] public float BlockCooldownAfterRelease { get; set; } = 0.5f;

	[Property, Group( "Combat — Server validation" )] public float ServerEyeHeight { get; set; } = 64f;

	/// <summary>Scene <see cref="CombatAuthority"/> (e.g. on your network manager). If unset, <see cref="CombatAuthority.Instance"/> is used when enabled.</summary>
	[Property, Group( "Combat — Networking" )] public CombatAuthority HostCombatAuthority { get; set; }

	/// <summary>Authoritative weapon damage before camera/swing alignment (server multiplies that next).</summary>
	[Property, Group( "Combat — Melee" )] public float MeleeWeaponBaseDamage { get; set; } = AttackCombatConstants.DefaultMeleeWeaponDamage;

	[Property, Group( "Combat — Stamina" )] public float PrimaryAttackStaminaBase { get; set; } = 5f;

	[Property, Group( "Combat — Stamina" )] public float PrimaryAttackStaminaPerHoldSecond { get; set; } = 10f;

	[Property, Group( "Combat — Stamina" )] public float PrimaryAttackStaminaMaxCost { get; set; } = 45f;

	/// <summary>Seconds of <see cref="Time.NowDouble"/> after primary **release** before hit + drag are sent to the host (post-release drag is summed until then).</summary>
	[Property, Group( "Combat — Melee" )] public float SwingDamageWindowSeconds { get; set; } = 1f;

	/// <summary>Post-release drag (pixels) on the good or bad axis needed before damage uses the good or bad tier (otherwise neutral).</summary>
	[Property, Group( "Combat — Melee" )] public float SwingDragGoodPixels { get; set; } = 48f;

	[Property, Group( "Combat — Melee" )] public float SwingDragDamageNeutralMul { get; set; } = 1f;

	[Property, Group( "Combat — Melee" )] public float SwingDragDamageGoodMul { get; set; } = 1.15f;

	[Property, Group( "Combat — Melee" )] public float SwingDragDamageBadMul { get; set; } = 0.75f;

	/// <summary>Clamp client-reported post-swing drag vector length (anti-cheat / overflow).</summary>
	[Property, Group( "Combat — Melee" )] public float SwingMaxPostDragSanityPixels { get; set; } = 2800f;

	/// <summary>Deadzone floor blended with <see cref="SwingLiveMicroMotionPixels"/> for live swing (keeps older prefab tuning meaningful).</summary>
	[Property, Group( "Combat — Swing from look" )] public float SwingAxisDeadzone { get; set; } = 0.02f;

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

	/// <summary>
	/// Per-hold sum of raw <see cref="Input.MouseDelta"/> (screen pixels) while attack / block buttons are down.
	/// Pure mouse delta — independent of pawn movement, strafing, or camera world rotation, so L/R/U
	/// classification reflects *only* what the player flicked with the mouse during the hold.
	/// </summary>
	Vector2 _primaryLookAccum;
	Vector2 _blockLookAccum;

	/// <summary>Live L/R/U from <see cref="Input.MouseDelta"/> every frame (no attack hold required); also used on primary release.</summary>
	byte _primaryLiveSwingDir = SwingDirs.Up;
	/// <summary>Per-frame swing cardinal while block is held.</summary>
	byte _blockLiveSwingDir = SwingDirs.Up;

	/// <summary>Time-decayed signed pixel evidence (screen-space; y positive = mouse moved down) — framerate-independent input to the classifier.</summary>
	Vector2 _primarySwingEvidence;
	Vector2 _blockSwingEvidence;
	/// <summary><see cref="RealTime.GlobalNow"/> of the last committed cardinal flip; gated by <see cref="SwingMinFlipHoldMs"/>.</summary>
	double _primaryLastFlipRealSeconds;
	double _blockLastFlipRealSeconds;

	byte _stickySwingDir = SwingDirs.Up;
	byte _lastAttackSwingDir = SwingDirs.Up;
	Vector2 _blockReleaseSwingXz;
	float _blockReleaseSwingVerticalHint;
	byte _blockReleaseSwingDir;

	byte _lockedPrimaryAttackSwingDir;
	bool _hasLockedPrimaryAttackDir;
	bool _primarySwingPhaseActive;
	/// <summary>End of post-release swing window, in <see cref="Time.NowDouble"/> (same clock as combat snapshots).</summary>
	double _primarySwingPhaseEndAtSandbox;
	Vector2 _primaryPostReleaseDragAccum;
	AttackReleaseIntent _pendingPrimarySwingIntent;
	bool _hadPrimaryAttackDownLastFrame;

	bool IsLocalCombatDriver()
	{
		if ( GameObject.IsProxy )
			return false;

		if ( GameObject.Network is { Active: true } n && !n.IsOwner )
			return false;

		return true;
	}

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid() )
			return;

		MaybeWarnCombatAuthorityMisconfigured();

		if ( !IsLocalCombatDriver() )
			return;

		_combatNetDiag = $"netActive={GameObject.Network is { Active: true }} isHost={Networking.IsHost}";

		// Finish any expired swing window before processing this frame's input (uses sandbox time, not wall clock).
		MaybeCompletePrimarySwingPhase();

		// Only reset the per-hold mouse accumulator on a fresh press; the live swing evidence is *continuous* across
		// presses (matches "_primaryLiveSwingDir is always tracked") so quick taps still reflect recent motion.
		if ( Input.Pressed( PrimaryAttackAction ) && !_primary.Down )
			_primaryLookAccum = default;

		if ( Input.Pressed( BlockAction ) && !_block.Down )
			_blockLookAccum = default;

		_primary.Step( PrimaryAttackAction, CanStartPrimaryAttack, CanContinuePrimaryAttack, GetViewDirectionForIntent, GetCameraPositionForIntent, GetPrimaryAttackRules(), OnOwnerValidPrimaryAttackRelease );
		_block.Step( BlockAction, CanStartBlock, CanContinueBlock, GetViewDirectionForIntent, GetCameraPositionForIntent, GetBlockRules(), OnOwnerValidBlockRelease );

		// First frame primary is held: lock swing dir (stamina is debited on host release from hold duration, not on press).
		if ( _primary.Down && !_hadPrimaryAttackDownLastFrame )
		{
			if ( _primarySwingPhaseActive )
				CancelPrimarySwingPhase();
			LockPreparedPrimaryAttackDirection();
		}

		_hadPrimaryAttackDownLastFrame = _primary.Down;

		TickSwingLookAccumulatorsAfterCombatStep();

		if ( ShowCombatInputDebug )
			DrawCombatInputDebug();

		if ( ShowSwingDirectionCrosshair )
			DrawTeardropCrosshairOverlay();
	}

	void TickSwingLookAccumulatorsAfterCombatStep()
	{
		var dt = MathF.Max( 0f, Time.Delta );
		var decay = SwingEvidenceDecaySeconds > 1e-4f ? MathF.Exp( -dt / SwingEvidenceDecaySeconds ) : 0f;
		var rawFrame = Input.MouseDelta;
		var frame = FilterSwingMouseEvidenceDelta( rawFrame );

		// Primary: direction is locked on press and frozen during hold; post-release drag only during swing phase.
		if ( !_primary.Down && !_primarySwingPhaseActive )
		{
			_primarySwingEvidence = _primarySwingEvidence * decay + frame;
			ApplyLiveSwingFromEvidence( _primarySwingEvidence, ref _primaryLiveSwingDir, ref _primaryLastFlipRealSeconds );
		}
		else if ( _hasLockedPrimaryAttackDir )
			_primaryLiveSwingDir = _lockedPrimaryAttackSwingDir;

		if ( _primarySwingPhaseActive && Time.NowDouble < _primarySwingPhaseEndAtSandbox )
			_primaryPostReleaseDragAccum += rawFrame;

		// Block: unchanged live tracking.
		_blockSwingEvidence = _blockSwingEvidence * decay + frame;
		ApplyLiveSwingFromEvidence( _blockSwingEvidence, ref _blockLiveSwingDir, ref _blockLastFlipRealSeconds );

		if ( Input.Down( PrimaryAttackAction ) )
			_primaryLookAccum += rawFrame;

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

	void LockPreparedPrimaryAttackDirection()
	{
		var dt = MathF.Max( 0f, Time.Delta );
		var decay = SwingEvidenceDecaySeconds > 1e-4f ? MathF.Exp( -dt / SwingEvidenceDecaySeconds ) : 0f;
		var e = _primarySwingEvidence * decay + FilterSwingMouseEvidenceDelta( Input.MouseDelta );
		_primarySwingEvidence = e;
		_lockedPrimaryAttackSwingDir = ClassifyLiveSwingFrame( e, _primaryLiveSwingDir );
		_hasLockedPrimaryAttackDir = true;
		_lastAttackSwingDir = _lockedPrimaryAttackSwingDir;
		_stickySwingDir = _lockedPrimaryAttackSwingDir;
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

		var sent = _pendingPrimarySwingIntent with
		{
			PostSwingDragScreenX = drag.x,
			PostSwingDragScreenY = drag.y
		};

		_primaryPostReleaseDragAccum = default;

		RunLocalMeleeTraceForVfxOnly( sent );
		LogCombatDiag( "CLIENT / OWNER",
			$"{CombatClientLogVersion} — Submit swing end seq={sent.IntentSequence} drag=({sent.PostSwingDragScreenX:F1},{sent.PostSwingDragScreenY:F1}) {CombatAuthority.FormatSwingLog( new Vector2( sent.SwingFromX, sent.SwingFromY ), sent.SwingVerticalHint, sent.SwingDir )}" );
		if ( LogAttackStaminaDebug )
		{
			var hold = Math.Max( 0f, (float)( sent.ReleasedGlobalSeconds - sent.PressedGlobalSeconds ) );
			var predicted = GetPrimaryAttackStaminaCostForHoldDuration( hold );
			Log.Info( $"[PlayerCombat/Stamina] predict hold={hold:0.###}s cost={predicted:0.#} (base={PrimaryAttackStaminaBase:0.#} +/s={PrimaryAttackStaminaPerHoldSecond:0.#}, max={PrimaryAttackStaminaMaxCost:0.#})" );
		}

		DispatchPrimaryAttackReleaseToAuthority( sent );
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

	protected virtual bool CanStartPrimaryAttack() => CanAffordPrimaryAttackOnPress();

	protected virtual bool CanContinuePrimaryAttack() => true;

	protected virtual bool CanStartBlock() => true;
	protected virtual bool CanContinueBlock() => true;

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

	void CardinalVectors( byte c, out Vector2 xz, out float v )
	{
		switch ( c )
		{
			case SwingDirs.Up:
				xz = DefaultSwingForwardWorldXz();
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
				xz = DefaultSwingForwardWorldXz();
				v = 1f;
				return;
		}
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
			if ( dx < -min )
				return SwingDirs.Left;
			if ( dx > min )
				return SwingDirs.Right;
			if ( yUp > min )
				return SwingDirs.Up;
			if ( yUp < -min )
				return dx < 0f ? SwingDirs.Left : SwingDirs.Right;
			return current;
		}

		if ( yUp > min )
			return SwingDirs.Up;
		if ( yUp < -min )
		{
			if ( dx < -min )
				return SwingDirs.Left;
			if ( dx > min )
				return SwingDirs.Right;
			return dx < 0f ? SwingDirs.Left : SwingDirs.Right;
		}

		if ( dx < -min )
			return SwingDirs.Left;
		if ( dx > min )
			return SwingDirs.Right;
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
	void ApplyLiveSwingFromEvidence( Vector2 evidence, ref byte currentDir, ref double lastFlipRealSeconds )
	{
		var desire = ClassifyLiveSwingFrame( evidence, currentDir );
		if ( desire == currentDir )
			return;

		var holdSeconds = MathF.Max( 0f, SwingMinFlipHoldMs ) * 0.001f;
		if ( IsOpposingLateralSwing( currentDir, desire ) )
			holdSeconds *= 1.5f;

		var now = RealTime.GlobalNow;
		if ( now - lastFlipRealSeconds < holdSeconds )
			return;

		var contrib = ContributionTowardCardinal( desire, evidence );
		if ( contrib < SwingFlipCommitThreshold( currentDir, desire ) )
			return;

		currentDir = desire;
		lastFlipRealSeconds = now;
	}

	void DrawTeardropCrosshairOverlay()
	{
		var cam = ResolveIntentCamera();
		if ( !cam.IsValid() )
			return;

		// True screen-space for this camera's viewport: avoids world-space DebugOverlay drift vs the reticle.
		var rect = cam.ScreenRect;
		var center = new Vector2( rect.Left + rect.Width * 0.5f, rect.Top + rect.Height * 0.5f );

		// Same live L/R/U + hysteresis path as attack; preview block swing while blocking, else attack.
		var swingPreview = Input.Down( BlockAction )
			? _blockLiveSwingDir
			: _primarySwingPhaseActive || _primary.Down
				? _lockedPrimaryAttackSwingDir
				: _primaryLiveSwingDir;
		var dir = SwingCardinalToScreenTeardropDir( swingPreview );
		var dLen = dir.Length;
		if ( dLen > 1e-5f )
			dir /= dLen;

		// ~60% of prior wireframe (18 / 20 / 10 px).
		const float r = 11f;
		const float tip = 12f;
		const float triHalf = 6f;
		const int fanSegments = 48;
		const float fanLineWidth = 2f;
		var col = Color.White.WithAlpha( 0.95f );

		var baseMid = center + dir * ( r * 0.55f );
		var perp = new Vector2( -dir.y, dir.x );
		var tipPos = center + dir * ( r + tip );
		var pLeft = baseMid + perp * triHalf;
		var pRight = baseMid - perp * triHalf;

		var hud = cam.Overlay;

		for ( var i = 0; i <= fanSegments; i++ )
		{
			var t = i / (float)fanSegments;
			var edge = Vector2.Lerp( pLeft, pRight, t );
			hud.DrawLine( tipPos, edge, fanLineWidth, col );
		}

		// Filled disc at viewport center (size = diameter in screen pixels).
		hud.DrawCircle( center, new Vector2( r * 2f, r * 2f ), col );
	}

	/// <summary>Unit offsets in screen space (+x right, +y down) for L / R / U — same cardinals as <see cref="ClassifyLiveSwingFrame"/>.</summary>
	static Vector2 SwingCardinalToScreenTeardropDir( byte cardinal )
	{
		if ( cardinal == SwingDirs.Left )
			return new Vector2( -1f, 0f );
		if ( cardinal == SwingDirs.Right )
			return new Vector2( 1f, 0f );
		return new Vector2( 0f, -1f );
	}

	Vector2 DefaultSwingForwardWorldXz()
	{
		var f = WorldRotation.Forward;
		var xz = new Vector2( f.x, f.z );
		if ( xz.LengthSquared < 1e-8f )
			return new Vector2( 0f, 1f );
		return xz.Normal;
	}

	Rotation GetCameraYawRotation()
	{
		var cam = ResolveIntentCamera();
		var yaw = cam.IsValid() ? cam.WorldRotation.Angles().yaw : WorldRotation.Angles().yaw;
		return new Angles( 0f, yaw, 0f ).ToRotation();
	}

	CombatChannelRules GetPrimaryAttackRules() => new CombatChannelRules { CooldownAfterValidReleaseSeconds = AttackCooldownAfterRelease };
	CombatChannelRules GetBlockRules() => new CombatChannelRules { CooldownAfterValidReleaseSeconds = BlockCooldownAfterRelease };

	float ComputePrimaryAttackStaminaCost( float holdSeconds )
	{
		var h = MathF.Max( 0f, holdSeconds );
		return MathF.Min( PrimaryAttackStaminaMaxCost, PrimaryAttackStaminaBase + PrimaryAttackStaminaPerHoldSecond * h );
	}

	/// <summary>Same formula as the owner-side swing cost; used by <see cref="CombatAuthority"/> on the host.</summary>
	public float GetPrimaryAttackStaminaCostForHoldDuration( float holdSeconds ) =>
		ComputePrimaryAttackStaminaCost( holdSeconds );

	/// <summary>Press gate: must afford the worst-case swing stamina so we never start a charge we cannot pay for.</summary>
	bool CanAffordPrimaryAttackOnPress()
	{
		var maxCost = PrimaryAttackStaminaMaxCost;
		if ( maxCost <= 0f && PrimaryAttackStaminaBase <= 0f && PrimaryAttackStaminaPerHoldSecond <= 0f )
			return true;

		var vitals = Components.Get<PlayerVitals>();
		if ( vitals is null )
			return false;

		return vitals.CanAffordStamina( maxCost );
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

		if ( _primarySwingPhaseActive )
		{
			LogCombatDiag( "CLIENT / OWNER", "Ignored release — swing damage window still active." );
			return;
		}

		if ( snapshot.ViewDirectionOnPress is not { } vf
		     || snapshot.ViewDirectionOnRelease is not { } vr
		     || snapshot.CameraPositionOnPress is not { } cp
		     || snapshot.CameraPositionOnRelease is not { } cr
		     || snapshot.PressedGlobalSeconds is not { } pg
		     || snapshot.ReleasedGlobalSeconds is not { } rg )
		{
			_combatNetDiag = "snapshot incomplete — intent not sent";
			LogCombatDiag( "CLIENT / OWNER", "Release snapshot missing view/camera/times — intent not sent." );
			return;
		}

		_attackIntentSequence++;
		var camPress = cp;
		var camRel = cr;
		var c = _hasLockedPrimaryAttackDir ? _lockedPrimaryAttackSwingDir : _primaryLiveSwingDir;
		CardinalVectors( c, out var swingXz, out var swingV );
		_lastAttackSwingDir = c;
		_stickySwingDir = c;

		var prepay = GetPrimaryAttackPressStaminaPrepayAmount();

		var intent = new AttackReleaseIntent
		{
			PressedGlobalSeconds = pg,
			ReleasedGlobalSeconds = rg,
			ClientCameraPressX = camPress.x,
			ClientCameraPressY = camPress.y,
			ClientCameraPressZ = camPress.z,
			ClientCameraReleaseX = camRel.x,
			ClientCameraReleaseY = camRel.y,
			ClientCameraReleaseZ = camRel.z,
			ViewForwardOnPress = vf,
			ViewForwardOnRelease = vr,
			ClientPlayerWorldPosition = WorldPosition,
			ClientPlayerWorldRotation = WorldRotation,
			IntentSequence = _attackIntentSequence,
			SwingFromX = swingXz.x,
			SwingFromY = swingXz.y,
			SwingVerticalHint = swingV,
			SwingDir = c,
			StaminaPrepaidMax = prepay,
			PostSwingDragScreenX = 0f,
			PostSwingDragScreenY = 0f
		};

		_pendingPrimarySwingIntent = intent;
		_primaryPostReleaseDragAccum = default;
		var w = SwingDamageWindowSeconds;
		if ( !float.IsFinite( w ) || w < 0f )
			w = 1f;
		var window = Math.Max( 0.04, (double)w );
		_primarySwingPhaseEndAtSandbox = Time.NowDouble + window;
		_primarySwingPhaseActive = true;

		_combatNetDiag = $"swing window {window:0.###}s (drag→dmg)";
		LogCombatDiag( "CLIENT / OWNER",
			$"{CombatClientLogVersion} — Begin swing seq={intent.IntentSequence} locked={SwingDirs.Letter( c )} held={snapshot.HoldDurationSeconds:0.###}s — damage after window" );
	}

	void OnOwnerValidBlockRelease( CombatButtonIntentSnapshot snapshot )
	{
		var c = _blockLiveSwingDir;
		CardinalVectors( c, out var bxz, out var bv );
		_blockReleaseSwingXz = bxz;
		_blockReleaseSwingVerticalHint = bv;
		_blockReleaseSwingDir = c;
		_stickySwingDir = c;
	}

	void DispatchPrimaryAttackReleaseToAuthority( AttackReleaseIntent intent )
	{
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
			return;
		}

		_combatNetDiag = Networking.IsHost ? "host→Rpc.Host (single server pass)" : "RPC->host sent (await result)";
		LogCombatDiag( "CLIENT (dispatch)", "RpcSubmitPrimaryAttackRelease -> host (see editor Output)" );
		RpcSubmitPrimaryAttackRelease( intent );
	}

	void RunLocalMeleeTraceForVfxOnly( AttackReleaseIntent intent )
	{
		if ( !GameObject.IsValid() )
		{
			LastLocalPredictedHitForVfx = Guid.Empty;
			return;
		}

		var dir = intent.ViewForwardOnPress;
		if ( dir.LengthSquared < 0.0001f )
		{
			LastLocalPredictedHitForVfx = Guid.Empty;
			return;
		}

		dir = dir.Normal;
		var origin = intent.ClientPlayerWorldPosition + Vector3.Up * ServerEyeHeight;
		var tr = CombatAuthority.RunAuthorityMeleeTrace( origin, dir, GameObject );
		LastLocalPredictedHitForVfx = tr.Hit && tr.GameObject.IsValid() ? tr.GameObject.Id : Guid.Empty;
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

		var head = WorldPosition + Vector3.Up * 80f;
		DebugOverlay.Text( head, $"{PrimaryAttackAction}:{(_primary.Down ? "DOWN" : "up")} {PrimaryAttack.HoldDurationSeconds:0.00}s\n{BlockAction}:{(_block.Down ? "DOWN" : "up")} {Block.HoldDurationSeconds:0.00}s", size: 12f, duration: 0f );
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
