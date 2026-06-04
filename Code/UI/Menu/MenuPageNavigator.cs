using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Top-center page tabs while any game menu page is open.</summary>
public sealed class MenuPageNavigator
{
	public const float TabSize = 56f;
	public const float TabGap = 6f;

	readonly PlayerGameMenuController _menuController;
	readonly MenuPageTabPanel[] _tabs = new MenuPageTabPanel[MenuPageRegistry.Pages.Length];

	Panel _root;

	public MenuPageNavigator( PlayerGameMenuController menuController )
	{
		_menuController = menuController;
	}

	public void Build( Panel overlay )
	{
		_root = new Panel { Parent = overlay };
		_root.Style.Set( "position", "absolute" );
		_root.Style.Set( "left", "50%" );
		_root.Style.Set( "top", "12px" );
		_root.Style.Set( "transform", "translateX(-50%)" );
		_root.Style.Set( "display", "none" );
		_root.Style.Set( "flex-direction", "row" );
		_root.Style.Set( "gap", $"{TabGap}px" );
		_root.Style.Set( "align-items", "center" );
		_root.Style.Set( "justify-content", "center" );
		_root.Style.Set( "pointer-events", "auto" );
		_root.Style.Set( "z-index", "3000" );
		_root.Style.Set( "padding", "10px 14px" );
		_root.Style.Set( "border-radius", "8px" );
		_root.Style.BackgroundColor = new Color( 0.24f, 0.26f, 0.30f, 0.92f );
		_root.Style.Set( "border-width", "1px" );
		_root.Style.Set( "border-color", "#5c6470" );

		for ( var i = 0; i < MenuPageRegistry.Pages.Length; i++ )
		{
			var page = MenuPageRegistry.Pages[i];
			var tab = new MenuPageTabPanel( page.PageId, _menuController ) { Parent = _root };
			tab.Style.Width = Length.Pixels( TabSize );
			tab.Style.Height = Length.Pixels( TabSize );
			tab.Style.Set( "flex-shrink", "0" );
			tab.Style.Set( "position", "relative" );
			tab.Style.Set( "box-sizing", "border-box" );
			tab.Style.BackgroundColor = new Color( 0.34f, 0.36f, 0.40f, 0.96f );
			tab.Style.Set( "border-width", "1px" );
			tab.Style.Set( "border-color", "#6a7280" );
			tab.Style.Set( "border-radius", "4px" );
			tab.Style.Set( "overflow", "hidden" );
			tab.Style.Set( "pointer-events", "auto" );

			var icon = new Panel { Parent = tab };
			icon.Style.Set( "position", "absolute" );
			icon.Style.Set( "left", "4px" );
			icon.Style.Set( "top", "4px" );
			icon.Style.Set( "right", "4px" );
			icon.Style.Set( "bottom", "4px" );
			icon.Style.Set( "background-size", "contain" );
			icon.Style.Set( "background-repeat", "no-repeat" );
			icon.Style.Set( "background-position", "center" );
			MenuUiTextures.ApplyBackground( icon, page.TabIconPath );

			_tabs[i] = tab;
		}

		RefreshHighlight();
	}

	public void SetMenuOpen( bool open )
	{
		if ( _root is null )
			return;

		_root.Style.Set( "display", open ? "flex" : "none" );
		if ( open )
			RefreshHighlight();
	}

	public void RefreshHighlight()
	{
		if ( _menuController is null )
			return;

		var active = _menuController.ActivePageId ?? MenuPageIds.Inventory;
		for ( var i = 0; i < _tabs.Length; i++ )
		{
			var tab = _tabs[i];
			if ( tab is null || !tab.IsValid() )
				continue;

			var selected = string.Equals( tab.PageId, active, StringComparison.OrdinalIgnoreCase );
			tab.Style.Set( "border-color", selected ? "#9ec5ff" : "#6a7280" );
			tab.Style.Set( "border-width", selected ? "2px" : "1px" );
			tab.Style.BackgroundColor = selected
				? new Color( 0.42f, 0.46f, 0.52f, 0.98f )
				: new Color( 0.34f, 0.36f, 0.40f, 0.96f );
		}
	}
}
