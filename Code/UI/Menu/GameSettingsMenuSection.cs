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
		( "dev_tools", "Dev tools" ),
		( "bug_report", "Report a bug" ),
	};

	/// <summary>Material icon button size (Mark's menu rule: icon buttons ≈ 52 px).</summary>
	const float IconButtonSize = 52f;

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
	readonly List<SettingsMenuButtonPanel> _gameSettingButtons = new();
	readonly List<SettingsMenuButtonPanel> _devToolButtons = new();
	readonly List<SettingsMenuButtonPanel> _audioButtons = new();
	VolumeSlider _musicSlider;
	VolumeSlider _sfxSlider;
	/// <summary>The slider whose track was pressed and is still held (Attack1 down).</summary>
	VolumeSlider _dragSlider;
	Label _fluidBlockLabel;
	Label _sensitivityLabel;
	Label _damageInfoLabel;
	Label _allCraftingLabel;
	Label _staminaLabel;
	Label _dayLockLabel;
	Label _resyncLabel;
	Label _scavDifficultyLabel;
	Label _scavEasierLabel;
	Label _scavHarderLabel;
	SettingsMenuButtonPanel _staminaButton;
	SettingsMenuButtonPanel _dayLockButton;
	SettingsMenuBackButtonPanel _backButton;
	SettingsMenuButtonPanel _bugIconButton;
	BugReportPage _bugReportPage;
	string _activeSubPageId;

	bool _menuOpen;
	bool _panelVisible;

	public void Build( Panel menuColumn )
	{
		// Saved volumes reach the mixers as soon as the HUD exists, not only when the Audio page opens.
		AudioVolumeSettings.EnsureLoaded();

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
		AddMenuButton( buttonColumn, "dev_tools", "Dev tools" );
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

		// Footer: bug report icon bottom-left (every page), build label centred.
		var footer = new Panel { Parent = _sectionRoot };
		footer.Style.Set( "position", "relative" );
		footer.Style.Set( "flex-shrink", "0" );
		footer.Style.Set( "width", "100%" );
		footer.Style.Set( "flex-direction", "row" );
		footer.Style.Set( "justify-content", "center" );
		footer.Style.Set( "align-items", "center" );
		footer.Style.Height = Length.Pixels( IconButtonSize + 8f );
		footer.Style.PaddingTop = Length.Pixels( 8f );

		_bugIconButton = new SettingsMenuButtonPanel { Parent = footer, Section = this, ActionId = "bug_report" };
		_bugIconButton.Style.Set( "position", "absolute" );
		_bugIconButton.Style.Set( "left", "0" );
		_bugIconButton.Style.Set( "top", "8px" );
		_bugIconButton.Style.Width = Length.Pixels( IconButtonSize );
		_bugIconButton.Style.Height = Length.Pixels( IconButtonSize );
		UiStyle.Button( _bugIconButton, UiButton.Ghost );
		_bugIconButton.Style.Set( "pointer-events", "none" );

		var bugIcon = new IconPanel { Parent = _bugIconButton, Text = "bug_report" };
		bugIcon.Style.FontSize = Length.Pixels( 30f );
		bugIcon.Style.FontColor = UiTheme.Colors.TextPrimaryColor;
		bugIcon.Style.Set( "pointer-events", "none" );

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

			var blockHint = new Label
			{
				Parent = body,
				Text = "Fluid block — ON: flick the mouse to move a held guard to another side; hold attack as well to keep it where it is while you turn. OFF: let go of block and press again to change sides.",
			};
			UiStyle.Text( blockHint, UiText.Secondary, CraftingMenuSection.SectionEntryFontSize );
			blockHint.Style.Set( "text-align", "center" );
			blockHint.Style.Set( "white-space", "normal" );
			blockHint.Style.Set( "margin-top", "24px" );
			blockHint.Style.Set( "margin-bottom", "14px" );

			_fluidBlockLabel = AddControlSchemeButton( body, "fluid_block", "" );

			var sensitivityHint = new Label
			{
				Parent = body,
				Text = "Combat sensitivity — how much mouse movement flips the attack / guard direction (left, right, overhead). 1 is the quickest; 10 needs a long, deliberate movement.",
			};
			UiStyle.Text( sensitivityHint, UiText.Secondary, CraftingMenuSection.SectionEntryFontSize );
			sensitivityHint.Style.Set( "text-align", "center" );
			sensitivityHint.Style.Set( "white-space", "normal" );
			sensitivityHint.Style.Set( "margin-top", "24px" );
			sensitivityHint.Style.Set( "margin-bottom", "14px" );

			// [-] value [+] on one fixed-width row: the label has its own width, so the buttons never move.
			var sensitivityRow = new Panel { Parent = body };
			sensitivityRow.Style.Set( "flex-direction", "row" );
			sensitivityRow.Style.Set( "align-items", "center" );
			sensitivityRow.Style.Set( "justify-content", "center" );
			sensitivityRow.Style.Set( "gap", $"{ButtonGap}px" );
			AddControlStepButton( sensitivityRow, "sensitivity_down", "-" );
			_sensitivityLabel = new Label { Parent = sensitivityRow, Text = "" };
			UiStyle.Text( _sensitivityLabel, UiText.Body, CraftingMenuSection.ItemNameFontSize );
			_sensitivityLabel.Style.Width = Length.Pixels( 260f );
			_sensitivityLabel.Style.Set( "flex-shrink", "0" );
			_sensitivityLabel.Style.Set( "text-align", "center" );
			_sensitivityLabel.Style.Set( "white-space", "nowrap" );
			AddControlStepButton( sensitivityRow, "sensitivity_up", "+" );
			RefreshControlLabels();
		}
		else if ( string.Equals( pageId, "game_settings", StringComparison.OrdinalIgnoreCase ) )
		{
			var column = new Panel { Parent = body };
			column.Style.Set( "flex-direction", "column" );
			column.Style.Set( "align-items", "stretch" );
			column.Style.Set( "gap", $"{ButtonGap}px" );
			column.Style.Width = Length.Pixels( ButtonWidth + 120f );

			AddDevToolHint( column, "Practice readout under the crosshair: the damage of your last hit and the bonus or penalty from turning with or against the swing." );
			var row = new SettingsMenuButtonPanel { Parent = column, Section = this, ActionId = "damage_info" };
			StyleMenuButton( row, UiButton.Default );
			_gameSettingButtons.Add( row );
			_damageInfoLabel = new Label { Parent = row, Text = "" };
			UiStyle.ButtonLabel( _damageInfoLabel, UiButton.Default, CraftingMenuSection.ItemNameFontSize );
			RefreshControlLabels();
		}
		else if ( string.Equals( pageId, "dev_tools", StringComparison.OrdinalIgnoreCase ) )
		{
			// Dev toggles (DevToolsSettings): one fixed-width button per flag, its label reads the current state.
			// Two fixed-width columns side by side: the flags on the left, the player controls on the right.
			var columns = new Panel { Parent = body };
			columns.Style.Set( "flex-direction", "row" );
			columns.Style.Set( "align-items", "center" );
			columns.Style.Set( "justify-content", "center" );
			columns.Style.Set( "gap", "48px" );

			var column = AddDevToolColumn( columns );
			BuildDevPlayersColumn( AddDevToolColumn( columns ) );

			AddDevToolHint( column, "Crafting menu: every recipe in your pocket and free (dev), or only where it belongs - workbench and stations." );
			_allCraftingLabel = AddDevToolButton( column, "dev_all_crafting", out _ );

			AddDevToolHint( column, "Everyone on the server (host only):" ).Style.Set( "margin-top", "14px" );
			_staminaLabel = AddDevToolButton( column, "dev_stamina", out _staminaButton );
			_dayLockLabel = AddDevToolButton( column, "dev_day_lock", out _dayLockButton );
			AddScavDifficultyStepper( column );

			AddDevToolHint( column, "Out of sync after a code change: the host sends everything to everyone again." ).Style.Set( "margin-top", "14px" );
			_resyncLabel = AddDevToolButton( column, "dev_resync", out _ );
			RefreshDevToolLabels();
		}
		else if ( string.Equals( pageId, "audio", StringComparison.OrdinalIgnoreCase ) )
		{
			var column = new Panel { Parent = body };
			column.Style.Set( "flex-direction", "column" );
			column.Style.Set( "align-items", "stretch" );
			column.Style.Set( "gap", $"{ButtonGap + 8f}px" );
			column.Style.Width = Length.Pixels( AudioRowWidth );

			_audioButtons.Clear();
			_musicSlider = AddVolumeSlider( column, "Music", "audio_music", isMusic: true );
			_sfxSlider = AddVolumeSlider( column, "Sound effects", "audio_sfx", isMusic: false );
			RefreshVolumeSliders();
		}
		else if ( string.Equals( pageId, "bug_report", StringComparison.OrdinalIgnoreCase ) )
		{
			_bugReportPage = new BugReportPage();
			_bugReportPage.Build( body );
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
		var kind = ButtonKindFor( actionId );
		StyleMenuButton( row, kind );
		_rootButtons.Add( row );

		var label = new Label { Parent = row, Text = labelText };
		UiStyle.ButtonLabel( label, kind, CraftingMenuSection.ItemNameFontSize );
	}

	/// <summary>Controls page: the toggle labels read the saved state.</summary>
	void RefreshControlLabels()
	{
		SetLabel( _fluidBlockLabel, $"Fluid block: {OnOff( CombatControlSettings.FluidBlock )}" );
		SetLabel( _sensitivityLabel, $"Combat sensitivity: {CombatControlSettings.Sensitivity}" );
		SetLabel( _damageInfoLabel, $"Show damage information: {OnOff( CombatControlSettings.ShowDamageInfo )}" );
	}

	/// <summary>A 52 px [-] / [+] button on the controls page (hit-tested with the page's other buttons).</summary>
	void AddControlStepButton( Panel row, string actionId, string text )
	{
		var button = new SettingsMenuButtonPanel { Parent = row, Section = this, ActionId = actionId };
		StyleMenuButton( button, UiButton.Default );
		button.Style.Set( "width", $"{IconButtonSize}px" );
		button.Style.Set( "height", $"{IconButtonSize}px" );
		button.Style.Set( "flex-shrink", "0" );
		button.Style.Set( "justify-content", "center" );
		button.Style.Set( "align-items", "center" );
		button.Style.PaddingLeft = Length.Pixels( 0f );
		button.Style.PaddingRight = Length.Pixels( 0f );
		_controlSchemeButtons.Add( button );

		var label = new Label { Parent = button, Text = text };
		UiStyle.ButtonLabel( label, UiButton.Default, CraftingMenuSection.ItemNameFontSize );
		label.Style.Set( "text-align", "center" );
	}

	Label AddControlSchemeButton( Panel parent, string actionId, string labelText )
	{
		var row = new SettingsMenuButtonPanel
		{
			Parent = parent,
			Section = this,
			ActionId = actionId
		};
		StyleMenuButton( row, UiButton.Default );
		_controlSchemeButtons.Add( row );

		var label = new Label { Parent = row, Text = labelText };
		UiStyle.ButtonLabel( label, UiButton.Default, CraftingMenuSection.ItemNameFontSize );
		return label;
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

	static Panel AddDevToolColumn( Panel parent )
	{
		var column = new Panel { Parent = parent };
		column.Style.Set( "flex-direction", "column" );
		column.Style.Set( "align-items", "stretch" );
		column.Style.Set( "gap", $"{ButtonGap}px" );
		column.Style.Set( "flex-shrink", "0" );
		column.Style.Width = Length.Pixels( ButtonWidth + 120f );
		return column;
	}

	// ---- Dev tools: players (DevPlayerControls) — [<] name [>] cycler, then bring / go to / respawn ----

	/// <summary>The scene is scanned for pawns this often while the page is open, never per frame.</summary>
	const float DevPlayerListRefreshSeconds = 0.5f;

	readonly List<PlayerVitals> _devPlayers = new();
	RealTimeSince _sinceDevPlayerScan = 999f;
	/// <summary>The cycler's pick, by pawn id, so it survives players joining and leaving.</summary>
	Guid _devPlayerId;
	string _devPlayerStatus = "";
	Label _devPlayerNameLabel;
	Label _devPlayerPrevLabel;
	Label _devPlayerNextLabel;
	Label _devPlayerBringLabel;
	Label _devPlayerGoToLabel;
	Label _devPlayerRespawnLabel;
	Label _devPlayerStatusLabel;

	void BuildDevPlayersColumn( Panel column )
	{
		AddDevToolHint( column, "Players (host only): pick one, then move or respawn them." );

		var row = new Panel { Parent = column };
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "align-items", "center" );
		row.Style.Set( "gap", $"{ButtonGap}px" );
		row.Style.Set( "width", "100%" );

		_devPlayerPrevLabel = AddStepButton( row, "dev_player_prev", "<" );
		_devPlayerNameLabel = new Label { Parent = row, Text = "" };
		UiStyle.Text( _devPlayerNameLabel, UiText.Body, CraftingMenuSection.ItemNameFontSize );
		// Zero basis + hidden overflow: a long name is cut, the arrows never move.
		_devPlayerNameLabel.Style.Set( "flex-grow", "1" );
		_devPlayerNameLabel.Style.Set( "flex-basis", "0" );
		_devPlayerNameLabel.Style.Set( "overflow", "hidden" );
		_devPlayerNameLabel.Style.Set( "text-align", "center" );
		_devPlayerNameLabel.Style.Set( "white-space", "nowrap" );
		_devPlayerNextLabel = AddStepButton( row, "dev_player_next", ">" );

		_devPlayerBringLabel = AddDevToolButton( column, "dev_player_bring", out _ );
		_devPlayerGoToLabel = AddDevToolButton( column, "dev_player_goto", out _ );
		_devPlayerRespawnLabel = AddDevToolButton( column, "dev_player_respawn", out _ );

		// What the last press did; a fixed height so the column never reflows.
		_devPlayerStatusLabel = new Label { Parent = column, Text = "" };
		UiStyle.Text( _devPlayerStatusLabel, UiText.Secondary, CraftingMenuSection.SectionEntryFontSize );
		_devPlayerStatusLabel.Style.Set( "text-align", "center" );
		_devPlayerStatusLabel.Style.Set( "white-space", "nowrap" );
		_devPlayerStatusLabel.Style.Set( "overflow", "hidden" );
		_devPlayerStatusLabel.Style.Height = Length.Pixels( 24f );
	}

	int DevPlayerIndex()
	{
		for ( var i = 0; i < _devPlayers.Count; i++ )
		{
			if ( _devPlayers[i].IsValid() && _devPlayers[i].GameObject.Id == _devPlayerId )
				return i;
		}

		return -1;
	}

	/// <summary>The picked pawn; a pick that left the game falls back to the first player.</summary>
	PlayerVitals SelectedDevPlayer( bool rescan = false )
	{
		if ( rescan || _sinceDevPlayerScan > DevPlayerListRefreshSeconds )
		{
			_sinceDevPlayerScan = 0f;
			DevPlayerControls.CollectPlayers( _devPlayers );
		}

		if ( _devPlayers.Count == 0 )
			return null;

		var index = DevPlayerIndex();
		if ( index < 0 )
		{
			index = 0;
			_devPlayerId = _devPlayers[0].GameObject.Id;
		}

		var player = _devPlayers[index];
		return player.IsValid() ? player : null;
	}

	void StepDevPlayer( int delta )
	{
		if ( SelectedDevPlayer( rescan: true ) is null )
			return;

		var index = ( DevPlayerIndex() + delta + _devPlayers.Count ) % _devPlayers.Count;
		_devPlayerId = _devPlayers[index].GameObject.Id;
		_devPlayerStatus = "";
	}

	void RefreshDevPlayerLabels()
	{
		var host = DevPlayerControls.CanUse;
		var selected = SelectedDevPlayer();
		var local = DevPlayerControls.FindLocal();
		var isSelf = selected is not null && ReferenceEquals( selected, local );

		string name;
		if ( selected is null )
			name = "No players";
		else
		{
			name = $"{DevPlayerControls.NameOf( selected, local )}  {DevPlayerIndex() + 1}/{_devPlayers.Count}";
			if ( isSelf )
				name += "  (you)";
			else if ( local is not null )
				name += $"  {TerrainWorldUnits.EngineToMeters( selected.WorldPosition.Distance( local.WorldPosition ) ):0} m";
		}

		SetLabel( _devPlayerNameLabel, name );
		SetLabel( _devPlayerBringLabel, "Bring them to me" );
		SetLabel( _devPlayerGoToLabel, "Bring me to them" );
		SetLabel( _devPlayerRespawnLabel, isSelf ? "Respawn me" : "Respawn them" );
		SetLabel( _devPlayerStatusLabel, host ? _devPlayerStatus : "Host only" );

		// Greyed = a press does nothing: a client, nobody picked, or moving yourself to yourself.
		var canAct = host && selected is not null;
		var canMove = canAct && !isSelf && local is not null;
		TintDevLabel( _devPlayerNameLabel, host );
		TintDevLabel( _devPlayerPrevLabel, host && _devPlayers.Count > 1 );
		TintDevLabel( _devPlayerNextLabel, host && _devPlayers.Count > 1 );
		TintDevLabel( _devPlayerBringLabel, canMove );
		TintDevLabel( _devPlayerGoToLabel, canMove );
		TintDevLabel( _devPlayerRespawnLabel, canAct );
	}

	static void TintDevLabel( Label label, bool active )
	{
		if ( label is not null && label.IsValid() )
			label.Style.FontColor = active ? UiTheme.Colors.TextPrimaryColor : UiTheme.Colors.TextMutedColor;
	}

	/// <summary>Scav difficulty: [-] level name [+] on one row, the label a fixed width so the row never reflows.</summary>
	void AddScavDifficultyStepper( Panel parent )
	{
		var row = new Panel { Parent = parent };
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "align-items", "center" );
		row.Style.Set( "gap", $"{ButtonGap}px" );
		row.Style.Set( "width", "100%" );

		_scavEasierLabel = AddStepButton( row, "dev_scav_easier", "-" );
		_scavDifficultyLabel = new Label { Parent = row, Text = "" };
		UiStyle.Text( _scavDifficultyLabel, UiText.Body, CraftingMenuSection.ItemNameFontSize );
		_scavDifficultyLabel.Style.Set( "flex-grow", "1" );
		_scavDifficultyLabel.Style.Set( "text-align", "center" );
		_scavDifficultyLabel.Style.Set( "white-space", "nowrap" );
		_scavHarderLabel = AddStepButton( row, "dev_scav_harder", "+" );
	}

	Label AddStepButton( Panel row, string actionId, string text )
	{
		var label = AddDevToolButton( row, actionId, out var button );
		button.Style.Set( "width", "52px" );
		button.Style.Set( "height", "52px" );
		button.Style.Set( "flex-shrink", "0" );
		button.Style.Set( "justify-content", "center" );
		button.Style.Set( "align-items", "center" );
		button.Style.PaddingLeft = Length.Pixels( 0f );
		button.Style.PaddingRight = Length.Pixels( 0f );
		label.Text = text;
		label.Style.Set( "text-align", "center" );
		return label;
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

	// ---- Audio page: [name] [-] [track] [+] [value], every cell a fixed width so nothing reflows ----

	const float AudioRowWidth = 640f;
	/// <summary>[-] / [+] and a click or drag on the track all land on 5 % steps.</summary>
	const float VolumeStep = 0.05f;

	sealed class VolumeSlider
	{
		public bool IsMusic;
		public Panel Track;
		public Panel Fill;
		public Label Value;
	}

	VolumeSlider AddVolumeSlider( Panel parent, string title, string actionPrefix, bool isMusic )
	{
		var row = new Panel { Parent = parent };
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "align-items", "center" );
		row.Style.Set( "gap", $"{ButtonGap}px" );
		row.Style.Set( "width", "100%" );
		row.Style.Height = Length.Pixels( IconButtonSize );

		var name = new Label { Parent = row, Text = title };
		UiStyle.Text( name, UiText.Body, CraftingMenuSection.ItemNameFontSize );
		name.Style.Width = Length.Pixels( 170f );
		name.Style.Set( "flex-shrink", "0" );
		name.Style.Set( "white-space", "nowrap" );

		AddVolumeStepButton( row, $"{actionPrefix}_down", "-" );

		// The whole 52 px tall cell is the hit area; the bar inside is the thin visible part.
		var track = new Panel { Parent = row };
		track.Style.Set( "flex-grow", "1" );
		track.Style.Set( "flex-direction", "row" );
		track.Style.Set( "align-items", "center" );
		track.Style.Height = Length.Pixels( IconButtonSize );
		track.Style.Set( "pointer-events", "none" );

		var bar = new Panel { Parent = track };
		bar.Style.Set( "width", "100%" );
		bar.Style.Height = Length.Pixels( 14f );
		bar.Style.Set( "flex-direction", "row" );
		bar.Style.Set( "overflow", "hidden" );
		bar.Style.Set( "border-radius", "4px" );
		bar.Style.BackgroundColor = UiTheme.Colors.BarTrackColor;

		var fill = new Panel { Parent = bar };
		fill.Style.Height = Length.Percent( 100 );
		fill.Style.BackgroundColor = UiTheme.Colors.AccentColor;

		AddVolumeStepButton( row, $"{actionPrefix}_up", "+" );

		var value = new Label { Parent = row, Text = "" };
		UiStyle.Text( value, UiText.Body, CraftingMenuSection.ItemNameFontSize );
		value.Style.Width = Length.Pixels( 64f );
		value.Style.Set( "flex-shrink", "0" );
		value.Style.Set( "text-align", "right" );
		value.Style.Set( "white-space", "nowrap" );

		return new VolumeSlider { IsMusic = isMusic, Track = track, Fill = fill, Value = value };
	}

	void AddVolumeStepButton( Panel row, string actionId, string text )
	{
		var button = new SettingsMenuButtonPanel { Parent = row, Section = this, ActionId = actionId };
		StyleMenuButton( button, UiButton.Default );
		button.Style.Set( "width", $"{IconButtonSize}px" );
		button.Style.Set( "height", $"{IconButtonSize}px" );
		button.Style.Set( "flex-shrink", "0" );
		button.Style.Set( "justify-content", "center" );
		button.Style.Set( "align-items", "center" );
		button.Style.PaddingLeft = Length.Pixels( 0f );
		button.Style.PaddingRight = Length.Pixels( 0f );
		_audioButtons.Add( button );

		var label = new Label { Parent = button, Text = text };
		UiStyle.ButtonLabel( label, UiButton.Default, CraftingMenuSection.ItemNameFontSize );
		label.Style.Set( "text-align", "center" );
	}

	static float VolumeOf( VolumeSlider slider ) =>
		slider.IsMusic ? AudioVolumeSettings.MusicVolume : AudioVolumeSettings.SfxVolume;

	/// <summary>Snaps to the 5 % grid and applies live; the file is written by the caller once the change is final.</summary>
	void SetVolume( VolumeSlider slider, float value )
	{
		value = MathF.Round( Math.Clamp( value, 0f, 1f ) / VolumeStep ) * VolumeStep;
		if ( MathF.Abs( value - VolumeOf( slider ) ) < 0.001f )
			return;

		if ( slider.IsMusic )
			AudioVolumeSettings.SetMusicVolume( value );
		else
			AudioVolumeSettings.SetSfxVolume( value );

		RefreshVolumeSliders();
	}

	void StepVolume( VolumeSlider slider, float delta )
	{
		if ( slider is null )
			return;

		SetVolume( slider, VolumeOf( slider ) + delta );
		AudioVolumeSettings.Save();
	}

	void SetVolumeFromPointer( VolumeSlider slider, Vector2 screenPos )
	{
		var rect = slider.Track.Box.Rect;
		if ( rect.Width <= 1f )
			return;

		SetVolume( slider, ( screenPos.x - rect.Left ) / rect.Width );
	}

	bool TryBeginVolumeDrag( Vector2 screenPos )
	{
		foreach ( var slider in new[] { _musicSlider, _sfxSlider } )
		{
			if ( slider is null || !InventoryScreenPointer.PanelBoxContainsScreen( slider.Track, screenPos ) )
				continue;

			_dragSlider = slider;
			SetVolumeFromPointer( slider, screenPos );
			return true;
		}

		return false;
	}

	/// <summary>Follows the pointer while Attack1 stays down; letting go (or leaving the page) saves.</summary>
	void TickVolumeDrag()
	{
		if ( _dragSlider is null )
			return;

		if ( Input.Down( "Attack1" ) )
			SetVolumeFromPointer( _dragSlider, InventoryScreenPointer.GetMenuOrMousePosition() );
		else
			EndVolumeDrag();
	}

	void EndVolumeDrag()
	{
		if ( _dragSlider is null )
			return;

		_dragSlider = null;
		AudioVolumeSettings.Save();
	}

	void RefreshVolumeSliders()
	{
		foreach ( var slider in new[] { _musicSlider, _sfxSlider } )
		{
			if ( slider is null )
				continue;

			var percent = MathF.Round( VolumeOf( slider ) * 100f );
			slider.Fill.Style.Width = Length.Percent( percent );
			SetLabel( slider.Value, $"{percent:0}%" );
		}
	}

	/// <summary>Quit actions are destructive; everything else is a plain menu button.</summary>
	static UiButton ButtonKindFor( string actionId ) =>
		actionId switch
		{
			"quit_menu" or "quit_desktop" => UiButton.Danger,
			"bug_report" => UiButton.Ghost,
			_ => UiButton.Default,
		};

	bool OnBugReportPage => string.Equals( _activeSubPageId, "bug_report", StringComparison.OrdinalIgnoreCase );

	/// <summary>True while a bug report box has the keyboard — the menu's page hotkeys and hotbar keys stand down.</summary>
	public bool IsCapturingText => _menuOpen && _panelVisible && OnBugReportPage && ( _bugReportPage?.HasTextFocus ?? false );

	/// <summary>Escape while typing a report only lets go of the keyboard; the next Escape closes the menu.</summary>
	public bool TryConsumeEscape()
	{
		if ( !IsCapturingText )
			return false;

		_bugReportPage.OnHidden();
		return true;
	}

	/// <summary>Button labels read the live state (host-wide ones arrive by sync, so this also runs while the page is open).</summary>
	void RefreshDevToolLabels()
	{
		SetLabel( _allCraftingLabel, GameHacks.AllCrafting ? "Recipes: all in pocket, free" : "Recipes: stations only" );

		// Clients see the host-wide state greyed out; only the host's click changes it.
		var host = DevToolsSettings.CanChangeHostSettings;
		var suffix = host ? "" : "  (host only)";
		SetLabel( _staminaLabel, $"Stamina drain: {OnOff( !DevToolsSettings.InfiniteStamina )}{suffix}" );
		SetLabel( _dayLockLabel, $"Lock daytime: {OnOff( DevToolsSettings.DayLocked )}{suffix}" );
		SetLabel( _resyncLabel, "Resync players" );
		var level = DevToolsSettings.ScavDifficulty;
		SetLabel( _scavDifficultyLabel, $"Scav difficulty: {level}/{ScavCombatStyleCatalog.LevelCount} {ScavCombatStyleCatalog.Get( level ).Name}{suffix}" );
		var tint = host ? UiTheme.Colors.TextPrimaryColor : UiTheme.Colors.TextMutedColor;
		if ( _staminaLabel is not null && _staminaLabel.IsValid() )
			_staminaLabel.Style.FontColor = tint;
		if ( _dayLockLabel is not null && _dayLockLabel.IsValid() )
			_dayLockLabel.Style.FontColor = tint;
		foreach ( var label in new[] { _scavDifficultyLabel, _scavEasierLabel, _scavHarderLabel } )
		{
			if ( label is not null && label.IsValid() )
				label.Style.FontColor = tint;
		}

		RefreshDevPlayerLabels();
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
		row.Style.Set( "width", "100%" );
		row.Style.PaddingTop = Length.Pixels( 10f );
		row.Style.PaddingBottom = Length.Pixels( 10f );
		row.Style.PaddingLeft = Length.Pixels( 14f );
		row.Style.PaddingRight = Length.Pixels( 14f );
		UiStyle.Button( row, kind );
		row.Style.Set( "pointer-events", "none" );
	}

	// ---- Soft-cursor hover (polled from TickMenu; the overlay never sends real mouse events) ----

	Panel _hoveredButton;

	/// <summary>The buttons the current page can click: root list, or Back + the open sub-page's list.</summary>
	void PollHover()
	{
		var pos = InventoryScreenPointer.GetMenuOrMousePosition();
		Panel hit = null;

		if ( !OnBugReportPage && InventoryScreenPointer.PanelBoxContainsScreen( _bugIconButton, pos ) )
			hit = _bugIconButton;
		else if ( string.IsNullOrEmpty( _activeSubPageId ) )
		{
			hit = FindButtonAt( _rootButtons, pos );
		}
		else
		{
			if ( _backButton is not null && _backButton.IsValid() && InventoryScreenPointer.PanelBoxContainsScreen( _backButton, pos ) )
				hit = _backButton;
			else if ( string.Equals( _activeSubPageId, "controls", StringComparison.OrdinalIgnoreCase ) )
				hit = FindButtonAt( _controlSchemeButtons, pos );
			else if ( string.Equals( _activeSubPageId, "game_settings", StringComparison.OrdinalIgnoreCase ) )
				hit = FindButtonAt( _gameSettingButtons, pos );
			else if ( string.Equals( _activeSubPageId, "dev_tools", StringComparison.OrdinalIgnoreCase ) )
				hit = FindButtonAt( _devToolButtons, pos );
			else if ( string.Equals( _activeSubPageId, "audio", StringComparison.OrdinalIgnoreCase ) )
				hit = FindButtonAt( _audioButtons, pos );
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

	/// <summary>Soft-cursor Attack1 — OS mouse is Hidden while the menu is open.</summary>
	public bool TryInvokeAtScreen( Vector2 screenPos )
	{
		if ( !_menuOpen || !_panelVisible )
			return false;

		if ( !OnBugReportPage && InventoryScreenPointer.PanelBoxContainsScreen( _bugIconButton, screenPos ) )
		{
			NavigateToSubPage( "bug_report" );
			return true;
		}

		var onRoot = string.IsNullOrEmpty( _activeSubPageId );
		if ( !onRoot )
		{
			if ( _backButton is not null && _backButton.IsValid()
			     && InventoryScreenPointer.PanelBoxContainsScreen( _backButton, screenPos ) )
			{
				NavigateToRoot();
				return true;
			}

			if ( OnBugReportPage )
				return _bugReportPage?.TryClick( screenPos ) ?? false;

			if ( string.Equals( _activeSubPageId, "controls", StringComparison.OrdinalIgnoreCase ) )
				return TryInvokeButtonAt( _controlSchemeButtons, screenPos );

			if ( string.Equals( _activeSubPageId, "game_settings", StringComparison.OrdinalIgnoreCase ) )
				return TryInvokeButtonAt( _gameSettingButtons, screenPos );

			if ( string.Equals( _activeSubPageId, "dev_tools", StringComparison.OrdinalIgnoreCase ) )
				return TryInvokeButtonAt( _devToolButtons, screenPos );

			if ( string.Equals( _activeSubPageId, "audio", StringComparison.OrdinalIgnoreCase ) )
				return TryInvokeButtonAt( _audioButtons, screenPos ) || TryBeginVolumeDrag( screenPos );

			return false;
		}

		return TryInvokeButtonAt( _rootButtons, screenPos );
	}

	bool TryInvokeButtonAt( List<SettingsMenuButtonPanel> buttons, Vector2 screenPos )
	{
		for ( var i = 0; i < buttons.Count; i++ )
		{
			var button = buttons[i];
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
			case "dev_tools":
			case "bug_report":
				NavigateToSubPage( actionId );
				break;
			case "dev_all_crafting":
				GameHacks.SetAllCrafting( !GameHacks.AllCrafting );
				RefreshDevToolLabels();
				break;
			case "dev_stamina":
				DevToolsSettings.HostSetInfiniteStamina( !DevToolsSettings.InfiniteStamina );
				RefreshDevToolLabels();
				break;
			case "dev_day_lock":
				DevToolsSettings.HostSetDayLocked( !DevToolsSettings.DayLocked );
				RefreshDevToolLabels();
				break;
			case "dev_resync":
				DevToolsSettings.RequestResync();
				break;
			case "dev_scav_easier":
				DevToolsSettings.HostSetScavDifficulty( DevToolsSettings.ScavDifficulty - 1 );
				RefreshDevToolLabels();
				break;
			case "dev_scav_harder":
				DevToolsSettings.HostSetScavDifficulty( DevToolsSettings.ScavDifficulty + 1 );
				RefreshDevToolLabels();
				break;
			case "dev_player_prev":
				StepDevPlayer( -1 );
				RefreshDevToolLabels();
				break;
			case "dev_player_next":
				StepDevPlayer( 1 );
				RefreshDevToolLabels();
				break;
			case "dev_player_bring":
				_devPlayerStatus = DevPlayerControls.BringToMe( SelectedDevPlayer( rescan: true ) );
				RefreshDevToolLabels();
				break;
			case "dev_player_goto":
				_devPlayerStatus = DevPlayerControls.GoTo( SelectedDevPlayer( rescan: true ) );
				RefreshDevToolLabels();
				break;
			case "dev_player_respawn":
				_devPlayerStatus = DevPlayerControls.Respawn( SelectedDevPlayer( rescan: true ) );
				RefreshDevToolLabels();
				break;
			case "audio_music_down":
				StepVolume( _musicSlider, -VolumeStep );
				break;
			case "audio_music_up":
				StepVolume( _musicSlider, VolumeStep );
				break;
			case "audio_sfx_down":
				StepVolume( _sfxSlider, -VolumeStep );
				break;
			case "audio_sfx_up":
				StepVolume( _sfxSlider, VolumeStep );
				break;
			case "grapple_pro":
				GrappleControlSchemeStore.Set( GrappleControlScheme.Pro );
				break;
			case "grapple_training":
				GrappleControlSchemeStore.Set( GrappleControlScheme.TrainingWheels );
				break;
			case "fluid_block":
				CombatControlSettings.SetFluidBlock( !CombatControlSettings.FluidBlock );
				RefreshControlLabels();
				break;
			case "sensitivity_down":
				CombatControlSettings.SetSensitivity( CombatControlSettings.Sensitivity - 1 );
				RefreshControlLabels();
				break;
			case "sensitivity_up":
				CombatControlSettings.SetSensitivity( CombatControlSettings.Sensitivity + 1 );
				RefreshControlLabels();
				break;
			case "damage_info":
				CombatControlSettings.SetShowDamageInfo( !CombatControlSettings.ShowDamageInfo );
				RefreshControlLabels();
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

		EndVolumeDrag();
		RefreshVolumeSliders();

		if ( _bugReportPage is not null )
		{
			if ( OnBugReportPage && _menuOpen && _panelVisible )
				_bugReportPage.OnShown();
			else
				_bugReportPage.OnHidden();
		}

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
		RefreshDevToolLabels();
	}

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
		{
			NavigateToRoot();
			Refresh();
		}
		else
		{
			_bugReportPage?.OnHidden();
		}

		UpdateVisibility();
	}

	public void SetPanelVisible( bool visible )
	{
		_panelVisible = visible;
		if ( visible )
			NavigateToRoot();
		else
			_bugReportPage?.OnHidden();

		UpdateVisibility();
	}

	public void TickMenu( bool menuOpen )
	{
		if ( !menuOpen || !_panelVisible )
		{
			SetHoveredButton( null );
			EndVolumeDrag();
			return;
		}

		PollHover();
		TickVolumeDrag();

		if ( string.Equals( _activeSubPageId, "dev_tools", StringComparison.OrdinalIgnoreCase ) )
			RefreshDevToolLabels();
		else if ( OnBugReportPage )
			_bugReportPage?.Tick( InventoryScreenPointer.GetMenuOrMousePosition() );
	}

	public void OnMenuGlobalMouseUp() { }

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}
}
