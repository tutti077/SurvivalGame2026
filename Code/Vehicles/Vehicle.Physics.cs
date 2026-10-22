using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Driving model: one <see cref="Rigidbody"/> chassis on four raycast springs (no wheel bodies). Every
/// physics step the simulating machine (host, or the driver once it owns the vehicle) traces straight
/// down from each wheel mount, pushes the chassis up with a spring + damper sized from
/// <see cref="SuspensionFrequencyHz"/> / <see cref="SuspensionDampingRatio"/> and the chassis mass, kills
/// sideways slip at each grounded wheel and pushes along the wheel's forward for throttle / brake.
/// Tyres follow GTA V's traction curve (handling.meta fTractionCurveMax / Min / Lateral, fTractionBiasFront;
/// Mark 2026-10-21: "feel like GTA 5"): side grip builds with slip angle up to <see cref="TyreGripCoefficient"/>
/// at <see cref="TyrePeakSlipDegrees"/>, then falls to <see cref="TyreSlideGripCoefficient"/> once the tyre
/// slides; side force takes that grip × the wheel's own spring load first and drive / brake get what is
/// left (friction circle), both acting at the ground contact (Mark 2026-10-04: rollover must come out of the physics). A hard turn shifts load onto the
/// outside wheels; whether the chassis then tips, slides or rolls over its cage collider falls out of
/// speed, steering angle, slope, bumps, mass-centre height and track width. Nothing caps the turn and
/// nothing damps or rights the chassis in the air (Mark 2026-10-21: flipping must be entirely physics —
/// a sharp turn plus a bump can roll it). μ sits under the tip point (half-track ÷ mass-centre height),
/// so on flat ground a hard turn slides; a bump or slope on top of it is what trips it over.
///
/// Steering is GTA's speed-limited lock: the front wheels never turn further than
/// <see cref="SteerSlipAssistFraction"/> of the peak slip angle past the way the front axle is travelling,
/// so at speed the fronts stay near peak grip with room left for throttle — holding W + A widens the
/// line and keeps accelerating instead of scrubbing at the grip limit. Parked or slow it is the full lock.
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

	[Property, Group( "Tyres" ), Title( "Tyre Grip (μ)" ), Description( "Peak grip while the tyre holds (GTA fTractionCurveMax, in g): drive + side force per wheel up to μ × its load. This is also the rollover knob: below the chassis tip point (≈1.05 g for the buggy) a flat hard turn slides and only a bump or slope rolls it; at or above it a flat full-lock turn rolls on its own. Dirt ≈ 0.7, grippy rubber on rock ≈ 1.0+ (GTA road cars run 2+ with a mass centre too low to roll)." )]
	public float TyreGripCoefficient { get; set; } = 0.9f;

	[Property, Group( "Tyres" ), Title( "Tyre Slide Grip (μ)" ), Description( "Grip once a tyre slides past its peak slip angle (GTA fTractionCurveMin). Lower than Tyre Grip = a slide is hard to catch; equal = no drop-off. GTA cars sit around 0.8 × the peak." )]
	public float TyreSlideGripCoefficient { get; set; } = 0.75f;

	[Property, Group( "Tyres" ), Title( "Tyre Peak Slip (deg)" ), Description( "Slip angle at which a tyre reaches full grip and past which it starts to slide (GTA fTractionCurveLateral). Lower = sharper, more planted; higher = softer, the body swings out further before it bites. Real tyres peak around 6–10°; GTA's usual 22.5 feels drifty on this model. Grip falls to Slide Grip over the same angle again past the peak." )]
	public float TyrePeakSlipDegrees { get; set; } = 10f;

	[Property, Group( "Tyres" ), Title( "Tyre Grip Bias Front (0–1)" ), Description( "Share of grip on the front axle (GTA fTractionBiasFront): 0.5 even, below 0.5 the rear holds better than the front so the tail stays planted and the nose washes wide first (GTA's arcade cars sit at about 0.48)." )]
	public float TyreGripBiasFront { get; set; } = 0.48f;

	[Property, Group( "Steering" ), Title( "Max Steer (deg)" )]
	public float MaxSteerDegrees { get; set; } = 32f;

	[Property, Group( "Steering" ), Title( "Steer Speed (deg/s)" )]
	public float SteerSpeedDegreesPerSecond { get; set; } = 160f;

	[Property, Group( "Steering" ), Title( "Steer Slip Assist (× peak slip)" ), Description( "GTA-style speed-limited lock: the front wheels turn at most this share of Tyre Peak Slip past the direction the front axle is moving, so they stay just under peak grip and leave room for throttle. Below ~1 the buggy keeps accelerating through a held turn (the line widens); 1 = exactly at peak; 0 = off (full lock at any speed — the tyres saturate and a turn scrubs speed)." )]
	public float SteerSlipAssistFraction { get; set; } = 0.8f;

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
		var target = Math.Clamp( steerInput, -1f, 1f ) * Math.Max( 1f, MaxSteerDegrees );
		target = ApplySteerSlipAssist( target );
		var step = Math.Max( 10f, SteerSpeedDegreesPerSecond ) * dt;
		_steerDegrees = MathX.Approach( _steerDegrees, target, step );
	}

	/// <summary>
	/// GTA's speed-limited lock, as a slip limit rather than a speed table: the front tyre's slip angle is
	/// wheel angle − the angle the front axle is travelling at (β), so the wheel may turn at most β ± the
	/// assist share of <see cref="TyrePeakSlipDegrees"/> in the direction the driver asks. Only ever reduces
	/// the input; no input leaves the wheels straight. Fades in from 2 to 6 m/s; reversing gets the full lock.
	/// </summary>
	float ApplySteerSlipAssist( float targetDegrees )
	{
		var assist = Math.Max( 0f, SteerSlipAssistFraction );
		if ( assist <= 0f || MathF.Abs( targetDegrees ) < 0.01f )
			return targetDegrees;

		var halfBase = TerrainWorldUnits.MetersToEngine( WheelBaseMeters ) * 0.5f;
		var frontAxle = WorldTransform.PointToWorld( new Vector3( halfBase, 0f, 0f ) );
		var velocity = _body.GetVelocityAtPoint( frontAxle );
		var along = Vector3.Dot( velocity, WorldRotation.Forward );
		var left = -Vector3.Dot( velocity, WorldRotation.Right );
		var speed = TerrainWorldUnits.EngineToMeters( MathF.Sqrt( along * along + left * left ) );
		var fade = Math.Clamp( ( speed - 2f ) / 4f, 0f, 1f );
		if ( fade <= 0f || along <= 0f )
			return targetDegrees;

		var beta = MathX.RadianToDegree( MathF.Atan2( left, along ) );
		var slip = assist * Math.Max( 1f, TyrePeakSlipDegrees );
		var maxLock = Math.Max( 1f, MaxSteerDegrees );
		var limited = targetDegrees > 0f
			? Math.Min( targetDegrees, Math.Clamp( beta + slip, 0f, maxLock ) )
			: Math.Max( targetDegrees, Math.Clamp( beta - slip, -maxLock, 0f ) );
		return MathX.Lerp( targetDegrees, limited, fade );
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
		var bias = Math.Clamp( TyreGripBiasFront, 0.01f, 0.99f );

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

			// Traction curve: grip rises with slip angle to the peak, then falls to the slide grip. The side
			// force is that grip × load, never more than would cancel the slip in one step (so a slow or
			// barely-slipping wheel settles instead of overshooting).
			var velocityAtMount = _body.GetVelocityAtPoint( mount );
			var lateral = Vector3.Dot( velocityAtMount, wheelRight );
			var rolling = MathF.Abs( Vector3.Dot( velocityAtMount, wheelForward ) );
			var slipDegrees = MathX.RadianToDegree( MathF.Atan2( MathF.Abs( lateral ), rolling ) );
			var axleGrip = 2f * ( IsFrontWheel( i ) ? bias : 1f - bias );
			var mu = TractionCurve( slipDegrees ) * axleGrip;
			var cancelForce = perWheelMass * MathF.Abs( lateral ) / dt;
			var sideForce = -MathF.Sign( lateral ) * Math.Min( cancelForce, mu * wheel.Load );

			// Friction circle with the side force first: drive / brake get the grip the corner leaves over, so
			// holding W through a turn keeps accelerating on a widening line instead of throttle stealing the
			// rear's side grip and spinning it (an even split spun it out in a planar sim, 2026-10-21). A light
			// inside wheel slides first; a wheel in the air contributes nothing. Pushed at the ground contact,
			// where a real tyre pushes — that lever arm under the mass centre is what tips a buggy in a hard turn.
			var sliding = slipDegrees > Math.Max( 1f, TyrePeakSlipDegrees );
			var limit = ( sliding ? mu : Math.Max( 0f, TyreGripCoefficient ) * axleGrip ) * wheel.Load;
			sideForce = Math.Clamp( sideForce, -limit, limit );
			var driveRoom = MathF.Sqrt( Math.Max( 0f, limit * limit - sideForce * sideForce ) );
			var driveForce = Math.Clamp( perWheelDrive, -driveRoom, driveRoom );

			_body.ApplyForceAt( wheel.ContactPoint, wheelForward * driveForce + wheelRight * sideForce );
		}
	}

	/// <summary>
	/// Grip (μ) at a slip angle, GTA's fTractionCurveMax / Min / Lateral: a sine rise to <see cref="TyreGripCoefficient"/>
	/// at <see cref="TyrePeakSlipDegrees"/>, then a linear fall to <see cref="TyreSlideGripCoefficient"/> over the
	/// same angle again.
	/// </summary>
	float TractionCurve( float slipDegrees )
	{
		var peak = Math.Max( 1f, TyrePeakSlipDegrees );
		var max = Math.Max( 0f, TyreGripCoefficient );
		if ( slipDegrees <= peak )
			return max * MathF.Sin( slipDegrees / peak * MathF.PI * 0.5f );

		var slide = Math.Clamp( ( slipDegrees - peak ) / peak, 0f, 1f );
		return MathX.Lerp( max, Math.Max( 0f, TyreSlideGripCoefficient ), slide );
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
