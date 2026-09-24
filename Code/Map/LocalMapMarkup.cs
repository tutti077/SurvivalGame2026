using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Which map tool the pointer is driving on the Map page.</summary>
public enum MapMarkupTool
{
	Pin,
	Pen,
	Eraser,
}

/// <summary>
/// The local player's map markup (pins + pen strokes) for the world they are in. Client-local
/// data — nothing here is networked. Loaded from <see cref="MapMarkupSaveStore"/> on first use
/// per (player, world) and written through on every committed change (pin add / remove / rename /
/// cross-off, stroke end, erase end, eye toggle). <see cref="Version"/> bumps on every change so
/// the map faces rebuild their markup panels only when something actually changed.
/// </summary>
public static class LocalMapMarkup
{
	/// <summary>Pen brush width on screen at the moment of drawing; stored in meters at that zoom.</summary>
	public const float PenWidthPixels = 5f;
	/// <summary>Eraser radius on screen.</summary>
	public const float EraserRadiusPixels = 14f;
	/// <summary>New stroke points closer than this on screen are skipped (keeps strokes small).</summary>
	public const float StrokePointSpacingPixels = 3f;

	static string _loadedPlayerKey;
	static string _loadedWorldKey;
	static MapMarkupSaveFile _file;

	/// <summary>Increments on every change; UI compares it to rebuild.</summary>
	public static int Version { get; private set; }

	/// <summary>Tool selection is per machine session, not saved.</summary>
	public static MapMarkupTool Tool { get; set; } = MapMarkupTool.Pin;

	public static string SelectedPinIcon { get; set; } = MapPinCatalog.DefaultIconId;

	public static bool ShowPins => Current.ShowPins;

	public static bool ShareLocation => Current.ShareLocation;

	public static IReadOnlyList<MapPinData> Pins => Current.Pins;

	public static IReadOnlyList<MapStrokeData> Strokes => Current.Strokes;

	static MapMarkupSaveFile Current
	{
		get
		{
			EnsureLoaded();
			return _file;
		}
	}

	/// <summary>Loads the file for the current player + world when either changed (scene load, new world).</summary>
	public static void EnsureLoaded()
	{
		var playerKey = QuestTracker.PlayerKey;
		var worldKey = ResolveWorldKey();
		if ( _file is not null
		     && string.Equals( playerKey, _loadedPlayerKey, StringComparison.Ordinal )
		     && string.Equals( worldKey, _loadedWorldKey, StringComparison.Ordinal ) )
			return;

		_loadedPlayerKey = playerKey;
		_loadedWorldKey = worldKey;
		_file = MapMarkupSaveStore.Load( playerKey, worldKey ) ?? new MapMarkupSaveFile();
		Version++;
	}

	/// <summary>World name + seed when a TerrainWorld is streaming; otherwise the scene name (hand-built test scenes).</summary>
	static string ResolveWorldKey()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return "world";

		foreach ( var manager in scene.GetAllComponents<TerrainWorldManager>() )
		{
			if ( manager is null || !manager.IsValid() )
				continue;

			return $"{manager.WorldName}_{manager.WorldSeed}";
		}

		return $"scene_{scene.Name}";
	}

	public static MapPinData AddPin( string iconId, Vector2 worldMeters )
	{
		var file = Current;
		var pin = new MapPinData
		{
			Id = Guid.NewGuid(),
			Icon = MapPinCatalog.IsKnown( iconId ) ? iconId : MapPinCatalog.DefaultIconId,
			Name = "",
			XMeters = worldMeters.x,
			YMeters = worldMeters.y,
		};
		file.Pins.Add( pin );
		Commit();
		return pin;
	}

	public static MapPinData FindPin( Guid id )
	{
		foreach ( var pin in Current.Pins )
		{
			if ( pin.Id == id )
				return pin;
		}

		return null;
	}

	public static bool RemovePin( Guid id )
	{
		var pin = FindPin( id );
		if ( pin is null )
			return false;

		Current.Pins.Remove( pin );
		Commit();
		return true;
	}

	public static bool ToggleCrossedOff( Guid id )
	{
		var pin = FindPin( id );
		if ( pin is null )
			return false;

		pin.CrossedOff = !pin.CrossedOff;
		Commit();
		return true;
	}

	public static bool RenamePin( Guid id, string name )
	{
		var pin = FindPin( id );
		if ( pin is null )
			return false;

		name = (name ?? "").Trim();
		if ( name.Length > 32 )
			name = name.Substring( 0, 32 );
		if ( string.Equals( pin.Name, name, StringComparison.Ordinal ) )
			return false;

		pin.Name = name;
		Commit();
		return true;
	}

	public static void SetShareLocation( bool share )
	{
		if ( Current.ShareLocation == share )
			return;

		Current.ShareLocation = share;
		Commit();
	}

	public static void SetShowPins( bool show )
	{
		if ( Current.ShowPins == show )
			return;

		Current.ShowPins = show;
		Commit();
	}

	/// <summary>Starts a pen stroke; points are appended while the button is held and the file is written on <see cref="EndStroke"/>.</summary>
	public static MapStrokeData BeginStroke( float widthMeters, Vector2 firstPointMeters )
	{
		var stroke = new MapStrokeData { WidthMeters = MathF.Max( 0.01f, widthMeters ) };
		stroke.Points.Add( firstPointMeters.x );
		stroke.Points.Add( firstPointMeters.y );
		Current.Strokes.Add( stroke );
		Version++;
		return stroke;
	}

	public static void AppendStrokePoint( MapStrokeData stroke, Vector2 pointMeters, float minSpacingMeters )
	{
		if ( stroke is null || stroke.PointCount == 0 )
			return;

		var last = stroke.PointAt( stroke.PointCount - 1 );
		if ( (pointMeters - last).Length < minSpacingMeters )
			return;

		stroke.Points.Add( pointMeters.x );
		stroke.Points.Add( pointMeters.y );
		Version++;
	}

	public static void EndStroke( MapStrokeData stroke )
	{
		if ( stroke is null )
			return;

		// A click with no drag is a dot: keep it as a two-point stroke so it renders.
		if ( stroke.PointCount == 1 )
		{
			stroke.Points.Add( stroke.Points[0] );
			stroke.Points.Add( stroke.Points[1] );
		}

		Commit();
	}

	/// <summary>Removes every stroke point within <paramref name="radiusMeters"/>, splitting strokes around the hole. Returns true when anything was removed.</summary>
	public static bool Erase( Vector2 centerMeters, float radiusMeters )
	{
		var file = Current;
		var radiusSq = radiusMeters * radiusMeters;
		var changed = false;
		var rebuilt = new List<MapStrokeData>( file.Strokes.Count + 4 );

		foreach ( var stroke in file.Strokes )
		{
			MapStrokeData piece = null;
			var removedAny = false;
			var count = stroke.PointCount;
			for ( var i = 0; i < count; i++ )
			{
				var p = stroke.PointAt( i );
				var hit = (p - centerMeters).LengthSquared <= radiusSq;
				if ( hit )
				{
					removedAny = true;
					if ( piece is not null && piece.PointCount >= 2 )
						rebuilt.Add( piece );
					piece = null;
					continue;
				}

				piece ??= new MapStrokeData { WidthMeters = stroke.WidthMeters };
				piece.Points.Add( p.x );
				piece.Points.Add( p.y );
			}

			if ( !removedAny )
			{
				rebuilt.Add( stroke );
				continue;
			}

			changed = true;
			if ( piece is not null && piece.PointCount >= 2 )
				rebuilt.Add( piece );
		}

		if ( !changed )
			return false;

		file.Strokes = rebuilt;
		Version++;
		return true;
	}

	/// <summary>Write-through after an erase drag ends (points are removed live while the button is held).</summary>
	public static void CommitErase() => Commit();

	static void Commit()
	{
		Version++;
		MapMarkupSaveStore.Save( _loadedPlayerKey, _loadedWorldKey, _file );
	}

	[ConCmd( "map_markup_clear" )]
	public static void ConCmdClear()
	{
		var file = Current;
		file.Pins.Clear();
		file.Strokes.Clear();
		Commit();
		Log.Info( $"[Map] Markup cleared for player '{_loadedPlayerKey}' world '{_loadedWorldKey}'." );
	}
}
