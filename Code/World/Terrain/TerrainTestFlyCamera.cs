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

	protected override void OnUpdate()
	{
		if ( !Enabled || InputLocked )
			return;

		if ( Mouse.Visibility != MouseVisibility.Hidden )
			CaptureMouse();

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
		var manager = Scene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		if ( manager is null || !manager.IsValid() )
			return;

		var posMeters = TerrainWorldUnits.EngineToMeters( WorldPosition );
		if ( !manager.TrySampleGroundMeters( posMeters.x, posMeters.y, out var groundZMeters ) )
			return;

		var targetMeters = groundZMeters + HeightAboveGroundMeters + _altitudeBoostMeters;
		WorldPosition = WorldPosition.WithZ( TerrainWorldUnits.MetersToEngine( targetMeters ) );
	}
}
