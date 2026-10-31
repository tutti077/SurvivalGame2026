namespace Survival;

/// <summary>
/// Altimeter for the terrain fly camera (Mark 2026-10-25: "tells me the current elevation"): top-left of the screen,
/// the camera's elevation above sea level and its height above the ground under it (over water: the water depth), in
/// metres. Sits on the same
/// object as the <see cref="CameraComponent"/> it draws on; the ground is sampled a few times a second, not per frame.
/// </summary>
[Title( "Terrain Fly Camera Altimeter" )]
public sealed class TerrainFlyCameraAltimeter : Component
{
	[Property, Title( "Ground Samples Per Second" ), Range( 1f, 30f ), Step( 1f ), Description( "How often the height above ground is re-read (the terrain sample is the generator's, not a trace)." )]
	public float GroundSamplesPerSecond { get; set; } = 8f;

	CameraComponent _camera;
	TerrainWorldManager _manager;
	float _groundMeters = float.NaN;
	TimeSince _sinceGround;

	protected override void OnStart()
	{
		_camera = Components.Get<CameraComponent>( FindMode.EverythingInSelf );
	}

	protected override void OnPreRender()
	{
		if ( _camera is null || !_camera.IsValid() || !_camera.Enabled )
			return;

		var posMeters = TerrainWorldUnits.EngineToMeters( WorldPosition );
		if ( _manager is null || !_manager.IsValid() )
			_manager = Scene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		var settings = _manager?.LoadSettings;

		if ( settings is not null && _sinceGround >= 1f / Math.Max( 1f, GroundSamplesPerSecond ) )
		{
			_sinceGround = 0f;
			_groundMeters = TerrainHeightQuery.TrySampleGroundMeters( settings, posMeters.x, posMeters.y, out var ground ) ? ground : float.NaN;
		}

		var seaLevel = settings?.SeaLevelMeters ?? 0f;
		var text = $"Elevation {posMeters.z - seaLevel:0.0} m";
		// Over a lake or the sea the ground under the camera is the bed: say how deep the water is instead
		if ( float.IsFinite( _groundMeters ) )
			text += _groundMeters < seaLevel
				? $"\nWater depth {seaLevel - _groundMeters:0.0} m"
				: $"\nAbove ground {posMeters.z - _groundMeters:0.0} m";

		var hud = _camera.Overlay;
		var rect = new Rect( 16f, 16f, 220f, float.IsFinite( _groundMeters ) ? 52f : 30f );
		hud.DrawRect( rect, new Color( 0.05f, 0.06f, 0.08f, 0.7f ), new Vector4( 6f ), default, default );
		hud.DrawText( text, 16f, Color.White, rect.Shrink( 10f, 6f ), TextFlag.LeftCenter );
	}
}
