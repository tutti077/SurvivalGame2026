namespace Survival;

/// <summary>Main menu overlay on the menu scene camera.</summary>
[Title( "Game Main Menu Host" )]
public sealed class GameMainMenuHost : Component
{
	GameMainMenu _menu;

	protected override void OnStart()
	{
		EnsureMenu();
	}

	void EnsureMenu()
	{
		if ( _menu is not null && _menu.IsValid() )
			return;

		var screenPanel = Components.Get<ScreenPanel>();
		if ( screenPanel is null || !screenPanel.IsValid() )
		{
			screenPanel = Components.Create<ScreenPanel>();
			var camera = Components.Get<CameraComponent>();
			if ( camera.IsValid() )
				screenPanel.TargetCamera = camera;
		}

		_menu = Components.Get<GameMainMenu>();
		if ( _menu is null || !_menu.IsValid() )
			_menu = Components.Create<GameMainMenu>();

		_menu.OnRefreshSaves = RefreshSaveList;
		_menu.OnLoadWorld = LoadWorld;
		_menu.OnDeleteWorld = DeleteWorld;
		_menu.OnCopyWorld = CopyWorld;
		_menu.OnRenameWorld = RenameWorld;
		_menu.OnCreateWorld = CreateWorld;
		RefreshSaveList();
	}

	void RefreshSaveList()
	{
		if ( _menu is null || !_menu.IsValid() )
			return;

		_menu.SaveEntries = WorldSaveIO.ListWorldSaves();
		if ( !string.IsNullOrWhiteSpace( _menu.SelectedWorldName )
			&& !_menu.SaveEntries.Any( s => string.Equals( s.WorldName, _menu.SelectedWorldName, StringComparison.OrdinalIgnoreCase ) ) )
		{
			_menu.SelectedWorldName = _menu.SaveEntries.Count > 0 ? _menu.SaveEntries[0].WorldName : "";
		}

		_menu.StateHasChanged();
	}

	void LoadWorld( string worldName )
	{
		WorldSessionState.BeginLoadWorld( worldName );
		LoadGameScene();
	}

	void DeleteWorld( string worldName )
	{
		if ( !WorldSaveIO.TryDeleteWorld( worldName ) )
			return;

		RefreshSaveList();
	}

	void CopyWorld( string sourceName, string destName )
	{
		if ( !WorldSaveIO.TryCopyWorld( sourceName, destName, out var sanitizedDest ) )
			return;

		_menu.SelectedWorldName = sanitizedDest;
		RefreshSaveList();
	}

	void RenameWorld( string sourceName, string destName )
	{
		if ( !WorldSaveIO.TryRenameWorld( sourceName, destName, out var sanitizedDest ) )
			return;

		_menu.SelectedWorldName = sanitizedDest;
		RefreshSaveList();
	}

	void CreateWorld( string worldName, int worldSeed )
	{
		WorldSessionState.BeginNewWorld( worldName, worldSeed );
		LoadGameScene();
	}

	static void LoadGameScene()
		=> GameSceneLoader.Load( WorldSessionState.GameScenePath );
}
