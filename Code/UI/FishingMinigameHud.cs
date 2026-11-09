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

	/// <summary>Player bar: theme positive colour at these two alphas (resting / over the fish).</summary>
	const float BarAlpha = 0.55f;
	const float BarOnFishAlpha = 0.8f;

	Panel _root;
	Panel _fishMarker;
	Panel _bar;
	Panel _progressFill;
	bool _visible;
	Color _appliedFishColor;

	public void Build( Panel parent )
	{
		var colors = UiTheme.Colors;
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
		UiStyle.Surface( frame, UiSurface.Raised );
		frame.Style.Set( "align-items", "center" );
		frame.Style.Set( "justify-content", "center" );

		var track = new Panel { Parent = frame };
		track.Style.Set( "position", "relative" );
		track.Style.Width = Length.Pixels( TrackWidth );
		track.Style.Height = Length.Pixels( TrackHeight );
		UiStyle.Surface( track, UiSurface.Sunken );
		track.Style.Set( "overflow", "hidden" );

		_bar = new Panel { Parent = track };
		_bar.Style.Set( "position", "absolute" );
		_bar.Style.Set( "left", "0" );
		_bar.Style.Set( "width", "100%" );
		_bar.Style.BackgroundColor = colors.PositiveColor.WithAlpha( BarAlpha );
		_bar.Style.Set( "border-radius", UiStyle.Px( UiTheme.Metrics.RadiusSmall ) );

		_fishMarker = new Panel { Parent = track };
		_fishMarker.Style.Set( "position", "absolute" );
		_fishMarker.Style.Set( "left", "50%" );
		_fishMarker.Style.Width = Length.Pixels( FishSize );
		_fishMarker.Style.Height = Length.Pixels( FishSize );
		_appliedFishColor = colors.WarningColor;
		_fishMarker.Style.BackgroundColor = _appliedFishColor;
		_fishMarker.Style.Set( "border-radius", "50%" );
		_fishMarker.Style.Set( "border-width", UiStyle.Px( UiTheme.Metrics.BorderWidthStrong ) );
		_fishMarker.Style.Set( "border-color", UiStyle.Css( colors.AccentBrightColor ) );

		var progressTrack = new Panel { Parent = _root };
		progressTrack.Style.Set( "position", "relative" );
		progressTrack.Style.Width = Length.Pixels( ProgressWidth );
		progressTrack.Style.Height = Length.Pixels( TrackHeight + 12f );
		UiStyle.Surface( progressTrack, UiSurface.Sunken, UiTheme.Metrics.RadiusSmall );
		progressTrack.Style.Set( "overflow", "hidden" );

		_progressFill = new Panel { Parent = progressTrack };
		_progressFill.Style.Set( "position", "absolute" );
		_progressFill.Style.Set( "left", "0" );
		_progressFill.Style.Set( "bottom", "0" );
		_progressFill.Style.Set( "width", "100%" );
		_progressFill.Style.BackgroundColor = colors.StaminaColor;
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
		_bar.Style.BackgroundColor = UiTheme.Colors.PositiveColor.WithAlpha( fishing.MinigameBarOnFish ? BarOnFishAlpha : BarAlpha );

		var fishTopPx = ( 1f - fishing.MinigameFish01 ) * TrackHeight - FishSize * 0.5f;
		fishTopPx = Math.Clamp( fishTopPx, 0f, TrackHeight - FishSize );
		_fishMarker.Style.Top = Length.Pixels( fishTopPx );
		_fishMarker.Style.Set( "margin-left", $"{-FishSize * 0.5f}px" );

		// Each species carries its own colour so the fight reads differently per fish.
		var speciesColor = fishing.MinigameFishColor;
		if ( speciesColor != _appliedFishColor )
		{
			_appliedFishColor = speciesColor;
			_fishMarker.Style.BackgroundColor = speciesColor;
		}

		_progressFill.Style.Height = Length.Percent( Math.Clamp( fishing.MinigameProgress01, 0f, 1f ) * 100f );
	}
}
