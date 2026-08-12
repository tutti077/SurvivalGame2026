using System;

namespace Survival;

/// <summary>One switchable menu page (tab icon + layout flags).</summary>
public sealed class MenuPageDefinition
{
	public string PageId { get; }
	public string Title { get; }
	public string TabIconPath { get; }
	public MenuPanelFlags Panels { get; }

	/// <summary>When false, the page can only be opened from the top tab bar (e.g. skills).</summary>
	public bool AllowsHotkey { get; }

	public MenuPageDefinition( string pageId, string title, string tabIconPath, MenuPanelFlags panels, bool allowsHotkey = true )
	{
		PageId = pageId;
		Title = title;
		TabIconPath = tabIconPath;
		Panels = panels;
		AllowsHotkey = allowsHotkey;
	}
}

[Flags]
public enum MenuPanelFlags
{
	None = 0,
	Inventory = 1,
	Crafting = 2,
	Skills = 4,
	Map = 8,
	Quests = 16,
	Settings = 32,
	AugmentStation = 64,
}
