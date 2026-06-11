using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Paperdoll equip slots in the inventory menu left column.</summary>
public sealed class EquipmentPaperdollSection : IPlayerMenuSection
{
	public const float Scale = 1.15f;
	public const float SlotSize = 64f * Scale;
	public const float SlotGap = 5f * Scale;
	public const float TitleFontSize = CraftingMenuSection.CraftingTitleFontSize;
	public const float LabelFontSize = 12f * Scale;
	public const float CountFontSize = CraftingMenuSection.SectionEntryFontSize;

	public string SectionId => "equipment";

	static readonly (EquipmentSlot Slot, string Label)[] SlotLayout =
	{
		(EquipmentSlot.Head, "Head"),
		(EquipmentSlot.Chest, "Chest"),
		(EquipmentSlot.MainHand, "Main"),
		(EquipmentSlot.OffHand, "Off"),
		(EquipmentSlot.Arms, "Arms"),
		(EquipmentSlot.Hands, "Hands"),
		(EquipmentSlot.Legs, "Legs"),
		(EquipmentSlot.Feet, "Feet"),
		(EquipmentSlot.Backpack, "Pack"),
		(EquipmentSlot.Grapple, "Hook"),
		(EquipmentSlot.Wingsuit, "Wing"),
	};

	readonly PlayerEquipment _equipment;
	readonly PlayerInventoryInteraction _interaction;
	readonly PlayerEquipmentPaperdollGridHost _gridHost;
	readonly List<SlotUi> _slotUi = new();

	Panel _sectionRoot;
	bool _menuOpen;
	bool _panelVisible;

	public EquipmentPaperdollSection(
		PlayerEquipment equipment,
		PlayerInventory inventory,
		PlayerInventoryInteraction interaction )
	{
		_equipment = equipment;
		_interaction = interaction;
		_gridHost = equipment is not null && inventory is not null
			? new PlayerEquipmentPaperdollGridHost( equipment, inventory )
			: null;
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

		var title = new Label { Parent = _sectionRoot, Text = "Equipment" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( TitleFontSize );
		title.Style.Set( "width", "100%" );
		title.Style.Set( "text-align", "center" );

		var grid = new Panel { Parent = _sectionRoot };
		grid.Style.Set( "flex-direction", "row" );
		grid.Style.Set( "flex-wrap", "wrap" );
		grid.Style.Set( "justify-content", "center" );
		grid.Style.Set( "gap", $"{SlotGap}px" );
		grid.Style.Set( "max-width", $"{SlotSize * 3f + SlotGap * 2f}px" );

		_slotUi.Clear();
		for ( var i = 0; i < SlotLayout.Length; i++ )
		{
			var (slot, label) = SlotLayout[i];
			var slotIndex = (int)slot;

			var slotHost = new Panel { Parent = grid };
			slotHost.Style.Set( "flex-direction", "column" );
			slotHost.Style.Set( "align-items", "center" );
			slotHost.Style.Set( "gap", "2px" );

			var slotPanel = new InventorySlotPanel( slotIndex, _gridHost, _interaction ) { Parent = slotHost };
			slotPanel.Style.Width = Length.Pixels( SlotSize );
			slotPanel.Style.Height = Length.Pixels( SlotSize );
			slotPanel.Style.Set( "flex-shrink", "0" );
			slotPanel.Style.Set( "position", "relative" );
			slotPanel.Style.Set( "box-sizing", "border-box" );
			slotPanel.Style.BackgroundColor = new Color( 0.1f, 0.11f, 0.13f, 0.95f );
			slotPanel.Style.Set( "border-width", "1px" );
			slotPanel.Style.Set( "border-color", "#474d57" );
			slotPanel.Style.Set( "border-radius", "4px" );
			slotPanel.Style.Set( "overflow", "hidden" );
			slotPanel.Style.Set( "pointer-events", "auto" );

			var slotLabel = new Label { Parent = slotHost, Text = label };
			slotLabel.Style.FontColor = new Color( 0.78f, 0.8f, 0.84f );
			slotLabel.Style.FontSize = Length.Pixels( LabelFontSize );

			_interaction?.RegisterSlot( slotPanel );
			_slotUi.Add( CreateSlotUi( slotPanel ) );
		}

		Refresh();
		UpdateVisibility();
	}

	public void Refresh()
	{
		if ( _equipment is null )
			return;

		for ( var i = 0; i < _slotUi.Count && i < SlotLayout.Length; i++ )
		{
			var slot = SlotLayout[i].Slot;
			ApplySlot( _slotUi[i], _equipment.GetSlot( slot ) );
		}
	}

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
		{
			EquipmentCatalog.ForceReload();
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

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
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
		count.Style.Set( "display", "none" );

		return new SlotUi( parent, icon, count );
	}

	void ApplySlot( SlotUi ui, InventorySlot slot )
	{
		var resourceId = slot.IsEmpty ? string.Empty : slot.ResourceId ?? string.Empty;
		var iconPath = slot.IsEmpty ? string.Empty : ResourceCatalog.GetIconPath( resourceId );
		if ( ui.LastResourceId == resourceId && ui.LastIconPath == iconPath )
			return;

		ui.LastResourceId = resourceId;
		ui.LastIconPath = iconPath;
		ResourceCatalog.ApplyStackVisual( ui.IconPanel, ui.CountLabel, slot );
	}

	sealed class SlotUi
	{
		public Panel Root { get; }
		public Panel IconPanel { get; }
		public Label CountLabel { get; }
		public string LastResourceId { get; set; }
		public string LastIconPath { get; set; }

		public SlotUi( Panel root, Panel iconPanel, Label countLabel )
		{
			Root = root;
			IconPanel = iconPanel;
			CountLabel = countLabel;
		}
	}
}
