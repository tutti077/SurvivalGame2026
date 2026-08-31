using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Stardew-style vertical fishing meter: bouncing fish marker, player-driven green bar, and a
/// yellow catch-progress column. Pure presentation — all state lives on <see cref="PlayerFishing"/>.
/// </summary>
public sealed class FishingMinigameHud
{
	const float TrackWidth = 58f;
	const float TrackHeight = 400f;
	const float ProgressWidth = 12f;
	const float FishSize = 24f;

	static readonly Color FrameColor = new( 0.36f, 0.25f, 0.12f, 0.95f );
	static readonly Color TrackColor = new( 0.55f, 0.68f, 0.75f, 0.85f );
	static readonly Color BarColor = new( 0.25f, 0.85f, 0.3f, 0.55f );
	static readonly Color BarOnFishColor = new( 0.3f, 0.95f, 0.35f, 0.8f );
	static readonly Color FishColor = new( 0.95f, 0.55f, 0.15f );
	static readonly Color ProgressBackColor = new( 0.15f, 0.13f, 0.08f, 0.9f );
	static readonly Color ProgressFillColor = new( 0.95f, 0.85f, 0.2f );

	Panel _root;
	Panel _fishMarker;
	Panel _bar;
	Panel _progressFill;
	bool _visible;

	public void Build( Panel parent )
	{
		_root = new Panel { Parent = parent };
		_root.Style.Set( "position", "absolute" );
		_root.Style.Set( "left", "26%" );
		_root.Style.Set( "top", "50%" );
		_root.Style.Set( "transform", "translate(-50%, -50%)" );
		_root.Style.Set( "flex-direction", "row" );
		_root.Style.Set( "gap", "6px" );
		_root.Style.Set( "pointer-events", "none" );
		_root.Style.Set( "display", "none" );

		var frame = new Panel { Parent = _root };
		frame.Style.Width = Length.Pixels( TrackWidth + 12f );
		frame.Style.Height = Length.Pixels( TrackHeight + 12f );
		frame.Style.BackgroundColor = FrameColor;
		frame.Style.Set( "border-radius", "8px" );
		frame.Style.Set( "align-items", "center" );
		frame.Style.Set( "justify-content", "center" );

		var track = new Panel { Parent = frame };
		track.Style.Set( "position", "relative" );
		track.Style.Width = Length.Pixels( TrackWidth );
		track.Style.Height = Length.Pixels( TrackHeight );
		track.Style.BackgroundColor = TrackColor;
		track.Style.Set( "border-radius", "5px" );
		track.Style.Set( "overflow", "hidden" );

		_bar = new Panel { Parent = track };
		_bar.Style.Set( "position", "absolute" );
		_bar.Style.Set( "left", "0" );
		_bar.Style.Set( "width", "100%" );
		_bar.Style.BackgroundColor = BarColor;
		_bar.Style.Set( "border-radius", "4px" );

		_fishMarker = new Panel { Parent = track };
		_fishMarker.Style.Set( "position", "absolute" );
		_fishMarker.Style.Set( "left", "50%" );
		_fishMarker.Style.Width = Length.Pixels( FishSize );
		_fishMarker.Style.Height = Length.Pixels( FishSize );
		_fishMarker.Style.BackgroundColor = FishColor;
		_fishMarker.Style.Set( "border-radius", "50%" );
		_fishMarker.Style.Set( "border-width", "2px" );
		_fishMarker.Style.Set( "border-color", "#5a3208" );

		var progressTrack = new Panel { Parent = _root };
		progressTrack.Style.Set( "position", "relative" );
		progressTrack.Style.Width = Length.Pixels( ProgressWidth );
		progressTrack.Style.Height = Length.Pixels( TrackHeight + 12f );
		progressTrack.Style.BackgroundColor = ProgressBackColor;
		progressTrack.Style.Set( "border-radius", "4px" );
		progressTrack.Style.Set( "overflow", "hidden" );

		_progressFill = new Panel { Parent = progressTrack };
		_progressFill.Style.Set( "position", "absolute" );
		_progressFill.Style.Set( "left", "0" );
		_progressFill.Style.Set( "bottom", "0" );
		_progressFill.Style.Set( "width", "100%" );
		_progressFill.Style.BackgroundColor = ProgressFillColor;
	}

	public void Tick( PlayerFishing fishing )
	{
		if ( _root is null )
			return;

		var active = fishing is not null && fishing.IsValid() && fishing.IsMinigameActive;
		if ( active != _visible )
		{
			_visible = active;
			_root.Style.Set( "display", active ? "flex" : "none" );
		}

		if ( !active )
			return;

		// Meter coordinates are 0 = bottom → panel top offsets flip.
		var barSizePx = fishing.MinigameBarSize01 * TrackHeight;
		var barTopPx = ( 1f - fishing.MinigameBar01 - fishing.MinigameBarSize01 ) * TrackHeight;
		_bar.Style.Top = Length.Pixels( barTopPx );
		_bar.Style.Height = Length.Pixels( barSizePx );
		_bar.Style.BackgroundColor = fishing.MinigameBarOnFish ? BarOnFishColor : BarColor;

		var fishTopPx = ( 1f - fishing.MinigameFish01 ) * TrackHeight - FishSize * 0.5f;
		fishTopPx = Math.Clamp( fishTopPx, 0f, TrackHeight - FishSize );
		_fishMarker.Style.Top = Length.Pixels( fishTopPx );
		_fishMarker.Style.Set( "margin-left", $"{-FishSize * 0.5f}px" );

		_progressFill.Style.Height = Length.Percent( Math.Clamp( fishing.MinigameProgress01, 0f, 1f ) * 100f );
	}
}
