using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Two-wheel drive model (<see cref="VehicleDriveModel.Bike"/>, the pod scooter). It shares the springs, the
/// traction curve, steering, fuel, seats and health with the buggy (<c>Vehicle.Physics.cs</c>); this file
/// holds what only a bike needs:
///
/// <list type="bullet">
/// <item><b>Round wheels</b> (Mark 2026-11-19: "the wheels need to be like actually perfect"): each wheel is a
/// sphere of the wheel's radius swept down its spring travel, not a ray from the hub. A ray only knows the
/// ground straight under the axle, so a rock or a kerb arrives as a step; the swept wheel touches it with its
/// leading edge first and rolls up it, the way a tyre does.</item>
/// <item><b>Balance</b>: with a wheel on the ground a torque about the chassis' forward axis holds the bike
/// upright and leans it into turns (lean angle from speed × yaw rate, capped). The tyres' side force acts level
/// with the mass centre, so cornering never levers the bike over; pitch and yaw stay free physics.</item>
/// <item><b>Air control</b> (Mark: "if the player is mid air and they hit W it should tip it forward, same
/// with ASD ... very slow tip so you can make jumps"): with both wheels off the ground W / S pitch the nose
/// down / up and A / D roll left / right, as a gentle torque toward a slow turn rate. No key = no torque:
/// whatever spin the bike has, it keeps.</item>
/// </list>
/// A bike that lands on its side has no wheel down, so nothing holds it up and it stays down: E on it stands
/// it back up (<see cref="StartBikeRighting"/>).
/// </summary>
public sealed partial class Vehicle
{
	[Property, Group( "Bike" ), Title( "Balance Frequency (Hz)" ), Description( "How firmly the bike is held at its lean angle while a wheel is on the ground. Higher = snappier, stiffer." )]
	public float BikeBalanceFrequencyHz { get; set; } = 2.5f;

	[Property, Group( "Bike" ), Title( "Balance Damping Ratio" ), Description( "1 = settles without wobbling." )]
	public float BikeBalanceDampingRatio { get; set; } = 1f;

	[Property, Group( "Bike" ), Title( "Max Lean (deg)" ), Description( "Most the bike leans into a turn." )]
	public float BikeMaxLeanDegrees { get; set; } = 32f;

	[Property, Group( "Bike" ), Title( "Lean Response (1/s)" ), Description( "How fast the lean follows the turn. Lower = lazier roll-in." )]
	public float BikeLeanResponse { get; set; } = 6f;

	[Property, Group( "Bike" ), Title( "Air Pitch Rate (deg/s)" ), Description( "W / S in the air: the pitch rate the bike works toward. 0 = no air pitch." )]
	public float AirPitchRateDegreesPerSecond { get; set; } = 90f;

	[Property, Group( "Bike" ), Title( "Air Roll Rate (deg/s)" ), Description( "A / D in the air: the roll rate the bike works toward. 0 = no air roll." )]
	public float AirRollRateDegreesPerSecond { get; set; } = 60f;

	[Property, Group( "Bike" ), Title( "Air Control Response (1/s)" ), Description( "How quickly a held key reaches the air rate. Low = a slow tip (about 1 / this seconds to get there)." )]
	public float AirControlResponse { get; set; } = 2f;

	[Property, Group( "Bike" ), Title( "Stand-Up Time (s)" ), Description( "How long the balance torque runs after E on a fallen bike." )]
	public float BikeRightingSeconds { get; set; } = 1.5f;

	/// <summary>Body proportions for the inertia estimates behind the balance / air torques (m): frame height and width.
	/// Kept on the small side: an estimate above the body's real inertia turns the balance gains up past stable.</summary>
	const float BikeBodyHeightMeters = 0.7f;
	const float BikeBodyWidthMeters = 0.3f;
	/// <summary>Angular acceleration cap of the balance torque (rad/s²): a fallen bike stands up, it does not snap up.</summary>
	const float BikeBalanceMaxAcceleration = 40f;

	float _bikeLeanRadians;
	float _bikeRightingRemaining;
	GameObject _bikeSteerVisual;
	GameObject _bikeForkVisual;
	GameObject _bikeSwingArmVisual;
	Rotation _bikeSteerRest = Rotation.Identity;
	Vector3 _bikeForkRest;
	Vector3 _bikeSwingArmRest;

	/// <summary>Debug: lean the balance is holding (deg, + = left) and whether the righting torque runs.</summary>
	public string DebugBike()
		=> $"lean={MathX.RadianToDegree( _bikeLeanRadians ):0.0}° up.z={WorldRotation.Up.z:0.00} righting={_bikeRightingRemaining:0.0}s grounded={_groundedWheels}";

	/// <summary>
	/// The wheel as a sphere swept from just above its fully compressed position down to full droop. Starts
	/// <c>0.75 × radius</c> above the mount so a wheel pushed to the bump stop is still a clean hit, not a
	/// trace that begins inside the ground (by then the hull is on the floor anyway).
	/// </summary>
	SceneTraceResult TraceBikeWheel( Vector3 mount, Vector3 up, float radius, float springLength, out float sweepLength )
	{
		var margin = radius * 0.75f;
		sweepLength = springLength + margin;
		return Scene.Trace.Sphere( radius, mount + up * margin, mount - up * springLength )
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "player", "trigger", "worlddrop" )
			.Run();
	}

	/// <summary>Where a tyre's side force acts on a bike: above the contact, level with the mass centre.</summary>
	Vector3 BikeSideForcePoint( Vector3 contact )
	{
		var up = WorldRotation.Up;
		var massCentreZ = _body.OverrideMassCenter ? _body.MassCenterOverride.z : 0f;
		var height = Vector3.Dot( WorldPosition - contact, up ) + massCentreZ;
		return contact + up * height;
	}

	float BikeRollInertia( float mass )
	{
		var h = TerrainWorldUnits.MetersToEngine( BikeBodyHeightMeters );
		var w = TerrainWorldUnits.MetersToEngine( BikeBodyWidthMeters );
		return mass / 12f * ( h * h + w * w );
	}

	float BikePitchInertia( float mass )
	{
		var h = TerrainWorldUnits.MetersToEngine( BikeBodyHeightMeters );
		var l = TerrainWorldUnits.MetersToEngine( WheelBaseMeters + 2f * WheelRadiusMeters );
		return mass / 12f * ( h * h + l * l );
	}

	void StartBikeRighting()
	{
		_bikeRightingRemaining = Math.Max( 0.2f, BikeRightingSeconds );
		_body.Sleeping = false;
		_body.Velocity = _body.Velocity;
	}

	/// <summary>Roll torque toward the lean angle while a wheel is down (or while being stood back up).</summary>
	void SimulateBikeBalance( float dt )
	{
		var righting = _bikeRightingRemaining > 0f;
		if ( righting )
			_bikeRightingRemaining -= dt;

		if ( _groundedWheels == 0 && !righting )
		{
			_bikeLeanRadians = 0f;
			return;
		}

		var forward = WorldRotation.Forward;
		var up = WorldRotation.Up;

		// Upright = world up as seen across the bike (a bike stays vertical on a side slope and follows a climb).
		var upright = Vector3.Up - forward * Vector3.Dot( Vector3.Up, forward );
		if ( upright.LengthSquared < 1e-4f )
			return;
		upright = upright.Normal;
		var leftward = Vector3.Cross( upright, forward );

		// Lean into the turn: the angle that balances the sideways acceleration of the turn against gravity.
		var gravity = Math.Max( 1f, Scene.PhysicsWorld.Gravity.Length );
		var forwardSpeed = Vector3.Dot( _body.Velocity, forward );
		var yawRate = Vector3.Dot( _body.AngularVelocity, Vector3.Up );
		var maxLean = MathX.DegreeToRadian( Math.Clamp( BikeMaxLeanDegrees, 0f, 60f ) );
		var wanted = righting || _groundedWheels == 0 ? 0f : Math.Clamp( MathF.Atan2( forwardSpeed * yawRate, gravity ), -maxLean, maxLean );
		_bikeLeanRadians = MathX.Lerp( _bikeLeanRadians, wanted, Math.Clamp( Math.Max( 0.1f, BikeLeanResponse ) * dt, 0f, 1f ) );

		var target = upright * MathF.Cos( _bikeLeanRadians ) + leftward * MathF.Sin( _bikeLeanRadians );

		// Signed roll error about the forward axis (right-handed: up × target), and the roll rate to damp.
		var error = Vector3.Dot( Vector3.Cross( up, target ), forward );
		if ( Vector3.Dot( up, target ) < 0f )
			error = error >= 0f ? 1f : -1f;   // past 90°: full correction the short way round
		var rollRate = Vector3.Dot( _body.AngularVelocity, forward );

		var omega = 2f * MathF.PI * Math.Max( 0.2f, BikeBalanceFrequencyHz );
		var acceleration = omega * omega * error - 2f * Math.Max( 0f, BikeBalanceDampingRatio ) * omega * rollRate;
		acceleration = Math.Clamp( acceleration, -BikeBalanceMaxAcceleration, BikeBalanceMaxAcceleration );

		var torque = BikeRollInertia( Math.Max( 1f, _body.Mass ) ) * acceleration;
		if ( MathF.Abs( torque ) > 1e-4f )
			_body.ApplyTorque( forward * torque );
	}

	/// <summary>
	/// Both wheels off the ground: W / S tip the nose down / up, A / D roll left / right — a torque toward a
	/// slow rate while the key is held, nothing when it is not.
	/// </summary>
	void SimulateBikeAirControl( float throttle, float steer, float dt )
	{
		if ( _groundedWheels > 0 || _bikeRightingRemaining > 0f )
			return;

		var response = Math.Max( 0.05f, AirControlResponse );
		var mass = Math.Max( 1f, _body.Mass );
		var spin = _body.AngularVelocity;

		// Nose down is a right-handed turn about the chassis' left axis.
		if ( MathF.Abs( throttle ) > 0.01f && AirPitchRateDegreesPerSecond > 0f )
		{
			var axis = WorldRotation.Left;
			var targetRate = MathX.DegreeToRadian( AirPitchRateDegreesPerSecond ) * Math.Clamp( throttle, -1f, 1f );
			var torque = BikePitchInertia( mass ) * ( targetRate - Vector3.Dot( spin, axis ) ) * response;
			_body.ApplyTorque( axis * torque );
		}

		// Rolling left is a right-handed turn about the chassis' backward axis (steer +1 = left).
		if ( MathF.Abs( steer ) > 0.01f && AirRollRateDegreesPerSecond > 0f )
		{
			var axis = WorldRotation.Backward;
			var targetRate = MathX.DegreeToRadian( AirRollRateDegreesPerSecond ) * Math.Clamp( steer, -1f, 1f );
			var torque = BikeRollInertia( mass ) * ( targetRate - Vector3.Dot( spin, axis ) ) * response;
			_body.ApplyTorque( axis * torque );
		}
	}

	/// <summary>
	/// Optional moving parts on the prefab: <c>Steer</c> (handlebar + fork tubes, yaws with the steering about its
	/// own up axis), <c>ForkLower</c> (fork sliders + mudguard: rides up and down with the front wheel and yaws
	/// with it) and <c>SwingArm</c> (pivots at its origin so its tail follows the rear wheel).
	/// </summary>
	void CollectBikeVisuals()
	{
		_bikeSteerVisual = FindChildByName( "Steer" );
		_bikeForkVisual = FindChildByName( "ForkLower" );
		_bikeSwingArmVisual = FindChildByName( "SwingArm" );
		if ( _bikeSteerVisual is { IsValid: true } )
			_bikeSteerRest = _bikeSteerVisual.LocalRotation;
		if ( _bikeForkVisual is { IsValid: true } )
			_bikeForkRest = _bikeForkVisual.LocalPosition;
		if ( _bikeSwingArmVisual is { IsValid: true } )
			_bikeSwingArmRest = _bikeSwingArmVisual.LocalPosition;
	}

	float BikeWheelDrop( int i, float springLength )
		=> _wheels[i].Grounded ? Math.Clamp( springLength - _wheels[i].Compression, 0f, springLength ) : springLength;

	void UpdateBikeVisuals( float springLength )
	{
		if ( _bikeSteerVisual is { IsValid: true } )
			_bikeSteerVisual.LocalRotation = _bikeSteerRest * Rotation.FromYaw( _steerDegrees );

		if ( _bikeForkVisual is { IsValid: true } )
		{
			_bikeForkVisual.LocalPosition = _bikeForkRest.WithZ( -BikeWheelDrop( 0, springLength ) );
			_bikeForkVisual.LocalRotation = Rotation.FromYaw( _steerDegrees );
		}

		if ( _bikeSwingArmVisual is { IsValid: true } )
		{
			// The arm's tail (its local -X end) points at the rear hub.
			var hub = WheelMountLocal( 1 ) - Vector3.Up * BikeWheelDrop( 1, springLength );
			var toHub = hub - _bikeSwingArmRest;
			var pitch = MathX.RadianToDegree( MathF.Atan2( toHub.z, -toHub.x ) );
			_bikeSwingArmVisual.LocalRotation = Rotation.FromPitch( pitch );
		}
	}
}
