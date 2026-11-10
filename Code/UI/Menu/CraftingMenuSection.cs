using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Left-side crafting panel: recipe preview, list, and craft action. At a workbench the header
/// grows a Craft / Upgrade tab pair (Craft selected on every open): the Upgrade tab lists the
/// player's upgradable gear (hotbar + bag), shows its tier numeral and the stats that grow one
/// tier up, and the action button raises the tier through <see cref="PlayerCrafting.OwnerTryUpgradeEquipment"/>.
/// </summary>
public sealed class CraftingMenuSection : IPlayerMenuSection
{
	enum WorkbenchTab { Craft, Upgrade }

	public const float WidthScale = 1.5f;
	public const float LengthScale = 2f;
	public const float TextScale = 1.4f;
	public const float LayoutScale = 1.25f;
	/// <summary>Recipe list row size vs the original oversized rows.</summary>
	public const float RecipeListItemScale = 0.5f;

	public const float RowIconSize = 48f * WidthScale * RecipeListItemScale;
	public const float DetailIconSize = 64f * WidthScale;
	public const float MinSectionHeight = 420f * LengthScale;
	/// <summary>
	/// The section fills the menu side anchor: the viewport minus the bottom HUD strip the vitals keep
	/// (<see cref="PlayerScreenHud.MenuColumnBottomInset"/>) and the column's own padding + border, as a
	/// vh value so a window resize re-lays it out; <see cref="MinSectionHeight"/> is the floor on short windows.
	/// </summary>
	public static float SectionViewHeightPercent
		=> (PlayerScreenHud.DesignScreenHeight - PlayerScreenHud.MenuColumnTopInset - PlayerScreenHud.MenuColumnBottomInset - PlayerScreenHud.MenuColumnChromeHeight)
		   / PlayerScreenHud.DesignScreenHeight * 100f;
	/// <summary>Shortest the recipe list gets; it grows with the section on taller windows.</summary>
	public const float RecipeListMinHeight = 220f * LengthScale;
	public const float DetailAreaMaxHeight = MinSectionHeight - RecipeListMinHeight - 80f * LayoutScale;
	public const float RecipeRowGap = 3f;
	/// <summary>
	/// Exact Style.Height for each recipe row (border-box). Scroll range uses this — keep in sync.
	/// </summary>
	public const float RecipeRowHeight = 44f;
	public const float RecipeRowBorder = 1f;
	/// <summary>Readable list names (not scaled as hard as the 50% row shrink).</summary>
	public const float RecipeListRowFontSize = 14f * TextScale * 0.85f;
	/// <summary>One physical mouse-wheel notch reveals this many recipe rows.</summary>
	public const float WheelItemsPerNotch = 4.5f;

	public const float CraftingTitleFontSize = 24f * TextScale;
	public const float ItemNameFontSize = 16f * TextScale;
	public const float ItemDescriptionFontSize = 12f * TextScale;
	public const float SectionHeaderFontSize = 17f * TextScale;
	public const float SectionEntryFontSize = 14f * TextScale;

	public string SectionId => "crafting";

	readonly PlayerInventory _inventory;
	readonly PlayerCrafting _crafting;
	readonly List<RowUi> _rows = new();

	Panel _sectionRoot;
	Panel _craftButton;
	Label _craftButtonLabel;
	Panel _detailIcon;
	Label _detailName;
	Label _detailDescription;
	Panel _requirementsEntries;
	Panel _statsEntries;
	Panel _craftRequirementsBlock;
	Panel _craftStatsBlock;
	Panel _upgradeStatsBlock;
	Panel _upgradeCostBlock;
	Panel _upgradeStatsEntries;
	Panel _upgradeCostEntries;
	Label _detailTier;
	string _detailIconPathApplied;
	Panel _recipeList;
	CraftingRecipeListPanel _recipeListPanel;

	WorkbenchTab _activeTab = WorkbenchTab.Craft;
	Panel _tabRow;
	Panel _craftTabButton;
	Panel _upgradeTabButton;
	bool _wasWorkbenchOpen;
	readonly List<UpgradeEntry> _upgradeEntries = new();
	readonly List<UpgradeEntry> _upgradeScratch = new();
	string _selectedUpgradeKey;
	int _builtUpgradeSignature;
	double _nextUpgradeListCheckAt;

	string _selectedRecipeId;
	bool _menuOpen;
	bool _panelVisible = true;
	bool _craftButtonPressedVisual;
	int _builtRecipeContentVersion = -1;
	int _builtNearStations;
	bool _builtWorkbenchOpen;

	PlayerInventoryInteraction _interaction;
	Label _titleLabel;
	Panel _repairButton;
	Label _repairButtonLabel;
	bool _repairAvailable;
	double _nextRepairStateRefreshAt;
	/// <summary>Last "can craft / upgrade" state shown on the action button, so a press keeps its disabled look.</summary>
	bool _craftButtonEnabled;

	/// <summary>True when the crafting page is open and this panel is shown — recipe list owns mouse wheel.</summary>
	public bool IsScrollTargetActive => _menuOpen && _panelVisible;

	public CraftingMenuSection( PlayerInventory inventory, PlayerCrafting crafting )
	{
		_inventory = inventory;
		_crafting = crafting;
	}

	public void Build( Panel menuColumn )
	{
		CraftingRecipeCatalog.EnsureLoaded();

		_sectionRoot = new Panel { Parent = menuColumn };
		_sectionRoot.Style.Set( "position", "relative" );
		_sectionRoot.Style.Set( "z-index", "1" );
		_sectionRoot.Style.Set( "pointer-events", "auto" );
		_sectionRoot.Style.Set( "flex-direction", "column" );
		_sectionRoot.Style.Set( "align-items", "stretch" );
		_sectionRoot.Style.Set( "gap", $"{10f * LayoutScale}px" );
		_sectionRoot.Style.Width = Length.Percent( 100 );
		// Viewport-relative, not a build-time pixel box: resizing the game window (windowed → full)
		// re-lays this out and the list below grows into the extra height.
		_sectionRoot.Style.Height = Length.ViewHeight( SectionViewHeightPercent );
		_sectionRoot.Style.MinHeight = Length.Pixels( MinSectionHeight );
		_sectionRoot.Style.Set( "overflow", "hidden" );
		_sectionRoot.Style.Set( "flex-shrink", "0" );

		var header = new Panel { Parent = _sectionRoot };
		header.Style.Set( "flex-direction", "row" );
		header.Style.Set( "align-items", "center" );
		header.Style.Set( "justify-content", "space-between" );
		header.Style.Set( "width", "100%" );
		header.Style.Set( "gap", $"{8f * LayoutScale}px" );

		var title = new Label { Parent = header, Text = "Crafting" };
		UiStyle.Text( title, UiText.Title, CraftingTitleFontSize );
		title.Style.Set( "flex-shrink", "0" );
		title.Style.Set( "white-space", "nowrap" );
		_titleLabel = title;

		// Workbench only: Craft / Upgrade tabs on the left, the repair button on the right. Hidden in
		// the personal menu. (The header above holds the title alone: "Workbench" plus two buttons
		// never fit the 400 px column - the repair label was clipped and the action button pushed out.)
		_tabRow = new Panel { Parent = _sectionRoot };
		_tabRow.Style.Set( "flex-direction", "row" );
		_tabRow.Style.Set( "align-items", "center" );
		_tabRow.Style.Set( "gap", $"{6f * LayoutScale}px" );
		_tabRow.Style.Set( "width", "100%" );
		_tabRow.Style.Set( "flex-shrink", "0" );
		_tabRow.Style.Set( "display", "none" );
		_craftTabButton = CreateTabButton( _tabRow, "Craft" );
		_upgradeTabButton = CreateTabButton( _tabRow, "Upgrade" );
		UpdateTabVisual();

		// Free tool repair, one most-damaged tool per click; greyed when nothing is damaged.
		_repairButton = new Panel { Parent = _tabRow };
		_repairButton.Style.Set( "margin-left", "auto" );
		_repairButton.Style.Set( "padding-left", $"{10f * LayoutScale}px" );
		_repairButton.Style.Set( "padding-right", $"{10f * LayoutScale}px" );
		_repairButton.Style.Set( "padding-top", $"{5f * LayoutScale}px" );
		_repairButton.Style.Set( "padding-bottom", $"{5f * LayoutScale}px" );
		UiStyle.Button( _repairButton, UiButton.Default );
		_repairButton.Style.Set( "pointer-events", "auto" );
		_repairButton.Style.Set( "flex-shrink", "0" );

		_repairButtonLabel = new Label { Parent = _repairButton, Text = "Repair" };
		UiStyle.ButtonLabel( _repairButtonLabel, UiButton.Default, 15f * TextScale );
		_repairButtonLabel.Style.Set( "white-space", "nowrap" );

		var detail = new Panel { Parent = _sectionRoot };
		detail.Style.Set( "flex-direction", "column" );
		detail.Style.Set( "gap", $"{10f * LayoutScale}px" );
		detail.Style.Set( "width", "100%" );
		detail.Style.PaddingTop = Length.Pixels( 4f * LayoutScale );
		detail.Style.PaddingBottom = Length.Pixels( 8f * LayoutScale );
		detail.Style.PaddingLeft = Length.Pixels( 8f * LayoutScale );
		detail.Style.PaddingRight = Length.Pixels( 8f * LayoutScale );
		UiStyle.Surface( detail, UiSurface.Raised );
		detail.Style.Set( "box-sizing", "border-box" );
		// Fixed height (not max-height): selecting a recipe must never resize the detail area or move the list.
		detail.Style.Height = Length.Pixels( DetailAreaMaxHeight );
		detail.Style.MinHeight = Length.Pixels( DetailAreaMaxHeight );
		detail.Style.MaxHeight = Length.Pixels( DetailAreaMaxHeight );
		detail.Style.Set( "flex-shrink", "0" );
		detail.Style.Set( "overflow-y", "scroll" );

		var detailRow = new Panel { Parent = detail };
		detailRow.Style.Set( "flex-direction", "row" );
		detailRow.Style.Set( "gap", $"{10f * LayoutScale}px" );
		detailRow.Style.Set( "align-items", "flex-start" );
		detailRow.Style.Set( "width", "100%" );

		_detailIcon = new Panel { Parent = detailRow };
		_detailIcon.Style.Width = Length.Pixels( DetailIconSize );
		_detailIcon.Style.Height = Length.Pixels( DetailIconSize );
		_detailIcon.Style.Set( "flex-shrink", "0" );
		UiStyle.Slot( _detailIcon );
		_detailIcon.Style.Set( "background-size", "contain" );
		_detailIcon.Style.Set( "background-repeat", "no-repeat" );
		_detailIcon.Style.Set( "background-position", "center" );

		var detailText = new Panel { Parent = detailRow };
		detailText.Style.Set( "flex-direction", "column" );
		detailText.Style.Set( "align-items", "stretch" );
		detailText.Style.Set( "gap", $"{4f * LayoutScale}px" );
		detailText.Style.Set( "flex-grow", "1" );
		detailText.Style.Set( "min-width", "0" );

		var nameRow = new Panel { Parent = detailText };
		nameRow.Style.Set( "flex-direction", "row" );
		nameRow.Style.Set( "align-items", "baseline" );
		nameRow.Style.Set( "gap", $"{8f * LayoutScale}px" );
		nameRow.Style.Set( "width", "100%" );

		_detailName = new Label { Parent = nameRow, Text = "Select a recipe" };
		UiStyle.Text( _detailName, UiText.Value, ItemNameFontSize );
		_detailName.Style.Set( "white-space", "normal" );
		_detailName.Style.Set( "flex-shrink", "1" );
		_detailName.Style.Set( "min-width", "0" );

		// Upgrade tab: the selected item's current tier as a roman numeral.
		_detailTier = new Label { Parent = nameRow, Text = "I" };
		UiStyle.Text( _detailTier, UiText.Accent, ItemNameFontSize );
		_detailTier.Style.Set( "white-space", "nowrap" );
		_detailTier.Style.Set( "flex-shrink", "0" );
		_detailTier.Style.Set( "display", "none" );

		_detailDescription = new Label { Parent = detailText, Text = "" };
		UiStyle.Text( _detailDescription, UiText.Secondary, ItemDescriptionFontSize );
		_detailDescription.Style.Set( "white-space", "normal" );
		_detailDescription.Style.Set( "display", "none" );

		_craftRequirementsBlock = CreateDetailListBlock( detail, "Requirements", out _requirementsEntries );
		_craftStatsBlock = CreateDetailListBlock( detail, "Stats", out _statsEntries );

		// Upgrade tab blocks: stats first (current >> next tier), then the materials.
		_upgradeStatsBlock = CreateDetailListBlock( detail, "Stats", out _upgradeStatsEntries );
		_upgradeCostBlock = CreateDetailListBlock( detail, "Requirements", out _upgradeCostEntries );
		_upgradeStatsBlock.Style.Set( "display", "none" );
		_upgradeCostBlock.Style.Set( "display", "none" );

		var listPanel = new CraftingRecipeListPanel { Parent = _sectionRoot };
		listPanel.Bind( this );
		listPanel.BuildChrome();
		_recipeListPanel = listPanel;
		_recipeList = listPanel;
		_recipeList.Style.Set( "width", "100%" );
		// Takes whatever height the section has left under the fixed header / button / detail block.
		_recipeList.Style.MinHeight = Length.Pixels( RecipeListMinHeight );
		_recipeList.Style.Set( "flex-shrink", "1" );
		_recipeList.Style.Set( "flex-grow", "1" );
		_recipeList.Style.Set( "pointer-events", "auto" );

		// The one primary action on the page (Craft / Upgrade), full width at the foot of the column:
		// "Need materials / space" and "Need Workbench nearby" fit here, never in the title row.
		_craftButton = new CraftButtonPanel( this ) { Parent = _sectionRoot };
		_craftButton.Style.Set( "position", "relative" );
		_craftButton.Style.Set( "overflow", "hidden" );
		_craftButton.Style.Set( "width", "100%" );
		_craftButton.Style.Set( "flex-shrink", "0" );
		_craftButton.Style.Set( "align-items", "center" );
		_craftButton.Style.Set( "justify-content", "center" );
		_craftButton.Style.Set( "padding-left", $"{12f * LayoutScale}px" );
		_craftButton.Style.Set( "padding-right", $"{12f * LayoutScale}px" );
		_craftButton.Style.Set( "padding-top", $"{7f * LayoutScale}px" );
		_craftButton.Style.Set( "padding-bottom", $"{7f * LayoutScale}px" );
		UiStyle.Button( _craftButton, UiButton.Primary );
		_craftButton.Style.Set( "pointer-events", "auto" );
		_craftButton.Style.Set( "z-index", "4" );

		_craftButtonLabel = new Label { Parent = _craftButton, Text = "Craft" };
		UiStyle.ButtonLabel( _craftButtonLabel, UiButton.Primary, 15f * TextScale );
		_craftButtonLabel.Style.Set( "position", "relative" );
		_craftButtonLabel.Style.Set( "z-index", "2" );
		_craftButtonLabel.Style.Set( "white-space", "nowrap" );
		_craftButtonLabel.Style.Set( "text-align", "center" );

		var rowParent = listPanel.Content;
		PopulateRows( rowParent );
	}

	/// <summary>Fill the list for the active tab (recipes, or the player's upgradable gear).</summary>
	void PopulateRows( Panel rowParent )
	{
		if ( IsUpgradeTab )
			PopulateUpgradeRows( rowParent );
		else
			PopulateRecipeRows( rowParent );
	}

	void PopulateRecipeRows( Panel rowParent )
	{
		if ( rowParent is null || !rowParent.IsValid() )
			return;

		CraftingRecipeCatalog.EnsureLoaded();
		ResourceDefinitionCatalog.EnsureLoaded();

		rowParent.DeleteChildren();
		_rows.Clear();
		var nearStations = NearbyStationsMask();
		var workbenchOpen = IsWorkbenchOpen;
		_builtNearStations = nearStations;
		_builtWorkbenchOpen = workbenchOpen;

		var allCrafting = GameHacks.AllCrafting;

		foreach ( var recipe in CraftingRecipeCatalog.All )
		{
			if ( recipe is null || string.IsNullOrWhiteSpace( recipe.Id ) )
				continue;

			if ( workbenchOpen )
			{
				// Workbench view lists the bench's recipe set (unlocked only).
				if ( !recipe.IsUnlockedByDefault || !recipe.AppearsAtStation( Workbench.StationId ) )
					continue;
			}
			else if ( !allCrafting )
			{
				// Personal menu: hand-craftable recipes (stations lists "inventory"), plus
				// station-gated recipes while that station is nearby (campfire food).
				if ( !recipe.IsUnlockedByDefault )
					continue;

				var handCraftable = recipe.AppearsAtStation( PlayerCrafting.InventoryStationId );
				var atStation = recipe.RequiresStation && HasRequiredStation( recipe );
				if ( !handCraftable && !atStation )
					continue;
			}
			// allCrafting hack: personal menu lists every recipe regardless of unlock / station.

			AddListRow( rowParent, recipe.Id, CraftingRecipeCatalog.ResolveIconPath( recipe ), recipe.DisplayName, null );
		}

		FinishRowList();
		_builtRecipeContentVersion = BuildRecipeListVersion( nearStations );
		RefreshWorkbenchHeader();

		var keepSelection = !string.IsNullOrWhiteSpace( _selectedRecipeId )
			&& CraftingRecipeCatalog.Get( _selectedRecipeId ) is not null;

		if ( keepSelection )
			SelectRecipe( _selectedRecipeId );
		else if ( _rows.Count > 0 )
			SelectRecipe( _rows[0].Key );
		else
			RefreshSelectedDetail();
	}

	/// <summary>Upgrade tab: one row per upgradable stack in the hotbar, then the bag.</summary>
	void PopulateUpgradeRows( Panel rowParent )
	{
		if ( rowParent is null || !rowParent.IsValid() )
			return;

		rowParent.DeleteChildren();
		_rows.Clear();
		CollectUpgradeEntries( _upgradeEntries );
		_builtUpgradeSignature = ComputeUpgradeSignature( _upgradeEntries );
		_builtWorkbenchOpen = IsWorkbenchOpen;

		for ( var i = 0; i < _upgradeEntries.Count; i++ )
		{
			var entry = _upgradeEntries[i];
			var id = entry.Stack.ResourceId;
			AddListRow( rowParent, entry.Key, ResourceCatalog.GetIconPath( id ), ResourceCatalog.Resolve( id ).DisplayName,
				EquipmentUpgrade.ToRoman( entry.Stack.Tier ) );
		}

		FinishRowList();

		var keepSelection = !string.IsNullOrWhiteSpace( _selectedUpgradeKey ) && FindUpgradeEntry( _selectedUpgradeKey ) is not null;
		if ( keepSelection )
			SelectUpgrade( _selectedUpgradeKey );
		else if ( _rows.Count > 0 )
			SelectUpgrade( _rows[0].Key );
		else
		{
			_selectedUpgradeKey = null;
			RefreshSelectedDetail();
		}
	}

	void AddListRow( Panel rowParent, string key, string iconPath, string displayName, string tierNumeral )
	{
		var row = new CraftingRecipeRowPanel
		{
			Parent = rowParent,
			Section = this,
			RowKey = key
		};
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "align-items", "center" );
		row.Style.Set( "justify-content", "flex-start" );
		row.Style.Set( "gap", $"{8f * LayoutScale * RecipeListItemScale}px" );
		row.Style.Set( "width", "100%" );
		row.Style.Height = Length.Pixels( RecipeRowHeight );
		row.Style.MinHeight = Length.Pixels( RecipeRowHeight );
		row.Style.MaxHeight = Length.Pixels( RecipeRowHeight );
		row.Style.Set( "flex-shrink", "0" );
		row.Style.Set( "flex-grow", "0" );
		row.Style.PaddingTop = Length.Pixels( 0f );
		row.Style.PaddingBottom = Length.Pixels( 0f );
		row.Style.PaddingLeft = Length.Pixels( 8f );
		row.Style.PaddingRight = Length.Pixels( 8f );
		row.Style.MarginTop = Length.Pixels( 0f );
		row.Style.MarginBottom = Length.Pixels( RecipeRowGap );
		// Row chrome: the border width is part of the row-height contract (border-box), so it stays the const.
		row.Style.Set( "border-width", $"{RecipeRowBorder}px" );
		row.Style.Set( "border-radius", UiStyle.Px( UiTheme.Metrics.RadiusSmall ) );
		StyleRow( row, selected: false );
		row.Style.Set( "pointer-events", "all" );
		row.Style.Set( "overflow", "hidden" );
		row.Style.Set( "box-sizing", "border-box" );
		row.ButtonInput = PanelInputType.UI;

		var icon = new Panel { Parent = row };
		icon.Style.Width = Length.Pixels( RowIconSize );
		icon.Style.Height = Length.Pixels( RowIconSize );
		icon.Style.Set( "flex-shrink", "0" );
		icon.Style.Set( "pointer-events", "none" );
		UiStyle.Slot( icon );
		icon.Style.Set( "background-size", "contain" );
		icon.Style.Set( "background-repeat", "no-repeat" );
		icon.Style.Set( "background-position", "center" );
		MenuUiTextures.ApplyBackground( icon, iconPath );

		var name = new Label { Parent = row, Text = displayName };
		UiStyle.Text( name, UiText.Body, RecipeListRowFontSize );
		name.Style.Set( "pointer-events", "none" );
		name.Style.Set( "white-space", "nowrap" );

		if ( !string.IsNullOrWhiteSpace( tierNumeral ) )
		{
			var tier = new Label { Parent = row, Text = tierNumeral };
			UiStyle.Text( tier, UiText.Accent, RecipeListRowFontSize );
			tier.Style.Set( "pointer-events", "none" );
			tier.Style.Set( "white-space", "nowrap" );
		}

		_rows.Add( new RowUi( row, icon, name, key ) );
	}

	/// <summary>List row fill + border: selected = tab-selected fill with the accent border, idle = raised + subtle.</summary>
	static void StyleRow( Panel row, bool selected )
	{
		if ( row is null || !row.IsValid() )
			return;

		var c = UiTheme.Colors;
		row.Style.BackgroundColor = selected ? c.TabSelectedColor : c.PanelRaisedColor;
		row.Style.Set( "border-color", UiStyle.Css( selected ? c.AccentColor : c.BorderSubtleColor ) );
	}

	void FinishRowList()
	{
		if ( _rows.Count > 0 )
			_rows[^1].Root.Style.MarginBottom = Length.Pixels( 0f );

		_recipeListPanel?.SetRowCount( _rows.Count );
	}

	/// <summary>
	/// Rebuild the list when its content changed: recipe catalog sync / station state on the Craft
	/// tab, the player's upgradable stacks (ids, slots, tiers) on the Upgrade tab — checked a few
	/// times a second, never per frame.
	/// </summary>
	void RebuildRecipeRowsIfNeeded()
	{
		var content = _recipeListPanel?.Content;
		if ( content is null || !content.IsValid() )
			return;

		if ( IsUpgradeTab )
		{
			if ( Time.NowDouble < _nextUpgradeListCheckAt )
				return;

			_nextUpgradeListCheckAt = Time.NowDouble + 0.25;
			CollectUpgradeEntries( _upgradeScratch );
			if ( ComputeUpgradeSignature( _upgradeScratch ) == _builtUpgradeSignature && IsWorkbenchOpen == _builtWorkbenchOpen )
				return;

			PopulateUpgradeRows( content );
			return;
		}

		CraftingRecipeCatalog.EnsureLoaded();
		ResourceDefinitionCatalog.EnsureLoaded();

		var nearStations = NearbyStationsMask();
		var version = BuildRecipeListVersion( nearStations );
		if ( version == _builtRecipeContentVersion && nearStations == _builtNearStations
		     && IsWorkbenchOpen == _builtWorkbenchOpen )
			return;

		PopulateRecipeRows( content );
	}

	static int BuildRecipeListVersion( int nearStations ) =>
		CraftingRecipeCatalog.ContentVersion
		^ (ResourceDefinitionCatalog.ContentVersion << 16)
		^ (GameHacks.Version << 24)
		^ (nearStations << 29);

	public void ApplyRecipeListWheel( Vector2 wheel )
	{
		if ( !IsScrollTargetActive )
			return;

		_recipeListPanel?.ApplyWheel( wheel );
	}

	public bool TryHandleScrollbarPointer( Vector2 screenPos, bool pressed )
	{
		if ( !IsScrollTargetActive && !( _recipeListPanel?.IsDraggingThumb ?? false ) )
			return false;

		return _recipeListPanel?.TryHandlePointer( screenPos, pressed ) ?? false;
	}

	public bool TrySelectRecipeAtScreen( Vector2 screenPos )
	{
		if ( !IsScrollTargetActive )
			return false;

		// Scroll math only: rows hidden by the list's overflow keep their Box.Rect, so a per-row
		// rect scan picked scrolled-away rows sitting under the detail area / craft button.
		if ( _recipeListPanel is not null
		     && _recipeListPanel.TryPickRowIndexAtScreen( screenPos, out var index )
		     && index >= 0 && index < _rows.Count )
		{
			SelectRow( _rows[index].Key );
			return true;
		}

		return false;
	}

	public bool TryCraftPointerAtScreen( Vector2 screenPos, bool pressed )
	{
		if ( !IsScrollTargetActive )
			return false;

		if ( TryTabPointerAtScreen( screenPos, pressed ) )
			return true;

		if ( TryRepairPointerAtScreen( screenPos, pressed ) )
			return true;

		var over = _craftButton is not null && _craftButton.IsValid()
		           && IsScreenPosInsidePanel( _craftButton, screenPos );

		if ( pressed )
		{
			if ( !over )
				return false;

			SetButtonPressedVisual( true );
			CraftSelectedOnClick();
			return true;
		}

		if ( _craftButtonPressedVisual )
		{
			SetButtonPressedVisual( false );
			return true;
		}

		return false;
	}

	static bool IsScreenPosInsidePanel( Panel panel, Vector2 screenPos )
	{
		if ( panel is null || !panel.IsValid() )
			return false;

		if ( panel.IsInside( screenPos ) )
			return true;

		var rect = panel.Box.Rect;
		if ( rect.Width <= 0f || rect.Height <= 0f )
			return false;

		return screenPos.x >= rect.Left && screenPos.x <= rect.Right
		       && screenPos.y >= rect.Top && screenPos.y <= rect.Bottom;
	}

	/// <summary>Row click for whichever list is showing (recipe id, or an upgrade stack key).</summary>
	public void SelectRow( string key )
	{
		if ( IsUpgradeTab )
			SelectUpgrade( key );
		else
			SelectRecipe( key );
	}

	public void SelectRecipe( string recipeId )
	{
		if ( string.IsNullOrWhiteSpace( recipeId ) )
			return;

		_selectedRecipeId = recipeId;
		// Highlight first — full Refresh can hitch on icon loads and looked like a delayed select.
		UpdateRowHighlights();
		RefreshSelectedDetail();
	}

	public void SelectUpgrade( string key )
	{
		if ( string.IsNullOrWhiteSpace( key ) )
			return;

		_selectedUpgradeKey = key;
		UpdateRowHighlights();
		RefreshSelectedDetail();
	}

	public void Refresh()
	{
		CraftingRecipeCatalog.EnsureLoaded();
		ResourceDefinitionCatalog.EnsureLoaded();
		RefreshSelectedDetail();
		UpdateRowHighlights();
		RefreshWorkbenchHeader();
	}

	void RefreshSelectedDetail()
	{
		if ( IsUpgradeTab )
		{
			RefreshUpgradeDetail();
			return;
		}

		SetDetailMode( upgrade: false );
		var recipe = CraftingRecipeCatalog.Get( _selectedRecipeId );
		if ( recipe is null )
		{
			_detailName.Text = "Select a recipe";
			SetDetailDescription( null );
			PopulateEntryList( _requirementsEntries, Array.Empty<string>() );
			PopulateEntryList( _statsEntries, Array.Empty<string>() );
			ApplyDetailIcon( null );
			UpdateCraftButton( false );
			return;
		}

		_detailName.Text = recipe.DisplayName;
		SetDetailDescription( ExtractDescription( recipe ) );
		PopulateEntryList( _requirementsEntries, BuildRequirementLines( recipe ) );
		PopulateEntryList( _statsEntries, BuildStatLines( recipe ) );
		ApplyDetailIcon( CraftingRecipeCatalog.ResolveIconPath( recipe ) );
		UpdateCraftButton( CanCraftRecipe( recipe ) );
	}

	void ApplyDetailIcon( string iconPath )
	{
		if ( string.Equals( _detailIconPathApplied, iconPath ?? string.Empty, StringComparison.OrdinalIgnoreCase ) )
			return;

		_detailIconPathApplied = iconPath ?? string.Empty;
		MenuUiTextures.ApplyBackground( _detailIcon, iconPath );
	}

	void SetDetailDescription( string description )
	{
		if ( _detailDescription is null )
			return;

		var hasText = !string.IsNullOrWhiteSpace( description );
		_detailDescription.Text = hasText ? description : "";
		_detailDescription.Style.Set( "display", hasText ? "flex" : "none" );
	}

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
		{
			CraftingRecipeCatalog.EnsureLoaded();
			ResourceDefinitionCatalog.EnsureLoaded();
			RebuildRecipeRowsIfNeeded();
			Refresh();
		}
		else
		{
			SetButtonPressedVisual( false );
		}

		UpdateVisibility();
	}

	public void SetPanelVisible( bool visible )
	{
		_panelVisible = visible;
		UpdateVisibility();
	}

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}

	public void TryCraftSelected()
	{
		if ( string.IsNullOrWhiteSpace( _selectedRecipeId ) || _inventory is null )
			return;

		var recipe = CraftingRecipeCatalog.Get( _selectedRecipeId );
		if ( recipe is null || !CanCraftRecipe( recipe ) )
			return;

		var applied = _inventory.OwnerTryCraftRecipe( _selectedRecipeId );
		if ( _crafting is not null && _crafting.LogCrafting )
			Log.Info( applied
				? $"[CraftingMenu] Craft request sent/applied for '{_selectedRecipeId}'."
				: $"[CraftingMenu] Craft request failed for '{_selectedRecipeId}' (host rejected or not authoritative)." );

		Refresh();
	}

	/// <summary>Action button press (Craft tab: craft; Upgrade tab: upgrade) — no hold, no progress bar.</summary>
	public void CraftSelectedOnClick()
	{
		if ( IsUpgradeTab )
		{
			UpgradeSelectedOnClick();
			return;
		}

		if ( !CanCraftSelectedRecipe() )
			return;

		TryCraftSelected();
	}

	public void TickMenu( bool menuOpen )
	{
		if ( !menuOpen || !IsScrollTargetActive )
			return;

		RebuildRecipeRowsIfNeeded();
		_recipeListPanel?.PollWheelWhileOpen();

		// Repair grey state follows live wear (host sync lands shortly after a repair click);
		// the upgrade detail follows live materials and the stack's synced tier the same way.
		if ( _repairButton is not null && IsWorkbenchOpen && Time.NowDouble >= _nextRepairStateRefreshAt )
		{
			_nextRepairStateRefreshAt = Time.NowDouble + 0.25;
			// RefreshWorkbenchHeader re-applies the rest state (clears a pressed look from a repair click).
			RefreshWorkbenchHeader();
			if ( IsUpgradeTab )
				RefreshSelectedDetail();
		}
	}

	public void OnMenuGlobalMouseUp()
	{
		_recipeListPanel?.EndThumbDrag();
	}

	public bool CanCraftSelectedRecipe()
	{
		var recipe = CraftingRecipeCatalog.Get( _selectedRecipeId );
		return recipe is not null && CanCraftRecipe( recipe );
	}

	bool CanCraftRecipe( CraftingRecipe recipe )
	{
		if ( recipe is null || _inventory is null )
			return false;

		// allCrafting hack: no station, no cost — only output space still matters.
		if ( !GameHacks.AllCrafting )
		{
			if ( recipe.RequiresStation && !HasRequiredStation( recipe ) )
				return false;

			if ( !HasScaledResources( recipe ) )
				return false;
		}

		return _inventory.CanFitResource( recipe.Id, recipe.TotalOutputAmount );
	}

	/// <summary>Campfire / drying rack within use range (CraftingStations bits) — station-gated recipes list by proximity.</summary>
	int NearbyStationsMask() =>
		_inventory is null ? 0 : CraftingStations.NearbyMask( _inventory.GameObject );

	PlayerInventoryInteraction ResolveInteraction() =>
		_interaction ??= _inventory?.Components.Get<PlayerInventoryInteraction>();

	bool IsWorkbenchOpen => ResolveInteraction()?.OpenWorkbench is not null;

	/// <summary>
	/// Title, tabs and repair button follow the open workbench; repair grey state reads live slot
	/// wear. Opening a bench always lands on the Craft tab; closing it drops the Upgrade tab.
	/// </summary>
	void RefreshWorkbenchHeader()
	{
		var workbenchOpen = IsWorkbenchOpen;

		if ( _titleLabel is not null )
			_titleLabel.Text = workbenchOpen ? "Workbench" : "Crafting";

		_tabRow?.Style.Set( "display", workbenchOpen ? "flex" : "none" );

		if ( workbenchOpen != _wasWorkbenchOpen )
		{
			_wasWorkbenchOpen = workbenchOpen;
			SetActiveTab( WorkbenchTab.Craft );
		}

		if ( _repairButton is null )
			return;

		_repairButton.Style.Set( "display", workbenchOpen ? "flex" : "none" );
		if ( !workbenchOpen )
			return;

		_repairAvailable = _crafting is not null && _crafting.HasDamagedTool();
		UiStyle.SetButtonState( _repairButton, UiButton.Default, hovered: false, pressed: false, disabled: !_repairAvailable );
		_repairButton.Style.Set( "cursor", _repairAvailable ? "pointer" : "default" );
		if ( _repairButtonLabel is not null )
			_repairButtonLabel.Text = "Repair";
	}

	bool TryRepairPointerAtScreen( Vector2 screenPos, bool pressed )
	{
		if ( !IsWorkbenchOpen || _repairButton is null || !_repairButton.IsValid() )
			return false;

		if ( !IsScreenPosInsidePanel( _repairButton, screenPos ) )
			return false;

		if ( !pressed )
			return true;

		if ( !_repairAvailable || _crafting is null )
			return true;

		UiStyle.SetButtonState( _repairButton, UiButton.Default, hovered: false, pressed: true, disabled: false );
		_crafting.OwnerTryRepairDamagedTool();
		_nextRepairStateRefreshAt = 0;
		return true;
	}

	bool HasRequiredStation( CraftingRecipe recipe )
	{
		if ( recipe is null || !recipe.RequiresStation || _inventory is null )
			return false;

		return CraftingStations.IsPlayerNear( _inventory.GameObject, recipe.RequiredStation );
	}

	bool HasScaledResources( CraftingRecipe recipe )
	{
		if ( _inventory is null || recipe.Ingredients is null )
			return false;

		for ( var i = 0; i < recipe.Ingredients.Count; i++ )
		{
			var ing = recipe.Ingredients[i];
			if ( ing is null )
				continue;

			if ( _inventory.CountResource( ing.ResourceId ) < Math.Max( 1, ing.Amount ) )
				return false;
		}

		return true;
	}

	void UpdateCraftButton( bool canCraft )
	{
		if ( _craftButton is null || _craftButtonLabel is null )
			return;

		_craftButtonEnabled = canCraft;
		if ( IsUpgradeTab )
		{
			_craftButtonLabel.Text = canCraft ? "Upgrade (LMB)" : DescribeUpgradeBlocker();
		}
		else if ( canCraft )
		{
			_craftButtonLabel.Text = "Craft (LMB)";
		}
		else
		{
			var recipe = CraftingRecipeCatalog.Get( _selectedRecipeId );
			if ( GameHacks.AllCrafting )
				_craftButtonLabel.Text = "Need space";
			else if ( recipe is not null && recipe.RequiresStation && !HasRequiredStation( recipe ) )
				_craftButtonLabel.Text = $"Need {CraftingStations.DisplayName( recipe.RequiredStation )} nearby";
			else
				_craftButtonLabel.Text = "Need materials / space";
		}

		UiStyle.SetButtonState( _craftButton, UiButton.Primary, hovered: false, pressed: _craftButtonPressedVisual, disabled: !canCraft );
	}

	void SetButtonPressedVisual( bool pressed )
	{
		_craftButtonPressedVisual = pressed;
		if ( _craftButton is null || !_craftButton.IsValid() )
			return;

		UiStyle.SetButtonState( _craftButton, UiButton.Primary, hovered: false, pressed: pressed, disabled: !_craftButtonEnabled );
		_craftButton.Style.Set( "transform", pressed ? "scale(0.96)" : "scale(1)" );
	}

	static bool IsMouseOverPanel( Panel panel )
	{
		if ( panel is null || !panel.IsValid() )
			return false;

		if ( panel.IsInside( Mouse.Position ) )
			return true;

		var rect = panel.Box.Rect;
		if ( rect.Width <= 0f || rect.Height <= 0f )
			return false;

		var m = Mouse.Position;
		return m.x >= rect.Left && m.x <= rect.Right && m.y >= rect.Top && m.y <= rect.Bottom;
	}

	void UpdateRowHighlights()
	{
		for ( var i = 0; i < _rows.Count; i++ )
		{
			var row = _rows[i].Root;
			if ( row is null || !row.IsValid() )
				continue;

			var selectedKey = IsUpgradeTab ? _selectedUpgradeKey : _selectedRecipeId;
			var selected = string.Equals( _rows[i].Key, selectedKey, StringComparison.OrdinalIgnoreCase );
			StyleRow( row, selected );

			// Rows the player cannot afford / upgrade read muted (re-evaluated on select + refresh, never per frame).
			var name = _rows[i].NameLabel;
			if ( name is not null && name.IsValid() )
				name.Style.FontColor = IsRowAffordable( _rows[i].Key ) ? UiTheme.Colors.TextPrimaryColor : UiTheme.Colors.TextMutedColor;
		}
	}

	bool IsRowAffordable( string key )
	{
		if ( IsUpgradeTab )
		{
			var entry = FindUpgradeEntry( key );
			return entry is not null && CanUpgradeEntry( entry.Value );
		}

		var recipe = CraftingRecipeCatalog.Get( key );
		return recipe is not null && CanCraftRecipe( recipe );
	}

	static Panel CreateDetailListBlock( Panel parent, string headingText, out Panel entriesHost )
	{
		var block = new Panel { Parent = parent };
		block.Style.Set( "flex-direction", "column" );
		block.Style.Set( "gap", $"{4f * LayoutScale}px" );
		block.Style.Set( "width", "100%" );

		var heading = new Label { Parent = block, Text = headingText };
		UiStyle.Text( heading, UiText.Header, SectionHeaderFontSize );

		entriesHost = new Panel { Parent = block };
		entriesHost.Style.Set( "flex-direction", "column" );
		entriesHost.Style.Set( "gap", $"{3f * LayoutScale}px" );
		entriesHost.Style.Set( "width", "100%" );
		entriesHost.Style.PaddingLeft = Length.Pixels( 6f * LayoutScale );

		return block;
	}

	static void PopulateEntryList( Panel host, IReadOnlyList<string> lines )
	{
		if ( host is null )
			return;

		host.DeleteChildren();

		if ( lines is null || lines.Count == 0 )
		{
			AddEntryLabel( host, "—" );
			return;
		}

		for ( var i = 0; i < lines.Count; i++ )
			AddEntryLabel( host, lines[i] );
	}

	static void AddEntryLabel( Panel host, string text )
	{
		var label = new Label { Parent = host, Text = text };
		UiStyle.Text( label, UiText.Secondary, SectionEntryFontSize );
		label.Style.Set( "white-space", "normal" );
	}

	List<string> BuildRequirementLines( CraftingRecipe recipe )
	{
		var lines = new List<string>();
		if ( recipe.Ingredients is null || recipe.Ingredients.Count == 0 )
			return lines;

		// allCrafting hack: every ingredient costs 0 (host consumes nothing).
		var free = GameHacks.AllCrafting;

		for ( var i = 0; i < recipe.Ingredients.Count; i++ )
		{
			var ing = recipe.Ingredients[i];
			if ( ing is null )
				continue;

			var def = ResourceCatalog.Resolve( ing.ResourceId );
			var have = _inventory?.CountResource( ing.ResourceId ) ?? 0;
			var need = free ? 0 : Math.Max( 1, ing.Amount );
			lines.Add( $"{have}/{need} {def.DisplayName}" );
		}

		return lines;
	}

	static List<string> BuildStatLines( CraftingRecipe recipe )
	{
		var lines = new List<string>();
		if ( recipe.Stats is null || recipe.Stats.Count == 0 )
			return lines;

		for ( var i = 0; i < recipe.Stats.Count; i++ )
		{
			var line = recipe.Stats[i];
			if ( line is null )
				continue;

			// Description lives under the display name beside the icon — not in Stats.
			if ( string.Equals( line.Label, "Description", StringComparison.OrdinalIgnoreCase ) )
				continue;

			// Type is a tag — show the value alone (not "Type: …").
			if ( string.Equals( line.Label, "Type", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( !string.IsNullOrWhiteSpace( line.Value ) )
					lines.Add( line.Value );
				continue;
			}

			lines.Add( $"{line.Label}: {line.Value}" );
		}

		return lines;
	}

	static string ExtractDescription( CraftingRecipe recipe )
	{
		if ( recipe?.Stats is null )
			return null;

		for ( var i = 0; i < recipe.Stats.Count; i++ )
		{
			var line = recipe.Stats[i];
			if ( line is null )
				continue;

			if ( !string.Equals( line.Label, "Description", StringComparison.OrdinalIgnoreCase ) )
				continue;

			return string.IsNullOrWhiteSpace( line.Value ) ? null : line.Value.Trim();
		}

		return null;
	}

	// ------------------------------------------------------------------ Upgrade tab

	bool IsUpgradeTab => _activeTab == WorkbenchTab.Upgrade && IsWorkbenchOpen;

	/// <summary>One upgradable stack the player carries: <c>grid:slot</c> key plus a snapshot of the stack.</summary>
	readonly struct UpgradeEntry
	{
		public string Key { get; init; }
		public string GridId { get; init; }
		public int SlotIndex { get; init; }
		public InventorySlot Stack { get; init; }
	}

	Panel CreateTabButton( Panel parent, string text )
	{
		var button = new Panel { Parent = parent };
		button.Style.Set( "padding-left", $"{14f * LayoutScale}px" );
		button.Style.Set( "padding-right", $"{14f * LayoutScale}px" );
		button.Style.Set( "padding-top", $"{5f * LayoutScale}px" );
		button.Style.Set( "padding-bottom", $"{5f * LayoutScale}px" );
		UiStyle.Tab( button, false );
		button.Style.Set( "pointer-events", "auto" );
		button.Style.Set( "cursor", "pointer" );
		button.Style.Set( "flex-shrink", "0" );

		var label = new Label { Parent = button, Text = text };
		UiStyle.Text( label, UiText.Value, 15f * TextScale );
		label.Style.Set( "pointer-events", "none" );
		label.Style.Set( "white-space", "nowrap" );
		return button;
	}

	void UpdateTabVisual()
	{
		StyleTab( _craftTabButton, _activeTab == WorkbenchTab.Craft );
		StyleTab( _upgradeTabButton, _activeTab == WorkbenchTab.Upgrade );
	}

	static void StyleTab( Panel button, bool active )
	{
		if ( button is null || !button.IsValid() )
			return;

		UiStyle.Tab( button, active );
		button.Style.Set( "opacity", active ? "1" : "0.8" );
	}

	void SetActiveTab( WorkbenchTab tab )
	{
		var changed = _activeTab != tab;
		_activeTab = tab;
		UpdateTabVisual();
		if ( !changed )
			return;

		SetButtonPressedVisual( false );
		var content = _recipeListPanel?.Content;
		if ( content is not null && content.IsValid() )
			PopulateRows( content );
		else
			RefreshSelectedDetail();
	}

	/// <summary>Soft-cursor Attack1 on the Craft / Upgrade tab buttons (workbench only).</summary>
	bool TryTabPointerAtScreen( Vector2 screenPos, bool pressed )
	{
		if ( !IsWorkbenchOpen || _tabRow is null || !_tabRow.IsValid() )
			return false;

		var overCraft = IsScreenPosInsidePanel( _craftTabButton, screenPos );
		var overUpgrade = !overCraft && IsScreenPosInsidePanel( _upgradeTabButton, screenPos );
		if ( !overCraft && !overUpgrade )
			return false;

		if ( pressed )
			SetActiveTab( overCraft ? WorkbenchTab.Craft : WorkbenchTab.Upgrade );

		return true;
	}

	void CollectUpgradeEntries( List<UpgradeEntry> into )
	{
		into.Clear();

		var hotbar = _inventory?.Components.Get<PlayerHotbar>();
		if ( hotbar is not null )
		{
			for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
			{
				var slot = hotbar.GetSlot( i );
				if ( EquipmentUpgrade.IsUpgradable( slot ) )
					into.Add( new UpgradeEntry { Key = $"{EquipmentUpgrade.HotbarGridId}:{i}", GridId = EquipmentUpgrade.HotbarGridId, SlotIndex = i, Stack = slot } );
			}
		}

		if ( _inventory is null )
			return;

		for ( var i = 0; i < _inventory.SlotCount; i++ )
		{
			var slot = _inventory.GetSlot( i );
			if ( EquipmentUpgrade.IsUpgradable( slot ) )
				into.Add( new UpgradeEntry { Key = $"{EquipmentUpgrade.BagGridId}:{i}", GridId = EquipmentUpgrade.BagGridId, SlotIndex = i, Stack = slot } );
		}
	}

	static int ComputeUpgradeSignature( List<UpgradeEntry> entries )
	{
		var hash = new HashCode();
		hash.Add( entries.Count );
		for ( var i = 0; i < entries.Count; i++ )
		{
			hash.Add( entries[i].Key, StringComparer.Ordinal );
			hash.Add( entries[i].Stack.ResourceId ?? string.Empty, StringComparer.OrdinalIgnoreCase );
			hash.Add( EquipmentUpgrade.NormalizeTier( entries[i].Stack.Tier ) );
		}

		return hash.ToHashCode();
	}

	UpgradeEntry? FindUpgradeEntry( string key )
	{
		if ( string.IsNullOrWhiteSpace( key ) )
			return null;

		for ( var i = 0; i < _upgradeEntries.Count; i++ )
		{
			if ( string.Equals( _upgradeEntries[i].Key, key, StringComparison.OrdinalIgnoreCase ) )
				return _upgradeEntries[i];
		}

		return null;
	}

	/// <summary>Swap the detail area between the craft blocks and the upgrade blocks (same fixed box).</summary>
	void SetDetailMode( bool upgrade )
	{
		_craftRequirementsBlock?.Style.Set( "display", upgrade ? "none" : "flex" );
		_craftStatsBlock?.Style.Set( "display", upgrade ? "none" : "flex" );
		_upgradeStatsBlock?.Style.Set( "display", upgrade ? "flex" : "none" );
		_upgradeCostBlock?.Style.Set( "display", upgrade ? "flex" : "none" );
		if ( !upgrade )
			_detailTier?.Style.Set( "display", "none" );
	}

	void RefreshUpgradeDetail()
	{
		SetDetailMode( upgrade: true );
		SetDetailDescription( null );

		var found = FindUpgradeEntry( _selectedUpgradeKey );
		if ( found is null || !EquipmentCatalog.TryGet( found.Value.Stack.ResourceId, out var profile ) || profile is null )
		{
			_detailName.Text = _upgradeEntries.Count == 0 ? "Nothing to upgrade" : "Select an item";
			_detailTier.Style.Set( "display", "none" );
			_upgradeStatsEntries?.DeleteChildren();
			AddEntryLabel( _upgradeStatsEntries, "—" );
			PopulateEntryList( _upgradeCostEntries, Array.Empty<string>() );
			ApplyDetailIcon( null );
			UpdateCraftButton( false );
			return;
		}

		var entry = found.Value;
		var stack = entry.Stack;
		var tier = EquipmentUpgrade.NormalizeTier( stack.Tier );
		var atMax = tier >= EquipmentUpgrade.MaxTier;
		var nextTier = atMax ? tier : tier + 1;

		_detailName.Text = ResourceCatalog.Resolve( stack.ResourceId ).DisplayName;
		_detailTier.Text = EquipmentUpgrade.ToRoman( tier );
		_detailTier.Style.Set( "display", "flex" );
		ApplyDetailIcon( ResourceCatalog.GetIconPath( stack.ResourceId ) );

		// Stats: current value, >> and the next tier's value in green. Armor never shows damage.
		_upgradeStatsEntries.DeleteChildren();
		var any = false;
		any |= AddUpgradeStatRow( "Damage", EquipmentUpgrade.GetDamage( profile, tier ), EquipmentUpgrade.GetDamage( profile, nextTier ), atMax );
		any |= AddUpgradeStatRow( "Armor", EquipmentUpgrade.GetArmor( profile, tier ), EquipmentUpgrade.GetArmor( profile, nextTier ), atMax );
		any |= AddUpgradeStatRow( "Durability", EquipmentUpgrade.GetDurabilityMax( profile, tier ), EquipmentUpgrade.GetDurabilityMax( profile, nextTier ), atMax );
		if ( !any )
			AddEntryLabel( _upgradeStatsEntries, "—" );
		if ( atMax )
			AddEntryLabel( _upgradeStatsEntries, $"Tier {EquipmentUpgrade.ToRoman( tier )} is the highest tier." );

		PopulateEntryList( _upgradeCostEntries, atMax ? Array.Empty<string>() : BuildUpgradeCostLines( stack.ResourceId ) );
		UpdateCraftButton( CanUpgradeEntry( entry ) );
	}

	/// <summary>One "Label  current >> next" row; skipped (false) when the stat is 0 at every tier.</summary>
	bool AddUpgradeStatRow( string label, float current, float next, bool atMax )
	{
		if ( current <= 0f && next <= 0f )
			return false;

		var row = new Panel { Parent = _upgradeStatsEntries };
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "align-items", "baseline" );
		row.Style.Set( "gap", $"{6f * LayoutScale}px" );
		row.Style.Set( "width", "100%" );

		AddInlineLabel( row, $"{label}:", UiText.Secondary );
		AddInlineLabel( row, $"{current:0.#}", UiText.Value );
		if ( atMax )
			return true;

		AddInlineLabel( row, ">>", UiText.Muted );
		// Next tier's value: value weight in the theme's positive colour.
		AddInlineLabel( row, $"{next:0.#}", UiText.Value, UiTheme.Colors.PositiveColor );
		return true;
	}

	static void AddInlineLabel( Panel row, string text, UiText kind, Color? colorOverride = null )
	{
		var label = new Label { Parent = row, Text = text };
		UiStyle.Text( label, kind, SectionEntryFontSize );
		if ( colorOverride is { } color )
			label.Style.FontColor = color;
		label.Style.Set( "white-space", "nowrap" );
	}

	List<string> BuildUpgradeCostLines( string resourceId )
	{
		var lines = new List<string>();
		var cost = EquipmentUpgrade.BuildCost( resourceId );
		if ( cost is null )
		{
			lines.Add( "No recipe — cannot upgrade" );
			return lines;
		}

		var free = GameHacks.AllCrafting;
		for ( var i = 0; i < cost.Count; i++ )
		{
			var ing = cost[i];
			var def = ResourceCatalog.Resolve( ing.ResourceId );
			var have = _inventory?.CountResource( ing.ResourceId ) ?? 0;
			var need = free ? 0 : Math.Max( 1, ing.Amount );
			lines.Add( $"{have}/{need} {def.DisplayName}" );
		}

		return lines;
	}

	bool CanUpgradeEntry( in UpgradeEntry entry )
	{
		if ( _inventory is null || !EquipmentUpgrade.CanUpgradeFurther( entry.Stack ) )
			return false;

		var cost = EquipmentUpgrade.BuildCost( entry.Stack.ResourceId );
		if ( cost is null )
			return false;

		return GameHacks.AllCrafting || cost.Count == 0 || _inventory.HasResources( cost );
	}

	string DescribeUpgradeBlocker()
	{
		var found = FindUpgradeEntry( _selectedUpgradeKey );
		if ( found is null )
			return _upgradeEntries.Count == 0 ? "Nothing to upgrade" : "Select an item";

		if ( !EquipmentUpgrade.CanUpgradeFurther( found.Value.Stack ) )
			return "Max tier";

		return EquipmentUpgrade.BuildCost( found.Value.Stack.ResourceId ) is null ? "No recipe" : "Need materials";
	}

	void UpgradeSelectedOnClick()
	{
		var found = FindUpgradeEntry( _selectedUpgradeKey );
		if ( found is null || _crafting is null || !CanUpgradeEntry( found.Value ) )
			return;

		var entry = found.Value;
		var sent = _crafting.OwnerTryUpgradeEquipment( entry.GridId, entry.SlotIndex, entry.Stack.ResourceId );
		if ( _crafting.LogCrafting )
			Log.Info( $"[CraftingMenu] Upgrade request {(sent ? "sent/applied" : "failed")} for {entry.Key} ({entry.Stack.ResourceId})." );

		// Host sync lands shortly: re-list (tier numeral) and re-read materials on the next tick.
		_nextUpgradeListCheckAt = 0;
		_nextRepairStateRefreshAt = 0;
	}

	sealed class CraftButtonPanel : Panel
	{
		readonly CraftingMenuSection _section;

		public CraftButtonPanel( CraftingMenuSection section ) => _section = section;

		public override bool WantsMouseInput() => false;

		protected override void OnMouseDown( MousePanelEvent e )
		{
			base.OnMouseDown( e );
			if ( e.Button != "mouseleft" )
				return;

			_section.SetButtonPressedVisual( true );
			_section.CraftSelectedOnClick();
		}

		protected override void OnMouseUp( MousePanelEvent e )
		{
			base.OnMouseUp( e );
			if ( e.Button != "mouseleft" )
				return;

			_section.SetButtonPressedVisual( false );
		}
	}

	sealed class RowUi
	{
		public Panel Root { get; }
		public Panel IconPanel { get; }
		/// <summary>Row name label — muted while the recipe / upgrade is unaffordable.</summary>
		public Label NameLabel { get; }
		/// <summary>Recipe id (Craft tab) or <c>grid:slot</c> stack key (Upgrade tab).</summary>
		public string Key { get; }

		public RowUi( Panel root, Panel iconPanel, Label nameLabel, string key )
		{
			Root = root;
			IconPanel = iconPanel;
			NameLabel = nameLabel;
			Key = key;
		}
	}
}
