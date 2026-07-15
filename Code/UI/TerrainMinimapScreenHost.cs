using Sandbox;

namespace Survival;

/// <summary>Ensures a top-of-screen biome minimap exists on the active camera.</summary>
[Title( "Terrain Minimap Screen Host" )]
public sealed class TerrainMinimapScreenHost : Component
{
	TerrainMinimapScreen _screen;

	protected override void OnStart()
	{
		EnsureScreen();
	}

	public bool EnsureScreen()
	{
		if ( _screen is not null && _screen.IsValid() )
			return true;

		var screenPanel = Components.Get<ScreenPanel>();
		if ( screenPanel is null || !screenPanel.IsValid() )
		{
			screenPanel = Components.Create<ScreenPanel>();
			var camera = Components.Get<CameraComponent>();
			if ( camera.IsValid() )
				screenPanel.TargetCamera = camera;
		}

		_screen = Components.Get<TerrainMinimapScreen>();
		if ( _screen is null || !_screen.IsValid() )
			_screen = Components.Create<TerrainMinimapScreen>();

		return _screen is not null && _screen.IsValid();
	}
}
