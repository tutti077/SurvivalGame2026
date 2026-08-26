using System;

namespace Survival;

/// <summary>
/// Attach next to <see cref="PlayerVitals"/> on the pawn root. Handles <see cref="PlayerController.IEvents"/> (jump input / jump–land), sprint stamina,
/// grapple (aim/attach/rope/swing — <c>PlayerMovement.Grapple.cs</c>), and wingsuit (<c>PlayerMovement.Wingsuit.cs</c>).
/// </summary>
[Title( "Player Movement" )]
public sealed partial class PlayerMovement : Component, PlayerController.IEvents, Component.ICollisionListener
{
	[Property, Group( "Stamina - Jump" )] public float JumpStaminaCost { get; set; } = 5f;

	/// <summary>
	/// Jump-height fraction used when stamina is too low to afford <see cref="JumpStaminaCost"/>. 1 = full jump, 0 = block jump.
	/// </summary>
	[Property, Group( "Stamina - Jump" )] public float ExhaustedJumpHeightFraction { get; set; } = 0.333f;

	/// <summary>Input cleared in <see cref="PreInput"/> when <see cref="JumpStaminaCost"/> is positive and stamina cannot pay the full cost.</summary>
	[Property, Group( "Stamina - Jump" )] public string JumpInputAction { get; set; } = "jump";

	/// <summary>
	/// Walkable slope margin (°). 45° roofs need a few degrees headroom or
	/// <see cref="Sandbox.Movement.MoveModeWalk"/> rejects the surface (can't walk up).
	/// </summary>
	[Property, Group( "Movement — Ground" ), Title( "Walkable ground angle (°)" ), Range( 40f, 70f ), Step( 1f )]
	public float WalkableGroundAngleDegrees { get; set; } = 50f;

	/// <summary>Usually matches <see cref="PlayerController.AltMoveButton"/> when <c>RunByDefault</c> is off.</summary>
	[Property, Group( "Stamina - Sprint" )] public string SprintInputAction { get; set; } = "run";

	[Property, Group( "Stamina - Sprint" )] public float SprintStaminaPerSecond { get; set; } = 2f;

	/// <summary>
	/// Stamina at or below this counts as "exhausted" (sprint blocked). Keep this above tiny per-frame regen to avoid flicker around zero.
	/// </summary>
	[Property, Group( "Stamina - Sprint" )] public float ExhaustedStaminaEpsilon { get; set; } = 0.25f;

	/// <summary>Hold to sneak (quieter footsteps, slower approach). Default matches s&amp;box Duck.</summary>
	[Property, Group( "Stamina - Sneak" )] public string SneakInputAction { get; set; } = "Duck";

	[Property, Group( "Stamina - Sneak" )] public float SneakStaminaPerSecond { get; set; } = 1.25f;

	[Property, Group( "Debug" ), Title( "Log noise/actions (10ms)" )]
	public bool LogEntityNoiseDebug { get; set; } = false;

	/// <summary>
	/// Optional per-player stamina regen delay override in seconds. Use a value >= 0 to override
	/// <see cref="VitalsAuthority.StaminaRegenDelaySeconds"/> for this pawn; negative values use authority default.
	/// </summary>
	[Property, Group( "Stamina - Regen" )] public float StaminaRegenDelayOverrideSeconds { get; set; } = -1f;

	[Property, Group( "Camera" ), Title( "Scroll wheel zoom" ), Description( "Off by default — scroll selects hotbar. Use +/- for zoom." )]
	public bool CameraScrollZoomEnabled { get; set; } = false;

	[Property, Group( "Camera" ), Title( "Keyboard zoom (+/-)" ), Description( "Equals/+ zooms in, Minus zooms out. Numpad +/- also work." )]
	public bool CameraKeyboardZoomEnabled { get; set; } = true;

	[Property, Group( "Camera" ), Title( "Zoom min distance" ), Range( 32f, 512f ), Step( 8f )]
	public float CameraZoomMinDistance { get; set; } = 96f;

	[Property, Group( "Camera" ), Title( "Zoom max distance" ), Range( 64f, 1600f ), Step( 8f )]
	public float CameraZoomMaxDistance { get; set; } = 800f;

	[Property, Group( "Camera" ), Title( "Zoom step" ), Range( 8f, 128f ), Step( 8f ), Description( "Distance change per wheel notch." )]
	public float CameraZoomStep { get; set; } = 48f;

	[Property, Group( "Camera" ), Title( "Far clip (m)" ), Description( "How far the view camera draws (1 unit = 1 m)." ), Range( 500f, 100000f ), Step( 500f )]
	public float CameraFarClipMeters { get; set; } = 50000f;

	PlayerVitals _vitals;
	PlayerController _controller;
	/// <summary>Authoritative stepped zoom distance (-1 until first poll).</summary>
	float _cameraZoomDistance = -1f;
	bool _sprintWasDown;
	float _sprintDebtPending;
	double _nextRunNoiseAt;
	double _nextFootstepNoiseAt;
	bool _sneakWasDown;
	float _sneakDebtPending;
	bool _sneakHeldReportedOnHost;
	bool _sneakHeldReportedToHostLast;
	bool _grappleWasAttached;
	float _grapplePrevRopeLength;
	/// <summary>Engine units of rope taken in per second this step. Gates the E swing assist.</summary>
	float _grappleRopeShrinkRate;
	/// <summary>Rope is at max length this step. Slack = inside the sphere; WASD still works.</summary>
	bool _grappleRopeTaut;
	bool _grappleFirstCatchDone;
	/// <summary>Full-body wall overlap this step — velocity zero until clear.</summary>
	bool _grappleBlockedByWall;

	/// <summary>
	/// While sprint is blocked, <see cref="PlayerController.RunSpeed"/> is forced down.
	/// Wingsuit mutes walk+run to 0 so MoveModeWalk cannot invent foot propulsion.
	/// Grapple air does <b>not</b> mute to 0 — that air-accelerated toward a zero wish and braked the swing.
	/// </summary>
	bool _runSpeedOverrideActive;
	float _savedRunSpeed;
	bool _walkSpeedMuteActive;
	float _savedWalkSpeed;
	bool _meleeLocomotionSlowActive;
	float _savedWalkForMelee;
	float _savedRunForMelee;
	float _grappleSteerX;
	float _grappleSteerY;
	bool _grappleSteerSampled;

	/// <summary>Prefab walk/run (not temporary melee/grapple overrides) for overspeed clamps.</summary>
	float _designWalkSpeed = 110f;
	float _designRunSpeed = 320f;

	/// <summary>
	/// Physics can redirect impact into horizontal for a few steps after <see cref="OnLanded"/> —
	/// keep scrubbing so roof downhill jumps don't launch.
	/// </summary>
	int _sanitizeLandFrames;

	/// <summary>Host copy of the owning client’s sprint button, for <see cref="ShouldBlockStaminaRegenForAuthority"/> (local driver uses <see cref="Sandbox.Input"/> directly).</summary>
	bool _sprintHeldReportedOnHost;

	bool _sprintHeldReportedToHostLast;

	/// <summary>Host-synced: countdown freeze for time trials (no move / jump / grapple / wingsuit).</summary>
	[Sync( SyncFlags.FromHost )]
	public bool TimeTrialInputLocked { get; private set; }

	public void HostSetTimeTrialFrozen( bool frozen )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		TimeTrialInputLocked = frozen;
		ApplyTimeTrialFreezeLocal( frozen );
		if ( GameObject.Network is { Active: true } )
			RpcOwnerApplyTimeTrialFreeze( frozen );
	}

	[Rpc.Owner]
	void RpcOwnerApplyTimeTrialFreeze( bool frozen )
	{
		ApplyTimeTrialFreezeLocal( frozen );
	}

	void ApplyTimeTrialFreezeLocal( bool frozen )
	{
		if ( !frozen )
			return;

		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		if ( body is not null && body.IsValid() )
			body.Velocity = Vector3.Zero;

		if ( GrappleAttached )
			DetachGrappleForHitReaction();
	}

	/// <summary>Host places a racer at the countdown pads; owning clients apply via <see cref="RpcOwnerApplyTimeTrialSpawn"/>.</summary>
	public void HostApplyTimeTrialSpawn( Vector3 worldPos, Rotation worldRot )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		ApplyTimeTrialSpawnLocal( worldPos, worldRot );
		if ( GameObject.Network is { Active: true } )
			RpcOwnerApplyTimeTrialSpawn( worldPos, worldRot );
	}

	[Rpc.Owner]
	void RpcOwnerApplyTimeTrialSpawn( Vector3 worldPos, Rotation worldRot )
	{
		ApplyTimeTrialSpawnLocal( worldPos, worldRot );
	}

	void ApplyTimeTrialSpawnLocal( Vector3 worldPos, Rotation worldRot )
	{
		GameObject.WorldPosition = worldPos;
		GameObject.WorldRotation = worldRot;

		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		if ( body is not null && body.IsValid() )
			body.Velocity = Vector3.Zero;

		if ( _controller is not null )
			_controller.EyeAngles = worldRot.Angles();
	}

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		_controller = Components.Get<PlayerController>();
		CacheDesignLocomotionSpeeds();
		ApplyWalkableGroundAngle();
		InitializeGrapple();
		if ( _vitals is null )
			Log.Warning( $"[PlayerMovement|{PlayerVitals.GetVitalsProcessRoleTag( GameObject )}] {GameObject.Name}: add PlayerVitals on this pawn — movement stamina hooks disabled." );
	}

	void CacheDesignLocomotionSpeeds()
	{
		if ( _controller is null || !_controller.IsValid() )
			return;

		if ( _controller.WalkSpeed > 1f )
			_designWalkSpeed = _controller.WalkSpeed;
		if ( _controller.RunSpeed > 1f )
			_designRunSpeed = _controller.RunSpeed;
	}

	float DesignGroundMaxSpeed()
	{
		var max = Math.Max( _designWalkSpeed, _designRunSpeed );
		return max > 1f ? max : 320f;
	}

	void ApplyWalkableGroundAngle()
	{
		var walk = Components.Get<Sandbox.Movement.MoveModeWalk>();
		if ( walk is null || !walk.IsValid() )
			return;

		walk.GroundAngle = Math.Clamp( WalkableGroundAngleDegrees, 1f, 89f );
	}

	bool IsLocalMovementDriver()
	{
		if ( GameObject.IsProxy )
			return false;

		if ( GameObject.Network is not { Active: true } n )
			return true;

		if ( n.Owner is null )
			return Networking.IsHost;

		return n.IsOwner;
	}

	/// <summary>Pulls accumulated sprint/sneak preview debt and clears it. Merged into negative stamina on <see cref="PlayerVitals.RequestVitalsDelta"/> / <see cref="VitalsAuthority.TryApplyDeltas"/>.</summary>
	public float TakePendingSprintStaminaDebt()
	{
		var d = _sprintDebtPending + _sneakDebtPending;
		_sprintDebtPending = 0f;
		_sneakDebtPending = 0f;
		return d;
	}

	/// <summary>Unsynced sprint/sneak preview total (authority pool estimate ≈ <see cref="PlayerVitals.CurrentStamina"/> + this).</summary>
	public float PeekPendingSprintStaminaDebt() => _sprintDebtPending + _sneakDebtPending;

	/// <summary>Stamina regen on the host must not run while this pawn is sprinting or sneaking here — authority stamina can lag behind preview until flush / merged spends.</summary>
	public bool ShouldBlockStaminaRegenForAuthority()
	{
		if ( _vitals is null )
			return false;

		if ( !string.IsNullOrWhiteSpace( SprintInputAction ) && SprintStaminaPerSecond > 0f )
		{
			if ( IsLocalMovementDriver() ? WantsSprintStaminaSpend() : _sprintHeldReportedOnHost )
				return true;
		}

		if ( !string.IsNullOrWhiteSpace( SneakInputAction ) && SneakStaminaPerSecond > 0f )
		{
			if ( IsLocalMovementDriver() ? WantsSneakStaminaSpend() : _sneakHeldReportedOnHost )
				return true;
		}

		return false;
	}

	/// <summary>True while Duck/sneak is held and the pawn is trying to move (local or host-reported).</summary>
	public bool IsSneakingForNoise()
	{
		if ( IsLocalMovementDriver() )
			return _sneakWasDown;
		return _sneakHeldReportedOnHost;
	}

	/// <summary>True while sprint is held (local or host-reported). Used by entity ambient alert bands.</summary>
	public bool IsSprintingForNoise()
	{
		if ( IsLocalMovementDriver() )
			return _sprintWasDown;
		return _sprintHeldReportedOnHost;
	}

	/// <summary>True while moving without sprint (walk intent). Sneak does not count.</summary>
	public bool IsWalkingForNoise()
	{
		if ( IsSprintingForNoise() || IsSneakingForNoise() )
			return false;

		if ( IsLocalMovementDriver() )
			return HasMovementSprintIntent();

		return IsMovingEnoughForFootsteps();
	}

	/// <summary>Owner-side attach impulse — called when local <see cref="GrappleAttached"/> rises.</summary>
	public void ApplyGrappleAttachVelocityScale( float scale )
	{
		if ( !IsLocalMovementDriver() )
			return;

		var body = Components.Get<Rigidbody>();
		if ( body is null )
			return;

		scale = Math.Clamp( scale, 0.5f, 2f );
		if ( MathF.Abs( scale - 1f ) < 1e-4f )
			return;

		body.Velocity *= scale;
	}

	/// <summary>Sprint stamina only while grounded, Run held, and a WASD movement key held.</summary>
	bool WantsSprintStaminaSpend()
	{
		if ( _vitals is null || string.IsNullOrWhiteSpace( SprintInputAction ) )
			return false;

		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller is null || !_controller.IsOnGround )
			return false;

		if ( IsMeleeAttackWalkOnlyOnGround() )
			return false;

		if ( !Input.Down( SprintInputAction ) || _vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon ) )
			return false;

		return HasMovementSprintIntent();
	}

	/// <summary>
	/// Clear held Run only for grounded melee walk-lock — never while airborne, so sprint can stay
	/// held through jumps. Airborne wish still won't invent sprint accel (controller air control);
	/// takeoff speed is kept until land.
	/// </summary>
	bool ShouldSuppressSprintInput() => IsMeleeAttackWalkOnlyOnGround();

	bool IsMeleeAttackWalkOnlyOnGround()
	{
		var combat = Components.Get<PlayerCombat>();
		return combat is not null && combat.IsMeleeAttackWalkOnlyActive;
	}

	/// <summary>
	/// True while this pawn is in its "just got hit" window (no jump, no rope). Read from
	/// <see cref="PlayerAnimation"/>, which owns the window.
	/// </summary>
	public bool IsHitReactionActive() =>
		Components.Get<PlayerAnimation>() is { IsHitReactionActive: true };

	/// <summary>Hit reaction start: the rope always drops, on every machine that knows about this pawn.</summary>
	internal void OnHitReactionBegan() => DetachGrappleForHitReaction();

	/// <summary>Soft-clear Run / AltMove during grounded melee walk-lock only.</summary>
	void SuppressBlockedSprintInput()
	{
		if ( !ShouldSuppressSprintInput() )
			return;

		if ( !string.IsNullOrWhiteSpace( SprintInputAction )
		     && (Input.Down( SprintInputAction ) || Input.Pressed( SprintInputAction )) )
			Input.SetAction( SprintInputAction, false );

		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller is null )
			return;

		var alt = _controller.AltMoveButton;
		if ( !string.IsNullOrWhiteSpace( alt )
		     && !string.Equals( alt, SprintInputAction, StringComparison.OrdinalIgnoreCase )
		     && (Input.Down( alt ) || Input.Pressed( alt )) )
			Input.SetAction( alt, false );
	}

	/// <summary>
	/// While dangling mid-air (grapple) or wingsuit, mute walk+run wish so MoveModeWalk air-control
	/// cannot invent foot propulsion. Normal jumps leave Run alone so held sprint survives landing
	/// and takeoff speed is not forced down to walk.
	/// </summary>
	void ApplyBlockedSprintRunSpeedOverride()
	{
		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller is null )
			return;

		// Rope or wingsuit owns your speed up here. Walk/Run stay at 0 so held Shift cannot raise
		// the air-control target and add swing speed — sprint is a walking thing, not a swinging one.
		if ( WingsuitDeployed || (GrappleAttached && !_controller.IsOnGround) )
		{
			if ( !_walkSpeedMuteActive )
			{
				_savedWalkSpeed = _controller.WalkSpeed;
				if ( !_runSpeedOverrideActive )
					_savedRunSpeed = _controller.RunSpeed;
				_walkSpeedMuteActive = true;
				_runSpeedOverrideActive = true;
			}

			_controller.WalkSpeed = 0f;
			_controller.RunSpeed = 0f;
			return;
		}

		// Leaving wingsuit / grapple-air mute — always fully restore walk+run (don't leave Run at 0).
		if ( _walkSpeedMuteActive || (_runSpeedOverrideActive && _savedWalkSpeed > 1f) )
		{
			RestoreBlockedSprintRunSpeed();
			ApplyMeleeAttackLocomotionSlow();
			return;
		}

		if ( ApplyMeleeAttackLocomotionSlow() )
			return;

		RestoreBlockedSprintRunSpeed();
	}

	/// <summary>
	/// Committed melee: scale Walk+Run to <see cref="PlayerCombat.MeleeAttackMoveSpeedScale"/> (default 10%)
	/// and force Run=Walk so sprint cannot bypass. Returns true when the override is active.
	/// </summary>
	bool ApplyMeleeAttackLocomotionSlow()
	{
		if ( _controller is null || !_controller.IsOnGround || !IsMeleeAttackWalkOnlyOnGround() )
		{
			RestoreMeleeAttackLocomotionSlow();
			return false;
		}

		var combat = Components.Get<PlayerCombat>();
		var scale = combat is not null ? Math.Clamp( combat.MeleeAttackMoveSpeedScale, 0.05f, 1f ) : 0.1f;

		if ( !_meleeLocomotionSlowActive )
		{
			_savedWalkForMelee = _controller.WalkSpeed > 1f ? _controller.WalkSpeed : 110f;
			_savedRunForMelee = _controller.RunSpeed > 1f ? _controller.RunSpeed : 320f;
			_meleeLocomotionSlowActive = true;
		}

		var wish = _savedWalkForMelee * scale;
		_controller.WalkSpeed = wish;
		_controller.RunSpeed = wish;
		return true;
	}

	void RestoreMeleeAttackLocomotionSlow()
	{
		if ( !_meleeLocomotionSlowActive )
			return;

		if ( _controller is not null )
		{
			_controller.WalkSpeed = _savedWalkForMelee > 1f ? _savedWalkForMelee : 110f;
			_controller.RunSpeed = _savedRunForMelee > 1f ? _savedRunForMelee : 320f;
		}

		_meleeLocomotionSlowActive = false;
		_savedWalkForMelee = 0f;
		_savedRunForMelee = 0f;
	}

	void RestoreBlockedSprintRunSpeed()
	{
		RestoreMeleeAttackLocomotionSlow();

		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _walkSpeedMuteActive && _controller is not null )
		{
			_controller.WalkSpeed = _savedWalkSpeed > 1f ? _savedWalkSpeed : 110f;
			_walkSpeedMuteActive = false;
		}

		if ( !_runSpeedOverrideActive )
			return;

		if ( _controller is not null )
			_controller.RunSpeed = _savedRunSpeed > 1f ? _savedRunSpeed : 320f;

		_runSpeedOverrideActive = false;
		_savedRunSpeed = 0f;
		_savedWalkSpeed = 0f;
	}

	static bool HasMovementSprintIntent() =>
		Input.Down( "Forward" )
		|| Input.Down( "Backward" )
		|| Input.Down( "Left" )
		|| Input.Down( "Right" );

	bool WantsSneakStaminaSpend()
	{
		if ( _vitals is null || string.IsNullOrWhiteSpace( SneakInputAction ) )
			return false;

		if ( !Input.Down( SneakInputAction ) || _vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon ) )
			return false;

		if ( GrappleAttached )
			return false;

		// Sneaking while sprinting doesn't stack — run wins for noise + stamina.
		if ( WantsSprintStaminaSpend() )
			return false;

		return HasMovementSprintIntent();
	}

	public void PreInput()
	{
		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		if ( TimeTrialInputLocked )
		{
			ClearActionIfPressed( JumpInputAction );
			if ( !string.IsNullOrWhiteSpace( SprintInputAction ) )
				ClearActionIfPressed( SprintInputAction );
			ClearActionIfPressed( SneakInputAction );
			_controller ??= Components.Get<PlayerController>();
			var body = _controller?.Body ?? Components.Get<Rigidbody>();
			if ( body is not null && body.IsValid() )
				body.Velocity = Vector3.Zero;
			return;
		}

		if ( GrappleControlSchemeStore.NeedsChoice && HasGrappleEquipped() )
		{
			ClearActionIfPressed( JumpInputAction );
			ClearActionIfPressed( SneakInputAction );
			if ( !string.IsNullOrWhiteSpace( SprintInputAction ) )
				ClearActionIfPressed( SprintInputAction );
			if ( Input.Pressed( "Attack1" ) || Input.Down( "Attack1" ) )
				Input.SetAction( "Attack1", false );
		}

		// Augment air hop / dash must see Jump before stamina or wingsuit clear it.
		TickAugmentJumpGates();

		// Before PlayerController.Jump: strip downhill -Z so SubtractDirection doesn't
		// convert slope-aligned speed into a horizontal launch (roof walk-off boost).
		PrepareGroundedJumpVelocity();

		if ( JumpStaminaCost > 0f
		     && !_vitals.CanAffordStamina( JumpStaminaCost )
		     && ExhaustedJumpHeightFraction <= 0f )
			PlayerVitals.ClearJumpInputIfPressed( JumpInputAction );

		// Just got hit: no jumping out of the reaction.
		if ( IsHitReactionActive() )
			ClearActionIfPressed( JumpInputAction );

		// Jump while grappled: no mid-air hop off the rope (both schemes).
		if ( GrappleAttached )
		{
			if ( _controller is null )
				_controller = Components.Get<PlayerController>();

			// Pro scheme: Space while hanging near a standable lip is a ledge grab, not a hop
			// (grounded / scheme / target gates live inside).
			TryStartGrappleLedgeGrabFromJumpPress();

			if ( _controller is not null && !_controller.IsOnGround )
				ClearActionIfPressed( JumpInputAction );

			// Training Wheels retract is Space — eat jump so reel isn't a hop.
			if ( GrappleControlSchemeStore.IsTrainingWheels )
				ClearActionIfPressed( JumpInputAction );
		}

		// Mid ledge pull: Space is already spent, no hop on arrival.
		if ( IsGrappleLedgePulling )
			ClearActionIfPressed( JumpInputAction );

		TickWingsuitJumpGate();

		if ( !string.IsNullOrWhiteSpace( SprintInputAction ) )
		{
			if ( ShouldSuppressSprintInput() )
				SuppressBlockedSprintInput();
			else if ( SprintStaminaPerSecond > 0f && _vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon ) )
				ClearActionIfPressed( SprintInputAction );
		}

		SampleGrappleSteerAndHideFromWalk();
		TickGrappleControllerOverride();
		ApplyBlockedSprintRunSpeedOverride();
	}

	public void OnJumped()
	{
		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		// Jump may SubtractDirection along the ground normal after PreInput — scrub boost, keep sprint.
		SanitizeHorizontalToDesignRun( keepUpwardZ: true );

		if ( _vitals.OnControllerJumpedForStaminaFromMovement( JumpStaminaCost, ExhaustedJumpHeightFraction ) )
			ApplyExhaustedJumpVelocityScale();

		OnAugmentJumped();
		TickPendingJumpLegsScale();
	}

	/// <summary>
	/// Before <see cref="PlayerController.Jump"/>: drop downhill -Z so SubtractDirection does not
	/// convert slope-aligned speed into free horizontal launch. XY stays at sprint; design-run
	/// cap happens in <see cref="OnJumped"/> / land sanitize (not walk-speed).
	/// </summary>
	void PrepareGroundedJumpVelocity()
	{
		if ( string.IsNullOrWhiteSpace( JumpInputAction ) || !Input.Pressed( JumpInputAction ) )
			return;

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null || !_controller.IsValid() || !_controller.IsOnGround )
			return;

		var body = _controller.Body ?? Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return;

		var v = body.Velocity;
		body.Velocity = new Vector3( v.x, v.y, 0f );
	}

	/// <summary>
	/// Cap horizontal speed to design walk/run max. Uses prefab speeds so temporary Run=Walk
	/// overrides cannot force a walk-speed scrub. Optional keep-upward for mid-jump.
	/// </summary>
	void SanitizeHorizontalToDesignRun( bool keepUpwardZ )
	{
		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return;

		// Shove dash intentionally exceeds run for a beat — don't eat it.
		var combat = Components.Get<PlayerCombat>();
		if ( combat is not null && combat.IsCombatActionLocked )
			return;

		if ( GrappleAttached || WingsuitDeployed )
			return;

		var groundMax = DesignGroundMaxSpeed();
		var v = body.Velocity;
		var flat = new Vector3( v.x, v.y, 0f );
		var speed = flat.Length;
		if ( speed <= groundMax + 0.5f )
		{
			if ( keepUpwardZ && v.z < 0f )
				body.Velocity = new Vector3( v.x, v.y, 0f );
			return;
		}

		flat *= groundMax / speed;
		var z = keepUpwardZ ? Math.Max( v.z, 0f ) : v.z;
		body.Velocity = new Vector3( flat.x, flat.y, z );
	}

	public void OnLanded( float distance, Vector3 impactVelocity )
	{
		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		if ( WingsuitDeployed )
			StowWingsuitOnGround();
		else if ( _wingsuitFreefallAwaitingLand )
			CompleteWingsuitLand();

		_wingsuitAirborneSeconds = 0f;
		// Slope redirect often lands after this callback — scrub for several physics steps.
		_sanitizeLandFrames = 20;
		SanitizeHorizontalToDesignRun( keepUpwardZ: false );
		_vitals.OnControllerLandedForJumpStaminaFromMovement( distance, impactVelocity );
		OnAugmentLanded();
	}

	/// <summary>
	/// After the built-in third-person camera (and its wall-trace ease), hard-place at our stepped zoom
	/// so zoom-out snaps the same way zoom-in does.
	/// </summary>
	void PlayerController.IEvents.PostCameraSetup( CameraComponent camera )
	{
		if ( !IsLocalMovementDriver() || !camera.IsValid() )
			return;

		camera.ZFar = Math.Max( 100f, CameraFarClipMeters );

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null || !_controller.IsValid() )
			return;

		var combat = Components.Get<PlayerCombat>();

		// First-person: FOV zoom (no CameraOffset pull).
		if ( !_controller.ThirdPerson )
		{
			if ( combat?.IsBowAdsActive == true )
				camera.FieldOfView = Math.Clamp( camera.FieldOfView * 0.82f, 35f, 110f );
			return;
		}

		if ( !CameraScrollZoomEnabled && !CameraKeyboardZoomEnabled )
			return;

		if ( _cameraZoomDistance < 0f )
		{
			var min = MathF.Min( CameraZoomMinDistance, CameraZoomMaxDistance );
			var max = MathF.Max( CameraZoomMinDistance, CameraZoomMaxDistance );
			var step = MathF.Max( 1f, CameraZoomStep );
			var start = Math.Clamp( _controller.CameraOffset.x, min, max );
			var startNotch = (float)Math.Round( (start - min) / step );
			_cameraZoomDistance = Math.Clamp( min + startNotch * step, min, max );
		}

		// Bow ADS: pull the third-person cam closer (FOV writes alone get overwritten every frame).
		float? adsDistance = null;
		if ( combat is not null )
		{
			var mul = combat.GetBowAdsCameraDistanceMultiplier();
			if ( mul < 0.999f )
			{
				var minAds = MathF.Max( 32f, CameraZoomMinDistance * 0.35f );
				adsDistance = Math.Clamp( _cameraZoomDistance * mul, minAds, _cameraZoomDistance );
			}
		}

		SnapThirdPersonCameraToZoomDistance( camera, adsDistance );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( IsLocalMovementDriver() && _vitals is not null )
		{
			UpdateSprintStaminaHoldAndFlushOnRelease();
			UpdateSneakStaminaHoldAndFlushOnRelease();
			DetectGrappleAttachEdge();
		}

		TickRunNoiseForEntities();
		TickFootstepNoiseForEntities();
		TickPlayerActionDebug();
		TickGrappleUpdate();

		if ( !IsLocalMovementDriver() )
			return;

		PollCameraScrollZoom();

		// Melee walk-lock / grapple-air mute (do not clear held sprint while jumping).
		SuppressBlockedSprintInput();
		ApplyBlockedSprintRunSpeedOverride();

		// Length winch runs in FixedUpdate only — applying it here too caused E/Q bobble.
		// Only catch large over-length desync after MoveModeWalk moves us.
		ApplyGrappleOverLengthCatchup();
		TickWingsuitDebugDraw();
	}

	void PollCameraScrollZoom()
	{
		if ( !CameraScrollZoomEnabled && !CameraKeyboardZoomEnabled )
			return;

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null || !_controller.IsValid() || !_controller.ThirdPerson )
			return;

		if ( Components.Get<PlayerGameMenuController>() is { IsMenuOpen: true } )
			return;

		var hammer = Components.Get<PlayerEquipment>()?.GetActiveTool<ToolBuildHammer>();
		var buildOwnsWheel = hammer is not null && hammer.IsPreviewingPlacePiece;

		var min = MathF.Min( CameraZoomMinDistance, CameraZoomMaxDistance );
		var max = MathF.Max( CameraZoomMinDistance, CameraZoomMaxDistance );
		var step = MathF.Max( 1f, CameraZoomStep );

		if ( _cameraZoomDistance < 0f )
		{
			var start = Math.Clamp( _controller.CameraOffset.x, min, max );
			var startNotch = (float)Math.Round( (start - min) / step );
			_cameraZoomDistance = Math.Clamp( min + startNotch * step, min, max );
			ApplyCameraZoomDistance( snapView: false );
		}

		var zoomIn = false;
		var zoomOut = false;

		if ( CameraScrollZoomEnabled && !buildOwnsWheel )
		{
			var scroll = Input.MouseWheel.y;
			if ( scroll > 0.01f )
				zoomIn = true;
			else if ( scroll < -0.01f )
				zoomOut = true;
		}

		if ( CameraKeyboardZoomEnabled && IsCameraZoomKeyPressed( zoomIn: true ) )
			zoomIn = true;
		if ( CameraKeyboardZoomEnabled && IsCameraZoomKeyPressed( zoomIn: false ) )
			zoomOut = true;

		if ( !zoomIn && !zoomOut )
			return;

		// Prefer zoom-in if both somehow fire same frame.
		var notch = (float)Math.Round( (_cameraZoomDistance - min) / step );
		if ( zoomIn )
			notch -= 1f;
		else
			notch += 1f;

		var next = Math.Clamp( min + notch * step, min, max );
		_cameraZoomDistance = next;
		ApplyCameraZoomDistance( snapView: true );
	}

	/// <summary>
	/// Hardcoded physical keys (not the rebindable Run action): = / + zoom in, - zoom out.
	/// Engine key names are Source button codes minus the KEY_ prefix: equal, minus, pad_plus, pad_minus.
	/// Shift+= is fine — the physical key is still "equal".
	/// </summary>
	static bool IsCameraZoomKeyPressed( bool zoomIn )
	{
		if ( zoomIn )
		{
			return Input.Keyboard.Pressed( "equal" )
			       || Input.Keyboard.Pressed( "pad_plus" )
			       || Input.Pressed( "CameraZoomIn" );
		}

		return Input.Keyboard.Pressed( "minus" )
		       || Input.Keyboard.Pressed( "pad_minus" )
		       || Input.Pressed( "CameraZoomOut" );
	}

	void ApplyCameraZoomDistance( bool snapView )
	{
		if ( _controller is null || !_controller.IsValid() || _cameraZoomDistance < 0f )
			return;

		var offset = _controller.CameraOffset;
		if ( MathF.Abs( offset.x - _cameraZoomDistance ) > 0.01f )
			_controller.CameraOffset = new Vector3( _cameraZoomDistance, offset.y, offset.z );

		if ( snapView )
			SnapThirdPersonCameraToZoomDistance( null );
	}

	/// <summary>
	/// Instantly place the view camera at the stepped distance (hard wall clamp, no ease-out).
	/// Uses a sphere sweep so foliage / thick mesh colliders (trees) still pull the camera forward
	/// instead of leaving it behind an opaque occluder.
	/// </summary>
	void SnapThirdPersonCameraToZoomDistance( CameraComponent cam, float? distanceOverride = null )
	{
		cam ??= BuildViewCamera.Resolve( GameObject );
		if ( cam is null || !cam.IsValid() || !Scene.IsValid() || _controller is null )
			return;

		var distance = distanceOverride ?? _cameraZoomDistance;
		if ( distance < 1f )
			distance = _cameraZoomDistance;

		var eye = GameObject.WorldPosition
		          + Vector3.Up * Math.Max( 8f, _controller.BodyHeight - _controller.EyeDistanceFromTop );
		var rot = cam.WorldRotation;
		var offset = _controller.CameraOffset;
		// Prefab CameraOffset is (back, height, side) — x is pull-back distance.
		var target = eye
		             - rot.Forward * distance
		             + Vector3.Up * offset.y
		             + rot.Right * offset.z;

		var radius = Math.Max( 6f, _controller.BodyRadius * 0.45f );
		var tr = Scene.Trace.Sphere( radius, eye, target )
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "player", "trigger" )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
		{
			cam.WorldPosition = target;
			return;
		}

		// Keep a bit of air off the hit surface so we don't sit inside the occluder.
		cam.WorldPosition = tr.HitPosition + tr.Normal * Math.Max( 4f, radius );
	}

	void TickRunNoiseForEntities()
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( Components.Get<EntityBrain>() is not null )
			return;

		var sprinting = IsLocalMovementDriver() ? _sprintWasDown : _sprintHeldReportedOnHost;
		if ( !sprinting )
			return;

		if ( Time.NowDouble < _nextRunNoiseAt )
			return;

		_nextRunNoiseAt = Time.NowDouble + 0.35;
		EntityNoiseBus.Emit( Scene, GameObject.WorldPosition, EntityNoiseKind.Run, GameObject );
	}

	void TickFootstepNoiseForEntities()
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( Components.Get<EntityBrain>() is not null )
			return;

		// Sprint already emits Run at longer range — skip overlapping footsteps.
		var sprinting = IsLocalMovementDriver() ? _sprintWasDown : _sprintHeldReportedOnHost;
		if ( sprinting )
			return;

		// Prefer WASD intent — PlayerController often has near-zero Rigidbody speed while walking.
		var walking = IsLocalMovementDriver()
			? HasMovementSprintIntent()
			: IsMovingEnoughForFootsteps();
		if ( !walking )
			return;

		if ( Time.NowDouble < _nextFootstepNoiseAt )
			return;

		_nextFootstepNoiseAt = Time.NowDouble + 0.4;
		var sneak = IsSneakingForNoise();
		EntityNoiseBus.Emit(
			Scene,
			GameObject.WorldPosition,
			sneak ? EntityNoiseKind.SneakFootstep : EntityNoiseKind.Footstep,
			GameObject );
	}

	void TickPlayerActionDebug()
	{
		EntityPerceptionDebug.Enabled = LogEntityNoiseDebug || EntityPerceptionDebug.Enabled;
		if ( !LogEntityNoiseDebug )
			return;

		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		var speed = body is { IsValid: true } ? body.Velocity.WithZ( 0f ).Length : 0f;
		var speedM = TerrainWorldUnits.EngineToMeters( speed );
		var sprinting = IsLocalMovementDriver() ? _sprintWasDown : _sprintHeldReportedOnHost;
		var sneak = IsSneakingForNoise();
		var moving = IsMovingEnoughForFootsteps();

		var action = "idle";
		if ( sprinting )
			action = "run";
		else if ( sneak && moving )
			action = "sneak";
		else if ( moving )
			action = "walk";

		var nearestEnemyM = -1f;
		if ( Scene.IsValid() )
		{
			var best = float.MaxValue;
			foreach ( var brain in Scene.GetAllComponents<EntityBrain>() )
			{
				if ( brain is null || !brain.IsValid() )
					continue;
				var d = Vector3.DistanceBetween( GameObject.WorldPosition, brain.GameObject.WorldPosition );
				if ( d < best )
					best = d;
			}

			if ( best < float.MaxValue )
				nearestEnemyM = TerrainWorldUnits.EngineToMeters( best );
		}

		EntityPerceptionDebug.LogPlayer(
			$"{GameObject.Name} action={action} speed={speedM:0.00}m/s sneak={sneak} sprint={sprinting} " +
			$"enemyDist={nearestEnemyM:0.00}m" );
	}

	bool IsMovingEnoughForFootsteps()
	{
		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return IsLocalMovementDriver() && HasMovementSprintIntent();

		var flat = body.Velocity.WithZ( 0f );
		// Lower threshold so slow walks still emit footsteps for AI hearing.
		return flat.Length >= TerrainWorldUnits.MetersToEngine( 0.15f );
	}

	void UpdateSneakStaminaHoldAndFlushOnRelease()
	{
		if ( string.IsNullOrWhiteSpace( SneakInputAction ) || SneakStaminaPerSecond <= 0f )
		{
			if ( GameObject.Network is { Active: true } && !Networking.IsHost && _sneakHeldReportedToHostLast )
			{
				_sneakHeldReportedToHostLast = false;
				RpcSneakHeldForRegen( false );
			}

			if ( _sneakWasDown )
				FlushSneakStaminaDebt( "sneak action cleared" );
			_sneakWasDown = false;
			return;
		}

		var sneakAllowed = WantsSneakStaminaSpend();
		var reportHeld = sneakAllowed;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost && reportHeld != _sneakHeldReportedToHostLast )
		{
			_sneakHeldReportedToHostLast = reportHeld;
			RpcSneakHeldForRegen( reportHeld );
		}

		if ( !sneakAllowed )
		{
			if ( _sneakWasDown )
				FlushSneakStaminaDebt( "sneak released" );
			_sneakWasDown = false;
			return;
		}

		var d = SneakStaminaPerSecond * Time.Delta;
		var applied = Math.Min( d, Math.Max( 0f, _vitals.CurrentStamina ) );
		if ( applied > 1e-6f )
		{
			_sneakDebtPending += applied;
			_vitals.ApplyLocalStaminaSprintPreviewSpend( applied );
		}

		_sneakWasDown = true;
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();

		if ( !IsLocalMovementDriver() )
			return;

		// Physics step: melee / grapple mute before MoveModeWalk samples wish speed.
		SuppressBlockedSprintInput();
		ApplyBlockedSprintRunSpeedOverride();

		if ( _sanitizeLandFrames > 0 )
		{
			SanitizeHorizontalToDesignRun( keepUpwardZ: false );
			_sanitizeLandFrames--;
		}

		TickGrappleFixedUpdate();
		ApplyGrappleRopeConstraint( Time.Delta );
		TickGrappleLedgePull( Time.Delta );
		TickWingsuitAirborneTimer( Time.Delta );
		TickWingsuitFlight( Time.Delta );
		TickWingsuitFreefallHandoff();
	}

	void DetectGrappleAttachEdge()
	{
		if ( GrappleAttached && !_grappleWasAttached )
		{
			_grappleReleaseAir = false;
			_grappleRopeTaut = false;
			_grappleFirstCatchDone = false;
			_grappleBlockedByWall = false;
			_grappleRopeShrinkRate = 0f;
			ResetGrappleSwingLog();
			ApplyGrappleAttachVelocityScale( AttachVelocityScale );
			if ( WingsuitDeployed )
				StowWingsuit( keepMomentum: true );
		}

		if ( _grappleWasAttached && !GrappleAttached )
		{
			_grappleReleaseAir = true;
			WakePawnAfterGrappleDetach();
		}

		if ( !GrappleAttached )
		{
			_grapplePrevRopeLength = 0f;
			_grappleRopeShrinkRate = 0f;
			_grappleRopeTaut = false;
			_grappleFirstCatchDone = false;
			_grappleBlockedByWall = false;
			FlushGrappleSwingLog();
		}

		_grappleWasAttached = GrappleAttached;
	}

	/// <summary>
	/// Rope hang mutes walk/run and <see cref="PlayerController.EnablePressing"/>. If we leave those
	/// off after detach, a still pawn floats until WASD wakes the controller.
	/// </summary>
	void WakePawnAfterGrappleDetach()
	{
		UpdatePressingOverride( false );
		TickGrappleControllerOverride();

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is not null && _controller.IsValid() )
			_controller.EnablePressing = true;

		RestoreBlockedSprintRunSpeed();

		var body = ResolveGrappleBody();
		if ( body is null || !body.IsValid() )
			return;

		if ( body.GravityScale < 1f )
			body.GravityScale = 1f;
	}

	void ApplyGrappleOverLengthCatchup()
	{
		if ( !GrappleAttached || GrappleRopeLengthEngine <= 1e-3f )
			return;

		var attach = ResolveGrappleAttachWorldPoint();
		var maxLen = Math.Max( 1f, GrappleRopeLengthEngine );
		var pos = GameObject.WorldPosition;
		var toPlayer = pos - attach;
		var dist = toPlayer.Length;
		if ( dist <= maxLen + 1f || dist < 1e-4f )
			return;

		var radial = toPlayer / dist;
		GameObject.WorldPosition = attach + radial * maxLen;
		Transform.ClearInterpolation();
	}

	void ApplyGrappleRopeConstraint( float dt )
	{
		// Ledge mantle broke the rope this instant — never fight the pull while the detach lands.
		if ( !GrappleAttached || GrappleRopeLengthEngine <= 1e-3f || IsGrappleLedgePulling )
		{
			_grapplePrevRopeLength = 0f;
			_grappleRopeTaut = false;
			return;
		}

		var body = ResolveGrappleBody();
		if ( body is null )
			return;

		dt = Math.Max( 1e-4f, dt );

		var attach = ResolveGrappleAttachWorldPoint();
		var maxLen = Math.Max( 1f, GrappleRopeLengthEngine );
		var prevLen = _grapplePrevRopeLength > 1e-3f ? _grapplePrevRopeLength : maxLen;
		var pos = GameObject.WorldPosition;
		var toPlayer = pos - attach;
		var dist = toPlayer.Length;
		if ( dist < 1e-4f )
		{
			_grapplePrevRopeLength = maxLen;
			return;
		}

		var radial = toPlayer / dist;
		var vel = body.Velocity;
		var vRad = Vector3.Dot( vel, radial );
		var vTan = vel - radial * vRad;
		var wasTaut = _grappleRopeTaut;

		// Length-only winch: E/Q change RopeLengthEngine; we only enforce the hard sphere.
		if ( dist > maxLen )
		{
			GameObject.WorldPosition = attach + radial * maxLen;
			Transform.ClearInterpolation();

			// Strip only outward radial pull. Re-writing full tangent every step was bleeding
			// speed and felt like dragging through mud on a taut rope.
			if ( vRad > 0f )
				body.Velocity = StripOutwardRadial( vel, radial, vRad, maxLen, dt );

			ApplyWinchAngularMomentum( body, radial, prevLen, maxLen );

			if ( !wasTaut )
			{
				var incomingSpeed = vel.Length;
				var tanDir = ResolveCatchTangent( vTan, vel, radial );
				if ( tanDir.LengthSquared > 1e-6f && incomingSpeed > vTan.Length + 1f )
					RecordGrappleRopeCatch( incomingSpeed, vTan.Length );
			}

			_grappleRopeTaut = true;
			_grappleFirstCatchDone = true;

			pos = GameObject.WorldPosition;
			toPlayer = pos - attach;
			dist = toPlayer.Length;
			if ( dist > 1e-4f )
				radial = toPlayer / dist;
		}
		else
		{
			_grappleRopeTaut = dist >= maxLen - Math.Max( 2f, maxLen * 0.02f );

			if ( _grappleRopeTaut && vRad > 0f )
				body.Velocity = StripOutwardRadial( vel, radial, vRad, maxLen, dt );

			if ( _grappleRopeTaut )
				ApplyWinchAngularMomentum( body, radial, prevLen, maxLen );
		}

		_grappleRopeShrinkRate = (prevLen - maxLen) / dt;
		_grapplePrevRopeLength = maxLen;

		TickGrappleBodyBlock();

		ApplyGrappleSwingPushAfterWalk( dt );

		if ( _grappleBlockedByWall )
		{
			var bodyAfterPump = ResolveGrappleBody();
			if ( bodyAfterPump is not null && bodyAfterPump.IsValid() )
				bodyAfterPump.Velocity = Vector3.Zero;
		}

		WriteGrappleSwingLogRow( dist, maxLen, radial, body.Velocity );
	}

	/// <summary>WASD swing thrust after the rope constraint so walk air-control cannot eat it.</summary>
	public void ApplyGrappleSwingPushAfterWalk( float dt )
	{
		if ( !IsLocalMovementDriver() )
			return;

		if ( !GrappleAttached || GrappleRopeLengthEngine <= 1e-3f )
			return;

		var body = ResolveGrappleBody();
		if ( body is null )
			return;

		var attach = ResolveGrappleAttachWorldPoint();
		var toPlayer = GameObject.WorldPosition - attach;
		var dist = toPlayer.Length;
		if ( dist < 1e-4f )
			return;

		ApplyPendulumSwingPush( body, toPlayer / dist, dist, Math.Max( 1e-4f, dt ) );
	}

	void ApplyPendulumSwingPush( Rigidbody body, Vector3 radial, float radius, float dt )
	{
		if ( body.Sleeping )
			body.Sleeping = false;

		radius = Math.Max( 1f, radius );

		var vel = body.Velocity;
		var vRadial = Vector3.Dot( vel, radial );
		var vTan = vel - radial * vRadial;
		var speed = vel.Length;

		float steerX;
		float steerY;
		if ( _grappleSteerSampled )
		{
			steerX = _grappleSteerX;
			steerY = _grappleSteerY;
		}
		else
		{
			ReadMoveAxes( out steerX, out steerY );
		}

		var hasInput = MathF.Abs( steerX ) > 1e-4f || MathF.Abs( steerY ) > 1e-4f;
		var retracting = IsRetractingRope;

		if ( _grappleBlockedByWall )
		{
			if ( hasInput )
				_grappleBlockedByWall = false;
			else
			{
				body.Velocity = Vector3.Zero;
				RecordGrappleSwingPush( "blocked", 0f, 0f, 0f, speed, 0f, 0f );
				return;
			}
		}

		// Air drag runs whether or not you hold a key: it is what makes a held direction finally
		// settle at its offset hang angle instead of oscillating around it forever.
		var damp = Math.Max( 0f, SwingCoastDamping );
		if ( damp > 0f && speed > 1e-4f )
		{
			vel *= Math.Max( 0f, 1f - damp * dt );
			body.Velocity = vel;
			vRadial = Vector3.Dot( vel, radial );
			vTan = vel - radial * vRadial;
			speed = vel.Length;
		}

		if ( !hasInput && !retracting )
		{
			RecordGrappleSwingPush( "coast", 0f, 0f, 0f, speed, body.Velocity.Length, speed );
			return;
		}

		var camRot = GetSwingCameraRotation();
		var gravity = Scene.IsValid() ? Scene.PhysicsWorld.Gravity : Vector3.Down * 800f;
		var gMag = gravity.Length * (body.GravityScale > 0.01f ? body.GravityScale : 1f );
		if ( gMag < 1f )
			gMag = 800f;

		var maxSpeed = Math.Max( 1f, SwingMaxSpeed );
		var speedFrac = Math.Clamp( speed / maxSpeed, 0f, 1f );
		var headroom = Math.Clamp( 1f - speedFrac * speedFrac * speedFrac * speedFrac, 0f, 1f );
		if ( headroom < 1e-4f )
		{
			RecordGrappleSwingPush( "capped", 0f, 0f, 0f, speed, speed, speed );
			return;
		}

		var tanSpeed = vTan.Length;
		var deltaV = Vector3.Zero;
		var along = 0f;

		// Full strength anywhere below the anchor, gone above it. This is the ceiling on flat
		// W..S..W..S (top out at the ledge) — drag used to do it, but a drag plateau weakens every
		// pump on the way up, which is what made the ramp take 7 pumps instead of 5.
		var heightScale = ComputePumpHeightScale( radial, gravity );

		if ( hasInput && heightScale > 1e-4f )
		{
			var wish = camRot.Forward * steerY + camRot.Right * steerX;
			if ( wish.LengthSquared > 1e-6f )
			{
				// Constant force in the held direction — a leaning weight, not a thruster. The rope
				// eats the radial part, so the tangent projection keeps its natural cosine falloff
				// (do not normalise here: that is what let a dead hang jet to 90 degrees).
				var force = ProjectOntoTangentPlane( wish.Normal, radial );
				deltaV += force * (gMag * Math.Clamp( SwingPumpGravityFraction, 0f, 1f ) * headroom * heightScale * dt);

				if ( tanSpeed > 1e-3f && force.LengthSquared > 1e-6f )
					along = Vector3.Dot( force.Normal, vTan / tanSpeed );
			}
		}

		// E is a winch. Its assist only exists while rope is genuinely coming in — reeled all the way
		// to min length, holding E adds nothing, so it can never be a free engine.
		if ( retracting && heightScale > 1e-4f && _grappleRopeShrinkRate > 1f
		     && tanSpeed >= Math.Max( 10f, SwingRetractPumpMinSpeed ) )
		{
			var frac = Math.Clamp( SwingRetractPumpGravityFraction, 0f, 1f );
			if ( frac > 1e-4f )
				deltaV += (vTan / tanSpeed) * (gMag * frac * headroom * heightScale * dt);
		}

		if ( deltaV.LengthSquared < 1e-8f )
		{
			RecordGrappleSwingPush( "stalled", along, 0f, 0f, speed, speed, tanSpeed );
			return;
		}

		var before = speed;
		body.Velocity = vel + deltaV;
		var pumpAccel = deltaV.Length / Math.Max( 1e-4f, dt );
		var phase = along < -0.15f ? "brake" : retracting && !hasInput ? "retract" : "pump";
		RecordGrappleSwingPush( phase, along, headroom, pumpAccel, before, body.Velocity.Length, tanSpeed );
	}

	static Vector3 ProjectOntoTangentPlane( Vector3 wish, Vector3 radial )
		=> wish - radial * Vector3.Dot( wish, radial );

	/// <summary>
	/// Take the outward pull off a taut rope while keeping the speed the rope only <i>rotated</i>.
	/// <para>
	/// A step of <c>tan·dt</c> along a rope of length <c>len</c> swings the rope through
	/// <c>theta = tan·dt/len</c>, and a straight-line integrator turns <c>tan·theta</c> of pure
	/// tangent speed into outward radial velocity. Dropping that instead of rotating it costs
	/// <c>tan³·dt/(2·len²)</c> per second — invisible on a slow swing, but it grows with the cube of
	/// speed, so a fast orbit hits a wall where that fake drag equals the pump and stops gaining
	/// (~2900 u/s on a 30 m rope, ~1400 on a 10 m one) no matter how high <see cref="SwingMaxSpeed"/> is.
	/// </para>
	/// Only a turn-sized outward component is given back, so a genuine outward fling (slack going
	/// taut, pay-out) still loses it and this can never return more than the speed coming in.
	/// </summary>
	static Vector3 StripOutwardRadial( Vector3 vel, Vector3 radial, float vRad, float len, float dt )
	{
		var vTan = vel - radial * vRad;
		var tan = vTan.Length;
		if ( tan < 1e-3f )
			return vTan;

		var theta = tan * dt / Math.Max( 1f, len );
		if ( vRad > tan * theta * 2f )
			return vTan;

		var speed = MathF.Sqrt( tan * tan + vRad * vRad );
		return vTan * (speed / tan);
	}

	/// <summary>
	/// Pump authority by height relative to the anchor: full below it, faded out above. A taut circular
	/// orbit always hangs below the anchor, so circling keeps full authority and can still build the
	/// speed to coast over the top — flat back-and-forth simply runs out of ceiling at the ledge.
	/// </summary>
	float ComputePumpHeightScale( Vector3 radial, Vector3 gravity )
	{
		var down = gravity.LengthSquared > 1e-6f ? gravity.Normal : Vector3.Down;
		var belowness = Vector3.Dot( radial, down );
		if ( belowness >= 0f )
			return 1f;

		var keep = Math.Clamp( SwingPumpAboveAnchorScale, 0f, 1f );
		// Fade over the ~15 degrees just past level so the ceiling is not a hard wall.
		var fade = Math.Clamp( 1f + belowness / 0.26f, 0f, 1f );
		return keep + (1f - keep) * fade;
	}

	/// <summary>
	/// Ice-skater rule: shorter rope, faster tangent. Pay-out does the reverse.
	/// Per-step clamp stops a hitch from exploding speed.
	/// </summary>
	void ApplyWinchAngularMomentum( Rigidbody body, Vector3 radial, float prevLen, float newLen )
	{
		if ( body is null || !body.IsValid() )
			return;
		if ( prevLen < 1f || newLen < 1f )
			return;
		if ( MathF.Abs( prevLen - newLen ) < 1e-3f )
			return;

		var vel = body.Velocity;
		var vRad = Vector3.Dot( vel, radial );
		var vTan = vel - radial * vRad;
		var tanSpeed = vTan.Length;
		if ( tanSpeed < 1e-4f )
			return;

		var scale = prevLen / newLen;
		var transfer = Math.Clamp( SwingWinchMomentumTransfer, 0f, 1f );
		var blended = 1f + (scale - 1f) * transfer;
		blended = Math.Clamp( blended, 0.85f, 1.15f );
		body.Velocity = radial * (vRad > 0f ? 0f : vRad) + vTan.Normal * (tanSpeed * blended);
	}

	/// <summary>
	/// Catch tangent from real motion only. Never invent <c>cross(radial, up)</c> — that is a
	/// sideways axis, and dumping catch speed onto it is how a vertical hang became an orbit.
	/// </summary>
	Vector3 ResolveCatchTangent( Vector3 vTan, Vector3 vel, Vector3 radial )
	{
		if ( vTan.LengthSquared > 1e-6f )
			return vTan.Normal;

		var horiz = vel.WithZ( 0f );
		horiz -= radial * Vector3.Dot( horiz, radial );
		if ( horiz.LengthSquared > 1e-6f )
			return horiz.Normal;

		var look = BuildSwingInputDirection();
		if ( look.LengthSquared > 1e-6f )
		{
			look -= radial * Vector3.Dot( look, radial );
			if ( look.LengthSquared > 1e-6f )
				return look.Normal;
		}

		return Vector3.Zero;
	}

	/// <summary>
	/// Capture WASD for the rope pump, then hide those actions so MoveModeWalk cannot
	/// air-accelerate (toward 0 or WalkSpeed) and steal tangent speed.
	/// </summary>
	void SampleGrappleSteerAndHideFromWalk()
	{
		_grappleSteerSampled = false;
		_grappleSteerX = 0f;
		_grappleSteerY = 0f;

		if ( !GrappleAttached )
			return;

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is not null && _controller.IsOnGround )
			return;

		ReadMoveAxes( out _grappleSteerX, out _grappleSteerY );
		_grappleSteerSampled = true;

		// SetAction only — ReleaseAction would eat the hold, so the next PreInput would see no WASD.
		HideMoveActionFromWalk( "Forward" );
		HideMoveActionFromWalk( "Backward" );
		HideMoveActionFromWalk( "Left" );
		HideMoveActionFromWalk( "Right" );

		// Sprint speeds up walking, never swinging.
		HideMoveActionFromWalk( SprintInputAction );
		if ( _controller is not null && !string.Equals( _controller.AltMoveButton, SprintInputAction, StringComparison.OrdinalIgnoreCase ) )
			HideMoveActionFromWalk( _controller.AltMoveButton );
	}

	/// <summary>
	/// Keyboard W/S/A/D are exact. Analog is only used when no movement key is down (gamepad).
	/// Holding W must not pick up A from AnalogMove.
	/// </summary>
	static void ReadMoveAxes( out float x, out float y )
	{
		x = (Input.Down( "Right" ) ? 1f : 0f) - (Input.Down( "Left" ) ? 1f : 0f);
		y = (Input.Down( "Forward" ) ? 1f : 0f) - (Input.Down( "Backward" ) ? 1f : 0f);
		if ( MathF.Abs( x ) > 1e-4f || MathF.Abs( y ) > 1e-4f )
			return;

		const float analogDeadzone = 0.2f;
		var analog = Input.AnalogMove;
		x = -analog.y;
		y = analog.x;
		if ( MathF.Abs( x ) < analogDeadzone )
			x = 0f;
		if ( MathF.Abs( y ) < analogDeadzone )
			y = 0f;
	}

	/// <summary>Active view camera rotation. WASD is relative to this, not the pawn model.</summary>
	Rotation GetSwingCameraRotation()
	{
		var cam = BuildViewCamera.Resolve( GameObject );
		if ( cam.IsValid() )
			return cam.WorldRotation;

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is not null && _controller.IsValid() )
			return _controller.EyeAngles.ToRotation();

		return GameObject.WorldRotation;
	}

	static void HideMoveActionFromWalk( string action )
	{
		if ( string.IsNullOrWhiteSpace( action ) )
			return;

		if ( !Input.Pressed( action ) && !Input.Down( action ) )
			return;

		Input.SetAction( action, false );
	}

	/// <summary>
	/// Camera-yaw horizontal WASD direction (unit), or zero when there is no input. Deliberately
	/// not projected onto the rope's tangent plane — that projection collapses to zero and then
	/// flips sign as you pass the anchor's height, which walled the swing at grapple-point height.
	/// </summary>
	Vector3 BuildSwingInputDirection()
	{
		float x;
		float y;
		if ( _grappleSteerSampled )
		{
			x = _grappleSteerX;
			y = _grappleSteerY;
		}
		else
		{
			ReadMoveAxes( out x, out y );
		}

		if ( MathF.Abs( x ) < 1e-4f && MathF.Abs( y ) < 1e-4f )
			return Vector3.Zero;

		var camRot = GetSwingCameraRotation();
		var wish = camRot.Forward * y + camRot.Right * x;
		return wish.LengthSquared < 1e-6f ? Vector3.Zero : wish.Normal;
	}

	Rigidbody ResolveGrappleBody()
	{
		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller?.Body is not null && _controller.Body.IsValid() )
			return _controller.Body;

		return Components.Get<Rigidbody>();
	}

	public void OnCollisionStart( Collision collision ) => TryKillGrappleOnCollision( collision );

	public void OnCollisionUpdate( Collision _ ) { }

	public void OnCollisionStop( CollisionStop _ ) { }

	void TryKillGrappleOnCollision( Collision collision )
	{
		if ( !GrappleAttached || !IsLocalMovementDriver() )
			return;

		var other = collision.Other.GameObject;
		if ( !other.IsValid() )
			return;
		if ( other == GameObject || other.Root == GameObject.Root )
			return;

		// Orbiting a grapple post means scraping that collider — not a wall slam.
		if ( IsGrappleSurfaceObject( other ) )
			return;

		var body = ResolveGrappleBody();
		var vel = body is not null && body.IsValid() ? body.Velocity : Vector3.Zero;
		var closing = collision.Contact.NormalSpeed;
		if ( closing < 1f )
			closing = Vector3.Dot( vel, -collision.Contact.Normal );

		ConsiderGrappleWallHit( collision.Contact.Normal, vel, closing );
	}

	void TickGrappleBodyBlock()
	{
		if ( !GrappleAttached )
			return;

		_controller ??= Components.Get<PlayerController>();
		var radius = _controller is not null ? Math.Max( 8f, _controller.BodyRadius * 0.85f ) : 14f;
		var pos = GameObject.WorldPosition;

		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		var body = ResolveGrappleBody();
		var vel = body is not null && body.IsValid() ? body.Velocity : Vector3.Zero;

		var overlap = scene.Trace.Sphere( radius, pos, pos )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		_grappleBlockedByWall = overlap.Hit
		                        && overlap.StartedSolid
		                        && !IsGrappleSurfaceObject( overlap.GameObject )
		                        && IsWallNormal( overlap.Normal )
		                        && vel.Length > 40f;
	}

	void ConsiderGrappleWallHit( Vector3 normal, Vector3 velocity, float closing )
	{
		if ( !IsWallNormal( normal ) )
			return;

		if ( closing < 120f )
			return;

		var body = ResolveGrappleBody();
		if ( body is not null && body.IsValid() )
		{
			body.Velocity = Vector3.Zero;
			body.AngularVelocity = Vector3.Zero;
		}

		_grappleBlockedByWall = true;
	}

	static bool IsWallNormal( Vector3 normal )
	{
		if ( normal.LengthSquared < 1e-6f )
			return false;

		normal = normal.Normal;
		// Ground skip (up) and head graze (down) are not a body block.
		return MathF.Abs( normal.z ) <= 0.55f;
	}

	static bool IsGrappleSurfaceObject( GameObject go )
	{
		if ( !go.IsValid() )
			return false;

		for ( var cur = go; cur.IsValid(); cur = cur.Parent )
		{
			if ( ObjectHasGrappleTag( cur ) )
				return true;
		}

		return false;
	}

	/// <summary>
	/// Host/authority: collision-clamped flat dash along <paramref name="flatForwardUnit"/>.
	/// <paramref name="meters"/> is designer meters; converted to pawn engine units via BodyHeight/1.8
	/// (citizen is ~72u tall ≈ 1.8m — a literal 1-unit "meter" was invisible).
	/// </summary>
	public void ServerApplyFlatDashMeters( Vector3 flatForwardUnit, float meters )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		ApplyFlatDashMeters( flatForwardUnit, meters );
	}

	/// <summary>Owner prediction: same dash math as host so the shover sees the lunge immediately.</summary>
	public void PredictFlatDashMeters( Vector3 flatForwardUnit, float meters )
	{
		if ( Networking.IsHost )
			return;

		ApplyFlatDashMeters( flatForwardUnit, meters );
	}

	void ApplyFlatDashMeters( Vector3 flatForwardUnit, float meters )
	{
		meters = Math.Max( 0f, meters );
		if ( meters <= 1e-4f )
			return;

		var flat = flatForwardUnit.WithZ( 0f );
		if ( flat.LengthSquared < 1e-6f )
			return;

		flat = flat.Normal;
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		_controller ??= Components.Get<PlayerController>();

		var bodyHeight = _controller is not null && _controller.IsValid()
			? Math.Max( 24f, _controller.BodyHeight )
			: 72f;
		var bodyRadius = _controller is not null && _controller.IsValid()
			? Math.Max( 8f, _controller.BodyRadius )
			: 16f;
		// Citizen BodyHeight 72 ≈ 1.8m → ~40 engine units per designer meter.
		var unitsPerMeter = bodyHeight / 1.8f;
		var distance = meters * unitsPerMeter;

		var start = GameObject.WorldPosition + Vector3.Up * (bodyHeight * 0.5f);
		var tr = scene.Trace.Ray( start, start + flat * distance )
			.Radius( bodyRadius * 0.9f )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		var travel = distance;
		if ( tr.Hit )
			travel = Math.Max( 0f, (tr.HitPosition - start).WithZ( 0f ).Length - bodyRadius );

		if ( travel <= 1e-4f )
			return;

		GameObject.WorldPosition += flat * travel;
		Transform.ClearInterpolation();
		if ( GameObject.Network is { Active: true } )
			GameObject.Network.ClearInterpolation();

		var body = ResolveGrappleBody();
		if ( body is not null && body.IsValid() )
		{
			var burst = flat * Math.Max( 200f, travel / 0.1f );
			body.Velocity = new Vector3( burst.x, burst.y, body.Velocity.z );
			body.AngularVelocity = Vector3.Zero;
		}
	}

	void UpdateSprintStaminaHoldAndFlushOnRelease()
	{
		if ( string.IsNullOrWhiteSpace( SprintInputAction ) || SprintStaminaPerSecond <= 0f )
		{
			if ( GameObject.Network is { Active: true } && !Networking.IsHost && _sprintHeldReportedToHostLast )
			{
				_sprintHeldReportedToHostLast = false;
				RpcSprintHeldForRegen( false );
			}

			if ( _sprintWasDown )
				FlushSprintStaminaDebt( "sprint action cleared" );
			_sprintWasDown = false;
			return;
		}

		var wantsSprint = Input.Down( SprintInputAction );
		var sprintAllowed = WantsSprintStaminaSpend();
		var reportHeld = sprintAllowed;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost && reportHeld != _sprintHeldReportedToHostLast )
		{
			_sprintHeldReportedToHostLast = reportHeld;
			RpcSprintHeldForRegen( reportHeld );
		}

		if ( !sprintAllowed )
		{
			if ( _sprintWasDown )
				FlushSprintStaminaDebt( wantsSprint && _vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon ) ? "stamina exhausted" : "sprint released" );
			_sprintWasDown = false;
			return;
		}

		if ( !_sprintWasDown && _vitals.LogVitalsNetworking )
			Log.Info( $"[PlayerMovement|{PlayerVitals.GetVitalsProcessRoleTag( GameObject )}] {GameObject.Name}: sprint held ({SprintInputAction}) — local drain {SprintStaminaPerSecond:0.#}/s, authority sync on release only" );

		var d = SprintStaminaPerSecond * Time.Delta;
		var applied = Math.Min( d, Math.Max( 0f, _vitals.CurrentStamina ) );
		if ( applied > 1e-6f )
		{
			_sprintDebtPending += applied;
			_vitals.ApplyLocalStaminaSprintPreviewSpend( applied );
		}

		_sprintWasDown = true;
	}

	static void ClearActionIfPressed( string action )
	{
		if ( string.IsNullOrWhiteSpace( action ) )
			return;

		if ( !Input.Pressed( action ) && !Input.Down( action ) )
			return;

		Input.SetAction( action, false );
		Input.ReleaseAction( action );
	}

	void ApplyExhaustedJumpVelocityScale()
	{
		var body = Components.Get<Rigidbody>();
		if ( body is null )
			return;

		var scale = Math.Clamp( ExhaustedJumpHeightFraction, 0f, 1f );
		if ( scale >= 0.999f )
			return;

		var up = Vector3.Up;
		var upwardSpeed = Vector3.Dot( body.Velocity, up );
		if ( upwardSpeed <= 1e-4f )
			return;

		var reducedUpwardSpeed = upwardSpeed * scale;
		body.Velocity += up * (reducedUpwardSpeed - upwardSpeed);
	}

	[Rpc.Host]
	void RpcSprintHeldForRegen( bool sprintHeld )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		_sprintHeldReportedOnHost = sprintHeld;
	}

	[Rpc.Host]
	void RpcSneakHeldForRegen( bool sneakHeld )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		_sneakHeldReportedOnHost = sneakHeld;
	}

	void FlushSneakStaminaDebt( string reason )
	{
		if ( _sneakDebtPending <= 1e-6f )
		{
			_sneakDebtPending = 0f;
			return;
		}

		var debt = _sneakDebtPending;

		if ( !_vitals.MayIssueVitalsDelta() )
		{
			_sneakDebtPending = 0f;
			return;
		}

		_sneakDebtPending = 0f;

		if ( !_vitals.RequestVitalsDelta( 0f, -debt, mergePendingSprintDebt: false ) )
		{
			_vitals.RestoreLocalStaminaAfterFailedSprintSpend( debt );
			if ( _vitals.LogVitalsNetworking )
				Log.Warning( $"[PlayerMovement|{PlayerVitals.GetVitalsProcessRoleTag( GameObject )}] {GameObject.Name}: {reason} — sneak stamina −{debt:0.###} rejected by authority (restored preview)" );
			return;
		}

		if ( _vitals.LogVitalsNetworking )
			Log.Info( $"[PlayerMovement|{PlayerVitals.GetVitalsProcessRoleTag( GameObject )}] {GameObject.Name}: {reason} — synced sneak stamina −{debt:0.###} → ST={_vitals.CurrentStamina:0.#}/{_vitals.CurrentStaminaMax:0.#}" );
	}

	void FlushSprintStaminaDebt( string reason )
	{
		if ( _sprintDebtPending <= 1e-6f )
		{
			_sprintDebtPending = 0f;
			return;
		}

		var debt = _sprintDebtPending;

		if ( !_vitals.MayIssueVitalsDelta() )
		{
			_sprintDebtPending = 0f;
			return;
		}

		_sprintDebtPending = 0f;

		// Do not use TrySpendStamina: preview already reduced CurrentStamina each frame; host authority still had full pool until this delta.
		if ( !_vitals.RequestVitalsDelta( 0f, -debt, mergePendingSprintDebt: false ) )
		{
			_vitals.RestoreLocalStaminaAfterFailedSprintSpend( debt );
			if ( _vitals.LogVitalsNetworking )
				Log.Warning( $"[PlayerMovement|{PlayerVitals.GetVitalsProcessRoleTag( GameObject )}] {GameObject.Name}: {reason} — sprint stamina −{debt:0.###} rejected by authority (restored preview)" );
			return;
		}

		if ( _vitals.LogVitalsNetworking )
			Log.Info( $"[PlayerMovement|{PlayerVitals.GetVitalsProcessRoleTag( GameObject )}] {GameObject.Name}: {reason} — synced sprint stamina −{debt:0.###} → ST={_vitals.CurrentStamina:0.#}/{_vitals.CurrentStaminaMax:0.#}" );
	}

	protected override void OnDestroy()
	{
		if ( _vitals is not null && ( _sprintWasDown || _sprintDebtPending > 1e-6f ) )
			FlushSprintStaminaDebt( "destroyed" );
		if ( _vitals is not null && ( _sneakWasDown || _sneakDebtPending > 1e-6f ) )
			FlushSneakStaminaDebt( "destroyed" );
		RestoreBlockedSprintRunSpeed();
		base.OnDestroy();
	}
}
