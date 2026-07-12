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

	/// <summary>
	/// Optional per-player stamina regen delay override in seconds. Use a value >= 0 to override
	/// <see cref="VitalsAuthority.StaminaRegenDelaySeconds"/> for this pawn; negative values use authority default.
	/// </summary>
	[Property, Group( "Stamina - Regen" )] public float StaminaRegenDelayOverrideSeconds { get; set; } = -1f;

	PlayerVitals _vitals;
	PlayerGrapple _grapple;
	PlayerController _controller;
	bool _sprintWasDown;
	float _sprintDebtPending;
	bool _grappleWasAttached;
	float _grapplePrevRopeLength;

	/// <summary>
	/// Set when we jump while grappled: sprint stays allowed until we land again
	/// (rope-dangling without a jump still blocks sprint).
	/// </summary>
	bool _sprintAllowedFromGrappleJump;

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

		if ( GameObject.Network is { Active: true } n && !n.IsOwner )
			return false;

		return true;
	}

	/// <summary>Pulls accumulated sprint preview debt and clears it. Merged into negative stamina on <see cref="PlayerVitals.RequestVitalsDelta"/> / <see cref="VitalsAuthority.TryApplyDeltas"/>.</summary>
	public float TakePendingSprintStaminaDebt()
	{
		var d = _sprintDebtPending;
		_sprintDebtPending = 0f;
		return d;
	}

	/// <summary>Unsynced sprint preview total (authority pool estimate ≈ <see cref="PlayerVitals.CurrentStamina"/> + this).</summary>
	public float PeekPendingSprintStaminaDebt() => _sprintDebtPending;

	/// <summary>Stamina regen on the host must not run while this pawn is sprinting here — authority stamina can lag behind preview until sprint flush / merged spends.</summary>
	public bool ShouldBlockStaminaRegenForAuthority()
	{
		if ( string.IsNullOrWhiteSpace( SprintInputAction ) || SprintStaminaPerSecond <= 0f || _vitals is null )
			return false;
		if ( IsLocalMovementDriver() )
			return WantsSprintStaminaSpend();
		return _sprintHeldReportedOnHost;
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
	/// Block sprint while airborne, except a jump that left the ground while already grappled
	/// (feet left via jump, not because the rope is holding our weight).
	/// </summary>
	bool IsSprintBlocked()
	{
		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller is null || _controller.IsOnGround )
		{
			_sprintAllowedFromGrappleJump = false;
			return false;
		}

		// Grapple + jump: keep run through the hop and onto the next landing.
		if ( _sprintAllowedFromGrappleJump )
			return false;

		// Normal mid-air, or dangling on the rope without a jump.
		return true;
	}

	static bool HasMovementSprintIntent() =>
		Input.Down( "Forward" )
		|| Input.Down( "Backward" )
		|| Input.Down( "Left" )
		|| Input.Down( "Right" );

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

		// Airborne while grappling: jump does nothing (ground + slack still allows jump).
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
			{
				// Soft suppress only — never ReleaseAction, or landing drops a held Shift.
				if ( Input.Down( SprintInputAction ) || Input.Pressed( SprintInputAction ) )
					Input.SetAction( SprintInputAction, false );
			}
			else if ( SprintStaminaPerSecond > 0f && _vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon ) )
			{
				ClearActionIfPressed( SprintInputAction );
			}
		}
	}

	public void OnJumped()
	{
		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		if ( _grapple is null )
			_grapple = Components.Get<PlayerGrapple>();

		// Jump while grappled (and able to leave the ground) — sprint may continue.
		if ( _grapple is { IsAttached: true } )
			_sprintAllowedFromGrappleJump = true;

		if ( _vitals.OnControllerJumpedForStaminaFromMovement( JumpStaminaCost, ExhaustedJumpHeightFraction ) )
			ApplyExhaustedJumpVelocityScale();
	}

	public void OnLanded( float distance, Vector3 impactVelocity )
	{
		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		_sprintAllowedFromGrappleJump = false;
		_vitals.OnControllerLandedForJumpStaminaFromMovement( distance, impactVelocity );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( !IsLocalMovementDriver() )
			return;

		if ( _vitals is not null )
		{
			UpdateSprintStaminaHoldAndFlushOnRelease();
			DetectGrappleAttachEdge();
		}

		// Keep Run suppressed for the whole airborne/dangling frame (controller may re-read input after PreInput).
		if ( !string.IsNullOrWhiteSpace( SprintInputAction ) && IsSprintBlocked()
		     && (Input.Down( SprintInputAction ) || Input.Pressed( SprintInputAction )) )
			Input.SetAction( SprintInputAction, false );

		// Length winch runs in FixedUpdate only — applying it here too caused E/Q bobble.
		// Only catch large over-length desync after MoveModeWalk moves us.
		ApplyGrappleOverLengthCatchup();
		ApplyGrappleSwingPushAfterWalk( Time.Delta );
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();

		if ( !IsLocalMovementDriver() )
			return;

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
		var along = tanSpeed > 1e-3f ? Vector3.Dot( vTan / tanSpeed, wish ) : 0f;
		var angleFromHang = Vector3.GetAngle( radial, Vector3.Down );

		var accel = Math.Max( 0f, _grapple.AirPushAcceleration );
		float speedSoften;

		if ( along > 0.25f && tanSpeed >= Math.Max( 1f, _grapple.PumpMinSpeed ) )
		{
			// Timed pump with the arc — builds velocity without needing a strong hold thrust.
			accel *= Math.Max( 1f, _grapple.PumpWithArcMult );
			speedSoften = Math.Max( 40f, _grapple.PumpSpeedSoften );
		}
		else if ( along < -0.15f && tanSpeed > 1e-3f )
		{
			// Fighting the arc bleeds momentum.
			accel *= Math.Max( 1f, _grapple.FightSwingBrakeMult );
			speedSoften = Math.Max( 40f, _grapple.SwingSpeedSoften );
		}
		else
		{
			// Hold / start from rest — weak, and fades past hold max angle (~15°) so you can't park at 45°.
			accel *= Math.Clamp( _grapple.HoldPushScale, 0f, 1f );
			var maxAng = Math.Max( 5f, _grapple.HoldMaxAngleDegrees );
			if ( angleFromHang > maxAng )
			{
				var fade = 1f - Math.Clamp( (angleFromHang - maxAng) / Math.Max( 8f, maxAng ), 0f, 1f );
				accel *= fade;
			}

			speedSoften = Math.Max( 40f, _grapple.SwingSpeedSoften );
		}

		var speedFactor = 1f / ( 1f + tanSpeed / speedSoften );
		body.Velocity += wish * (accel * speedFactor * dt);
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
		base.OnDestroy();
	}
}
