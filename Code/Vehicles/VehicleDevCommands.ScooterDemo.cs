using System;
using Sandbox;

namespace Survival;

/// <summary>
/// <c>scooter_demo</c>: a filmed run for the nearest bike-model vehicle (the pod scooter), from where it stands
/// and along the way it faces: a rumble strip of low kerbs (the swept wheels roll over them), then a kicker
/// ramp; full throttle in, S held in the air (the slow tip back), then a left and a right sweep and a brake.
/// A chase camera trails it (its id is logged for the editor's camera screenshots). Logs air time, how far the
/// nose tipped in the air, the lowest up.z on the ground and the top speed. Dev only; needs ~70 m of open ground ahead.
/// </summary>
public static partial class VehicleDevCommands
{
	[ConCmd( "scooter_demo" )]
	public static void ConCmdScooterDemo( float rampDegrees = 20f )
	{
		var vehicle = FindTestVehicle( "scooter_demo" );
		if ( vehicle is null )
			return;
		if ( !vehicle.IsBike )
		{
			Log.Warning( "[Scooter demo] the nearest vehicle is not a bike (vehicle_spawn vehicle_pod_scooter first)." );
			return;
		}

		// The scripted-test base parks the vehicle on the known stretch of testscene1; this run starts where the scooter stands.
		var keep = vehicle.WorldTransform;
		var demo = new ScooterDemo( vehicle, keep, Math.Clamp( rampDegrees, 5f, 35f ) );
		vehicle.WorldPosition = keep.Position;
		vehicle.WorldRotation = keep.Rotation;
		vehicle.GameObject.Transform.ClearInterpolation();
		_test = demo;
		Log.Info( "[Scooter demo] started." );
	}

	sealed class ScooterDemo : ScriptedTest
	{
		static GameObject _props;
		static GameObject _camera;

		readonly Vector3 _origin;
		readonly Vector3 _forward;
		readonly float _rampStartUnits;
		Vector3 _camPos;
		Vector3 _camLook;
		bool _camInit;
		bool _jumped;
		bool _landed;
		float _airSeconds;
		float _pitchAtTakeoff;
		float _maxTipDegrees;
		float _minUpZGrounded = 1f;
		float _topSpeed;
		float _maxKerbUpZLoss;

		public ScooterDemo( Vehicle vehicle, Transform start, float rampDegrees ) : base( vehicle )
		{
			var scene = vehicle.Scene;
			_origin = start.Position;
			_forward = start.Rotation.Forward.WithZ( 0f ).Normal;
			var yaw = start.Rotation.Yaw();

			_props?.Destroy();
			_props = new GameObject( true, "ScooterDemoProps" );

			// Rumble strip: six kerbs 12 cm tall, 30 cm deep, from 12 m on, 1.6 m apart.
			for ( var i = 0; i < 6; i++ )
				SpawnBox( scene, TerrainWorldUnits.MetersToEngine( 12f + i * 1.6f ), yaw, new Vector3( 12f, 160f, 4.8f ), 0f, new Color( 0.2f, 0.55f, 0.75f ), 0f );

			// Kicker ramp at 34 m: 4 m long, 6 m wide.
			_rampStartUnits = TerrainWorldUnits.MetersToEngine( 34f );
			SpawnBox( scene, _rampStartUnits, yaw, new Vector3( 160f, 240f, 6f ), rampDegrees, new Color( 0.75f, 0.28f, 0.08f ), 3f );

			if ( _camera is null || !_camera.IsValid() )
			{
				_camera = new GameObject( true, "ScooterDemoCamera" );
				var cam = _camera.Components.Create<CameraComponent>();
				cam.FieldOfView = 62f;
				cam.ZNear = 4f;
				cam.ZFar = 60000f;
				cam.IsMainCamera = false;
				cam.Priority = -100;
			}
			Log.Info( $"[Scooter demo] camera id={_camera.Id}" );
		}

		/// <summary>A dev box across the run at <paramref name="alongUnits"/>: its near edge on the ground, tilted up by <paramref name="pitchDeg"/>.</summary>
		void SpawnBox( Scene scene, float alongUnits, float yaw, Vector3 size, float pitchDeg, Color tint, float sink )
		{
			var probe = _origin + _forward * alongUnits;
			var tr = scene.Trace.Ray( probe + Vector3.Up * 400f, probe - Vector3.Up * 400f )
				.WithoutTags( "player", "trigger", "worlddrop", "vehicle" )
				.Run();
			var ground = tr.Hit ? tr.HitPosition : probe;

			var rad = MathF.PI * pitchDeg / 180f;
			var box = new GameObject( true, "ScooterDemoBox" );
			box.SetParent( _props, false );
			box.WorldRotation = Rotation.FromYaw( yaw ) * Rotation.FromPitch( -pitchDeg );
			box.WorldPosition = ground + _forward * ( MathF.Cos( rad ) * size.x * 0.5f )
			                    + Vector3.Up * ( MathF.Sin( rad ) * size.x * 0.5f + ( pitchDeg > 0f ? -size.z * 0.5f : size.z * 0.5f ) - sink );
			box.WorldScale = size / 50f;   // dev box is 50^3

			var renderer = box.Components.Create<ModelRenderer>();
			renderer.Model = Model.Load( "models/dev/box.vmdl" );
			renderer.Tint = tint;
			var collider = box.Components.Create<BoxCollider>();
			collider.Scale = new Vector3( 50f, 50f, 50f );
			collider.Static = true;
		}

		protected override void OnTick( float speed )
		{
			var dt = Math.Max( 0.001f, _vehicle.LastStepDelta );
			var airborne = _vehicle.GroundedWheelCount == 0;
			var along = Vector3.Dot( _vehicle.WorldPosition - _origin, _forward );
			_topSpeed = Math.Max( _topSpeed, speed );

			if ( airborne && along > _rampStartUnits && !_landed )
			{
				if ( !_jumped )
				{
					_jumped = true;
					_pitchAtTakeoff = _vehicle.WorldRotation.Pitch();
				}
				_airSeconds += dt;
				_maxTipDegrees = Math.Max( _maxTipDegrees, MathF.Abs( MathX.DeltaDegrees( _pitchAtTakeoff, _vehicle.WorldRotation.Pitch() ) ) );
			}
			else if ( _jumped && !airborne && !_landed && _airSeconds > 0.15f )
			{
				_landed = true;
				Log.Info( $"[Scooter demo] landed: air {_airSeconds:0.00} s, nose tipped {_maxTipDegrees:0} deg with S held, up.z on touchdown {_vehicle.WorldRotation.Up.z:0.00}" );
			}

			if ( !airborne )
			{
				_minUpZGrounded = Math.Min( _minUpZGrounded, _vehicle.WorldRotation.Up.z );
				if ( along < _rampStartUnits )
					_maxKerbUpZLoss = Math.Max( _maxKerbUpZLoss, 1f - _vehicle.WorldRotation.Up.z );
			}

			if ( _camera is null || !_camera.IsValid() )
				return;

			// Rear three-quarter chase, close enough to read the wheels.
			var vehiclePos = _vehicle.WorldPosition;
			var forward = _vehicle.WorldRotation.Forward.WithZ( 0f ).Normal;
			if ( forward.LengthSquared < 0.01f )
				forward = _forward;
			var right = Vector3.Cross( forward, Vector3.Up );
			var wantPos = vehiclePos - forward * 125f + right * 70f + Vector3.Up * 48f;
			var wantLook = vehiclePos + Vector3.Up * 14f;
			if ( !_camInit )
			{
				_camInit = true;
				_camPos = wantPos;
				_camLook = wantLook;
			}
			var flat = Vector3.Lerp( _camPos, wantPos, 1f - MathF.Exp( -dt * 6f ) );
			var z = MathX.Lerp( _camPos.z, wantPos.z, 1f - MathF.Exp( -dt * 2f ) );
			_camPos = flat.WithZ( z );
			_camLook = Vector3.Lerp( _camLook, wantLook, 1f - MathF.Exp( -dt * 12f ) );
			_camera.WorldPosition = _camPos;
			_camera.WorldRotation = Rotation.LookAt( _camLook - _camPos, Vector3.Up );
		}

		/// <summary>Small steering correction back onto the start line (kerbs nudge the heading), so the run meets the ramp.</summary>
		float HoldLine()
		{
			var leftward = Vector3.Cross( Vector3.Up, _forward );
			var offsetMeters = TerrainWorldUnits.EngineToMeters( Vector3.Dot( _vehicle.WorldPosition - _origin, leftward ) );
			var headingError = MathX.DeltaDegrees( Rotation.LookAt( _forward ).Yaw(), _vehicle.WorldRotation.Yaw() );   // + = pointing left of the line
			return Math.Clamp( -0.10f * offsetMeters - 0.03f * headingError, -0.35f, 0.35f );
		}

		protected override bool RunPhases( float t, float speed, out float throttle, out float steer )
		{
			throttle = 0f;
			steer = 0f;
			switch ( Phase )
			{
				case 0:   // flat out over the kerbs and off the ramp; S in the air = the slow tip back
					throttle = _jumped && !_landed ? -1f : 1f;
					if ( !_jumped )
						steer = HoldLine();
					if ( _landed || t >= 12f ) NextPhase();
					return true;
				case 1:   // roll out straight
					throttle = 1f;
					if ( t >= 0.8f ) NextPhase();
					return true;
				case 2:   // sweep left
					throttle = 1f;
					steer = 1f;
					if ( t >= 1.8f ) NextPhase();
					return true;
				case 3:   // sweep right
					throttle = 1f;
					steer = -1f;
					if ( t >= 2.4f ) NextPhase();
					return true;
				case 4:   // brake to a stop (S stops, then would reverse: cut it at walking pace)
					throttle = speed > 1f ? -1f : 0f;
					if ( t >= 4f || speed < 0.5f )
					{
						Log.Info( $"[Scooter demo] done: top {_topSpeed:0.0} m/s ({_topSpeed * 2.23694f:0} mph), jumped={_jumped} landed={_landed}, lowest up.z on the ground {_minUpZGrounded:0.00} (kerbs: {1f - _maxKerbUpZLoss:0.00}), flipped={_vehicle.IsFlipped}" );
						return false;
					}
					return true;
			}
			return false;
		}
	}
}
