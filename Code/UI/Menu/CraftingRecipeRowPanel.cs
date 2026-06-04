using Sandbox.UI;

namespace Survival;

public sealed class CraftingRecipeRowPanel : Panel
{
	public CraftingMenuSection Section { get; init; }
	public string RecipeId { get; init; }

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		if ( e.Button != "mouseleft" || Section is null )
			return;

		Section.SelectRecipe( RecipeId );
	}
}
