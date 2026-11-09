using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Centered world map panel: crew column on the left, a square map window in the middle, and a
/// tool column on the right — pin icons, pen / eraser, controls text — with an eye / + / − strip
/// hanging off the map's top-right edge so nothing sits over the map itself.
/// The menu hides the OS cursor, so every button here is a hit rect dispatched from the soft
/// cursor (<see cref="TrySelectAtScreen"/> etc.); map input is routed by screen position through
/// <see cref="TerrainWorldMapFace"/> and committed to <see cref="LocalMapMarkup"/>.
/// </summary>
public sealed class MapMenuSection : IPlayerMenuSection
{
	public string SectionId => "map";

	const float ToolIconSize = 52f;
	const float ToolIconGap = 10f;
	/// <summary>Icon inset inside a pin / tool tile.</summary>
	const float ToolIconInset = 4f;
	/// <summary>A tile's outer width: the theme border sits outside the ToolIconSize box.</summary>
	static float ToolTileOuterSize => ToolIconSize + 2f * UiTheme.Metrics.BorderWidth;
	const float StripButtonSize = 30f;
	const float ToolColumnWidth = 300f;
	const float HeaderFontSize = 20f;
	const float BodyFontSize = 17f;
	const float RadioSize = 20f;
	const float ShareRowHeight = 30f;
	const int CrewPinRows = 5;
	const float DoubleClickSeconds = 0.4f;
	const float DoubleClickPixels = 12f;
	/// <summary>Wheel notch = this many +/- steps (ScaleStep each) so a flick actually moves the map.</summary>
	const int WheelZoomStepsPerNotch = 3;

	readonly TerrainWorldMapFace _face = new();
	/// <summary>One fixed line under Controls: dungeon floor + rooms found (never reflows the column).</summary>
	Label _dungeonLine;
	long _dungeonLineKey = long.MinValue;
	readonly CrewMapPanel _crewPanel;
	readonly PlayerInventoryInteraction _interaction;
	readonly List<(Panel Panel, Action Action)> _toolTargets = new();
	readonly List<(Panel Panel, Action Action)> _shareTargets = new();
	readonly List<(Guid Key, Radio Radio)> _crewPinRadios = new();
	readonly List<PlayerCrew> _crewMates = new();
	readonly Dictionary<string, Panel> _pinButtons = new( StringComparer.OrdinalIgnoreCase );
	Panel _sectionRoot;
	Panel _mapHost;
	Panel _penButton;
	Panel _eraserButton;
	Panel _eyeButton;
	Panel _zoomInButton;
	Panel _zoomOutButton;
	Radio _shareLocationRadio;
	Panel _crewPinsBox;
	string _crewPinsKey;
	double _nextCrewRefreshAt;
	TextEntry _nameEntry;
	Guid _namingPinId;
	bool _naming;
	bool _menuOpen;
	bool _panelVisible;

	double _lastMapClickAt = -10;
	Vector2 _lastMapClickPos;

	/// <summary>Pointer must travel this far before a press becomes a pan (keeps clicks and double-clicks clean).</summary>
	const float PanStartPixels = 4f;

	bool _panArmed;
	bool _panning;
	Vector2 _panPressPos;
	Vector2 _panLastPos;
	bool _dragging;
	MapMarkupTool _dragTool;
	MapStrokeData _activeStroke;
	bool _eraseChanged;

	public MapMenuSection( PlayerInventoryInteraction interaction )
	{
		_interaction = interaction;
		_crewPanel = new CrewMapPanel( interaction );
	}

	/// <summary>True while the pin-name entry has the keyboard — page hotkeys must stand down.</summary>
	public bool IsCapturingText => _naming && _menuOpen && _panelVisible;

	public void Build( Panel menuColumn )
	{
		_sectionRoot = new Panel { Parent = menuColumn };
		_sectionRoot.Style.Set( "position", "relative" );
		_sectionRoot.Style.Width = Length.Percent( 100 );
		_sectionRoot.Style.Height = Length.Percent( 100 );
		_sectionRoot.Style.Set( "pointer-events", "auto" );
		_sectionRoot.Style.Set( "flex-direction", "column" );
		_sectionRoot.Style.Set( "align-items", "stretch" );
		_sectionRoot.Style.PaddingTop = Length.Pixels( 10f );
		_sectionRoot.Style.PaddingBottom = Length.Pixels( 10f );
		_sectionRoot.Style.PaddingLeft = Length.Pixels( 12f );
		_sectionRoot.Style.PaddingRight = Length.Pixels( 12f );
		UiStyle.Surface( _sectionRoot, UiSurface.Frame );

		var header = new Panel { Parent = _sectionRoot };
		header.Style.Set( "flex-direction", "row" );
		header.Style.Set( "align-items", "center" );
		header.Style.Set( "justify-content", "space-between" );
		header.Style.Set( "width", "100%" );
		header.Style.Set( "margin-bottom", "8px" );
		header.Style.Set( "flex-shrink", "0" );

		var title = new Label { Parent = header, Text = "Map" };
		UiStyle.Text( title, UiText.Title );

		var hint = new Label { Parent = header, Text = "Wheel zooms · double-click places a pin" };
		UiStyle.Text( hint, UiText.Muted, 15f );

		var content = new Panel { Parent = _sectionRoot };
		content.Style.Set( "flex-direction", "row" );
		content.Style.Set( "align-items", "stretch" );
		content.Style.Set( "flex-grow", "1" );
		content.Style.Width = Length.Percent( 100 );
		content.Style.Set( "overflow", "hidden" );

		_crewPanel.Build( content );

		// Rectangular window into the map: takes every pixel the crew and tool columns leave.
		// The face keeps its zoom stage square, so a wide window crops the map, never stretches it.
		_mapHost = new Panel { Parent = content };
		_mapHost.Style.Set( "flex-direction", "column" );
		_mapHost.Style.Set( "flex-grow", "1" );
		_mapHost.Style.Set( "flex-shrink", "1" );
		_mapHost.Style.Height = Length.Percent( 100 );
		_mapHost.Style.Set( "min-width", "300px" );

		_face.Build( _mapHost, sizePixels: 0f, fillParent: true );

		BuildSideStrip( content );
		BuildToolColumn( content );
		BuildNameEntry();
		RefreshToolHighlights();
		UpdateVisibility();
	}

	/// <summary>Eye / + / − column hanging off the map's top-right edge (outside the map, never over it).</summary>
	void BuildSideStrip( Panel parent )
	{
		var strip = new Panel { Parent = parent };
		strip.Style.Set( "flex-direction", "column" );
		strip.Style.Set( "flex-shrink", "0" );
		strip.Style.Set( "align-items", "center" );
		strip.Style.Width = Length.Pixels( StripButtonSize + 6f );
		strip.Style.MarginLeft = Length.Pixels( 2f );
		strip.Style.Set( "gap", "4px" );
		strip.Style.Set( "pointer-events", "none" );

		_eyeButton = MakeStripButton( strip, null );
		_toolTargets.Add( (_eyeButton, () =>
		{
			LocalMapMarkup.SetShowPins( !LocalMapMarkup.ShowPins );
			RefreshToolHighlights();
		}) );

		_zoomInButton = MakeStripButton( strip, "+" );
		_toolTargets.Add( (_zoomInButton, () =>
		{
			TerrainMinimapZoom.TryZoomIn();
			RefreshToolHighlights();
		}) );

		_zoomOutButton = MakeStripButton( strip, "-" );
		_toolTargets.Add( (_zoomOutButton, () =>
		{
			TerrainMinimapZoom.TryZoomOut();
			RefreshToolHighlights();
		}) );
	}

	static Panel MakeStripButton( Panel parent, string glyph )
	{
		var btn = new Panel { Parent = parent };
		btn.Style.Set( "flex-shrink", "0" );
		btn.Style.Width = Length.Pixels( StripButtonSize );
		btn.Style.Height = Length.Pixels( StripButtonSize );
		UiStyle.Button( btn, UiButton.Ghost );
		btn.Style.Set( "pointer-events", "none" );

		if ( glyph is not null )
		{
			var label = new Label { Parent = btn, Text = glyph };
			UiStyle.ButtonLabel( label, UiButton.Ghost, HeaderFontSize );
		}

		return btn;
	}

	void BuildToolColumn( Panel parent )
	{
		var column = new Panel { Parent = parent };
		column.Style.Set( "flex-direction", "column" );
		column.Style.Set( "flex-shrink", "0" );
		column.Style.Set( "align-items", "center" );
		column.Style.Width = Length.Pixels( ToolColumnWidth );
		column.Style.PaddingLeft = Length.Pixels( 10f );
		column.Style.PaddingTop = Length.Pixels( 24f );
		column.Style.Set( "pointer-events", "none" );

		AddSectionHeader( column, "Pins" );

		var grid = new Panel { Parent = column };
		grid.Style.Set( "flex-direction", "row" );
		grid.Style.Set( "flex-wrap", "wrap" );
		grid.Style.Set( "justify-content", "center" );
		grid.Style.Set( "gap", $"{ToolIconGap:0}px" );
		// Three pins a row up to nine icons, four beyond that: the column has a fixed height budget and
		// every extra row of pins is a row the controls text below would have to give up.
		var pinColumns = MapPinCatalog.Icons.Count > 9 ? 4 : 3;
		grid.Style.Width = Length.Pixels( ToolTileOuterSize * pinColumns + ToolIconGap * (pinColumns - 1) + 2f );
		// Explicit height: a wrapped flex row does not grow for its extra rows here, so without this
		// the Markup header draws over the last row of pins.
		var pinRows = (int)Math.Ceiling( MapPinCatalog.Icons.Count / (float)pinColumns );
		grid.Style.Height = Length.Pixels( pinRows * ToolTileOuterSize + Math.Max( 0, pinRows - 1 ) * ToolIconGap );
		grid.Style.Set( "flex-shrink", "0" );
		grid.Style.MarginTop = Length.Pixels( 8f );
		grid.Style.Set( "pointer-events", "none" );

		foreach ( var icon in MapPinCatalog.Icons )
		{
			var id = icon.Id;
			var btn = MakeIconButton( grid, icon.TexturePath );
			_pinButtons[id] = btn;
			_toolTargets.Add( (btn, () =>
			{
				LocalMapMarkup.SelectedPinIcon = id;
				LocalMapMarkup.Tool = MapMarkupTool.Pin;
				RefreshToolHighlights();
			}) );
		}

		var markupHeader = AddSectionHeader( column, "Markup" );
		markupHeader.Style.MarginTop = Length.Pixels( 18f );

		var markupRow = new Panel { Parent = column };
		markupRow.Style.Set( "flex-direction", "row" );
		markupRow.Style.Set( "justify-content", "center" );
		markupRow.Style.Set( "gap", $"{ToolIconGap:0}px" );
		markupRow.Style.MarginTop = Length.Pixels( 8f );
		markupRow.Style.Set( "flex-shrink", "0" );
		markupRow.Style.Set( "pointer-events", "none" );

		_penButton = MakeIconButton( markupRow, "ui/map/tool_pen.png" );
		_toolTargets.Add( (_penButton, () =>
		{
			LocalMapMarkup.Tool = LocalMapMarkup.Tool == MapMarkupTool.Pen ? MapMarkupTool.Pin : MapMarkupTool.Pen;
			RefreshToolHighlights();
		}) );

		_eraserButton = MakeIconButton( markupRow, "ui/map/tool_eraser.png" );
		_toolTargets.Add( (_eraserButton, () =>
		{
			LocalMapMarkup.Tool = LocalMapMarkup.Tool == MapMarkupTool.Eraser ? MapMarkupTool.Pin : MapMarkupTool.Eraser;
			RefreshToolHighlights();
		}) );

		var controlsHeader = AddSectionHeader( column, "Controls" );
		controlsHeader.Style.MarginTop = Length.Pixels( 18f );

		var controls = new Panel { Parent = column };
		controls.Style.Set( "flex-direction", "column" );
		controls.Style.Set( "align-items", "flex-start" );
		controls.Style.Set( "flex-shrink", "0" );
		controls.Style.MarginTop = Length.Pixels( 6f );
		controls.Style.Set( "pointer-events", "none" );

		AddControlLine( controls, "LMB ×2: Add Pin" );
		AddControlLine( controls, "LMB: Cross Off Pin" );
		AddControlLine( controls, "RMB: Remove Pin" );
		AddControlLine( controls, "MMB: Ping Crew" );
		AddControlLine( controls, "Wheel: Zoom" );
		AddControlLine( controls, "LMB drag: Pan map" );
		AddControlLine( controls, "Pen / Eraser: hold RMB" );

		// Dungeon readout: which floor layer the map is showing and how much of the dungeon has been found.
		_dungeonLine = new Label { Parent = controls, Text = "Dungeon: —" };
		UiStyle.Text( _dungeonLine, UiText.Accent, BodyFontSize );
		_dungeonLine.Style.MarginTop = Length.Pixels( 10f );
		_dungeonLine.Style.Set( "white-space", "nowrap" );
		_dungeonLine.Style.Set( "pointer-events", "none" );

		// Coop sharing: my location to the crew, and which crew mates' pins land on my map.
		var sharingHeader = AddSectionHeader( column, "Sharing" );
		sharingHeader.Style.MarginTop = Length.Pixels( 18f );

		var shareRow = MakeRadioRow( column, "Show location to crew", out _shareLocationRadio );
		shareRow.Style.MarginTop = Length.Pixels( 6f );
		UiStyle.Surface( shareRow, UiSurface.Sunken );
		_shareTargets.Add( (shareRow, () =>
		{
			LocalMapMarkup.SetShareLocation( !LocalMapMarkup.ShareLocation );
			RefreshToolHighlights();
		}) );

		var crewPinsHeader = AddSectionHeader( column, "Crew Pins" );
		crewPinsHeader.Style.MarginTop = Length.Pixels( 18f );

		// Fixed-height box: rows never move the column around when the crew changes. It is the one
		// block allowed to shrink (down to two rows) on a short window, so the controls text above
		// never collapses into itself.
		_crewPinsBox = new Panel { Parent = column };
		_crewPinsBox.Style.Set( "flex-direction", "column" );
		_crewPinsBox.Style.Set( "flex-shrink", "1" );
		_crewPinsBox.Style.Width = Length.Percent( 100 );
		_crewPinsBox.Style.Height = Length.Pixels( CrewPinRows * ShareRowHeight + 6f );
		_crewPinsBox.Style.MinHeight = Length.Pixels( 2f * ShareRowHeight + 6f );
		_crewPinsBox.Style.MarginTop = Length.Pixels( 6f );
		_crewPinsBox.Style.Set( "overflow", "hidden" );
		_crewPinsBox.Style.Set( "pointer-events", "none" );
		UiStyle.Surface( _crewPinsBox, UiSurface.Sunken );
		RefreshCrewPinRows( force: true );
	}

	/// <summary>Radio-button visuals: ring, filled dot when on, red X through it when the setting is off.</summary>
	sealed class Radio
	{
		public Panel Outer;
		public Panel Inner;
		public Panel Cross;
	}

	static Panel MakeRadioRow( Panel parent, string text, out Radio radio )
	{
		var row = new Panel { Parent = parent };
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "align-items", "center" );
		row.Style.Set( "flex-shrink", "0" );
		row.Style.Width = Length.Percent( 100 );
		row.Style.Height = Length.Pixels( ShareRowHeight );
		row.Style.PaddingLeft = Length.Pixels( 8f );
		row.Style.Set( "gap", "10px" );
		row.Style.Set( "pointer-events", "none" );

		radio = new Radio();
		radio.Outer = new Panel { Parent = row };
		radio.Outer.Style.Set( "position", "relative" );
		radio.Outer.Style.Set( "flex-shrink", "0" );
		radio.Outer.Style.Width = Length.Pixels( RadioSize );
		radio.Outer.Style.Height = Length.Pixels( RadioSize );
		radio.Outer.Style.Set( "border-radius", "50%" );
		radio.Outer.Style.Set( "border-width", UiStyle.Px( UiTheme.Metrics.BorderWidthStrong ) );
		radio.Outer.Style.Set( "border-color", UiStyle.Css( UiTheme.Colors.BorderStrongColor ) );
		radio.Outer.Style.BackgroundColor = UiTheme.Colors.PanelSunkenColor;
		radio.Outer.Style.Set( "align-items", "center" );
		radio.Outer.Style.Set( "justify-content", "center" );
		radio.Outer.Style.Set( "overflow", "visible" );
		radio.Outer.Style.Set( "pointer-events", "none" );

		radio.Inner = new Panel { Parent = radio.Outer };
		radio.Inner.Style.Width = Length.Pixels( RadioSize * 0.5f );
		radio.Inner.Style.Height = Length.Pixels( RadioSize * 0.5f );
		radio.Inner.Style.Set( "border-radius", "50%" );
		radio.Inner.Style.BackgroundColor = UiTheme.Colors.AccentColor;
		radio.Inner.Style.Set( "pointer-events", "none" );

		radio.Cross = new Panel { Parent = radio.Outer };
		radio.Cross.Style.Set( "position", "absolute" );
		radio.Cross.Style.Set( "left", "-5px" );
		radio.Cross.Style.Set( "top", "-5px" );
		radio.Cross.Style.Set( "right", "-5px" );
		radio.Cross.Style.Set( "bottom", "-5px" );
		radio.Cross.Style.Set( "pointer-events", "none" );
		MenuUiTextures.ApplyBackground( radio.Cross, MapPinCatalog.CrossOffTexturePath );

		var label = new Label { Parent = row, Text = text };
		UiStyle.Text( label, UiText.Body, BodyFontSize );
		label.Style.Set( "white-space", "nowrap" );
		label.Style.Set( "overflow", "hidden" );
		label.Style.Set( "pointer-events", "none" );
		return row;
	}

	static void StyleRadio( Radio radio, bool on, bool crossWhenOff )
	{
		if ( radio?.Outer is null || !radio.Outer.IsValid() )
			return;

		radio.Inner.Style.Set( "display", on ? "flex" : "none" );
		radio.Cross.Style.Set( "display", !on && crossWhenOff ? "flex" : "none" );
		radio.Outer.Style.Set( "border-color", UiStyle.Css( on ? UiTheme.Colors.AccentColor : UiTheme.Colors.BorderStrongColor ) );
	}

	/// <summary>One radio per crew mate; rebuilt only when the crew's membership changes (hit rects stay put).</summary>
	void RefreshCrewPinRows( bool force )
	{
		if ( _crewPinsBox is null || !_crewPinsBox.IsValid() )
			return;

		if ( !force && Time.NowDouble < _nextCrewRefreshAt )
			return;

		_nextCrewRefreshAt = Time.NowDouble + 0.5;
		var scene = Sandbox.Game.ActiveScene;
		CrewMapShare.CollectCrewMates( scene, CrewMapShare.FindLocalCrew( scene ), _crewMates );

		var key = "";
		for ( var i = 0; i < _crewMates.Count; i++ )
			key += _crewMates[i].PlayerKey.ToString( "N" ) + "=" + CrewRegistry.ResolvePawnDisplayName( _crewMates[i].GameObject ) + ";";

		if ( !force && string.Equals( key, _crewPinsKey, StringComparison.Ordinal ) )
			return;

		_crewPinsKey = key;
		_crewPinsBox.DeleteChildren( true );
		_shareTargets.RemoveAll( t => t.Panel is null || !t.Panel.IsValid() );
		_crewPinRadios.Clear();

		if ( _crewMates.Count == 0 )
		{
			var none = new Label { Parent = _crewPinsBox, Text = "No crew mates online" };
			UiStyle.Text( none, UiText.Muted, BodyFontSize );
			none.Style.PaddingLeft = Length.Pixels( 8f );
			none.Style.Set( "pointer-events", "none" );
			return;
		}

		for ( var i = 0; i < _crewMates.Count && i < CrewPinRows; i++ )
		{
			var mate = _crewMates[i];
			var mateKey = mate.PlayerKey;
			var name = CrewRegistry.ResolvePawnDisplayName( mate.GameObject );
			var row = MakeRadioRow( _crewPinsBox, $"Show {name}'s pins", out var radio );
			_crewPinRadios.Add( (mateKey, radio) );
			_shareTargets.Add( (row, () =>
			{
				CrewMapShare.SetShowPinsOf( mateKey, !CrewMapShare.IsShowingPinsOf( mateKey ) );
				RefreshToolHighlights();
			}) );
		}

		RefreshToolHighlights();
	}

	static Panel AddSectionHeader( Panel parent, string text )
	{
		var block = new Panel { Parent = parent };
		block.Style.Set( "flex-direction", "column" );
		block.Style.Set( "align-items", "center" );
		block.Style.Width = Length.Percent( 100 );
		block.Style.Set( "flex-shrink", "0" );
		block.Style.Set( "pointer-events", "none" );

		var label = new Label { Parent = block, Text = text };
		UiStyle.Text( label, UiText.Header, HeaderFontSize );
		label.Style.Set( "pointer-events", "none" );

		// Same 3 px gap under the header as before — the divider's bottom margin is dropped.
		var rule = UiStyle.Divider( block, 1f, 3f );
		rule.Style.MarginBottom = Length.Pixels( 0f );
		return block;
	}

	static void AddControlLine( Panel parent, string text )
	{
		var label = new Label { Parent = parent, Text = text };
		label.Style.Set( "flex-shrink", "0" );
		UiStyle.Text( label, UiText.Body, BodyFontSize );
		label.Style.MarginTop = Length.Pixels( 4f );
		label.Style.Set( "white-space", "nowrap" );
		label.Style.Set( "pointer-events", "none" );
	}

	static Panel MakeIconButton( Panel parent, string texturePath )
	{
		var btn = new Panel { Parent = parent };
		btn.Style.Set( "flex-shrink", "0" );
		btn.Style.Width = Length.Pixels( ToolIconSize );
		btn.Style.Height = Length.Pixels( ToolIconSize );
		UiStyle.Button( btn, UiButton.Ghost );
		btn.Style.Set( "pointer-events", "none" );
		btn.Style.Set( "overflow", "hidden" );

		// A plain flex child, never absolute: an absolute icon here anchored to the whole
		// section and painted the pin shapes across the map at full size. Inset by a fixed
		// margin instead of button padding so the tile's outer size stays ToolIconSize + border
		// (padding grew the old tiles to 64 px and only two pins fit a row).
		var icon = new Panel { Parent = btn };
		icon.Style.Width = Length.Pixels( ToolIconSize - 2f * ToolIconInset );
		icon.Style.Height = Length.Pixels( ToolIconSize - 2f * ToolIconInset );
		icon.Style.Set( "flex-shrink", "0" );
		icon.Style.Set( "pointer-events", "none" );
		MenuUiTextures.ApplyBackground( icon, texturePath );
		return btn;
	}

	void BuildNameEntry()
	{
		_nameEntry = new TextEntry { Parent = _sectionRoot };
		_nameEntry.Style.Set( "position", "absolute" );
		_nameEntry.Style.Width = Length.Pixels( 200f );
		_nameEntry.Style.Height = Length.Pixels( 30f );
		_nameEntry.Style.PaddingLeft = Length.Pixels( 6f );
		_nameEntry.Style.PaddingRight = Length.Pixels( 6f );
		UiStyle.Field( _nameEntry );
		// The entry floats over the map while a pin is named — accent border so it reads as live.
		_nameEntry.Style.Set( "border-color", UiStyle.Css( UiTheme.Colors.AccentColor ) );
		_nameEntry.Style.Set( "z-index", "50" );
		_nameEntry.Style.Set( "display", "none" );
		_nameEntry.Placeholder = "Name this pin";
		_nameEntry.AddEventListener( "onsubmit", () => CommitNaming() );
	}

	public void Refresh() { }

	/// <summary>"Dungeon: floor 2/3 · rooms 5/38" — text only changes when discovery or the floor changes.</summary>
	void RefreshDungeonLine()
	{
		if ( _dungeonLine is null || !_dungeonLine.IsValid() )
			return;

		var gen = BoxDungeonGenerator.Active;
		if ( gen is null || !gen.IsValid() || gen.Map is null )
		{
			if ( _dungeonLineKey != long.MinValue )
			{
				_dungeonLineKey = long.MinValue;
				_dungeonLine.Text = "Dungeon: —";
			}
			return;
		}

		var key = unchecked( ((long)gen.GetHashCode() << 32) ^ ((long)gen.Exploration.Version << 8) ^ (uint)gen.Exploration.CurrentFloor );
		if ( key == _dungeonLineKey )
			return;

		_dungeonLineKey = key;
		_dungeonLine.Text = $"Dungeon: floor {gen.Exploration.CurrentFloor + 1}/{gen.Map.Floors} · rooms {gen.VisitedRoomCount}/{gen.RoomCount}";
	}

	public void SetMenuOpen( bool isOpen )
	{
		if ( !isOpen )
		{
			CommitNaming();
			EndDrag();
			EndPan();
			_face.ResetPan();
		}

		_menuOpen = isOpen;
		UpdateVisibility();
	}

	public void SetPanelVisible( bool visible )
	{
		if ( !visible )
		{
			CommitNaming();
			EndDrag();
			EndPan();
			_face.ResetPan();
		}

		_panelVisible = visible;
		UpdateVisibility();
	}

	public void TickMenu( bool menuOpen )
	{
		if ( !menuOpen || !_panelVisible )
			return;

		_face.Tick();
		_crewPanel.Tick();
		RefreshCrewPinRows( force: false );
		RefreshDungeonLine();
		RefreshToolHighlights();

		// Enter commits the name even if the entry's own submit event never fires.
		if ( _naming && Input.Keyboard.Pressed( "enter" ) )
			CommitNaming();
	}

	public void OnMenuGlobalMouseUp() { }

	/// <summary>Escape while naming a pin only closes the entry (keeps the pin), not the menu.</summary>
	public bool TryConsumeEscape()
	{
		if ( !IsCapturingText )
			return false;

		CommitNaming();
		return true;
	}

	/// <summary>Soft-cursor Attack1 on the map page — routed from the menu input overlay.</summary>
	public bool TrySelectAtScreen( Vector2 screenPos )
	{
		if ( !_menuOpen || !_panelVisible )
			return false;

		if ( _naming )
		{
			// Clicking the entry keeps typing; clicking anywhere else commits the name AND still
			// counts as a click (a pin icon swaps on the first press, not the second).
			if ( _nameEntry is not null && InventoryScreenPointer.PanelBoxContainsScreen( _nameEntry, screenPos ) )
				return true;

			CommitNaming();
		}

		if ( _crewPanel.TryClickAtScreen( screenPos ) )
			return true;

		if ( TryClickTargets( _toolTargets, screenPos ) || TryClickTargets( _shareTargets, screenPos ) )
			return true;

		if ( !_face.ContainsScreen( screenPos ) )
			return false;

		if ( LocalMapMarkup.Tool != MapMarkupTool.Pin )
			return true; // pen / eraser: LMB only pans (drag tick); strokes are RMB

		var pin = _face.FindPinAtScreen( screenPos );
		if ( pin is not null )
		{
			LocalMapMarkup.ToggleCrossedOff( pin.Id );
			_lastMapClickAt = -10;
			return true;
		}

		var now = Time.NowDouble;
		var isDouble = now - _lastMapClickAt <= DoubleClickSeconds
		               && (screenPos - _lastMapClickPos).Length <= DoubleClickPixels;
		if ( !isDouble )
		{
			_lastMapClickAt = now;
			_lastMapClickPos = screenPos;
			return true;
		}

		_lastMapClickAt = -10;
		if ( !_face.TryScreenToWorldMeters( screenPos, out var meters ) )
			return true;

		var created = LocalMapMarkup.AddPin( LocalMapMarkup.SelectedPinIcon, meters );
		_interaction?.Components.Get<PlayerQuests>()?.OwnerReport( QuestEventIds.MapMarkerPlaced, created.Icon );
		BeginNaming( created.Id, screenPos );
		return true;
	}

	static bool TryClickTargets( List<(Panel Panel, Action Action)> targets, Vector2 screenPos )
	{
		for ( var i = 0; i < targets.Count; i++ )
		{
			var (panel, action) = targets[i];
			if ( panel is null || !panel.IsValid() )
				continue;
			if ( !InventoryScreenPointer.PanelBoxContainsScreen( panel, screenPos ) )
				continue;

			action?.Invoke();
			return true;
		}

		return false;
	}

	/// <summary>Soft-cursor Attack2 on the map page: remove the pin under the pointer.</summary>
	public bool TrySecondaryAtScreen( Vector2 screenPos )
	{
		if ( !_menuOpen || !_panelVisible || !_face.ContainsScreen( screenPos ) )
			return false;

		if ( _naming )
			CommitNaming();

		// Pen / eraser draw with the right button (the left one drags the map); removal is pin-tool only.
		if ( LocalMapMarkup.Tool != MapMarkupTool.Pin )
			return true;

		var pin = _face.FindPinAtScreen( screenPos );
		if ( pin is not null )
			LocalMapMarkup.RemovePin( pin.Id );

		return true;
	}

	/// <summary>Soft-cursor middle mouse on the map page: ping the spot to the crew.</summary>
	public bool TryMiddleAtScreen( Vector2 screenPos, PlayerCrew crew )
	{
		if ( !_menuOpen || !_panelVisible || !_face.TryScreenToWorldMeters( screenPos, out var meters ) )
			return false;

		if ( crew is not null && crew.IsValid() )
			crew.OwnerSendMapPing( meters );
		else
			MapPingFeed.Add( meters, CrewRegistry.ResolvePawnDisplayName( _interaction?.GameObject ) );

		return true;
	}

	/// <summary>Every frame while the menu is open: Attack1 drags the map, Attack2 drives pen / eraser strokes.</summary>
	public void TickPointerDrag( Vector2 screenPos, bool held )
	{
		if ( !_menuOpen || !_panelVisible )
			return;

		TickPan( screenPos, held );

		var drawHeld = Input.Down( "Attack2" );
		if ( !drawHeld )
		{
			if ( _dragging )
				EndDrag();
			return;
		}

		var tool = LocalMapMarkup.Tool;
		if ( !_dragging )
		{
			if ( _naming || tool == MapMarkupTool.Pin || !_face.TryScreenToWorldMeters( screenPos, out var start ) )
				return;

			_dragging = true;
			_dragTool = tool;
			var ppm = MathF.Max( 0.0001f, _face.ScreenPixelsPerMeter() );
			if ( tool == MapMarkupTool.Pen )
				_activeStroke = LocalMapMarkup.BeginStroke( LocalMapMarkup.PenWidthPixels / ppm, start );
			else
				_eraseChanged = LocalMapMarkup.Erase( start, LocalMapMarkup.EraserRadiusPixels / ppm );
			return;
		}

		if ( !_face.TryScreenToWorldMeters( screenPos, out var point ) )
			return;

		var pixelsPerMeter = MathF.Max( 0.0001f, _face.ScreenPixelsPerMeter() );
		if ( _dragTool == MapMarkupTool.Pen )
			LocalMapMarkup.AppendStrokePoint( _activeStroke, point, LocalMapMarkup.StrokePointSpacingPixels / pixelsPerMeter );
		else if ( LocalMapMarkup.Erase( point, LocalMapMarkup.EraserRadiusPixels / pixelsPerMeter ) )
			_eraseChanged = true;
	}

	/// <summary>
	/// Left click-and-drag pans the map with every tool (a press on a pin is a click, not a pan).
	/// A press only becomes a pan after <see cref="PanStartPixels"/> of travel, so single and
	/// double clicks are unaffected.
	/// </summary>
	void TickPan( Vector2 screenPos, bool primaryHeld )
	{
		if ( _panArmed )
		{
			if ( !primaryHeld )
			{
				EndPan();
				return;
			}

			if ( !_panning && (screenPos - _panPressPos).Length >= PanStartPixels )
				_panning = true;

			if ( _panning )
			{
				_face.PanByScreenPixels( screenPos - _panLastPos );
				_panLastPos = screenPos;
			}

			return;
		}

		if ( _naming || _dragging || !primaryHeld )
			return;

		if ( !_face.ContainsScreen( screenPos ) || _face.FindPinAtScreen( screenPos ) is not null )
			return;

		_panArmed = true;
		_panning = false;
		_panPressPos = screenPos;
		_panLastPos = screenPos;
	}

	void EndPan()
	{
		_panArmed = false;
		_panning = false;
	}

	void EndDrag()
	{
		if ( !_dragging )
			return;

		_dragging = false;
		if ( _dragTool == MapMarkupTool.Pen )
			LocalMapMarkup.EndStroke( _activeStroke );
		else if ( _eraseChanged )
			LocalMapMarkup.CommitErase();

		_activeStroke = null;
		_eraseChanged = false;
	}

	/// <summary>Menu mouse wheel on the map page — zooms over the map, otherwise scrolls the nearby players list.</summary>
	public void ApplyWheel( Vector2 wheel )
	{
		if ( !_menuOpen || !_panelVisible )
			return;

		var pointer = InventoryScreenPointer.GetMenuOrMousePosition();
		if ( _face.ContainsScreen( pointer ) )
		{
			// Menu sink convention: positive Y = wheel down → zoom out.
			var delta = MathF.Abs( wheel.y ) >= MathF.Abs( wheel.x ) ? wheel.y : wheel.x;
			if ( MathF.Abs( delta ) < 0.01f )
				return;

			for ( var i = 0; i < WheelZoomStepsPerNotch; i++ )
			{
				if ( delta < 0f )
					TerrainMinimapZoom.TryZoomIn();
				else
					TerrainMinimapZoom.TryZoomOut();
			}

			RefreshToolHighlights();
			return;
		}

		_crewPanel.ApplyNearbyWheel( wheel );
	}

	void BeginNaming( Guid pinId, Vector2 screenPos )
	{
		if ( _nameEntry is null || !_nameEntry.IsValid() || _sectionRoot is null )
			return;

		_naming = true;
		_namingPinId = pinId;

		var origin = _sectionRoot.PanelPositionToScreenPosition( Vector2.Zero );
		var scale = _sectionRoot.ScaleToScreen > 0.001f ? _sectionRoot.ScaleToScreen : 1f;
		var local = (screenPos - origin) / scale;
		_nameEntry.Style.Left = Length.Pixels( local.x + TerrainWorldMapFace.PinSizePixels * 0.6f );
		_nameEntry.Style.Top = Length.Pixels( local.y - 13f );
		_nameEntry.Text = "";
		_nameEntry.Style.Set( "display", "flex" );
		_nameEntry.Focus();
	}

	void CommitNaming()
	{
		if ( !_naming )
			return;

		_naming = false;
		var name = _nameEntry?.Text ?? "";
		LocalMapMarkup.RenamePin( _namingPinId, name );
		_namingPinId = Guid.Empty;

		if ( _nameEntry is not null && _nameEntry.IsValid() )
		{
			_nameEntry.Blur();
			_nameEntry.Style.Set( "display", "none" );
		}

		// Nothing on the page should keep keyboard focus once the name is in — a focused entry
		// would swallow the next click.
		if ( InputFocus.Current == _nameEntry )
			InputFocus.Clear();
	}

	void RefreshToolHighlights()
	{
		var tool = LocalMapMarkup.Tool;
		foreach ( var (id, btn) in _pinButtons )
		{
			var selected = tool == MapMarkupTool.Pin
			               && string.Equals( id, LocalMapMarkup.SelectedPinIcon, StringComparison.OrdinalIgnoreCase );
			StyleSelectable( btn, selected );
		}

		StyleSelectable( _penButton, tool == MapMarkupTool.Pen );
		StyleSelectable( _eraserButton, tool == MapMarkupTool.Eraser );

		if ( _eyeButton is not null && _eyeButton.IsValid() )
		{
			MenuUiTextures.ApplyBackground( _eyeButton, LocalMapMarkup.ShowPins ? "ui/map/tool_eye_on.png" : "ui/map/tool_eye_off.png" );
			_eyeButton.Style.Set( "background-size", "80% 80%" );
			_eyeButton.Style.Set( "background-position", "center" );
			_eyeButton.Style.Set( "background-repeat", "no-repeat" );
			// ApplyBackground clears the fill; keep the button looking like its + / - neighbours.
			UiStyle.SetButtonState( _eyeButton, UiButton.Ghost, hovered: false, pressed: false, disabled: false );
		}

		StyleZoomButton( _zoomInButton, TerrainMinimapZoom.Level < TerrainMinimapZoom.Max - 0.001f );
		StyleZoomButton( _zoomOutButton, TerrainMinimapZoom.Level > TerrainMinimapZoom.Min + 0.001f );

		StyleRadio( _shareLocationRadio, LocalMapMarkup.ShareLocation, crossWhenOff: true );
		for ( var i = 0; i < _crewPinRadios.Count; i++ )
			StyleRadio( _crewPinRadios[i].Radio, CrewMapShare.IsShowingPinsOf( _crewPinRadios[i].Key ), crossWhenOff: false );
	}

	static void StyleSelectable( Panel btn, bool selected )
	{
		if ( btn is null || !btn.IsValid() )
			return;

		// The selected tool reads as pressed with the accent border.
		UiStyle.SetButtonState( btn, UiButton.Ghost, hovered: false, pressed: selected, disabled: false );
		if ( selected )
			btn.Style.Set( "border-color", UiStyle.Css( UiTheme.Colors.AccentColor ) );
	}

	static void StyleZoomButton( Panel btn, bool canPress )
	{
		if ( btn is null || !btn.IsValid() )
			return;

		UiStyle.SetButtonState( btn, UiButton.Ghost, hovered: false, pressed: false, disabled: !canPress );
	}

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}
}
