using System;
using System.Collections.Generic;
using Game;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Fullscreen settings: root menu + in-place sub-pages (back ascends to root).</summary>
public sealed class GameSettingsMenuSection : IPlayerMenuSection
{
	public const float ButtonWidth = 360f;
	public const float ButtonGap = 10f;

	public string SectionId => "settings";

	static readonly (string Id, string Title)[] SubPages =
	{
		( "game_settings", "Game settings" ),
		( "controls", "Controls" ),
		( "audio", "Audio" ),
		( "video", "Video" ),
		( "player_stats", "Player stats" ),
	};

	Panel _sectionRoot;
	Panel _rootHeader;
	Panel _subHeader;
	Label _subHeaderTitle;
	Panel _contentHost;
	Panel _rootPage;
	Panel _subPageHost;
	Label _buildLabel;

	readonly Dictionary<string, Panel> _subPageById = new();
	readonly List<SettingsMenuButtonPanel> _rootButtons = new();
	SettingsMenuBackButtonPanel _backButton;
	string _activeSubPageId;

	bool _menuOpen;
	bool _panelVisible;

	public void Build( Panel menuColumn )
	{
		_sectionRoot = new Panel { Parent = menuColumn };
		_sectionRoot.Style.Set( "position", "relative" );
		_sectionRoot.Style.Width = Length.Percent( 100 );
		_sectionRoot.Style.Height = Length.Percent( 100 );
		_sectionRoot.Style.Set( "pointer-events", "auto" );
		_sectionRoot.Style.Set( "flex-direction", "column" );
		_sectionRoot.Style.Set( "align-items", "stretch" );
		_sectionRoot.Style.PaddingTop = Length.Pixels( 10f );
		_sectionRoot.Style.PaddingBottom = Length.Pixels( 10f );
		_sectionRoot.Style.PaddingLeft = Length.Pixels( 12f );
		_sectionRoot.Style.PaddingRight = Length.Pixels( 12f );
		_sectionRoot.Style.BackgroundColor = new Color( 0.03f, 0.04f, 0.06f, 0.92f );
		_sectionRoot.Style.Set( "border-radius", "10px" );
		_sectionRoot.Style.Set( "border-width", "1px" );
		_sectionRoot.Style.Set( "border-color", "#3a4250" );

		_rootHeader = new Panel { Parent = _sectionRoot };
		_rootHeader.Style.Set( "flex-direction", "row" );
		_rootHeader.Style.Set( "align-items", "center" );
		_rootHeader.Style.Set( "justify-content", "center" );
		_rootHeader.Style.Set( "width", "100%" );
		_rootHeader.Style.Set( "margin-bottom", "8px" );
		_rootHeader.Style.Set( "flex-shrink", "0" );

		var rootTitle = new Label { Parent = _rootHeader, Text = "Settings" };
		rootTitle.Style.FontColor = Color.White;
		rootTitle.Style.FontSize = Length.Pixels( CraftingMenuSection.CraftingTitleFontSize );

		_subHeader = new Panel { Parent = _sectionRoot };
		_subHeader.Style.Set( "position", "relative" );
		_subHeader.Style.Set( "width", "100%" );
		_subHeader.Style.Set( "min-height", "44px" );
		_subHeader.Style.Set( "margin-bottom", "8px" );
		_subHeader.Style.Set( "flex-shrink", "0" );
		_subHeader.Style.Set( "display", "none" );

		var subTitleWrap = new Panel { Parent = _subHeader };
		subTitleWrap.Style.Set( "position", "absolute" );
		subTitleWrap.Style.Set( "left", "0" );
		subTitleWrap.Style.Set( "right", "0" );
		subTitleWrap.Style.Set( "top", "0" );
		subTitleWrap.Style.Set( "bottom", "0" );
		subTitleWrap.Style.Set( "flex-direction", "row" );
		subTitleWrap.Style.Set( "align-items", "center" );
		subTitleWrap.Style.Set( "justify-content", "center" );
		subTitleWrap.Style.Set( "pointer-events", "none" );

		_subHeaderTitle = new Label { Parent = subTitleWrap, Text = "" };
		_subHeaderTitle.Style.FontColor = Color.White;
		_subHeaderTitle.Style.FontSize = Length.Pixels( CraftingMenuSection.CraftingTitleFontSize );
		_subHeaderTitle.Style.Set( "text-align", "center" );

		var subBack = new SettingsMenuBackButtonPanel { Parent = _subHeader, Section = this };
		_backButton = subBack;
		subBack.Style.Set( "position", "absolute" );
		subBack.Style.Set( "left", "0" );
		subBack.Style.Set( "top", "0" );
		subBack.Style.Set( "z-index", "2" );
		subBack.Style.Set( "flex-direction", "row" );
		subBack.Style.Set( "align-items", "center" );
		subBack.Style.Set( "pointer-events", "none" );
		subBack.Style.PaddingTop = Length.Pixels( 6f );
		subBack.Style.PaddingBottom = Length.Pixels( 6f );
		subBack.Style.PaddingLeft = Length.Pixels( 12f );
		subBack.Style.PaddingRight = Length.Pixels( 12f );
		subBack.Style.BackgroundColor = new Color( 0.12f, 0.14f, 0.17f, 0.95f );
		subBack.Style.Set( "border-width", "1px" );
		subBack.Style.Set( "border-color", "#454c58" );
		subBack.Style.Set( "border-radius", "6px" );

		var subBackLabel = new Label { Parent = subBack, Text = "Back" };
		subBackLabel.Style.FontColor = Color.White;
		subBackLabel.Style.FontSize = Length.Pixels( CraftingMenuSection.SectionEntryFontSize );
		subBackLabel.Style.Set( "pointer-events", "none" );

		_contentHost = new Panel { Parent = _sectionRoot };
		_contentHost.Style.Set( "position", "relative" );
		_contentHost.Style.Set( "flex-grow", "1" );
		_contentHost.Style.Set( "width", "100%" );
		_contentHost.Style.Set( "min-height", "280px" );

		_rootPage = new Panel { Parent = _contentHost };
		_rootPage.Style.Set( "position", "absolute" );
		_rootPage.Style.Set( "left", "0" );
		_rootPage.Style.Set( "top", "0" );
		_rootPage.Style.Set( "right", "0" );
		_rootPage.Style.Set( "bottom", "0" );
		_rootPage.Style.Set( "flex-direction", "column" );
		_rootPage.Style.Set( "align-items", "center" );
		_rootPage.Style.Set( "justify-content", "center" );
		_rootPage.Style.Set( "pointer-events", "auto" );

		var buttonColumn = new Panel { Parent = _rootPage };
		buttonColumn.Style.Set( "flex-direction", "column" );
		buttonColumn.Style.Set( "align-items", "stretch" );
		buttonColumn.Style.Set( "gap", $"{ButtonGap}px" );
		buttonColumn.Style.Width = Length.Pixels( ButtonWidth );

		_rootButtons.Clear();
		AddMenuButton( buttonColumn, "game_settings", "Game settings" );
		AddMenuButton( buttonColumn, "controls", "Controls" );
		AddMenuButton( buttonColumn, "audio", "Audio" );
		AddMenuButton( buttonColumn, "video", "Video" );
		AddMenuButton( buttonColumn, "player_stats", "Player stats" );
		AddMenuButton( buttonColumn, "quit_menu", "Quit to menu" );
		AddMenuButton( buttonColumn, "quit_desktop", "Quit to desktop" );

		_subPageHost = new Panel { Parent = _contentHost };
		_subPageHost.Style.Set( "position", "absolute" );
		_subPageHost.Style.Set( "left", "0" );
		_subPageHost.Style.Set( "top", "0" );
		_subPageHost.Style.Set( "right", "0" );
		_subPageHost.Style.Set( "bottom", "0" );
		_subPageHost.Style.Set( "flex-direction", "column" );
		_subPageHost.Style.Set( "align-items", "stretch" );
		_subPageHost.Style.Set( "pointer-events", "auto" );
		_subPageHost.Style.Set( "display", "none" );

		for ( var i = 0; i < SubPages.Length; i++ )
			BuildSubPage( SubPages[i].Id, SubPages[i].Title );

		var footer = new Panel { Parent = _sectionRoot };
		footer.Style.Set( "flex-shrink", "0" );
		footer.Style.Set( "width", "100%" );
		footer.Style.Set( "flex-direction", "row" );
		footer.Style.Set( "justify-content", "center" );
		footer.Style.PaddingTop = Length.Pixels( 12f );

		_buildLabel = new Label { Parent = footer, Text = $"Build {GameBuildLabel.Display}" };
		_buildLabel.Style.FontColor = new Color( 0.55f, 0.58f, 0.64f );
		_buildLabel.Style.FontSize = Length.Pixels( CraftingMenuSection.SectionEntryFontSize );

		NavigateToRoot();
		UpdateVisibility();
	}

	void BuildSubPage( string pageId, string titleText )
	{
		var page = new Panel { Parent = _subPageHost };
		page.Style.Set( "position", "absolute" );
		page.Style.Set( "left", "0" );
		page.Style.Set( "top", "0" );
		page.Style.Set( "right", "0" );
		page.Style.Set( "bottom", "0" );
		page.Style.Set( "flex-direction", "column" );
		page.Style.Set( "align-items", "stretch" );
		page.Style.Set( "display", "none" );
		page.Style.Set( "pointer-events", "auto" );

		var body = new Panel { Parent = page };
		body.Style.Set( "flex-grow", "1" );
		body.Style.Set( "flex-direction", "column" );
		body.Style.Set( "align-items", "center" );
		body.Style.Set( "justify-content", "center" );
		body.Style.Set( "width", "100%" );
		body.Style.PaddingLeft = Length.Pixels( 24f );
		body.Style.PaddingRight = Length.Pixels( 24f );

		var placeholder = new Label { Parent = body, Text = $"{titleText} options coming soon." };
		placeholder.Style.FontColor = new Color( 0.72f, 0.74f, 0.78f );
		placeholder.Style.FontSize = Length.Pixels( CraftingMenuSection.SectionEntryFontSize );
		placeholder.Style.Set( "text-align", "center" );
		placeholder.Style.Set( "white-space", "normal" );

		_subPageById[pageId] = page;
	}

	void AddMenuButton( Panel parent, string actionId, string labelText )
	{
		var row = new SettingsMenuButtonPanel
		{
			Parent = parent,
			Section = this,
			ActionId = actionId
		};
		StyleMenuButton( row );
		_rootButtons.Add( row );

		var label = new Label { Parent = row, Text = labelText };
		label.Style.FontColor = Color.White;
		label.Style.FontSize = Length.Pixels( CraftingMenuSection.ItemNameFontSize );
		label.Style.Set( "pointer-events", "none" );
	}

	static void StyleMenuButton( Panel row )
	{
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "align-items", "center" );
		row.Style.Set( "justify-content", "center" );
		row.Style.Set( "width", "100%" );
		row.Style.PaddingTop = Length.Pixels( 10f );
		row.Style.PaddingBottom = Length.Pixels( 10f );
		row.Style.PaddingLeft = Length.Pixels( 14f );
		row.Style.PaddingRight = Length.Pixels( 14f );
		row.Style.BackgroundColor = new Color( 0.12f, 0.14f, 0.17f, 0.95f );
		row.Style.Set( "border-width", "1px" );
		row.Style.Set( "border-color", "#454c58" );
		row.Style.Set( "border-radius", "6px" );
		row.Style.Set( "pointer-events", "none" );
	}

	/// <summary>Soft-cursor Attack1 — OS mouse is Hidden while the menu is open.</summary>
	public bool TryInvokeAtScreen( Vector2 screenPos )
	{
		if ( !_menuOpen || !_panelVisible )
			return false;

		var onRoot = string.IsNullOrEmpty( _activeSubPageId );
		if ( !onRoot )
		{
			if ( _backButton is not null && _backButton.IsValid()
			     && InventoryScreenPointer.PanelBoxContainsScreen( _backButton, screenPos ) )
			{
				NavigateToRoot();
				return true;
			}

			return false;
		}

		for ( var i = 0; i < _rootButtons.Count; i++ )
		{
			var button = _rootButtons[i];
			if ( button is null || !button.IsValid() )
				continue;

			if ( !InventoryScreenPointer.PanelBoxContainsScreen( button, screenPos ) )
				continue;

			InvokeAction( button.ActionId );
			return true;
		}

		return false;
	}

	public void InvokeAction( string actionId )
	{
		switch ( actionId )
		{
			case "game_settings":
			case "controls":
			case "audio":
			case "video":
			case "player_stats":
				NavigateToSubPage( actionId );
				break;
			case "quit_menu":
				GameSettingsMenuActions.QuitToMainMenu();
				break;
			case "quit_desktop":
				GameSettingsMenuActions.QuitToDesktop();
				break;
		}
	}

	public void NavigateToRoot()
	{
		_activeSubPageId = null;
		ApplyNavigationState();
	}

	void NavigateToSubPage( string pageId )
	{
		if ( !_subPageById.ContainsKey( pageId ) )
			return;

		_activeSubPageId = pageId;
		ApplyNavigationState();
	}

	static string ResolveSubPageTitle( string pageId )
	{
		for ( var i = 0; i < SubPages.Length; i++ )
		{
			if ( string.Equals( SubPages[i].Id, pageId, StringComparison.OrdinalIgnoreCase ) )
				return SubPages[i].Title;
		}

		return "";
	}

	void ApplyNavigationState()
	{
		var onRoot = string.IsNullOrEmpty( _activeSubPageId );

		if ( _rootHeader is not null )
			_rootHeader.Style.Set( "display", onRoot ? "flex" : "none" );

		if ( _subHeader is not null )
			_subHeader.Style.Set( "display", onRoot ? "none" : "flex" );

		if ( _subHeaderTitle is not null )
			_subHeaderTitle.Text = onRoot ? "" : ResolveSubPageTitle( _activeSubPageId );

		if ( _rootPage is not null )
			_rootPage.Style.Set( "display", onRoot ? "flex" : "none" );

		if ( _subPageHost is not null )
			_subPageHost.Style.Set( "display", onRoot ? "none" : "flex" );

		foreach ( var pair in _subPageById )
			pair.Value.Style.Set( "display", pair.Key == _activeSubPageId ? "flex" : "none" );
	}

	public void Refresh()
	{
		if ( _buildLabel is not null )
			_buildLabel.Text = $"Build {GameBuildLabel.Display}";
	}

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
		{
			NavigateToRoot();
			Refresh();
		}

		UpdateVisibility();
	}

	public void SetPanelVisible( bool visible )
	{
		_panelVisible = visible;
		if ( visible )
			NavigateToRoot();

		UpdateVisibility();
	}

	public void TickMenu( bool menuOpen ) { }

	public void OnMenuGlobalMouseUp() { }

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}
}
