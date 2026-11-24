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

	/// <summary>
	/// First person shows the pawn's arms (<see cref="PlayerAnimation"/>, FirstPerson.cs); the camera sits this far ahead of
	/// the eyes along the view's yaw, clear of the shoulders.
	/// </summary>
	[Property, Group( "Camera" ), Title( "First person: camera forward (m)" ), Range( 0f, 0.4f ), Step( 0.01f )]
	public float FirstPersonCameraForwardMeters { get; set; } = 0.14f;

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
	float _designWalkSpeed = 160f;
	float _designRunSpeed = 280f;

	/// <summary>
	/// Ground speeds (Mark 2026-11-14, Last Oasis): W walks at <see cref="WalkSpeedMetersPerSecond"/> (4.5 m/s) and runs
	/// with Shift at <see cref="RunSpeedMetersPerSecond"/> (5.85 m/s = walk +30 %; 2026-11-17, Mark: "increase walk speed
	/// and make run speed like barely faster than walk speed", then "make run 20% faster instead of 10%", 2026-11-18 "make run
	/// speed 30% faster than walk" - was 3.5 / 6.8, then 4.5 / 5.4); A / D strafe at <see cref="StrafeSpeedScale"/> (80 %)
	/// of that and S walks backwards at <see cref="BackwardSpeedScale"/> (70 %). The body faces the camera while moving.
	/// Meters per second, converted once at the pawn scale (BodyHeight / 1.8) into the controller's WalkSpeed / RunSpeed.
	/// </summary>
	[Property, Group( "Ground Speed" ), Title( "Walk forward (m/s)" ), Range( 0.5f, 10f ), Step( 0.1f )]
	public float WalkSpeedMetersPerSecond { get; set; } = 4.5f;

	[Property, Group( "Ground Speed" ), Title( "Run forward (m/s)" ), Range( 0.5f, 15f ), Step( 0.05f )]
	public float RunSpeedMetersPerSecond { get; set; } = 5.85f;

	[Property, Group( "Ground Speed" ), Title( "Strafe (x forward)" ), Description( "A / D move at this share of the forward speed." ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float StrafeSpeedScale { get; set; } = 0.8f;

	[Property, Group( "Ground Speed" ), Title( "Backward (x forward)" ), Description( "S walks backwards at this share of the forward speed." ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float BackwardSpeedScale { get; set; } = 0.7f;

	/// <summary>
	/// Combat footwork (Mark 2026-11-14 melee spec): ground speed while a melee action runs, as a share of the speed the
	/// pawn would have otherwise. The state comes from <see cref="PlayerCombat.CurrentFootworkState"/>.
	/// </summary>
	[Property, Group( "Combat Footwork" ), Title( "Light windup (x)" ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float FootworkLightWindupScale { get; set; } = 0.95f;

	[Property, Group( "Combat Footwork" ), Title( "Light release (x)" ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float FootworkLightReleaseScale { get; set; } = 0.9f;

	[Property, Group( "Combat Footwork" ), Title( "Attack recovery (x)" ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float FootworkRecoveryScale { get; set; } = 0.95f;

	[Property, Group( "Combat Footwork" ), Title( "Blocking (x)" ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float FootworkBlockingScale { get; set; } = 0.85f;

	[Property, Group( "Combat Footwork" ), Title( "Heavy windup (x)" ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float FootworkHeavyWindupScale { get; set; } = 0.8f;

	[Property, Group( "Combat Footwork" ), Title( "Heavy release (x)" ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float FootworkHeavyReleaseScale { get; set; } = 0.75f;

	[Property, Group( "Combat Footwork" ), Title( "Staggered (x)" ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float FootworkStaggeredScale { get; set; } = 0.4f;

	/// <summary>The combat footwork share for the pawn's current melee state (1 outside combat).</summary>
	public float GetCombatFootworkScale()
	{
		var combat = Components.Get<PlayerCombat>();
		if ( combat is null || !combat.Enabled )
			return 1f;

		return combat.CurrentFootworkState switch
		{
			CombatFootworkState.LightWindup => FootworkLightWindupScale,
			CombatFootworkState.LightRelease => FootworkLightReleaseScale,
			CombatFootworkState.Recovery => FootworkRecoveryScale,
			CombatFootworkState.Blocking => FootworkBlockingScale,
			CombatFootworkState.HeavyWindup => FootworkHeavyWindupScale,
			CombatFootworkState.HeavyRelease => FootworkHeavyReleaseScale,
			CombatFootworkState.Staggered => FootworkStaggeredScale,
			_ => 1f
		};
	}

	/// <summary>
	/// W = 1, A / D = <see cref="StrafeSpeedScale"/>, S = <see cref="BackwardSpeedScale"/>; diagonals blend by how much of
	/// the stick points each way. Read from the driver's own move input (the controller moves on the driver).
	/// </summary>
	public float GetDirectionalSpeedScale()
	{
		if ( !IsLocalMovementDriver() )
			return 1f;

		var move = Input.AnalogMove;
		var forward = move.x;
		var side = MathF.Abs( move.y );
		var along = MathF.Abs( forward );
		var total = along + side;
		if ( total < 1e-3f )
			return 1f;

		var forwardShare = forward >= 0f ? 1f : Math.Clamp( BackwardSpeedScale, 0.1f, 1f );
		return (along * forwardShare + side * Math.Clamp( StrafeSpeedScale, 0.1f, 1f )) / total;
	}

	/// <summary>Walk toggle: the default move drops to this slow walk (1.6 m/s, the walk clip pace).</summary>
	[Property, Group( "Walk Toggle" ), Title( "Walk speed (m/s)" ), Range( 0.5f, 4f ), Step( 0.05f )]
	public float WalkToggleMetersPerSecond { get; set; } = 1.6f;

	[Property, Group( "Walk Toggle" ), Title( "Walk toggle input" )]
	public string WalkInputAction { get; set; } = "Walk";

	/// <summary>Owner: the walk toggle is on (the default move is the slow walk instead of the jog).</summary>
	public bool IsWalkToggled { get; private set; }

	/// <summary>
	/// Aiming a bow (an arrow drawn, or ADS) takes this share off walk and run - an action, like the melee footwork.
	/// Carrying gear costs nothing (Mark 2026-11-17: "players should not be slowed down when they equip armor or weapons";
	/// the 2026-11-12 10 % penalties for a weapon / shield out and worn chest / feet armor are deleted).
	/// </summary>
	[Property, Group( "Gear Speed" ), Title( "Bow aiming penalty (0-1)" ), Description( "Walk and run lose this share while an arrow is drawn or the bow is aimed." ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float BowAimSpeedPenalty { get; set; } = 0.2f;

	/// <summary>
	/// Physics can redirect impact into horizontal for a few steps after <see cref="OnLanded"/> —
	/// keep scrubbing so roof downhill jumps don't launch.
	/// </summary>
	int _sanitizeLandFrames;

	/// <summary>Host copy of the owning client’s sprint button, for <see cref="ShouldBlockStaminaRegenForAuthority"/> (local driver uses <see cref="Sandbox.Input"/> directly).</summary>
	bool _sprintHeldReportedOnHost;

	bool _sprintHeldReportedToHostLast;

	/// <summary>Host-synced: staged-event freeze (time trial countdown, arena countdown) — no move / jump / grapple / wingsuit.</summary>
	[Sync( SyncFlags.FromHost )]
	public bool EventInputLocked { get; private set; }

	public void HostSetEventFrozen( bool frozen )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		EventInputLocked = frozen;
		ApplyEventFreezeLocal( frozen );
		if ( GameObject.Network is { Active: true } )
			RpcOwnerApplyEventFreeze( frozen );
	}

	[Rpc.Owner]
	void RpcOwnerApplyEventFreeze( bool frozen )
	{
		ApplyEventFreezeLocal( frozen );
	}

	/// <summary>Host-synced: a <see cref="BearTrap"/> has this pawn — no move / jump / roll / grapple. Look and swing still work.</summary>
	[Sync( SyncFlags.FromHost )]
	public bool TrapLocked { get; private set; }

	/// <summary>Owner-side: where the trap holds the feet (XY); Z stays the pawn's own so the controller keeps its ground.</summary>
	Vector3 _trapAnchorWorld;
	bool _trapAnchorActive;

	/// <summary>
	/// Host: the trap sets this on catch (with the trap centre as <paramref name="anchorWorld"/>) and
	/// clears it on release (or death — see <see cref="PlayerVitals"/>). The owner is pulled to the
	/// anchor and held there every frame, so a foot on the plate's corner still ends up centred.
	/// </summary>
	public void HostSetTrapped( bool trapped, Vector3 anchorWorld = default )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( TrapLocked == trapped )
			return;

		TrapLocked = trapped;
		ApplyTrapHoldLocal( trapped, anchorWorld );
		if ( GameObject.Network is { Active: true } )
			RpcOwnerApplyTrapHold( trapped, anchorWorld );
	}

	[Rpc.Owner]
	void RpcOwnerApplyTrapHold( bool trapped, Vector3 anchorWorld )
	{
		ApplyTrapHoldLocal( trapped, anchorWorld );
	}

	void ApplyTrapHoldLocal( bool trapped, Vector3 anchorWorld )
	{
		ApplyEventFreezeLocal( trapped );
		_trapAnchorActive = trapped;
		_trapAnchorWorld = anchorWorld;
		if ( trapped )
			PinToTrapAnchor();
	}

	/// <summary>Owner: slide the feet onto the trap centre (XY only — never the camera, never the root yaw).</summary>
	void PinToTrapAnchor()
	{
		if ( !_trapAnchorActive )
			return;

		var pos = GameObject.WorldPosition;
		var target = new Vector3( _trapAnchorWorld.x, _trapAnchorWorld.y, pos.z );
		if ( (pos - target).WithZ( 0f ).LengthSquared < 0.01f )
			return;

		GameObject.WorldPosition = target;
	}

	void ApplyEventFreezeLocal( bool frozen )
	{
		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();

		if ( !frozen )
		{
			// The freeze zeroes velocity every frame, so a pawn frozen mid-air (arena spawns sit
			// above their pad) can put its physics body to sleep — it then floats after "FIGHT!"
			// until input wakes it. Wake it explicitly so gravity drops it immediately.
			if ( body is not null && body.IsValid() && body.Sleeping )
				body.Sleeping = false;
			return;
		}

		if ( body is not null && body.IsValid() )
			body.Velocity = Vector3.Zero;

		if ( GrappleAttached )
			DetachGrappleForHitReaction();
	}

	/// <summary>Host places a pawn at a staged-event spawn (time trial pads, arena start); owning clients apply via <see cref="RpcOwnerApplyEventSpawn"/>.</summary>
	public void HostApplyEventSpawn( Vector3 worldPos, Rotation worldRot )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		ApplyEventSpawnLocal( worldPos, worldRot );
		if ( GameObject.Network is { Active: true } )
			RpcOwnerApplyEventSpawn( worldPos, worldRot );
	}

	[Rpc.Owner]
	void RpcOwnerApplyEventSpawn( Vector3 worldPos, Rotation worldRot )
	{
		ApplyEventSpawnLocal( worldPos, worldRot );
	}

	void ApplyEventSpawnLocal( Vector3 worldPos, Rotation worldRot )
	{
		GameObject.WorldPosition = worldPos;
		GameObject.WorldRotation = worldRot;

		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		if ( body is not null && body.IsValid() )
			body.Velocity = Vector3.Zero;

		if ( _controller is not null )
			_controller.EyeAngles = worldRot.Angles();
		SnapBodyYawToLook();
	}

	/// <summary>
	/// Host dev teleport (Settings → Dev tools → Players, <see cref="DevPlayerControls"/>): position only — the
	/// camera and the root yaw stay the player's own. Lets go of whatever would drag the pawn back first (bear trap,
	/// vehicle seat, bed, grapple rope). False on a client, or while the pawn still rides its stasis pod.
	/// </summary>
	public bool HostDevTeleport( Vector3 worldPos )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return false;
		if ( PodLocked )
			return false;

		HostSetTrapped( false );
		Vehicle.HostEjectIfSeated( GameObject );
		HostLeaveBed( placeBeside: false );
		DetachGrappleForWorldTeleport();

		ApplyDevTeleportLocal( worldPos );
		if ( GameObject.Network is { Active: true } )
			RpcOwnerApplyDevTeleport( worldPos );
		return true;
	}

	/// <summary>The pawn's transform is owner-simulated: the owning client places itself.</summary>
	[Rpc.Owner]
	void RpcOwnerApplyDevTeleport( Vector3 worldPos )
	{
		ApplyDevTeleportLocal( worldPos );
	}

	void ApplyDevTeleportLocal( Vector3 worldPos )
	{
		GameObject.WorldPosition = worldPos;
		Transform.ClearInterpolation();
		if ( GameObject.Network is { Active: true } )
			Network.ClearInterpolation();

		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		if ( body is not null && body.IsValid() )
		{
			body.Velocity = Vector3.Zero;
			body.Sleeping = false;
		}
	}

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		_controller = Components.Get<PlayerController>();
		CacheDesignLocomotionSpeeds();
		ApplyWalkableGroundAngle();
		InitializeGrapple();
		InitializeCarryWeight();
		if ( _vitals is null )
			Log.Warning( $"[PlayerMovement|{PlayerVitals.GetVitalsProcessRoleTag( GameObject )}] {GameObject.Name}: add PlayerVitals on this pawn — movement stamina hooks disabled." );
	}

	/// <summary>The forward walk / run speeds in engine units: the meters properties converted once at the pawn scale.</summary>
	void CacheDesignLocomotionSpeeds()
	{
		if ( _controller is null || !_controller.IsValid() )
			return;

		var unitsPerMeter = Math.Max( 1f, _controller.BodyHeight ) / 1.8f;
		_designWalkSpeed = WalkSpeedMetersPerSecond > 0f ? WalkSpeedMetersPerSecond * unitsPerMeter
			: _controller.WalkSpeed > 1f ? _controller.WalkSpeed : _designWalkSpeed;
		_designRunSpeed = RunSpeedMetersPerSecond > 0f ? RunSpeedMetersPerSecond * unitsPerMeter
			: _controller.RunSpeed > 1f ? _controller.RunSpeed : _designRunSpeed;
	}

	float DesignGroundMaxSpeed()
	{
		var max = Math.Max( _designWalkSpeed, _designRunSpeed );
		return max > 1f ? max : 280f;
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

	/// <summary>
	/// Crouch key held (or the <c>sneak</c> hack, or the controller ducked), whatever the stamina and whether the
	/// pawn moves - a player kneeling still with food in hand is what an animal reads as a taming offer.
	/// </summary>
	public bool IsCrouchHeldForTaming()
	{
		var ducked = _controller is not null && _controller.IsValid() && _controller.IsDucking;
		if ( IsLocalMovementDriver() )
			return ducked || GameHacks.SimulatedSneakHeld || (!string.IsNullOrWhiteSpace( SneakInputAction ) && Input.Down( SneakInputAction ));
		return ducked || _sneakHeldReportedOnHost;
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

		if ( ShouldSuppressSprintInput() )
			return false;

		if ( !Input.Down( SprintInputAction ) || _vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon ) )
			return false;

		return HasMovementSprintIntent();
	}

	/// <summary>
	/// Clear held Run for the bola walk-lock and the bow-aim walk cap. Airborne wish still
	/// won't invent sprint accel (controller air control); takeoff speed is kept until land.
	/// </summary>
	bool ShouldSuppressSprintInput() => IsMeleeAttackWalkOnlyOnGround() || IsCombatWalkCapActive();

	bool IsMeleeAttackWalkOnlyOnGround()
	{
		var combat = Components.Get<PlayerCombat>();
		return combat is not null && combat.IsMeleeAttackWalkOnlyActive;
	}

	/// <summary>Regular walking speed, no sprint: an aimed bow (<see cref="PlayerCombat.IsAttackWalkCapActive"/>).</summary>
	bool IsCombatWalkCapActive() => Components.Get<PlayerCombat>() is { IsAttackWalkCapActive: true };

	/// <summary>
	/// True while this pawn is in its "just got hit" window (no jump, no rope). Read from
	/// <see cref="PlayerAnimation"/>, which owns the window.
	/// </summary>
	public bool IsHitReactionActive() =>
		Components.Get<PlayerAnimation>() is { IsHitReactionActive: true };

	/// <summary>Hit reaction start: the rope always drops, on every machine that knows about this pawn.</summary>
	internal void OnHitReactionBegan() => DetachGrappleForHitReaction();

	/// <summary>Soft-clear Run / AltMove during the melee walk-lock and the combat walk cap.</summary>
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
		// A trap mutes the same way: zero wish so WASD cannot creep the pawn off the plate. A hook on a player or a
		// tow log mutes nothing: the shooter keeps normal ground locomotion (PlayerMovement.GrapplePlayer.cs).
		if ( WingsuitDeployed || TrapLocked || IsSeated || PodLocked
		     || (GrappleAttached && !IsMovingTargetAttach && !_controller.IsOnGround) )
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
		ApplyArmorLocomotionScale();
	}

	/// <summary>
	/// Walk / run share kept while aiming a bow (<see cref="BowAimSpeedPenalty"/>); 1 otherwise. Written only in the
	/// plain-locomotion branch: the melee slow and the grapple / wingsuit mute snapshot whatever is current, so they
	/// scale with it and restore to it.
	/// </summary>
	float ComputeBowAimSpeedScale() =>
		Components.Get<PlayerCombat>() is { IsBowAiming: true } ? Math.Clamp( 1f - BowAimSpeedPenalty, 0.1f, 1f ) : 1f;

	void ApplyArmorLocomotionScale()
	{
		if ( _controller is null || !_controller.IsValid() )
			return;

		var tow = ComputeTowSpeedScale();   // dragging a log on a taut rope slows the holder
		// W / A / D / S direction and the combat footwork share. Carried weight slows nothing (Mark 2026-11-16).
		// On someone's grapple hook: HookedSpeedScale (PlayerMovement.GrapplePlayer.cs).
		var scale = ComputeBowAimSpeedScale() * tow * HookedSpeedScale() * GetDirectionalSpeedScale() * GetCombatFootworkScale();
		// Walk toggle: the default move drops from the jog to the slow walk (0 / unset falls back to the jog).
		var baseWalk = IsWalkToggled && WalkToggleMetersPerSecond > 0f
			? WalkToggleMetersPerSecond * Math.Max( 1f, _controller.BodyHeight ) / 1.8f
			: _designWalkSpeed;
		_controller.WalkSpeed = baseWalk * scale;
		// An aimed bow: Shift adds nothing — run is pulled down to walk.
		_controller.RunSpeed = IsCombatWalkCapActive() ? _controller.WalkSpeed : _designRunSpeed * scale;
	}

	/// <summary>
	/// Bola charge: scale Walk+Run to <see cref="PlayerCombat.MeleeAttackMoveSpeedScale"/> and force Run=Walk so sprint
	/// cannot bypass. Melee swings, guard and stagger use the combat footwork shares instead. True when active.
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
			_savedWalkForMelee = _controller.WalkSpeed > 1f ? _controller.WalkSpeed : _designWalkSpeed;
			_savedRunForMelee = _controller.RunSpeed > 1f ? _controller.RunSpeed : _designRunSpeed;
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
			_controller.WalkSpeed = _savedWalkForMelee > 1f ? _savedWalkForMelee : _designWalkSpeed;
			_controller.RunSpeed = _savedRunForMelee > 1f ? _savedRunForMelee : _designRunSpeed;
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
			_controller.WalkSpeed = _savedWalkSpeed > 1f ? _savedWalkSpeed : _designWalkSpeed;
			_walkSpeedMuteActive = false;
		}

		if ( !_runSpeedOverrideActive )
			return;

		if ( _controller is not null )
			_controller.RunSpeed = _savedRunSpeed > 1f ? _savedRunSpeed : _designRunSpeed;

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

		if ( !(Input.Down( SneakInputAction ) || GameHacks.SimulatedSneakHeld) || _vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon ) )
			return false;

		if ( GrappleAttached )
			return false;

		// Sneaking while sprinting doesn't stack — run wins for noise + stamina.
		if ( WantsSprintStaminaSpend() )
			return false;

		// Sneaky Feet augment: sneaking is free.
		if ( ResolveAugments()?.IsAbilityOn( AugmentAbility.SneakyFeet ) == true )
			return false;

		return HasMovementSprintIntent();
	}

	public void PreInput()
	{
		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		if ( GameHacks.ConsumeSimulatedJumpPress() && !string.IsNullOrWhiteSpace( JumpInputAction ) )
			Input.SetAction( JumpInputAction, true );

		if ( !string.IsNullOrWhiteSpace( WalkInputAction ) && Input.Pressed( WalkInputAction ) )
			IsWalkToggled = !IsWalkToggled;

		// The sneak hack holds the crouch key itself, before the controller reads input, so it ducks like a real press.
		if ( GameHacks.SimulatedSneakHeld && !string.IsNullOrWhiteSpace( SneakInputAction ) )
			Input.SetAction( SneakInputAction, true );

		if ( PreInputArenaSpectate() )
			return;

		if ( IsSeated )
		{
			PreInputSeated();
			return;
		}

		// Lying in a bed: rest on the mattress; Use / Jump / a move key gets up (PlayerMovement.Bed.cs).
		if ( PreInputBed() )
			return;

		TickPodLock();
		if ( EventInputLocked || TrapLocked || PodLocked )
		{
			ClearActionIfPressed( JumpInputAction );
			if ( !string.IsNullOrWhiteSpace( SprintInputAction ) )
				ClearActionIfPressed( SprintInputAction );
			ClearActionIfPressed( SneakInputAction );
			_controller ??= Components.Get<PlayerController>();
			var body = _controller?.Body ?? Components.Get<Rigidbody>();
			if ( body is not null && body.IsValid() )
				body.Velocity = Vector3.Zero;
			if ( TrapLocked )
				PinToTrapAnchor();
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

		// Dodge roll and the augment air hop must see Jump before stamina or wingsuit clear it;
		// roll first so Block/Crouch (+A/D) + Space is a roll, never a jump.
		TickDodgeRollGate();
		// Airborne Space with a build-piece / static-object lip in reach is a ledge grab — before the air hop / wingsuit.
		TryStartJumpLedgeGrabFromJumpPress();
		TickAugmentJumpGates();
		TickAugmentSlideMotion();

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
		if ( GrappleAttached && !IsMovingTargetAttach )
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
		if ( IsLedgePulling )
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

		// Fixed-tick race: the controller can consume a roll press (Block/Crouch + Space) as a jump
		// before the frame's PreInput gate sees it — convert that hop into the roll. No stamina, no jump anim path.
		if ( TryConvertJumpIntoDodgeRoll() )
			return;

		// Jump may SubtractDirection along the ground normal after PreInput — scrub boost, keep sprint.
		SanitizeHorizontalToDesignRun( keepUpwardZ: true );

		if ( _vitals.OnControllerJumpedForStaminaFromMovement( JumpStaminaCost, ExhaustedJumpHeightFraction ) )
			ApplyExhaustedJumpVelocityScale();

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
		// Let go of a grapple in the air and came down fast: skip along instead (PlayerMovement.GrappleSkip.cs).
		if ( TryGrappleLandingSkip( impactVelocity ) )
			_sanitizeLandFrames = 0;
		else
		{
			// Slope redirect often lands after this callback — scrub for several physics steps.
			_sanitizeLandFrames = 20;
			SanitizeHorizontalToDesignRun( keepUpwardZ: false );
		}
		_vitals.OnControllerLandedForJumpStaminaFromMovement( distance, impactVelocity );
		OnAugmentLanded();
	}

	/// <summary>Perf bisect only (<c>perf_fov</c>): forces the pawn camera FOV after the engine sets it. 0 = off.</summary>
	public static float PerfFovOverride;

	/// <summary>
	/// After the built-in third-person camera (and its wall-trace ease), hard-place at our stepped zoom
	/// so zoom-out snaps the same way zoom-in does.
	/// </summary>
	void PlayerController.IEvents.PostCameraSetup( CameraComponent camera )
	{
		if ( !IsLocalMovementDriver() || !camera.IsValid() )
			return;

		if ( PlacePodCamera( camera ) )
			return;

		// Metres on the prefab, engine units on the camera (this line used to assign metres as units: 50 000 "m" was 1 250 m of view).
		camera.ZFar = Math.Max( TerrainWorldUnits.MetersToEngine( Math.Max( 100f, CameraFarClipMeters ) ), camera.ZFar );
		if ( PerfFovOverride > 0f )
			camera.FieldOfView = Math.Clamp( PerfFovOverride, 20f, 140f );

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null || !_controller.IsValid() )
			return;

		var combat = Components.Get<PlayerCombat>();

		// First-person: the eyes nudged forward over the shown body, FOV zoom (no CameraOffset pull).
		if ( !_controller.ThirdPerson )
		{
			var unitsPerMeter = MathF.Max( 1f, _controller.BodyHeight ) / 1.8f;
			camera.WorldPosition += Rotation.FromYaw( _controller.EyeAngles.yaw ).Forward * (FirstPersonCameraForwardMeters * unitsPerMeter);
			// The controller tags the pawn's renderers "viewer" in first person and the pawn camera leaves that tag out: draw
			// them - the body and worn pieces are shadow-only there and an arms-only renderer stands in (PlayerAnimation,
			// FirstPerson.cs). Nothing else on the pawn carries the tag.
			camera.RenderExcludeTags.Remove( "viewer" );
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

		// All machines: arena ghost hide/show; owner also flies the ghost.
		TickArenaSpectate();

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
		TickControllerBodyRotationOff();

		if ( !IsLocalMovementDriver() )
			return;

		TickSeatedPin();
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
	/// Engine button-code names are literal glyphs for punctuation ("-", "=") and KP_* for the
	/// numpad — "minus"/"equals"/"add"/"subtract" are not valid names. Shift+= still reports "=".
	/// </summary>
	static bool IsCameraZoomKeyPressed( bool zoomIn )
	{
		if ( zoomIn )
		{
			return Input.Keyboard.Pressed( "=" )
			       || Input.Keyboard.Pressed( "KP_PLUS" )
			       || Input.Pressed( "CameraZoomIn" );
		}

		return Input.Keyboard.Pressed( "-" )
		       || Input.Keyboard.Pressed( "KP_MINUS" )
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
		// Ignore from the ROOT, like the stock controller's own boom trace: while seated the pawn is
		// parented under its vehicle seat, and ignoring only the pawn let this sweep start solid inside
		// the chassis and pin the camera at the eye (dune buggy, 2026-10-05).
		var tr = Scene.Trace.Sphere( radius, eye, target )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.WithoutTags( "player", "trigger" )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
		{
			CameraBumpingGround = false;
			cam.WorldPosition = target;
			return;
		}

		// Upward-facing hit normal = the camera boom is being shortened by the ground, not a wall.
		CameraBumpingGround = tr.Normal.z > 0.5f;

		// Keep a bit of air off the hit surface so we don't sit inside the occluder.
		cam.WorldPosition = tr.HitPosition + tr.Normal * Math.Max( 4f, radius );
	}

	/// <summary>True while the third-person camera trace is pushed in by a ground-like surface (feeds the look-up body fade).</summary>
	public bool CameraBumpingGround { get; private set; }

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

		// Host-side: a client attacker's pawn is a proxy here, so the local-driver gate below
		// would starve the player-hook validity checks (target lost / downed / out of range).
		TickGrapplePlayerTargetValidity();
		TickGrappledByValidity();
		TickGrappleTowValidity();
		TickGrappleRideValidity();
		HostTickGrappleGearSync();
		TickGrappleTowPull( Time.Delta );   // host drives the towed log, whoever holds the rope

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
		TickLedgePull( Time.Delta );
		TickWingsuitAirborneTimer( Time.Delta );
		TickWingsuitFlight( Time.Delta );
		TickWingsuitFreefallHandoff();
		TickSlideOffCreatures();
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
			ArmGrappleSkipOnRopeEdge( attached: true );
			ApplyGrappleAttachVelocityScale( AttachVelocityScale );
			if ( WingsuitDeployed )
				StowWingsuit( keepMomentum: true );
		}

		if ( _grappleWasAttached && !GrappleAttached )
		{
			_grappleReleaseAir = true;
			ArmGrappleSkipOnRopeEdge( attached: false );
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
		// A ridden body tows through rope tension, never a position snap (PlayerMovement.GrappleRide.cs).
		if ( !GrappleAttached || GrappleRopeLengthEngine <= 1e-3f || IsMovingTargetAttach || GrappleAttachRideId != Guid.Empty )
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
		// Player attach: nobody is constrained — the rope stretches for the short tether
		// (PlayerMovement.GrapplePlayer.cs).
		if ( !GrappleAttached || GrappleRopeLengthEngine <= 1e-3f || IsLedgePulling || IsMovingTargetAttach )
		{
			_grapplePrevRopeLength = 0f;
			_grappleRopeTaut = false;
			ResetGrappleRideTowState();
			return;
		}

		var body = ResolveGrappleBody();
		if ( body is null )
			return;

		dt = Math.Max( 1e-4f, dt );

		// A hook on a moving body is a tow rope: solved against the anchor's velocity (PlayerMovement.GrappleRide.cs).
		if ( GrappleAttachRideId != Guid.Empty )
		{
			ApplyGrappleRideRopeConstraint( body, dt );
			return;
		}

		ResetGrappleRideTowState();

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

		// Player / tow attach never swings — WASD stays with MoveModeWalk.
		if ( !GrappleAttached || IsMovingTargetAttach )
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
		// Player / tow attach: walking into things while dragging something must not break the rope.
		// Towed behind a moving body: scraping terrain and rocks is part of the ride, the rope keeps pulling.
		if ( !GrappleAttached || IsMovingTargetAttach || IsGrappleRideAnchorMoving || !IsLocalMovementDriver() )
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
		ShutdownCarryWeight();
		base.OnDestroy();
	}
}
