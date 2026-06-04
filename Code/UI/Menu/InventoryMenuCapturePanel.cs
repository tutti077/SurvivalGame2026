using Sandbox.UI;

namespace Survival;

/// <summary>Catches left-mouse release over the menu (including gaps between slots).</summary>
public sealed class InventoryMenuCapturePanel : Panel
{
	public PlayerInventoryInteraction Interaction { get; set; }

	protected override void OnMouseUp( MousePanelEvent e )
	{
		base.OnMouseUp( e );
		if ( e.Button == "mouseleft" )
			Interaction?.OnGlobalMouseUp();
	}
}
