namespace Survival;

/// <summary>Scene path constants + helpers so menu and world scenes stay isolated.</summary>
public static class GameSceneIdentity
{
	public const string MainMenuScenePath = "scenes/mainmenu.scene";
	public const string GameScenePath = "scenes/terrainTest.scene";

	public static bool IsMainMenu( Scene scene )
		=> TryGetSceneTitle( scene, out var title )
			&& title.Equals( "mainmenu", StringComparison.OrdinalIgnoreCase );

	public static bool IsGameWorld( Scene scene )
		=> TryGetSceneTitle( scene, out var title )
			&& title.Equals( "terrainTest", StringComparison.OrdinalIgnoreCase );

	static bool TryGetSceneTitle( Scene scene, out string title )
	{
		title = null;
		if ( !scene.IsValid() )
			return false;

		foreach ( var info in scene.GetAllComponents<SceneInformation>() )
		{
			if ( string.IsNullOrWhiteSpace( info.Title ) )
				continue;

			title = info.Title.Trim();
			return true;
		}

		return false;
	}
}
