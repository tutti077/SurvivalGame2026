namespace Survival;

/// <summary>Replaces the active scene (menu ↔ world) without additive loading.</summary>
public static class GameSceneLoader
{
	public static bool Load( string scenePath )
	{
		if ( string.IsNullOrWhiteSpace( scenePath ) )
			return false;

		var sceneFile = ResourceLibrary.Get<SceneFile>( scenePath );
		if ( !sceneFile.IsValid() )
		{
			Log.Warning( $"[GameSceneLoader] Scene not found: {scenePath}" );
			return false;
		}

		var options = new SceneLoadOptions
		{
			IsAdditive = false,
			DeleteEverything = true,
			ShowLoadingScreen = true,
		};

		if ( !options.SetScene( sceneFile ) )
		{
			Log.Warning( $"[GameSceneLoader] Failed to set scene: {scenePath}" );
			return false;
		}

		return Sandbox.Game.ChangeScene( options );
	}
}
