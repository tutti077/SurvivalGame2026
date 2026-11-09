using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>4×N inventory grid section for <see cref="PlayerScreenHud"/>.</summary>
public sealed class InventoryMenuSection : IPlayerMenuSection
{
	public const float Scale = 1.35f;
	public const float SlotSize = 64f * Scale;
	public const float SlotGap = 5f * Scale;
	public const float TitleFontSize = CraftingMenuSection.CraftingTitleFontSize;
	public const float CountFontSize = CraftingMenuSection.SectionEntryFontSize;

	public string SectionId => "inventory";

	readonly PlayerInventory _inventory;
	readonly PlayerInventoryInteraction _interaction;
	readonly PlayerInventoryGridHost _gridHost;
	readonly List<SlotUi> _slotUi = new();

	Panel _sectionRoot;
	Panel _grid;
	bool _menuOpen;

	public InventoryMenuSection( PlayerInventory inventory, PlayerInventoryInteraction interaction )
	{
		_inventory = inventory;
		_interaction = interaction;
		_gridHost = inventory is not null ? new PlayerInventoryGridHost( "player", inventory ) : null;
	}

	public void Build( Panel menuColumn )
	{
		_sectionRoot = new Panel { Parent = menuColumn };
		_sectionRoot.Style.Set( "position", "relative" );
		_sectionRoot.Style.Set( "z-index", "4" );
		_sectionRoot.Style.Set( "pointer-events", "auto" );
		_sectionRoot.Style.Set( "flex-direction", "column" );
		_sectionRoot.Style.Set( "align-items", "center" );
		_sectionRoot.Style.Set( "gap", $"{8f * Scale}px" );
		_sectionRoot.Style.Width = Length.Percent( 100 );

		UiStyle.AddTitle( _sectionRoot, "Inventory", TitleFontSize );

		_grid = new Panel { Parent = _sectionRoot };
		_grid.Style.Set( "flex-direction", "column" );
		_grid.Style.Set( "gap", $"{SlotGap}px" );

		var columns = Math.Max( 1, _inventory?.Columns ?? InventoryDefaults.DefaultColumns );
		var slotCount = Math.Max( 1, _inventory?.SlotCount ?? InventoryDefaults.DefaultSlotCount );
		var rows = (int)Math.Ceiling( slotCount / (float)columns );
		var gridWidth = columns * SlotSize + (columns - 1) * SlotGap;
		_grid.Style.Width = Length.Pixels( gridWidth );
		_grid.Style.Set( "align-self", "center" );

		var dropZone = new InventoryPlayerDropZonePanel( _interaction ) { Parent = _sectionRoot };
		dropZone.Style.Width = Length.Pixels( gridWidth );
		dropZone.Style.Height = Length.Pixels( 52f * Scale );
		dropZone.Style.Set( "flex-shrink", "0" );
		dropZone.Style.Set( "align-self", "center" );
		dropZone.Style.Set( "justify-content", "center" );
		dropZone.Style.Set( "align-items", "center" );
		dropZone.Style.Set( "border-width", UiStyle.Px( UiTheme.Metrics.BorderWidthStrong ) );
		dropZone.Style.Set( "border-style", "dashed" );
		dropZone.Style.Set( "border-radius", UiStyle.Px( UiTheme.Metrics.Radius ) );
		dropZone.Style.Set( "pointer-events", "auto" );
		dropZone.Style.Set( "display", "none" );
		dropZone.SetHighlighted( false );

		var dropLabel = new Label { Parent = dropZone, Text = "Drop item" };
		UiStyle.Text( dropLabel, UiText.Secondary, 14f * Scale );
		dropLabel.Style.Set( "text-shadow", UiStyle.TextShadowCss );
		dropLabel.Style.Set( "pointer-events", "none" );

		_interaction?.RegisterPlayerDropZone( dropZone );

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

				var slotPanel = new InventorySlotPanel( slotIndex, _gridHost, _interaction ) { Parent = rowPanel };
				slotPanel.Style.Width = Length.Pixels( SlotSize );
				slotPanel.Style.Height = Length.Pixels( SlotSize );
				slotPanel.Style.Set( "flex-shrink", "0" );
				slotPanel.Style.Set( "flex-grow", "0" );
				slotPanel.Style.Set( "position", "relative" );
				UiStyle.Slot( slotPanel );
				slotPanel.Style.Set( "pointer-events", "auto" );
				slotPanel.Style.Set( "z-index", "10" );

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
		UpdateVisibility();
	}

	public void TickMenu( bool menuOpen )
	{
		if ( menuOpen )
			Refresh();
	}

	public void OnMenuGlobalMouseUp() { }

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen ? "flex" : "none" );
	}

	static SlotUi CreateSlotUi( Panel parent )
	{
		var inset = 4f * Scale;
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
		UiStyle.CountBadge( count, CountFontSize );
		count.Style.Set( "display", "none" );
		count.Style.Set( "pointer-events", "none" );

		return new SlotUi( parent, icon, count );
	}

	void ApplySlot( SlotUi ui, InventorySlot slot )
	{
		var resourceId = slot.IsEmpty ? string.Empty : slot.ResourceId ?? string.Empty;
		var count = slot.IsEmpty ? 0 : slot.Count;
		var preferred = IsPreferredAmmoStack( resourceId );

		var wear = slot.IsEmpty ? 0 : slot.Wear;
		var iconPath = slot.IsEmpty ? string.Empty : ResourceCatalog.GetIconPath( resourceId );
		if ( ui.LastResourceId == resourceId && ui.LastCount == count && ui.LastIconPath == iconPath
		     && ui.LastPreferred == preferred && ui.LastWear == wear )
		{
			return;
		}

		ui.LastResourceId = resourceId;
		ui.LastCount = count;
		ui.LastIconPath = iconPath;
		ui.LastPreferred = preferred;
		ui.LastWear = wear;
		ResourceCatalog.ApplyStackVisual( ui.IconPanel, ui.CountLabel, slot );
		UiStyle.SetSlotState( ui.Root, preferred ? UiSlotState.Preferred : UiSlotState.Idle );
	}

	bool IsPreferredAmmoStack( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) || _inventory is null )
			return false;

		var pref = _inventory.Components.Get<PlayerAmmoPreference>();
		return pref is not null && pref.IsPreferredAmmo( resourceId );
	}

	sealed class SlotUi
	{
		public Panel Root { get; }
		public Panel IconPanel { get; }
		public Label CountLabel { get; }
		public string LastResourceId { get; set; }
		public string LastIconPath { get; set; }
		public int LastCount { get; set; } = -1;
		public bool LastPreferred { get; set; }
		public int LastWear { get; set; } = -1;

		public SlotUi( Panel root, Panel iconPanel, Label countLabel )
		{
			Root = root;
			IconPanel = iconPanel;
			CountLabel = countLabel;
		}
	}
}
