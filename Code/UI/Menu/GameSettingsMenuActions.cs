using Sandbox;

namespace Survival;

/// <summary>Exit / navigation actions from the in-game settings menu.</summary>
public static class GameSettingsMenuActions
{
	/// <summary>Scene path to load for <see cref="QuitToMainMenu"/>.</summary>
	public const string MainMenuScenePath = GameSceneIdentity.MainMenuScenePath;

	public static void QuitToMainMenu()
	{
		if ( string.IsNullOrWhiteSpace( MainMenuScenePath ) )
			return;

		if ( !GameSceneLoader.Load( MainMenuScenePath ) )
			Log.Warning( "[GameSettings] Quit to menu failed — is scenes/mainmenu.scene present?" );
	}

	public static void QuitToDesktop()
	{
		Sandbox.Game.Close();
	}
}
