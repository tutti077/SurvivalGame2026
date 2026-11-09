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
	readonly List<SettingsMenuButtonPanel> _controlSchemeButtons = new();
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
		UiStyle.Surface( _sectionRoot, UiSurface.Frame );

		_rootHeader = new Panel { Parent = _sectionRoot };
		_rootHeader.Style.Set( "flex-direction", "row" );
		_rootHeader.Style.Set( "align-items", "center" );
		_rootHeader.Style.Set( "justify-content", "center" );
		_rootHeader.Style.Set( "width", "100%" );
		_rootHeader.Style.Set( "margin-bottom", "8px" );
		_rootHeader.Style.Set( "flex-shrink", "0" );

		var rootTitle = new Label { Parent = _rootHeader, Text = "Settings" };
		UiStyle.Text( rootTitle, UiText.Title, CraftingMenuSection.CraftingTitleFontSize );

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
		UiStyle.Text( _subHeaderTitle, UiText.Title, CraftingMenuSection.CraftingTitleFontSize );
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
		UiStyle.Button( subBack, UiButton.Default );
		subBack.Style.Set( "pointer-events", "none" );

		var subBackLabel = new Label { Parent = subBack, Text = "Back" };
		UiStyle.ButtonLabel( subBackLabel, UiButton.Default, CraftingMenuSection.SectionEntryFontSize );

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
		UiStyle.Text( _buildLabel, UiText.Muted, CraftingMenuSection.SectionEntryFontSize );

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

		if ( string.Equals( pageId, "controls", StringComparison.OrdinalIgnoreCase ) )
		{
			var intro = new Label
			{
				Parent = body,
				Text = "Grapple winch — Pro is E retract / Q detract. Training Wheels is Space retract / Ctrl detract.",
			};
			UiStyle.Text( intro, UiText.Secondary, CraftingMenuSection.SectionEntryFontSize );
			intro.Style.Set( "text-align", "center" );
			intro.Style.Set( "white-space", "normal" );
			intro.Style.Set( "margin-bottom", "14px" );

			AddControlSchemeButton( body, "grapple_pro", "Pro (E / Q)" );
			AddControlSchemeButton( body, "grapple_training", "Training Wheels (Space / Ctrl)" );
		}
		else
		{
			var placeholder = new Label { Parent = body, Text = $"{titleText} options coming soon." };
			UiStyle.Text( placeholder, UiText.Secondary, CraftingMenuSection.SectionEntryFontSize );
			placeholder.Style.Set( "text-align", "center" );
			placeholder.Style.Set( "white-space", "normal" );
		}

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
		StyleMenuButton( row, UiButton.Default );
		_rootButtons.Add( row );

		var label = new Label { Parent = row, Text = labelText };
		UiStyle.ButtonLabel( label, UiButton.Default, CraftingMenuSection.ItemNameFontSize );
	}

	static Label AddDevToolHint( Panel parent, string text )
	{
		var hint = new Label { Parent = parent, Text = text };
		UiStyle.Text( hint, UiText.Secondary, CraftingMenuSection.SectionEntryFontSize );
		hint.Style.Set( "text-align", "center" );
		hint.Style.Set( "white-space", "normal" );
		hint.Style.Set( "margin-bottom", "4px" );
		return hint;
	}

	Label AddDevToolButton( Panel parent, string actionId, out SettingsMenuButtonPanel button )
	{
		var row = new SettingsMenuButtonPanel
		{
			Parent = parent,
			Section = this,
			ActionId = actionId
		};
		StyleMenuButton( row, UiButton.Default );
		_devToolButtons.Add( row );
		button = row;

		var label = new Label { Parent = row, Text = "" };
		UiStyle.ButtonLabel( label, UiButton.Default, CraftingMenuSection.ItemNameFontSize );
		return label;
	}

	void AddControlSchemeButton( Panel parent, string actionId, string labelText )
	{
		var row = new SettingsMenuButtonPanel
		{
			Parent = parent,
			Section = this,
			ActionId = actionId
		};
		var kind = ButtonKindFor( actionId );
		StyleMenuButton( row, kind );
		_controlSchemeButtons.Add( row );

		var label = new Label { Parent = row, Text = labelText };
		UiStyle.ButtonLabel( label, kind, CraftingMenuSection.ItemNameFontSize );
	}

	/// <summary>Quit actions are destructive; everything else is a plain menu button.</summary>
	static UiButton ButtonKindFor( string actionId ) =>
		actionId is "quit_menu" or "quit_desktop" ? UiButton.Danger : UiButton.Default;

	/// <summary>Button labels read the live state (host-wide ones arrive by sync, so this also runs while the page is open).</summary>
	void RefreshDevToolLabels()
	{
		SetLabel( _staticComboLabel, $"Static 3-hit combo: {OnOff( DevToolsSettings.StaticMeleeCombo )}" );
		SetLabel( _allCraftingLabel, GameHacks.AllCrafting ? "Recipes: all in pocket, free" : "Recipes: stations only" );

		// Clients see the host-wide state greyed out; only the host's click changes it.
		var host = DevToolsSettings.CanChangeHostSettings;
		var suffix = host ? "" : "  (host only)";
		SetLabel( _staminaLabel, $"Stamina drain: {OnOff( !DevToolsSettings.InfiniteStamina )}{suffix}" );
		SetLabel( _dayLockLabel, $"Lock daytime: {OnOff( DevToolsSettings.DayLocked )}{suffix}" );
		var tint = host ? UiTheme.Colors.TextPrimaryColor : UiTheme.Colors.TextMutedColor;
		if ( _staminaLabel is not null && _staminaLabel.IsValid() )
			_staminaLabel.Style.FontColor = tint;
		if ( _dayLockLabel is not null && _dayLockLabel.IsValid() )
			_dayLockLabel.Style.FontColor = tint;
	}

	static string OnOff( bool on ) => on ? "ON" : "OFF";

	static void SetLabel( Label label, string text )
	{
		if ( label is not null && label.IsValid() && label.Text != text )
			label.Text = text;
	}

	static void StyleMenuButton( Panel row, UiButton kind )
	{
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "align-items", "center" );
		row.Style.Set( "justify-content", "center" );
		row.Style.Set( "width", "100%" );
		row.Style.PaddingTop = Length.Pixels( 10f );
		row.Style.PaddingBottom = Length.Pixels( 10f );
		row.Style.PaddingLeft = Length.Pixels( 14f );
		row.Style.PaddingRight = Length.Pixels( 14f );
		UiStyle.Button( row, kind );
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

			if ( string.Equals( _activeSubPageId, "controls", StringComparison.OrdinalIgnoreCase ) )
			{
				for ( var i = 0; i < _controlSchemeButtons.Count; i++ )
				{
					var button = _controlSchemeButtons[i];
					if ( button is null || !button.IsValid() )
						continue;

					if ( !InventoryScreenPointer.PanelBoxContainsScreen( button, screenPos ) )
						continue;
	// ---- Soft-cursor hover (polled from TickMenu; the overlay never sends real mouse events) ----

	Panel _hoveredButton;

	/// <summary>The buttons the current page can click: root list, or Back + the open sub-page's list.</summary>
	void PollHover()
	{
		var pos = InventoryScreenPointer.GetMenuOrMousePosition();
		Panel hit = null;

		if ( string.IsNullOrEmpty( _activeSubPageId ) )
		{
			hit = FindButtonAt( _rootButtons, pos );
		}
		else
		{
			if ( _backButton is not null && _backButton.IsValid() && InventoryScreenPointer.PanelBoxContainsScreen( _backButton, pos ) )
				hit = _backButton;
			else if ( string.Equals( _activeSubPageId, "controls", StringComparison.OrdinalIgnoreCase ) )
				hit = FindButtonAt( _controlSchemeButtons, pos );
			else if ( string.Equals( _activeSubPageId, "dev_tools", StringComparison.OrdinalIgnoreCase ) )
				hit = FindButtonAt( _devToolButtons, pos );
		}

		SetHoveredButton( hit );
	}

	static Panel FindButtonAt( List<SettingsMenuButtonPanel> buttons, Vector2 screenPos )
	{
		for ( var i = 0; i < buttons.Count; i++ )
		{
			var button = buttons[i];
			if ( button is null || !button.IsValid() )
				continue;

			if ( InventoryScreenPointer.PanelBoxContainsScreen( button, screenPos ) )
				return button;
		}

		return null;
	}

	void SetHoveredButton( Panel hit )
	{
		if ( ReferenceEquals( hit, _hoveredButton ) )
			return;

		if ( _hoveredButton is not null && _hoveredButton.IsValid() )
			UiStyle.SetButtonState( _hoveredButton, KindOf( _hoveredButton ), hovered: false, pressed: false, disabled: false );

		_hoveredButton = hit;

		if ( _hoveredButton is not null && _hoveredButton.IsValid() )
			UiStyle.SetButtonState( _hoveredButton, KindOf( _hoveredButton ), hovered: true, pressed: false, disabled: false );
	}

	static UiButton KindOf( Panel button ) =>
		button is SettingsMenuButtonPanel menuButton ? ButtonKindFor( menuButton.ActionId ) : UiButton.Default;


					InvokeAction( button.ActionId );
					return true;
				}
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
			case "grapple_pro":
				GrappleControlSchemeStore.Set( GrappleControlScheme.Pro );
				break;
			case "grapple_training":
				GrappleControlSchemeStore.Set( GrappleControlScheme.TrainingWheels );
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

	public void TickMenu( bool menuOpen )
	{
		if ( !menuOpen || !_panelVisible )
		{
			SetHoveredButton( null );
			return;
		}

		PollHover();

		if ( string.Equals( _activeSubPageId, "dev_tools", StringComparison.OrdinalIgnoreCase ) )
			RefreshDevToolLabels();
	}

	public void OnMenuGlobalMouseUp() { }

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}
}
