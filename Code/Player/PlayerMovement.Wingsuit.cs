using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Wingsuit deploy / glide / stow. Owned by <see cref="PlayerMovement"/> (Commandment #1).
/// Deploys along look aim. Air stow enters freefall handoff until ground contact restores walk.
/// Grapple attach stows the suit. Near-ground probe stows even if IsOnGround lags.
/// Hard nose-up dumps speed and re-enables full gravity until tipped into a dive again.
/// </summary>
partial class PlayerMovement
{
	[Property, Group( "Wingsuit" ), Title( "Enabled" )]
	public bool WingsuitEnabled { get; set; } = true;

	[Property, Group( "Wingsuit" ), Title( "Pitch rate (deg/s)" ), Range( 30f, 180f ), Step( 5f )]
	public float WingsuitPitchRate { get; set; } = 75f;

	[Property, Group( "Wingsuit" ), Title( "Roll rate (deg/s)" ), Range( 30f, 180f ), Step( 5f )]
	public float WingsuitRollRate { get; set; } = 100f;

	[Property, Group( "Wingsuit" ), Title( "Roll return (deg/s)" ), Range( 0f, 120f ), Step( 5f )]
	public float WingsuitRollReturn { get; set; } = 55f;

	[Property, Group( "Wingsuit" ), Title( "Min pitch (deg, nose up)" ), Range( -80f, 0f ), Step( 1f )]
	public float WingsuitMinPitch { get; set; } = -65f;

	[Property, Group( "Wingsuit" ), Title( "Max pitch (deg, nose down)" ), Range( 0f, 85f ), Step( 1f )]
	public float WingsuitMaxPitch { get; set; } = 75f;

	[Property, Group( "Wingsuit" ), Title( "Fallback open pitch (no camera)" ), Range( -20f, 60f ), Step( 1f )]
	public float WingsuitOpenPitch { get; set; } = 18f;

	[Property, Group( "Wingsuit" ), Title( "Max roll (deg)" ), Range( 15f, 75f ), Step( 1f )]
	public float WingsuitMaxRoll { get; set; } = 40f;

	[Property, Group( "Wingsuit" ), Title( "Bank turn rate (deg/s at full roll)" ), Range( 20f, 180f ), Step( 5f )]
	public float WingsuitBankTurnRate { get; set; } = 110f;

	[Property, Group( "Wingsuit" ), Title( "Steer authority (1/s)" ), Range( 0.5f, 8f ), Step( 0.1f )]
	public float WingsuitSteerAuthority { get; set; } = 4f;

	[Property, Group( "Wingsuit" ), Title( "Max speed" ), Range( 500f, 8000f ), Step( 50f )]
	public float WingsuitMaxSpeed { get; set; } = 3200f;

	[Property, Group( "Wingsuit" ), Title( "Stall speed" ), Range( 50f, 800f ), Step( 10f )]
	public float WingsuitStallSpeed { get; set; } = 280f;

	[Property, Group( "Wingsuit" ), Title( "Base drag (1/s)" ), Range( 0f, 2f ), Step( 0.01f )]
	public float WingsuitBaseDrag { get; set; } = 0.025f;

	[Property, Group( "Wingsuit" ), Title( "Nose-up bleed (1/s)" ), Range( 0f, 4f ), Step( 0.05f )]
	public float WingsuitNoseUpBleed { get; set; } = 0.28f;

	[Property, Group( "Wingsuit" ), Title( "Climb cost scale" ), Range( 0f, 2f ), Step( 0.05f )]
	public float WingsuitClimbCostScale { get; set; } = 0.08f;

	[Property, Group( "Wingsuit" ), Title( "Dive accel scale" ), Range( 0f, 3f ), Step( 0.05f )]
	public float WingsuitDiveAccelScale { get; set; } = 1.35f;

	[Property, Group( "Wingsuit" ), Title( "Cruise accel scale" ), Range( 0f, 1f ), Step( 0.01f )]
	public float WingsuitCruiseAccelScale { get; set; } = 0.22f;

	[Property, Group( "Wingsuit" ), Title( "Min airborne before deploy (s)" ), Range( 0.2f, 3f ), Step( 0.05f )]
	public float WingsuitMinAirborneSeconds { get; set; } = 0.9f;

	[Property, Group( "Wingsuit" ), Title( "Crash min speed" ), Range( 50f, 2000f ), Step( 25f )]
	public float WingsuitCrashMinSpeed { get; set; } = 180f;

	[Property, Group( "Wingsuit" ), Title( "Crash head-on (0-1)" ), Range( 0.2f, 1f ), Step( 0.05f )]
	public float WingsuitCrashHeadOnDot { get; set; } = 0.45f;

	[Property, Group( "Wingsuit" ), Title( "Invert stall nose-up (0-1)" ), Range( 0.5f, 1f ), Step( 0.05f )]
	public float WingsuitInvertStallNoseUp { get; set; } = 0.82f;

	[Property, Group( "Wingsuit" ), Title( "Ground stow distance" ), Range( 8f, 80f ), Step( 1f )]
	public float WingsuitGroundStowDistance { get; set; } = 28f;

	[Property, Group( "Wingsuit" ), Title( "Debug glide triangle" )]
	public bool WingsuitDebugDrawEnabled { get; set; } = true;

	[Property, Group( "Wingsuit" ), Title( "Debug triangle length" ), Range( 20f, 300f ), Step( 5f )]
	public float WingsuitDebugTriangleLength { get; set; } = 110f;

	[Property, Group( "Wingsuit" ), Title( "Debug triangle width" ), Range( 20f, 300f ), Step( 5f )]
	public float WingsuitDebugTriangleWidth { get; set; } = 90f;

	[Sync] public bool WingsuitDeployed { get; private set; }

	/// <summary>Glide pitch (deg). Negative = nose up, positive = nose down.</summary>
	float _wingsuitPitch;

	float _wingsuitRoll;
	float _wingsuitYaw;
	float _wingsuitSpeedLimit;
	float _wingsuitAirborneSeconds;
	bool _wingsuitSavedGravity = true;
	bool _wingsuitGravityOverrideActive;
	/// <summary>True after a hard nose-up stall — full gravity until tipped back into a dive.</summary>
	bool _wingsuitGravityPlummet;

	/// <summary>
	/// After air-stow / crash: not gliding, but still waiting for ground to re-enter walk.
	/// Keeps freefall connected to land (Space-stow used to clear Deployed and ignore ground).
	/// </summary>
	bool _wingsuitFreefallAwaitingLand;

	public bool IsWingsuitDeployed => WingsuitDeployed;

	bool HasWingsuitEquipped()
	{
		var equipment = Components.Get<PlayerEquipment>();
		if ( equipment is null )
			return false;

		var id = equipment.GetSlotResourceId( EquipmentSlot.Wingsuit );
		if ( string.IsNullOrWhiteSpace( id ) )
			return false;

		return EquipmentCatalog.HasAction( id, EquippedItemActions.Wingsuit )
		       || id.Contains( "wingsuit", StringComparison.OrdinalIgnoreCase );
	}

	void TickWingsuitAirborneTimer( float dt )
	{
		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller is null )
			return;

		if ( _controller.IsOnGround )
		{
			_wingsuitAirborneSeconds = 0f;
			return;
		}

		_wingsuitAirborneSeconds += Math.Max( 0f, dt );
	}

	void TickWingsuitJumpGate()
	{
		if ( !WingsuitEnabled || !IsLocalMovementDriver() )
			return;

		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( WingsuitDeployed )
		{
			if ( Input.Pressed( JumpInputAction ) )
			{
				StowWingsuit( keepMomentum: true );
				ClearActionIfPressed( JumpInputAction );
			}
			else
				ClearActionIfPressed( JumpInputAction );

			return;
		}

		if ( !Input.Pressed( JumpInputAction ) )
			return;

		if ( _controller is null || _controller.IsOnGround )
			return;

		// Block short hops — must be airborne longer than a normal jump.
		if ( _wingsuitAirborneSeconds < WingsuitMinAirborneSeconds )
			return;

		if ( !HasWingsuitEquipped() )
			return;

		if ( GrappleAttached )
			return;

		DeployWingsuit();
		ClearActionIfPressed( JumpInputAction );
	}

	void DeployWingsuit()
	{
		if ( WingsuitDeployed )
			return;

		if ( GrappleAttached )
			return;

		var body = Components.Get<Rigidbody>();
		var carryVel = body is not null && body.IsValid() ? body.Velocity : Vector3.Zero;
		var carrySpeed = carryVel.Length;

		if ( body is not null && body.IsValid() )
		{
			_wingsuitSavedGravity = body.Gravity;
			body.Gravity = false;
			_wingsuitGravityOverrideActive = true;
			body.Velocity = carryVel;
		}

		_wingsuitSpeedLimit = Math.Max( WingsuitMaxSpeed, carrySpeed );
		_wingsuitRoll = 0f;
		_wingsuitGravityPlummet = false;

		var yaw = GameObject.WorldRotation.Angles().yaw;
		var pitch = WingsuitOpenPitch;
		var cam = BuildViewCamera.Resolve( GameObject );
		if ( cam.IsValid() )
		{
			var look = cam.WorldRotation;
			yaw = look.Angles().yaw;
			var fwd = look.Forward.Normal;
			pitch = MathF.Asin( Math.Clamp( -fwd.z, -1f, 1f ) ) * (180f / MathF.PI);
		}

		_wingsuitYaw = yaw;
		_wingsuitPitch = Math.Clamp( pitch, WingsuitMinPitch, WingsuitMaxPitch );
		_wingsuitFreefallAwaitingLand = false;
		WingsuitDeployed = true;
	}

	void StowWingsuit( bool keepMomentum )
	{
		if ( !WingsuitDeployed )
			return;

		WingsuitDeployed = false;
		_wingsuitPitch = 0f;
		_wingsuitRoll = 0f;
		_wingsuitSpeedLimit = 0f;
		_wingsuitGravityPlummet = false;

		var body = Components.Get<Rigidbody>();
		if ( body is not null && body.IsValid() )
		{
			body.Gravity = true;
			_wingsuitGravityOverrideActive = false;
			if ( !keepMomentum )
				body.Velocity = Vector3.Zero;
		}

		UprightWingsuitBodyYaw();
		RestoreBlockedSprintRunSpeed();

		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		// Air dismount → freefall until we touch ground (don't drop ground logic with Deployed).
		_wingsuitFreefallAwaitingLand = _controller is null || !_controller.IsOnGround;
		if ( !_wingsuitFreefallAwaitingLand )
			CompleteWingsuitLand();
	}

	/// <summary>
	/// Ground contact while still deployed: stow then finish the land handoff.
	/// </summary>
	void StowWingsuitOnGround()
	{
		if ( WingsuitDeployed )
			StowWingsuit( keepMomentum: false );

		CompleteWingsuitLand();
	}

	/// <summary>
	/// Freefall after air-stow: keep gravity/upright until ground, then re-enter walk.
	/// </summary>
	void TickWingsuitFreefallHandoff()
	{
		if ( !_wingsuitFreefallAwaitingLand || WingsuitDeployed || !IsLocalMovementDriver() )
			return;

		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		var body = Components.Get<Rigidbody>();
		if ( body is not null && body.IsValid() && !body.Gravity )
			body.Gravity = true;

		UprightWingsuitBodyYaw();

		var onGround = _controller is not null && _controller.IsOnGround;
		// Tight touch only — do not use the 28m glide skim distance (that would snap from altitude).
		if ( onGround || TryGetWingsuitGroundHit( out _, out var touch, maxAbove: GetWingsuitLandTouchDistance() ) && touch )
			CompleteWingsuitLand();
	}

	float GetWingsuitLandTouchDistance()
	{
		if ( _controller is null )
			_controller = Components.Get<PlayerController>();
		if ( _controller is null )
			return 10f;
		return Math.Clamp( _controller.BodyHeight * 0.2f, 6f, 14f );
	}

	/// <summary>
	/// Kill glide slide, snap to surface, restore walk — end of freefall / ground stow.
	/// </summary>
	void CompleteWingsuitLand()
	{
		_wingsuitFreefallAwaitingLand = false;
		_wingsuitAirborneSeconds = 0f;

		TryGetWingsuitGroundHit( out var groundHit, out var hasGround, maxAbove: WingsuitGroundStowDistance );

		var body = Components.Get<Rigidbody>();
		if ( body is not null && body.IsValid() )
		{
			body.Gravity = true;
			body.Velocity = Vector3.Down * 40f;
		}

		UprightWingsuitBodyYaw();

		if ( hasGround )
		{
			var lift = _controller is not null ? Math.Max( 1f, _controller.BodyRadius * 0.15f ) : 2f;
			GameObject.WorldPosition = groundHit.HitPosition + groundHit.Normal * lift;
			Transform.ClearInterpolation();
		}

		RestoreBlockedSprintRunSpeed();
	}

	/// <summary>Glide tips the whole pawn — PlayerController needs upright yaw to ground properly.</summary>
	void UprightWingsuitBodyYaw()
	{
		var yaw = GameObject.WorldRotation.Angles().yaw;
		var cam = BuildViewCamera.Resolve( GameObject );
		if ( cam.IsValid() )
			yaw = cam.WorldRotation.Angles().yaw;
		else if ( MathF.Abs( _wingsuitYaw ) > 1e-3f )
			yaw = _wingsuitYaw;

		GameObject.WorldRotation = Rotation.FromYaw( yaw );
		Transform.ClearInterpolation();
	}

	bool TryGetWingsuitGroundHit( out SceneTraceResult tr, out bool hitGround, float? maxAbove = null )
	{
		tr = default;
		hitGround = false;

		if ( !Scene.IsValid() )
			return false;

		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		var limit = maxAbove ?? WingsuitGroundStowDistance;
		var bodyHeight = _controller is not null ? Math.Max( 24f, _controller.BodyHeight ) : 72f;
		var radius = _controller is not null ? Math.Max( 4f, _controller.BodyRadius * 0.35f ) : 8f;
		var probe = Math.Max( radius * 2f, limit );
		var origin = GameObject.WorldPosition + Vector3.Up * (bodyHeight * 0.5f);
		var end = origin + Vector3.Down * (bodyHeight * 0.5f + probe);

		tr = Scene.Trace.Sphere( radius, origin, end )
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "player", "trigger" )
			.Run();

		if ( !tr.Hit || Vector3.Dot( tr.Normal, Vector3.Up ) < 0.35f )
			return false;

		var above = Vector3.Dot( GameObject.WorldPosition - tr.HitPosition, Vector3.Up );
		if ( above > limit )
			return false;

		hitGround = true;
		return true;
	}

	bool IsWingsuitNearGround() =>
		TryGetWingsuitGroundHit( out _, out var hit, maxAbove: WingsuitGroundStowDistance ) && hit;

	void SetWingsuitGlideGravity( Rigidbody body, bool glideNoGravity )
	{
		if ( body is null || !body.IsValid() )
			return;

		if ( glideNoGravity )
		{
			if ( !_wingsuitGravityOverrideActive )
				_wingsuitSavedGravity = body.Gravity;
			body.Gravity = false;
			_wingsuitGravityOverrideActive = true;
		}
		else
		{
			if ( _wingsuitGravityOverrideActive )
			{
				body.Gravity = true;
				_wingsuitGravityOverrideActive = false;
			}
			else
				body.Gravity = true;
		}
	}

	void TickWingsuitFlight( float dt )
	{
		if ( !WingsuitDeployed || !IsLocalMovementDriver() )
			return;

		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller is not null && _controller.IsOnGround )
		{
			StowWingsuitOnGround();
			return;
		}

		if ( GrappleAttached )
		{
			StowWingsuit( keepMomentum: true );
			return;
		}

		if ( IsWingsuitNearGround() )
		{
			StowWingsuitOnGround();
			return;
		}

		dt = Math.Max( 1e-4f, dt );

		// W = tip nose forward (dive / gain speed). S = tip nose back (climb / lose speed).
		var pitchInput = (Input.Down( "Forward" ) ? 1f : 0f) + (Input.Down( "Backward" ) ? -1f : 0f);
		var rollInput = (Input.Down( "Left" ) ? -1f : 0f) + (Input.Down( "Right" ) ? 1f : 0f);

		_wingsuitPitch = Math.Clamp(
			_wingsuitPitch + pitchInput * WingsuitPitchRate * dt,
			WingsuitMinPitch,
			WingsuitMaxPitch );

		if ( MathF.Abs( rollInput ) > 1e-3f )
		{
			_wingsuitRoll = Math.Clamp(
				_wingsuitRoll + rollInput * WingsuitRollRate * dt,
				-WingsuitMaxRoll,
				WingsuitMaxRoll );
		}
		else if ( WingsuitRollReturn > 0f )
		{
			var step = WingsuitRollReturn * dt;
			if ( MathF.Abs( _wingsuitRoll ) <= step )
				_wingsuitRoll = 0f;
			else
				_wingsuitRoll -= MathF.Sign( _wingsuitRoll ) * step;
		}

		var bankFrac = WingsuitMaxRoll > 1e-3f ? _wingsuitRoll / WingsuitMaxRoll : 0f;
		_wingsuitYaw -= bankFrac * WingsuitBankTurnRate * dt;

		var glideRot = BuildWingsuitGlideRotation();
		var nose = glideRot.Forward.Normal;

		var body = Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return;

		var gravity = Scene.IsValid() ? Scene.PhysicsWorld.Gravity : Vector3.Down * 800f;
		if ( gravity.LengthSquared < 1e-4f )
			gravity = Vector3.Down * 800f;

		var gMag = gravity.Length;
		var gDir = gravity / gMag;
		var alongNose = Vector3.Dot( gDir, nose );
		var noseUpAmount = Math.Clamp( -alongNose, 0f, 1f );

		if ( TryWingsuitCrashImpact( body, body.Velocity, dt ) )
			return;

		// Hard inverted stall: tip straight up → dump speed → full gravity plummet.
		if ( !_wingsuitGravityPlummet && noseUpAmount >= WingsuitInvertStallNoseUp )
		{
			var dump = MathF.Exp( -6.5f * noseUpAmount * dt );
			var v = body.Velocity * dump;
			if ( v.Length < WingsuitStallSpeed * 0.35f || noseUpAmount >= 0.92f )
			{
				_wingsuitGravityPlummet = true;
				SetWingsuitGlideGravity( body, glideNoGravity: false );
				body.Velocity = Vector3.Lerp( v, gDir * Math.Max( v.Length, 80f ), 0.55f );
				GameObject.WorldRotation = Rotation.LookAt( nose, glideRot.Up );
				Transform.ClearInterpolation();
				return;
			}

			body.Velocity = v;
		}

		if ( _wingsuitGravityPlummet )
		{
			SetWingsuitGlideGravity( body, glideNoGravity: false );
			GameObject.WorldRotation = Rotation.LookAt( nose, glideRot.Up );
			Transform.ClearInterpolation();

			// Tip back into a dive to catch air again with whatever fall speed you have.
			if ( alongNose > 0.18f )
			{
				_wingsuitGravityPlummet = false;
				var fallSpeed = body.Velocity.Length;
				_wingsuitSpeedLimit = Math.Max( _wingsuitSpeedLimit, Math.Max( fallSpeed, WingsuitStallSpeed ) );
				SetWingsuitGlideGravity( body, glideNoGravity: true );
			}

			return;
		}

		SetWingsuitGlideGravity( body, glideNoGravity: true );

		var vel = body.Velocity;
		var speed = vel.Length;

		if ( speed >= 1f )
		{
			var desired = nose * speed;
			var steer = 1f - MathF.Exp( -WingsuitSteerAuthority * dt );
			vel = Vector3.Lerp( vel, desired, steer );
			speed = vel.Length;
		}

		if ( alongNose > 0f )
		{
			speed += gMag * alongNose * WingsuitDiveAccelScale * dt;
		}
		else if ( noseUpAmount > 1e-3f && speed > 1f )
		{
			// Mild climb bleed; hard invert handled above.
			var climbK = WingsuitNoseUpBleed * (0.35f + 0.65f * noseUpAmount);
			if ( noseUpAmount > 0.55f )
				climbK *= 1f + 3.5f * (noseUpAmount - 0.55f);
			speed *= MathF.Exp( -climbK * dt );
			speed = Math.Max( 0f, speed - gMag * noseUpAmount * WingsuitClimbCostScale * dt );
		}

		var efficient = Math.Clamp( 1f - MathF.Abs( _wingsuitPitch - 22f ) / 40f, 0f, 1f );
		if ( efficient > 0.2f && alongNose >= -0.05f )
			speed += gMag * WingsuitCruiseAccelScale * efficient * dt;

		speed *= MathF.Exp( -WingsuitBaseDrag * (1.05f - 0.55f * efficient) * dt );
		var speedCap = Math.Max( WingsuitMaxSpeed, _wingsuitSpeedLimit );
		speed = Math.Clamp( speed, 0f, speedCap );

		var stallRef = Math.Max( 1f, WingsuitStallSpeed );
		var speedRatio = Math.Clamp( speed / stallRef, 0f, 1.5f );
		var glideAuthority = Math.Clamp( 1f - MathF.Exp( -2.4f * speedRatio ), 0f, 1f );

		var glideVel = nose * Math.Max( speed, 0f );
		var fallVel = vel + gravity * dt;
		vel = Vector3.Lerp( fallVel, glideVel, glideAuthority );

		var plummet = 1f - glideAuthority;
		if ( plummet > 1e-3f )
			vel += gravity * plummet * plummet * dt;

		// Soft stall with no airspeed → commit to gravity plummet (same as invert).
		if ( speed < WingsuitStallSpeed * 0.25f && noseUpAmount > 0.35f )
		{
			_wingsuitGravityPlummet = true;
			SetWingsuitGlideGravity( body, glideNoGravity: false );
			body.Velocity = vel;
			GameObject.WorldRotation = Rotation.LookAt( nose, glideRot.Up );
			Transform.ClearInterpolation();
			return;
		}

		body.Velocity = vel;
		GameObject.WorldRotation = Rotation.LookAt( nose, glideRot.Up );
		Transform.ClearInterpolation();
	}

	/// <summary>
	/// Head-on hit while fast: dump all glide speed, stow the suit, plummet under gravity.
	/// Skips floor-like hits (normal mostly up) — those use the near-ground stow path.
	/// </summary>
	bool TryWingsuitCrashImpact( Rigidbody body, Vector3 vel, float dt )
	{
		if ( !Scene.IsValid() || body is null || !body.IsValid() )
			return false;

		var speed = vel.Length;
		if ( speed < WingsuitCrashMinSpeed )
			return false;

		var dir = vel / speed;
		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		var radius = _controller is not null ? Math.Max( 10f, _controller.BodyRadius * 0.85f ) : 14f;
		var height = _controller is not null ? Math.Max( 24f, _controller.BodyHeight * 0.45f ) : 36f;
		var origin = GameObject.WorldPosition + Vector3.Up * height;
		var probe = Math.Max( radius * 1.25f, speed * Math.Max( dt, 1e-3f ) * 2.5f + radius );

		var tr = Scene.Trace.Sphere( radius, origin, origin + dir * probe )
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "player", "trigger" )
			.Run();

		if ( !tr.Hit )
			return false;

		// Floor / gentle ground — leave to OnLanded.
		if ( Vector3.Dot( tr.Normal, Vector3.Up ) > 0.7f )
			return false;

		var headOn = -Vector3.Dot( dir, tr.Normal );
		if ( headOn < WingsuitCrashHeadOnDot )
			return false;

		StowWingsuit( keepMomentum: false );
		// Dead stop then drop — gravity is restored by Stow.
		if ( body.IsValid() )
			body.Velocity = Vector3.Down * 120f;

		return true;
	}

	Rotation BuildWingsuitGlideRotation() =>
		Rotation.FromYaw( _wingsuitYaw )
		* Rotation.FromPitch( _wingsuitPitch )
		* Rotation.FromRoll( _wingsuitRoll );

	void TickWingsuitDebugDraw()
	{
		if ( !WingsuitDebugDrawEnabled || !IsLocalMovementDriver() )
			return;

		if ( !WingsuitDeployed )
		{
			if ( _wingsuitFreefallAwaitingLand )
			{
				DebugOverlay.ScreenText(
					new Vector2( 24f, 220f ),
					"[ Wingsuit ] freefall → waiting for ground",
					size: 12f );
			}
			else if ( _wingsuitAirborneSeconds > 0f && HasWingsuitEquipped() )
			{
				var ready = _wingsuitAirborneSeconds >= WingsuitMinAirborneSeconds;
				DebugOverlay.ScreenText(
					new Vector2( 24f, 220f ),
					ready
						? $"[ Wingsuit ] ready  air {_wingsuitAirborneSeconds:0.00}s"
						: $"[ Wingsuit ] wait  air {_wingsuitAirborneSeconds:0.00}s / {WingsuitMinAirborneSeconds:0.00}s",
					size: 12f );
			}

			return;
		}

		var glideRot = BuildWingsuitGlideRotation();
		var origin = GameObject.WorldPosition + Vector3.Up * 40f;
		var forward = glideRot.Forward;
		var right = glideRot.Right;

		var length = Math.Max( 10f, WingsuitDebugTriangleLength );
		var halfWidth = Math.Max( 5f, WingsuitDebugTriangleWidth * 0.5f );

		// Flat triangle in the glide plane: tip = nose, base behind the player.
		var tip = origin + forward * (length * 0.65f);
		var left = origin - forward * (length * 0.35f) - right * halfWidth;
		var rightPt = origin - forward * (length * 0.35f) + right * halfWidth;

		var noseDown = _wingsuitPitch > 2f;
		var noseUp = _wingsuitPitch < -2f;
		var color = noseDown
			? new Color( 0.25f, 0.95f, 0.35f )
			: noseUp
				? new Color( 0.95f, 0.45f, 0.2f )
				: new Color( 0.35f, 0.75f, 1f );

		DebugOverlay.Line( tip, left, color, 0f );
		DebugOverlay.Line( left, rightPt, color, 0f );
		DebugOverlay.Line( rightPt, tip, color, 0f );
		DebugOverlay.Line( origin, tip, color.WithAlpha( 0.85f ), 0f );
		DebugOverlay.Sphere( new Sphere( tip, 4f ), color, 0f );

		var body = Components.Get<Rigidbody>();
		var speed = body is not null && body.IsValid() ? body.Velocity.Length : 0f;
		var x = 24f;
		var y = 220f;
		DebugOverlay.ScreenText( new Vector2( x, y ), "[ Wingsuit ]", size: 12f );
		y += 16f;
		DebugOverlay.ScreenText( new Vector2( x, y ), $"pitch {_wingsuitPitch:0}°  (W dive / S climb)", size: 13f );
		y += 16f;
		DebugOverlay.ScreenText( new Vector2( x, y ), $"bank  {_wingsuitRoll:0}°  (into turn)", size: 13f );
		y += 16f;
		DebugOverlay.ScreenText( new Vector2( x, y ), $"speed {speed:0}", size: 13f );
	}
}
