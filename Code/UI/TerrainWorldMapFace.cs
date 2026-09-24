using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Shared biome-map face for HUD minimap and Map menu.
/// Prefer a file-backed UI texture — <c>Bitmap.ToTexture</c> often fails as a Panel background.
/// Without a <see cref="TerrainWorldManager"/> (hand-built test scenes) it is a plain local radar
/// centered on the camera, so world markers still have somewhere to show. Markers: the viewer's
/// cross + heading, the red ring around a base under raid (<see cref="BaseRaidSession"/>), and the
/// player's own markup (<see cref="LocalMapMarkup"/>: pins at a constant screen size, pen strokes
/// as rotated segments, crew pings). Markup lives on a world-anchored layer inside the zoom stage,
/// so panning the radar or zooming the map moves one panel, not every pin.
/// </summary>
public sealed class TerrainWorldMapFace
{
	public const float DefaultMinimapSize = 180f;

	/// <summary>Local radar (no TerrainWorld): meters across the face at the widest zoom.</summary>
	const float LocalRadarSpanMeters = 2400f;
	/// <summary>The raid ring never shrinks below this on screen, however far out the map is zoomed.</summary>
	const float RaidRingMinPixels = 14f;
	/// <summary>Pins keep this size on screen at every zoom.</summary>
	public const float PinSizePixels = 28f;
	const float PinLabelWidth = 120f;
	const float StrokeMinPixels = 1.5f;
	const float PingSizePixels = 34f;

	sealed class PinView
	{
		public Guid Id;
		public Panel Root;
	}

	Panel _host;
	Panel _zoomStage;
	Image _mapImage;
	Panel _mapFallback;
	Label _placeholder;
	Panel _markerCrossH;
	Panel _markerCrossV;
	Panel _heading;
	Panel _raidRing;
	Panel _worldLayer;
	Panel _strokeLayer;
	Panel _pinLayer;
	Panel _pingLayer;
	Panel _crewLayer;
	float _sizePixels;
	bool _fillParent;
	bool _showPinNames;
	TerrainWorldManager _manager;
	TerrainPreviewSettings _settings;
	Texture _boundTexture;
	float _appliedZoom = -1f;
	float _appliedStagePixels = -1f;
	Vector2 _lastFocusUv = new( -1f, -1f );
	Vector2 _radarOriginMeters;
	bool _radarMode;
	int _builtMarkupVersion = -1;
	bool _builtMarkupRadarMode;
	int _builtPingVersion = -1;
	float _builtStagePixels = -1f;
	float _builtSpanMeters = -1f;
	bool _builtShowPins = true;
	readonly List<PinView> _pinViews = new();
	readonly List<Panel> _strokePanels = new();
	readonly List<(Panel Ring, double StartedAt)> _pingPanels = new();
	readonly List<PlayerCrew> _crewMates = new();
	readonly Dictionary<Guid, Panel> _crewMarkers = new();
	readonly List<Guid> _staleCrewKeys = new();
	int _builtRemotePinKey;

	public Panel Host => _host;

	/// <summary>True while a TerrainWorld is streaming (map is the world); false in local-radar mode.</summary>
	public bool HasWorldMap => !_radarMode && _manager is not null;

	public void Build( Panel parent, float sizePixels, bool fillParent )
	{
		_sizePixels = sizePixels;
		_fillParent = fillParent;
		_showPinNames = fillParent;
		_host = new Panel { Parent = parent };
		if ( fillParent )
		{
			_host.Style.Set( "position", "relative" );
			_host.Style.Set( "flex-grow", "1" );
			_host.Style.Width = Length.Percent( 100 );
			_host.Style.Height = Length.Percent( 100 );
			_host.Style.Set( "min-height", "280px" );
		}
		else
		{
			_host.Style.Set( "position", "relative" );
			_host.Style.Width = Length.Pixels( sizePixels );
			_host.Style.Height = Length.Pixels( sizePixels );
		}

		_host.Style.Set( "overflow", "hidden" );
		_host.Style.Set( "pointer-events", "none" );
		_host.Style.Set( "border-radius", fillParent ? "6px" : "8px" );
		_host.Style.Set( "border-width", "1px" );
		_host.Style.Set( "border-color", "#3a4250" );
		_host.Style.BackgroundColor = new Color( 0.06f, 0.08f, 0.10f, 0.92f );

		_zoomStage = new Panel { Parent = _host };
		_zoomStage.Style.Set( "position", "absolute" );
		_zoomStage.Style.Set( "left", "0" );
		_zoomStage.Style.Set( "top", "0" );
		_zoomStage.Style.Set( "overflow", "visible" );
		_zoomStage.Style.Set( "pointer-events", "none" );

		_mapFallback = new Panel { Parent = _zoomStage };
		FillAbsolute( _mapFallback );
		_mapFallback.Style.Set( "background-size", "100% 100%" );
		_mapFallback.Style.Set( "background-repeat", "no-repeat" );
		_mapFallback.Style.Set( "background-position", "center" );

		_mapImage = new Image { Parent = _zoomStage };
		FillAbsolute( _mapImage );
		_mapImage.Style.Set( "background-size", "100% 100%" );
		_mapImage.Style.Set( "background-repeat", "no-repeat" );
		_mapImage.Style.Set( "background-position", "center" );
		_mapImage.Style.Set( "object-fit", "fill" );

		// World-anchored markup: children sit at fixed map UV; in radar mode the whole layer
		// slides with the camera instead of every child being re-placed.
		_worldLayer = new Panel { Parent = _zoomStage };
		FillAbsolute( _worldLayer );
		_worldLayer.Style.Set( "overflow", "visible" );
		_worldLayer.Style.Set( "pointer-events", "none" );
		_worldLayer.Style.Set( "z-index", "2" );

		_strokeLayer = CreateLayer( _worldLayer, 1 );
		_pinLayer = CreateLayer( _worldLayer, 3 );
		_pingLayer = CreateLayer( _worldLayer, 4 );
		_crewLayer = CreateLayer( _worldLayer, 5 );

		_placeholder = new Label { Parent = _host, Text = "Generating map…" };
		FillAbsolute( _placeholder );
		_placeholder.Style.Set( "align-items", "center" );
		_placeholder.Style.Set( "justify-content", "center" );
		_placeholder.Style.FontColor = new Color( 0.75f, 0.78f, 0.82f, 0.55f );
		_placeholder.Style.FontSize = Length.Pixels( fillParent ? 22f : 12f );
		_placeholder.Style.Set( "pointer-events", "none" );
		_placeholder.Style.Set( "z-index", "2" );

		_markerCrossH = CreateMarkerBar( horizontal: true );
		_markerCrossV = CreateMarkerBar( horizontal: false );
		_heading = new Panel { Parent = _zoomStage };
		_heading.Style.Set( "position", "absolute" );
		_heading.Style.Width = Length.Pixels( 2f );
		_heading.Style.Height = Length.Pixels( 16f );
		_heading.Style.BackgroundColor = new Color( 0.2f, 0.95f, 1f, 0.95f );
		_heading.Style.Set( "transform-origin", "center bottom" );
		_heading.Style.Set( "pointer-events", "none" );
		_heading.Style.Set( "display", "none" );
		_heading.Style.Set( "z-index", "4" );

		_raidRing = new Panel { Parent = _zoomStage };
		_raidRing.Style.Set( "position", "absolute" );
		_raidRing.Style.Set( "border-width", "2px" );
		_raidRing.Style.Set( "border-color", "#ff3b30" );
		_raidRing.Style.Set( "border-radius", "50%" );
		_raidRing.Style.BackgroundColor = new Color( 1f, 0.2f, 0.15f, 0.18f );
		_raidRing.Style.Set( "pointer-events", "none" );
		_raidRing.Style.Set( "display", "none" );
		_raidRing.Style.Set( "z-index", "2" );

		ApplyZoomLayout( focusUv: new Vector2( 0.5f, 0.5f ), force: true );
	}

	static Panel CreateLayer( Panel parent, int zIndex )
	{
		var layer = new Panel { Parent = parent };
		FillAbsolute( layer );
		layer.Style.Set( "overflow", "visible" );
		layer.Style.Set( "pointer-events", "none" );
		layer.Style.Set( "z-index", zIndex.ToString() );
		return layer;
	}

	static void FillAbsolute( Panel panel )
	{
		panel.Style.Set( "position", "absolute" );
		panel.Style.Set( "left", "0" );
		panel.Style.Set( "top", "0" );
		panel.Style.Set( "right", "0" );
		panel.Style.Set( "bottom", "0" );
	}

	Panel CreateMarkerBar( bool horizontal )
	{
		var bar = new Panel { Parent = _zoomStage };
		bar.Style.Set( "position", "absolute" );
		if ( horizontal )
		{
			bar.Style.Width = Length.Pixels( 12f );
			bar.Style.Height = Length.Pixels( 2f );
		}
		else
		{
			bar.Style.Width = Length.Pixels( 2f );
			bar.Style.Height = Length.Pixels( 12f );
		}

		bar.Style.BackgroundColor = Color.White.WithAlpha( 0.95f );
		bar.Style.Set( "pointer-events", "none" );
		bar.Style.Set( "display", "none" );
		bar.Style.Set( "z-index", "3" );
		return bar;
	}

	public void Tick()
	{
		if ( _host is null || !_host.IsValid() )
			return;

		EnsureManager();
		BindMapTexture();
		UpdateMarkerAndZoom();
		UpdateMarkup();
	}

	void EnsureManager()
	{
		if ( _manager is not null && _manager.IsValid() )
			return;

		_manager = null;
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		foreach ( var manager in scene.GetAllComponents<TerrainWorldManager>() )
		{
			if ( manager is null || !manager.IsValid() )
				continue;

			_manager = manager;
			return;
		}
	}

	void BindMapTexture()
	{
		var map = _manager is not null && _manager.IsValid()
			? _manager.GetHudBiomeMapTexture()
			: null;

		if ( map is not null && map.IsValid() )
		{
			if ( _boundTexture == map )
			{
				if ( _placeholder is not null )
					_placeholder.Style.Set( "display", "none" );
				return;
			}

			_boundTexture = map;
			if ( _mapImage is not null )
				_mapImage.Texture = map;
			if ( _mapFallback is not null )
			{
				_mapFallback.Style.SetBackgroundImage( map );
				_mapFallback.Style.Set( "background-size", "100% 100%" );
				_mapFallback.Style.Set( "background-repeat", "no-repeat" );
				_mapFallback.Style.Set( "background-position", "center" );
			}

			if ( _placeholder is not null )
				_placeholder.Style.Set( "display", "none" );
			return;
		}

		if ( _boundTexture is not null )
		{
			_boundTexture = null;
			if ( _mapImage is not null )
				_mapImage.Texture = null;
			if ( _mapFallback is not null )
			{
				_mapFallback.Style.BackgroundImage = null;
				_mapFallback.Style.Set( "background-image", "none" );
			}
		}

		if ( _placeholder is not null )
		{
			// No TerrainWorld at all: the face is a local radar, not a map that is still coming.
			if ( _manager is null )
			{
				_placeholder.Style.Set( "display", "none" );
				return;
			}

			_placeholder.Style.Set( "display", "flex" );
			if ( _manager is not null && _manager.IsValid() && _manager.IsMapGenerating )
				_placeholder.Text = $"Map {_manager.MapGenerationProgress01 * 100f:0}%";
			else
				_placeholder.Text = "Waiting for map…";
		}
	}

	void UpdateMarkerAndZoom()
	{
		if ( _manager is null )
		{
			UpdateLocalRadar();
			return;
		}

		_radarMode = false;
		var focusUv = new Vector2( 0.5f, 0.5f );
		TerrainPreviewSettings streamSettings = null;
		var hasStream = false;

		if ( _manager is not null && _manager.IsValid() && _manager.HasStreamPosition )
		{
			var settings = _manager.BuildGenerationSettings();
			if ( settings is not null )
			{
				focusUv = TerrainBiomeMapCoordinates.WorldMetersToPreviewNormalized(
					_manager.StreamXMeters,
					_manager.StreamYMeters,
					settings );
				hasStream = true;
				streamSettings = settings;
			}
		}

		if ( streamSettings is not null )
			_settings = streamSettings;
		else
			_settings ??= _manager.BuildGenerationSettings();

		ApplyZoomLayout( focusUv, force: false );
		PlaceWorldLayer();

		if ( !hasStream || _boundTexture is null )
		{
			SetMarkerVisible( false );
			SetRaidRingVisible( false );
			return;
		}

		UpdateRaidRingOnMap( streamSettings );

		// Markers live on the zoom stage in full-map UV space (0–100%).
		PlaceCenter( _markerCrossH, focusUv, -6f, -1f );
		PlaceCenter( _markerCrossV, focusUv, -1f, -6f );

		var dir = _manager.StreamLookDirectionMap;
		if ( dir.LengthSquared > 1e-8f )
		{
			dir = dir.Normal;
			var deg = MathF.Atan2( dir.x, -dir.y ) * (180f / MathF.PI);
			PlaceCenter( _heading, focusUv, -1f, -16f );
			_heading.Style.Set( "transform", $"rotate({deg:0.##}deg)" );
			_heading.Style.Set( "display", "flex" );
		}
		else if ( _heading is not null )
		{
			_heading.Style.Set( "display", "none" );
		}

		SetMarkerVisible( true );
	}

	/// <summary>No TerrainWorld: camera-centered radar — the viewer in the middle, north up like the map.</summary>
	void UpdateLocalRadar()
	{
		_radarMode = true;
		ApplyZoomLayout( new Vector2( 0.5f, 0.5f ), force: false );

		var camera = Sandbox.Game.ActiveScene?.Camera;
		if ( camera is null || !camera.IsValid() )
		{
			SetMarkerVisible( false );
			SetRaidRingVisible( false );
			return;
		}

		var camMeters = TerrainWorldUnits.EngineToMeters( camera.WorldPosition );
		_radarOriginMeters = new Vector2( camMeters.x, camMeters.y );
		PlaceWorldLayer();

		var center = new Vector2( 0.5f, 0.5f );
		PlaceCenter( _markerCrossH, center, -6f, -1f );
		PlaceCenter( _markerCrossV, center, -1f, -6f );
		SetMarkerVisible( true );

		var dir = TerrainBiomeMapCoordinates.WorldForwardToPreviewMapDirection( camera.WorldRotation.Forward );
		if ( dir.LengthSquared > 1e-8f && _heading is not null )
		{
			dir = dir.Normal;
			var deg = MathF.Atan2( dir.x, -dir.y ) * (180f / MathF.PI);
			PlaceCenter( _heading, center, -1f, -16f );
			_heading.Style.Set( "transform", $"rotate({deg:0.##}deg)" );
			_heading.Style.Set( "display", "flex" );
		}

		var raid = ActiveRaid();
		if ( raid is null )
		{
			SetRaidRingVisible( false );
			return;
		}

		// Same mirror as the biome map: +X reads leftward, +Y downward.
		var offsetMeters = TerrainWorldUnits.EngineToMeters( raid.RaidCenter - camera.WorldPosition );
		var uv = new Vector2(
			0.5f - offsetMeters.x / LocalRadarSpanMeters,
			0.5f + offsetMeters.y / LocalRadarSpanMeters );
		var radiusUv = TerrainWorldUnits.EngineToMeters( raid.RaidRadiusUnits ) / LocalRadarSpanMeters;
		PlaceRaidRing( uv, radiusUv );
	}

	void UpdateRaidRingOnMap( TerrainPreviewSettings settings )
	{
		var raid = ActiveRaid();
		if ( raid is null || settings is null || settings.WorldDiameterMeters <= 0f )
		{
			SetRaidRingVisible( false );
			return;
		}

		var centerMeters = TerrainWorldUnits.EngineToMeters( raid.RaidCenter );
		var uv = TerrainBiomeMapCoordinates.WorldMetersToPreviewNormalized( centerMeters.x, centerMeters.y, settings );
		var radiusUv = TerrainWorldUnits.EngineToMeters( raid.RaidRadiusUnits ) / settings.WorldDiameterMeters;
		PlaceRaidRing( uv, radiusUv );
	}

	static BaseRaidSession ActiveRaid() =>
		BaseRaidSession.Instance is { } raid && raid.IsValid() && raid.IsRaidActive ? raid : null;

	/// <summary>Ring centered on <paramref name="uv"/> (zoom-stage space); diameter follows the zoom, never under <see cref="RaidRingMinPixels"/>.</summary>
	void PlaceRaidRing( Vector2 uv, float radiusUv )
	{
		if ( _raidRing is null || !_raidRing.IsValid() )
			return;

		var stagePixels = StagePixels();
		var diameter = MathF.Max( RaidRingMinPixels, radiusUv * 2f * stagePixels );
		_raidRing.Style.Width = Length.Pixels( diameter );
		_raidRing.Style.Height = Length.Pixels( diameter );
		PlaceCenter( _raidRing, uv, -diameter * 0.5f, -diameter * 0.5f );
		SetRaidRingVisible( true );
	}

	/// <summary>Unmeasured fill-parent face (first frame) — any sane size until the layout box exists.</summary>
	const float UnmeasuredHostPixels = 600f;

	/// <summary>Face width in UI pixels (the Map menu face fills its parent, so it is measured).</summary>
	float HostWidthPixels()
	{
		if ( !_fillParent || _host is null || !_host.IsValid() )
			return _sizePixels;

		var scale = MathF.Max( 0.001f, _host.ScaleToScreen );
		var width = _host.Box.Rect.Width / scale;
		return width > 1f ? width : UnmeasuredHostPixels;
	}

	float HostHeightPixels()
	{
		if ( !_fillParent || _host is null || !_host.IsValid() )
			return _sizePixels;

		var scale = MathF.Max( 0.001f, _host.ScaleToScreen );
		var height = _host.Box.Rect.Height / scale;
		return height > 1f ? height : UnmeasuredHostPixels;
	}

	/// <summary>
	/// Zoom stage edge in UI pixels. The map texture is square and the face may be a rectangle, so
	/// the stage is a square of the longer edge × zoom: the window crops the map, never stretches it.
	/// </summary>
	float StagePixels() => MathF.Max( HostWidthPixels(), HostHeightPixels() ) * TerrainMinimapZoom.Level;

	void SetRaidRingVisible( bool visible )
	{
		if ( _raidRing is not null && _raidRing.IsValid() )
			_raidRing.Style.Set( "display", visible ? "flex" : "none" );
	}

	void ApplyZoomLayout( Vector2 focusUv, bool force )
	{
		if ( _zoomStage is null || !_zoomStage.IsValid() )
			return;

		var zoom = TerrainMinimapZoom.Level;
		var hostW = HostWidthPixels();
		var hostH = HostHeightPixels();
		var stage = MathF.Max( hostW, hostH ) * zoom;
		if ( !force
		     && Math.Abs( zoom - _appliedZoom ) < 0.001f
		     && Math.Abs( stage - _appliedStagePixels ) < 0.5f
		     && (focusUv - _lastFocusUv).LengthSquared < 1e-8f )
			return;

		_appliedZoom = zoom;
		_appliedStagePixels = stage;
		_lastFocusUv = focusUv;

		// The window may be a rectangle: each axis sees its own fraction of the square stage,
		// centered on the focus and clamped so the map edge never leaves a gap.
		var viewW = MathF.Min( 1f, hostW / stage );
		var viewH = MathF.Min( 1f, hostH / stage );
		var leftUv = Math.Clamp( focusUv.x - viewW * 0.5f, 0f, 1f - viewW );
		var topUv = Math.Clamp( focusUv.y - viewH * 0.5f, 0f, 1f - viewH );

		_zoomStage.Style.Width = Length.Pixels( stage );
		_zoomStage.Style.Height = Length.Pixels( stage );
		_zoomStage.Style.Left = Length.Pixels( -leftUv * stage );
		_zoomStage.Style.Top = Length.Pixels( -topUv * stage );
	}

	static void PlaceCenter( Panel panel, Vector2 uv01, float offsetXPx, float offsetYPx )
	{
		if ( panel is null || !panel.IsValid() )
			return;

		panel.Style.Left = Length.Percent( uv01.x * 100f );
		panel.Style.Top = Length.Percent( uv01.y * 100f );
		panel.Style.Set( "margin-left", $"{offsetXPx:0.##}px" );
		panel.Style.Set( "margin-top", $"{offsetYPx:0.##}px" );
	}

	void SetMarkerVisible( bool visible )
	{
		var display = visible ? "flex" : "none";
		if ( _markerCrossH is not null )
			_markerCrossH.Style.Set( "display", display );
		if ( _markerCrossV is not null )
			_markerCrossV.Style.Set( "display", display );
		if ( !visible && _heading is not null )
			_heading.Style.Set( "display", "none" );
	}

	// ------------------------------------------------------------------
	// World ↔ map space
	// ------------------------------------------------------------------

	/// <summary>Meters across the full zoom stage (world diameter, or the radar span).</summary>
	float SpanMeters()
	{
		if ( _radarMode || _settings is null || _settings.WorldDiameterMeters <= 0f )
			return LocalRadarSpanMeters;

		return _settings.WorldDiameterMeters;
	}

	/// <summary>UI pixels per world meter at the current zoom (stroke widths, brush sizes).</summary>
	public float PixelsPerMeter()
	{
		var span = SpanMeters();
		if ( span <= 0f )
			return 1f;

		return StagePixels() / span;
	}

	/// <summary>World meters → world-layer UV. Radar mode is origin-centered; the layer itself slides with the camera.</summary>
	Vector2 WorldToLayerUv( Vector2 meters )
	{
		if ( !_radarMode && _settings is not null && _settings.WorldDiameterMeters > 0f )
			return TerrainBiomeMapCoordinates.WorldMetersToPreviewNormalized( meters.x, meters.y, _settings );

		var span = LocalRadarSpanMeters;
		return new Vector2( 0.5f - meters.x / span, 0.5f + meters.y / span );
	}

	/// <summary>Zoom-stage UV (0–1 across the whole map) → world meters.</summary>
	Vector2 StageUvToWorld( Vector2 uv )
	{
		if ( !_radarMode && _settings is not null && _settings.WorldDiameterMeters > 0f )
		{
			var diameter = _settings.WorldDiameterMeters;
			var radius = _settings.WorldRadiusMeters;
			return new Vector2( (1f - uv.x) * diameter - radius, uv.y * diameter - radius );
		}

		var span = LocalRadarSpanMeters;
		return new Vector2(
			_radarOriginMeters.x + (0.5f - uv.x) * span,
			_radarOriginMeters.y + (uv.y - 0.5f) * span );
	}

	void PlaceWorldLayer()
	{
		if ( _worldLayer is null || !_worldLayer.IsValid() )
			return;

		if ( !_radarMode )
		{
			_worldLayer.Style.Left = Length.Percent( 0f );
			_worldLayer.Style.Top = Length.Percent( 0f );
			return;
		}

		var span = LocalRadarSpanMeters;
		_worldLayer.Style.Left = Length.Percent( _radarOriginMeters.x / span * 100f );
		_worldLayer.Style.Top = Length.Percent( -_radarOriginMeters.y / span * 100f );
	}

	/// <summary>True when <paramref name="screenPos"/> is over the visible map window.</summary>
	public bool ContainsScreen( Vector2 screenPos ) =>
		_host is not null && InventoryScreenPointer.PanelBoxContainsScreen( _host, screenPos );

	/// <summary>Screen position over the map → world meters from center. False outside the map or before it has a mode.</summary>
	public bool TryScreenToWorldMeters( Vector2 screenPos, out Vector2 meters )
	{
		meters = default;
		if ( !ContainsScreen( screenPos ) || _zoomStage is null || !_zoomStage.IsValid() )
			return false;

		var rect = _zoomStage.Box.Rect;
		if ( rect.Width < 1f || rect.Height < 1f )
			return false;

		var uv = new Vector2( (screenPos.x - rect.Left) / rect.Width, (screenPos.y - rect.Top) / rect.Height );
		meters = StageUvToWorld( uv );
		return true;
	}

	/// <summary>Screen pixels per world meter at the current zoom — for brush sizes given in screen pixels.</summary>
	public float ScreenPixelsPerMeter()
	{
		if ( _zoomStage is null || !_zoomStage.IsValid() )
			return PixelsPerMeter();

		var width = _zoomStage.Box.Rect.Width;
		var span = SpanMeters();
		return width > 1f && span > 0f ? width / span : PixelsPerMeter();
	}

	/// <summary>The pin under the pointer (topmost first), or null. Hidden pins never hit.</summary>
	public MapPinData FindPinAtScreen( Vector2 screenPos )
	{
		if ( !LocalMapMarkup.ShowPins || !ContainsScreen( screenPos ) )
			return null;

		for ( var i = _pinViews.Count - 1; i >= 0; i-- )
		{
			var view = _pinViews[i];
			if ( view.Root is null || !view.Root.IsValid() )
				continue;

			if ( !InventoryScreenPointer.PanelBoxContainsScreen( view.Root, screenPos ) )
				continue;

			return LocalMapMarkup.FindPin( view.Id );
		}

		return null;
	}

	/// <summary>Screen rect of a pin's icon (for anchoring the name entry). False when not shown.</summary>
	public bool TryGetPinScreenRect( Guid pinId, out Rect rect )
	{
		rect = default;
		foreach ( var view in _pinViews )
		{
			if ( view.Id != pinId || view.Root is null || !view.Root.IsValid() )
				continue;

			rect = view.Root.Box.Rect;
			return rect.Width > 0.5f;
		}

		return false;
	}

	// ------------------------------------------------------------------
	// Markup rendering
	// ------------------------------------------------------------------

	void UpdateMarkup()
	{
		if ( _worldLayer is null || !_worldLayer.IsValid() )
			return;

		LocalMapMarkup.EnsureLoaded();
		var stagePixels = StagePixels();
		var span = SpanMeters();
		var version = LocalMapMarkup.Version;
		var showPins = LocalMapMarkup.ShowPins;

		var geometryChanged = MathF.Abs( stagePixels - _builtStagePixels ) > 0.5f
		                      || MathF.Abs( span - _builtSpanMeters ) > 0.01f
		                      || _builtMarkupRadarMode != _radarMode;

		var scene = Sandbox.Game.ActiveScene;
		CrewMapShare.CollectCrewMates( scene, CrewMapShare.FindLocalCrew( scene ), _crewMates );
		var remoteKey = RemotePinKey();

		if ( version != _builtMarkupVersion || geometryChanged || showPins != _builtShowPins || remoteKey != _builtRemotePinKey )
		{
			_builtMarkupVersion = version;
			_builtStagePixels = stagePixels;
			_builtSpanMeters = span;
			_builtMarkupRadarMode = _radarMode;
			_builtShowPins = showPins;
			_builtRemotePinKey = remoteKey;
			RebuildStrokes( stagePixels, span );
			RebuildPins( showPins );
			_builtPingVersion = -1;
		}

		UpdateCrewMarkers();

		// Reading Pings prunes expired ones (and bumps the version), so compare afterwards.
		var pings = MapPingFeed.Pings;
		if ( MapPingFeed.Version != _builtPingVersion )
		{
			_builtPingVersion = MapPingFeed.Version;
			RebuildPings( pings );
		}

		AnimatePings();
	}

	void RebuildStrokes( float stagePixels, float spanMeters )
	{
		foreach ( var panel in _strokePanels )
			panel?.Delete( true );
		_strokePanels.Clear();

		if ( _strokeLayer is null || !_strokeLayer.IsValid() || spanMeters <= 0f )
			return;

		var pixelsPerMeter = stagePixels / spanMeters;
		var color = new Color( 1f, 0.92f, 0.1f, 0.95f );

		foreach ( var stroke in LocalMapMarkup.Strokes )
		{
			var count = stroke.PointCount;
			if ( count < 1 )
				continue;

			var thickness = MathF.Max( StrokeMinPixels, stroke.WidthMeters * pixelsPerMeter );
			if ( count == 1 )
			{
				AddStrokeSegment( WorldToLayerUv( stroke.PointAt( 0 ) ), WorldToLayerUv( stroke.PointAt( 0 ) ), 0f, thickness, color );
				continue;
			}

			for ( var i = 1; i < count; i++ )
			{
				var a = stroke.PointAt( i - 1 );
				var b = stroke.PointAt( i );
				var lengthPx = (b - a).Length * pixelsPerMeter;
				AddStrokeSegment( WorldToLayerUv( a ), WorldToLayerUv( b ), lengthPx, thickness, color );
			}
		}
	}

	/// <summary>One rotated bar between two layer-UV points; rounded ends so joints read as a continuous line.</summary>
	void AddStrokeSegment( Vector2 uvA, Vector2 uvB, float lengthPx, float thicknessPx, Color color )
	{
		var mid = (uvA + uvB) * 0.5f;
		var dx = uvB.x - uvA.x;
		var dy = uvB.y - uvA.y;
		var angleDeg = MathF.Atan2( dy, dx ) * (180f / MathF.PI);

		var seg = new Panel { Parent = _strokeLayer };
		seg.Style.Set( "position", "absolute" );
		seg.Style.Left = Length.Percent( mid.x * 100f );
		seg.Style.Top = Length.Percent( mid.y * 100f );
		seg.Style.Width = Length.Pixels( lengthPx + thicknessPx );
		seg.Style.Height = Length.Pixels( thicknessPx );
		seg.Style.Set( "margin-left", $"{-(lengthPx + thicknessPx) * 0.5f:0.##}px" );
		seg.Style.Set( "margin-top", $"{-thicknessPx * 0.5f:0.##}px" );
		seg.Style.Set( "border-radius", $"{thicknessPx * 0.5f:0.##}px" );
		seg.Style.Set( "transform-origin", "center center" );
		seg.Style.Set( "transform", $"rotate({angleDeg:0.##}deg)" );
		seg.Style.BackgroundColor = color;
		seg.Style.Set( "pointer-events", "none" );
		_strokePanels.Add( seg );
	}

	/// <summary>Changes when a crew mate's shared pins, or which mates are shown, change.</summary>
	int RemotePinKey()
	{
		var key = CrewMapShare.Version * 397;
		for ( var i = 0; i < _crewMates.Count; i++ )
		{
			var mate = _crewMates[i];
			if ( mate is null || !mate.IsValid() || !CrewMapShare.IsShowingPinsOf( mate.PlayerKey ) )
				continue;

			key = unchecked( key * 31 + (mate.SharedPinsBlob?.GetHashCode() ?? 0) + mate.PlayerKey.GetHashCode() );
		}

		return key;
	}

	void RebuildPins( bool showPins )
	{
		foreach ( var view in _pinViews )
			view.Root?.Delete( true );
		_pinViews.Clear();
		_pinLayer?.DeleteChildren( true );

		if ( _pinLayer is null || !_pinLayer.IsValid() )
			return;

		_pinLayer.Style.Set( "display", showPins ? "flex" : "none" );
		if ( !showPins )
			return;

		foreach ( var pin in LocalMapMarkup.Pins )
		{
			var root = CreatePinPanel( new Vector2( pin.XMeters, pin.YMeters ), pin.Icon, pin.Name, pin.CrossedOff, 1f );
			_pinViews.Add( new PinView { Id = pin.Id, Root = root } );
		}

		// Crew mates' pins the viewer opted into: drawn a little faded, labelled with the owner,
		// never hit-testable (only your own pins can be crossed off or removed).
		for ( var i = 0; i < _crewMates.Count; i++ )
		{
			var mate = _crewMates[i];
			if ( mate is null || !mate.IsValid() || !CrewMapShare.IsShowingPinsOf( mate.PlayerKey ) )
				continue;

			var owner = CrewRegistry.ResolvePawnDisplayName( mate.GameObject );
			var shared = mate.SharedPins;
			for ( var p = 0; p < shared.Count; p++ )
			{
				var pin = shared[p];
				var label = string.IsNullOrWhiteSpace( pin.Name ) ? owner : $"{pin.Name} · {owner}";
				CreatePinPanel( new Vector2( pin.XMeters, pin.YMeters ), pin.Icon, label, pin.CrossedOff, 0.8f );
			}
		}
	}

	Panel CreatePinPanel( Vector2 worldMeters, string icon, string name, bool crossedOff, float opacity )
	{
		var half = PinSizePixels * 0.5f;
		var uv = WorldToLayerUv( worldMeters );

		var root = new Panel { Parent = _pinLayer };
		root.Style.Set( "position", "absolute" );
		root.Style.Left = Length.Percent( uv.x * 100f );
		root.Style.Top = Length.Percent( uv.y * 100f );
		root.Style.Width = Length.Pixels( PinSizePixels );
		root.Style.Height = Length.Pixels( PinSizePixels );
		root.Style.Set( "margin-left", $"{-half:0.##}px" );
		root.Style.Set( "margin-top", $"{-half:0.##}px" );
		root.Style.Set( "overflow", "visible" );
		root.Style.Set( "pointer-events", "none" );
		root.Style.Set( "opacity", $"{(crossedOff ? opacity * 0.6f : opacity):0.##}" );
		MenuUiTextures.ApplyBackground( root, MapPinCatalog.TexturePathFor( icon ) );

		if ( crossedOff )
		{
			var cross = new Panel { Parent = root };
			cross.Style.Set( "position", "absolute" );
			cross.Style.Set( "left", "-4px" );
			cross.Style.Set( "top", "-4px" );
			cross.Style.Set( "right", "-4px" );
			cross.Style.Set( "bottom", "-4px" );
			cross.Style.Set( "pointer-events", "none" );
			MenuUiTextures.ApplyBackground( cross, MapPinCatalog.CrossOffTexturePath );
		}

		if ( _showPinNames && !string.IsNullOrWhiteSpace( name ) )
			AddMarkerLabel( root, name, PinSizePixels + 1f, Color.White );

		return root;
	}

	static void AddMarkerLabel( Panel parent, string text, float topPx, Color color )
	{
		var label = new Label { Parent = parent, Text = text };
		label.Style.Set( "position", "absolute" );
		label.Style.Set( "top", $"{topPx:0.##}px" );
		label.Style.Set( "left", $"{-(PinLabelWidth - PinSizePixels) * 0.5f:0.##}px" );
		label.Style.Width = Length.Pixels( PinLabelWidth );
		label.Style.Set( "justify-content", "center" );
		label.Style.Set( "text-align", "center" );
		label.Style.FontColor = color;
		label.Style.FontSize = Length.Pixels( 14f );
		label.Style.Set( "text-shadow", "0 0 3px #000, 1px 1px 0 #000" );
		label.Style.Set( "white-space", "nowrap" );
		label.Style.Set( "pointer-events", "none" );
	}

	/// <summary>Crew mates who share their location: a coloured dot that follows their pawn every tick.</summary>
	void UpdateCrewMarkers()
	{
		if ( _crewLayer is null || !_crewLayer.IsValid() )
			return;

		_staleCrewKeys.Clear();
		foreach ( var key in _crewMarkers.Keys )
			_staleCrewKeys.Add( key );

		const float dotSize = 14f;
		for ( var i = 0; i < _crewMates.Count; i++ )
		{
			var mate = _crewMates[i];
			if ( mate is null || !mate.IsValid() || !mate.ShareLocation )
				continue;

			var key = mate.PlayerKey;
			_staleCrewKeys.Remove( key );

			if ( !_crewMarkers.TryGetValue( key, out var dot ) || dot is null || !dot.IsValid() )
			{
				dot = new Panel { Parent = _crewLayer };
				dot.Style.Set( "position", "absolute" );
				dot.Style.Width = Length.Pixels( dotSize );
				dot.Style.Height = Length.Pixels( dotSize );
				dot.Style.Set( "border-radius", "50%" );
				dot.Style.Set( "border-width", "2px" );
				dot.Style.Set( "border-color", "#111111" );
				dot.Style.BackgroundColor = CrewMapShare.ColorFor( key );
				dot.Style.Set( "overflow", "visible" );
				dot.Style.Set( "pointer-events", "none" );
				if ( _showPinNames )
					AddMarkerLabel( dot, CrewRegistry.ResolvePawnDisplayName( mate.GameObject ), dotSize + 1f, CrewMapShare.ColorFor( key ) );
				_crewMarkers[key] = dot;
			}

			var meters = TerrainWorldUnits.EngineToMeters( mate.GameObject.WorldPosition );
			var uv = WorldToLayerUv( new Vector2( meters.x, meters.y ) );
			PlaceCenter( dot, uv, -dotSize * 0.5f, -dotSize * 0.5f );
		}

		for ( var i = 0; i < _staleCrewKeys.Count; i++ )
		{
			if ( _crewMarkers.TryGetValue( _staleCrewKeys[i], out var dot ) )
				dot?.Delete( true );
			_crewMarkers.Remove( _staleCrewKeys[i] );
		}
	}

	void RebuildPings( IReadOnlyList<MapPingFeed.Ping> pings )
	{
		foreach ( var (ring, _) in _pingPanels )
			ring?.Delete( true );
		_pingPanels.Clear();

		if ( _pingLayer is null || !_pingLayer.IsValid() )
			return;

		var half = PingSizePixels * 0.5f;
		foreach ( var ping in pings )
		{
			var uv = WorldToLayerUv( ping.WorldMeters );
			var ring = new Panel { Parent = _pingLayer };
			ring.Style.Set( "position", "absolute" );
			ring.Style.Left = Length.Percent( uv.x * 100f );
			ring.Style.Top = Length.Percent( uv.y * 100f );
			ring.Style.Width = Length.Pixels( PingSizePixels );
			ring.Style.Height = Length.Pixels( PingSizePixels );
			ring.Style.Set( "margin-left", $"{-half:0.##}px" );
			ring.Style.Set( "margin-top", $"{-half:0.##}px" );
			ring.Style.Set( "border-radius", "50%" );
			ring.Style.Set( "border-width", "3px" );
			ring.Style.Set( "border-color", "#ffe14d" );
			ring.Style.BackgroundColor = new Color( 1f, 0.88f, 0.3f, 0.25f );
			ring.Style.Set( "overflow", "visible" );
			ring.Style.Set( "pointer-events", "none" );

			if ( _showPinNames && !string.IsNullOrWhiteSpace( ping.SenderName ) )
			{
				var label = new Label { Parent = ring, Text = ping.SenderName };
				label.Style.Set( "position", "absolute" );
				label.Style.Set( "top", $"{PingSizePixels - 2f:0.##}px" );
				label.Style.Set( "left", $"{-(PinLabelWidth - PingSizePixels) * 0.5f:0.##}px" );
				label.Style.Width = Length.Pixels( PinLabelWidth );
				label.Style.Set( "justify-content", "center" );
				label.Style.Set( "text-align", "center" );
				label.Style.FontColor = new Color( 1f, 0.92f, 0.4f );
				label.Style.FontSize = Length.Pixels( 12f );
				label.Style.Set( "text-shadow", "0 0 3px #000, 1px 1px 0 #000" );
				label.Style.Set( "white-space", "nowrap" );
				label.Style.Set( "pointer-events", "none" );
			}

			_pingPanels.Add( (ring, ping.StartedAt) );
		}
	}

	/// <summary>Pings pulse so a fresh one catches the eye without needing a sound.</summary>
	void AnimatePings()
	{
		if ( _pingPanels.Count == 0 )
			return;

		var now = Time.NowDouble;
		foreach ( var (ring, startedAt) in _pingPanels )
		{
			if ( ring is null || !ring.IsValid() )
				continue;

			var age = (float)(now - startedAt);
			var pulse = 0.55f + 0.45f * MathF.Abs( MathF.Sin( age * 4f ) );
			var fade = Math.Clamp( (MapPingFeed.LifetimeSeconds - age) / 2f, 0f, 1f );
			ring.Style.Set( "opacity", $"{pulse * fade:0.##}" );
		}
	}
}
