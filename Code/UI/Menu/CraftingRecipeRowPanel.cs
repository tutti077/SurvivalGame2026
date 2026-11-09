using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// One row of the crafting / upgrade list. <see cref="RowKey"/> is a recipe id on the Craft tab and
/// a <c>grid:slot</c> stack key on the workbench Upgrade tab; the section resolves it.
/// </summary>
public sealed class CraftingRecipeRowPanel : Panel
{
	public CraftingMenuSection Section { get; init; }
	public string RowKey { get; init; }

	public override bool WantsMouseInput() => false;

	public override void OnMouseWheel( Vector2 value )
	{
		Section?.ApplyRecipeListWheel( value );
	}

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		if ( e.Button != "mouseleft" || Section is null )
			return;

		Section.SelectRow( RowKey );
		e.StopPropagation();
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		base.OnMouseUp( e );
		if ( e.Button is "mouseleft" or "mouse1" or "Attack1" )
			Section?.OnMenuGlobalMouseUp();
	}
}
