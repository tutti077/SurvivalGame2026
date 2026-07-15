using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Top-right terrain biome minimap with +/− zoom under the bottom-right corner.</summary>
public sealed class TerrainMinimapHud
{
	const float ZoomButtonSize = 26f;
	const float ZoomButtonGap = 4f;

	readonly TerrainWorldMapFace _face = new();
	Panel _frame;
	Panel _zoomRow;
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
		_built = true;
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

	void RefreshButtonStyles()
	{
		StyleZoomButton( _zoomOutButton, canPress: TerrainMinimapZoom.Level > TerrainMinimapZoom.Min + 0.001f );
		StyleZoomButton( _zoomInButton, canPress: TerrainMinimapZoom.Level < TerrainMinimapZoom.Max - 0.001f );
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
