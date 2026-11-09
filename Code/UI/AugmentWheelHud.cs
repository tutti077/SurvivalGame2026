using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// The C radial: installed wheel augments laid out on a ring around screen centre while
/// <see cref="PlayerAugments.IsWheelOpen"/>. Segment 0 sits at the top, the rest clockwise — the same
/// convention the pawn uses to turn mouse travel into a pick. The highlighted entry shows its name
/// in the middle; a toggle that is on carries the theme's active (green) ring.
/// </summary>
public sealed class AugmentWheelHud
{
	const float RingRadius = 170f;
	const float EntrySize = 96f;
	const float IconSize = 48f;
	const float NameFont = 13f;
	const float CenterFont = 18f;

	readonly List<EntryUi> _entries = new();

	Panel _host;
	Panel _ring;
	Label _center;
	PlayerAugments _augments;
	string _builtKey;
	bool _shown;

	public void Build( Panel root, PlayerAugments augments )
	{
		_augments = augments;

		_host = new Panel { Parent = root };
		_host.Style.Set( "position", "absolute" );
		_host.Style.Set( "left", "0" );
		_host.Style.Set( "top", "0" );
		_host.Style.Set( "right", "0" );
		_host.Style.Set( "bottom", "0" );
		_host.Style.Set( "justify-content", "center" );
		_host.Style.Set( "align-items", "center" );
		_host.Style.Set( "pointer-events", "none" );
		_host.Style.Set( "display", "none" );
		_host.Style.Set( "z-index", "50" );

		_ring = new Panel { Parent = _host };
		_ring.Style.Set( "position", "relative" );
		_ring.Style.Width = Length.Pixels( (RingRadius + EntrySize) * 2f );
		_ring.Style.Height = Length.Pixels( (RingRadius + EntrySize) * 2f );
		_ring.Style.Set( "pointer-events", "none" );

		_center = new Label { Parent = _ring, Text = "" };
		_center.Style.Set( "position", "absolute" );
		_center.Style.Set( "left", "0" );
		_center.Style.Set( "right", "0" );
		_center.Style.Set( "top", $"{RingRadius + EntrySize - CenterFont}px" );
		_center.Style.Set( "text-align", "center" );
		UiStyle.Text( _center, UiText.Hud, CenterFont );
		_center.Style.Set( "pointer-events", "none" );
	}

	public void Tick()
	{
		if ( _host is null || !_host.IsValid() || _augments is null )
			return;

		var open = _augments.IsWheelOpen;
		if ( open != _shown )
		{
			_shown = open;
			_host.Style.Set( "display", open ? "flex" : "none" );
		}

		if ( !open )
			return;

		var entries = _augments.WheelEntries;
		RebuildIfChanged( entries );

		var selected = _augments.WheelSelectedIndex;
		for ( var i = 0; i < _entries.Count; i++ )
		{
			var ui = _entries[i];
			var isSelected = i == selected;
			if ( string.IsNullOrWhiteSpace( ui.Id ) )
			{
				// Empty segment: hovering it just means "pick nothing".
				ui.ApplyState( isSelected ? UiSlotState.Selected : UiSlotState.Locked, glow: false );
				continue;
			}

			_augments.TryGetTriggerState( ui.Id, out var cooldownLeft, out var cooldownTotal, out var on, out var battery01 );
			ApplyFill( ui.Fill, cooldownLeft, cooldownTotal, on, battery01 );
			// A toggle that is on keeps its green ring; the "about to pick" glow is added on top of it.
			ui.ApplyState( on ? UiSlotState.Active : isSelected ? UiSlotState.Selected : UiSlotState.Idle, glow: on && isSelected );
		}

		_center.Text = selected >= 0 && selected < entries.Count
			? entries[selected].DisplayName
			: selected >= 0 ? "Empty · release C for nothing" : "Move to pick · release C";
	}

	void RebuildIfChanged( IReadOnlyList<AugmentDefinition> entries )
	{
		var key = string.Join( "|", System.Linq.Enumerable.Select( entries, e => e.Id ) );
		if ( string.Equals( key, _builtKey, StringComparison.Ordinal ) )
			return;

		_builtKey = key;
		for ( var i = 0; i < _entries.Count; i++ )
			_entries[i].Root?.Delete();
		_entries.Clear();

		// Always six segments so an installed augment never fills the whole ring.
		var count = PlayerAugments.WheelSlotCount;
		var centre = RingRadius + EntrySize;
		for ( var i = 0; i < count; i++ )
		{
			var angle = MathF.PI * 2f * i / count;
			var x = centre + MathF.Sin( angle ) * RingRadius - EntrySize * 0.5f;
			var y = centre - MathF.Cos( angle ) * RingRadius - EntrySize * 0.5f;

			var entry = new Panel { Parent = _ring };
			entry.Style.Set( "position", "absolute" );
			entry.Style.Left = Length.Pixels( x );
			entry.Style.Top = Length.Pixels( y );
			entry.Style.Width = Length.Pixels( EntrySize );
			entry.Style.Height = Length.Pixels( EntrySize );
			entry.Style.Set( "flex-direction", "column" );
			entry.Style.Set( "align-items", "center" );
			entry.Style.Set( "justify-content", "center" );
			entry.Style.Set( "gap", "4px" );
			UiStyle.Slot( entry, UiTheme.Metrics.RadiusLarge );
			entry.Style.Set( "pointer-events", "none" );

			// Fill sits under the icon and name; height is the fraction left, so it drains from the top down.
			var fill = new Panel { Parent = entry };
			fill.Style.Set( "position", "absolute" );
			fill.Style.Set( "left", "0" );
			fill.Style.Set( "right", "0" );
			fill.Style.Set( "bottom", "0" );
			fill.Style.Set( "height", "0%" );
			fill.Style.Set( "border-radius", UiStyle.Px( Math.Max( 0f, UiTheme.Metrics.RadiusLarge - UiTheme.Metrics.BorderWidthStrong ) ) );
			fill.Style.Set( "pointer-events", "none" );

			var icon = new Panel { Parent = entry };
			icon.Style.Set( "z-index", "1" );
			icon.Style.Width = Length.Pixels( IconSize );
			icon.Style.Height = Length.Pixels( IconSize );
			icon.Style.Set( "background-size", "contain" );
			icon.Style.Set( "background-repeat", "no-repeat" );
			icon.Style.Set( "background-position", "center" );
			var def = i < entries.Count ? entries[i] : null;
			if ( def is not null )
				MenuUiTextures.ApplyBackground( icon, def.Icon );
			else
				icon.Style.Set( "display", "none" );

			var name = new Label { Parent = entry, Text = def?.DisplayName ?? "empty" };
			name.Style.Set( "z-index", "1" );
			UiStyle.Text( name, def is null ? UiText.Muted : UiText.Hud, NameFont );
			name.Style.Set( "text-align", "center" );
			name.Style.Set( "white-space", "normal" );

			_entries.Add( new EntryUi( entry, fill, def?.Id ) );
		}
	}

	/// <summary>
	/// Recharging → red, the cooldown fraction left. On → yellow, the battery fraction left (a full
	/// box at activation, a thin strip along the bottom when nearly empty). Off with a partly
	/// recharged battery → dim yellow. Idle and full → no fill (the plain look).
	/// </summary>
	static void ApplyFill( Panel fill, float cooldownLeft, float cooldownTotal, bool on, float battery01 )
	{
		if ( fill is null || !fill.IsValid() )
			return;

		// Fill behind the icon: warning yellow = battery left while on (drains from the top), negative red = recharge cooldown left.
		var colors = UiTheme.Colors;
		float fraction;
		Color color;
		if ( cooldownLeft > 0.01f && cooldownTotal > 0.01f )
		{
			fraction = Math.Clamp( cooldownLeft / cooldownTotal, 0f, 1f );
			color = colors.NegativeColor.WithAlpha( 0.92f );
		}
		else if ( on )
		{
			fraction = Math.Clamp( battery01, 0f, 1f );
			color = colors.WarningColor.WithAlpha( 0.92f );
		}
		else if ( battery01 < 0.995f )
		{
			fraction = Math.Clamp( battery01, 0f, 1f );
			color = colors.WarningColor.WithAlpha( 0.45f );
		}
		else
		{
			fraction = 0f;
			color = Color.Transparent;
		}

		fill.Style.BackgroundColor = color;
		fill.Style.Set( "height", $"{MathF.Max( fraction * 100f, fraction > 0f ? 3f : 0f ):0.#}%" );
	}

	public void Dispose()
	{
		_host?.Delete();
		_host = null;
	}

	sealed class EntryUi
	{
		public Panel Root { get; }
		public Panel Fill { get; }
		public string Id { get; }
		// Last applied look: the theme slot state writes several style properties, so only re-apply on change.
		UiSlotState? _state;
		bool _glow;

		public EntryUi( Panel root, Panel fill, string id )
		{
			Root = root;
			Fill = fill;
			Id = id;
		}

		public void ApplyState( UiSlotState state, bool glow )
		{
			if ( Root is null || !Root.IsValid() || (_state == state && _glow == glow) )
				return;

			_state = state;
			_glow = glow;
			UiStyle.SetSlotState( Root, state );
			if ( glow )
				Root.Style.Set( "box-shadow", $"0px 0px 10px {UiStyle.Css( UiTheme.Colors.AccentGlowColor )}" );
		}
	}
}
