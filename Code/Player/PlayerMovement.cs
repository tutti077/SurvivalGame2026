using System;

namespace Survival;

/// <summary>
/// Attach next to <see cref="PlayerVitals"/> on the pawn root. Handles <see cref="PlayerController.IEvents"/> (jump input / jump–land), sprint stamina,
/// and rope-swing constraint / air push while <see cref="PlayerGrapple"/> is attached.
/// </summary>
[Title( "Player Movement" )]
public sealed class PlayerMovement : Component, PlayerController.IEvents
{
	[Property, Group( "Stamina - Jump" )] public float JumpStaminaCost { get; set; } = 5f;

	/// <summary>
	/// Jump-height fraction used when stamina is too low to afford <see cref="JumpStaminaCost"/>. 1 = full jump, 0 = block jump.
	/// </summary>
	[Property, Group( "Stamina - Jump" )] public float ExhaustedJumpHeightFraction { get; set; } = 0.333f;

	/// <summary>Input cleared in <see cref="PreInput"/> when <see cref="JumpStaminaCost"/> is positive and stamina cannot pay the full cost.</summary>
	[Property, Group( "Stamina - Jump" )] public string JumpInputAction { get; set; } = "jump";

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

	[Property, Group( "Camera" ), Title( "Scroll wheel zoom" ), Description( "Mouse wheel steps third-person CameraOffset.x in/out. Skipped while the game menu is open or build placement owns the wheel." )]
	public bool CameraScrollZoomEnabled { get; set; } = true;

	[Property, Group( "Camera" ), Title( "Zoom min distance" ), Range( 32f, 512f ), Step( 8f )]
	public float CameraZoomMinDistance { get; set; } = 96f;

	[Property, Group( "Camera" ), Title( "Zoom max distance" ), Range( 64f, 1600f ), Step( 8f )]
	public float CameraZoomMaxDistance { get; set; } = 800f;

	[Property, Group( "Camera" ), Title( "Zoom step" ), Range( 8f, 128f ), Step( 8f ), Description( "Distance change per wheel notch." )]
	public float CameraZoomStep { get; set; } = 48f;

	PlayerVitals _vitals;
	PlayerGrapple _grapple;
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

	/// <summary>
	/// While sprint is blocked, <see cref="PlayerController.RunSpeed"/> is forced down.
	/// While grappled in the air, walk+run speeds are muted to 0 so air-control cannot
	/// drag tangent speed toward WalkSpeed (that felt like a pump “cap”).
	/// </summary>
	bool _runSpeedOverrideActive;
	float _savedRunSpeed;
	bool _walkSpeedMuteActive;
	float _savedWalkSpeed;

	/// <summary>Host copy of the owning client’s sprint button, for <see cref="ShouldBlockStaminaRegenForAuthority"/> (local driver uses <see cref="Sandbox.Input"/> directly).</summary>
	bool _sprintHeldReportedOnHost;

	bool _sprintHeldReportedToHostLast;

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		_grapple = Components.Get<PlayerGrapple>();
		_controller = Components.Get<PlayerController>();
		if ( _vitals is null )
			Log.Warning( $"[PlayerMovement|{PlayerVitals.GetVitalsProcessRoleTag( GameObject )}] {GameObject.Name}: add PlayerVitals on this pawn — movement stamina hooks disabled." );
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

	/// <summary>Owner-side attach impulse — called when local <see cref="PlayerGrapple.IsAttached"/> rises.</summary>
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

	/// <summary>Sprint stamina only while Run is held and a WASD movement key is held.</summary>
	bool WantsSprintStaminaSpend()
	{
		if ( _vitals is null || string.IsNullOrWhiteSpace( SprintInputAction ) )
			return false;

		if ( IsSprintBlocked() )
			return false;

		if ( !Input.Down( SprintInputAction ) || _vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon ) )
			return false;

		return HasMovementSprintIntent();
	}

	/// <summary>
	/// Sprint only with feet on the ground. Mid-air (with or without rope) cannot Run —
	/// that was inventing foot propulsion while dangling. Grappled + grounded is allowed.
	/// </summary>
	bool IsSprintBlocked()
	{
		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller is null )
			return false;

		return !_controller.IsOnGround;
	}

	/// <summary>Soft-clear Run / AltMove so held Shift cannot stick as sprint this frame.</summary>
	void SuppressBlockedSprintInput()
	{
		if ( !IsSprintBlocked() )
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
	/// Even if Run input leaks past <see cref="SuppressBlockedSprintInput"/>, wish speed cannot
	/// sprint while airborne. While dangling mid-air, mute walk+run wish so MoveModeWalk air-control
	/// cannot invent foot propulsion — swing speed then only comes from rope pumps / prior ground run.
	/// Grappled + grounded leaves walk/run alone so feet can sprint and walk normally.
	/// </summary>
	void ApplyBlockedSprintRunSpeedOverride()
	{
		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller is null )
			return;

		if ( _grapple is null )
			_grapple = Components.Get<PlayerGrapple>();

		var grappleAir = _grapple is { IsAttached: true } && !_controller.IsOnGround;

		if ( grappleAir )
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

		if ( _walkSpeedMuteActive )
		{
			_controller.WalkSpeed = _savedWalkSpeed > 1f ? _savedWalkSpeed : 110f;
			_walkSpeedMuteActive = false;
		}

		if ( IsSprintBlocked() )
		{
			if ( !_runSpeedOverrideActive )
			{
				_savedRunSpeed = _controller.RunSpeed;
				_runSpeedOverrideActive = true;
			}

			_controller.RunSpeed = Math.Max( 0f, _controller.WalkSpeed );
			return;
		}

		RestoreBlockedSprintRunSpeed();
	}

	void RestoreBlockedSprintRunSpeed()
	{
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

		// Sneaking while sprinting doesn't stack — run wins for noise + stamina.
		if ( WantsSprintStaminaSpend() )
			return false;

		return HasMovementSprintIntent();
	}

	public void PreInput()
	{
		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		if ( JumpStaminaCost > 0f
		     && !_vitals.CanAffordStamina( JumpStaminaCost )
		     && ExhaustedJumpHeightFraction <= 0f )
			PlayerVitals.ClearJumpInputIfPressed( JumpInputAction );

		if ( _grapple is null )
			_grapple = Components.Get<PlayerGrapple>();

		// Jump while grappled: feet on ground only (same as sprint — no mid-air hop off the rope).
		if ( _grapple is { IsAttached: true } )
		{
			if ( _controller is null )
				_controller = Components.Get<PlayerController>();

			if ( _controller is not null && !_controller.IsOnGround )
				ClearActionIfPressed( JumpInputAction );
		}

		if ( !string.IsNullOrWhiteSpace( SprintInputAction ) )
		{
			if ( IsSprintBlocked() )
				SuppressBlockedSprintInput();
			else if ( SprintStaminaPerSecond > 0f && _vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon ) )
				ClearActionIfPressed( SprintInputAction );
		}

		ApplyBlockedSprintRunSpeedOverride();
	}

	public void OnJumped()
	{
		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		if ( _vitals.OnControllerJumpedForStaminaFromMovement( JumpStaminaCost, ExhaustedJumpHeightFraction ) )
			ApplyExhaustedJumpVelocityScale();
	}

	public void OnLanded( float distance, Vector3 impactVelocity )
	{
		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		_vitals.OnControllerLandedForJumpStaminaFromMovement( distance, impactVelocity );
	}

	/// <summary>
	/// After the built-in third-person camera (and its wall-trace ease), hard-place at our stepped zoom
	/// so zoom-out snaps the same way zoom-in does.
	/// </summary>
	void PlayerController.IEvents.PostCameraSetup( CameraComponent camera )
	{
		if ( !IsLocalMovementDriver() || !CameraScrollZoomEnabled || _cameraZoomDistance < 0f )
			return;

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null || !_controller.IsValid() || !_controller.ThirdPerson )
			return;

		if ( !camera.IsValid() )
			return;

		SnapThirdPersonCameraToZoomDistance( camera );
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

		if ( !IsLocalMovementDriver() )
			return;

		PollCameraScrollZoom();

		// Keep Run suppressed for the whole airborne/dangling frame (controller may re-read input after PreInput).
		SuppressBlockedSprintInput();
		ApplyBlockedSprintRunSpeedOverride();

		// Length winch runs in FixedUpdate only — applying it here too caused E/Q bobble.
		// Only catch large over-length desync after MoveModeWalk moves us.
		ApplyGrappleOverLengthCatchup();
		ApplyGrappleSwingPushAfterWalk( Time.Delta );
	}

	void PollCameraScrollZoom()
	{
		if ( !CameraScrollZoomEnabled )
			return;

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null || !_controller.IsValid() || !_controller.ThirdPerson )
			return;

		if ( Components.Get<PlayerGameMenuController>() is { IsMenuOpen: true } )
			return;

		var hammer = Components.Get<PlayerEquipment>()?.GetActiveTool<ToolBuildHammer>();
		if ( hammer is not null && hammer.IsPreviewingPlacePiece )
			return;

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

		var scroll = Input.MouseWheel.y;
		if ( MathF.Abs( scroll ) <= 0.01f )
			return;

		var notch = (float)Math.Round( (_cameraZoomDistance - min) / step );
		if ( scroll > 0f )
			notch -= 1f; // wheel up → zoom in (closer)
		else
			notch += 1f;

		var next = Math.Clamp( min + notch * step, min, max );
		_cameraZoomDistance = next;
		// Hard-place the camera so zoom-out does not ease via the controller's wall-trace recovery.
		ApplyCameraZoomDistance( snapView: true );
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
	void SnapThirdPersonCameraToZoomDistance( CameraComponent cam )
	{
		cam ??= BuildViewCamera.Resolve( GameObject );
		if ( cam is null || !cam.IsValid() || !Scene.IsValid() || _controller is null )
			return;

		var eye = GameObject.WorldPosition
		          + Vector3.Up * Math.Max( 8f, _controller.BodyHeight - _controller.EyeDistanceFromTop );
		var rot = cam.WorldRotation;
		var offset = _controller.CameraOffset;
		// Prefab CameraOffset is (back, height, side) — x is pull-back distance.
		var target = eye
		             - rot.Forward * _cameraZoomDistance
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

		// Physics step: re-clamp before MoveModeWalk air-control samples wish speed.
		SuppressBlockedSprintInput();
		ApplyBlockedSprintRunSpeedOverride();
		ApplyGrappleRopeConstraint( Time.Delta );
	}

	void DetectGrappleAttachEdge()
	{
		if ( _grapple is null )
			_grapple = Components.Get<PlayerGrapple>();

		if ( _grapple is null )
		{
			_grappleWasAttached = false;
			return;
		}

		if ( _grapple.IsAttached && !_grappleWasAttached )
			ApplyGrappleAttachVelocityScale( _grapple.AttachVelocityScale );

		if ( !_grapple.IsAttached )
			_grapplePrevRopeLength = 0f;

		_grappleWasAttached = _grapple.IsAttached;
	}

	void ApplyGrappleOverLengthCatchup()
	{
		if ( _grapple is null )
			_grapple = Components.Get<PlayerGrapple>();

		if ( _grapple is null || !_grapple.IsAttached || _grapple.RopeLengthEngine <= 1e-3f )
			return;

		var attach = _grapple.AttachWorldPoint;
		var maxLen = Math.Max( 1f, _grapple.RopeLengthEngine );
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
		if ( _grapple is null )
			_grapple = Components.Get<PlayerGrapple>();

		if ( _grapple is null || !_grapple.IsAttached || _grapple.RopeLengthEngine <= 1e-3f )
		{
			_grapplePrevRopeLength = 0f;
			return;
		}

		var body = ResolveGrappleBody();
		if ( body is null )
			return;

		dt = Math.Max( 1e-4f, dt );

		var attach = _grapple.AttachWorldPoint;
		var maxLen = Math.Max( 1f, _grapple.RopeLengthEngine );
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

		// Length-only winch: E/Q change RopeLengthEngine; we only enforce the hard sphere.
		// Dual position teleports were fighting MoveModeWalk and stalling the swing.
		if ( dist > maxLen )
		{
			GameObject.WorldPosition = attach + radial * maxLen;
			Transform.ClearInterpolation();

			// Shorten → conserve angular momentum (faster tangent on a tighter arc).
			if ( prevLen > maxLen + 1e-3f )
			{
				var scale = Math.Clamp( prevLen / maxLen, 1f, 1.45f );
				vTan *= scale;
				ClampTangentialSpeed( ref vTan, 2400f );
			}

			body.Velocity = vTan;

			pos = GameObject.WorldPosition;
			toPlayer = pos - attach;
			dist = toPlayer.Length;
			if ( dist > 1e-4f )
				radial = toPlayer / dist;
		}
		else if ( dist >= maxLen - 0.5f && vRad > 0f )
		{
			// Pay-out while taut: ease tangent as the arc grows.
			if ( maxLen > prevLen + 1e-3f )
			{
				var scale = Math.Clamp( prevLen / maxLen, 0.7f, 1f );
				vTan *= scale;
			}

			body.Velocity = vTan;
		}

		_grapplePrevRopeLength = maxLen;
		// Push runs in OnUpdate after MoveModeWalk so walk air-control does not eat the accel.
	}

	static void ClampTangentialSpeed( ref Vector3 vTan, float maxSpeed )
	{
		var speed = vTan.Length;
		if ( speed > maxSpeed && speed > 1e-4f )
			vTan *= maxSpeed / speed;
	}

	/// <summary>WASD swing thrust — call after the controller moves so velocity sticks.</summary>
	public void ApplyGrappleSwingPushAfterWalk( float dt )
	{
		if ( !IsLocalMovementDriver() )
			return;

		if ( _grapple is null )
			_grapple = Components.Get<PlayerGrapple>();

		if ( _grapple is null || !_grapple.IsAttached || _grapple.RopeLengthEngine <= 1e-3f )
			return;

		var body = ResolveGrappleBody();
		if ( body is null )
			return;

		var attach = _grapple.AttachWorldPoint;
		var toPlayer = GameObject.WorldPosition - attach;
		var dist = toPlayer.Length;
		if ( dist < 1e-4f )
			return;

		ApplyPendulumSwingPush( body, toPlayer / dist, Math.Max( 1e-4f, dt ) );
	}

	void ApplyPendulumSwingPush( Rigidbody body, Vector3 radial, float dt )
	{
		// Horizontal camera wish only — no camera-up loft (that was yanking arcs toward 45°).
		var wish = BuildHorizontalSwingWish( radial );
		var vel = body.Velocity;
		var vRadial = Vector3.Dot( vel, radial );
		var vTan = vel - radial * vRadial;

		if ( wish.LengthSquared < 1e-6f )
		{
			var damp = Math.Max( 0f, _grapple.SwingCoastDamping );
			if ( damp > 0f && vTan.LengthSquared > 1e-4f )
				body.Velocity = radial * vRadial + vTan * Math.Max( 0f, 1f - damp * dt );
			return;
		}

		wish = wish.Normal;
		var tanSpeed = vTan.Length;
		var tanDir = tanSpeed > 1e-3f ? vTan / tanSpeed : wish;
		var along = Vector3.Dot( tanDir, wish );
		var angleFromHang = Vector3.GetAngle( radial, Vector3.Down );
		var startAccel = Math.Max( 0f, _grapple.AirPushAcceleration );
		var alignMin = Math.Clamp( _grapple.PumpAlignDot, 0.01f, 0.5f );
		var minPumpSpeed = Math.Max( 1f, _grapple.PumpMinSpeed );

		// With the arc (W…S… timed with travel): compound pump.
		if ( along > alignMin && tanSpeed >= minPumpSpeed )
		{
			var gain = Math.Max( 0f, _grapple.PumpVelocityGainPerSecond );
			var scale = 1f + gain * dt;
			body.Velocity = radial * vRadial + tanDir * (tanSpeed * scale);
			return;
		}

		// Against the arc: coast by default. Optional FightBrakePerSecond if designers want scrub.
		if ( along < -alignMin && tanSpeed > 1e-3f )
		{
			var brake = Math.Max( 0f, _grapple.FightBrakePerSecond );
			if ( brake <= 1e-4f )
				return;

			var scale = MathF.Exp( -brake * dt );
			body.Velocity = radial * vRadial + tanDir * (tanSpeed * scale);
			return;
		}

		// Start from hang / low speed only — weak constant push, fades with angle + speed
		// so holding W cannot launch you up the arc.
		var holdAccel = startAccel * Math.Clamp( _grapple.HoldPushScale, 0f, 1f );
		var maxAng = Math.Max( 5f, _grapple.HoldMaxAngleDegrees );
		if ( angleFromHang > maxAng )
		{
			var fade = 1f - Math.Clamp( (angleFromHang - maxAng) / Math.Max( 8f, maxAng ), 0f, 1f );
			holdAccel *= fade;
		}

		var holdSoften = Math.Max( 20f, _grapple.SwingSpeedSoften );
		var holdFactor = 1f / ( 1f + tanSpeed / holdSoften );
		body.Velocity = radial * vRadial + vTan + wish * (holdAccel * holdFactor * dt);
	}

	/// <summary>
	/// WASD from camera yaw, projected onto the rope tangent plane.
	/// Stays mostly horizontal so pumps build ±angle around hang instead of lofting up the rope.
	/// </summary>
	Vector3 BuildHorizontalSwingWish( Vector3 radial )
	{
		var forward = Input.Down( "Forward" ) ? 1f : 0f;
		var back = Input.Down( "Backward" ) ? 1f : 0f;
		var left = Input.Down( "Left" ) ? 1f : 0f;
		var right = Input.Down( "Right" ) ? 1f : 0f;
		var x = right - left;
		var y = forward - back;
		if ( MathF.Abs( x ) < 1e-4f && MathF.Abs( y ) < 1e-4f )
			return Vector3.Zero;

		var cam = BuildViewCamera.Resolve( GameObject );
		var yaw = cam.IsValid() ? cam.WorldRotation.Angles().yaw : GameObject.WorldRotation.Angles().yaw;
		var yawRot = new Angles( 0f, yaw, 0f ).ToRotation();

		var wish = yawRot.Forward * y + yawRot.Right * x;
		wish -= radial * Vector3.Dot( wish, radial );
		return wish;
	}

	Rigidbody ResolveGrappleBody()
	{
		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller?.Body is not null && _controller.Body.IsValid() )
			return _controller.Body;

		return Components.Get<Rigidbody>();
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
