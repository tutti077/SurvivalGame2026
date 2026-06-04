using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Left-side pickup toasts: icon + quantity, newest at bottom, fade out over time.</summary>
public sealed class PickupNotificationHud
{
	public const float RowHeight = 52f;
	public const float RowGap = 6f;
	public const float IconSize = 40f;
	public const int MaxVisibleRows = 7;
	public const float LifetimeSeconds = 3.5f;
	public const float FadeSeconds = 0.85f;

	readonly List<PickupToastEntry> _entries = new();

	Panel _host;
	Panel _list;
	bool _built;

	public void Build( Panel root )
	{
		if ( _built )
			return;

		_host = new Panel { Parent = root };
		_host.Style.Set( "position", "absolute" );
		_host.Style.Set( "left", "16px" );
		_host.Style.Set( "top", "22%" );
		_host.Style.Set( "bottom", "28%" );
		_host.Style.Set( "width", "220px" );
		_host.Style.Set( "pointer-events", "none" );
		_host.Style.Set( "overflow", "hidden" );
		_host.Style.Set( "z-index", "3" );

		_list = new Panel { Parent = _host };
		_list.Style.Set( "position", "absolute" );
		_list.Style.Set( "left", "0" );
		_list.Style.Set( "right", "0" );
		_list.Style.Set( "bottom", "0" );
		_list.Style.Set( "flex-direction", "column" );
		_list.Style.Set( "justify-content", "flex-end" );
		_list.Style.Set( "align-items", "flex-start" );
		_list.Style.Set( "gap", $"{RowGap}px" );

		_built = true;
	}

	public void Enqueue( ResourcePickupNotice notice )
	{
		if ( !_built || _list is null || notice.Amount <= 0 || string.IsNullOrWhiteSpace( notice.ResourceId ) )
			return;

		while ( _entries.Count >= MaxVisibleRows )
			RemoveOldest();

		var entry = CreateRow( notice );
		_entries.Add( entry );

		entry.Row.Style.Set( "transform", "translateY( 12px )" );
		entry.Row.Style.Set( "opacity", "0" );
		entry.SpawnTime = Time.NowDouble;
		entry.SlideStartTime = entry.SpawnTime;
	}

	public void Tick()
	{
		if ( !_built )
			return;

		var now = Time.NowDouble;

		for ( var i = _entries.Count - 1; i >= 0; i-- )
		{
			var entry = _entries[i];
			if ( entry.Row is null || !entry.Row.IsValid() )
			{
				_entries.RemoveAt( i );
				continue;
			}

			var age = (float)( now - entry.SpawnTime );
			var slideT = Math.Clamp( (float)( ( now - entry.SlideStartTime ) / 0.18f ), 0f, 1f );
			var slideOffset = 12f * ( 1f - slideT );
			entry.Row.Style.Set( "transform", $"translateY( {slideOffset}px )" );

			if ( age >= LifetimeSeconds )
			{
				RemoveEntryAt( i );
				continue;
			}

			var opacity = 1f;
			if ( age > LifetimeSeconds - FadeSeconds )
			{
				var fadeT = ( age - ( LifetimeSeconds - FadeSeconds ) ) / FadeSeconds;
				opacity = 1f - Math.Clamp( fadeT, 0f, 1f );
			}

			entry.Row.Style.Set( "opacity", opacity.ToString( "0.###" ) );
		}
	}

	PickupToastEntry CreateRow( ResourcePickupNotice notice )
	{
		var def = ResourceCatalog.Resolve( notice.ResourceId );
		var iconPath = ResourceCatalog.GetIconPath( notice.ResourceId );

		var row = new Panel { Parent = _list };
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "align-items", "center" );
		row.Style.Set( "gap", "10px" );
		row.Style.Set( "flex-shrink", "0" );
		row.Style.MinHeight = Length.Pixels( RowHeight );
		row.Style.PaddingTop = Length.Pixels( 6f );
		row.Style.PaddingBottom = Length.Pixels( 6f );
		row.Style.PaddingLeft = Length.Pixels( 8f );
		row.Style.PaddingRight = Length.Pixels( 12f );
		row.Style.BackgroundColor = new Color( 0.06f, 0.07f, 0.09f, 0.88f );
		row.Style.Set( "border-width", "1px" );
		row.Style.Set( "border-color", "#3a4250" );
		row.Style.Set( "border-radius", "6px" );
		row.Style.Set( "pointer-events", "none" );

		var icon = new Panel { Parent = row };
		icon.Style.Width = Length.Pixels( IconSize );
		icon.Style.Height = Length.Pixels( IconSize );
		icon.Style.Set( "flex-shrink", "0" );
		icon.Style.Set( "background-size", "contain" );
		icon.Style.Set( "background-repeat", "no-repeat" );
		icon.Style.Set( "background-position", "center" );
		icon.Style.Set( "border-radius", "4px" );
		icon.Style.BackgroundColor = def.FallbackColor.WithAlpha( 0.85f );

		if ( !MenuUiTextures.ApplyBackground( icon, iconPath ) && def.Icon is not null )
			icon.Style.SetBackgroundImage( def.Icon );

		var qty = new Label { Parent = row, Text = $"+{notice.Amount}" };
		qty.Style.FontColor = Color.White;
		qty.Style.FontSize = Length.Pixels( 18f );
		qty.Style.Set( "font-weight", "bold" );

		return new PickupToastEntry
		{
			Row = row,
			SpawnTime = Time.NowDouble,
			SlideStartTime = Time.NowDouble
		};
	}

	void RemoveOldest()
	{
		if ( _entries.Count == 0 )
			return;

		RemoveEntryAt( 0 );
	}

	void RemoveEntryAt( int index )
	{
		if ( index < 0 || index >= _entries.Count )
			return;

		var entry = _entries[index];
		if ( entry.Row is not null && entry.Row.IsValid() )
			entry.Row.Delete();

		_entries.RemoveAt( index );
	}

	sealed class PickupToastEntry
	{
		public Panel Row;
		public double SpawnTime;
		public double SlideStartTime;
	}
}
