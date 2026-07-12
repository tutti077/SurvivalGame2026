using Sandbox;
using Sandbox.UI;

namespace Survival;

public sealed class CraftingRecipeRowPanel : Panel
{
	public CraftingMenuSection Section { get; init; }
	public string RecipeId { get; init; }

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

		Section.SelectRecipe( RecipeId );
		e.StopPropagation();
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		base.OnMouseUp( e );
		if ( e.Button is "mouseleft" or "mouse1" or "Attack1" )
			Section?.OnMenuGlobalMouseUp();
	}
}
