using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Full-screen augment station. Left: crafting (info box, Craft, list grouped by socket).
/// Centre: the interactive paper doll (<see cref="AugmentPaperdollView"/> — Enhance buttons, sockets,
/// player preview), the Augment button (gold coins) that commits pending sockets, and the augment
/// bank strip. Right: the bag. Every button is a soft-cursor screen-rect hit-test routed through
/// <see cref="TryPressAtScreen"/>.
/// </summary>
public sealed class AugmentStationMenuSection : IPlayerMenuSection
{
	public const float SlotSize = AugmentPaperdollView.SlotSize;
	public const float SlotGap = AugmentPaperdollView.SlotGap;
	const float HeaderFont = 20f;
	const float BodyFont = AugmentPaperdollView.BodyFont;
	const float SmallFont = AugmentPaperdollView.SmallFont;
	const float RowHeight = 44f;
	const float RowGap = 4f;
	const float GroupHeaderHeight = 30f;
	const float InfoBoxHeight = 250f;
	const float ButtonHeight = 36f;
	const float WheelPixelsPerNotch = RowHeight * 2f + RowGap * 2f;
	const float ScrollBarWidth = 18f;
	const float MinThumbHeight = 28f;

	public string SectionId => "augment_station";

	static readonly Color PanelBg = new( 0.07f, 0.08f, 0.10f, 0.92f );
	static readonly Color BoxBg = AugmentPaperdollView.BoxBg;
	static readonly Color TitleColor = AugmentPaperdollView.TitleColor;
	static readonly Color MutedColor = AugmentPaperdollView.MutedColor;
	static readonly Color LabelColor = AugmentPaperdollView.LabelColor;
	static readonly Color CostColor = AugmentPaperdollView.CostColor;
	static readonly Color SlotLineColor = new( 0.62f, 0.75f, 0.95f );
	static readonly Color ButtonOn = new( 0.22f, 0.45f, 0.28f, 0.95f );
	static readonly Color ButtonOff = AugmentPaperdollView.ButtonOff;
	const string BorderIdle = AugmentPaperdollView.BorderIdle;
	const string BorderSelected = "#6aa0ff";

	readonly PlayerAugments _augments;
	readonly PlayerInventory _inventory;
	readonly PlayerInventoryInteraction _interaction;
	readonly PlayerAugmentInstalledGridHost _installedHost;
	readonly PlayerAugmentBankGridHost _bankHost;
	readonly PlayerInventoryGridHost _bagHost;
	readonly AugmentPaperdollView _doll;

	readonly List<AugmentPaperdollView.SlotUi> _bankUi = new();
	readonly List<AugmentPaperdollView.SlotUi> _bagUi = new();
	readonly List<RowUi> _rows = new();
	readonly List<Panel> _bankSlotPanels = new();
	readonly List<Panel> _bankRows = new();

	// Column fit: what sits under the doll at 1× (scales with it) and what never scales.
	const float BankRowCount = 2f;
	const float BankTitleFont = BodyFont;
	const float CommitButtonWidth = 220f;
	const float ExtraScalableNominal = ButtonHeight + BankTitleFont + 6f + BankRowCount * SlotSize + (BankRowCount - 1f) * SlotGap;
	const float FixedColumnHeight = HeaderFont + 8f + 5f * 8f + 20f;

	Panel _stationColumn;
	Label _bankTitle;

	Panel _sectionRoot;
	Panel _craftButton;
	Label _craftLabel;
	Label _detailName;
	Panel _detailLines;
	Panel _detailCostRow;
	Label _detailCostLabel;
	Panel _listViewport;
	Panel _listContent;
	Panel _scrollTrack;
	Panel _scrollThumb;
	bool _draggingThumb;
	float _dragStartMouseY;
	float _dragStartScrollY;
	float _lastThumbTop = -1f;
	float _lastThumbHeight = -1f;
	Panel _augmentButton;
	Label _augmentLabel;
	Label _augmentCostLabel;
	Panel _augmentCostIcon;

	string _selectedId;
	bool _menuOpen;
	bool _panelVisible;
	int _builtCatalogVersion = -1;
	int _lastAugmentVersion = -1;
	float _scrollY;
	float _contentHeight;

	public AugmentStationMenuSection(
		PlayerAugments augments,
		PlayerInventory inventory,
		PlayerInventoryInteraction interaction )
	{
		_augments = augments;
		_inventory = inventory;
		_interaction = interaction;
		_installedHost = augments is not null && inventory is not null
			? new PlayerAugmentInstalledGridHost( augments, inventory, readOnly: false )
			: null;
		_bankHost = augments is not null && inventory is not null
			? new PlayerAugmentBankGridHost( augments, inventory )
			: null;
		_bagHost = inventory is not null ? new PlayerInventoryGridHost( "player", inventory ) : null;
		_doll = new AugmentPaperdollView( augments, _installedHost, interaction, interactive: true );

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
		BuildStationColumn( _sectionRoot );
		BuildBagColumn( _sectionRoot );

		Refresh();
		UpdateVisibility();
	}

	// ── Left: crafting ──────────────────────────────────────────────────────────────────────

	void BuildCraftColumn( Panel parent )
	{
		var col = MakeColumn( parent, "24%" );

		AddTitle( col, "Augment Crafting" );

		var info = new Panel { Parent = col };
		info.Style.Set( "flex-direction", "column" );
		info.Style.Set( "width", "100%" );
		info.Style.Height = Length.Pixels( InfoBoxHeight );
		info.Style.Set( "flex-shrink", "0" );
		info.Style.Set( "overflow", "hidden" );
		info.Style.Set( "gap", "4px" );
		info.Style.Set( "padding", "10px" );
		info.Style.BackgroundColor = BoxBg;
		info.Style.Set( "border-radius", "4px" );
		info.Style.Set( "border-width", "1px" );
		info.Style.Set( "border-color", BorderIdle );

		_detailName = new Label { Parent = info, Text = "Select an augment" };
		_detailName.Style.FontColor = TitleColor;
		_detailName.Style.FontSize = Length.Pixels( BodyFont + 2f );
		_detailName.Style.Set( "pointer-events", "none" );

		_detailLines = new Panel { Parent = info };
		_detailLines.Style.Set( "flex-direction", "column" );
		_detailLines.Style.Set( "gap", "3px" );
		_detailLines.Style.Set( "flex-grow", "1" );
		_detailLines.Style.Set( "overflow", "hidden" );
		_detailLines.Style.Set( "pointer-events", "none" );

		// Augment (gold) cost pinned to the bottom of the box: "Augment cost  150 [coin]".
		_detailCostRow = new Panel { Parent = info };
		_detailCostRow.Style.Set( "flex-direction", "row" );
		_detailCostRow.Style.Set( "align-items", "center" );
		_detailCostRow.Style.Set( "gap", "8px" );
		_detailCostRow.Style.Set( "flex-shrink", "0" );
		_detailCostRow.Style.Height = Length.Pixels( 26f );
		_detailCostRow.Style.Set( "border-top-width", "1px" );
		_detailCostRow.Style.Set( "border-color", "#3a404a" );
		_detailCostRow.Style.Set( "pointer-events", "none" );

		var costTitle = new Label { Parent = _detailCostRow, Text = "Augment cost" };
		costTitle.Style.FontColor = LabelColor;
		costTitle.Style.FontSize = Length.Pixels( BodyFont - 2f );
		costTitle.Style.Set( "pointer-events", "none" );

		_detailCostLabel = new Label { Parent = _detailCostRow, Text = "" };
		_detailCostLabel.Style.FontColor = CostColor;
		_detailCostLabel.Style.FontSize = Length.Pixels( BodyFont );
		_detailCostLabel.Style.Set( "margin-left", "auto" );
		_detailCostLabel.Style.Set( "pointer-events", "none" );

		AugmentPaperdollView.MakeCostIcon( _detailCostRow, "ui/items/currency_goldCoins.png" );

		_craftButton = MakeButton( col, "Craft", out _craftLabel );
		_craftButton.Style.Set( "width", "100%" );

		var listFrame = new Panel { Parent = col };
		listFrame.Style.Set( "flex-direction", "column" );
		listFrame.Style.Set( "width", "100%" );
		listFrame.Style.Set( "flex-grow", "1" );
		listFrame.Style.Set( "flex-shrink", "1" );
		listFrame.Style.Set( "min-height", "0" );
		listFrame.Style.Set( "overflow", "hidden" );
		listFrame.Style.Set( "position", "relative" );
		listFrame.Style.BackgroundColor = BoxBg;
		listFrame.Style.Set( "border-radius", "4px" );
		listFrame.Style.Set( "border-width", "1px" );
		listFrame.Style.Set( "border-color", BorderIdle );
		_listViewport = listFrame;

		_listContent = new Panel { Parent = listFrame };
		_listContent.Style.Set( "position", "absolute" );
		_listContent.Style.Set( "left", "0" );
		_listContent.Style.Set( "right", $"{ScrollBarWidth + 2f}px" );
		_listContent.Style.Set( "top", "0" );
		_listContent.Style.Set( "flex-direction", "column" );
		_listContent.Style.Set( "gap", $"{RowGap}px" );
		_listContent.Style.Set( "padding", "6px" );

		// Scrollbar: soft-cursor rect hit-tests (TryHandleScrollbarPointer), thumb follows the wheel too.
		_scrollTrack = new Panel { Parent = listFrame };
		_scrollTrack.Style.Set( "position", "absolute" );
		_scrollTrack.Style.Set( "top", "0" );
		_scrollTrack.Style.Set( "bottom", "0" );
		_scrollTrack.Style.Set( "right", "0" );
		_scrollTrack.Style.Width = Length.Pixels( ScrollBarWidth );
		_scrollTrack.Style.BackgroundColor = new Color( 0.08f, 0.09f, 0.11f, 0.95f );
		_scrollTrack.Style.Set( "border-radius", "4px" );
		_scrollTrack.Style.Set( "pointer-events", "none" );

		_scrollThumb = new Panel { Parent = _scrollTrack };
		_scrollThumb.Style.Set( "position", "absolute" );
		_scrollThumb.Style.Set( "left", "2px" );
		_scrollThumb.Style.Set( "right", "2px" );
		_scrollThumb.Style.Set( "top", "0px" );
		_scrollThumb.Style.Height = Length.Pixels( MinThumbHeight );
		_scrollThumb.Style.BackgroundColor = new Color( 0.55f, 0.60f, 0.68f, 0.95f );
		_scrollThumb.Style.Set( "border-radius", "3px" );
		_scrollThumb.Style.Set( "pointer-events", "none" );

		PopulateRows();
	}

	void PopulateRows()
	{
		if ( _listContent is null || !_listContent.IsValid() )
			return;

		AugmentCatalog.EnsureLoaded();
		_listContent.DeleteChildren();
		_rows.Clear();
		_contentHeight = 12f;

		// One group per socket, in socket order — "Head - Eye", "Head - Jaw", …
		for ( var s = 0; s < AugmentSlots.Count; s++ )
		{
			var slot = (AugmentSlot)s;
			var any = false;
			foreach ( var def in AugmentCatalog.All )
			{
				if ( def is null || string.IsNullOrWhiteSpace( def.Id ) || !def.IsUnlockedByDefault )
					continue;

				if ( !def.TryGetPrimarySlot( out var primary ) || primary != slot )
					continue;

				if ( !any )
				{
					any = true;
					AddGroupHeader( AugmentSlots.Label( slot ) );
				}

				AddRow( def );
			}
		}

		_builtCatalogVersion = AugmentCatalog.ContentVersion;
		_scrollY = 0f;
		ApplyScroll();

		if ( _rows.Count > 0 )
			SelectRecipe( string.IsNullOrWhiteSpace( _selectedId ) ? _rows[0].Id : _selectedId );
		else
			RefreshDetail();
	}

	void AddGroupHeader( string text )
	{
		var header = new Label { Parent = _listContent, Text = text };
		header.Style.FontColor = TitleColor;
		header.Style.FontSize = Length.Pixels( BodyFont );
		header.Style.Height = Length.Pixels( GroupHeaderHeight );
		header.Style.Set( "width", "100%" );
		header.Style.Set( "flex-shrink", "0" );
		header.Style.Set( "border-bottom-width", "1px" );
		header.Style.Set( "border-color", "#5a6170" );
		header.Style.Set( "pointer-events", "none" );
		_contentHeight += GroupHeaderHeight + RowGap;
	}

	void AddRow( AugmentDefinition def )
	{
		var row = new Panel { Parent = _listContent };
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "align-items", "center" );
		row.Style.Set( "gap", "8px" );
		row.Style.Height = Length.Pixels( RowHeight );
		row.Style.Set( "width", "100%" );
		row.Style.Set( "flex-shrink", "0" );
		row.Style.Set( "box-sizing", "border-box" );
		row.Style.BackgroundColor = new Color( 0.13f, 0.14f, 0.17f, 0.9f );
		row.Style.Set( "border-radius", "3px" );
		row.Style.Set( "border-width", "1px" );
		row.Style.Set( "border-color", "transparent" );
		row.Style.PaddingLeft = Length.Pixels( 6f );
		row.Style.Set( "pointer-events", "all" );

		var icon = new Panel { Parent = row };
		icon.Style.Width = Length.Pixels( 30f );
		icon.Style.Height = Length.Pixels( 30f );
		icon.Style.Set( "flex-shrink", "0" );
		icon.Style.Set( "pointer-events", "none" );
		MenuUiTextures.ApplyBackground( icon, def.Icon );

		var name = new Label { Parent = row, Text = def.DisplayName };
		name.Style.FontColor = TitleColor;
		name.Style.FontSize = Length.Pixels( BodyFont );
		name.Style.Set( "pointer-events", "none" );

		_rows.Add( new RowUi( row, def.Id ) );
		_contentHeight += RowHeight + RowGap;
	}

	public void SelectRecipe( string id )
	{
		_selectedId = id;
		for ( var i = 0; i < _rows.Count; i++ )
		{
			var selected = string.Equals( _rows[i].Id, id, StringComparison.OrdinalIgnoreCase );
			_rows[i].Root.Style.Set( "border-color", selected ? BorderSelected : "transparent" );
		}

		RefreshDetail();
	}

	void RefreshDetail()
	{
		if ( _detailName is null || _detailLines is null )
			return;

		_detailLines.DeleteChildren();
		if ( !AugmentCatalog.TryGet( _selectedId, out var def ) )
		{
			_detailName.Text = "Select an augment";
			_detailCostLabel.Text = "";
			_detailCostRow.Style.Set( "display", "none" );
			SetButtonState( _craftButton, _craftLabel, false, ButtonOn );
			return;
		}

		_detailName.Text = def.DisplayName;
		_detailCostLabel.Text = GameHacks.FreeAugments ? "free" : AugmentInfo.DescribeInstallCost( def ).Replace( " gold", "" );
		_detailCostRow.Style.Set( "display", "flex" );
		foreach ( var (text, kind) in AugmentInfo.BuildLines( def ) )
		{
			// The gold price has its own coin row under the lines.
			if ( kind == AugmentInfoLineKind.InstallCost )
				continue;

			var line = new Label { Parent = _detailLines, Text = text };
			line.Style.FontColor = kind switch
			{
				AugmentInfoLineKind.Slot or AugmentInfoLineKind.Activation => SlotLineColor,
				AugmentInfoLineKind.Cost => CostColor,
				AugmentInfoLineKind.Stat => LabelColor,
				AugmentInfoLineKind.Warning => new Color( 0.95f, 0.45f, 0.4f ),
				_ => MutedColor,
			};
			line.Style.FontSize = Length.Pixels( kind == AugmentInfoLineKind.Description ? BodyFont - 2f : BodyFont - 3f );
			line.Style.Set( "white-space", "normal" );
			line.Style.Set( "pointer-events", "none" );
		}

		SetButtonState( _craftButton, _craftLabel, _augments?.CanCraft( def.Id ) ?? false, ButtonOn );
	}

	/// <summary>Wheel over the station: scroll the augment list.</summary>
	public void ApplyListWheel( Vector2 wheel )
	{
		if ( !_menuOpen || !_panelVisible || MathF.Abs( wheel.y ) < 1e-4f )
			return;

		// Panel convention: positive wheel = scroll down = larger offset (same as the crafting list).
		_scrollY += wheel.y * WheelPixelsPerNotch;
		ApplyScroll();
	}

	void ApplyScroll()
	{
		if ( _listContent is null || !_listContent.IsValid() || _listViewport is null )
			return;

		_scrollY = Math.Clamp( _scrollY, 0f, MaxScrollY );
		_listContent.Style.Top = Length.Pixels( -_scrollY );
		UpdateScrollbarVisual();
	}

	float ViewHeight
	{
		get
		{
			if ( _listViewport is null || !_listViewport.IsValid() )
				return 0f;

			var scale = MathF.Max( 0.001f, _listViewport.ScaleToScreen );
			return _listViewport.Box.Rect.Height / scale;
		}
	}

	/// <summary>Laid-out content height (padding included) once the panel has a box; the nominal row sum before that.</summary>
	float ContentHeight
	{
		get
		{
			if ( _listContent is null || !_listContent.IsValid() )
				return _contentHeight;

			var scale = MathF.Max( 0.001f, _listContent.ScaleToScreen );
			var measured = _listContent.Box.Rect.Height / scale;
			return measured > 1f ? measured : _contentHeight;
		}
	}

	float MaxScrollY => MathF.Max( 0f, ContentHeight - ViewHeight );

	// ── Scrollbar (soft-cursor: track / thumb are hit-tested by rect, not by panel events) ──

	/// <summary>Overlay Attack1 routed here while the station is open: press to jump / drag the thumb, release to end.</summary>
	public bool TryHandleScrollbarPointer( Vector2 screenPos, bool pressed )
	{
		if ( !pressed )
		{
			if ( !_draggingThumb )
				return false;

			_draggingThumb = false;
			return true;
		}

		if ( _draggingThumb )
		{
			UpdateDragFromScreenY( screenPos.y );
			return true;
		}

		if ( !_menuOpen || !_panelVisible || MaxScrollY <= 1f || !IsInside( _scrollTrack, screenPos ) )
			return false;

		if ( !IsInside( _scrollThumb, screenPos ) )
			JumpToTrackAtScreenY( screenPos.y );

		_draggingThumb = true;
		_dragStartMouseY = screenPos.y;
		_dragStartScrollY = _scrollY;
		return true;
	}

	float ThumbHeight
	{
		get
		{
			var view = ViewHeight;
			var content = ContentHeight;
			if ( view <= 1f || content <= view )
				return view;

			return MathF.Max( MinThumbHeight, view * (view / content) );
		}
	}

	void UpdateDragFromScreenY( float screenY )
	{
		var scale = _listViewport is not null && _listViewport.IsValid() ? MathF.Max( 0.001f, _listViewport.ScaleToScreen ) : 1f;
		var travel = MathF.Max( 1f, ViewHeight - ThumbHeight );
		var deltaStyle = (screenY - _dragStartMouseY) / scale;
		_scrollY = _dragStartScrollY + deltaStyle / travel * MaxScrollY;
		ApplyScroll();
	}

	void JumpToTrackAtScreenY( float screenY )
	{
		if ( _scrollTrack is null || !_scrollTrack.IsValid() )
			return;

		var scale = MathF.Max( 0.001f, _scrollTrack.ScaleToScreen );
		var rect = _scrollTrack.Box.Rect;
		var localY = (screenY - rect.Top) / scale - ThumbHeight * 0.5f;
		var travel = MathF.Max( 1f, ViewHeight - ThumbHeight );
		_scrollY = Math.Clamp( localY / travel, 0f, 1f ) * MaxScrollY;
		ApplyScroll();
	}

	/// <summary>Cheap per-frame sync while the page is open — heights are only known after layout.</summary>
	void UpdateScrollbarVisual()
	{
		if ( _scrollTrack is null || !_scrollTrack.IsValid() || _scrollThumb is null || !_scrollThumb.IsValid() )
			return;

		var maxY = MaxScrollY;
		var canScroll = maxY > 1f;
		var thumbH = ThumbHeight;
		var travel = MathF.Max( 0f, ViewHeight - thumbH );
		var t = canScroll ? Math.Clamp( _scrollY / maxY, 0f, 1f ) : 0f;
		var thumbTop = t * travel;

		if ( MathF.Abs( thumbTop - _lastThumbTop ) < 0.25f && MathF.Abs( thumbH - _lastThumbHeight ) < 0.25f )
			return;

		_lastThumbTop = thumbTop;
		_lastThumbHeight = thumbH;
		_scrollTrack.Style.Set( "opacity", canScroll ? "1" : "0.35" );
		_scrollThumb.Style.Height = Length.Pixels( thumbH );
		_scrollThumb.Style.Set( "top", $"{thumbTop:0.##}px" );
	}

	// ── Centre: paper doll + bank ───────────────────────────────────────────────────────────

	void BuildStationColumn( Panel parent )
	{
		var col = MakeColumn( parent, "52%" );
		col.Style.Set( "align-items", "center" );
		_stationColumn = col;

		AddTitle( col, "Augment Station" );

		_doll.Build( col );
		_doll.ScaleApplied += ApplyExtraScale;

		// Augment (commit) button + its gold cost readout.
		var commitRow = new Panel { Parent = col };
		commitRow.Style.Set( "flex-direction", "row" );
		commitRow.Style.Set( "align-items", "center" );
		commitRow.Style.Set( "gap", "10px" );
		commitRow.Style.Set( "flex-shrink", "0" );

		_augmentButton = MakeButton( commitRow, "Augment", out _augmentLabel );
		_augmentButton.Style.Width = Length.Pixels( 220f );

		_augmentCostLabel = new Label { Parent = commitRow, Text = "" };
		_augmentCostLabel.Style.FontColor = CostColor;
		_augmentCostLabel.Style.FontSize = Length.Pixels( BodyFont );
		_augmentCostLabel.Style.Set( "pointer-events", "none" );

		_augmentCostIcon = AugmentPaperdollView.MakeCostIcon( commitRow, "ui/items/currency_goldCoins.png" );

		// Bank: fixed 2 × 8 rows (no wrap — a wrapped grid left a stray pair eating the column), scaled with the doll.
		_bankTitle = new Label { Parent = col, Text = "Augment Bank" };
		_bankTitle.Style.FontColor = TitleColor;
		_bankTitle.Style.FontSize = Length.Pixels( BankTitleFont );
		_bankTitle.Style.Set( "flex-shrink", "0" );
		_bankTitle.Style.Set( "pointer-events", "none" );

		var bankGrid = new Panel { Parent = col };
		bankGrid.Style.Set( "flex-direction", "column" );
		bankGrid.Style.Set( "gap", $"{SlotGap}px" );
		bankGrid.Style.Set( "align-items", "center" );
		bankGrid.Style.Set( "flex-shrink", "0" );

		_bankUi.Clear();
		_bankSlotPanels.Clear();
		_bankRows.Clear();
		Panel row = null;
		for ( var i = 0; i < PlayerAugments.BankSlotCount; i++ )
		{
			if ( i % PlayerAugments.BankColumns == 0 )
			{
				row = new Panel { Parent = bankGrid };
				row.Style.Set( "flex-direction", "row" );
				row.Style.Set( "gap", $"{SlotGap}px" );
				row.Style.Set( "flex-shrink", "0" );
				_bankRows.Add( row );
			}

			var slotPanel = new InventorySlotPanel( i, _bankHost, _interaction ) { Parent = row };
			AugmentPaperdollView.StyleSlot( slotPanel );
			_interaction?.RegisterSlot( slotPanel );
			_bankSlotPanels.Add( slotPanel );
			_bankUi.Add( AugmentPaperdollView.CreateSlotUi( slotPanel ) );
		}
	}

	/// <summary>Doll fit changed: the Augment button, bank title and bank slots follow the same scale.</summary>
	void ApplyExtraScale( float s )
	{
		if ( _augmentButton is not null && _augmentButton.IsValid() )
		{
			_augmentButton.Style.Width = Length.Pixels( CommitButtonWidth * s );
			_augmentButton.Style.Height = Length.Pixels( ButtonHeight * s );
		}

		if ( _augmentLabel is not null && _augmentLabel.IsValid() )
			_augmentLabel.Style.FontSize = Length.Pixels( BodyFont * s );

		if ( _augmentCostLabel is not null && _augmentCostLabel.IsValid() )
			_augmentCostLabel.Style.FontSize = Length.Pixels( BodyFont * s );

		if ( _bankTitle is not null && _bankTitle.IsValid() )
			_bankTitle.Style.FontSize = Length.Pixels( BankTitleFont * s );

		for ( var i = 0; i < _bankSlotPanels.Count; i++ )
		{
			var slot = _bankSlotPanels[i];
			if ( slot is null || !slot.IsValid() )
				continue;

			slot.Style.Width = Length.Pixels( SlotSize * s );
			slot.Style.Height = Length.Pixels( SlotSize * s );
		}

		for ( var i = 0; i < _bankRows.Count; i++ )
		{
			if ( _bankRows[i] is { } bankRow && bankRow.IsValid() )
				bankRow.Style.Set( "gap", $"{SlotGap * s:0.#}px" );
		}
	}

	// ── Right: bag ──────────────────────────────────────────────────────────────────────────

	void BuildBagColumn( Panel parent )
	{
		var col = MakeColumn( parent, "24%" );
		col.Style.Set( "align-items", "center" );

		AddTitle( col, "Inventory" );

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
			AugmentPaperdollView.StyleSlot( slotPanel );
			_interaction?.RegisterSlot( slotPanel );
			_bagUi.Add( AugmentPaperdollView.CreateSlotUi( slotPanel ) );
		}
	}

	// ── Input ───────────────────────────────────────────────────────────────────────────────

	/// <summary>Soft-cursor press anywhere on the page: buttons and list rows. False = not ours (slots handle themselves).</summary>
	public bool TryPressAtScreen( Vector2 screenPos )
	{
		if ( !_menuOpen || !_panelVisible || _augments is null )
			return false;

		if ( IsInside( _craftButton, screenPos ) )
		{
			if ( !string.IsNullOrWhiteSpace( _selectedId ) )
				_augments.OwnerTryCraft( _selectedId );
			return true;
		}

		if ( IsInside( _augmentButton, screenPos ) )
		{
			_augments.OwnerTryCommitAugments();
			return true;
		}

		if ( _doll.TryPressEnhanceAtScreen( screenPos ) )
			return true;

		// The scrollbar is the overlay's next handler — leave its presses alone.
		if ( IsInside( _scrollTrack, screenPos ) )
			return false;

		// Rows scrolled out of the list frame are clipped — never clickable.
		if ( IsInside( _listViewport, screenPos ) )
		{
			for ( var i = 0; i < _rows.Count; i++ )
			{
				if ( !IsInside( _rows[i].Root, screenPos ) )
					continue;

				SelectRecipe( _rows[i].Id );
				return true;
			}

			return true;
		}

		return false;
	}

	/// <summary>Overlay page drag (pointer + Attack1 held): click-drag on the preview spins the body.</summary>
	public void TickPointerDrag( Vector2 screenPos, bool held )
	{
		if ( !_menuOpen || !_panelVisible )
			return;

		_doll.TickPointerDrag( screenPos, held );
	}

	static bool IsInside( Panel panel, Vector2 screenPos ) =>
		panel is not null && panel.IsValid() && panel.IsInside( screenPos );

	// ── Refresh ─────────────────────────────────────────────────────────────────────────────

	public void Refresh()
	{
		if ( AugmentCatalog.ContentVersion != _builtCatalogVersion )
			PopulateRows();
		else
			RefreshDetail();

		_doll.Refresh();
		RefreshCommitButton();
		RefreshSlotList( _bankUi, i => _augments?.GetBankSlot( i ) ?? InventorySlot.Empty );
		RefreshSlotList( _bagUi, i => _inventory?.GetSlot( i ) ?? InventorySlot.Empty );

		_lastAugmentVersion = _augments?.ContentsVersion ?? -1;
	}

	void RefreshCommitButton()
	{
		if ( _augmentButton is null || _augments is null )
			return;

		var pending = _augments.HasPendingInstalls();
		var cost = _augments.ComputePendingGoldCost();
		_augmentCostLabel.Text = pending ? cost.ToString() : "";
		_augmentCostIcon.Style.Set( "display", pending ? "flex" : "none" );
		SetButtonState( _augmentButton, _augmentLabel, _augments.CanCommitAugments(), ButtonOn );
	}

	static void RefreshSlotList( List<AugmentPaperdollView.SlotUi> list, Func<int, InventorySlot> getter )
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

		// Bag changes arrive through the HUD's InventoryChanged refresh; this catches socket / bank / enhance edits.
		if ( (_augments?.ContentsVersion ?? -1) != _lastAugmentVersion )
			Refresh();

		_doll.TickLayout( _stationColumn, ExtraScalableNominal, FixedColumnHeight );
		UpdateScrollbarVisual();
	}

	public void OnMenuGlobalMouseUp() { }

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}

	// ── Widgets ─────────────────────────────────────────────────────────────────────────────

	static Panel MakeColumn( Panel parent, string width )
	{
		var col = new Panel { Parent = parent };
		col.Style.Set( "flex-direction", "column" );
		col.Style.Set( "width", width );
		col.Style.Set( "height", "100%" );
		// Padding inside the 100% box — otherwise the column runs 20 px past the page and clips its bottom.
		col.Style.Set( "box-sizing", "border-box" );
		col.Style.Set( "gap", "8px" );
		col.Style.Set( "overflow", "hidden" );
		col.Style.Set( "padding", "10px" );
		col.Style.BackgroundColor = PanelBg;
		col.Style.Set( "border-radius", "6px" );
		return col;
	}

	static void AddTitle( Panel parent, string text )
	{
		var title = new Label { Parent = parent, Text = text };
		title.Style.FontColor = TitleColor;
		title.Style.FontSize = Length.Pixels( HeaderFont );
		title.Style.Set( "width", "100%" );
		title.Style.Set( "text-align", "center" );
		title.Style.Set( "flex-shrink", "0" );
		title.Style.Set( "pointer-events", "none" );
	}

	static Panel MakeButton( Panel parent, string text, out Label label )
	{
		var button = new Panel { Parent = parent };
		button.Style.Height = Length.Pixels( ButtonHeight );
		button.Style.BackgroundColor = ButtonOn;
		button.Style.Set( "border-radius", "4px" );
		button.Style.Set( "justify-content", "center" );
		button.Style.Set( "align-items", "center" );
		button.Style.Set( "flex-shrink", "0" );
		button.Style.Set( "pointer-events", "all" );

		label = new Label { Parent = button, Text = text };
		label.Style.FontColor = Color.White;
		label.Style.FontSize = Length.Pixels( BodyFont );
		label.Style.Set( "pointer-events", "none" );
		return button;
	}

	static void SetButtonState( Panel button, Label label, bool enabled, Color onColor )
	{
		if ( button is null || !button.IsValid() )
			return;

		button.Style.BackgroundColor = enabled ? onColor : ButtonOff;
		if ( label is not null )
			label.Style.FontColor = enabled ? Color.White : MutedColor;
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
