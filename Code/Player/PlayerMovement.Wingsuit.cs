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
	public float WingsuitPitchRate { get; set; } = 120f;

	[Property, Group( "Wingsuit" ), Title( "Roll rate (deg/s)" ), Range( 30f, 180f ), Step( 5f )]
	public float WingsuitRollRate { get; set; } = 160f;

	[Property, Group( "Wingsuit" ), Title( "Roll return (deg/s)" ), Range( 0f, 120f ), Step( 5f )]
	public float WingsuitRollReturn { get; set; } = 85f;

	[Property, Group( "Wingsuit" ), Title( "Min pitch (deg, nose up)" ), Range( -90f, 0f ), Step( 1f )]
	public float WingsuitMinPitch { get; set; } = -90f;

	[Property, Group( "Wingsuit" ), Title( "Max pitch (deg, nose down)" ), Range( 0f, 90f ), Step( 1f )]
	public float WingsuitMaxPitch { get; set; } = 90f;

	[Property, Group( "Wingsuit" ), Title( "Fallback open pitch (no camera)" ), Range( -20f, 60f ), Step( 1f )]
	public float WingsuitOpenPitch { get; set; } = 18f;

	[Property, Group( "Wingsuit" ), Title( "Max roll (deg)" ), Range( 15f, 75f ), Step( 1f )]
	public float WingsuitMaxRoll { get; set; } = 40f;

	[Property, Group( "Wingsuit" ), Title( "Bank turn rate (deg/s at full roll)" ), Range( 20f, 180f ), Step( 5f )]
	public float WingsuitBankTurnRate { get; set; } = 110f;

	[Property, Group( "Wingsuit" ), Title( "Steer authority (1/s)" ), Range( 0.5f, 8f ), Step( 0.1f )]
	public float WingsuitSteerAuthority { get; set; } = 6f;

	/// <summary>
	/// Control response multiplier at max speed (pitch / roll / bank-turn rates). 1 = speed never
	/// dulls the controls; lower = the faster you fly, the more you fight your own momentum to turn.
	/// Full response at stall speed, easing quadratically down to this floor at max speed.
	/// </summary>
	[Property, Group( "Wingsuit" ), Title( "Turn response at max speed (0-1)" ), Range( 0.05f, 1f ), Step( 0.05f )]
	public float WingsuitMinTurnResponse { get; set; } = 0.3f;

	/// <summary>Quadratic falloff: mid speeds keep most of their agility, the top end gets heavy.</summary>
	float GetWingsuitTurnResponse( float speed )
	{
		var floor = Math.Clamp( WingsuitMinTurnResponse, 0.05f, 1f );
		var span = Math.Max( 1f, WingsuitMaxSpeed - WingsuitStallSpeed );
		var t = Math.Clamp( (speed - WingsuitStallSpeed) / span, 0f, 1f );
		return 1f - (1f - floor) * t * t;
	}

	/// <summary>
	/// Dive speed cap ≈ 1.5× what a long freefall actually reaches (~3200 with gravity 850 and
	/// the pawn's 0.1 linear damping). Climb height scales with entry speed, so this cap also
	/// bounds how high a max-speed pull-up can go.
	/// </summary>
	[Property, Group( "Wingsuit" ), Title( "Max speed" ), Range( 500f, 8000f ), Step( 50f )]
	public float WingsuitMaxSpeed { get; set; } = 4800f;

	[Property, Group( "Wingsuit" ), Title( "Stall speed" ), Range( 50f, 800f ), Step( 10f )]
	public float WingsuitStallSpeed { get; set; } = 280f;

	[Property, Group( "Wingsuit" ), Title( "Base drag (1/s)" ), Range( 0f, 2f ), Step( 0.01f )]
	public float WingsuitBaseDrag { get; set; } = 0.025f;

	/// <summary>Small aerodynamic drag while climbing — gravity (climb cost) does the real work.</summary>
	[Property, Group( "Wingsuit" ), Title( "Nose-up bleed (1/s)" ), Range( 0f, 4f ), Step( 0.05f )]
	public float WingsuitNoseUpBleed { get; set; } = 0.05f;

	/// <summary>
	/// Fraction of gravity charged against airspeed while climbing. 1 = full ballistic trade:
	/// climb height is bounded by v²/2g, so how far you can pull up depends on entry speed.
	/// Must stay at or above <see cref="WingsuitDiveAccelScale"/> — if diving pays out more than
	/// climbing charges, the dive/climb pump generates free energy and the player never lands.
	/// Slightly above 1 so every pump cycle ends lower than it began.
	/// </summary>
	[Property, Group( "Wingsuit" ), Title( "Climb cost scale" ), Range( 0f, 2f ), Step( 0.05f )]
	public float WingsuitClimbCostScale { get; set; } = 1.15f;

	/// <summary>
	/// Straight-down dive gains speed at this multiple of gravity (freefall = 1.0). Keep at 1:
	/// anything above it mints energy against the climb cost (see <see cref="WingsuitClimbCostScale"/>).
	/// </summary>
	[Property, Group( "Wingsuit" ), Title( "Dive accel scale" ), Range( 0f, 3f ), Step( 0.05f )]
	public float WingsuitDiveAccelScale { get; set; } = 1f;

	/// <summary>
	/// Bonus speed near the efficient glide pitch, <b>only while genuinely descending</b> — it
	/// models a clean glide converting sink into airspeed, not an engine. It used to fire at level
	/// attitude too, where it beat base drag and held ~3200 u/s at zero sink forever: the core of
	/// the never-needs-to-land bug.
	/// </summary>
	[Property, Group( "Wingsuit" ), Title( "Cruise accel scale" ), Range( 0f, 1f ), Step( 0.01f )]
	public float WingsuitCruiseAccelScale { get; set; } = 0.22f;

	/// <summary>
	/// Forward distance per unit of height in level (blue) flight — the flight path tilts down by
	/// this ratio while airspeed stays untouched, so a level attitude still sinks and range is
	/// bounded by altitude without slowing travel. This is the "must come down" rule: bleeding
	/// <i>speed</i> in level flight ruined travel and kept altitude; a glide slope drains altitude
	/// and keeps travel. Fades out where a dive supplies its own descent and where a climb is
	/// paying the ballistic cost. Lesser wingsuit tiers lower this to shorten their legs.
	/// </summary>
	[Property, Group( "Wingsuit" ), Title( "Glide ratio (distance : height)" ), Range( 1f, 12f ), Step( 0.5f )]
	public float WingsuitGlideRatio { get; set; } = 5f;

	[Property, Group( "Wingsuit" ), Title( "Min airborne before deploy (s)" ), Range( 0.2f, 3f ), Step( 0.05f )]
	public float WingsuitMinAirborneSeconds { get; set; } = 0.9f;

	/// <summary>
	/// Nose-down (W) input is ignored this long after deploy. W is almost always still held from
	/// the sprint that carried the player off the cliff — for the first beat it means "I was
	/// running", not "dive", and honoring it slammed the opening pitch straight down.
	/// </summary>
	[Property, Group( "Wingsuit" ), Title( "Deploy dive-input grace (s)" ), Range( 0f, 2f ), Step( 0.05f )]
	public float WingsuitDeployForwardGraceSeconds { get; set; } = 0.5f;

	/// <summary>After stowing the wingsuit, it cannot be reopened for this long (no open-close flutter).</summary>
	[Property, Group( "Wingsuit" ), Title( "Redeploy cooldown (s)" ), Range( 0f, 10f ), Step( 0.5f )]
	public float WingsuitRedeployCooldownSeconds { get; set; } = 3f;

	/// <summary>
	/// After the stall nose-over reaches full dive, the wing holds off biting for this long — the
	/// player is in true gravity free fall, dropping and building fall speed, not in a steerable
	/// glide with locked input. Control and glide return together at the end, so the comeback
	/// timing feels identical while the height is already genuinely gone: a pull-up then has to
	/// turn real downward momentum instead of cancelling a dive that never fell.
	/// </summary>
	[Property, Group( "Wingsuit" ), Title( "Stall free-fall hold (s)" ), Range( 0f, 3f ), Step( 0.05f )]
	public float WingsuitStallFreefallHoldSeconds { get; set; } = 0.5f;

	[Property, Group( "Wingsuit" ), Title( "Crash min speed" ), Range( 50f, 2000f ), Step( 25f )]
	public float WingsuitCrashMinSpeed { get; set; } = 180f;

	[Property, Group( "Wingsuit" ), Title( "Crash head-on (0-1)" ), Range( 0.2f, 1f ), Step( 0.05f )]
	public float WingsuitCrashHeadOnDot { get; set; } = 0.45f;

	[Property, Group( "Wingsuit" ), Title( "Invert stall nose-up (0-1)" ), Range( 0.5f, 1f ), Step( 0.05f )]
	public float WingsuitInvertStallNoseUp { get; set; } = 0.82f;

	/// <summary>
	/// Optional extra drag past the invert threshold. Off by default: gravity (climb cost scale)
	/// spends the airspeed, and the plummet triggers once speed is genuinely gone. This drag
	/// scales with speed, so any meaningful value punishes fast climbs disproportionately.
	/// </summary>
	[Property, Group( "Wingsuit" ), Title( "Invert stall bleed (1/s)" ), Range( 0f, 8f ), Step( 0.1f )]
	public float WingsuitInvertStallBleed { get; set; } = 0f;

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

	/// <summary>When the suit last opened — anchors the deploy dive-input grace window.</summary>
	float _wingsuitDeployTime;

	/// <summary>When the nose-over reached full dive — anchors the free-fall hold before air bites.</summary>
	float _wingsuitNoseOverDoneTime = float.MinValue;

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

		// Swimming is not airborne — otherwise leaving water grants an instant deploy.
		if ( _controller.IsOnGround || _controller.IsSwimming )
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

		// The suit is for air — never deploys from water.
		if ( _controller.IsSwimming )
			return;

		// Block short hops — must be airborne longer than a normal jump.
		if ( _wingsuitAirborneSeconds < WingsuitMinAirborneSeconds )
			return;

		if ( !HasWingsuitEquipped() )
			return;

		if ( GrappleAttached )
			return;

		// Cooldown after a stow: leave the jump press for other systems (double-jump augments).
		if ( Time.Now - _wingsuitStowedTime < Math.Max( 0f, WingsuitRedeployCooldownSeconds ) )
			return;

		DeployWingsuit();
		ClearActionIfPressed( JumpInputAction );
	}

	float _wingsuitStowedTime = float.MinValue;

	void DeployWingsuit()
	{
		if ( WingsuitDeployed )
			return;

		if ( GrappleAttached )
			return;

		if ( Time.Now - _wingsuitStowedTime < Math.Max( 0f, WingsuitRedeployCooldownSeconds ) )
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
		_wingsuitDeployTime = Time.Now;
		WingsuitDeployed = true;
		Components.Get<PlayerQuests>()?.OwnerReport( QuestEventIds.WingsuitDeployed );

		// Snap the pawn onto the camera-facing glide attitude NOW — the first physics tick applies
		// the same rotation, but waiting for it showed the old facing for a beat at deploy.
		var deployGlideRot = BuildWingsuitGlideRotation();
		GameObject.WorldRotation = Rotation.LookAt( deployGlideRot.Forward.Normal, deployGlideRot.Up );
		Transform.ClearInterpolation();
		Components.Get<PlayerAnimation>()?.ReleaseCombatFacingOverride();
	}

	void StowWingsuit( bool keepMomentum )
	{
		if ( !WingsuitDeployed )
			return;

		WingsuitDeployed = false;
		_wingsuitStowedTime = Time.Now;
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
	/// Water entry while deployed: put the suit away and hand straight to the swim —
	/// velocity untouched so the pawn submerges exactly like a plain jump-in.
	/// </summary>
	void StowWingsuitIntoWater()
	{
		StowWingsuit( keepMomentum: true );
		// No land handoff in water — the swim mode owns the pawn from here.
		_wingsuitFreefallAwaitingLand = false;
		_wingsuitAirborneSeconds = 0f;
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

		// Splashdown during freefall: the swim owns the pawn — no land snap, velocity untouched.
		if ( _controller is not null && _controller.IsSwimming )
		{
			_wingsuitFreefallAwaitingLand = false;
			_wingsuitAirborneSeconds = 0f;
			return;
		}

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

		// Splashdown: water ends the flight, momentum carries into the swim.
		if ( _controller is not null && _controller.IsSwimming )
		{
			StowWingsuitIntoWater();
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

		// W still held from the sprint off the cliff is not a dive request — see the grace property.
		if ( pitchInput > 0f && Time.Now - _wingsuitDeployTime < WingsuitDeployForwardGraceSeconds )
			pitchInput = 0f;

		// Stalled: the suit has quit flying. Controls are dead while the nose drops through on its
		// own (see the plummet branch) — the player gets them back once air catches in the dive.
		if ( _wingsuitGravityPlummet )
		{
			pitchInput = 0f;
			rollInput = 0f;
		}

		var body = Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return;

		// Faster = heavier controls: the same stick input changes attitude less per second, so
		// turning at speed feels like fighting your own momentum.
		var turnResponse = GetWingsuitTurnResponse( body.Velocity.Length );

		_wingsuitPitch = Math.Clamp(
			_wingsuitPitch + pitchInput * WingsuitPitchRate * turnResponse * dt,
			WingsuitMinPitch,
			WingsuitMaxPitch );

		if ( MathF.Abs( rollInput ) > 1e-3f )
		{
			_wingsuitRoll = Math.Clamp(
				_wingsuitRoll + rollInput * WingsuitRollRate * turnResponse * dt,
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
		_wingsuitYaw -= bankFrac * WingsuitBankTurnRate * turnResponse * dt;

		var glideRot = BuildWingsuitGlideRotation();
		var nose = glideRot.Forward.Normal;

		var gravity = Scene.IsValid() ? Scene.PhysicsWorld.Gravity : Vector3.Down * 800f;
		if ( gravity.LengthSquared < 1e-4f )
			gravity = Vector3.Down * 800f;

		var gMag = gravity.Length;
		var gDir = gravity / gMag;
		var alongNose = Vector3.Dot( gDir, nose );
		var noseUpAmount = Math.Clamp( -alongNose, 0f, 1f );

		if ( TryWingsuitCrashImpact( body, body.Velocity, dt ) )
			return;

		// Hard inverted stall: tip straight up → trade speed for altitude, plummet only once
		// airspeed is actually spent (zoom climb, not an instant brake).
		var hardStallBledThisTick = false;
		if ( !_wingsuitGravityPlummet && noseUpAmount >= WingsuitInvertStallNoseUp )
		{
			var dump = MathF.Exp( -WingsuitInvertStallBleed * noseUpAmount * dt );
			var v = body.Velocity * dump;
			if ( v.Length < WingsuitStallSpeed * 0.35f )
			{
				_wingsuitGravityPlummet = true;
				_wingsuitNoseOverDoneTime = float.MinValue;
				SetWingsuitGlideGravity( body, glideNoGravity: false );
				body.Velocity = Vector3.Lerp( v, gDir * Math.Max( v.Length, 80f ), 0.55f );
				GameObject.WorldRotation = Rotation.LookAt( nose, glideRot.Up );
				Transform.ClearInterpolation();
				return;
			}

			body.Velocity = v;
			hardStallBledThisTick = true;
		}

		if ( _wingsuitGravityPlummet )
		{
			SetWingsuitGlideGravity( body, glideNoGravity: false );

			// The stalled suit noses over by itself, all the way to the full dive — the player
			// pushed past what the wing could hold, so the wing decides the exit. Twice the manual
			// pitch rate: a stall throws the nose down, it doesn't ease it. Gravity builds real
			// fall speed the whole way.
			_wingsuitPitch = Math.Min( _wingsuitPitch + WingsuitPitchRate * 2f * dt, WingsuitMaxPitch );
			glideRot = BuildWingsuitGlideRotation();
			nose = glideRot.Forward.Normal;

			GameObject.WorldRotation = Rotation.LookAt( nose, glideRot.Up );
			Transform.ClearInterpolation();

			// Bottom of the nose-over: the wing does not bite yet. Free fall holds for a beat so
			// the drop is already real — then glide and control return together, as a max-pitch
			// dive carrying the accumulated fall speed. Pulling back up (S) carries the player on
			// the heading they had, but now it has to turn genuine downward momentum.
			if ( _wingsuitPitch >= WingsuitMaxPitch - 0.5f )
			{
				if ( _wingsuitNoseOverDoneTime == float.MinValue )
					_wingsuitNoseOverDoneTime = Time.Now;

				if ( Time.Now - _wingsuitNoseOverDoneTime >= WingsuitStallFreefallHoldSeconds )
				{
					_wingsuitGravityPlummet = false;
					var fallSpeed = body.Velocity.Length;
					_wingsuitSpeedLimit = Math.Max( _wingsuitSpeedLimit, Math.Max( fallSpeed, WingsuitStallSpeed ) );
					SetWingsuitGlideGravity( body, glideNoGravity: true );
				}
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
			// Light aero drag; hard invert already bled this tick (skip so it doesn't double-dip).
			if ( !hardStallBledThisTick )
			{
				var climbK = WingsuitNoseUpBleed * (0.35f + 0.65f * noseUpAmount);
				if ( noseUpAmount > 0.55f )
					climbK *= 1f + 2f * (noseUpAmount - 0.55f);
				speed *= MathF.Exp( -climbK * dt );
			}

			// The real climb cost: gravity spends airspeed, so climb height scales with entry speed.
			speed = Math.Max( 0f, speed - gMag * noseUpAmount * WingsuitClimbCostScale * dt );
		}

		// Cruise assist only while sinking (nose ≳ 5° down) — descent is the suit's one fuel tank.
		var efficient = Math.Clamp( 1f - MathF.Abs( _wingsuitPitch - 22f ) / 40f, 0f, 1f );
		if ( efficient > 0.2f && alongNose > 0.08f )
			speed += gMag * WingsuitCruiseAccelScale * efficient * dt;

		speed *= MathF.Exp( -WingsuitBaseDrag * (1.05f - 0.55f * efficient) * dt );
		var speedCap = Math.Max( WingsuitMaxSpeed, _wingsuitSpeedLimit );
		speed = Math.Clamp( speed, 0f, speedCap );

		var stallRef = Math.Max( 1f, WingsuitStallSpeed );
		var speedRatio = Math.Clamp( speed / stallRef, 0f, 1.5f );
		var glideAuthority = Math.Clamp( 1f - MathF.Exp( -2.4f * speedRatio ), 0f, 1f );

		// Blue-phase glide slope: a level attitude flies a descending path. The direction tilts
		// down by 1/GlideRatio at the same airspeed — magnitude untouched, so travel speed is
		// preserved and only altitude drains. Strongest in the flat band, fading toward a dive
		// (which sinks by itself) and toward a climb (which is already paying gravity for height).
		var glideDir = nose;
		var blueness = 1f - Math.Clamp( MathF.Abs( alongNose ) / 0.35f, 0f, 1f );
		if ( blueness > 1e-3f && WingsuitGlideRatio > 0.1f )
			glideDir = ( nose + Vector3.Down * (blueness / WingsuitGlideRatio) ).Normal;

		var glideVel = glideDir * Math.Max( speed, 0f );
		var fallVel = vel + gravity * dt;
		vel = Vector3.Lerp( fallVel, glideVel, glideAuthority );

		var plummet = 1f - glideAuthority;
		if ( plummet > 1e-3f )
			vel += gravity * plummet * plummet * dt;

		// Stall is pure airspeed: below stall speed the wing cannot hold altitude and it noses over
		// into the gravity plummet — level flight included, no attitude or hold-time requirement.
		// Maintaining elevation therefore requires keeping speed above WingsuitStallSpeed. The steep
		// zoom-climb branch above is the one exception (its own deeper threshold, so trading speed
		// for height still works); the deploy grace stops a slow opening from stalling before air
		// catches the wing.
		if ( speed < WingsuitStallSpeed
		     && noseUpAmount < WingsuitInvertStallNoseUp
		     && Time.Now - _wingsuitDeployTime > Math.Max( 0.25f, WingsuitDeployForwardGraceSeconds ) )
		{
			_wingsuitGravityPlummet = true;
			_wingsuitNoseOverDoneTime = float.MinValue;
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
