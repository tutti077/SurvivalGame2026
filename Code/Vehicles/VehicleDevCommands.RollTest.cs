using System;
using Sandbox;

namespace Survival;

/// <summary>
/// <c>vehicle_rolltest</c>: rollover tuning for the local driver — from the <c>vehicle_test</c> start,
/// full throttle up to a target speed, then a held steer (full left lock by default), then hands off. Logs the lowest up.z, how
/// many times the chassis went over (up.z below −0.5) and whether it ended flipped. Dev only.
/// </summary>
public static partial class VehicleDevCommands
{
	/// <summary>Usage: <c>vehicle_rolltest [mph] [turnSeconds] [steer 0–1]</c> — the local pawn must be driving.</summary>
	[ConCmd( "vehicle_rolltest" )]
	public static void ConCmdRollTest( float mph = 60f, float turnSeconds = 2f, float steer = 1f )
	{
		var pawn = FindLocalPawn();
		var vehicle = pawn?.Components.Get<PlayerMovement>()?.SeatedVehicle;
		if ( vehicle is null || !vehicle.IsValid() )
		{
			Log.Warning( "[Vehicle] vehicle_rolltest: the local pawn must be driving (vehicle_spawn, vehicle_enter, vehicle_fuel first)." );
			return;
		}

		_test = new ScriptedRollTest( vehicle, mph / 2.23694f, turnSeconds, steer );
		Log.Info( $"[Vehicle rolltest] started: {mph:0} mph, {turnSeconds:0.0} s at {steer:0.##} steer." );
	}

	sealed class ScriptedRollTest : ScriptedTest
	{
		readonly float _targetSpeed;
		readonly float _turnSeconds;
		readonly float _steer;
		float _minUpZ = 1f;
		int _overturns;
		bool _upsideDown;
		float _turnEntrySpeed;

		public ScriptedRollTest( Vehicle vehicle, float targetMetersPerSecond, float turnSeconds, float steer ) : base( vehicle )
		{
			_steer = Math.Clamp( steer, -1f, 1f );
			_targetSpeed = Math.Clamp( targetMetersPerSecond, 1f, vehicle.MaxSpeedMetersPerSecond );
			_turnSeconds = Math.Clamp( turnSeconds, 0.2f, 10f );
		}

		protected override void OnTick( float speed )
		{
			if ( Phase < 1 )
				return;

			var upZ = _vehicle.WorldRotation.Up.z;
			_minUpZ = Math.Min( _minUpZ, upZ );
			if ( !_upsideDown && upZ < -0.5f )
			{
				_upsideDown = true;
				_overturns++;
			}
			else if ( _upsideDown && upZ > 0.5f )
				_upsideDown = false;
		}

		protected override bool RunPhases( float t, float speed, out float throttle, out float steer )
		{
			throttle = 0f;
			steer = 0f;
			switch ( Phase )
			{
				case 0:   // straight up to speed
					throttle = 1f;
					if ( speed >= _targetSpeed || t > 15f )
					{
						_turnEntrySpeed = speed;
						NextPhase();
					}
					return true;
				case 1:   // steer held (1 = full left lock), throttle held
					throttle = 1f;
					steer = _steer;
					if ( t >= _turnSeconds )
						NextPhase();
					return true;
				case 2:   // hands off, let it settle
					if ( t < 5f )
						return true;
					Log.Info( $"[Vehicle rolltest] entry {_turnEntrySpeed * 2.23694f:0} mph steer {_steer:0.##} — lowest up.z {_minUpZ:0.00}, went over {_overturns}x, ended up.z {_vehicle.WorldRotation.Up.z:0.00} flipped={_vehicle.IsFlipped} (μ {_vehicle.TyreGripCoefficient:0.00})" );
					return false;
			}

			return false;
		}
	}
}
