using Sandbox.UI;

namespace Survival;

/// <summary>Full-screen drag ghost host; must not capture mouse or block camera look.</summary>
public sealed class InventoryDragLayerPanel : Panel
{
	public override bool WantsMouseInput() => false;
}
