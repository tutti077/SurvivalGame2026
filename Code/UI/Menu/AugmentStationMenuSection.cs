using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Full-screen augment station: craft list (left), paper-doll + bank (center), player bag (right).
/// </summary>
public sealed class AugmentStationMenuSection : IPlayerMenuSection
{
	public const float SlotSize = 56f;
	public const float SlotGap = 4f;
	public const float CraftHoldSeconds = 1f;

	public string SectionId => "augment_station";

	readonly PlayerAugments _augments;
	readonly PlayerInventory _inventory;
	readonly PlayerInventoryInteraction _interaction;
	readonly PlayerAugmentInstalledGridHost _installedHost;
	readonly PlayerAugmentBankGridHost _bankHost;
	readonly PlayerInventoryGridHost _bagHost;

	readonly List<SlotUi> _installedUi = new();
	readonly List<SlotUi> _bankUi = new();
	readonly List<SlotUi> _bagUi = new();
	readonly List<RowUi> _rows = new();

	Panel _sectionRoot;
	Panel _craftButton;
	Panel _craftProgressFill;
	Label _detailName;
	Label _detailDescription;
	Panel _requirementsEntries;
	Panel _statsEntries;
	Panel _rowParent;

	string _selectedId;
	bool _menuOpen;
	bool _panelVisible;
	bool _craftHoldActive;
	bool _craftHoldCompleted;
	float _craftHoldElapsed;
	int _builtCatalogVersion = -1;
	int _lastAugmentVersion = -1;

	static readonly Color CraftButtonColor = new( 0.22f, 0.45f, 0.28f, 0.95f );

	public AugmentStationMenuSection(
		PlayerAugments augments,
		PlayerInventory inventory,
		PlayerInventoryInteraction interaction )
	{
		_augments = augments;
		_inventory = inventory;
		_interaction = interaction;
		_installedHost = augments is not null && inventory is not null
			? new PlayerAugmentInstalledGridHost( augments, inventory )
			: null;
		_bankHost = augments is not null && inventory is not null
			? new PlayerAugmentBankGridHost( augments, inventory )
			: null;
		_bagHost = inventory is not null ? new PlayerInventoryGridHost( "player", inventory ) : null;

		if ( _installedHost is not null )
			_interaction?.RegisterGrid( _installedHost );
		if ( _bankHost is not null )
			_interaction?.RegisterGrid( _bankHost );
	}

	public void Build( Panel parent )
	{
		_sectionRoot = new Panel { Parent = parent };
		_sectionRoot.Style.Set( "position", "relative" );
		_sectionRoot.Style.Set( "width", "100%" );
		_sectionRoot.Style.Set( "height", "100%" );
		_sectionRoot.Style.Set( "flex-direction", "row" );
		_sectionRoot.Style.Set( "gap", "16px" );
		_sectionRoot.Style.Set( "pointer-events", "auto" );
		_sectionRoot.Style.Set( "display", "none" );

		BuildCraftColumn( _sectionRoot );
		BuildCenterColumn( _sectionRoot );
		BuildBagColumn( _sectionRoot );

		Refresh();
		UpdateVisibility();
	}

	void BuildCraftColumn( Panel parent )
	{
		var col = new Panel { Parent = parent };
		col.Style.Set( "flex-direction", "column" );
		col.Style.Set( "width", "32%" );
		col.Style.Set( "height", "100%" );
		col.Style.Set( "gap", "8px" );
		col.Style.Set( "overflow", "hidden" );

		var title = new Label { Parent = col, Text = "Augment Crafting" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( 22f );

		_detailName = new Label { Parent = col, Text = "Select an augment" };
		_detailName.Style.FontColor = Color.White;
		_detailName.Style.FontSize = Length.Pixels( 18f );

		_detailDescription = new Label { Parent = col, Text = "" };
		_detailDescription.Style.FontColor = new Color( 0.72f, 0.74f, 0.78f );
		_detailDescription.Style.FontSize = Length.Pixels( 13f );
		_detailDescription.Style.Set( "white-space", "normal" );

		var reqTitle = new Label { Parent = col, Text = "Requirements" };
		reqTitle.Style.FontColor = new Color( 0.85f, 0.87f, 0.9f );
		reqTitle.Style.FontSize = Length.Pixels( 14f );
		_requirementsEntries = new Panel { Parent = col };
		_requirementsEntries.Style.Set( "flex-direction", "column" );
		_requirementsEntries.Style.Set( "gap", "2px" );

		var statsTitle = new Label { Parent = col, Text = "Stats" };
		statsTitle.Style.FontColor = new Color( 0.85f, 0.87f, 0.9f );
		statsTitle.Style.FontSize = Length.Pixels( 14f );
		_statsEntries = new Panel { Parent = col };
		_statsEntries.Style.Set( "flex-direction", "column" );
		_statsEntries.Style.Set( "gap", "2px" );

		_craftButton = new Panel { Parent = col };
		_craftButton.Style.Set( "width", "100%" );
		_craftButton.Style.Height = Length.Pixels( 40f );
		_craftButton.Style.BackgroundColor = CraftButtonColor;
		_craftButton.Style.Set( "border-radius", "4px" );
		_craftButton.Style.Set( "justify-content", "center" );
		_craftButton.Style.Set( "align-items", "center" );
		_craftButton.Style.Set( "overflow", "hidden" );
		_craftButton.Style.Set( "position", "relative" );

		_craftProgressFill = new Panel { Parent = _craftButton };
		_craftProgressFill.Style.Set( "position", "absolute" );
		_craftProgressFill.Style.Set( "left", "0" );
		_craftProgressFill.Style.Set( "top", "0" );
		_craftProgressFill.Style.Set( "bottom", "0" );
		_craftProgressFill.Style.Width = Length.Percent( 0 );
		_craftProgressFill.Style.BackgroundColor = new Color( 0.35f, 0.7f, 0.45f, 0.55f );
		_craftProgressFill.Style.Set( "pointer-events", "none" );

		var craftLabel = new Label { Parent = _craftButton, Text = "Hold to Craft" };
		craftLabel.Style.FontColor = Color.White;
		craftLabel.Style.FontSize = Length.Pixels( 15f );
		craftLabel.Style.Set( "z-index", "1" );

		var listTitle = new Label { Parent = col, Text = "Recipes" };
		listTitle.Style.FontColor = Color.White;
		listTitle.Style.FontSize = Length.Pixels( 16f );

		var list = new Panel { Parent = col };
		list.Style.Set( "flex-direction", "column" );
		list.Style.Set( "flex-grow", "1" );
		list.Style.Set( "overflow", "scroll" );
		list.Style.Set( "gap", "4px" );
		_rowParent = list;
		PopulateRows();
	}

	void BuildCenterColumn( Panel parent )
	{
		var col = new Panel { Parent = parent };
		col.Style.Set( "flex-direction", "column" );
		col.Style.Set( "width", "36%" );
		col.Style.Set( "height", "100%" );
		col.Style.Set( "gap", "10px" );
		col.Style.Set( "align-items", "center" );

		var dollTitle = new Label { Parent = col, Text = "Installed Augments" };
		dollTitle.Style.FontColor = Color.White;
		dollTitle.Style.FontSize = Length.Pixels( 18f );

		var doll = new Panel { Parent = col };
		doll.Style.Set( "flex-direction", "column" );
		doll.Style.Set( "gap", "8px" );
		doll.Style.Set( "width", "100%" );

		string lastPart = null;
		Panel row = null;
		_installedUi.Clear();
		for ( var i = 0; i < AugmentSlots.Layout.Length; i++ )
		{
			var (slot, bodyPart, variation) = AugmentSlots.Layout[i];
			if ( !string.Equals( lastPart, bodyPart, StringComparison.Ordinal ) )
			{
				lastPart = bodyPart;
				var partLabel = new Label { Parent = doll, Text = bodyPart };
				partLabel.Style.FontColor = new Color( 0.78f, 0.8f, 0.84f );
				partLabel.Style.FontSize = Length.Pixels( 13f );
				row = new Panel { Parent = doll };
				row.Style.Set( "flex-direction", "row" );
				row.Style.Set( "gap", $"{SlotGap}px" );
				row.Style.Set( "justify-content", "center" );
			}

			var host = new Panel { Parent = row };
			host.Style.Set( "flex-direction", "column" );
			host.Style.Set( "align-items", "center" );
			host.Style.Set( "gap", "2px" );

			var slotPanel = new InventorySlotPanel( (int)slot, _installedHost, _interaction ) { Parent = host };
			StyleSlot( slotPanel );
			var label = new Label { Parent = host, Text = variation };
			label.Style.FontColor = new Color( 0.7f, 0.72f, 0.76f );
			label.Style.FontSize = Length.Pixels( 11f );
			_interaction?.RegisterSlot( slotPanel );
			_installedUi.Add( CreateSlotUi( slotPanel ) );
		}

		var bankTitle = new Label { Parent = col, Text = "Augment Bank" };
		bankTitle.Style.FontColor = Color.White;
		bankTitle.Style.FontSize = Length.Pixels( 16f );

		var bankGrid = new Panel { Parent = col };
		bankGrid.Style.Set( "flex-direction", "row" );
		bankGrid.Style.Set( "flex-wrap", "wrap" );
		bankGrid.Style.Set( "gap", $"{SlotGap}px" );
		bankGrid.Style.Set( "justify-content", "center" );
		bankGrid.Style.Set( "max-width", $"{SlotSize * BankColumns() + SlotGap * (BankColumns() - 1)}px" );

		_bankUi.Clear();
		for ( var i = 0; i < PlayerAugments.BankSlotCount; i++ )
		{
			var slotPanel = new InventorySlotPanel( i, _bankHost, _interaction ) { Parent = bankGrid };
			StyleSlot( slotPanel );
			_interaction?.RegisterSlot( slotPanel );
			_bankUi.Add( CreateSlotUi( slotPanel ) );
		}
	}

	void BuildBagColumn( Panel parent )
	{
		var col = new Panel { Parent = parent };
		col.Style.Set( "flex-direction", "column" );
		col.Style.Set( "width", "28%" );
		col.Style.Set( "height", "100%" );
		col.Style.Set( "gap", "8px" );
		col.Style.Set( "align-items", "center" );

		var title = new Label { Parent = col, Text = "Inventory" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( 18f );

		var columns = Math.Max( 1, _inventory?.Columns ?? InventoryDefaults.DefaultColumns );
		var slotCount = Math.Max( 1, _inventory?.SlotCount ?? InventoryDefaults.DefaultSlotCount );
		var grid = new Panel { Parent = col };
		grid.Style.Set( "flex-direction", "row" );
		grid.Style.Set( "flex-wrap", "wrap" );
		grid.Style.Set( "gap", $"{SlotGap}px" );
		grid.Style.Width = Length.Pixels( columns * SlotSize + (columns - 1) * SlotGap );

		_bagUi.Clear();
		for ( var i = 0; i < slotCount; i++ )
		{
			var slotPanel = new InventorySlotPanel( i, _bagHost, _interaction ) { Parent = grid };
			StyleSlot( slotPanel );
			_interaction?.RegisterSlot( slotPanel );
			_bagUi.Add( CreateSlotUi( slotPanel ) );
		}
	}

	static int BankColumns() => PlayerAugments.BankColumns;

	static void StyleSlot( Panel slotPanel )
	{
		slotPanel.Style.Width = Length.Pixels( SlotSize );
		slotPanel.Style.Height = Length.Pixels( SlotSize );
		slotPanel.Style.Set( "flex-shrink", "0" );
		slotPanel.Style.Set( "position", "relative" );
		slotPanel.Style.BackgroundColor = new Color( 0.1f, 0.11f, 0.13f, 0.95f );
		slotPanel.Style.Set( "border-width", "1px" );
		slotPanel.Style.Set( "border-color", "#474d57" );
		slotPanel.Style.Set( "border-radius", "4px" );
		slotPanel.Style.Set( "overflow", "hidden" );
		slotPanel.Style.Set( "pointer-events", "auto" );
	}

	void PopulateRows()
	{
		if ( _rowParent is null || !_rowParent.IsValid() )
			return;

		AugmentCatalog.EnsureLoaded();
		_rowParent.DeleteChildren();
		_rows.Clear();

		foreach ( var def in AugmentCatalog.All )
		{
			if ( def is null || string.IsNullOrWhiteSpace( def.Id ) || !def.IsUnlockedByDefault )
				continue;

			var row = new Panel { Parent = _rowParent };
			row.Style.Set( "flex-direction", "row" );
			row.Style.Set( "align-items", "center" );
			row.Style.Set( "gap", "8px" );
			row.Style.Height = Length.Pixels( 40f );
			row.Style.Set( "width", "100%" );
			row.Style.BackgroundColor = new Color( 0.10f, 0.11f, 0.13f, 0.9f );
			row.Style.Set( "border-radius", "3px" );
			row.Style.PaddingLeft = Length.Pixels( 6f );
			row.Style.Set( "pointer-events", "all" );

			var icon = new Panel { Parent = row };
			icon.Style.Width = Length.Pixels( 28f );
			icon.Style.Height = Length.Pixels( 28f );
			icon.Style.Set( "pointer-events", "none" );
			MenuUiTextures.ApplyBackground( icon, def.Icon );

			var name = new Label { Parent = row, Text = def.DisplayName };
			name.Style.FontColor = Color.White;
			name.Style.FontSize = Length.Pixels( 14f );
			name.Style.Set( "pointer-events", "none" );

			_rows.Add( new RowUi( row, def.Id ) );
		}

		_builtCatalogVersion = AugmentCatalog.ContentVersion;
		if ( _rows.Count > 0 )
			SelectRecipe( string.IsNullOrWhiteSpace( _selectedId ) ? _rows[0].Id : _selectedId );
		else
			RefreshDetail();
	}

	public void SelectRecipe( string id )
	{
		_selectedId = id;
		for ( var i = 0; i < _rows.Count; i++ )
		{
			var selected = string.Equals( _rows[i].Id, id, StringComparison.OrdinalIgnoreCase );
			_rows[i].Root.Style.Set( "border-width", selected ? "1px" : "0px" );
			_rows[i].Root.Style.Set( "border-color", selected ? "#6aa0ff" : "transparent" );
		}

		RefreshDetail();
	}

	void RefreshDetail()
	{
		if ( _detailName is null )
			return;

		if ( !AugmentCatalog.TryGet( _selectedId, out var def ) )
		{
			_detailName.Text = "Select an augment";
			_detailDescription.Text = "";
			_requirementsEntries?.DeleteChildren();
			_statsEntries?.DeleteChildren();
			return;
		}

		_detailName.Text = def.DisplayName;
		_detailDescription.Text = def.Description ?? "";

		_requirementsEntries?.DeleteChildren();
		if ( def.Ingredients is not null )
		{
			foreach ( var ing in def.Ingredients )
			{
				var line = new Label
				{
					Parent = _requirementsEntries,
					Text = $"{ResourceCatalog.Resolve( ing.ResourceId ).DisplayName} ×{ing.Amount}"
				};
				line.Style.FontColor = new Color( 0.8f, 0.82f, 0.86f );
				line.Style.FontSize = Length.Pixels( 13f );
			}
		}

		_statsEntries?.DeleteChildren();
		if ( def.Stats is not null )
		{
			foreach ( var stat in def.Stats )
			{
				var line = new Label
				{
					Parent = _statsEntries,
					Text = $"{stat.Label}: {stat.Value}"
				};
				line.Style.FontColor = new Color( 0.8f, 0.82f, 0.86f );
				line.Style.FontSize = Length.Pixels( 13f );
			}
		}
	}

	public bool TrySelectRecipeAtScreen( Vector2 screenPos )
	{
		if ( !_menuOpen || !_panelVisible )
			return false;

		for ( var i = 0; i < _rows.Count; i++ )
		{
			var root = _rows[i].Root;
			if ( root is null || !root.IsValid() || !root.IsInside( screenPos ) )
				continue;

			SelectRecipe( _rows[i].Id );
			return true;
		}

		return false;
	}

	public bool TryCraftPointerAtScreen( Vector2 screenPos, bool pressed )
	{
		if ( !_menuOpen || !_panelVisible || _craftButton is null || !_craftButton.IsValid() )
			return false;

		if ( !_craftButton.IsInside( screenPos ) )
		{
			if ( !pressed )
				CancelCraftHold();
			return false;
		}

		if ( pressed )
		{
			BeginCraftHold();
			return true;
		}

		CancelCraftHold();
		return true;
	}

	void BeginCraftHold()
	{
		if ( _craftHoldActive )
			return;

		_craftHoldActive = true;
		_craftHoldCompleted = false;
		_craftHoldElapsed = 0f;
	}

	void CancelCraftHold()
	{
		_craftHoldActive = false;
		_craftHoldCompleted = false;
		_craftHoldElapsed = 0f;
		if ( _craftProgressFill is not null )
			_craftProgressFill.Style.Width = Length.Percent( 0 );
	}

	void AdvanceCraftHold()
	{
		if ( !_craftHoldActive || _craftHoldCompleted )
			return;

		_craftHoldElapsed += Time.Delta;
		var t = Math.Clamp( _craftHoldElapsed / CraftHoldSeconds, 0f, 1f );
		if ( _craftProgressFill is not null )
			_craftProgressFill.Style.Width = Length.Percent( t * 100f );

		if ( t < 1f )
			return;

		_craftHoldCompleted = true;
		_craftHoldActive = false;
		if ( _augments is not null && !string.IsNullOrWhiteSpace( _selectedId ) )
			_augments.OwnerTryCraft( _selectedId );

		if ( _craftProgressFill is not null )
			_craftProgressFill.Style.Width = Length.Percent( 0 );
	}

	public void Refresh()
	{
		if ( AugmentCatalog.ContentVersion != _builtCatalogVersion )
			PopulateRows();
		else
			RefreshDetail();

		RefreshSlotList( _installedUi, i => _augments?.GetInstalled( (AugmentSlot)i ) ?? InventorySlot.Empty );
		RefreshSlotList( _bankUi, i => _augments?.GetBankSlot( i ) ?? InventorySlot.Empty );
		RefreshSlotList( _bagUi, i => _inventory?.GetSlot( i ) ?? InventorySlot.Empty );
		_lastAugmentVersion = _augments?.ContentsVersion ?? -1;
	}

	static void RefreshSlotList( List<SlotUi> list, Func<int, InventorySlot> getter )
	{
		for ( var i = 0; i < list.Count; i++ )
			ResourceCatalog.ApplyStackVisual( list[i].IconPanel, list[i].CountLabel, getter( i ) );
	}

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
		{
			AugmentCatalog.EnsureLoaded();
			Refresh();
		}
		else
			CancelCraftHold();

		UpdateVisibility();
	}

	public void SetPanelVisible( bool visible )
	{
		_panelVisible = visible;
		UpdateVisibility();
	}

	public void TickMenu( bool menuOpen )
	{
		if ( !menuOpen || !_panelVisible )
			return;

		if ( ( _augments?.ContentsVersion ?? -1 ) != _lastAugmentVersion )
			Refresh();

		AdvanceCraftHold();
	}

	public void OnMenuGlobalMouseUp() => CancelCraftHold();

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}

	static SlotUi CreateSlotUi( InventorySlotPanel slotPanel )
	{
		var icon = new Panel { Parent = slotPanel };
		icon.Style.Set( "position", "absolute" );
		icon.Style.Set( "left", "4px" );
		icon.Style.Set( "right", "4px" );
		icon.Style.Set( "top", "4px" );
		icon.Style.Set( "bottom", "4px" );
		icon.Style.Set( "pointer-events", "none" );
		icon.Style.Set( "background-size", "contain" );
		icon.Style.Set( "background-repeat", "no-repeat" );
		icon.Style.Set( "background-position", "center" );

		var count = new Label { Parent = slotPanel, Text = "" };
		count.Style.Set( "position", "absolute" );
		count.Style.Set( "right", "3px" );
		count.Style.Set( "bottom", "1px" );
		count.Style.FontColor = Color.White;
		count.Style.FontSize = Length.Pixels( 12f );
		count.Style.Set( "pointer-events", "none" );

		return new SlotUi( icon, count );
	}

	readonly struct SlotUi
	{
		public Panel IconPanel { get; }
		public Label CountLabel { get; }
		public SlotUi( Panel icon, Label count )
		{
			IconPanel = icon;
			CountLabel = count;
		}
	}

	readonly struct RowUi
	{
		public Panel Root { get; }
		public string Id { get; }
		public RowUi( Panel root, string id )
		{
			Root = root;
			Id = id;
		}
	}
}
