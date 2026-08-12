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
	const float CraftingMenuColumnWidth = 470f;
	const string DefaultHarvestPromptText = "Harvest";
	const int ZGameMenu = 2500;

	static readonly Color DepletedPortionColor = new Color( 0.82f, 0.82f, 0.83f );

	PlayerVitals _vitals;
	PlayerHandHarvest _handHarvest;
	PlayerEquipment _equipment;
	PlayerGameMenuController _menuController;
	PlayerInventory _inventory;
	PlayerHotbar _hotbar;
	PlayerInventoryInteraction _inventoryInteraction;
	HotbarHud _hotbarHud;
	TerrainMinimapHud _minimapHud;
	PlayerCrafting _crafting;
	InventoryMenuInputOverlay _menuInputOverlay;
	MenuPageNavigator _pageNavigator;
	BuildMenuHud _buildMenuHud;
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
	Label _promptKeyLabel;
	Label _promptLabel;
	bool _promptWasVisible;

	Panel _menuRoot;
	Panel _menuLeftRoot;
	Panel _menuRightRoot;
	Panel _menuSkillsCenterRoot;
	Panel _menuSkillsDetailRoot;
	Panel _menuMapRoot;
	Panel _menuAugmentRoot;
	Panel _leftMenuColumn;
	Panel _rightMenuColumn;
	readonly List<IPlayerMenuSection> _sections = new();
	CraftingMenuSection _craftingSection;
	QuestMenuSection _questsSection;
	SkillsMenuSection _skillsSection;
	MapMenuSection _mapSection;
	GameSettingsMenuSection _settingsSection;
	EquipmentPaperdollSection _equipmentSection;
	ContainerMenuSection _containerSection;
	AugmentStationMenuSection _augmentStationSection;
	PlayerAugments _augments;
	PickupNotificationHud _pickupNotifications;

	protected override void OnTreeFirstBuilt()
	{
		base.OnTreeFirstBuilt();
		TryBuildHud();
	}

	/// <summary>
	/// While crafting is open, any wheel over the HUD scrolls the recipe list.
	/// Prefer this over <see cref="Input.MouseWheel"/> — that signal is cleared when the cursor is visible.
	/// </summary>
	protected override void OnMouseWheel( Vector2 value )
	{
		if ( _menuController is not null && _menuController.IsMenuOpen
		     && string.Equals( _menuController.ActivePageId, MenuPageIds.Crafting, StringComparison.OrdinalIgnoreCase )
		     && _craftingSection is not null )
		{
			_craftingSection.ApplyRecipeListWheel( value );
			return;
		}

		base.OnMouseWheel( value );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !_built )
			TryBuildHud();

		if ( _menuController is not null && _menuController.IsMenuOpen )
		{
			_menuInputOverlay?.PollMenuPointer();
			for ( var i = 0; i < _sections.Count; i++ )
				_sections[i].TickMenu( true );
		}

		_pickupNotifications?.Tick();
		_minimapHud?.Tick();
		_buildMenuHud?.Tick();

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
		if ( _inventoryInteraction is not null )
		{
			_inventoryInteraction.ContainerChanged -= OnContainerChanged;
			_inventoryInteraction.FocusedContainerChanged -= OnInteractionPromptChanged;
			_inventoryInteraction.FocusedAugmentStationChanged -= OnInteractionPromptChanged;
			_inventoryInteraction.AugmentStationChanged -= OnAugmentStationChanged;
		}
		if ( _handHarvest is not null )
			_handHarvest.FocusedNodeChanged -= OnInteractionPromptChanged;
		if ( _equipment is not null )
			_equipment.EquipmentChanged -= OnEquipmentToolChanged;
		if ( _inventory is not null )
		{
			_inventory.InventoryChanged -= OnInventoryChanged;
			_inventory.ResourcePickedUp -= OnResourcePickedUp;
		}

		_hotbarHud?.Dispose();
		_buildMenuHud = null;
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
		_hotbar = FindOnAncestors<PlayerHotbar>();
		_equipment = FindOnAncestors<PlayerEquipment>();
		_augments = FindOnAncestors<PlayerAugments>();

		_inventoryInteraction = FindOnAncestors<PlayerInventoryInteraction>();
		_inventoryInteraction?.SetDragLayerRoot( Panel );
		if ( _equipment is not null && _inventory is not null )
			_inventoryInteraction?.RegisterGrid( new PlayerEquipmentPaperdollGridHost( _equipment, _inventory ) );

		BuildVitals( Panel );
		BuildHarvestPrompt( Panel );
		BuildPickupNotifications( Panel );
		BuildMinimap( Panel );
		BuildGameMenu( Panel );
		BuildHotbar( Panel );
		BuildBuildMenu( Panel );

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

		GameObject.Components.Create<NavMeshDebugDraw>();
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
		_equipment ??= FindOnAncestors<PlayerEquipment>();

		if ( _handHarvest is null )
		{
			Log.Warning( $"[PlayerScreenHud] {GameObject.Name}: no PlayerHandHarvest — interaction prompt skipped." );
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

		_promptKeyLabel = new Label { Parent = keyCap, Text = "F" };
		_promptKeyLabel.Style.FontColor = Color.Black;
		_promptKeyLabel.Style.FontSize = Length.Pixels( 15f );

		_promptLabel = new Label { Parent = _promptRoot, Text = DefaultHarvestPromptText };
		_promptLabel.Style.FontColor = Color.White;
		_promptLabel.Style.FontSize = Length.Pixels( 18f );

		_handHarvest?.FocusedNodeChanged += OnInteractionPromptChanged;
		if ( _inventoryInteraction is not null )
			_inventoryInteraction.FocusedContainerChanged += OnInteractionPromptChanged;
		if ( _equipment is not null )
			_equipment.EquipmentChanged += OnEquipmentToolChanged;
		OnInteractionPromptChanged();
	}

	void OnEquipmentToolChanged() => _equipmentSection?.Refresh();

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

	void BuildMinimap( Panel root )
	{
		_minimapHud = new TerrainMinimapHud();
		_minimapHud.Build( root );
		UpdateMinimapVisibility();
	}

	void BuildHotbar( Panel root )
	{
		_hotbar ??= FindOnAncestors<PlayerHotbar>();
		_inventoryInteraction ??= FindOnAncestors<PlayerInventoryInteraction>();

		if ( _hotbar is null )
		{
			Log.Warning( $"[PlayerScreenHud] {GameObject.Name}: no PlayerHotbar — hotbar HUD skipped." );
			return;
		}

		if ( _inventoryInteraction is null )
			Log.Warning( $"[PlayerScreenHud] {GameObject.Name}: no PlayerInventoryInteraction — hotbar drag disabled." );

		_hotbarHud = new HotbarHud();
		_hotbarHud.Build( root, _hotbar, _inventoryInteraction );
	}

	void BuildBuildMenu( Panel root )
	{
		_equipment ??= FindOnAncestors<PlayerEquipment>();
		if ( _equipment is null )
		{
			Log.Warning( $"[PlayerScreenHud] {GameObject.Name}: no PlayerEquipment — build menu skipped." );
			return;
		}

		_buildMenuHud = new BuildMenuHud( _equipment );
		_buildMenuHud.Build( root );
	}

	void BuildGameMenu( Panel root )
	{
		_menuController = FindOnAncestors<PlayerGameMenuController>();
		_inventory = FindOnAncestors<PlayerInventory>();
		_equipment ??= FindOnAncestors<PlayerEquipment>();
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
		_menuInputOverlay.BindMenuGlobalMouseUp( OnMenuGlobalMouseUp );
		_menuInputOverlay.ButtonInput = PanelInputType.UI;
		_menuInputOverlay.Style.Set( "position", "absolute" );
		_menuInputOverlay.Style.Set( "left", "0" );
		_menuInputOverlay.Style.Set( "top", "0" );
		_menuInputOverlay.Style.Set( "width", "100%" );
		_menuInputOverlay.Style.Set( "height", "100%" );
		_menuInputOverlay.Style.Set( "z-index", ZGameMenu.ToString() );
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
		_menuAugmentRoot = CreateMapCenterAnchor( _menuRoot );
		_menuSkillsCenterRoot = CreateSkillsCenterAnchor( _menuRoot );
		_menuSkillsDetailRoot = CreateSkillsDetailAnchor( _menuRoot );
		_menuLeftRoot = CreateMenuSideAnchor( _menuRoot, alignLeft: true );
		_menuRightRoot = CreateMenuSideAnchor( _menuRoot, alignLeft: false );

		_leftMenuColumn = CreateMenuColumn( _menuLeftRoot, InventoryMenuColumnWidth );
		_rightMenuColumn = CreateMenuColumn( _menuRightRoot, InventoryMenuColumnWidth );

		_equipmentSection = new EquipmentPaperdollSection( _equipment, _inventory, _inventoryInteraction );
		_sections.Add( _equipmentSection );
		_equipmentSection.Build( _leftMenuColumn );

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
		_menuInputOverlay.BindCraftingWheel( _craftingSection.ApplyRecipeListWheel );
		_menuInputOverlay.BindCraftingScrollbar( _craftingSection.TryHandleScrollbarPointer );
		_menuInputOverlay.BindCraftingRecipeSelect( OnMenuRecipeSelectAtScreen );
		_menuInputOverlay.BindCraftingCraftPointer( OnMenuCraftPointerAtScreen );
		_menuInputOverlay.BindTabSelect( _pageNavigator.TrySelectTabAtScreen );
		_menuInputOverlay.BindPageContentSelect( TryMenuPageContentAtScreen );
		_menuController.MenuMouseWheelSink = OnMenuMouseWheel;

		_augmentStationSection = new AugmentStationMenuSection( _augments, _inventory, _inventoryInteraction );
		_sections.Add( _augmentStationSection );
		_augmentStationSection.Build( _menuAugmentRoot );

		_questsSection = new QuestMenuSection();
		_sections.Add( _questsSection );
		_questsSection.Build( _leftMenuColumn );

		_containerSection = new ContainerMenuSection( _inventoryInteraction );
		_sections.Add( _containerSection );
		_containerSection.Build( _leftMenuColumn );
		if ( _inventoryInteraction is not null )
		{
			_inventoryInteraction.ContainerChanged += OnContainerChanged;
			_inventoryInteraction.FocusedContainerChanged += OnInteractionPromptChanged;
			_inventoryInteraction.FocusedAugmentStationChanged += OnInteractionPromptChanged;
			_inventoryInteraction.AugmentStationChanged += OnAugmentStationChanged;
		}

		var inventorySection = new InventoryMenuSection( _inventory, _inventoryInteraction );
		_sections.Add( inventorySection );
		inventorySection.Build( _rightMenuColumn );

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

	void OnInteractionPromptChanged()
	{
		if ( _promptRoot is null )
			return;

		// Container / augment station under the crosshair wins over harvest.
		var focusedContainer = _inventoryInteraction?.FocusedContainer;
		var focusedStation = _inventoryInteraction?.FocusedAugmentStation;
		var showOpen = (focusedContainer is not null && focusedContainer.IsValid())
		               || (focusedStation is not null && focusedStation.IsValid());
		var showHarvest = !showOpen && _handHarvest?.FocusedNode is not null;
		var show = showOpen || showHarvest;

		if ( _promptKeyLabel is not null )
			_promptKeyLabel.Text = showOpen ? "E" : "F";

		if ( _promptLabel is not null )
		{
			if ( focusedContainer is not null && focusedContainer.IsValid() )
			{
				_promptLabel.Text = string.IsNullOrWhiteSpace( focusedContainer.DisplayName )
					? "Open"
					: $"Open {focusedContainer.DisplayName}";
			}
			else if ( focusedStation is not null && focusedStation.IsValid() )
			{
				_promptLabel.Text = string.IsNullOrWhiteSpace( focusedStation.DisplayName )
					? "Open Augment Station"
					: $"Open {focusedStation.DisplayName}";
			}
			else
			{
				_promptLabel.Text = DefaultHarvestPromptText;
			}
		}

		if ( show == _promptWasVisible )
			return;

		_promptWasVisible = show;
		_promptRoot.Style.Set( "display", show ? "flex" : "none" );
	}

	void OnInventoryChanged()
	{
		if ( _menuController is null || !_menuController.IsMenuOpen )
			return;

		RefreshInventoryDependentSections();
	}

	void RefreshInventoryDependentSections()
	{
		for ( var i = 0; i < _sections.Count; i++ )
		{
			var id = _sections[i].SectionId;
			if ( id is "inventory" or "crafting" or "equipment" or "augment_station" )
				_sections[i].Refresh();
		}
	}

	void OnMenuOpenChanged( bool isOpen ) => ApplyMenuOpenState( isOpen );

	void OnMenuLayoutChanged() => ApplyMenuLayout();

	void OnContainerChanged()
	{
		_containerSection?.BindContainer( _inventoryInteraction?.OpenContainer );

		if ( _menuController is not null && _menuController.IsMenuOpen )
			ApplyMenuLayout();
	}

	void OnAugmentStationChanged()
	{
		if ( _menuController is not null && _menuController.IsMenuOpen )
			ApplyMenuLayout();

		_augmentStationSection?.Refresh();
	}

	bool OnMenuRecipeSelectAtScreen( Vector2 screenPos )
	{
		if ( _menuController is not null
		     && string.Equals( _menuController.ActivePageId, MenuPageIds.AugmentStation, StringComparison.OrdinalIgnoreCase )
		     && _augmentStationSection is not null
		     && _augmentStationSection.TrySelectRecipeAtScreen( screenPos ) )
			return true;

		return _craftingSection is not null && _craftingSection.TrySelectRecipeAtScreen( screenPos );
	}

	bool OnMenuCraftPointerAtScreen( Vector2 screenPos, bool pressed )
	{
		if ( _menuController is not null
		     && string.Equals( _menuController.ActivePageId, MenuPageIds.AugmentStation, StringComparison.OrdinalIgnoreCase )
		     && _augmentStationSection is not null
		     && _augmentStationSection.TryCraftPointerAtScreen( screenPos, pressed ) )
			return true;

		return _craftingSection is not null && _craftingSection.TryCraftPointerAtScreen( screenPos, pressed );
	}

	void ApplyMenuOpenState( bool isOpen )
	{
		if ( _menuRoot is null )
			return;

		Panel.Style.Set( "pointer-events", isOpen ? "auto" : "none" );

		_menuInputOverlay?.SetOpen( isOpen );
		_pageNavigator?.SetMenuOpen( isOpen );

		_menuRoot.Style.Set( "display", isOpen ? "flex" : "none" );
		// Keep none so empty areas don't eat wheel/clicks; interactive children still use pointer-events: auto.
		_menuRoot.Style.Set( "pointer-events", "none" );

		foreach ( var section in _sections )
			section.SetMenuOpen( isOpen );

		UpdateHotbarVisibility();
		UpdateMinimapVisibility();

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
			if ( _menuAugmentRoot is not null )
				_menuAugmentRoot.Style.Set( "display", "none" );
		}
	}

	void ApplyMenuLayout()
	{
		if ( _menuController is null )
			return;

		var panels = _menuController.VisiblePanels;
		var showMap = (panels & MenuPanelFlags.Map) != 0;
		var showSettings = (panels & MenuPanelFlags.Settings) != 0;
		var showAugmentStation = (panels & MenuPanelFlags.AugmentStation) != 0;
		var showFullscreen = showMap || showSettings || showAugmentStation;
		var showSkills = !showFullscreen && (panels & MenuPanelFlags.Skills) != 0;
		var showQuests = !showFullscreen && !showSkills && (panels & MenuPanelFlags.Quests) != 0;
		var showCrafting = !showFullscreen && !showSkills && !showQuests && (panels & MenuPanelFlags.Crafting) != 0;
		var showInventory = !showFullscreen && !showSkills && (panels & MenuPanelFlags.Inventory) != 0;
		var containerOpen = _inventoryInteraction?.OpenContainer is not null;
		var showContainer = showInventory && !showCrafting && !showQuests && containerOpen;
		var showPaperdoll = showInventory && !showCrafting && !showQuests && !containerOpen;
		var showLeftColumn = showCrafting || showQuests || showPaperdoll || showContainer;

		if ( _menuMapRoot is not null )
			_menuMapRoot.Style.Set( "display", (showMap || showSettings) ? "flex" : "none" );

		if ( _menuAugmentRoot is not null )
			_menuAugmentRoot.Style.Set( "display", showAugmentStation ? "flex" : "none" );

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
		_equipmentSection?.SetPanelVisible( showPaperdoll );
		_containerSection?.SetPanelVisible( showContainer );
		_skillsSection?.SetPanelVisible( showSkills );
		_mapSection?.SetPanelVisible( showMap );
		_settingsSection?.SetPanelVisible( showSettings );
		_augmentStationSection?.SetPanelVisible( showAugmentStation );

		for ( var i = 0; i < _sections.Count; i++ )
		{
			if ( _sections[i].SectionId == "inventory" )
				_sections[i].SetPanelVisible( showInventory );
		}

		_pageNavigator?.RefreshHighlight();
		UpdateHotbarVisibility();
		UpdateMinimapVisibility();
	}

	void UpdateHotbarVisibility()
	{
		if ( _hotbarHud is null )
			return;

		if ( _menuController is null || !_menuController.IsMenuOpen )
		{
			_hotbarHud.SetVisible( true );
			return;
		}

		var panels = _menuController.VisiblePanels;
		var hideForFullscreen = (panels & MenuPanelFlags.Map) != 0
		                        || (panels & MenuPanelFlags.Settings) != 0
		                        || (panels & MenuPanelFlags.AugmentStation) != 0;
		_hotbarHud.SetVisible( !hideForFullscreen );
	}

	void UpdateMinimapVisibility()
	{
		if ( _minimapHud is null )
			return;

		if ( _menuController is null || !_menuController.IsMenuOpen )
		{
			_minimapHud.SetVisible( true );
			return;
		}

		var panels = _menuController.VisiblePanels;
		var hideForFullscreen = (panels & MenuPanelFlags.Map) != 0
		                        || (panels & MenuPanelFlags.Settings) != 0
		                        || (panels & MenuPanelFlags.AugmentStation) != 0;
		_minimapHud.SetVisible( !hideForFullscreen );
	}

	void RefreshAllSections()
	{
		foreach ( var section in _sections )
			section.Refresh();
	}

	void OnMenuMouseWheel( Vector2 wheel )
	{
		if ( _menuController is null || _craftingSection is null )
			return;

		if ( !string.Equals( _menuController.ActivePageId, MenuPageIds.Crafting, StringComparison.OrdinalIgnoreCase ) )
			return;

		_craftingSection.ApplyRecipeListWheel( wheel );
	}

	/// <summary>Soft-cursor Attack1 for skills / quests / settings (crafting has its own binds).</summary>
	bool TryMenuPageContentAtScreen( Vector2 screenPos )
	{
		if ( _menuController is null )
			return false;

		var page = _menuController.ActivePageId;
		if ( string.Equals( page, MenuPageIds.Skills, StringComparison.OrdinalIgnoreCase ) )
			return _skillsSection?.TrySelectNodeAtScreen( screenPos ) ?? false;

		if ( string.Equals( page, MenuPageIds.Quests, StringComparison.OrdinalIgnoreCase ) )
			return _questsSection?.TrySelectQuestAtScreen( screenPos ) ?? false;

		if ( string.Equals( page, MenuPageIds.Settings, StringComparison.OrdinalIgnoreCase ) )
			return _settingsSection?.TryInvokeAtScreen( screenPos ) ?? false;

		return false;
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
