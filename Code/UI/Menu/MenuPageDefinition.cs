using System;

namespace Survival;

/// <summary>One switchable menu page (tab icon + layout flags).</summary>
public sealed class MenuPageDefinition
{
	public string PageId { get; }
	public string Title { get; }
	public string TabIconPath { get; }
	public MenuPanelFlags Panels { get; }

	public MenuPageDefinition( string pageId, string title, string tabIconPath, MenuPanelFlags panels )
	{
		PageId = pageId;
		Title = title;
		TabIconPath = tabIconPath;
		Panels = panels;
	}
}

[Flags]
public enum MenuPanelFlags
{
	None = 0,
	Inventory = 1,
	Crafting = 2,
}
