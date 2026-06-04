using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>4×N inventory grid section for <see cref="PlayerScreenHud"/>.</summary>
public sealed class InventoryMenuSection : IPlayerMenuSection
{
	public const float SlotSize = 64f;
	public const float SlotGap = 5f;

	public string SectionId => "inventory";

	readonly PlayerInventory _inventory;
	readonly PlayerInventoryInteraction _interaction;
	readonly List<SlotUi> _slotUi = new();

	Panel _sectionRoot;
	Panel _grid;

	public InventoryMenuSection( PlayerInventory inventory, PlayerInventoryInteraction interaction )
	{
		_inventory = inventory;
		_interaction = interaction;
	}

	public void Build( Panel menuColumn )
	{
		_sectionRoot = new Panel { Parent = menuColumn };
		_sectionRoot.Style.Set( "position", "relative" );
		_sectionRoot.Style.Set( "z-index", "1" );
		_sectionRoot.Style.Set( "flex-direction", "column" );
		_sectionRoot.Style.Set( "align-items", "center" );
		_sectionRoot.Style.Set( "gap", "8px" );
		_sectionRoot.Style.Width = Length.Percent( 100 );

		var title = new Label { Parent = _sectionRoot, Text = "Inventory" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( 18f );
		title.Style.Set( "width", "100%" );
		title.Style.Set( "text-align", "center" );

		_grid = new Panel { Parent = _sectionRoot };
		_grid.Style.Set( "flex-direction", "column" );
		_grid.Style.Set( "gap", $"{SlotGap}px" );

		var columns = Math.Max( 1, _inventory?.Columns ?? InventoryDefaults.DefaultColumns );
		var slotCount = Math.Max( 1, _inventory?.SlotCount ?? InventoryDefaults.DefaultSlotCount );
		var rows = (int)Math.Ceiling( slotCount / (float)columns );
		var gridWidth = columns * SlotSize + (columns - 1) * SlotGap;
		_grid.Style.Width = Length.Pixels( gridWidth );
		_grid.Style.Set( "align-self", "center" );

		_slotUi.Clear();
		var slotIndex = 0;
		for ( var row = 0; row < rows; row++ )
		{
			var rowPanel = new Panel { Parent = _grid };
			rowPanel.Style.Set( "flex-direction", "row" );
			rowPanel.Style.Set( "gap", $"{SlotGap}px" );
			rowPanel.Style.Set( "flex-shrink", "0" );

			for ( var col = 0; col < columns; col++ )
			{
				if ( slotIndex >= slotCount )
					break;

				var slotPanel = new InventorySlotPanel( slotIndex, _interaction ) { Parent = rowPanel };
				slotPanel.Style.Width = Length.Pixels( SlotSize );
				slotPanel.Style.Height = Length.Pixels( SlotSize );
				slotPanel.Style.Set( "flex-shrink", "0" );
				slotPanel.Style.Set( "flex-grow", "0" );
				slotPanel.Style.Set( "position", "relative" );
				slotPanel.Style.Set( "box-sizing", "border-box" );
				slotPanel.Style.BackgroundColor = new Color( 0.12f, 0.13f, 0.15f, 0.92f );
				slotPanel.Style.Set( "border-width", "1px" );
				slotPanel.Style.Set( "border-color", "#474d57" );
				slotPanel.Style.Set( "border-radius", "4px" );
				slotPanel.Style.Set( "overflow", "hidden" );
				slotPanel.Style.Set( "pointer-events", "auto" );

				_interaction?.RegisterSlot( slotPanel );
				_slotUi.Add( CreateSlotUi( slotPanel ) );
				slotIndex++;
			}
		}

		Refresh();
	}

	public void Refresh()
	{
		if ( _inventory is null )
			return;

		for ( var i = 0; i < _slotUi.Count; i++ )
			ApplySlot( _slotUi[i], _inventory.GetSlot( i ) );
	}

	public void SetMenuOpen( bool isOpen )
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", isOpen ? "flex" : "none" );
	}

	static SlotUi CreateSlotUi( Panel parent )
	{
		var icon = new Panel { Parent = parent };
		icon.Style.Set( "position", "absolute" );
		icon.Style.Set( "left", "4px" );
		icon.Style.Set( "top", "4px" );
		icon.Style.Set( "right", "4px" );
		icon.Style.Set( "bottom", "4px" );
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
		count.Style.FontSize = Length.Pixels( 13f );
		count.Style.Set( "text-shadow", "1px 1px 2px black" );
		count.Style.Set( "display", "none" );
		count.Style.Set( "pointer-events", "none" );

		return new SlotUi( parent, icon, count );
	}

	void ApplySlot( SlotUi ui, InventorySlot slot )
	{
		var resourceId = slot.IsEmpty ? string.Empty : slot.ResourceId ?? string.Empty;
		var count = slot.IsEmpty ? 0 : slot.Count;

		if ( ui.LastResourceId == resourceId && ui.LastCount == count )
			return;

		ui.LastResourceId = resourceId;
		ui.LastCount = count;
		ResourceCatalog.ApplyStackVisual( ui.IconPanel, ui.CountLabel, slot );
	}

	sealed class SlotUi
	{
		public Panel Root { get; }
		public Panel IconPanel { get; }
		public Label CountLabel { get; }
		public string LastResourceId { get; set; }
		public int LastCount { get; set; } = -1;

		public SlotUi( Panel root, Panel iconPanel, Label countLabel )
		{
			Root = root;
			IconPanel = iconPanel;
			CountLabel = countLabel;
		}
	}
}
