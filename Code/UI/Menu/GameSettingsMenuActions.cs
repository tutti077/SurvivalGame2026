using Sandbox;

namespace Survival;

/// <summary>Exit / navigation actions from the in-game settings menu.</summary>
public static class GameSettingsMenuActions
{
	/// <summary>Scene path to load for <see cref="QuitToMainMenu"/> (e.g. <c>scenes/mainmenu.scene</c>). Empty = log only.</summary>
	public const string MainMenuScenePath = "";

	public static void QuitToMainMenu()
	{
		if ( !string.IsNullOrWhiteSpace( MainMenuScenePath ) && Sandbox.Game.ActiveScene.IsValid() )
		{
			Sandbox.Game.ActiveScene.LoadFromFile( MainMenuScenePath );
			return;
		}

		Log.Warning( "[GameSettings] Quit to menu: set GameSettingsMenuActions.MainMenuScenePath to your main-menu scene." );
	}

	public static void QuitToDesktop()
	{
		Sandbox.Game.Close();
	}
}
