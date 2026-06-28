namespace Survival;

/// <summary>Simple fly camera for terrain test scenes (WASD + mouse look).</summary>
[Title( "Terrain Test Fly Camera" )]
public sealed class TerrainTestFlyCamera : Component
{
	[Property] public float MoveSpeed { get; set; } = 400f;
	[Property] public float FastMoveMultiplier { get; set; } = 3f;
	[Property] public float LookSensitivity { get; set; } = 0.075f;
	[Property, ReadOnly] public bool InputLocked { get; set; }

	Angles _viewAngles;
	MouseVisibility _savedMouseVisibility = MouseVisibility.Auto;
	bool _mouseCaptured;

	protected override void OnEnabled()
	{
		_viewAngles = WorldRotation.Angles();
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

		var move = Vector3.Zero;
		if ( Input.Down( "Forward" ) )
			move += WorldRotation.Forward;
		if ( Input.Down( "Backward" ) )
			move -= WorldRotation.Forward;
		if ( Input.Down( "Left" ) )
			move -= WorldRotation.Right;
		if ( Input.Down( "Right" ) )
			move += WorldRotation.Right;
		if ( Input.Down( "Jump" ) )
			move += Vector3.Up;
		if ( Input.Down( "Duck" ) )
			move -= Vector3.Up;

		if ( move.LengthSquared < 1e-8f )
			return;

		var speed = MoveSpeed * (Input.Down( "Run" ) ? FastMoveMultiplier : 1f);
		WorldPosition += move.Normal * speed * Time.Delta;
	}
}
