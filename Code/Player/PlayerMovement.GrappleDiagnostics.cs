using System;
using System.Globalization;
using System.Text;
using Game;
using Sandbox;

namespace Survival;

/// <summary>
/// Swing telemetry for tuning the grapple pump. One CSV row per physics step while attached, written
/// to <c>FileSystem.Data</c> as <c>grapple_swing_log.csv</c> — on Windows that is
/// <c>sbox/data/local/survivalgamebasics#local/</c>.
/// <para>
/// Exists because swing feel questions ("why does it take 30 s to clear the anchor", "where is the
/// energy going") are answered by amplitude per half swing and by what the pump did each step, and
/// neither is visible from inside the game. Off by default and every hook is behind the toggle.
/// </para>
/// </summary>
public sealed partial class PlayerMovement
{
	const string GrappleSwingLogFileName = "grapple_swing_log.csv";

	/// <summary>Rows buffered before a flush. One physics step each, so ~50/s.</summary>
	const int GrappleSwingLogFlushRows = 250;

	/// <summary>Write a CSV row per physics step while the rope is attached. Debug only.</summary>
	[Property, Group( "Grapple Swing" ), Title( "Log Swing To File" )]
	public bool GrappleSwingLogEnabled { get; set; } = true;

	StringBuilder _grappleLogBuffer;
	int _grappleLogRows;
	float _grappleLogStartTime;
	bool _grappleLogHeaderWritten;

	// Last values from ApplyPendulumSwingPush, read by the row writer at the end of the step.
	string _grappleLogPhase = "none";
	float _grappleLogAlong;
	float _grappleLogCharge;
	float _grappleLogPumpAccel;
	float _grappleLogSpeedBefore;
	float _grappleLogSpeedAfter;
	float _grappleLogSwingSpeed;

	// Set by the rope constraint when a genuine slack -> taut catch happens.
	bool _grappleLogCatch;
	float _grappleLogCatchBefore;
	float _grappleLogCatchAfter;

	/// <summary>New attach = new run. Re-emits the header so each run records its own tuning values.</summary>
	void ResetGrappleSwingLog()
	{
		if ( !GrappleSwingLogEnabled )
			return;

		FlushGrappleSwingLog();
		_grappleLogStartTime = Time.Now;
		_grappleLogPhase = "none";
		_grappleLogHeaderWritten = false;
		(_grappleLogBuffer ??= new StringBuilder()).AppendLine( "# --- attach ---" );
	}

	void RecordGrappleSwingPush( string phase, float along, float charge, float pumpAccel,
		float speedBefore, float speedAfter, float swingSpeed )
	{
		if ( !GrappleSwingLogEnabled )
			return;

		_grappleLogPhase = phase;
		_grappleLogAlong = along;
		_grappleLogCharge = charge;
		_grappleLogPumpAccel = pumpAccel;
		_grappleLogSpeedBefore = speedBefore;
		_grappleLogSpeedAfter = speedAfter;
		_grappleLogSwingSpeed = swingSpeed;
	}

	void RecordGrappleRopeCatch( float speedBefore, float speedAfter )
	{
		if ( !GrappleSwingLogEnabled )
			return;

		_grappleLogCatch = true;
		_grappleLogCatchBefore = speedBefore;
		_grappleLogCatchAfter = speedAfter;
	}

	/// <summary>One row per physics step, written after the constraint and pump have both run.</summary>
	void WriteGrappleSwingLogRow( float dist, float maxLen, Vector3 radial, Vector3 velocity )
	{
		if ( !GrappleSwingLogEnabled )
			return;

		_grappleLogBuffer ??= new StringBuilder();

		if ( !_grappleLogHeaderWritten )
		{
			_grappleLogBuffer.AppendLine(
				"# build=" + GameBuildLabel.Display
				+ " pump=" + F( SwingPumpGravityFraction )
				+ " maxSpeed=" + F( SwingMaxSpeed )
				+ " coastDamp=" + F( SwingCoastDamping ) );
			_grappleLogBuffer.AppendLine(
				"t,ropeLen,dist,angleDeg,taut,phase,along,charge,pumpAccel,speedBefore,speedAfter,swingSpeed,steerX,steerY,catch,catchBefore,catchAfter" );
			_grappleLogHeaderWritten = true;
		}

		// 0 deg = hanging straight down, 90 = level with the anchor, >90 = above it.
		var angle = MathF.Acos( Math.Clamp( -radial.z, -1f, 1f ) ) * (180f / MathF.PI );

		_grappleLogBuffer
			.Append( F( Time.Now - _grappleLogStartTime ) ).Append( ',' )
			.Append( F( maxLen ) ).Append( ',' )
			.Append( F( dist ) ).Append( ',' )
			.Append( F( angle ) ).Append( ',' )
			.Append( _grappleRopeTaut ? '1' : '0' ).Append( ',' )
			.Append( _grappleLogPhase ).Append( ',' )
			.Append( F( _grappleLogAlong ) ).Append( ',' )
			.Append( F( _grappleLogCharge ) ).Append( ',' )
			.Append( F( _grappleLogPumpAccel ) ).Append( ',' )
			.Append( F( _grappleLogSpeedBefore ) ).Append( ',' )
			.Append( F( _grappleLogSpeedAfter ) ).Append( ',' )
			.Append( F( _grappleLogSwingSpeed ) ).Append( ',' )
			.Append( F( _grappleSteerX ) ).Append( ',' )
			.Append( F( _grappleSteerY ) ).Append( ',' )
			.Append( _grappleLogCatch ? '1' : '0' ).Append( ',' )
			.Append( F( _grappleLogCatchBefore ) ).Append( ',' )
			.Append( F( _grappleLogCatchAfter ) )
			.AppendLine();

		_grappleLogCatch = false;
		_grappleLogPhase = "slack";
		_grappleLogRows++;

		// Velocity is passed so a future column can be added without rethreading the call site.
		_ = velocity;

		if ( _grappleLogRows >= GrappleSwingLogFlushRows )
			FlushGrappleSwingLog();
	}

	void FlushGrappleSwingLog()
	{
		if ( _grappleLogBuffer is null || _grappleLogBuffer.Length == 0 )
			return;

		try
		{
			var existing = FileSystem.Data.FileExists( GrappleSwingLogFileName )
				? FileSystem.Data.ReadAllText( GrappleSwingLogFileName )
				: string.Empty;

			FileSystem.Data.WriteAllText( GrappleSwingLogFileName, existing + _grappleLogBuffer );
		}
		catch ( Exception e )
		{
			Log.Warning( $"Grapple swing log write failed: {e.Message}" );
		}

		_grappleLogBuffer.Clear();
		_grappleLogRows = 0;
	}

	static string F( float v ) => v.ToString( "0.###", CultureInfo.InvariantCulture );
}
