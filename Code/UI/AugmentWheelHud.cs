using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// The F radial: installed wheel augments laid out on a ring around screen centre while
/// <see cref="PlayerAugments.IsWheelOpen"/>. Segment 0 sits at the top, the rest clockwise — the same
/// convention the pawn uses to turn mouse travel into a pick. The highlighted entry shows its name
/// in the middle; a toggle that is on carries a green edge.
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
	const string BorderIdle = "#3a4150";
	const string BorderSelected = "#8fbaff";
	const string BorderOn = "#5ec46a";

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
			_augments.TryGetTriggerState( ui.Id, out _, out _, out var on, out _ );
			ui.Root.Style.BackgroundColor = isSelected ? EntryBgSelected : EntryBg;
			ui.Root.Style.Set( "border-color", on ? BorderOn : isSelected ? BorderSelected : BorderIdle );
		}

		_center.Text = selected >= 0 && selected < entries.Count
			? entries[selected].DisplayName
			: entries.Count > 0 ? "Move to pick · release F" : "";
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

		var count = entries.Count;
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

			var icon = new Panel { Parent = entry };
			icon.Style.Width = Length.Pixels( IconSize );
			icon.Style.Height = Length.Pixels( IconSize );
			icon.Style.Set( "background-size", "contain" );
			icon.Style.Set( "background-repeat", "no-repeat" );
			icon.Style.Set( "background-position", "center" );
			MenuUiTextures.ApplyBackground( icon, entries[i].Icon );

			var name = new Label { Parent = entry, Text = entries[i].DisplayName };
			name.Style.FontColor = Color.White;
			name.Style.FontSize = Length.Pixels( NameFont );
			name.Style.Set( "text-align", "center" );
			name.Style.Set( "white-space", "normal" );

			_entries.Add( new EntryUi( entry, entries[i].Id ) );
		}
	}

	public void Dispose()
	{
		_host?.Delete();
		_host = null;
	}

	readonly struct EntryUi
	{
		public Panel Root { get; }
		public string Id { get; }
		public EntryUi( Panel root, string id )
		{
			Root = root;
			Id = id;
		}
	}
}
