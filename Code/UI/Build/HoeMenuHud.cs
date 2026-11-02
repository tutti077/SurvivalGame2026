using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Center-screen picker for the hoe's ground mode (Attack2 with the hoe out) - the build menu's layout with four tiles:
/// Grass, Bare ground, Tilled soil, Grade (<see cref="HoeMode"/>). Click a tile to pick the mode and close; right-click,
/// Escape or a click beside the panel closes without changing it. Hover is polled like the build menu's (mouse-out bubbles
/// from a tile to the grid and cleared the hover there).
/// </summary>
public sealed class HoeMenuHud
{
	const float Scale = BuildMenuHud.Scale;
	const float SlotSize = BuildMenuHud.SlotSize * 1.5f;
	const float SlotGap = BuildMenuHud.SlotGap;
	const float TitleFontSize = BuildMenuHud.TitleFontSize;
	const float BodyFontSize = BuildMenuHud.BodyFontSize;
	const float DetailNameFontSize = BuildMenuHud.DetailNameFontSize;
	const float DetailPanelHeight = BuildMenuHud.DetailPanelHeight;

	/// <summary>The tiles, in menu order (Mark 2026-11-02: grass, bare ground, tilled soil, the grading tool).</summary>
	static readonly (HoeMode Mode, Color Color)[] Tiles =
	{
		(HoeMode.Grass, new Color( 0.30f, 0.62f, 0.24f )),
		(HoeMode.Bare, new Color( 0.52f, 0.38f, 0.24f )),
		(HoeMode.Till, new Color( 0.36f, 0.24f, 0.14f )),
		(HoeMode.Grade, new Color( 0.24f, 0.42f, 0.66f )),
		(HoeMode.Raise, new Color( 0.80f, 0.62f, 0.30f )),
		(HoeMode.Cobble, new Color( 0.55f, 0.55f, 0.58f )),
	};

	readonly PlayerEquipment _equipment;
	readonly List<ModeSlotUi> _slots = new();

	BuildMenuInputOverlay _overlay;
	Panel _panelRoot;
	Panel _grid;
	Label _detailName;
	Label _detailText;
	Panel _squareRow;
	Panel _squareBox;
	Label _squareCheck;
	Label _squareLabel;
	ToolHoe _boundHoe;
	HoeMode? _hoveredMode;

	public HoeMenuHud( PlayerEquipment equipment ) => _equipment = equipment;

	public void Tick() => PollHoveredSlot();

	public void Build( Panel hudRoot )
	{
		if ( _equipment is null || hudRoot is null )
			return;

		_overlay = new BuildMenuInputOverlay { Parent = hudRoot };
		_overlay.Bind( _equipment );
		_overlay.EscapeRequested = () => ResolveHoe()?.SetMenuOpen( false );
		_overlay.DismissRequested = () => ResolveHoe()?.SetMenuOpen( false );
		_overlay.ButtonInput = PanelInputType.UI;
		_overlay.Style.Set( "position", "absolute" );
		_overlay.Style.Set( "left", "0" );
		_overlay.Style.Set( "top", "0" );
		_overlay.Style.Set( "width", "100%" );
		_overlay.Style.Set( "height", "100%" );
		_overlay.Style.Set( "z-index", "3500" );
		_overlay.Style.Set( "display", "none" );
		_overlay.Style.Set( "pointer-events", "none" );
		_overlay.SetOpen( false );

		var panelWidth = (Tiles.Length * SlotSize) + ((Tiles.Length - 1) * SlotGap) + (32f * Scale);
		_panelRoot = new Panel { Parent = _overlay };
		_panelRoot.Style.Set( "position", "absolute" );
		_panelRoot.Style.Set( "left", "50%" );
		_panelRoot.Style.Set( "top", "50%" );
		_panelRoot.Style.Set( "transform", "translate(-50%, -50%)" );
		_panelRoot.Style.Set( "flex-direction", "column" );
		_panelRoot.Style.Set( "align-items", "stretch" );
		_panelRoot.Style.Set( "gap", $"{8f * Scale}px" );
		_panelRoot.Style.Set( "width", $"{panelWidth}px" );
		_panelRoot.Style.PaddingLeft = Length.Pixels( 16f * Scale );
		_panelRoot.Style.PaddingRight = Length.Pixels( 16f * Scale );
		_panelRoot.Style.PaddingTop = Length.Pixels( 14f * Scale );
		_panelRoot.Style.PaddingBottom = Length.Pixels( 14f * Scale );
		_panelRoot.Style.BackgroundColor = new Color( 0.06f, 0.06f, 0.07f, 0.92f );
		_panelRoot.Style.Set( "border-radius", "8px" );
		_panelRoot.Style.Set( "pointer-events", "auto" );

		var title = new Label { Parent = _panelRoot, Text = "Hoe" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( TitleFontSize );

		_grid = new Panel { Parent = _panelRoot };
		_grid.Style.Set( "flex-direction", "row" );
		_grid.Style.Set( "justify-content", "center" );
		_grid.Style.Set( "gap", $"{SlotGap}px" );

		foreach ( var (mode, color) in Tiles )
		{
			var slotPanel = new Panel { Parent = _grid };
			slotPanel.Style.Width = Length.Pixels( SlotSize );
			slotPanel.Style.Height = Length.Pixels( SlotSize );
			slotPanel.Style.BackgroundColor = new Color( 0.12f, 0.13f, 0.15f, 0.92f );
			slotPanel.Style.Set( "border-radius", "4px" );
			slotPanel.Style.Set( "border-width", "1px" );
			slotPanel.Style.Set( "border-color", "#474d57" );
			slotPanel.Style.Set( "flex-direction", "column" );
			slotPanel.Style.Set( "align-items", "center" );
			slotPanel.Style.Set( "justify-content", "center" );
			slotPanel.Style.Set( "gap", $"{6f * Scale}px" );
			slotPanel.Style.Set( "cursor", "pointer" );

			// No icons for the ground modes yet: a swatch of the ground it makes, named under it
			var swatch = new Panel { Parent = slotPanel };
			swatch.Style.Width = Length.Pixels( SlotSize * 0.5f );
			swatch.Style.Height = Length.Pixels( SlotSize * 0.5f );
			swatch.Style.BackgroundColor = color;
			swatch.Style.Set( "border-radius", "50%" );
			swatch.Style.Set( "pointer-events", "none" );

			var name = new Label { Parent = slotPanel, Text = ToolHoe.ModeTitle( mode ) };
			name.Style.FontColor = Color.White;
			name.Style.FontSize = Length.Pixels( BodyFontSize );
			name.Style.Set( "pointer-events", "none" );

			var picked = mode;
			slotPanel.AddEventListener( "onclick", () => ResolveHoe()?.SelectMode( picked ) );
			_slots.Add( new ModeSlotUi { Panel = slotPanel, Mode = mode } );
		}

		_squareRow = new Panel { Parent = _panelRoot };
		_squareRow.Style.Set( "flex-direction", "row" );
		_squareRow.Style.Set( "align-items", "center" );
		_squareRow.Style.Set( "gap", $"{6f * Scale}px" );
		_squareRow.AddEventListener( "onclick", () => ResolveHoe()?.ToggleSquarePatch() );

		_squareBox = new Panel { Parent = _squareRow };
		_squareBox.Style.Width = Length.Pixels( 18f * Scale );
		_squareBox.Style.Height = Length.Pixels( 18f * Scale );
		_squareBox.Style.BackgroundColor = new Color( 0.18f, 0.18f, 0.2f );
		_squareBox.Style.Set( "border-radius", "3px" );
		_squareBox.Style.Set( "align-items", "center" );
		_squareBox.Style.Set( "justify-content", "center" );
		_squareBox.Style.Set( "border-width", "2px" );
		_squareBox.Style.Set( "border-color", "#595961" );

		_squareCheck = new Label { Parent = _squareBox, Text = "\u2713" };
		_squareCheck.Style.FontColor = new Color( 0.22f, 0.48f, 0.95f );
		_squareCheck.Style.FontSize = Length.Pixels( 14f * Scale );
		_squareCheck.Style.Set( "display", "none" );

		_squareLabel = new Label { Parent = _squareRow, Text = "Square patch (paint and raise; F swaps)" };
		_squareLabel.Style.FontColor = Color.White;
		_squareLabel.Style.FontSize = Length.Pixels( BodyFontSize );

		var detailRoot = new Panel { Parent = _panelRoot };
		detailRoot.Style.Set( "flex-direction", "column" );
		detailRoot.Style.Set( "align-items", "center" );
		detailRoot.Style.Set( "justify-content", "center" );
		detailRoot.Style.Set( "gap", $"{4f * Scale}px" );
		detailRoot.Style.Set( "width", "100%" );
		detailRoot.Style.Set( "height", $"{DetailPanelHeight}px" );
		detailRoot.Style.Set( "min-height", $"{DetailPanelHeight}px" );
		detailRoot.Style.Set( "flex-shrink", "0" );
		detailRoot.Style.Set( "overflow", "hidden" );

		_detailName = new Label { Parent = detailRoot };
		_detailName.Style.FontColor = Color.White;
		_detailName.Style.FontSize = Length.Pixels( DetailNameFontSize );
		_detailName.Style.Set( "text-align", "center" );

		_detailText = new Label { Parent = detailRoot };
		_detailText.Style.FontColor = new Color( 0.82f, 0.84f, 0.88f );
		_detailText.Style.FontSize = Length.Pixels( BodyFontSize );
		_detailText.Style.Set( "text-align", "center" );
		_detailText.Style.Set( "white-space", "pre-line" );

		_equipment.EquipmentChanged += OnEquipmentChanged;
		RebindHoeEvents();
		OnMenuOpenChanged();
	}

	void PollHoveredSlot()
	{
		if ( _grid is null )
			return;
		var hoe = ResolveHoe();
		if ( hoe is null || !hoe.IsMenuOpen )
			return;

		ModeSlotUi hovered = null;
		for ( var i = 0; i < _slots.Count; i++ )
		{
			if ( _slots[i]?.Panel is { HasHovered: true } )
			{
				hovered = _slots[i];
				break;
			}
		}
		if ( _hoveredMode == hovered?.Mode )
			return;
		_hoveredMode = hovered?.Mode;
		UpdateSlotHighlights();
		UpdateDetailPanel();
	}

	ToolHoe ResolveHoe() => _equipment?.GetActiveTool<ToolHoe>();

	void RebindHoeEvents()
	{
		if ( _boundHoe is not null )
		{
			_boundHoe.MenuOpenChanged -= OnMenuOpenChanged;
			_boundHoe.ModeChanged -= OnModeChanged;
		}
		_boundHoe = ResolveHoe();
		if ( _boundHoe is null )
			return;
		_boundHoe.MenuOpenChanged += OnMenuOpenChanged;
		_boundHoe.ModeChanged += OnModeChanged;
	}

	void OnEquipmentChanged()
	{
		RebindHoeEvents();
		OnMenuOpenChanged();
	}

	void OnModeChanged()
	{
		UpdateSlotHighlights();
		UpdateDetailPanel();
		ApplySquareUi();
	}

	void ApplySquareUi()
	{
		var on = ResolveHoe()?.SquarePatch ?? false;
		if ( _squareBox is not null )
		{
			_squareBox.Style.BackgroundColor = on ? new Color( 0.14f, 0.24f, 0.42f, 0.95f ) : new Color( 0.18f, 0.18f, 0.2f );
			_squareBox.Style.Set( "border-color", on ? "#387af2" : "#595961" );
		}
		_squareCheck?.Style.Set( "display", on ? "flex" : "none" );
	}

	void OnMenuOpenChanged()
	{
		var hoe = ResolveHoe();
		var visible = hoe is not null && hoe.IsMenuOpen;
		if ( visible )
			RebindHoeEvents();
		_overlay?.SetOpen( visible );
		_panelRoot?.Style.Set( "display", visible ? "flex" : "none" );
		_hoveredMode = null;
		UpdateSlotHighlights();
		UpdateDetailPanel();
		ApplySquareUi();
	}

	/// <summary>The hovered tile lifts (the build menu's treatment); the current mode keeps a blue border so the player sees what the hoe is set to.</summary>
	void UpdateSlotHighlights()
	{
		var current = ResolveHoe()?.Mode;
		for ( var i = 0; i < _slots.Count; i++ )
		{
			var slot = _slots[i];
			if ( slot?.Panel is null )
				continue;
			var isHovered = _hoveredMode == slot.Mode;
			var isCurrent = current == slot.Mode;
			slot.Panel.Style.Set( "border-color", isHovered ? "#8ab4f8" : isCurrent ? "#387af2" : "#474d57" );
			slot.Panel.Style.Set( "border-width", isHovered || isCurrent ? "3px" : "1px" );
			slot.Panel.Style.BackgroundColor = isHovered
				? new Color( 0.20f, 0.28f, 0.42f, 0.98f )
				: new Color( 0.12f, 0.13f, 0.15f, 0.92f );
			slot.Panel.Style.Set( "transform", isHovered ? "scale(1.08)" : "scale(1)" );
		}
	}

	void UpdateDetailPanel()
	{
		if ( _detailName is null || _detailText is null )
			return;
		var hoe = ResolveHoe();
		var mode = _hoveredMode ?? hoe?.Mode ?? HoeMode.Till;
		var title = ToolHoe.ModeTitle( mode );
		if ( mode == HoeMode.Grade && hoe is not null )
			title += hoe.GradeSlope ? " (slope)" : " (flat)";
		_detailName.Text = _hoveredMode is null ? $"Current: {title}" : title;
		_detailText.Text = ToolHoe.ModeDescription( mode );
	}

	sealed class ModeSlotUi
	{
		public Panel Panel;
		public HoeMode Mode;
	}
}
