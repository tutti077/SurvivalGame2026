using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// The C radial: installed wheel augments laid out on a ring around screen centre while
/// <see cref="PlayerAugments.IsWheelOpen"/>. Segment 0 sits at the top, the rest clockwise — the same
/// convention the pawn uses to turn mouse travel into a pick. The highlighted entry shows its name
/// in the middle; a toggle that is on carries a yellow ring.
/// </summary>
public sealed class AugmentWheelHud
{
	const float RingRadius = 170f;
	const float EntrySize = 96f;
	const float IconSize = 48f;
	const float NameFont = 13f;
	const float CenterFont = 18f;

	static readonly Color EntryBg = new( 0.07f, 0.08f, 0.10f, 0.9f );
	static readonly Color EntryBgSelected = new( 0.2f, 0.3f, 0.45f, 0.96f );
	// Fill behind the icon: yellow = battery left while on (drains from the top), red = recharge cooldown left.
	static readonly Color FillOn = new( 0.98f, 0.88f, 0.50f, 0.92f );
	static readonly Color FillPartial = new( 0.98f, 0.88f, 0.50f, 0.45f );
	static readonly Color FillCooldown = new( 0.85f, 0.18f, 0.12f, 0.92f );
	static readonly Color EntryBgEmpty = new( 0.05f, 0.06f, 0.08f, 0.55f );
	static readonly Color EntryBgSelectedEmpty = new( 0.12f, 0.16f, 0.24f, 0.7f );
	const string BorderEmpty = "#262b35";
	const string BorderIdle = "#3a4150";
	const string BorderSelected = "#8fbaff";
	/// <summary>Yellow ring: this toggle is currently on. Blue stays the "about to pick" highlight.</summary>
	const string BorderOn = "#e0b84a";

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
		_center.Style.FontColor = Color.White;
		_center.Style.FontSize = Length.Pixels( CenterFont );
		_center.Style.Set( "text-shadow", "1px 1px 3px rgba(0,0,0,0.9)" );
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
				ui.Root.Style.BackgroundColor = isSelected ? EntryBgSelectedEmpty : EntryBgEmpty;
				ui.Root.Style.Set( "border-color", isSelected ? BorderSelected : BorderEmpty );
				ui.Root.Style.Set( "border-width", "2px" );
				continue;
			}

			_augments.TryGetTriggerState( ui.Id, out var cooldownLeft, out var cooldownTotal, out var on, out var battery01 );
			ui.Root.Style.BackgroundColor = isSelected ? EntryBgSelected : EntryBg;
			ApplyFill( ui.Fill, cooldownLeft, cooldownTotal, on, battery01 );
			ui.Root.Style.Set( "border-color", on ? BorderOn : isSelected ? BorderSelected : BorderIdle );
			ui.Root.Style.Set( "border-width", on ? "4px" : "2px" );
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
			entry.Style.Set( "box-sizing", "border-box" );
			entry.Style.BackgroundColor = EntryBg;
			entry.Style.Set( "border-width", "2px" );
			entry.Style.Set( "border-color", BorderIdle );
			entry.Style.Set( "border-radius", "10px" );
			entry.Style.Set( "pointer-events", "none" );

			// Fill sits under the icon and name; height is the fraction left, so it drains from the top down.
			var fill = new Panel { Parent = entry };
			fill.Style.Set( "position", "absolute" );
			fill.Style.Set( "left", "0" );
			fill.Style.Set( "right", "0" );
			fill.Style.Set( "bottom", "0" );
			fill.Style.Set( "height", "0%" );
			fill.Style.Set( "border-radius", "8px" );
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
			name.Style.Set( "text-shadow", "1px 1px 2px rgba(0,0,0,0.9)" );
			name.Style.FontColor = Color.White;
			name.Style.FontSize = Length.Pixels( NameFont );
			name.Style.Set( "text-align", "center" );
			name.Style.Set( "white-space", "normal" );

			if ( def is null )
				name.Style.FontColor = new Color( 0.45f, 0.48f, 0.55f );

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

		float fraction;
		Color color;
		if ( cooldownLeft > 0.01f && cooldownTotal > 0.01f )
		{
			fraction = Math.Clamp( cooldownLeft / cooldownTotal, 0f, 1f );
			color = FillCooldown;
		}
		else if ( on )
		{
			fraction = Math.Clamp( battery01, 0f, 1f );
			color = FillOn;
		}
		else if ( battery01 < 0.995f )
		{
			fraction = Math.Clamp( battery01, 0f, 1f );
			color = FillPartial;
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

	readonly struct EntryUi
	{
		public Panel Root { get; }
		public Panel Fill { get; }
		public string Id { get; }
		public EntryUi( Panel root, Panel fill, string id )
		{
			Root = root;
			Fill = fill;
			Id = id;
		}
	}
}
