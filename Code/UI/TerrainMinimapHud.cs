using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Top-right terrain biome minimap with +/− zoom under the bottom-right corner, and the wind arrow under that.</summary>
public sealed class TerrainMinimapHud
{
	const float ZoomButtonSize = 26f;
	const float ZoomButtonGap = 4f;

	readonly TerrainWorldMapFace _face = new();
	Panel _frame;
	Panel _zoomRow;
	Panel _windRow;
	Panel _windArrow;
	int _windDeg = int.MinValue;
	int _windOpacityStep = -1;
	bool _windShown = true;
	RealTimeSince _windStyled;
	TerrainMinimapZoomButton _zoomOutButton;
	TerrainMinimapZoomButton _zoomInButton;
	bool _built;
	bool _attackWasDown;

	public void Build( Panel root )
	{
		if ( _built || root is null )
			return;

		_frame = new Panel { Parent = root };
		_frame.Style.Set( "position", "absolute" );
		_frame.Style.Set( "top", "16px" );
		_frame.Style.Set( "right", "16px" );
		_frame.Style.Set( "pointer-events", "none" );
		_frame.Style.Set( "z-index", "1600" );
		_frame.Style.Set( "flex-direction", "column" );
		_frame.Style.Set( "align-items", "flex-end" );
		_frame.Style.PaddingTop = Length.Pixels( 3f );
		_frame.Style.PaddingBottom = Length.Pixels( 3f );
		_frame.Style.PaddingLeft = Length.Pixels( 3f );
		_frame.Style.PaddingRight = Length.Pixels( 3f );
		_frame.Style.BackgroundColor = new Color( 0.04f, 0.05f, 0.07f, 0.82f );
		_frame.Style.Set( "border-radius", "10px" );
		_frame.Style.Set( "border-width", "1px" );
		_frame.Style.Set( "border-color", "#3a4250" );

		_face.Build( _frame, TerrainWorldMapFace.DefaultMinimapSize, fillParent: false );

		_zoomRow = new Panel { Parent = _frame };
		_zoomRow.Style.Set( "position", "relative" );
		_zoomRow.Style.Set( "flex-direction", "row" );
		_zoomRow.Style.Set( "justify-content", "flex-end" );
		_zoomRow.Style.Set( "align-items", "center" );
		_zoomRow.Style.Set( "gap", $"{ZoomButtonGap}px" );
		_zoomRow.Style.Set( "margin-top", "6px" );
		_zoomRow.Style.Set( "pointer-events", "auto" );
		_zoomRow.Style.Set( "z-index", "1601" );

		_zoomOutButton = CreateZoomButton( "−", zoomIn: false );
		_zoomInButton = CreateZoomButton( "+", zoomIn: true );
		RefreshButtonStyles();
		BuildWindRow();
		_built = true;
	}

	/// <summary>
	/// "wind" + an arrow under the zoom buttons: the arrow points the way the wind BLOWS, in map orientation
	/// (same mapping as the heading tick), and brightens with the current gust. Hidden when the scene has no WindSystem.
	/// </summary>
	void BuildWindRow()
	{
		_windRow = new Panel { Parent = _frame };
		_windRow.Style.Set( "position", "relative" );
		_windRow.Style.Set( "flex-direction", "row" );
		_windRow.Style.Set( "justify-content", "flex-end" );
		_windRow.Style.Set( "align-items", "center" );
		_windRow.Style.Set( "gap", "6px" );
		_windRow.Style.Set( "margin-top", "4px" );
		_windRow.Style.Set( "pointer-events", "none" );

		var label = new Label { Parent = _windRow, Text = "wind" };
		label.Style.FontColor = new Color( 0.75f, 0.8f, 0.88f, 0.9f );
		label.Style.FontSize = Length.Pixels( 11f );

		_windArrow = new Panel { Parent = _windRow };
		_windArrow.Style.Width = Length.Pixels( 22f );
		_windArrow.Style.Height = Length.Pixels( 22f );
		_windArrow.Style.Set( "position", "relative" );
		_windArrow.Style.Set( "transform-origin", "center center" );

		var shaft = new Panel { Parent = _windArrow };
		shaft.Style.Set( "position", "absolute" );
		shaft.Style.Set( "left", "10px" );
		shaft.Style.Set( "top", "4px" );
		shaft.Style.Width = Length.Pixels( 2f );
		shaft.Style.Height = Length.Pixels( 16f );
		shaft.Style.BackgroundColor = new Color( 0.2f, 0.95f, 1f, 0.95f );

		// Two borders of a small square, turned 45°, make the arrow head.
		var head = new Panel { Parent = _windArrow };
		head.Style.Set( "position", "absolute" );
		head.Style.Set( "left", "7px" );
		head.Style.Set( "top", "3px" );
		head.Style.Width = Length.Pixels( 8f );
		head.Style.Height = Length.Pixels( 8f );
		head.Style.Set( "border-top-width", "2px" );
		head.Style.Set( "border-left-width", "2px" );
		head.Style.Set( "border-color", "#33f2ff" );
		head.Style.Set( "transform", "rotate(45deg)" );
	}

	void UpdateWindArrow()
	{
		if ( _windRow is null || !_windRow.IsValid() )
			return;

		var wind = WindSystem.Current;
		var show = wind.IsValid() && wind.WindEnabled;
		if ( show != _windShown )
		{
			_windShown = show;
			_windRow.Style.Set( "display", show ? "flex" : "none" );
		}

		if ( !show )
			return;

		// Style.Set re-lays the HUD out; four times a second is plenty for an arrow.
		if ( _windStyled < 0.25f )
			return;
		_windStyled = 0;

		var dir = TerrainBiomeMapCoordinates.WorldForwardToPreviewMapDirection( wind.Direction );
		if ( dir.LengthSquared > 1e-8f )
		{
			dir = dir.Normal;
			var deg = (int)MathF.Round( MathF.Atan2( dir.x, -dir.y ) * (180f / MathF.PI) );
			if ( deg != _windDeg )
			{
				_windDeg = deg;
				_windArrow.Style.Set( "transform", $"rotate({deg}deg)" );
			}
		}

		// Brightness follows the summed wind in four steps, so a gust visibly lights the arrow up.
		var step = (int)MathF.Round( wind.Strength01 * 4f );
		if ( step != _windOpacityStep )
		{
			_windOpacityStep = step;
			_windArrow.Style.Set( "opacity", $"{0.35f + (0.1625f * step):0.00}" );
		}
	}

	TerrainMinimapZoomButton CreateZoomButton( string label, bool zoomIn )
	{
		var button = new TerrainMinimapZoomButton
		{
			Parent = _zoomRow,
			ZoomIn = zoomIn,
			OnActivated = () =>
			{
				if ( zoomIn )
					TerrainMinimapZoom.TryZoomIn();
				else
					TerrainMinimapZoom.TryZoomOut();
				RefreshButtonStyles();
			},
		};

		button.Style.Width = Length.Pixels( ZoomButtonSize );
		button.Style.Height = Length.Pixels( ZoomButtonSize );
		button.Style.Set( "align-items", "center" );
		button.Style.Set( "justify-content", "center" );
		button.Style.Set( "border-radius", "5px" );
		button.Style.Set( "border-width", "1px" );
		button.Style.Set( "border-color", "#4a5568" );
		button.Style.Set( "pointer-events", "auto" );
		button.Style.Set( "cursor", "pointer" );

		var text = new Label { Parent = button, Text = label };
		text.Style.FontColor = Color.White;
		text.Style.FontSize = Length.Pixels( 16f );
		text.Style.Set( "pointer-events", "none" );

		return button;
	}

	// Last applied state; Style.Set every frame dirtied the panel styles (UI re-layout) for nothing.
	bool? _zoomOutStyled;
	bool? _zoomInStyled;

	void RefreshButtonStyles()
	{
		var canOut = TerrainMinimapZoom.Level > TerrainMinimapZoom.Min + 0.001f;
		var canIn = TerrainMinimapZoom.Level < TerrainMinimapZoom.Max - 0.001f;
		if ( _zoomOutStyled != canOut )
		{
			StyleZoomButton( _zoomOutButton, canOut );
			_zoomOutStyled = canOut;
		}

		if ( _zoomInStyled != canIn )
		{
			StyleZoomButton( _zoomInButton, canIn );
			_zoomInStyled = canIn;
		}
	}

	static void StyleZoomButton( TerrainMinimapZoomButton button, bool canPress )
	{
		if ( button is null || !button.IsValid() )
			return;

		button.Style.BackgroundColor = canPress
			? new Color( 0.16f, 0.18f, 0.22f, 0.95f )
			: new Color( 0.10f, 0.11f, 0.13f, 0.7f );
		button.Style.Set( "opacity", canPress ? "1" : "0.45" );
	}

	public void Tick()
	{
		if ( !_built )
			return;

		// No keyboard Input.Pressed for ad-hoc names — those are not registered actions and spam the console.
		PollPointerZoom();
		_face.Tick();
		RefreshButtonStyles();
		UpdateWindArrow();
	}

	/// <summary>
	/// Per-frame part only: the +/- click edge. For hosts that refresh the map itself at a lower rate
	/// (<see cref="PlayerScreenHud"/> passive tick) so a short click between refreshes is not lost.
	/// </summary>
	public void TickInput()
	{
		if ( !_built )
			return;

		PollPointerZoom();
	}

	/// <summary>Map redraw + button styles without input polling (pair with <see cref="TickInput"/>).</summary>
	public void TickVisuals()
	{
		if ( !_built )
			return;

		_face.Tick();
		RefreshButtonStyles();
		UpdateWindArrow();
	}

	void PollPointerZoom()
	{
		var attackDown = Input.Down( "Attack1" );
		var pressed = attackDown && !_attackWasDown;
		_attackWasDown = attackDown;
		if ( !pressed )
			return;

		var screenPos = InventoryScreenPointer.GetMenuOrMousePosition();
		if ( TryActivateZoomButton( _zoomInButton, screenPos ) )
			return;

		TryActivateZoomButton( _zoomOutButton, screenPos );
	}

	bool TryActivateZoomButton( TerrainMinimapZoomButton button, Vector2 screenPos )
	{
		if ( button is null || !button.IsValid() )
			return false;

		if ( !InventoryScreenPointer.PanelBoxContainsScreen( button, screenPos ) )
			return false;

		button.Activate();
		return true;
	}

	public void SetVisible( bool visible )
	{
		if ( _frame is null || !_frame.IsValid() )
			return;

		_frame.Style.Set( "display", visible ? "flex" : "none" );
	}
}
