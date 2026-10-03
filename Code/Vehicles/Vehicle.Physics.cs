using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Driving model: one <see cref="Rigidbody"/> chassis on four raycast springs (no wheel bodies). Every
/// physics step the simulating machine (host, or the driver once it owns the vehicle) traces straight
/// down from each wheel mount, pushes the chassis up with a spring + damper sized from
/// <see cref="SuspensionFrequencyHz"/> / <see cref="SuspensionDampingRatio"/> and the chassis mass, kills
/// sideways slip at each grounded wheel (<see cref="LateralGripMetersPerSecond2"/>) and pushes along the
/// wheel's forward for throttle / brake. Drive and grip forces act at the mount points (chassis height),
/// not the contact patch, so a hard turn leans instead of rolling the buggy.
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
	public float MaxSpeedMetersPerSecond { get; set; } = 27f;

	[Property, Group( "Drive" ), Title( "Reverse Speed (m/s)" )]
	public float ReverseSpeedMetersPerSecond { get; set; } = 7f;

	[Property, Group( "Drive" ), Title( "Acceleration (m/s²)" )]
	public float AccelerationMetersPerSecond2 { get; set; } = 7f;

	[Property, Group( "Drive" ), Title( "Brake Deceleration (m/s²)" )]
	public float BrakeDecelerationMetersPerSecond2 { get; set; } = 12f;

	[Property, Group( "Drive" ), Title( "Rolling Drag (m/s²)" ), Description( "Slow-down with no pedal held." )]
	public float RollingDragMetersPerSecond2 { get; set; } = 4f;

	[Property, Group( "Drive" ), Title( "Lateral Grip (m/s²)" ), Description( "Max sideways correction per grounded wheel. Lower = more slide." )]
	public float LateralGripMetersPerSecond2 { get; set; } = 90f;

	[Property, Group( "Steering" ), Title( "Max Steer (deg)" )]
	public float MaxSteerDegrees { get; set; } = 32f;

	[Property, Group( "Steering" ), Title( "Steer Speed (deg/s)" )]
	public float SteerSpeedDegreesPerSecond { get; set; } = 160f;

	[Property, Group( "Steering" ), Title( "Steer at Top Speed (fraction)" ), Description( "Steering lock shrinks toward this at max speed so the buggy does not spin out." )]
	public float HighSpeedSteerFraction { get; set; } = 0.35f;

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

	[Property, Group( "Suspension" ), Title( "Airborne Spin Damping" ), Description( "How fast tumbling dies off with all four wheels off the ground." )]
	public float AirborneAngularDamping { get; set; } = 2.5f;

	[Property, Group( "Suspension" ), Title( "Airborne Self-Right" ), Description( "Pull toward upright in the air so a jump lands on its wheels." )]
	public float AirborneUprightStrength { get; set; } = 4f;

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
		return $"sleeping={_body?.Sleeping} motion={_body?.MotionEnabled} accel={AccelerationMetersPerSecond2} freq={SuspensionFrequencyHz} k={k:0} wheels=[{string.Join( " ", parts )}] grounded={_groundedWheels} steer={_steerDegrees:0.0} grav={Scene.PhysicsWorld.Gravity}";
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
		public Vector3 ContactNormal;
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

		ReadDriverInput( out var throttle, out var steer, out var throttleHeld );

		// A body that came to a dead stop falls asleep and then ignores applied forces: pedals wake it.
		// Unconditional — the Sleeping getter can lag the physics world's own state by a step.
		if ( throttleHeld || MathF.Abs( steer ) > 0.01f )
		{
			_body.Sleeping = false;
			_body.Velocity = _body.Velocity;
		}

		SimulateSuspension( dt );
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

	/// <summary>Only the local pawn in seat 0 drives. Steer: +1 = left (Source yaw), so D gives -1.</summary>
	void ReadDriverInput( out float throttle, out float steer, out bool throttleHeld )
	{
		throttle = 0f;
		steer = 0f;
		throttleHeld = false;

		var driver = GetOccupant( 0 );
		if ( driver is null || !driver.IsValid() )
			return;

		var vitals = driver.Components.Get<PlayerVitals>();
		if ( vitals is null || !vitals.IsLocalInputOwnedPawn() )
			return;

		var menu = driver.Components.Get<PlayerGameMenuController>();
		if ( menu is { IsMenuOpen: true } )
			return;

		// Editor filming: `vehicle_drive` holds pedals the way a key would.
		if ( VehicleDevCommands.TryGetSimulatedInput( out var simThrottle, out var simSteer ) )
		{
			throttle = simThrottle;
			steer = simSteer;
			throttleHeld = MathF.Abs( simThrottle ) > 0.01f;
			return;
		}

		var forward = Input.Down( "Forward" ) ? 1f : 0f;
		var backward = Input.Down( "Backward" ) ? 1f : 0f;
		var left = Input.Down( "Left" ) ? 1f : 0f;
		var right = Input.Down( "Right" ) ? 1f : 0f;

		throttle = forward - backward;
		steer = left - right;
		throttleHeld = forward > 0f || backward > 0f;
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

		_groundedWheels = 0;
		for ( var i = 0; i < 4; i++ )
		{
			ref var wheel = ref _wheels[i];
			var mount = WorldTransform.PointToWorld( WheelMountLocal( i ) );
			var tr = Scene.Trace.Ray( mount, mount - up * rayLength )
				.IgnoreGameObjectHierarchy( GameObject )
				.WithoutTags( "player", "trigger", "worlddrop" )
				.Run();

			if ( !tr.Hit )
			{
				wheel.Grounded = false;
				wheel.Compression = 0f;
				continue;
			}

			var compression = rayLength - tr.Distance;
			var velocityAtMount = _body.GetVelocityAtPoint( mount );
			var upwardSpeed = Vector3.Dot( velocityAtMount, up );
			var force = k * compression - c * upwardSpeed;
			if ( force < 0f )
				force = 0f;

			// ApplyForceAt, measured in engine (2026-10-02): force × Scene.FixedDelta as an impulse came out
			// several times weaker than the same force applied here, so the force path is the calibrated one.
			_body.ApplyForceAt( mount, up * force );

			wheel.Grounded = true;
			wheel.Compression = compression;
			wheel.ContactNormal = tr.Normal.LengthSquared > 0.01f ? tr.Normal.Normal : Vector3.Up;
			_groundedWheels++;
		}
	}

	void SimulateSteering( float steerInput, float dt )
	{
		var speedFraction = Math.Clamp( MathF.Abs( SyncedSpeedMetersPerSecond ) / Math.Max( 1f, MaxSpeedMetersPerSecond ), 0f, 1f );
		var lock_ = MaxSteerDegrees * MathX.Lerp( 1f, Math.Clamp( HighSpeedSteerFraction, 0.05f, 1f ), speedFraction );
		var target = Math.Clamp( steerInput, -1f, 1f ) * lock_;
		var step = Math.Max( 10f, SteerSpeedDegreesPerSecond ) * dt;
		_steerDegrees = MathX.Approach( _steerDegrees, target, step );
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

		// No pedal, an empty tank, or sitting on the limiter: the tyres roll the buggy down to a stop.
		if ( !driving )
			accel = RollTowardStop( forwardSpeed, dt );

		var accelUnits = TerrainWorldUnits.MetersToEngine( accel );
		var perWheelDrive = mass * accelUnits / _groundedWheels;
		var gripUnits = TerrainWorldUnits.MetersToEngine( Math.Max( 0f, LateralGripMetersPerSecond2 ) );
		var perWheelMass = mass * 0.25f;

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

			// Drive / brake along the (steered) wheel forward.
			if ( MathF.Abs( perWheelDrive ) > 0.01f )
				_body.ApplyForceAt( mount, wheelForward * perWheelDrive );

			// Sideways grip: cancel lateral slip at this wheel, up to the grip limit.
			var v = _body.GetVelocityAtPoint( mount );
			var lateral = Vector3.Dot( v, wheelRight );
			var wantedAccel = -lateral / dt;
			wantedAccel = Math.Clamp( wantedAccel, -gripUnits, gripUnits );
			_body.ApplyForceAt( mount, wheelRight * ( perWheelMass * wantedAccel ) );
		}
	}

	/// <summary>Decelerate toward zero without crossing it (m/s²).</summary>
	float BrakeToward( float forwardSpeed, float dt )
	{
		var maxStep = MathF.Abs( forwardSpeed ) / dt;
		var decel = Math.Min( Math.Max( 0f, BrakeDecelerationMetersPerSecond2 ), maxStep );
		return -MathF.Sign( forwardSpeed ) * decel;
	}

	float RollTowardStop( float forwardSpeed, float dt )
	{
		if ( MathF.Abs( forwardSpeed ) < 0.05f )
			return 0f;

		var maxStep = MathF.Abs( forwardSpeed ) / dt;
		var decel = Math.Min( Math.Max( 0f, RollingDragMetersPerSecond2 ), maxStep );
		return -MathF.Sign( forwardSpeed ) * decel;
	}

	void SimulateAirStability( float dt )
	{
		if ( _groundedWheels > 0 )
			return;

		var damping = Math.Clamp( 1f - Math.Max( 0f, AirborneAngularDamping ) * dt, 0f, 1f );
		_body.AngularVelocity *= damping;

		// Nudge the chassis up-vector toward world up so a jump comes down on its wheels.
		var axis = Vector3.Cross( WorldRotation.Up, Vector3.Up );
		if ( axis.LengthSquared > 1e-6f )
			_body.AngularVelocity += axis * ( Math.Max( 0f, AirborneUprightStrength ) * dt );
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
