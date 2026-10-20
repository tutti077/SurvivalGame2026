using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Hover drive model (<see cref="VehicleDriveModel.Hover"/>; first user: the hover platform, Mark
/// 2026-10-08 — "a flat floating platform that players can build on … kinda ice skate as it drives").
/// One <see cref="Rigidbody"/> held up by four lift pads: every physics step the simulating machine
/// traces straight down from each pad, takes the <b>highest</b> ground under the hull as the reference
/// (so the deck clears a hillside instead of tilting into it) and pushes each pad toward
/// <see cref="HoverTargetHeightMeters"/> above it with a soft spring + damper. A levelling torque keeps the
/// deck flat; yaw is free. Nothing writes velocities.
///
/// Driving is ice: W / S thrust along the deck's forward (same brake-before-reverse contract as the
/// wheels), A / D turn the hull with a yaw torque — with W / S held at <see cref="HoverYawRateDegreesPerSecond"/>,
/// with neither held it spins on the spot at <see cref="HoverSpinRateDegreesPerSecond"/> (Mark 2026-10-08:
/// "spin on a dime") — and only <see cref="HoverSideGripPerSecond"/> of the sideways speed is bled off each
/// second, so the velocity lags the heading and the platform drifts through turns. Jump (Space) raises the target height, Duck (Ctrl) lowers it, between
/// <see cref="HoverMinHeightMeters"/> and <see cref="HoverMaxHeightMeters"/>. No power (empty cell slot)
/// = no thrust, no climb, and the target sinks to the minimum at <see cref="HoverUnpoweredSinkRateMetersPerSecond"/>
/// — the deck never falls out of the sky onto what is built on it.
/// All designer values are meters / seconds; converted once here.
/// </summary>
public sealed partial class Vehicle
{
	[Property, Group( "Hover" ), Title( "Hull Bottom (m)" ), Description( "Origin to the underside of the lift pods: hover height is measured from the ground to this, not to the origin." )]
	public float HoverHullBottomMeters { get; set; } = 0.55f;

	[Property, Group( "Hover" ), Title( "Min Height (m)" ), Description( "Lowest the underside may hover above the ground; also the height it spawns at and sinks to when unpowered." )]
	public float HoverMinHeightMeters { get; set; } = 1f;

	[Property, Group( "Hover" ), Title( "Max Height (m)" )]
	public float HoverMaxHeightMeters { get; set; } = 30f;

	[Property, Group( "Hover" ), Title( "Climb Rate (m/s)" ), Description( "How fast Space / Ctrl move the target height." )]
	public float HoverClimbRateMetersPerSecond { get; set; } = 3f;

	[Property, Group( "Hover" ), Title( "Pad Spacing Length (m)" ), Description( "Distance between the front and rear lift pads along the deck." )]
	public float HoverPadLengthMeters { get; set; } = 4.4f;

	[Property, Group( "Hover" ), Title( "Pad Spacing Width (m)" ), Description( "Distance between the left and right lift pads across the deck." )]
	public float HoverPadWidthMeters { get; set; } = 2.8f;

	[Property, Group( "Hover" ), Title( "Lift Frequency (Hz)" ), Description( "Softness of the cushion. 1 Hz settles a height change in about a second without bouncing." )]
	public float HoverLiftFrequencyHz { get; set; } = 1f;

	[Property, Group( "Hover" ), Title( "Lift Damping Ratio" )]
	public float HoverLiftDampingRatio { get; set; } = 1f;

	[Property, Group( "Hover" ), Title( "Lift Limit (× pad weight)" ), Description( "Most one pad may push or pull, as a multiple of the static load on that pad. Keeps a sudden ledge from launching the deck." )]
	public float HoverLiftLimitMultiple { get; set; } = 2.5f;

	[Property, Group( "Hover" ), Title( "Level Torque (rad/s²)" ), Description( "Torque toward a flat deck per radian of tilt." )]
	public float HoverLevelStrength { get; set; } = 8f;

	[Property, Group( "Hover" ), Title( "Level Damping (1/s)" ), Description( "Damping on pitch / roll rate (yaw is left alone)." )]
	public float HoverLevelDamping { get; set; } = 4f;

	[Property, Group( "Hover" ), Title( "Yaw Rate (deg/s)" ), Description( "Turn rate A / D ask for while W or S is held (steering on the move)." )]
	public float HoverYawRateDegreesPerSecond { get; set; } = 40f;

	[Property, Group( "Hover" ), Title( "Spin Rate (deg/s)" ), Description( "Turn rate A / D ask for with neither W nor S held: the platform spins on the spot." )]
	public float HoverSpinRateDegreesPerSecond { get; set; } = 60f;

	[Property, Group( "Hover" ), Title( "Yaw Response (1/s)" ), Description( "How quickly the hull's yaw rate reaches what the stick asks for. Lower = lazier, more boat-like." )]
	public float HoverYawResponse { get; set; } = 2.5f;

	[Property, Group( "Hover" ), Title( "Side Grip (1/s)" ), Description( "Share of the sideways speed bled off per second. 0 = pure ice (never stops sliding), 10 = rails. 0.7 keeps a drift going for a couple of seconds after a turn." )]
	public float HoverSideGripPerSecond { get; set; } = 0.7f;

	[Property, Group( "Hover" ), Title( "Unpowered Sink Rate (m/s)" ), Description( "With no power cell the target height drifts down to Min Height at this rate." )]
	public float HoverUnpoweredSinkRateMetersPerSecond { get; set; } = 1f;

	/// <summary>Height the underside aims for above the highest ground under the pads (meters). Simulating machine writes; HUD reads anywhere.</summary>
	[Sync]
	public float HoverTargetHeightMeters { get; private set; }

	/// <summary>Debug / HUD: the last measured clearance under the pads (meters), NaN when no ground was in reach.</summary>
	public float HoverClearanceMeters { get; private set; } = float.NaN;

	public bool UsesVerticalInput => IsHover;

	bool _hoverTargetSeeded;

	/// <summary>Dev: set the target height from the console (clamped to the min / max).</summary>
	public void SetHoverTargetHeight( float meters )
	{
		_hoverTargetSeeded = true;
		HoverTargetHeightMeters = Math.Clamp( meters, Math.Max( 0.1f, HoverMinHeightMeters ), Math.Max( HoverMinHeightMeters, HoverMaxHeightMeters ) );
	}

	public string DebugHover()
		=> $"target={HoverTargetHeightMeters:0.0}m clearance={(float.IsNaN( HoverClearanceMeters ) ? "none" : HoverClearanceMeters.ToString( "0.0" ) + "m")} up.z={WorldRotation.Up.z:0.00} spin={MathX.RadianToDegree( _body?.AngularVelocity.Length ?? 0f ):0}°/s v={TerrainWorldUnits.EngineToMeters( _body?.Velocity.Length ?? 0f ):0.0}m/s";

	/// <summary>Pad i in hull space: 0/1 front (left / right), 2/3 rear.</summary>
	Vector3 HoverPadLocal( int i )
	{
		var halfLength = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, HoverPadLengthMeters ) ) * 0.5f;
		var halfWidth = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, HoverPadWidthMeters ) ) * 0.5f;
		var x = i < 2 ? halfLength : -halfLength;
		var y = ( i % 2 == 0 ) ? halfWidth : -halfWidth;
		return new Vector3( x, y, 0f );
	}

	void SimulateHover( float throttle, float steer, float vertical, float dt )
	{
		TickHoverTarget( vertical, dt );
		SimulateHoverLift( dt );
		SimulateHoverLevel( dt );
		SimulateHoverDrive( throttle, dt );
		SimulateHoverYaw( steer, MathF.Abs( throttle ) > 0.01f, dt );
	}

	void TickHoverTarget( float vertical, float dt )
	{
		var min = Math.Max( 0.1f, HoverMinHeightMeters );
		var max = Math.Max( min, HoverMaxHeightMeters );

		if ( !_hoverTargetSeeded )
		{
			_hoverTargetSeeded = true;
			if ( HoverTargetHeightMeters <= 0f )
				HoverTargetHeightMeters = min;
		}

		if ( HasFuel )
		{
			if ( MathF.Abs( vertical ) > 0.01f )
				HoverTargetHeightMeters = Math.Clamp( HoverTargetHeightMeters + vertical * Math.Max( 0f, HoverClimbRateMetersPerSecond ) * dt, min, max );
			return;
		}

		if ( HoverTargetHeightMeters > min )
			HoverTargetHeightMeters = Math.Max( min, HoverTargetHeightMeters - Math.Max( 0f, HoverUnpoweredSinkRateMetersPerSecond ) * dt );
	}

	readonly float[] _hoverPadGroundZ = new float[4];
	readonly bool[] _hoverPadHit = new bool[4];

	/// <summary>
	/// Four lift pads, one soft spring each, all measured from the highest ground under the hull so the
	/// deck stays flat over a slope. A pad with no ground in reach (over a chasm) just carries its share
	/// of the weight with vertical damping, so the deck floats on at its current height.
	/// </summary>
	void SimulateHoverLift( float dt )
	{
		var mass = Math.Max( 1f, _body.Mass );
		var sprung = mass * 0.25f;
		var omega = 2f * MathF.PI * Math.Max( 0.2f, HoverLiftFrequencyHz );
		var k = sprung * omega * omega;
		var c = 2f * Math.Max( 0f, HoverLiftDampingRatio ) * MathF.Sqrt( k * sprung );
		var gravityUnits = Math.Max( 1f, Scene.PhysicsWorld.Gravity.Length );
		var limit = Math.Max( 1f, HoverLiftLimitMultiple ) * sprung * gravityUnits;

		var hullBottom = TerrainWorldUnits.MetersToEngine( Math.Max( 0f, HoverHullBottomMeters ) );
		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( HoverMinHeightMeters, HoverMaxHeightMeters ) + Math.Max( 0f, HoverHullBottomMeters ) + 10f );

		// Pass 1: ground under each pad (world down — a cushion measures height, not hull-normal distance).
		var highest = float.MinValue;
		var anyHit = false;
		for ( var i = 0; i < 4; i++ )
		{
			var pad = WorldTransform.PointToWorld( HoverPadLocal( i ) );
			var tr = Scene.Trace.Ray( pad, pad - Vector3.Up * reach )
				.IgnoreGameObjectHierarchy( GameObject )
				.WithoutTags( "player", "trigger", "worlddrop", "buildpreview" )
				.Run();
			_hoverPadHit[i] = tr.Hit && !tr.StartedSolid;
			_hoverPadGroundZ[i] = _hoverPadHit[i] ? tr.HitPosition.z : 0f;
			if ( _hoverPadHit[i] )
			{
				anyHit = true;
				highest = MathF.Max( highest, tr.HitPosition.z );
			}
		}

		var targetUnits = TerrainWorldUnits.MetersToEngine( HoverTargetHeightMeters ) + hullBottom;
		HoverClearanceMeters = anyHit ? TerrainWorldUnits.EngineToMeters( WorldPosition.z - hullBottom - highest ) : float.NaN;

		// Pass 2: springs. Every pad aims for the same deck height above the highest ground; the per-pad
		// error then only differs by tilt, which is what levels the deck through the physics. Each pad also
		// carries its share of the weight outright: a bare spring sags g / ω² under the load it holds (0.5 m at
		// 1 Hz — measured 2026-10-08, the deck sat at 0.5 m with the target at the 1 m minimum).
		for ( var i = 0; i < 4; i++ )
		{
			var pad = WorldTransform.PointToWorld( HoverPadLocal( i ) );
			var upwardSpeed = Vector3.Dot( _body.GetVelocityAtPoint( pad ), Vector3.Up );
			float force;
			if ( anyHit )
			{
				var error = ( highest + targetUnits ) - pad.z;
				force = sprung * gravityUnits + k * error - c * upwardSpeed;
			}
			else
			{
				force = sprung * gravityUnits - c * upwardSpeed;
			}

			force = Math.Clamp( force, -limit, limit );
			_body.ApplyForceAt( pad, Vector3.Up * force );
		}
	}

	/// <summary>Torque toward a flat deck plus damping on pitch / roll rate — yaw untouched so the hull can turn.</summary>
	void SimulateHoverLevel( float dt )
	{
		var mass = Math.Max( 1f, _body.Mass );
		var length = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, HoverPadLengthMeters ) );
		var width = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, HoverPadWidthMeters ) );
		var height = TerrainWorldUnits.MetersToEngine( 0.6f );
		var inertiaTilt = mass / 12f * ( Math.Max( length, width ) * Math.Max( length, width ) + height * height );

		var up = WorldRotation.Up;
		var axis = Vector3.Cross( up, Vector3.Up );
		var torque = axis * ( Math.Max( 0f, HoverLevelStrength ) * inertiaTilt );

		var spin = _body.AngularVelocity;
		var tiltSpin = spin - Vector3.Up * Vector3.Dot( spin, Vector3.Up );
		torque -= tiltSpin * ( Math.Max( 0f, HoverLevelDamping ) * inertiaTilt );

		if ( torque.LengthSquared > 1e-6f )
			_body.ApplyTorque( torque );
	}

	/// <summary>Thrust along the deck's planar forward with the wheels' brake-before-reverse contract; weak side grip = ice.</summary>
	void SimulateHoverDrive( float throttle, float dt )
	{
		var mass = Math.Max( 1f, _body.Mass );
		var forward = WorldRotation.Forward.WithZ( 0f );
		if ( forward.LengthSquared < 1e-4f )
			return;
		forward = forward.Normal;
		var right = Vector3.Cross( forward, Vector3.Up ).Normal;   // Forward × Up = Right in this engine's frame

		var velocity = _body.Velocity;
		var forwardSpeed = TerrainWorldUnits.EngineToMeters( Vector3.Dot( velocity, forward ) );
		var sideSpeedUnits = Vector3.Dot( velocity, right );

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

		if ( !driving )
			accel = RollTowardStop( forwardSpeed, dt, HasFuel ? RollingDragMetersPerSecond2 : OutOfFuelCoastDragMetersPerSecond2 );

		// Ice: only a fraction of the sideways speed goes each second, never more than all of it in one step.
		var gripStep = Math.Min( 1f, Math.Max( 0f, HoverSideGripPerSecond ) * dt );
		var sideAccelUnits = -sideSpeedUnits * gripStep / dt;

		var force = forward * ( mass * TerrainWorldUnits.MetersToEngine( accel ) ) + right * ( mass * sideAccelUnits );
		if ( force.LengthSquared > 1e-6f )
			_body.ApplyForce( force );
	}

	/// <summary>
	/// Yaw torque toward the asked turn rate: the hull turns, the velocity follows late (drift). A / D with W
	/// or S held steer at <see cref="HoverYawRateDegreesPerSecond"/>; A / D alone spin the hull on the spot at
	/// <see cref="HoverSpinRateDegreesPerSecond"/>.
	/// </summary>
	void SimulateHoverYaw( float steer, bool throttleHeld, float dt )
	{
		var mass = Math.Max( 1f, _body.Mass );
		var length = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, HoverPadLengthMeters ) );
		var width = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, HoverPadWidthMeters ) );
		var inertiaYaw = mass / 12f * ( length * length + width * width );

		var rate = throttleHeld ? HoverYawRateDegreesPerSecond : HoverSpinRateDegreesPerSecond;
		var targetRate = MathX.DegreeToRadian( Math.Clamp( steer, -1f, 1f ) * Math.Max( 0f, rate ) );
		var currentRate = Vector3.Dot( _body.AngularVelocity, Vector3.Up );

		// Hands off: the hull's own yaw winds down too, otherwise a drifting platform spins forever.
		var response = Math.Max( 0.1f, HoverYawResponse );
		var torque = inertiaYaw * ( targetRate - currentRate ) * response;
		if ( MathF.Abs( torque ) > 1e-4f )
			_body.ApplyTorque( Vector3.Up * torque );
	}

	/// <summary>Planar speed, signed by the hull's forward (a sideways drift still reads as speed on the HUD).</summary>
	float HoverSignedPlanarSpeedMetersPerSecond()
	{
		var planar = _body.Velocity.WithZ( 0f );
		var sign = Vector3.Dot( planar, WorldRotation.Forward.WithZ( 0f ) ) < 0f ? -1f : 1f;
		return sign * TerrainWorldUnits.EngineToMeters( planar.Length );
	}
}
