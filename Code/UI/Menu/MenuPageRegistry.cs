using System;

namespace Survival;

/// <summary>Registered menu pages shown in the top tab bar.</summary>
public static class MenuPageRegistry
{
	public static readonly MenuPageDefinition[] Pages =
	{
		new( MenuPageIds.Inventory, "Inventory", "ui/menu/InventoryTab.png", MenuPanelFlags.Inventory ),
		new( MenuPageIds.Crafting, "Crafting", "ui/menu/CraftingTab.png", MenuPanelFlags.Inventory | MenuPanelFlags.Crafting ),
		new( MenuPageIds.Skills, "Skills", "ui/menu/SkillsTab.png", MenuPanelFlags.Skills, allowsHotkey: false ),
		new( MenuPageIds.Quests, "Quests", "ui/menu/QuestsTab.png", MenuPanelFlags.Inventory | MenuPanelFlags.Quests, allowsHotkey: false ),
		new( MenuPageIds.Map, "Map", "ui/menu/MapTab.png", MenuPanelFlags.Map ),
		new( MenuPageIds.Settings, "Settings", "ui/menu/tab_blank.png", MenuPanelFlags.Settings ),
		// Station-only page — not shown in the top tab bar (filtered in MenuPageNavigator).
		new( MenuPageIds.AugmentStation, "Augments", "ui/menu/CraftingTab.png",
			MenuPanelFlags.AugmentStation | MenuPanelFlags.Inventory, allowsHotkey: false ),
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
