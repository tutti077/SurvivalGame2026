namespace Survival;

/// <summary>Simple fly camera for terrain test scenes (WASD + mouse look, Jump/Duck = up/down).</summary>
[Title( "Terrain Test Fly Camera" )]
public sealed class TerrainTestFlyCamera : Component
{
	/// <summary>Engine units/sec (see <see cref="TerrainWorldUnits.UnitsPerMeter"/>). Default ≈ 55 m/s; shift ≈ 165 m/s (~10 km/min).</summary>
	[Property] public float MoveSpeed { get; set; } = 2220f;
	[Property] public float FastMoveMultiplier { get; set; } = 3f;
	[Property] public float LookSensitivity { get; set; } = 0.075f;
	[Property, Title( "Follow terrain height" ), Description( "When on: WASD stays horizontal; Jump/Duck adjust height above sampled ground. When off: free 3D flight." )]
	public bool FollowTerrainHeight { get; set; }
	[Property, Title( "Height above ground (m)" ), Range( 16f, 2000f ), Step( 8f ), Description( "Baseline clearance when Follow terrain height is on." )]
	public float HeightAboveGroundMeters { get; set; } = 96f;
	[Property, ReadOnly] public bool InputLocked { get; set; }

	Angles _viewAngles;
	MouseVisibility _savedMouseVisibility = MouseVisibility.Auto;
	bool _mouseCaptured;
	float _altitudeBoostMeters;
	TerrainWorldManager _manager;

	protected override void OnEnabled()
	{
		_viewAngles = WorldRotation.Angles();
		_altitudeBoostMeters = 0f;
		if ( !InputLocked )
			CaptureMouse();
	}

	protected override void OnDisabled()
	{
		ReleaseMouse();
	}

	public void SetInputLocked( bool locked )
	{
		InputLocked = locked;
		if ( !IsValid || !Enabled )
			return;

		if ( locked )
			ReleaseMouse();
		else
		{
			_viewAngles = WorldRotation.Angles();
			CaptureMouse();
		}
	}

	void CaptureMouse()
	{
		if ( _mouseCaptured )
			return;

		_savedMouseVisibility = Mouse.Visibility;
		Mouse.Visibility = MouseVisibility.Hidden;
		_mouseCaptured = true;
	}

	void ReleaseMouse()
	{
		if ( !_mouseCaptured )
			return;

		Mouse.Visibility = _savedMouseVisibility;
		_mouseCaptured = false;
	}

	/// <summary>Orients the camera toward a world point and syncs internal look angles.</summary>
	public void SetViewLookAt( Vector3 worldTarget )
	{
		var toTarget = worldTarget - WorldPosition;
		if ( toTarget.LengthSquared < 1e-8f )
			return;

		WorldRotation = Rotation.LookAt( toTarget.Normal, Vector3.Up );
		_viewAngles = WorldRotation.Angles();
	}

	/// <summary>Dev teleport (console <c>terrain_tp_biome</c>): move to an engine-unit position, look at a point, reset altitude boost.</summary>
	public void TeleportTo( Vector3 worldPositionEngine, Vector3 lookAtEngine )
	{
		_altitudeBoostMeters = 0f;
		WorldPosition = worldPositionEngine;
		SetViewLookAt( lookAtEngine );
	}

	/// <summary>Places the camera above world spawn and looks toward terrain ahead (+Y). Distances are meters.</summary>
	public void SnapToTerrainView( float groundZMeters, float heightAboveGroundMeters, float lookAheadMeters )
	{
		heightAboveGroundMeters = Math.Max( 64f, heightAboveGroundMeters );
		lookAheadMeters = Math.Max( 32f, lookAheadMeters );
		_altitudeBoostMeters = 0f;

		var groundEngine = TerrainWorldUnits.MetersToEngine( groundZMeters );
		var heightEngine = TerrainWorldUnits.MetersToEngine( heightAboveGroundMeters );
		var lookAheadEngine = TerrainWorldUnits.MetersToEngine( lookAheadMeters );

		WorldPosition = new Vector3( 0f, -lookAheadEngine * 0.35f, groundEngine + heightEngine );
		SetViewLookAt( new Vector3( 0f, lookAheadEngine, groundEngine ) );
	}

	// terrain_fly: level flight along the current heading at a fixed speed, for perf runs (the wingsuit proxy).
	static float _autoFlyMetersPerSecond;
	static float _autoFlyUntil = -1f;

	// perf_suite: scripted flight — speed along the heading, the heading turning at a fixed rate, the height held at
	// so many metres above the terrain (terrain-following) or at an absolute metre, the pitch fixed. Hands off.
	static bool _scripted;
	static float _scriptSpeed, _scriptYawRate, _scriptAgl, _scriptAbsZ, _scriptPitch;

	/// <summary>Start a scripted flight: <paramref name="aglMeters"/> ≥ 0 follows the terrain at that height, else <paramref name="absZMeters"/> is held.</summary>
	public static void BeginScript( float metersPerSecond, float yawDegreesPerSecond, float aglMeters, float absZMeters, float pitchDegrees )
	{
		_scripted = true;
		_scriptSpeed = metersPerSecond;
		_scriptYawRate = yawDegreesPerSecond;
		_scriptAgl = aglMeters;
		_scriptAbsZ = absZMeters;
		_scriptPitch = pitchDegrees;
	}

	public static void EndScript() => _scripted = false;

	/// <summary>Park the camera at world metres with a pitch (+ = down) and yaw, syncing the look angles.</summary>
	public void SetPoseMeters( Vector3 positionMeters, float pitchDegrees, float yawDegrees )
	{
		_altitudeBoostMeters = 0f;
		WorldPosition = TerrainWorldUnits.MetersToEngine( positionMeters );
		_viewAngles = new Angles( pitchDegrees, yawDegrees, 0f );
		WorldRotation = _viewAngles.ToRotation();
	}

	void TickScript()
	{
		_viewAngles.yaw += _scriptYawRate * Time.Delta;
		_viewAngles.pitch = _scriptPitch;
		WorldRotation = _viewAngles.ToRotation();
		var heading = WorldRotation.Forward.WithZ( 0f );
		if ( _scriptSpeed > 0f && heading.LengthSquared > 1e-6f )
			WorldPosition += heading.Normal * TerrainWorldUnits.MetersToEngine( _scriptSpeed ) * Time.Delta;

		if ( _scriptAgl >= 0f )
		{
			if ( _manager is null || !_manager.IsValid() )
				_manager = Scene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
			var posMeters = TerrainWorldUnits.EngineToMeters( WorldPosition );
			if ( _manager is not null && _manager.IsValid() && _manager.TrySampleGroundMeters( posMeters.x, posMeters.y, out var groundZ ) )
				WorldPosition = WorldPosition.WithZ( TerrainWorldUnits.MetersToEngine( groundZ + _scriptAgl ) );
		}
		else
			WorldPosition = WorldPosition.WithZ( TerrainWorldUnits.MetersToEngine( _scriptAbsZ ) );
	}

	/// <summary>Usage: <c>terrain_fly [metresPerSecond] [seconds]</c> — fly level along the camera heading (default 55 m/s ≈ 120 mph for 20 s); <c>terrain_fly 0</c> stops.</summary>
	[ConCmd( "terrain_fly" )]
	public static void ConCmdFly( float metersPerSecond = 55f, float seconds = 20f )
	{
		_autoFlyMetersPerSecond = Math.Max( 0f, metersPerSecond );
		_autoFlyUntil = metersPerSecond > 0f ? Time.Now + Math.Max( 0.1f, seconds ) : -1f;
		Log.Info( metersPerSecond > 0f
			? $"[TerrainDev] auto-fly {metersPerSecond:0} m/s ({metersPerSecond * 2.237f:0} mph) for {seconds:0} s along the camera heading."
			: "[TerrainDev] auto-fly stopped." );
	}

	protected override void OnUpdate()
	{
		if ( !Enabled || InputLocked )
			return;

		if ( _scripted )
		{
			TickScript();
			return;
		}

		if ( _autoFlyUntil > 0f )
		{
			if ( Time.Now >= _autoFlyUntil )
			{
				_autoFlyUntil = -1f;
				Log.Info( "[TerrainDev] auto-fly finished." );
			}
			else
			{
				var heading = WorldRotation.Forward.WithZ( 0f );
				if ( heading.LengthSquared > 1e-6f )
					WorldPosition += heading.Normal * TerrainWorldUnits.MetersToEngine( _autoFlyMetersPerSecond ) * Time.Delta;
				return;   // no mouse / keys while auto-flying
			}
		}

		// Something else (a destroyed pawn's controller, a menu) handed the mouse back: take it again, even if we
		// think we already hold it — the flag only tracks OUR last call.
		if ( Mouse.Visibility != MouseVisibility.Hidden )
		{
			_mouseCaptured = false;
			CaptureMouse();
		}

		_viewAngles.pitch += Input.MouseDelta.y * LookSensitivity;
		_viewAngles.yaw -= Input.MouseDelta.x * LookSensitivity;
		_viewAngles.pitch = Math.Clamp( _viewAngles.pitch, -89f, 89f );
		WorldRotation = _viewAngles.ToRotation();

		var speed = MoveSpeed * (Input.Down( "Run" ) ? FastMoveMultiplier : 1f);
		var verticalInput = (Input.Down( "Jump" ) ? 1f : 0f) - (Input.Down( "Duck" ) ? 1f : 0f);

		if ( FollowTerrainHeight )
		{
			var move = Vector3.Zero;
			if ( Input.Down( "Forward" ) )
				move += WorldRotation.Forward;
			if ( Input.Down( "Backward" ) )
				move -= WorldRotation.Forward;
			if ( Input.Down( "Left" ) )
				move -= WorldRotation.Right;
			if ( Input.Down( "Right" ) )
				move += WorldRotation.Right;

			move.z = 0f;
			if ( move.LengthSquared > 1e-8f )
				WorldPosition += move.Normal * speed * Time.Delta;

			if ( Math.Abs( verticalInput ) > 0.001f )
			{
				var verticalSpeedMeters = TerrainWorldUnits.EngineToMeters( speed );
				_altitudeBoostMeters += verticalInput * verticalSpeedMeters * Time.Delta;
			}

			ApplyTerrainHeightFollow();
			return;
		}

		_altitudeBoostMeters = 0f;
		var freeMove = Vector3.Zero;
		if ( Input.Down( "Forward" ) )
			freeMove += WorldRotation.Forward;
		if ( Input.Down( "Backward" ) )
			freeMove -= WorldRotation.Forward;
		if ( Input.Down( "Left" ) )
			freeMove -= WorldRotation.Right;
		if ( Input.Down( "Right" ) )
			freeMove += WorldRotation.Right;
		if ( verticalInput > 0f )
			freeMove += Vector3.Up;
		else if ( verticalInput < 0f )
			freeMove -= Vector3.Up;

		if ( freeMove.LengthSquared < 1e-8f )
			return;

		WorldPosition += freeMove.Normal * speed * Time.Delta;
	}

	void ApplyTerrainHeightFollow()
	{
		// Resolved once; a scene scan per frame is not worth a terrain-follow height.
		if ( _manager is null || !_manager.IsValid() )
			_manager = Scene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		var manager = _manager;
		if ( manager is null || !manager.IsValid() )
			return;

		var posMeters = TerrainWorldUnits.EngineToMeters( WorldPosition );
		if ( !manager.TrySampleGroundMeters( posMeters.x, posMeters.y, out var groundZMeters ) )
			return;

		var targetMeters = groundZMeters + HeightAboveGroundMeters + _altitudeBoostMeters;
		WorldPosition = WorldPosition.WithZ( TerrainWorldUnits.MetersToEngine( targetMeters ) );
	}
}
