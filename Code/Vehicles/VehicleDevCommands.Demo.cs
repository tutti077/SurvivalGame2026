using System;
using Sandbox;

namespace Survival;

/// <summary>
/// <c>vehicle_demo</c>: a filmed drive for the local driver — two dev-box ramps along +x from the
/// test start, a chase camera that trails the buggy (its object id is logged so the editor can
/// screenshot it), then a scripted run: full throttle over both ramps, a left and a right sweep,
/// a brake and a short reverse. Dev only, same shape as <c>vehicle_test</c>.
/// </summary>
public static partial class VehicleDevCommands
{
	/// <summary>Usage: <c>vehicle_demo</c> — the local pawn must be driving (vehicle_spawn, vehicle_enter, vehicle_fuel first).</summary>
	[ConCmd( "vehicle_demo" )]
	public static void ConCmdDemo()
	{
		var pawn = FindLocalPawn();
		var vehicle = pawn?.Components.Get<PlayerMovement>()?.SeatedVehicle;
		if ( vehicle is null || !vehicle.IsValid() )
		{
			Log.Warning( "[Vehicle] vehicle_demo: the local pawn must be driving (vehicle_spawn, vehicle_enter, vehicle_fuel first)." );
			return;
		}

		_test = new ScriptedDemo( vehicle );
		Log.Info( "[Vehicle demo] started." );
	}

	sealed class ScriptedDemo : ScriptedTest
	{
		static GameObject _props;
		static GameObject _camera;

		Vector3 _camPos;
		Vector3 _camLook;
		bool _camInit;

		public ScriptedDemo( Vehicle vehicle ) : base( vehicle )
		{
			var scene = vehicle.Scene;
			_props?.Destroy();
			_props = new GameObject( true, "VehicleDemoProps" );

			// Three ramps on the +x run, each steeper than the last (hit at ~4.4 s, ~6.3 s and ~8.4 s at full speed).
			SpawnRamp( scene, 600f + 60f * 40f, lengthUnits: 320f, widthUnits: 260f, pitchDeg: 12f );
			SpawnRamp( scene, 600f + 110f * 40f, lengthUnits: 400f, widthUnits: 260f, pitchDeg: 17f );
			SpawnRamp( scene, 600f + 165f * 40f, lengthUnits: 440f, widthUnits: 260f, pitchDeg: 23f );

			if ( _camera is null || !_camera.IsValid() )
			{
				_camera = new GameObject( true, "VehicleDemoCamera" );
				var cam = _camera.Components.Create<CameraComponent>();
				cam.FieldOfView = 75f;
				cam.ZNear = 5f;
				cam.ZFar = 60000f;
				cam.IsMainCamera = false;
				cam.Priority = -100;
			}
			Log.Info( $"[Vehicle demo] camera id={_camera.Id}" );
		}

		void SpawnRamp( Scene scene, float x, float lengthUnits, float widthUnits, float pitchDeg )
		{
			var probe = new Vector3( x, 0f, 0f );
			var tr = scene.Trace.Ray( probe + Vector3.Up * 400f, probe - Vector3.Up * 400f )
				.WithoutTags( "player", "trigger", "worlddrop", "vehicle" )
				.Run();
			var groundZ = tr.Hit ? tr.HitPosition.z : 0f;

			var rad = MathF.PI * pitchDeg / 180f;
			const float thickness = 6f;
			var ramp = new GameObject( true, "VehicleDemoRamp" );
			ramp.SetParent( _props, false );
			ramp.WorldRotation = Rotation.FromPitch( -pitchDeg );                      // +x end up
			ramp.WorldPosition = new Vector3( x + MathF.Cos( rad ) * lengthUnits * 0.5f, 0f, groundZ + MathF.Sin( rad ) * lengthUnits * 0.5f - thickness * 0.5f );
			ramp.WorldScale = new Vector3( lengthUnits / 50f, widthUnits / 50f, thickness / 50f );   // dev box is 50^3

			var renderer = ramp.Components.Create<ModelRenderer>();
			renderer.Model = Model.Load( "models/dev/box.vmdl" );
			renderer.Tint = new Color( 0.75f, 0.28f, 0.08f );
			var collider = ramp.Components.Create<BoxCollider>();
			collider.Scale = new Vector3( 50f, 50f, 50f );
			collider.Static = true;
		}

		protected override void OnTick( float speed )
		{
			if ( _camera is null || !_camera.IsValid() )
				return;

			var vehiclePos = _vehicle.WorldPosition;
			var forward = _vehicle.WorldRotation.Forward.WithZ( 0f ).Normal;
			if ( forward.LengthSquared < 0.01f )
				forward = Vector3.Forward;

			var wantPos = vehiclePos - forward * 300f + Vector3.Up * 115f;
			var wantLook = vehiclePos + Vector3.Up * 30f;
			if ( !_camInit )
			{
				_camInit = true;
				_camPos = wantPos;
				_camLook = wantLook;
			}
			var dt = Math.Max( 0.001f, _vehicle.LastStepDelta );
			// Height follows slowly so a jump reads as the buggy rising in frame, not the camera lifting with it.
			var flat = Vector3.Lerp( _camPos, wantPos, 1f - MathF.Exp( -dt * 5f ) );
			var z = MathX.Lerp( _camPos.z, wantPos.z, 1f - MathF.Exp( -dt * 1.5f ) );
			_camPos = flat.WithZ( z );
			_camLook = Vector3.Lerp( _camLook, wantLook, 1f - MathF.Exp( -dt * 10f ) );
			_camera.WorldPosition = _camPos;
			_camera.WorldRotation = Rotation.LookAt( _camLook - _camPos, Vector3.Up );
		}

		protected override bool RunPhases( float t, float speed, out float throttle, out float steer )
		{
			throttle = 0f;
			steer = 0f;
			switch ( Phase )
			{
				case 0:   // flat out over both ramps (second landing ~ 9 s)
					throttle = 1f;
					if ( t >= 10f ) NextPhase();
					return true;
				case 1:   // sweep left
					throttle = 1f;
					steer = 1f;
					if ( t >= 2.6f ) NextPhase();
					return true;
				case 2:   // sweep right
					throttle = 1f;
					steer = -1f;
					if ( t >= 3.2f ) NextPhase();
					return true;
				case 3:   // straighten
					throttle = 1f;
					if ( t >= 1.2f ) NextPhase();
					return true;
				case 4:   // brake, then it reverses on its own once stopped
					throttle = -1f;
					if ( t >= 4.2f ) NextPhase();
					return true;
				case 5:   // let it settle
					if ( t >= 1.5f )
					{
						Log.Info( "[Vehicle demo] done." );
						return false;
					}
					return true;
			}
			return false;
		}
	}
}
