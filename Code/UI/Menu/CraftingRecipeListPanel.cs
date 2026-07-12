using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Fixed-height recipe list. Content moves via absolute <c>top</c> (keeps row Box.Rect aligned for clicks).
/// Wheel comes from gameplay <see cref="Input.MouseWheel"/> while the menu uses Hidden + soft cursor.
/// </summary>
public sealed class CraftingRecipeListPanel : Panel
{
	public const float ScrollBarWidth = 18f;
	const float MinThumbHeight = 28f;

	CraftingMenuSection _section;
	Panel _viewport;
	Panel _content;
	ScrollTrackPanel _scrollTrack;
	ScrollThumbPanel _scrollThumb;

	float _scrollY;
	float _contentHeight;
	int _rowCount;
	bool _draggingThumb;
	float _dragStartMouseY;
	float _dragStartScrollY;
	float _lastWheelApplyTime = -1f;

	public Panel Content => _content;
	public bool IsDraggingThumb => _draggingThumb;

	public void Bind( CraftingMenuSection section ) => _section = section;

	public void BuildChrome()
	{
		Style.Set( "position", "relative" );
		Style.Set( "overflow", "hidden" );
		Style.Set( "pointer-events", "all" );

		_viewport = new ViewportPanel { Parent = this, List = this };
		_viewport.Style.Set( "position", "absolute" );
		_viewport.Style.Set( "left", "0" );
		_viewport.Style.Set( "top", "0" );
		_viewport.Style.Set( "bottom", "0" );
		_viewport.Style.Set( "right", $"{ScrollBarWidth}px" );
		_viewport.Style.Set( "overflow", "hidden" );
		_viewport.Style.Set( "pointer-events", "all" );

		_content = new Panel { Parent = _viewport };
		_content.Style.Set( "position", "absolute" );
		_content.Style.Set( "left", "0" );
		_content.Style.Set( "right", "0" );
		_content.Style.Set( "top", "0px" );
		_content.Style.Set( "flex-direction", "column" );
		_content.Style.Set( "flex-shrink", "0" );
		_content.Style.Set( "flex-grow", "0" );
		_content.Style.Set( "width", "100%" );
		_content.Style.Set( "pointer-events", "all" );
		_content.Style.PaddingRight = Length.Pixels( 2f );

		_scrollTrack = new ScrollTrackPanel { Parent = this, List = this };
		_scrollTrack.Style.Set( "position", "absolute" );
		_scrollTrack.Style.Set( "top", "0" );
		_scrollTrack.Style.Set( "bottom", "0" );
		_scrollTrack.Style.Set( "right", "0" );
		_scrollTrack.Style.Width = Length.Pixels( ScrollBarWidth );
		_scrollTrack.Style.Set( "z-index", "30" );
		_scrollTrack.Style.BackgroundColor = new Color( 0.08f, 0.09f, 0.11f, 0.95f );
		_scrollTrack.Style.Set( "border-radius", "4px" );
		_scrollTrack.Style.Set( "pointer-events", "all" );
		_scrollTrack.Style.Set( "cursor", "pointer" );

		_scrollThumb = new ScrollThumbPanel { Parent = _scrollTrack, List = this };
		_scrollThumb.Style.Set( "position", "absolute" );
		_scrollThumb.Style.Set( "left", "2px" );
		_scrollThumb.Style.Set( "right", "2px" );
		_scrollThumb.Style.Set( "top", "0px" );
		_scrollThumb.Style.Height = Length.Pixels( MinThumbHeight );
		_scrollThumb.Style.Set( "z-index", "31" );
		_scrollThumb.Style.BackgroundColor = new Color( 0.55f, 0.60f, 0.68f, 0.95f );
		_scrollThumb.Style.Set( "border-radius", "3px" );
		_scrollThumb.Style.Set( "pointer-events", "all" );
		_scrollThumb.Style.Set( "cursor", "grab" );
	}

	public void SetRowCount( int rowCount )
	{
		_rowCount = Math.Max( 0, rowCount );
		_contentHeight = ComputeContentHeight( _rowCount );

		if ( _content is not null && _content.IsValid() )
			_content.Style.Height = Length.Pixels( Math.Max( 1f, _contentHeight ) );

		SetScrollY( _scrollY );
	}

	public static float ComputeContentHeight( int rowCount )
	{
		if ( rowCount <= 0 )
			return 0f;

		var rowH = CraftingMenuSection.RecipeRowHeight;
		var gap = CraftingMenuSection.RecipeRowGap;
		return rowCount * rowH + ( rowCount - 1 ) * gap;
	}

	public static float GetRowStride() =>
		CraftingMenuSection.RecipeRowHeight + CraftingMenuSection.RecipeRowGap;

	public static float GetNotchStep() =>
		CraftingMenuSection.WheelItemsPerNotch * GetRowStride();

	public override bool WantsMouseInput() => false;

	public override void OnMouseWheel( Vector2 value ) => ApplyWheel( value );

	public void ApplyWheel( Vector2 wheel )
	{
		var delta = wheel.y;
		if ( MathF.Abs( wheel.x ) > MathF.Abs( wheel.y ) )
			delta = wheel.x;

		if ( MathF.Abs( delta ) < 0.01f )
			return;

		if ( Time.Now - _lastWheelApplyTime < 0.001f )
			return;
		_lastWheelApplyTime = Time.Now;

		// UI / Input notches are typically ±1. Huge raw deltas still count as one notch.
		var notches = MathF.Abs( delta ) < 2f
			? MathF.Sign( delta ) * MathF.Max( 1f, MathF.Abs( delta ) )
			: MathF.Sign( delta );

		// Panel docs: positive wheel = scroll down = increase scroll offset.
		SetScrollY( _scrollY + notches * GetNotchStep() );
	}

	public void PollWheelWhileOpen() { }

	/// <summary>Pick row by scroll math — survives soft-cursor / Box.Rect quirks.</summary>
	public bool TryPickRowIndexAtScreen( Vector2 screenPos, out int rowIndex )
	{
		rowIndex = -1;
		if ( _rowCount <= 0 || _viewport is null || !_viewport.IsValid() )
			return false;

		var view = _viewport.Box.Rect;
		if ( view.Height <= 1f )
			view = Box.Rect;

		if ( screenPos.x < view.Left || screenPos.x > view.Right
		     || screenPos.y < view.Top || screenPos.y > view.Bottom )
			return false;

		var scale = ScaleToScreen > 0.001f ? ScaleToScreen : 1f;
		var localY = ( screenPos.y - view.Top ) / scale + _scrollY;
		var stride = GetRowStride();
		if ( stride < 1f )
			return false;

		rowIndex = (int)MathF.Floor( localY / stride );
		if ( rowIndex < 0 || rowIndex >= _rowCount )
			return false;

		return true;
	}

	/// <summary>Overlay / tick-driven scrollbar: press to jump/drag, hold to drag, release to end.</summary>
	public bool TryHandlePointer( Vector2 screenPos, bool pressed )
	{
		if ( !pressed )
		{
			if ( !_draggingThumb )
				return false;

			EndThumbDrag();
			return true;
		}

		// Already dragging — always update from mouse Y (don't require cursor stay on the thin strip).
		if ( _draggingThumb )
		{
			UpdateDragFromScreenY( screenPos.y );
			return true;
		}

		if ( !CanScroll() )
			return false;

		if ( !IsOverScrollbar( screenPos ) )
			return false;

		// Track click jumps first, then drag from that point (delta-based, no grab-offset sync issues).
		if ( !IsOverThumb( screenPos ) )
			JumpToTrackAtScreenY( screenPos.y );

		BeginThumbDragAt( screenPos.y );
		return true;
	}

	public bool IsOverScrollbar( Vector2 screenPos )
	{
		if ( !TryGetScrollbarHitRect( out var left, out var right, out var top, out var bottom ) )
			return false;

		return screenPos.x >= left && screenPos.x <= right
		       && screenPos.y >= top && screenPos.y <= bottom;
	}

	bool IsOverThumb( Vector2 screenPos )
	{
		if ( !TryGetTrackScreenMetrics( out var trackTop, out var trackH, out var thumbH ) )
			return false;

		if ( !TryGetScrollbarHitRect( out var left, out var right, out _, out _ ) )
			return false;

		var thumbTop = GetThumbScreenTopFromMetrics( trackTop, trackH, thumbH );
		return screenPos.x >= left && screenPos.x <= right
		       && screenPos.y >= thumbTop - 2f && screenPos.y <= thumbTop + thumbH + 2f;
	}

	bool TryGetScrollbarHitRect( out float left, out float right, out float top, out float bottom )
	{
		left = right = top = bottom = 0f;
		if ( !IsValid )
			return false;

		var pad = 6f * MathF.Max( 1f, ScaleToScreen );

		if ( _scrollTrack is not null && _scrollTrack.IsValid() )
		{
			var track = _scrollTrack.Box.Rect;
			if ( track.Width > 1f && track.Height > 1f )
			{
				left = track.Left - pad;
				right = track.Right + pad;
				top = track.Top - pad;
				bottom = track.Bottom + pad;
				return true;
			}
		}

		var list = Box.Rect;
		if ( list.Width <= 1f || list.Height <= 1f )
			return false;

		var hitWidth = MathF.Max( ScrollBarWidth, 28f ) * MathF.Max( 1f, ScaleToScreen );
		left = list.Right - hitWidth - pad;
		right = list.Right + pad;
		top = list.Top - pad;
		bottom = list.Bottom + pad;
		return true;
	}

	static bool IsScreenPosInsidePanel( Panel panel, Vector2 screenPos )
	{
		if ( panel is null || !panel.IsValid() )
			return false;

		if ( panel.IsInside( screenPos ) )
			return true;

		var rect = panel.Box.Rect;
		if ( rect.Width <= 0f || rect.Height <= 0f )
			return false;

		return screenPos.x >= rect.Left && screenPos.x <= rect.Right
		       && screenPos.y >= rect.Top && screenPos.y <= rect.Bottom;
	}

	public override void Tick()
	{
		base.Tick();
		UpdateScrollbarVisual();
	}

	public void BeginThumbDrag() => BeginThumbDragAt( Mouse.Position.y );

	public void BeginThumbDragAt( float screenY )
	{
		if ( !CanScroll() )
			return;

		_draggingThumb = true;
		_dragStartMouseY = screenY;
		_dragStartScrollY = _scrollY;
	}

	public void EndThumbDrag()
	{
		_draggingThumb = false;
	}

	public void JumpToTrackClick() => JumpToTrackAtScreenY( Mouse.Position.y );

	void JumpToTrackAtScreenY( float screenY )
	{
		if ( !CanScroll() || !TryGetTrackScreenMetrics( out var trackTop, out var trackH, out var thumbH ) )
			return;

		var travel = Math.Max( 1f, trackH - thumbH );
		var localY = screenY - trackTop - thumbH * 0.5f;
		var t = Math.Clamp( localY / travel, 0f, 1f );
		SetScrollNormalized( t );
	}

	void UpdateDragFromScreenY( float screenY )
	{
		if ( !TryGetTrackScreenMetrics( out _, out var trackH, out var thumbH ) )
			return;

		var travel = Math.Max( 1f, trackH - thumbH );
		var dy = screenY - _dragStartMouseY;
		SetScrollY( _dragStartScrollY + dy / travel * GetMaxScrollY() );
	}

	bool TryGetTrackScreenMetrics( out float trackTop, out float trackH, out float thumbH )
	{
		trackTop = 0f;
		trackH = 0f;
		thumbH = 0f;

		var list = Box.Rect;
		if ( list.Height > 1f )
		{
			trackTop = list.Top;
			trackH = list.Height;
		}
		else if ( _scrollTrack is not null && _scrollTrack.IsValid() && _scrollTrack.Box.Rect.Height > 1f )
		{
			trackTop = _scrollTrack.Box.Rect.Top;
			trackH = _scrollTrack.Box.Rect.Height;
		}
		else
		{
			trackH = GetViewHeight() * MathF.Max( 1f, ScaleToScreen );
			if ( trackH <= 1f )
				return false;
			trackTop = list.Top;
		}

		thumbH = GetThumbHeightStyle() * MathF.Max( 1f, ScaleToScreen );
		thumbH = Math.Clamp( thumbH, 1f, trackH );
		return true;
	}

	void SetScrollNormalized( float t ) => SetScrollY( Math.Clamp( t, 0f, 1f ) * GetMaxScrollY() );

	float GetScrollY() => _scrollY;

	void SetScrollY( float y )
	{
		_scrollY = Math.Clamp( y, 0f, GetMaxScrollY() );
		ApplyContentOffset();
		UpdateScrollbarVisual();
	}

	void ApplyContentOffset()
	{
		if ( _content is null || !_content.IsValid() )
			return;

		_content.Style.Set( "top", $"{-_scrollY:0.##}px" );
	}

	bool CanScroll() => GetMaxScrollY() > 1f;

	float GetViewHeight() => CraftingMenuSection.RecipeListMaxHeight;

	float GetTrackHeightStyle() => GetViewHeight();

	float GetThumbScreenTop()
	{
		if ( !TryGetTrackScreenMetrics( out var trackTop, out var trackH, out var thumbH ) )
			return Mouse.Position.y;

		return GetThumbScreenTopFromMetrics( trackTop, trackH, thumbH );
	}

	float GetThumbScreenTopFromMetrics( float trackTop, float trackH, float thumbH )
	{
		var maxY = GetMaxScrollY();
		var t = maxY > 0f ? Math.Clamp( _scrollY / maxY, 0f, 1f ) : 0f;
		var travel = Math.Max( 0f, trackH - thumbH );
		return trackTop + t * travel;
	}

	float GetContentHeight() => Math.Max( _contentHeight, 0f );

	float GetMaxScrollY() => Math.Max( 0f, GetContentHeight() - GetViewHeight() );

	float GetThumbHeightStyle()
	{
		var trackH = GetTrackHeightStyle();
		var viewH = GetViewHeight();
		var contentH = GetContentHeight();
		if ( contentH <= viewH + 1f )
			return trackH;

		return Math.Clamp( viewH / contentH * trackH, MinThumbHeight, trackH );
	}

	void UpdateScrollbarVisual()
	{
		if ( _scrollTrack is null || !_scrollTrack.IsValid() || _scrollThumb is null || !_scrollThumb.IsValid() )
			return;

		var maxY = GetMaxScrollY();
		var canScroll = maxY > 1f;
		_scrollTrack.Style.Set( "opacity", canScroll ? "1" : "0.35" );

		var trackH = GetTrackHeightStyle();

		if ( !canScroll )
		{
			_scrollThumb.Style.Set( "top", "0px" );
			_scrollThumb.Style.Height = Length.Pixels( trackH );
			return;
		}

		var thumbH = GetThumbHeightStyle();
		var travel = Math.Max( 0f, trackH - thumbH );
		var scrollY = _scrollY;
		var t = maxY > 0f ? Math.Clamp( scrollY / maxY, 0f, 1f ) : 0f;
		var thumbTop = t <= 0.001f ? 0f : t >= 0.999f ? travel : t * travel;

		_scrollThumb.Style.Height = Length.Pixels( thumbH );
		_scrollThumb.Style.Set( "top", $"{thumbTop:0.##}px" );
	}

	sealed class ViewportPanel : Panel
	{
		public CraftingRecipeListPanel List { get; init; }

		public override bool WantsMouseInput() => false;

		public override void OnMouseWheel( Vector2 value )
		{
			// Prefer our notch-sized scroll; skip base.TryScroll to avoid double movement.
			List?.ApplyWheel( value );
		}
	}

	sealed class ScrollThumbPanel : Panel
	{
		public CraftingRecipeListPanel List { get; init; }

		public override bool WantsMouseInput() => false;

		public override void OnMouseWheel( Vector2 value ) => List?.ApplyWheel( value );

		protected override void OnMouseDown( MousePanelEvent e )
		{
			base.OnMouseDown( e );
			if ( e.Button != "mouseleft" || List is null )
				return;

			List.BeginThumbDrag();
			e.StopPropagation();
		}

		protected override void OnMouseUp( MousePanelEvent e )
		{
			base.OnMouseUp( e );
			List?.EndThumbDrag();
		}
	}

	sealed class ScrollTrackPanel : Panel
	{
		public CraftingRecipeListPanel List { get; init; }

		public override bool WantsMouseInput() => false;

		public override void OnMouseWheel( Vector2 value ) => List?.ApplyWheel( value );

		protected override void OnMouseDown( MousePanelEvent e )
		{
			base.OnMouseDown( e );
			if ( e.Button != "mouseleft" || List is null )
				return;

			List.JumpToTrackClick();
			List.BeginThumbDrag();
			e.StopPropagation();
		}

		protected override void OnMouseUp( MousePanelEvent e )
		{
			base.OnMouseUp( e );
			List?.EndThumbDrag();
		}
	}
}
