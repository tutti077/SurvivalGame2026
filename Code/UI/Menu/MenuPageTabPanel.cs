using Sandbox.UI;

namespace Survival;

/// <summary>One page tab in the top menu navigator (icon box, like inventory slots).</summary>
public sealed class MenuPageTabPanel : Panel
{
	public string PageId { get; }
	public PlayerGameMenuController MenuController { get; }

	public MenuPageTabPanel( string pageId, PlayerGameMenuController menuController )
	{
		PageId = pageId;
		MenuController = menuController;
	}

	/// <summary>OS mouse is Hidden while the menu uses the soft cursor — Attack1 hit-tests select pages.</summary>
	public override bool WantsMouseInput() => false;
}
