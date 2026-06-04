using System;

namespace Survival;

/// <summary>Registered menu pages shown in the top tab bar.</summary>
public static class MenuPageRegistry
{
	public static readonly MenuPageDefinition[] Pages =
	{
		new( MenuPageIds.Inventory, "Inventory", "ui/menu/InventoryTab.png", MenuPanelFlags.Inventory ),
		new( MenuPageIds.Crafting, "Crafting", "ui/menu/tab_blank.png", MenuPanelFlags.Inventory | MenuPanelFlags.Crafting ),
	};

	public static MenuPageDefinition Get( string pageId )
	{
		if ( string.IsNullOrWhiteSpace( pageId ) )
			return Pages[0];

		for ( var i = 0; i < Pages.Length; i++ )
		{
			if ( string.Equals( Pages[i].PageId, pageId, StringComparison.OrdinalIgnoreCase ) )
				return Pages[i];
		}

		return Pages[0];
	}
}
