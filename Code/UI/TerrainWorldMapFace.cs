using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Shared biome-map face for HUD minimap and Map menu.
/// Prefer a file-backed UI texture — <c>Bitmap.ToTexture</c> often fails as a Panel background.
/// Without a <see cref="TerrainWorldManager"/> (hand-built test scenes) it is a plain local radar
/// centered on the camera, so world markers still have somewhere to show. Markers: the viewer's
/// cross + heading, and the red ring around a base under raid (<see cref="BaseRaidSession"/>).
/// </summary>
public sealed class TerrainWorldMapFace
{
	public const float DefaultMinimapSize = 180f;

	/// <summary>Local radar (no TerrainWorld): meters across the face at the widest zoom.</summary>
	const float LocalRadarSpanMeters = 2400f;
	/// <summary>The raid ring never shrinks below this on screen, however far out the map is zoomed.</summary>
	const float RaidRingMinPixels = 14f;

	Panel _host;
	Panel _zoomStage;
	Image _mapImage;
	Panel _mapFallback;
	Label _placeholder;
	Panel _markerCrossH;
	Panel _markerCrossV;
	Panel _heading;
	Panel _raidRing;
	float _sizePixels;
	bool _fillParent;
	TerrainWorldManager _manager;
	Texture _boundTexture;
	float _appliedZoom = -1f;
	Vector2 _lastFocusUv = new( -1f, -1f );

	public Panel Host => _host;

	public void Build( Panel parent, float sizePixels, bool fillParent )
	{
		_sizePixels = sizePixels;
		_fillParent = fillParent;
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
			else if ( _manager is null )
				_placeholder.Text = "No TerrainWorld…";
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

		ApplyZoomLayout( focusUv, force: false );

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
		ApplyZoomLayout( new Vector2( 0.5f, 0.5f ), force: false );

		var camera = Sandbox.Game.ActiveScene?.Camera;
		if ( camera is null || !camera.IsValid() )
		{
			SetMarkerVisible( false );
			SetRaidRingVisible( false );
			return;
		}

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

		var stagePixels = HostPixels() * TerrainMinimapZoom.Level;
		var diameter = MathF.Max( RaidRingMinPixels, radiusUv * 2f * stagePixels );
		_raidRing.Style.Width = Length.Pixels( diameter );
		_raidRing.Style.Height = Length.Pixels( diameter );
		PlaceCenter( _raidRing, uv, -diameter * 0.5f, -diameter * 0.5f );
		SetRaidRingVisible( true );
	}

	/// <summary>Face width in UI pixels (the Map menu face fills its parent, so it is measured).</summary>
	float HostPixels()
	{
		if ( !_fillParent || _host is null || !_host.IsValid() )
			return _sizePixels;

		var scale = MathF.Max( 0.001f, _host.ScaleToScreen );
		var width = _host.Box.Rect.Width / scale;
		return width > 1f ? width : _sizePixels;
	}

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
		if ( !force
		     && Math.Abs( zoom - _appliedZoom ) < 0.001f
		     && (focusUv - _lastFocusUv).LengthSquared < 1e-8f )
			return;

		_appliedZoom = zoom;
		_lastFocusUv = focusUv;

		var view = 1f / zoom;
		var leftUv = Math.Clamp( focusUv.x - view * 0.5f, 0f, 1f - view );
		var topUv = Math.Clamp( focusUv.y - view * 0.5f, 0f, 1f - view );

		var sizePct = zoom * 100f;
		_zoomStage.Style.Width = Length.Percent( sizePct );
		_zoomStage.Style.Height = Length.Percent( sizePct );
		_zoomStage.Style.Left = Length.Percent( -leftUv * sizePct );
		_zoomStage.Style.Top = Length.Percent( -topUv * sizePct );
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
}
