using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Single <see cref="PanelComponent"/> for local pawn HUD: vitals, harvest prompt, and game menu.
/// Replaces three separate panel roots (each ticked layout/audio separately).
/// </summary>
[Title( "Player Screen HUD" )]
public sealed class PlayerScreenHud : PanelComponent
{
	const float BarWidth = 360f;
	const float BarHeight = 22f;
	const float BarGap = 6f;
	const float InventoryMenuColumnWidth = 400f;
	const float CraftingMenuColumnWidth = 455f;
	const string DefaultHarvestPromptText = "Harvest";

	static readonly Color DepletedPortionColor = new Color( 0.82f, 0.82f, 0.83f );

	PlayerVitals _vitals;
	PlayerHandHarvest _handHarvest;
	PlayerGameMenuController _menuController;
	PlayerInventory _inventory;
	PlayerInventoryInteraction _inventoryInteraction;
	PlayerCrafting _crafting;
	InventoryMenuInputOverlay _menuInputOverlay;
	MenuPageNavigator _pageNavigator;
	ScreenPanel _hudScreen;
	bool _deferScreenPanelCamera;
	bool _built;

	Label _healthText;
	Panel _healthRoot;
	Panel _healthFill;
	Label _staminaText;
	Panel _staminaRoot;
	Panel _staminaFill;

	Panel _promptRoot;
	bool _promptWasVisible;

	Panel _menuRoot;
	Panel _menuLeftRoot;
	Panel _menuRightRoot;
	Panel _menuSkillsCenterRoot;
	Panel _menuSkillsDetailRoot;
	Panel _menuMapRoot;
	Panel _leftMenuColumn;
	Panel _rightMenuColumn;
	readonly List<IPlayerMenuSection> _sections = new();
	CraftingMenuSection _craftingSection;
	QuestMenuSection _questsSection;
	SkillsMenuSection _skillsSection;
	MapMenuSection _mapSection;
	GameSettingsMenuSection _settingsSection;
	PickupNotificationHud _pickupNotifications;

	protected override void OnTreeFirstBuilt()
	{
		base.OnTreeFirstBuilt();
		TryBuildHud();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !_built )
			TryBuildHud();

		if ( _menuController is not null && _menuController.IsMenuOpen )
		{
			for ( var i = 0; i < _sections.Count; i++ )
				_sections[i].TickMenu( true );

			_inventoryInteraction?.PollInventoryInput( _menuController.VisiblePanels );
		}

		_pickupNotifications?.Tick();

		if ( !_deferScreenPanelCamera || _hudScreen is null || !_hudScreen.IsValid() )
			return;

		if ( TryBindScreenPanelCamera( _hudScreen ) )
		{
			_hudScreen.Enabled = true;
			_deferScreenPanelCamera = false;
		}
	}

	protected override void OnDestroy()
	{
		if ( _vitals is not null )
			_vitals.OnVitalsChanged -= RefreshVitals;
		if ( _handHarvest is not null )
			_handHarvest.FocusedNodeChanged -= OnHarvestFocusChanged;
		if ( _inventory is not null )
		{
			_inventory.InventoryChanged -= OnInventoryChanged;
			_inventory.ResourcePickedUp -= OnResourcePickedUp;
		}
		if ( _menuController is not null )
		{
			_menuController.MenuOpenChanged -= OnMenuOpenChanged;
			_menuController.MenuLayoutChanged -= OnMenuLayoutChanged;
		}
		base.OnDestroy();
	}

	void TryBuildHud()
	{
		if ( _built )
			return;

		_vitals = FindOnAncestors<PlayerVitals>();
		if ( _vitals is null || !_vitals.IsLocalInputOwnedPawn() )
		{
			if ( _vitals is null )
				Log.Warning( $"[PlayerScreenHud] {GameObject.Name}: no PlayerVitals — HUD hidden." );
			Panel.Style.Set( "display", "none" );
			_built = true;
			return;
		}

		var screen = Components.Get<ScreenPanel>();
		_hudScreen = screen;
		if ( screen is not null )
			screen.Enabled = false;

		Panel.Style.Set( "position", "absolute" );
		Panel.Style.Set( "left", "0" );
		Panel.Style.Set( "top", "0" );
		Panel.Style.Set( "width", "100%" );
		Panel.Style.Set( "height", "100%" );
		Panel.Style.Set( "pointer-events", "none" );

		_inventory = FindOnAncestors<PlayerInventory>();

		BuildVitals( Panel );
		BuildHarvestPrompt( Panel );
		BuildPickupNotifications( Panel );
		BuildGameMenu( Panel );

		if ( screen is not null )
		{
			if ( TryBindScreenPanelCamera( screen ) )
			{
				screen.Enabled = true;
				_deferScreenPanelCamera = false;
			}
			else
			{
				_deferScreenPanelCamera = true;
				Log.Warning( $"[PlayerScreenHud] {GameObject.Name}: ScreenPanel.TargetCamera not bound yet — will retry." );
			}
		}

		_vitals.OnVitalsChanged += RefreshVitals;
		RefreshVitals();

		_built = true;
	}

	void BuildVitals( Panel root )
	{
		var vitalsHost = new Panel { Parent = root };
		vitalsHost.Style.Set( "position", "absolute" );
		vitalsHost.Style.Set( "left", "16px" );
		vitalsHost.Style.Set( "bottom", "16px" );
		vitalsHost.Style.Set( "pointer-events", "none" );

		var hostHeight = BarHeight * 2f + BarGap;
		var barsHost = new Panel { Parent = vitalsHost };
		barsHost.Style.Width = Length.Pixels( BarWidth );
		barsHost.Style.Height = Length.Pixels( hostHeight );
		barsHost.Style.Set( "position", "relative" );

		_healthRoot = new Panel { Parent = barsHost };
		_healthRoot.Style.Set( "position", "absolute" );
		_healthRoot.Style.Set( "left", "0" );
		_healthRoot.Style.Set( "top", "0" );
		_healthRoot.Style.Width = Length.Pixels( BarWidth );
		_healthRoot.Style.Height = Length.Pixels( BarHeight );
		_healthRoot.Style.BackgroundColor = DepletedPortionColor;
		_healthRoot.Style.Set( "overflow", "hidden" );

		_healthFill = new Panel { Parent = _healthRoot };
		_healthFill.Style.Set( "position", "absolute" );
		_healthFill.Style.Set( "top", "0" );
		_healthFill.Style.Set( "left", "0" );
		_healthFill.Style.Set( "height", "100%" );
		_healthFill.Style.Set( "z-index", "0" );
		_healthFill.Style.BackgroundColor = new Color( 0.92f, 0.18f, 0.14f );

		_healthText = new Label { Parent = _healthRoot };
		_healthText.Style.Set( "position", "absolute" );
		_healthText.Style.Set( "width", "100%" );
		_healthText.Style.Set( "height", "100%" );
		_healthText.Style.Set( "align-items", "center" );
		_healthText.Style.Set( "justify-content", "center" );
		_healthText.Style.Set( "z-index", "1" );
		_healthText.Style.FontColor = Color.Black;
		_healthText.Style.FontSize = Length.Pixels( 14f );

		_staminaRoot = new Panel { Parent = barsHost };
		_staminaRoot.Style.Set( "position", "absolute" );
		_staminaRoot.Style.Set( "left", "0" );
		_staminaRoot.Style.Set( "top", $"{BarHeight + BarGap}px" );
		_staminaRoot.Style.Width = Length.Pixels( BarWidth );
		_staminaRoot.Style.Height = Length.Pixels( BarHeight );
		_staminaRoot.Style.BackgroundColor = DepletedPortionColor;
		_staminaRoot.Style.Set( "overflow", "hidden" );

		_staminaFill = new Panel { Parent = _staminaRoot };
		_staminaFill.Style.Set( "position", "absolute" );
		_staminaFill.Style.Set( "top", "0" );
		_staminaFill.Style.Set( "left", "0" );
		_staminaFill.Style.Set( "height", "100%" );
		_staminaFill.Style.Set( "z-index", "0" );
		_staminaFill.Style.BackgroundColor = new Color( 0.98f, 0.86f, 0.2f );

		_staminaText = new Label { Parent = _staminaRoot };
		_staminaText.Style.Set( "position", "absolute" );
		_staminaText.Style.Set( "width", "100%" );
		_staminaText.Style.Set( "height", "100%" );
		_staminaText.Style.Set( "align-items", "center" );
		_staminaText.Style.Set( "justify-content", "center" );
		_staminaText.Style.Set( "z-index", "1" );
		_staminaText.Style.FontColor = Color.Black;
		_staminaText.Style.FontSize = Length.Pixels( 14f );
	}

	void BuildHarvestPrompt( Panel root )
	{
		_handHarvest = FindOnAncestors<PlayerHandHarvest>();
		if ( _handHarvest is null )
		{
			Log.Warning( $"[PlayerScreenHud] {GameObject.Name}: no PlayerHandHarvest — harvest prompt skipped." );
			return;
		}

		var promptHost = new Panel { Parent = root };
		promptHost.Style.Set( "position", "absolute" );
		promptHost.Style.Set( "left", "50%" );
		promptHost.Style.Set( "top", "58%" );
		promptHost.Style.Set( "transform", "translate(-50%, -50%)" );
		promptHost.Style.Set( "pointer-events", "none" );

		_promptRoot = new Panel { Parent = promptHost };
		_promptRoot.Style.Set( "flex-direction", "row" );
		_promptRoot.Style.Set( "align-items", "center" );
		_promptRoot.Style.Set( "justify-content", "center" );
		_promptRoot.Style.Set( "gap", "10px" );
		_promptRoot.Style.PaddingLeft = Length.Pixels( 14f );
		_promptRoot.Style.PaddingRight = Length.Pixels( 14f );
		_promptRoot.Style.PaddingTop = Length.Pixels( 8f );
		_promptRoot.Style.PaddingBottom = Length.Pixels( 8f );
		_promptRoot.Style.BackgroundColor = new Color( 0.06f, 0.06f, 0.07f, 0.82f );
		_promptRoot.Style.Set( "border-radius", "6px" );
		_promptRoot.Style.Set( "display", "none" );

		var keyCap = new Panel { Parent = _promptRoot };
		keyCap.Style.MinWidth = Length.Pixels( 28f );
		keyCap.Style.Height = Length.Pixels( 28f );
		keyCap.Style.Set( "align-items", "center" );
		keyCap.Style.Set( "justify-content", "center" );
		keyCap.Style.BackgroundColor = new Color( 0.92f, 0.92f, 0.94f );
		keyCap.Style.Set( "border-radius", "4px" );

		var keyLabel = new Label { Parent = keyCap, Text = "F" };
		keyLabel.Style.FontColor = Color.Black;
		keyLabel.Style.FontSize = Length.Pixels( 15f );

		var promptLabel = new Label { Parent = _promptRoot, Text = DefaultHarvestPromptText };
		promptLabel.Style.FontColor = Color.White;
		promptLabel.Style.FontSize = Length.Pixels( 18f );

		_handHarvest.FocusedNodeChanged += OnHarvestFocusChanged;
		OnHarvestFocusChanged();
	}

	void BuildPickupNotifications( Panel root )
	{
		if ( _inventory is null )
		{
			Log.Warning( $"[PlayerScreenHud] {GameObject.Name}: no PlayerInventory — pickup toasts skipped." );
			return;
		}

		_pickupNotifications = new PickupNotificationHud();
		_pickupNotifications.Build( root );
		_inventory.ResourcePickedUp += OnResourcePickedUp;
	}

	void OnResourcePickedUp( ResourcePickupNotice notice ) => _pickupNotifications?.Enqueue( notice );

	void BuildGameMenu( Panel root )
	{
		_menuController = FindOnAncestors<PlayerGameMenuController>();
		_inventory = FindOnAncestors<PlayerInventory>();
		_inventoryInteraction = FindOnAncestors<PlayerInventoryInteraction>();
		_crafting = FindOnAncestors<PlayerCrafting>();
		if ( _menuController is null || _inventory is null )
		{
			Log.Warning( $"[PlayerScreenHud] {GameObject.Name}: missing menu controller or inventory — menu skipped." );
			return;
		}

		if ( _inventoryInteraction is null )
			Log.Warning( $"[PlayerScreenHud] {GameObject.Name}: no PlayerInventoryInteraction — inventory clicks disabled." );

		if ( _crafting is null )
			Log.Warning( $"[PlayerScreenHud] {GameObject.Name}: no PlayerCrafting — craft button disabled." );

		_inventoryInteraction?.BindMenu( _menuController );

		_menuInputOverlay = new InventoryMenuInputOverlay { Parent = Panel };
		_menuInputOverlay.BindMenuController( _menuController );
		_menuInputOverlay.BindInventoryInteraction( _inventoryInteraction );
		_menuInputOverlay.ButtonInput = PanelInputType.UI;
		_menuInputOverlay.Style.Set( "position", "absolute" );
		_menuInputOverlay.Style.Set( "left", "0" );
		_menuInputOverlay.Style.Set( "top", "0" );
		_menuInputOverlay.Style.Set( "width", "100%" );
		_menuInputOverlay.Style.Set( "height", "100%" );
		_menuInputOverlay.SetOpen( false );

		_pageNavigator = new MenuPageNavigator( _menuController );
		_pageNavigator.Build( _menuInputOverlay );

		_menuRoot = new Panel { Parent = _menuInputOverlay };
		_menuRoot.Style.Set( "position", "absolute" );
		_menuRoot.Style.Set( "left", "0" );
		_menuRoot.Style.Set( "top", "0" );
		_menuRoot.Style.Set( "right", "0" );
		_menuRoot.Style.Set( "bottom", "0" );
		_menuRoot.Style.Set( "pointer-events", "none" );
		_menuRoot.Style.Set( "display", "none" );

		_menuMapRoot = CreateMapCenterAnchor( _menuRoot );
		_menuSkillsCenterRoot = CreateSkillsCenterAnchor( _menuRoot );
		_menuSkillsDetailRoot = CreateSkillsDetailAnchor( _menuRoot );
		_menuLeftRoot = CreateMenuSideAnchor( _menuRoot, alignLeft: true );
		_menuRightRoot = CreateMenuSideAnchor( _menuRoot, alignLeft: false );

		_leftMenuColumn = CreateMenuColumn( _menuLeftRoot, CraftingMenuColumnWidth );
		_rightMenuColumn = CreateMenuColumn( _menuRightRoot, InventoryMenuColumnWidth );

		_skillsSection = new SkillsMenuSection( _menuSkillsDetailRoot );
		_sections.Add( _skillsSection );
		_skillsSection.Build( _menuSkillsCenterRoot );

		_mapSection = new MapMenuSection();
		_sections.Add( _mapSection );
		_mapSection.Build( _menuMapRoot );

		_settingsSection = new GameSettingsMenuSection();
		_sections.Add( _settingsSection );
		_settingsSection.Build( _menuMapRoot );

		_craftingSection = new CraftingMenuSection( _inventory, _crafting );
		_sections.Add( _craftingSection );
		_craftingSection.Build( _leftMenuColumn );

		_questsSection = new QuestMenuSection();
		_sections.Add( _questsSection );
		_questsSection.Build( _leftMenuColumn );

		var inventorySection = new InventoryMenuSection( _inventory, _inventoryInteraction );
		_sections.Add( inventorySection );
		inventorySection.Build( _rightMenuColumn );

		_inventoryInteraction?.BindDragLayer( _rightMenuColumn );

		_inventory.InventoryChanged += OnInventoryChanged;
		_menuController.MenuOpenChanged += OnMenuOpenChanged;
		_menuController.MenuLayoutChanged += OnMenuLayoutChanged;
		ApplyMenuOpenState( _menuController.IsMenuOpen );
	}

	static Panel CreateMapCenterAnchor( Panel parent )
	{
		var anchor = new Panel { Parent = parent };
		anchor.Style.Set( "position", "absolute" );
		anchor.Style.Set( "left", "6%" );
		anchor.Style.Set( "right", "6%" );
		anchor.Style.Set( "top", "8%" );
		anchor.Style.Set( "bottom", "6%" );
		anchor.Style.Set( "display", "none" );
		anchor.Style.Set( "flex-direction", "column" );
		anchor.Style.Set( "align-items", "stretch" );
		anchor.Style.Set( "justify-content", "center" );
		anchor.Style.Set( "pointer-events", "auto" );
		anchor.Style.Set( "z-index", "2" );
		return anchor;
	}

	static Panel CreateSkillsCenterAnchor( Panel parent )
	{
		var anchor = new Panel { Parent = parent };
		anchor.Style.Set( "position", "absolute" );
		anchor.Style.Set( "left", "7%" );
		anchor.Style.Set( "right", "26%" );
		anchor.Style.Set( "top", "9%" );
		anchor.Style.Set( "bottom", "7%" );
		anchor.Style.Set( "display", "none" );
		anchor.Style.Set( "flex-direction", "column" );
		anchor.Style.Set( "align-items", "stretch" );
		anchor.Style.Set( "justify-content", "center" );
		anchor.Style.Set( "pointer-events", "auto" );
		anchor.Style.Set( "z-index", "2" );
		return anchor;
	}

	static Panel CreateSkillsDetailAnchor( Panel parent )
	{
		var anchor = new Panel { Parent = parent };
		anchor.Style.Set( "position", "absolute" );
		anchor.Style.Set( "right", "2%" );
		anchor.Style.Set( "top", "9%" );
		anchor.Style.Set( "bottom", "7%" );
		anchor.Style.Set( "width", "22%" );
		anchor.Style.Set( "display", "none" );
		anchor.Style.Set( "flex-direction", "column" );
		anchor.Style.Set( "pointer-events", "auto" );
		anchor.Style.Set( "z-index", "2" );
		return anchor;
	}

	static Panel CreateMenuSideAnchor( Panel parent, bool alignLeft )
	{
		var anchor = new Panel { Parent = parent };
		anchor.Style.Set( "position", "absolute" );
		anchor.Style.Set( "top", "0" );
		anchor.Style.Set( "bottom", "0" );
		anchor.Style.Set( "width", "33%" );
		anchor.Style.Set( "display", "none" );
		anchor.Style.Set( "flex-direction", "column" );
		anchor.Style.Set( "align-items", "center" );
		anchor.Style.Set( "justify-content", "center" );
		anchor.Style.Set( "pointer-events", "auto" );

		if ( alignLeft )
		{
			anchor.Style.Set( "left", "0" );
			anchor.Style.Set( "width", "38%" );
			anchor.Style.Set( "align-items", "flex-start" );
			anchor.Style.Set( "justify-content", "center" );
			anchor.Style.PaddingLeft = Length.Pixels( 8f );
			anchor.Style.PaddingRight = Length.Pixels( 10f );
		}
		else
		{
			anchor.Style.Set( "right", "0" );
			anchor.Style.PaddingRight = Length.Pixels( 20f );
			anchor.Style.PaddingLeft = Length.Pixels( 12f );
		}

		return anchor;
	}

	static Panel CreateMenuColumn( Panel parent, float widthPixels )
	{
		var pad = widthPixels > 400f ? 20f : 16f;
		var gap = widthPixels > 400f ? 16f : 14f;

		var column = new Panel { Parent = parent };
		column.Style.Set( "position", "relative" );
		column.Style.Width = Length.Pixels( widthPixels );
		column.Style.Set( "flex-direction", "column" );
		column.Style.Set( "gap", $"{gap}px" );
		column.Style.Set( "flex-shrink", "0" );
		column.Style.Set( "pointer-events", "auto" );
		column.Style.PaddingTop = Length.Pixels( pad );
		column.Style.PaddingBottom = Length.Pixels( pad );
		column.Style.PaddingLeft = Length.Pixels( pad );
		column.Style.PaddingRight = Length.Pixels( pad );
		column.Style.BackgroundColor = new Color( 0.05f, 0.06f, 0.08f, 0.88f );
		column.Style.Set( "border-radius", "8px" );
		column.Style.Set( "border-width", "1px" );
		column.Style.Set( "border-color", "#383d47" );
		return column;
	}

	void OnHarvestFocusChanged()
	{
		if ( _promptRoot is null )
			return;

		var show = _handHarvest is not null && _handHarvest.FocusedNode is not null;
		if ( show == _promptWasVisible )
			return;

		_promptWasVisible = show;
		_promptRoot.Style.Set( "display", show ? "flex" : "none" );
	}

	void OnInventoryChanged()
	{
		if ( _menuController is null || !_menuController.IsMenuOpen )
			return;

		RefreshAllSections();
	}

	void OnMenuOpenChanged( bool isOpen ) => ApplyMenuOpenState( isOpen );

	void OnMenuLayoutChanged() => ApplyMenuLayout();

	void ApplyMenuOpenState( bool isOpen )
	{
		if ( _menuRoot is null )
			return;

		Panel.Style.Set( "pointer-events", isOpen ? "auto" : "none" );

		_menuInputOverlay?.SetOpen( isOpen );
		_pageNavigator?.SetMenuOpen( isOpen );

		_menuRoot.Style.Set( "display", isOpen ? "flex" : "none" );
		_menuRoot.Style.Set( "pointer-events", isOpen ? "auto" : "none" );

		foreach ( var section in _sections )
			section.SetMenuOpen( isOpen );

		if ( isOpen )
		{
			ApplyMenuLayout();
			RefreshAllSections();
		}
		else
		{
			if ( _menuLeftRoot is not null )
				_menuLeftRoot.Style.Set( "display", "none" );
			if ( _menuRightRoot is not null )
				_menuRightRoot.Style.Set( "display", "none" );
			if ( _menuSkillsCenterRoot is not null )
				_menuSkillsCenterRoot.Style.Set( "display", "none" );
			if ( _menuSkillsDetailRoot is not null )
				_menuSkillsDetailRoot.Style.Set( "display", "none" );
			if ( _menuMapRoot is not null )
				_menuMapRoot.Style.Set( "display", "none" );
		}
	}

	void ApplyMenuLayout()
	{
		if ( _menuController is null )
			return;

		var panels = _menuController.VisiblePanels;
		var showMap = (panels & MenuPanelFlags.Map) != 0;
		var showSettings = (panels & MenuPanelFlags.Settings) != 0;
		var showFullscreen = showMap || showSettings;
		var showSkills = !showFullscreen && (panels & MenuPanelFlags.Skills) != 0;
		var showQuests = !showFullscreen && !showSkills && (panels & MenuPanelFlags.Quests) != 0;
		var showCrafting = !showFullscreen && !showSkills && !showQuests && (panels & MenuPanelFlags.Crafting) != 0;
		var showLeftColumn = showCrafting || showQuests;
		var showInventory = !showFullscreen && !showSkills && (panels & MenuPanelFlags.Inventory) != 0;

		if ( _menuMapRoot is not null )
			_menuMapRoot.Style.Set( "display", showFullscreen ? "flex" : "none" );

		if ( _menuSkillsCenterRoot is not null )
			_menuSkillsCenterRoot.Style.Set( "display", showSkills ? "flex" : "none" );

		if ( _menuSkillsDetailRoot is not null )
			_menuSkillsDetailRoot.Style.Set( "display", showSkills ? "flex" : "none" );

		if ( _menuLeftRoot is not null )
			_menuLeftRoot.Style.Set( "display", showLeftColumn ? "flex" : "none" );

		if ( _menuRightRoot is not null )
			_menuRightRoot.Style.Set( "display", showInventory ? "flex" : "none" );

		_craftingSection?.SetPanelVisible( showCrafting );
		_questsSection?.SetPanelVisible( showQuests );
		_skillsSection?.SetPanelVisible( showSkills );
		_mapSection?.SetPanelVisible( showMap );
		_settingsSection?.SetPanelVisible( showSettings );

		for ( var i = 0; i < _sections.Count; i++ )
		{
			if ( _sections[i].SectionId == "inventory" )
				_sections[i].SetPanelVisible( showInventory );
		}

		_pageNavigator?.RefreshHighlight();
	}

	void RefreshAllSections()
	{
		foreach ( var section in _sections )
			section.Refresh();
	}

	void OnMenuGlobalMouseUp()
	{
		for ( var i = 0; i < _sections.Count; i++ )
			_sections[i].OnMenuGlobalMouseUp();
	}

	void RefreshVitals()
	{
		if ( _vitals is null || _healthFill is null )
			return;

		var hMax = Math.Max( 1f, _vitals.CurrentHealthMax );
		var sMax = Math.Max( 1e-3f, _vitals.CurrentStaminaMax );
		var hFrac = Math.Clamp( _vitals.CurrentHealth / hMax, 0f, 1f );
		var sFrac = Math.Clamp( _vitals.CurrentStamina / sMax, 0f, 1f );

		_healthText.Text = $"{_vitals.CurrentHealth:0}/{_vitals.CurrentHealthMax:0}";
		_healthFill.Style.Width = Length.Pixels( BarWidth * hFrac );

		_staminaText.Text = $"{_vitals.CurrentStamina:0}/{_vitals.CurrentStaminaMax:0}";
		_staminaFill.Style.Width = Length.Pixels( BarWidth * sFrac );
	}

	T FindOnAncestors<T>() where T : Component
	{
		for ( var go = GameObject; go.IsValid(); go = go.Parent )
		{
			var c = go.Components.Get<T>();
			if ( c is not null )
				return c;
		}

		return null;
	}

	bool TryBindScreenPanelCamera( ScreenPanel screen )
	{
		if ( screen is null )
			return true;
		if ( !screen.IsValid() )
			return false;

		try
		{
			if ( TryResolveHudTargetCamera( GameObject, out var cam ) && cam.IsValid() )
			{
				screen.TargetCamera = cam;
				return true;
			}

			var scene = Scene;
			if ( scene is not null )
			{
				var sceneCam = scene.Camera;
				if ( sceneCam is not null && sceneCam.IsValid() )
				{
					screen.TargetCamera = sceneCam;
					return true;
				}
			}

			var existing = screen.TargetCamera;
			return existing is not null && existing.IsValid();
		}
		catch ( NullReferenceException )
		{
			return false;
		}
	}

	static bool TryResolveHudTargetCamera( GameObject from, out CameraComponent found )
	{
		found = default;
		if ( !from.IsValid() )
			return false;

		for ( var go = from; go.IsValid(); go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is null )
				continue;

			var embedded = pc.Components.Get<CameraComponent>();
			if ( embedded.IsValid() )
			{
				found = embedded;
				return true;
			}
		}

		for ( var go = from; go.IsValid(); go = go.Parent )
		{
			if ( TryFindFirstCameraInHierarchy( go, out found ) && found.IsValid() )
				return true;
		}

		return false;
	}

	static bool TryFindFirstCameraInHierarchy( GameObject go, out CameraComponent found )
	{
		found = default;
		if ( !go.IsValid() )
			return false;

		var self = go.Components.Get<CameraComponent>();
		if ( self.IsValid() )
		{
			found = self;
			return true;
		}

		foreach ( var ch in go.Children )
		{
			if ( TryFindFirstCameraInHierarchy( ch, out found ) )
				return true;
		}

		return false;
	}
}
