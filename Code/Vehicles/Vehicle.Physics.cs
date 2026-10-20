using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Driving model: one <see cref="Rigidbody"/> chassis on four raycast springs (no wheel bodies). Every
/// physics step the simulating machine (host, or the driver once it owns the vehicle) traces straight
/// down from each wheel mount, pushes the chassis up with a spring + damper sized from
/// <see cref="SuspensionFrequencyHz"/> / <see cref="SuspensionDampingRatio"/> and the chassis mass, kills
/// sideways slip at each grounded wheel and pushes along the wheel's forward for throttle / brake.
/// Tyre forces are plain friction (Mark 2026-10-04: rollover must come out of the physics, not a
/// scripted roll): each wheel's drive + side force acts at its ground contact and is capped at
/// <see cref="TyreGripCoefficient"/> × that wheel's own spring load. A hard turn shifts load onto the
/// outside wheels; whether the chassis then tips, slides or rolls over its cage collider falls out of
/// speed, steering angle, slope, bumps, mass-centre height and track width. The upright pull only runs
/// when truly airborne (jumps), so a buggy lying on its side or roof stays there.
///
/// Controls (driver only): W accelerates up to <see cref="MaxSpeedMetersPerSecond"/>; A / D steer;
/// S brakes while rolling forward and only becomes reverse once the buggy has stopped (Mark: wheels
/// must never spin backwards under forward momentum — that drifts the car). W does the same mirrored
/// when rolling backwards. No throttle = rolling drag. No fuel = no drive (brakes still work).
/// All designer values are meters / seconds; converted once here.
/// </summary>
public sealed partial class Vehicle
{
	[Property, Group( "Drive" ), Title( "Max Speed (m/s)" ), Description( "27 m/s ≈ 60 mph." )]
	public float MaxSpeedMetersPerSecond { get; set; } = 54f;

	[Property, Group( "Drive" ), Title( "Reverse Speed (m/s)" )]
	public float ReverseSpeedMetersPerSecond { get; set; } = 7f;

	[Property, Group( "Drive" ), Title( "Acceleration (m/s²)" )]
	public float AccelerationMetersPerSecond2 { get; set; } = 7f;

	[Property, Group( "Drive" ), Title( "Brake Deceleration (m/s²)" )]
	public float BrakeDecelerationMetersPerSecond2 { get; set; } = 12f;

	[Property, Group( "Drive" ), Title( "Rolling Drag (m/s²)" ), Description( "Slow-down with no pedal held." )]
	public float RollingDragMetersPerSecond2 { get; set; } = 4f;

	[Property, Group( "Drive" ), Title( "Out-of-Fuel Coast Drag (m/s²)" ), Description( "Slow-down once the tank is empty: the engine is dead, W does nothing, the buggy coasts down to a stop at this rate (brakes still work)." )]
	public float OutOfFuelCoastDragMetersPerSecond2 { get; set; } = 1.5f;

	[Property, Group( "Drive" ), Title( "Tyre Grip (μ)" ), Description( "Tyre friction coefficient: each wheel's drive + side force is capped at μ × the load on that wheel. Higher = corners harder before sliding, so it tips over instead of sliding out sooner. Dirt ≈ 0.7, grippy rubber on rock ≈ 1.0+." )]
	public float TyreGripCoefficient { get; set; } = 1.2f;

	[Property, Group( "Steering" ), Title( "Max Steer (deg)" )]
	public float MaxSteerDegrees { get; set; } = 32f;

	[Property, Group( "Steering" ), Title( "Steer Speed (deg/s)" )]
	public float SteerSpeedDegreesPerSecond { get; set; } = 160f;

	[Property, Group( "Steering" ), Title( "Steer Lateral Limit (g)" ), Description( "The most sideways g a full-lock turn may ask for at low and mid speed: the lock shrinks with speed so v²·tan(lock)/wheelbase never exceeds it. Kept under the roll-over point (~0.9–1.0 g for this chassis) so a hard turn at 30–45 mph rides on three wheels or slides instead of rolling (Mark 2026-10-06: \"far too easy to flip\")." )]
	public float SteerLateralLimitG { get; set; } = 0.75f;

	[Property, Group( "Steering" ), Title( "Steer Lateral Limit at Top Speed (g)" ), Description( "The limit climbs toward this at max speed, so a full-lock yank at highway speed can still roll it. ~1.05 g is reached near 60 mph with the defaults." )]
	public float SteerLateralLimitTopSpeedG { get; set; } = 3.25f;

	[Property, Group( "Steering" ), Title( "Steer Lateral Limit Curve" ), Description( "Exponent on speed / max speed for the climb between the two limits: 1 = linear, 3 = stays low until the last third of the speed range." )]
	public float SteerLateralLimitCurve { get; set; } = 3f;

	[Property, Group( "Suspension" ), Title( "Wheel Base (m)" )]
	public float WheelBaseMeters { get; set; } = 2.4f;

	[Property, Group( "Suspension" ), Title( "Track Width (m)" )]
	public float TrackWidthMeters { get; set; } = 1.7f;

	[Property, Group( "Suspension" ), Title( "Wheel Radius (m)" )]
	public float WheelRadiusMeters { get; set; } = 0.35f;

	[Property, Group( "Suspension" ), Title( "Spring Length (m)" ), Description( "Unloaded spring length below the mount. The chassis sags ~0.2 m under its own weight." )]
	public float SuspensionLengthMeters { get; set; } = 0.55f;

	[Property, Group( "Suspension" ), Title( "Frequency (Hz)" ), Description( "Lower = softer. Real dune buggies sit around 1.2–1.6." )]
	public float SuspensionFrequencyHz { get; set; } = 2.0f;

	[Property, Group( "Suspension" ), Title( "Damping Ratio" ), Description( "0.7–1.0 soaks bumps without bouncing." )]
	public float SuspensionDampingRatio { get; set; } = 0.85f;

	[Property, Group( "Suspension" ), Title( "Bump Stop (× wheel load)" ), Description( "Most one spring + damper may push, as a multiple of the static load on that wheel. A wheel that lands fully compressed used to fire the whole spring and damper at once and kick the chassis back over (Mark 2026-10-06: \"keeps rolling\"). 3 carries bumps and landings without launching it." )]
	public float SuspensionBumpStopMultiple { get; set; } = 3f;

	[Property, Group( "Suspension" ), Title( "Airborne Spin Damping" ), Description( "Rigidbody angular damping while truly airborne (all four wheels up and no ground under the chassis); the prefab's own value is restored on touchdown." )]
	public float AirborneAngularDamping { get; set; } = 2.5f;

	[Property, Group( "Suspension" ), Title( "Airborne Self-Right (rad/s²)" ), Description( "Torque toward upright while truly airborne, so a jump lands on its wheels. A real torque through the physics (it used to write angular velocity, which fed a tumble past 90°). 0 = none, the chassis lands however it flies." )]
	public float AirborneUprightStrength { get; set; } = 0f;

	/// <summary>Signed speed along the chassis forward, m/s (simulating machine writes; HUD reads anywhere).</summary>
	[Sync]
	public float SyncedSpeedMetersPerSecond { get; private set; }

	public float SpeedMph => MathF.Abs( SyncedSpeedMetersPerSecond ) * 2.23694f;

	/// <summary>Debug: the dt the last simulation step used.</summary>
	public float LastStepDelta { get; private set; }

	/// <summary>Debug: per-wheel grounded flag and compression (units), plus the spring constant in use.</summary>
	public string DebugWheels()
	{
		var mass = Math.Max( 1f, _body?.Mass ?? 1f );
		var omega = 2f * MathF.PI * Math.Max( 0.2f, SuspensionFrequencyHz );
		var k = mass * 0.25f * omega * omega;
		var parts = new string[4];
		for ( var i = 0; i < 4; i++ )
			parts[i] = _wheels[i].Grounded ? $"{_wheels[i].Compression:0.0}" : "air";
		return $"sleeping={_body?.Sleeping} motion={_body?.MotionEnabled} accel={AccelerationMetersPerSecond2} freq={SuspensionFrequencyHz} k={k:0} wheels=[{string.Join( " ", parts )}] grounded={_groundedWheels} spin={MathX.RadianToDegree( _body?.AngularVelocity.Length ?? 0f ):0}°/s steer={_steerDegrees:0.0} grav={Scene.PhysicsWorld.Gravity}";
	}

	/// <summary>True on the machine that steps the physics (the owner — host until a driver takes over).</summary>
	public bool IsSimulatingMachine
		=> GameObject.Network is not { Active: true } net
		   || net.IsOwner
		   || ( net.Owner is null && Networking.IsHost );

	struct WheelState
	{
		public bool Grounded;
		public float Compression;
		public float Load;
		public Vector3 ContactNormal;
		public Vector3 ContactPoint;
		public float SpinDegrees;
	}

	static readonly string[] WheelVisualNames = { "WheelFL", "WheelFR", "WheelRL", "WheelRR" };

	Rigidbody _body;
	float _steerDegrees;
	readonly WheelState[] _wheels = new WheelState[4];
	readonly GameObject[] _wheelVisuals = new GameObject[4];
	int _groundedWheels;

	void CollectWheelVisuals()
	{
		for ( var i = 0; i < WheelVisualNames.Length; i++ )
		{
			_wheelVisuals[i] = null;
			foreach ( var child in GameObject.Children )
			{
				if ( child.IsValid() && string.Equals( child.Name, WheelVisualNames[i], StringComparison.OrdinalIgnoreCase ) )
				{
					_wheelVisuals[i] = child;
					break;
				}
			}
		}
	}

	/// <summary>Mount point of wheel i in chassis space: 0/1 front (left/right), 2/3 rear.</summary>
	Vector3 WheelMountLocal( int i )
	{
		var halfBase = TerrainWorldUnits.MetersToEngine( WheelBaseMeters ) * 0.5f;
		var halfTrack = TerrainWorldUnits.MetersToEngine( TrackWidthMeters ) * 0.5f;
		var x = i < 2 ? halfBase : -halfBase;
		var y = ( i % 2 == 0 ) ? halfTrack : -halfTrack;
		return new Vector3( x, y, 0f );
	}

	bool IsFrontWheel( int i ) => i < 2;

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();
		if ( IsPreviewGhost )
			return;

		_body ??= Components.Get<Rigidbody>();
		if ( _body is null || !_body.IsValid() )
			return;

		TickOccupancyValidity();

		if ( !IsSimulatingMachine )
			return;

		// The physics step, not the frame: Time.Delta inside OnFixedUpdate is not reliably the fixed step.
		var dt = Scene.FixedDelta;
		if ( dt <= 0f )
			dt = Time.Delta;
		if ( dt <= 0f )
			return;
		LastStepDelta = dt;

		ReadDriverInput( out var throttle, out var steer, out var vertical, out var throttleHeld );

		// A body that came to a dead stop falls asleep and then ignores applied forces: pedals wake it.
		// Unconditional — the Sleeping getter can lag the physics world's own state by a step.
		if ( throttleHeld || MathF.Abs( steer ) > 0.01f || MathF.Abs( vertical ) > 0.01f )
		{
			_body.Sleeping = false;
			_body.Velocity = _body.Velocity;
		}

		if ( IsHover )
		{
			// Hover platform: cushion, levelling, ice drive (Vehicle.Hover.cs). No wheels, never "flipped".
			SimulateHover( throttle, steer, vertical, dt );
			SimulateFlipPush( dt );
			if ( IsFlipped )
				IsFlipped = false;
			SyncedSpeedMetersPerSecond = HoverSignedPlanarSpeedMetersPerSecond();
			TickFuelBurn( dt, throttleHeld || MathF.Abs( vertical ) > 0.01f );
			return;
		}

		SimulateSuspension( dt );
		SimulateFlipPush( dt );
		UpdateFlippedState();
		SimulateSteering( steer, dt );
		SimulateDrive( throttle, dt );
		SimulateAirStability( dt );

		SyncedSpeedMetersPerSecond = TerrainWorldUnits.EngineToMeters( Vector3.Dot( _body.Velocity, WorldRotation.Forward ) );
		TickFuelBurn( dt, throttleHeld );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( IsPreviewGhost )
			return;

		UpdateWheelVisuals( Time.Delta );
	}

	/// <summary>
	/// Only the local pawn in seat 0 drives. W / S throttle, A / D steer (+1 = left, Source yaw) on both drive
	/// models — on a hover vehicle A / D alone spin it in place (<see cref="HoverSpinRateDegreesPerSecond"/>).
	/// Vertical (hover only): +1 = the pawn's jump action, -1 = its sneak action — read off the driver's
	/// <see cref="PlayerMovement"/> so the key names live in one place.
	/// </summary>
	void ReadDriverInput( out float throttle, out float steer, out float vertical, out bool throttleHeld )
	{
		throttle = 0f;
		steer = 0f;
		vertical = 0f;
		throttleHeld = false;

		// Editor filming / tuning: `vehicle_drive` and the scripted tests hold the pedals of THIS vehicle the
		// way a driver would — with or without one in the seat (the simulating machine runs them).
		if ( VehicleDevCommands.TryGetSimulatedInput( this, out var simThrottle, out var simSteer ) )
		{
			throttle = simThrottle;
			steer = simSteer;
			throttleHeld = MathF.Abs( simThrottle ) > 0.01f;
			return;
		}

		var driver = GetOccupant( 0 );
		if ( driver is null || !driver.IsValid() )
			return;

		var vitals = driver.Components.Get<PlayerVitals>();
		if ( vitals is null || !vitals.IsLocalInputOwnedPawn() )
			return;

		var menu = driver.Components.Get<PlayerGameMenuController>();
		if ( menu is { IsMenuOpen: true } )
			return;

		var forward = Input.Down( "Forward" ) ? 1f : 0f;
		var backward = Input.Down( "Backward" ) ? 1f : 0f;
		var left = Input.Down( "Left" ) ? 1f : 0f;
		var right = Input.Down( "Right" ) ? 1f : 0f;

		throttle = forward - backward;
		steer = left - right;
		throttleHeld = forward > 0f || backward > 0f;

		if ( UsesVerticalInput && driver.Components.Get<PlayerMovement>() is { } movement )
		{
			var up = !string.IsNullOrWhiteSpace( movement.JumpInputAction ) && Input.Down( movement.JumpInputAction ) ? 1f : 0f;
			var down = !string.IsNullOrWhiteSpace( movement.SneakInputAction ) && Input.Down( movement.SneakInputAction ) ? 1f : 0f;
			vertical = up - down;
		}
	}

	void SimulateSuspension( float dt )
	{
		var up = WorldRotation.Up;
		var mass = Math.Max( 1f, _body.Mass );
		var sprung = mass * 0.25f;
		var omega = 2f * MathF.PI * Math.Max( 0.2f, SuspensionFrequencyHz );
		var k = sprung * omega * omega;
		var c = 2f * Math.Max( 0f, SuspensionDampingRatio ) * MathF.Sqrt( k * sprung );

		var radius = TerrainWorldUnits.MetersToEngine( Math.Max( 0.05f, WheelRadiusMeters ) );
		var springLength = TerrainWorldUnits.MetersToEngine( Math.Max( 0.05f, SuspensionLengthMeters ) );
		var rayLength = springLength + radius;
		var gravityUnits = Math.Max( 1f, Scene.PhysicsWorld.Gravity.Length );

		_groundedWheels = 0;
		for ( var i = 0; i < 4; i++ )
		{
			ref var wheel = ref _wheels[i];
			var mount = WorldTransform.PointToWorld( WheelMountLocal( i ) );
			var tr = Scene.Trace.Ray( mount, mount - up * rayLength )
				.IgnoreGameObjectHierarchy( GameObject )
				.WithoutTags( "player", "trigger", "worlddrop" )
				.Run();

			// A ray that starts inside the ground or grazes it sideways is a buggy lying on its side, not a
			// wheel on the floor: counting it read as full compression and the spring shoved the chassis upright.
			if ( !tr.Hit || tr.StartedSolid || Vector3.Dot( tr.Normal, up ) < 0.35f )
			{
				wheel.Grounded = false;
				wheel.Compression = 0f;
				wheel.Load = 0f;
				continue;
			}

			var compression = rayLength - tr.Distance;
			var velocityAtMount = _body.GetVelocityAtPoint( mount );
			var upwardSpeed = Vector3.Dot( velocityAtMount, up );
			var force = k * compression - c * upwardSpeed;
			if ( force < 0f )
				force = 0f;

			// Bump stop: the spring and damper together never exceed a few times this wheel's static load,
			// so a wheel that lands fully compressed cannot launch the chassis.
			var bumpStop = Math.Max( 1f, SuspensionBumpStopMultiple ) * sprung * gravityUnits;
			if ( force > bumpStop )
				force = bumpStop;

			// ApplyForceAt, measured in engine (2026-10-02): force × Scene.FixedDelta as an impulse came out
			// several times weaker than the same force applied here, so the force path is the calibrated one.
			_body.ApplyForceAt( mount, up * force );

			wheel.Grounded = true;
			wheel.Compression = compression;
			wheel.Load = force;
			wheel.ContactNormal = tr.Normal.LengthSquared > 0.01f ? tr.Normal.Normal : Vector3.Up;
			wheel.ContactPoint = tr.HitPosition;
			_groundedWheels++;
		}
	}

	void SimulateSteering( float steerInput, float dt )
	{
		var target = Math.Clamp( steerInput, -1f, 1f ) * SteerLockDegreesAtSpeed( MathF.Abs( SyncedSpeedMetersPerSecond ) );
		var step = Math.Max( 10f, SteerSpeedDegreesPerSecond ) * dt;
		_steerDegrees = MathX.Approach( _steerDegrees, target, step );
	}

	/// <summary>
	/// Speed-sensitive lock: the largest wheel angle whose steady-state lateral demand v²·tan(δ)/L stays
	/// under the lateral limit for this speed (the limit itself climbs from <see cref="SteerLateralLimitG"/>
	/// to <see cref="SteerLateralLimitTopSpeedG"/> along <see cref="SteerLateralLimitCurve"/>), never more
	/// than <see cref="MaxSteerDegrees"/>. Parked it is the full lock; at 60 mph with the defaults about 6°.
	/// </summary>
	public float SteerLockDegreesAtSpeed( float speedMetersPerSecond )
	{
		var maxLock = Math.Max( 1f, MaxSteerDegrees );
		var speed = Math.Max( 0f, speedMetersPerSecond );
		if ( speed < 0.5f )
			return maxLock;

		var fraction = Math.Clamp( speed / Math.Max( 1f, MaxSpeedMetersPerSecond ), 0f, 1f );
		var climb = MathF.Pow( fraction, Math.Clamp( SteerLateralLimitCurve, 0.2f, 6f ) );
		var limitG = MathX.Lerp( Math.Max( 0.05f, SteerLateralLimitG ), Math.Max( 0.05f, SteerLateralLimitTopSpeedG ), climb );

		// Everything in meters: gravity and the wheelbase converted once here.
		var gravity = TerrainWorldUnits.EngineToMeters( Math.Max( 1f, Scene.PhysicsWorld.Gravity.Length ) );
		var wheelBase = Math.Max( 0.5f, WheelBaseMeters );
		var tanLock = limitG * gravity * wheelBase / ( speed * speed );
		return Math.Min( maxLock, MathX.RadianToDegree( MathF.Atan( tanLock ) ) );
	}

	Rotation WheelRotation( int i )
		=> IsFrontWheel( i ) ? WorldRotation * Rotation.FromYaw( _steerDegrees ) : WorldRotation;

	void SimulateDrive( float throttle, float dt )
	{
		if ( _groundedWheels == 0 )
			return;

		var mass = Math.Max( 1f, _body.Mass );
		var forward = WorldRotation.Forward;
		var velocity = _body.Velocity;
		var forwardSpeed = TerrainWorldUnits.EngineToMeters( Vector3.Dot( velocity, forward ) );   // m/s, signed

		// Longitudinal acceleration request in m/s² along the wheel forward (+ = forward).
		var accel = 0f;
		var driving = false;
		const float stoppedBand = 0.5f;
		if ( throttle > 0f )
		{
			if ( forwardSpeed < -stoppedBand )
			{
				accel = BrakeToward( forwardSpeed, dt );
				driving = true;
			}
			else if ( forwardSpeed < MaxSpeedMetersPerSecond && HasFuel )
			{
				accel = AccelerationMetersPerSecond2;
				driving = true;
			}
		}
		else if ( throttle < 0f )
		{
			if ( forwardSpeed > stoppedBand )
			{
				accel = BrakeToward( forwardSpeed, dt );
				driving = true;
			}
			else if ( forwardSpeed > -ReverseSpeedMetersPerSecond && HasFuel )
			{
				accel = -AccelerationMetersPerSecond2 * 0.7f;
				driving = true;
			}
		}

		// No pedal or sitting on the limiter: the tyres roll the buggy down to a stop. An empty tank is a
		// dead engine: the pedal does nothing and the buggy coasts down more gently (S still brakes above).
		if ( !driving )
			accel = RollTowardStop( forwardSpeed, dt, HasFuel ? RollingDragMetersPerSecond2 : OutOfFuelCoastDragMetersPerSecond2 );

		var accelUnits = TerrainWorldUnits.MetersToEngine( accel );
		var perWheelDrive = mass * accelUnits / _groundedWheels;
		var perWheelMass = mass * 0.25f;
		var mu = Math.Max( 0f, TyreGripCoefficient );

		for ( var i = 0; i < 4; i++ )
		{
			ref var wheel = ref _wheels[i];
			if ( !wheel.Grounded )
				continue;

			var mount = WorldTransform.PointToWorld( WheelMountLocal( i ) );
			var rot = WheelRotation( i );
			var n = wheel.ContactNormal;
			var wheelForward = ( rot.Forward - n * Vector3.Dot( rot.Forward, n ) ).Normal;
			var wheelRight = ( rot.Right - n * Vector3.Dot( rot.Right, n ) ).Normal;

			// Side force that would cancel this wheel's sideways slip in one step.
			var lateral = Vector3.Dot( _body.GetVelocityAtPoint( mount ), wheelRight );
			var sideForce = -perWheelMass * lateral / dt;

			// Friction circle: drive + side together never exceed μ × this wheel's load. A light inside wheel
			// slides first; a wheel in the air contributes nothing. Pushed at the ground contact, where a real
			// tyre pushes — that lever arm under the mass centre is what tips a buggy in a hard turn.
			var tyreForce = wheelForward * perWheelDrive + wheelRight * sideForce;
			var limit = mu * wheel.Load;
			if ( tyreForce.LengthSquared > limit * limit )
				tyreForce = tyreForce.Normal * limit;

			_body.ApplyForceAt( wheel.ContactPoint, tyreForce );
		}
	}

	/// <summary>Decelerate toward zero without crossing it (m/s²).</summary>
	float BrakeToward( float forwardSpeed, float dt )
	{
		var maxStep = MathF.Abs( forwardSpeed ) / dt;
		var decel = Math.Min( Math.Max( 0f, BrakeDecelerationMetersPerSecond2 ), maxStep );
		return -MathF.Sign( forwardSpeed ) * decel;
	}

	float RollTowardStop( float forwardSpeed, float dt, float dragMetersPerSecond2 )
	{
		if ( MathF.Abs( forwardSpeed ) < 0.05f )
			return 0f;

		var maxStep = MathF.Abs( forwardSpeed ) / dt;
		var decel = Math.Min( Math.Max( 0f, dragMetersPerSecond2 ), maxStep );
		return -MathF.Sign( forwardSpeed ) * decel;
	}

	float _groundAngularDamping = -1f;
	bool _airborneDampingApplied;

	/// <summary>
	/// Truly airborne (no wheel down and no ground under the chassis): the rigidbody's angular damping is
	/// raised to <see cref="AirborneAngularDamping"/> and, if <see cref="AirborneUprightStrength"/> is set, a
	/// torque toward upright is applied through the physics. Nothing writes velocities: a tumble keeps the
	/// momentum it earned. A rolled buggy lying on its side or roof has ground under it and is left alone.
	/// </summary>
	void SimulateAirStability( float dt )
	{
		if ( _groundAngularDamping < 0f )
			_groundAngularDamping = _body.AngularDamping;

		var airborne = _groundedWheels == 0;
		if ( airborne )
		{
			var probe = TerrainWorldUnits.MetersToEngine( Math.Max( 0.05f, SuspensionLengthMeters + WheelRadiusMeters ) * 1.5f );
			var ground = Scene.Trace.Ray( WorldPosition, WorldPosition - Vector3.Up * probe )
				.IgnoreGameObjectHierarchy( GameObject )
				.WithoutTags( "player", "trigger", "worlddrop" )
				.Run();
			airborne = !ground.Hit;
		}

		if ( !airborne )
		{
			if ( _airborneDampingApplied )
			{
				_body.AngularDamping = _groundAngularDamping;
				_airborneDampingApplied = false;
			}
			return;
		}

		if ( !_airborneDampingApplied )
		{
			_body.AngularDamping = Math.Max( 0f, AirborneAngularDamping );
			_airborneDampingApplied = true;
		}

		var strength = Math.Max( 0f, AirborneUprightStrength );
		if ( strength <= 0f )
			return;

		// Torque sized from a box estimate of the hull's inertia so the knob reads as rad/s² toward upright.
		var axis = Vector3.Cross( WorldRotation.Up, Vector3.Up );
		if ( axis.LengthSquared < 1e-6f )
			return;

		var track = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, TrackWidthMeters ) );
		var height = TerrainWorldUnits.MetersToEngine( 1.2f );
		var inertia = Math.Max( 1f, _body.Mass ) / 12f * ( track * track + height * height );
		_body.ApplyTorque( axis * ( strength * inertia ) );
	}

	/// <summary>Every machine: wheel meshes sit at the spring length (proxies show rest length), steer and spin.</summary>
	void UpdateWheelVisuals( float dt )
	{
		var radius = TerrainWorldUnits.MetersToEngine( Math.Max( 0.05f, WheelRadiusMeters ) );
		var springLength = TerrainWorldUnits.MetersToEngine( Math.Max( 0.05f, SuspensionLengthMeters ) );
		var circumference = 2f * MathF.PI * radius;
		var travelUnits = TerrainWorldUnits.MetersToEngine( SyncedSpeedMetersPerSecond ) * dt;
		var spinStep = circumference > 0.01f ? travelUnits / circumference * 360f : 0f;

		for ( var i = 0; i < 4; i++ )
		{
			var go = _wheelVisuals[i];
			if ( go is null || !go.IsValid() )
				continue;

			ref var wheel = ref _wheels[i];
			wheel.SpinDegrees = ( wheel.SpinDegrees + spinStep ) % 360f;

			var drop = wheel.Grounded ? Math.Clamp( springLength - wheel.Compression, 0f, springLength ) : springLength;
			go.LocalPosition = WheelMountLocal( i ) - Vector3.Up * drop;
			var yaw = IsFrontWheel( i ) ? _steerDegrees : 0f;
			go.LocalRotation = Rotation.FromYaw( yaw ) * Rotation.FromPitch( wheel.SpinDegrees );
		}
	}
}
