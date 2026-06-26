namespace Survival;

/// <summary>Ensures a full-screen load overlay exists on the active camera.</summary>
[Title( "Terrain World Load Screen Host" )]
public sealed class TerrainWorldLoadScreenHost : Component
{
	TerrainWorldLoadScreen _screen;

	protected override void OnStart()
	{
		EnsureScreen();
	}

	public void Show( string title, string status, float progress01 )
	{
		if ( !EnsureScreen() )
			return;

		_screen.SetDisplay( true, title, status, progress01 );
	}

	public void Hide()
	{
		if ( _screen is null || !_screen.IsValid() )
			return;

		_screen.SetDisplay( false, "", "", 0f );
	}

	bool EnsureScreen()
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

		_screen = Components.Get<TerrainWorldLoadScreen>();
		if ( _screen is null || !_screen.IsValid() )
			_screen = Components.Create<TerrainWorldLoadScreen>();

		return _screen is not null && _screen.IsValid();
	}
}
