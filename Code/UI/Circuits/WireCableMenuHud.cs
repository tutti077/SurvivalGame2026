using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Center-screen cable colour picker for the wire stripper (Attack2), the wire counterpart of
/// <see cref="BuildMenuHud"/>: three swatches, click one to select it and close.
/// </summary>
public sealed class WireCableMenuHud
{
	public const float Scale = BuildMenuHud.Scale;
	public const float SwatchSize = BuildMenuHud.SlotSize;
	public const float SwatchGap = BuildMenuHud.SlotGap * 2f;
	public const float TitleFontSize = BuildMenuHud.TitleFontSize;
	public const float BodyFontSize = BuildMenuHud.BodyFontSize;

	readonly PlayerEquipment _equipment;
	readonly List<SwatchUi> _swatches = new();

	WireCableMenuInputOverlay _overlay;
	Panel _panelRoot;
	ToolWireStripper _boundStripper;

	public WireCableMenuHud( PlayerEquipment equipment ) => _equipment = equipment;

	public void Tick()
	{
		// The tool instance changes whenever the hotbar does; keep the swatches on the live one.
		if ( !ReferenceEquals( _boundStripper, ResolveStripper() ) )
			OnEquipmentChanged();
	}

	public void Build( Panel hudRoot )
	{
		if ( _equipment is null || hudRoot is null )
			return;

		_overlay = new WireCableMenuInputOverlay { Parent = hudRoot };
		_overlay.Bind( _equipment );
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

		_panelRoot = new Panel { Parent = _overlay };
		_panelRoot.Style.Set( "position", "absolute" );
		_panelRoot.Style.Set( "left", "50%" );
		_panelRoot.Style.Set( "top", "50%" );
		_panelRoot.Style.Set( "transform", "translate(-50%, -50%)" );
		_panelRoot.Style.Set( "flex-direction", "column" );
		_panelRoot.Style.Set( "align-items", "center" );
		_panelRoot.Style.Set( "gap", $"{10f * Scale}px" );
		_panelRoot.Style.PaddingLeft = Length.Pixels( 18f * Scale );
		_panelRoot.Style.PaddingRight = Length.Pixels( 18f * Scale );
		_panelRoot.Style.PaddingTop = Length.Pixels( 14f * Scale );
		_panelRoot.Style.PaddingBottom = Length.Pixels( 14f * Scale );
		_panelRoot.Style.BackgroundColor = new Color( 0.06f, 0.06f, 0.07f, 0.92f );
		_panelRoot.Style.Set( "border-radius", "8px" );
		_panelRoot.Style.Set( "pointer-events", "auto" );

		var title = new Label { Parent = _panelRoot, Text = "Wire Colour" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( TitleFontSize );

		var row = new Panel { Parent = _panelRoot };
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "align-items", "flex-start" );
		row.Style.Set( "gap", $"{SwatchGap}px" );

		for ( var i = 0; i < CircuitCableExtensions.Count; i++ )
		{
			var cable = (CircuitCable)i;
			var column = new Panel { Parent = row };
			column.Style.Set( "flex-direction", "column" );
			column.Style.Set( "align-items", "center" );
			column.Style.Set( "gap", $"{4f * Scale}px" );
			column.Style.Set( "cursor", "pointer" );

			var swatch = new Panel { Parent = column };
			swatch.Style.Width = Length.Pixels( SwatchSize );
			swatch.Style.Height = Length.Pixels( SwatchSize );
			swatch.Style.BackgroundColor = cable.ToColor();
			swatch.Style.Set( "border-radius", "6px" );
			swatch.Style.Set( "border-width", "3px" );
			swatch.Style.Set( "border-color", "#474d57" );
			swatch.Style.Set( "pointer-events", "none" );

			var label = new Label { Parent = column, Text = cable.DisplayName() };
			label.Style.FontColor = Color.White;
			label.Style.FontSize = Length.Pixels( BodyFontSize );
			label.Style.Set( "pointer-events", "none" );

			column.AddEventListener( "onclick", () =>
			{
				var stripper = ResolveStripper();
				if ( stripper is null )
					return;

				stripper.SetCable( cable );
				stripper.SetCableMenuOpen( false );
			} );

			_swatches.Add( new SwatchUi( swatch, cable ) );
		}

		var hint = new Label { Parent = _panelRoot, Text = "Hold LMB on a sphere, release on another to wire them. Same pair again removes the wire." };
		hint.Style.FontColor = new Color( 0.82f, 0.84f, 0.88f );
		hint.Style.FontSize = Length.Pixels( BodyFontSize );
		hint.Style.Set( "text-align", "center" );

		_equipment.EquipmentChanged += OnEquipmentChanged;
		RebindStripperEvents();
		OnCableMenuOpenChanged();
		ApplySelection();
	}

	ToolWireStripper ResolveStripper() => _equipment?.GetActiveTool<ToolWireStripper>();

	void RebindStripperEvents()
	{
		if ( _boundStripper is not null )
		{
			_boundStripper.CableMenuOpenChanged -= OnCableMenuOpenChanged;
			_boundStripper.SelectedCableChanged -= ApplySelection;
		}

		_boundStripper = ResolveStripper();
		if ( _boundStripper is null )
			return;

		_boundStripper.CableMenuOpenChanged += OnCableMenuOpenChanged;
		_boundStripper.SelectedCableChanged += ApplySelection;
	}

	void OnEquipmentChanged()
	{
		RebindStripperEvents();
		OnCableMenuOpenChanged();
		ApplySelection();
	}

	void OnCableMenuOpenChanged()
	{
		var stripper = ResolveStripper();
		var visible = stripper is not null && stripper.IsCableMenuOpen;
		_overlay?.SetOpen( visible );
	}

	void ApplySelection()
	{
		var selected = ResolveStripper()?.SelectedCable ?? CircuitCable.Red;
		for ( var i = 0; i < _swatches.Count; i++ )
		{
			var isSelected = _swatches[i].Cable == selected;
			_swatches[i].Panel.Style.Set( "border-color", isSelected ? "#ffffff" : "#474d57" );
		}
	}

	sealed class SwatchUi
	{
		public readonly Panel Panel;
		public readonly CircuitCable Cable;

		public SwatchUi( Panel panel, CircuitCable cable )
		{
			Panel = panel;
			Cable = cable;
		}
	}
}
