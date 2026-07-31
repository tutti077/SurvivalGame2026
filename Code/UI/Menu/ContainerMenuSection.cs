using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Left-column grid for the currently opened world container (chest, etc.) — mirrors
/// <see cref="InventoryMenuSection"/>. Slots render from the interaction's shared
/// <see cref="ContainerInventoryGridHost"/>, so all click modifiers work unchanged.
/// </summary>
public sealed class ContainerMenuSection : IPlayerMenuSection
{
	public string SectionId => "container";

	readonly PlayerInventoryInteraction _interaction;
	readonly List<SlotUi> _slotUi = new();

	Panel _sectionRoot;
	Label _title;
	Panel _grid;
	bool _menuOpen;
	bool _panelVisible;
	int _builtSlotCount = -1;
	int _builtColumns = -1;
	ContainerInventory _boundContainer;

	public ContainerMenuSection( PlayerInventoryInteraction interaction )
	{
		_interaction = interaction;
	}

	public void Build( Panel menuColumn )
	{
		_sectionRoot = new Panel { Parent = menuColumn };
		_sectionRoot.Style.Set( "position", "relative" );
		_sectionRoot.Style.Set( "z-index", "4" );
		_sectionRoot.Style.Set( "pointer-events", "auto" );
		_sectionRoot.Style.Set( "flex-direction", "column" );
		_sectionRoot.Style.Set( "align-items", "center" );
		_sectionRoot.Style.Set( "gap", $"{8f * InventoryMenuSection.Scale}px" );
		_sectionRoot.Style.Width = Length.Percent( 100 );

		_title = new Label { Parent = _sectionRoot, Text = "Chest" };
		_title.Style.FontColor = Color.White;
		_title.Style.FontSize = Length.Pixels( InventoryMenuSection.TitleFontSize );
		_title.Style.Set( "width", "100%" );
		_title.Style.Set( "text-align", "center" );

		_grid = new Panel { Parent = _sectionRoot };
		_grid.Style.Set( "flex-direction", "column" );
		_grid.Style.Set( "gap", $"{InventoryMenuSection.SlotGap}px" );

		UpdateVisibility();
	}

	/// <summary>Rebind to the opened container (or null on close); rebuilds the grid when dimensions change.</summary>
	public void BindContainer( ContainerInventory container )
	{
		if ( _boundContainer == container )
			return;

		if ( _boundContainer is not null )
			_boundContainer.ContentsChanged -= Refresh;

		_boundContainer = container;

		if ( _boundContainer is not null )
		{
			_boundContainer.ContentsChanged += Refresh;
			_title.Text = string.IsNullOrWhiteSpace( _boundContainer.DisplayName ) ? "Chest" : _boundContainer.DisplayName;
			EnsureGridMatchesContainer();
			Refresh();
		}

		UpdateVisibility();
	}

	void EnsureGridMatchesContainer()
	{
		if ( _grid is null || _boundContainer is null )
			return;

		var slotCount = Math.Max( 1, _boundContainer.SlotCount );
		var columns = Math.Max( 1, _boundContainer.Columns );
		if ( _builtSlotCount == slotCount && _builtColumns == columns )
			return;

		_grid.DeleteChildren( true );
		_slotUi.Clear();

		var gridHost = _interaction?.ContainerGrid;
		var rows = (int)Math.Ceiling( slotCount / (float)columns );
		var gridWidth = columns * InventoryMenuSection.SlotSize + (columns - 1) * InventoryMenuSection.SlotGap;
		_grid.Style.Width = Length.Pixels( gridWidth );
		_grid.Style.Set( "align-self", "center" );

		var slotIndex = 0;
		for ( var row = 0; row < rows; row++ )
		{
			var rowPanel = new Panel { Parent = _grid };
			rowPanel.Style.Set( "flex-direction", "row" );
			rowPanel.Style.Set( "gap", $"{InventoryMenuSection.SlotGap}px" );
			rowPanel.Style.Set( "flex-shrink", "0" );

			for ( var col = 0; col < columns; col++ )
			{
				if ( slotIndex >= slotCount )
					break;

				var slotPanel = new InventorySlotPanel( slotIndex, gridHost, _interaction ) { Parent = rowPanel };
				slotPanel.Style.Width = Length.Pixels( InventoryMenuSection.SlotSize );
				slotPanel.Style.Height = Length.Pixels( InventoryMenuSection.SlotSize );
				slotPanel.Style.Set( "flex-shrink", "0" );
				slotPanel.Style.Set( "flex-grow", "0" );
				slotPanel.Style.Set( "position", "relative" );
				slotPanel.Style.Set( "box-sizing", "border-box" );
				slotPanel.Style.BackgroundColor = new Color( 0.13f, 0.11f, 0.09f, 0.92f );
				slotPanel.Style.Set( "border-width", "1px" );
				slotPanel.Style.Set( "border-color", "#5a5040" );
				slotPanel.Style.Set( "border-radius", "4px" );
				slotPanel.Style.Set( "overflow", "hidden" );
				slotPanel.Style.Set( "pointer-events", "auto" );
				slotPanel.Style.Set( "z-index", "10" );

				_interaction?.RegisterSlot( slotPanel );
				_slotUi.Add( CreateSlotUi( slotPanel ) );
				slotIndex++;
			}
		}

		_builtSlotCount = slotCount;
		_builtColumns = columns;
	}

	public void Refresh()
	{
		var gridHost = _interaction?.ContainerGrid;
		if ( gridHost is null || !gridHost.IsActive )
			return;

		for ( var i = 0; i < _slotUi.Count; i++ )
			ApplySlot( _slotUi[i], gridHost.GetSlot( i ) );
	}

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
		{
			ResourceDefinitionCatalog.EnsureLoaded();
			Refresh();
		}

		UpdateVisibility();
	}

	public void SetPanelVisible( bool visible )
	{
		_panelVisible = visible;
		UpdateVisibility();
	}

	public void TickMenu( bool menuOpen ) { }

	public void OnMenuGlobalMouseUp() { }

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		var show = _menuOpen && _panelVisible && _boundContainer is not null && _boundContainer.IsValid();
		_sectionRoot.Style.Set( "display", show ? "flex" : "none" );
	}

	static SlotUi CreateSlotUi( Panel parent )
	{
		var inset = 4f * InventoryMenuSection.Scale;
		var icon = new Panel { Parent = parent };
		icon.Style.Set( "position", "absolute" );
		icon.Style.Set( "left", $"{inset}px" );
		icon.Style.Set( "top", $"{inset}px" );
		icon.Style.Set( "right", $"{inset}px" );
		icon.Style.Set( "bottom", $"{inset}px" );
		icon.Style.Set( "background-size", "contain" );
		icon.Style.Set( "background-repeat", "no-repeat" );
		icon.Style.Set( "background-position", "center" );
		icon.Style.Set( "display", "none" );
		icon.Style.Set( "pointer-events", "none" );

		var count = new Label { Parent = parent };
		count.Style.Set( "position", "absolute" );
		count.Style.Set( "right", "3px" );
		count.Style.Set( "bottom", "1px" );
		count.Style.Set( "padding-left", "3px" );
		count.Style.Set( "padding-right", "3px" );
		count.Style.Set( "padding-top", "1px" );
		count.Style.Set( "padding-bottom", "1px" );
		count.Style.Set( "background-color", "rgba(0,0,0,0.65)" );
		count.Style.Set( "border-radius", "3px" );
		count.Style.FontColor = Color.White;
		count.Style.FontSize = Length.Pixels( InventoryMenuSection.CountFontSize );
		count.Style.Set( "text-shadow", "1px 1px 2px black" );
		count.Style.Set( "display", "none" );
		count.Style.Set( "pointer-events", "none" );

		return new SlotUi( icon, count );
	}

	void ApplySlot( SlotUi ui, InventorySlot slot )
	{
		var resourceId = slot.IsEmpty ? string.Empty : slot.ResourceId ?? string.Empty;
		var count = slot.IsEmpty ? 0 : slot.Count;

		var iconPath = slot.IsEmpty ? string.Empty : ResourceCatalog.GetIconPath( resourceId );
		if ( ui.LastResourceId == resourceId && ui.LastCount == count && ui.LastIconPath == iconPath )
			return;

		ui.LastResourceId = resourceId;
		ui.LastCount = count;
		ui.LastIconPath = iconPath;
		ResourceCatalog.ApplyStackVisual( ui.IconPanel, ui.CountLabel, slot );
	}

	sealed class SlotUi
	{
		public Panel IconPanel { get; }
		public Label CountLabel { get; }
		public string LastResourceId { get; set; }
		public string LastIconPath { get; set; }
		public int LastCount { get; set; } = -1;

		public SlotUi( Panel iconPanel, Label countLabel )
		{
			IconPanel = iconPanel;
			CountLabel = countLabel;
		}
	}
}
