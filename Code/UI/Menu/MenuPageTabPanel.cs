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

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		if ( e.Button != "mouseleft" || MenuController is null )
			return;

		MenuController.SetActivePage( PageId );
	}
}
