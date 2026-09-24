using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Full-screen augment station. Left: crafting (info box, Craft, list grouped by socket).
/// Centre: the paper doll — six body parts in two columns around a standing player preview, each
/// with an Enhance button (augment cores) and three sockets; the Augment button (gold coins) under
/// the preview commits pending sockets; the augment bank strip along the bottom. Right: the bag.
/// Every button is a soft-cursor screen-rect hit-test routed through <see cref="TryPressAtScreen"/>.
/// </summary>
public sealed class AugmentStationMenuSection : IPlayerMenuSection
{
	public const float SlotSize = 56f;
	public const float SlotGap = 4f;
	const float HeaderFont = 20f;
	const float BodyFont = 17f;
	const float SmallFont = 13f;
	const float RowHeight = 44f;
	const float RowGap = 4f;
	const float GroupHeaderHeight = 30f;
	const float InfoBoxHeight = 250f;
	const float ButtonHeight = 42f;
	const float CostIconSize = 20f;
	const float WheelPixelsPerNotch = RowHeight * 2f + RowGap * 2f;

	public string SectionId => "augment_station";

	static readonly Color PanelBg = new( 0.07f, 0.08f, 0.10f, 0.92f );
	static readonly Color BoxBg = new( 0.10f, 0.11f, 0.13f, 0.95f );
	static readonly Color TitleColor = Color.White;
	static readonly Color MutedColor = new( 0.72f, 0.74f, 0.78f );
	static readonly Color LabelColor = new( 0.78f, 0.8f, 0.84f );
	static readonly Color CostColor = new( 0.93f, 0.8f, 0.4f );
	static readonly Color SlotLineColor = new( 0.62f, 0.75f, 0.95f );
	static readonly Color ButtonOn = new( 0.22f, 0.45f, 0.28f, 0.95f );
	static readonly Color ButtonOff = new( 0.2f, 0.21f, 0.24f, 0.95f );
	static readonly Color EnhanceOn = new( 0.25f, 0.4f, 0.6f, 0.95f );
	static readonly Color EnhanceDone = new( 0.16f, 0.18f, 0.22f, 0.95f );
	const string BorderIdle = "#474d57";
	const string BorderLocked = "#2a2d33";
	const string BorderPending = "#e0b84a";
	const string BorderActive = "#5ec46a";
	const string BorderSelected = "#6aa0ff";

	// Sketch order: left column Head / Arms / Legs, right column Torso / Hands / Feet.
	static readonly AugmentBodyPart[] LeftParts = { AugmentBodyPart.Head, AugmentBodyPart.Arms, AugmentBodyPart.Legs };
	static readonly AugmentBodyPart[] RightParts = { AugmentBodyPart.Torso, AugmentBodyPart.Hands, AugmentBodyPart.Feet };

	readonly PlayerAugments _augments;
	readonly PlayerInventory _inventory;
	readonly PlayerInventoryInteraction _interaction;
	readonly PlayerAugmentInstalledGridHost _installedHost;
	readonly PlayerAugmentBankGridHost _bankHost;
	readonly PlayerInventoryGridHost _bagHost;

	readonly SocketUi[] _socketUi = new SocketUi[AugmentSlots.Count];
	readonly PartUi[] _partUi = new PartUi[AugmentBodyParts.Count];
	readonly List<SlotUi> _bankUi = new();
	readonly List<SlotUi> _bagUi = new();
	readonly List<RowUi> _rows = new();

	Panel _sectionRoot;
	Panel _craftButton;
	Label _craftLabel;
	Label _detailName;
	Panel _detailLines;
	Panel _detailCostRow;
	Label _detailCostLabel;
	Panel _listViewport;
	Panel _listContent;
	Panel _augmentButton;
	Label _augmentLabel;
	Label _augmentCostLabel;
	Panel _augmentCostIcon;
	AugmentPlayerPreviewPanel _preview;

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

		MakeCostIcon( _detailCostRow, "ui/items/currency_goldCoins.png" );

		_craftButton = MakeButton( col, "Craft", out _craftLabel );
		_craftButton.Style.Set( "width", "100%" );

		var listFrame = new Panel { Parent = col };
		listFrame.Style.Set( "flex-direction", "column" );
		listFrame.Style.Set( "width", "100%" );
		listFrame.Style.Set( "flex-grow", "1" );
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
		_listContent.Style.Set( "right", "0" );
		_listContent.Style.Set( "top", "0" );
		_listContent.Style.Set( "flex-direction", "column" );
		_listContent.Style.Set( "gap", $"{RowGap}px" );
		_listContent.Style.Set( "padding", "6px" );

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

		var tier = new Label { Parent = row, Text = $"T{def.ResolvedTier}" };
		tier.Style.FontColor = MutedColor;
		tier.Style.FontSize = Length.Pixels( SmallFont );
		tier.Style.Set( "margin-left", "auto" );
		tier.Style.PaddingRight = Length.Pixels( 8f );
		tier.Style.Set( "pointer-events", "none" );

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
		_detailCostLabel.Text = AugmentInfo.DescribeInstallCost( def ).Replace( " gold", "" );
		_detailCostRow.Style.Set( "display", "flex" );
		foreach ( var (text, kind) in AugmentInfo.BuildLines( def ) )
		{
			// The gold price has its own coin row under the lines.
			if ( kind == AugmentInfoLineKind.InstallCost )
				continue;

			var line = new Label { Parent = _detailLines, Text = text };
			line.Style.FontColor = kind switch
			{
				AugmentInfoLineKind.Slot => SlotLineColor,
				AugmentInfoLineKind.Cost => CostColor,
				AugmentInfoLineKind.Stat => LabelColor,
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

		_scrollY -= wheel.y * WheelPixelsPerNotch;
		ApplyScroll();
	}

	void ApplyScroll()
	{
		if ( _listContent is null || !_listContent.IsValid() || _listViewport is null )
			return;

		var scale = MathF.Max( 0.001f, _listViewport.ScaleToScreen );
		var viewHeight = _listViewport.Box.Rect.Height / scale;
		var maxScroll = MathF.Max( 0f, _contentHeight - viewHeight );
		_scrollY = Math.Clamp( _scrollY, 0f, maxScroll );
		_listContent.Style.Top = Length.Pixels( -_scrollY );
	}

	// ── Centre: paper doll + bank ───────────────────────────────────────────────────────────

	void BuildStationColumn( Panel parent )
	{
		var col = MakeColumn( parent, "52%" );
		col.Style.Set( "align-items", "center" );

		AddTitle( col, "Augment Station" );

		var body = new Panel { Parent = col };
		body.Style.Set( "flex-direction", "row" );
		body.Style.Set( "width", "100%" );
		body.Style.Set( "flex-grow", "1" );
		body.Style.Set( "gap", "12px" );
		body.Style.Set( "align-items", "stretch" );

		var left = new Panel { Parent = body };
		left.Style.Set( "flex-direction", "column" );
		left.Style.Set( "justify-content", "space-between" );
		left.Style.Set( "flex-grow", "1" );
		left.Style.Set( "align-items", "flex-end" );
		for ( var i = 0; i < LeftParts.Length; i++ )
			BuildPart( left, LeftParts[i] );

		var previewFrame = new Panel { Parent = body };
		previewFrame.Style.Set( "flex-direction", "column" );
		previewFrame.Style.Width = Length.Pixels( 300f );
		previewFrame.Style.Set( "flex-shrink", "0" );
		previewFrame.Style.BackgroundColor = BoxBg;
		previewFrame.Style.Set( "border-radius", "4px" );
		previewFrame.Style.Set( "border-width", "1px" );
		previewFrame.Style.Set( "border-color", BorderIdle );
		previewFrame.Style.Set( "overflow", "hidden" );
		previewFrame.Style.Set( "pointer-events", "none" );

		_preview = new AugmentPlayerPreviewPanel { Parent = previewFrame };
		_preview.Style.Set( "width", "100%" );
		_preview.Style.Set( "height", "100%" );

		var right = new Panel { Parent = body };
		right.Style.Set( "flex-direction", "column" );
		right.Style.Set( "justify-content", "space-between" );
		right.Style.Set( "flex-grow", "1" );
		right.Style.Set( "align-items", "flex-start" );
		for ( var i = 0; i < RightParts.Length; i++ )
			BuildPart( right, RightParts[i] );

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

		_augmentCostIcon = MakeCostIcon( commitRow, "ui/items/currency_goldCoins.png" );

		// Bank strip.
		var bankTitle = new Label { Parent = col, Text = "Augment Bank" };
		bankTitle.Style.FontColor = TitleColor;
		bankTitle.Style.FontSize = Length.Pixels( BodyFont );
		bankTitle.Style.Set( "flex-shrink", "0" );

		var bankGrid = new Panel { Parent = col };
		bankGrid.Style.Set( "flex-direction", "row" );
		bankGrid.Style.Set( "flex-wrap", "wrap" );
		bankGrid.Style.Set( "gap", $"{SlotGap}px" );
		bankGrid.Style.Set( "justify-content", "center" );
		bankGrid.Style.Set( "flex-shrink", "0" );
		bankGrid.Style.Width = Length.Pixels( SlotSize * PlayerAugments.BankColumns + SlotGap * (PlayerAugments.BankColumns - 1) );

		_bankUi.Clear();
		for ( var i = 0; i < PlayerAugments.BankSlotCount; i++ )
		{
			var slotPanel = new InventorySlotPanel( i, _bankHost, _interaction ) { Parent = bankGrid };
			StyleSlot( slotPanel );
			_interaction?.RegisterSlot( slotPanel );
			_bankUi.Add( CreateSlotUi( slotPanel ) );
		}
	}

	void BuildPart( Panel parent, AugmentBodyPart part )
	{
		var block = new Panel { Parent = parent };
		block.Style.Set( "flex-direction", "column" );
		block.Style.Set( "gap", "6px" );
		block.Style.Set( "flex-shrink", "0" );

		var header = new Panel { Parent = block };
		header.Style.Set( "flex-direction", "row" );
		header.Style.Set( "align-items", "center" );
		header.Style.Set( "gap", "8px" );
		header.Style.Height = Length.Pixels( 30f );

		var name = new Label { Parent = header, Text = AugmentBodyParts.Label( part ) };
		name.Style.FontColor = TitleColor;
		name.Style.FontSize = Length.Pixels( BodyFont );
		name.Style.Width = Length.Pixels( 60f );
		name.Style.Set( "pointer-events", "none" );

		var enhance = new Panel { Parent = header };
		enhance.Style.Width = Length.Pixels( 96f );
		enhance.Style.Height = Length.Pixels( 28f );
		enhance.Style.BackgroundColor = EnhanceOn;
		enhance.Style.Set( "border-radius", "4px" );
		enhance.Style.Set( "justify-content", "center" );
		enhance.Style.Set( "align-items", "center" );
		enhance.Style.Set( "pointer-events", "all" );

		var enhanceLabel = new Label { Parent = enhance, Text = "Enhance" };
		enhanceLabel.Style.FontColor = Color.White;
		enhanceLabel.Style.FontSize = Length.Pixels( SmallFont + 1f );
		enhanceLabel.Style.Set( "pointer-events", "none" );

		var cost = new Label { Parent = header, Text = "" };
		cost.Style.FontColor = CostColor;
		cost.Style.FontSize = Length.Pixels( BodyFont );
		cost.Style.Set( "pointer-events", "none" );

		var coreIcon = MakeCostIcon( header, $"ui/items/{AugmentCurrency.CoreResourceId}.png" );

		var socketsRow = new Panel { Parent = block };
		socketsRow.Style.Set( "flex-direction", "row" );
		socketsRow.Style.Set( "gap", $"{SlotGap}px" );

		var slots = AugmentBodyParts.SlotsOf( part );
		for ( var i = 0; i < slots.Length; i++ )
			BuildSocket( socketsRow, slots[i] );

		_partUi[(int)part] = new PartUi( enhance, enhanceLabel, cost, coreIcon );
	}

	void BuildSocket( Panel row, AugmentSlot slot )
	{
		var host = new Panel { Parent = row };
		host.Style.Set( "flex-direction", "column" );
		host.Style.Set( "align-items", "center" );
		host.Style.Set( "gap", "2px" );
		host.Style.Width = Length.Pixels( SlotSize + 8f );

		var slotPanel = new InventorySlotPanel( (int)slot, _installedHost, _interaction ) { Parent = host };
		StyleSlot( slotPanel );
		_interaction?.RegisterSlot( slotPanel );
		var ui = CreateSlotUi( slotPanel );

		var lockOverlay = new Panel { Parent = slotPanel };
		lockOverlay.Style.Set( "position", "absolute" );
		lockOverlay.Style.Set( "left", "0" );
		lockOverlay.Style.Set( "top", "0" );
		lockOverlay.Style.Set( "right", "0" );
		lockOverlay.Style.Set( "bottom", "0" );
		lockOverlay.Style.BackgroundColor = new Color( 0f, 0f, 0f, 0.62f );
		lockOverlay.Style.Set( "justify-content", "center" );
		lockOverlay.Style.Set( "align-items", "center" );
		lockOverlay.Style.Set( "pointer-events", "none" );
		var lockText = new Label { Parent = lockOverlay, Text = "locked" };
		lockText.Style.FontColor = new Color( 0.55f, 0.57f, 0.62f );
		lockText.Style.FontSize = Length.Pixels( 11f );
		lockText.Style.Set( "pointer-events", "none" );

		var label = new Label { Parent = host, Text = AugmentSlots.VariationLabel( slot ) };
		label.Style.FontColor = LabelColor;
		label.Style.FontSize = Length.Pixels( SmallFont - 1f );
		label.Style.Set( "pointer-events", "none" );

		_socketUi[(int)slot] = new SocketUi( slotPanel, ui, lockOverlay );
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
			StyleSlot( slotPanel );
			_interaction?.RegisterSlot( slotPanel );
			_bagUi.Add( CreateSlotUi( slotPanel ) );
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

		for ( var p = 0; p < _partUi.Length; p++ )
		{
			if ( !IsInside( _partUi[p]?.EnhanceButton, screenPos ) )
				continue;

			_augments.OwnerTryEnhance( (AugmentBodyPart)p );
			return true;
		}

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

	static bool IsInside( Panel panel, Vector2 screenPos ) =>
		panel is not null && panel.IsValid() && panel.IsInside( screenPos );

	// ── Refresh ─────────────────────────────────────────────────────────────────────────────

	public void Refresh()
	{
		if ( AugmentCatalog.ContentVersion != _builtCatalogVersion )
			PopulateRows();
		else
			RefreshDetail();

		RefreshSockets();
		RefreshParts();
		RefreshCommitButton();
		RefreshSlotList( _bankUi, i => _augments?.GetBankSlot( i ) ?? InventorySlot.Empty );
		RefreshSlotList( _bagUi, i => _inventory?.GetSlot( i ) ?? InventorySlot.Empty );
		RefreshPreviewOutfit();

		_lastAugmentVersion = _augments?.ContentsVersion ?? -1;
	}

	void RefreshSockets()
	{
		for ( var i = 0; i < _socketUi.Length; i++ )
		{
			var ui = _socketUi[i];
			if ( ui is null )
				continue;

			var slot = (AugmentSlot)i;
			var stack = _augments?.GetInstalled( slot ) ?? InventorySlot.Empty;
			ResourceCatalog.ApplyStackVisual( ui.Slot.IconPanel, ui.Slot.CountLabel, stack );

			var unlocked = _augments?.IsSlotUnlocked( slot ) ?? false;
			ui.LockOverlay.Style.Set( "display", unlocked ? "none" : "flex" );

			var border = !unlocked ? BorderLocked
				: _augments.IsSlotActive( slot ) ? BorderActive
				: _augments.IsSlotPending( slot ) ? BorderPending
				: BorderIdle;
			ui.Root.Style.Set( "border-color", border );
		}
	}

	void RefreshParts()
	{
		for ( var p = 0; p < _partUi.Length; p++ )
		{
			var ui = _partUi[p];
			if ( ui is null )
				continue;

			var part = (AugmentBodyPart)p;
			var cost = _augments?.GetNextEnhanceCoreCost( part ) ?? 0;
			if ( cost <= 0 )
			{
				ui.CostLabel.Text = "full";
				ui.CoreIcon.Style.Set( "display", "none" );
				ui.EnhanceButton.Style.BackgroundColor = EnhanceDone;
				ui.EnhanceLabel.Style.FontColor = MutedColor;
				continue;
			}

			ui.CostLabel.Text = cost.ToString();
			ui.CoreIcon.Style.Set( "display", "flex" );
			var can = _augments?.CanEnhance( part ) ?? false;
			ui.EnhanceButton.Style.BackgroundColor = can ? EnhanceOn : ButtonOff;
			ui.EnhanceLabel.Style.FontColor = can ? Color.White : MutedColor;
		}
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

	void RefreshPreviewOutfit()
	{
		if ( _preview is null || _augments is null )
			return;

		var equipment = _augments.Components.Get<PlayerEquipment>();
		_preview.SetClothing( equipment?.NetworkedWornClothing ?? string.Empty );
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

	static Panel MakeCostIcon( Panel parent, string iconPath )
	{
		var icon = new Panel { Parent = parent };
		icon.Style.Width = Length.Pixels( CostIconSize );
		icon.Style.Height = Length.Pixels( CostIconSize );
		icon.Style.Set( "flex-shrink", "0" );
		icon.Style.Set( "pointer-events", "none" );
		icon.Style.Set( "background-size", "contain" );
		icon.Style.Set( "background-repeat", "no-repeat" );
		icon.Style.Set( "background-position", "center" );
		MenuUiTextures.ApplyBackground( icon, iconPath );
		return icon;
	}

	static void StyleSlot( Panel slotPanel )
	{
		slotPanel.Style.Width = Length.Pixels( SlotSize );
		slotPanel.Style.Height = Length.Pixels( SlotSize );
		slotPanel.Style.Set( "flex-shrink", "0" );
		slotPanel.Style.Set( "position", "relative" );
		slotPanel.Style.Set( "box-sizing", "border-box" );
		slotPanel.Style.BackgroundColor = new Color( 0.1f, 0.11f, 0.13f, 0.95f );
		slotPanel.Style.Set( "border-width", "2px" );
		slotPanel.Style.Set( "border-color", BorderIdle );
		slotPanel.Style.Set( "border-radius", "4px" );
		slotPanel.Style.Set( "overflow", "hidden" );
		slotPanel.Style.Set( "pointer-events", "auto" );
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

	sealed class SocketUi
	{
		public Panel Root { get; }
		public SlotUi Slot { get; }
		public Panel LockOverlay { get; }
		public SocketUi( Panel root, SlotUi slot, Panel lockOverlay )
		{
			Root = root;
			Slot = slot;
			LockOverlay = lockOverlay;
		}
	}

	sealed class PartUi
	{
		public Panel EnhanceButton { get; }
		public Label EnhanceLabel { get; }
		public Label CostLabel { get; }
		public Panel CoreIcon { get; }
		public PartUi( Panel enhanceButton, Label enhanceLabel, Label costLabel, Panel coreIcon )
		{
			EnhanceButton = enhanceButton;
			EnhanceLabel = enhanceLabel;
			CostLabel = costLabel;
			CoreIcon = coreIcon;
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
