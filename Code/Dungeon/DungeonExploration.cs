using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// What the local player has discovered of a dungeon: rooms and halls they have stood in, and the floor they are
/// on now. Client-local session state (the map page shows only your own progress, like your pins), reset whenever
/// the dungeon is rebuilt. <see cref="Version"/> bumps on every change so the map face rebuilds only then.
/// </summary>
public sealed class DungeonExploration
{
	public int Version { get; private set; }
	public int CurrentFloor { get; private set; }

	readonly HashSet<int> _rooms = new();
	readonly HashSet<int> _halls = new();

	public int VisitedRoomCount => _rooms.Count;
	public bool IsRoomVisited( int roomIndex ) => _rooms.Contains( roomIndex );
	public bool IsHallVisited( int hallIndex ) => _halls.Contains( hallIndex );

	/// <summary>Forget everything; the entrance room is always known.</summary>
	public void Reset( DungeonLayout layout )
	{
		_rooms.Clear();
		_halls.Clear();
		CurrentFloor = 0;
		if ( layout is not null && layout.EntranceRoom >= 0 )
			_rooms.Add( layout.EntranceRoom );
		Version++;
	}

	/// <summary>
	/// Sample the viewer's position: <paramref name="localUnits"/> in the generator's space picks the floor and cell,
	/// <paramref name="worldMeters"/> tests the hall rectangles on that floor. Returns true when anything changed.
	/// </summary>
	public bool Track( DungeonLayout layout, DungeonMapModel map, Vector3 localUnits, Vector2 worldMeters )
	{
		if ( layout is null || map is null || map.FloorHeightUnits <= 0f || map.PitchUnits <= 0f )
			return false;

		var changed = false;

		// Past ~a third of the climb you count as being on the floor above, so the map flips mid-stairs, not at the top.
		var floor = (int)MathF.Floor( (localUnits.z + map.FloorHeightUnits * 0.35f) / map.FloorHeightUnits );
		floor = Math.Clamp( floor, 0, Math.Max( 0, layout.Floors - 1 ) );
		if ( floor != CurrentFloor )
		{
			CurrentFloor = floor;
			changed = true;
		}

		var cx = (int)MathF.Round( (localUnits.x - map.OriginLocalUnits.x) / map.PitchUnits );
		var cy = (int)MathF.Round( (localUnits.y - map.OriginLocalUnits.y) / map.PitchUnits );
		var room = layout.RoomAt( floor, cx, cy );
		if ( room >= 0 && _rooms.Add( room ) )
			changed = true;

		// Halls: the one you are standing in, plus any whose both ends you have already seen.
		for ( var i = 0; i < map.Halls.Count; i++ )
		{
			var hall = map.Halls[i];
			if ( hall.Floor != floor || _halls.Contains( i ) )
				continue;

			var inside = worldMeters.x >= hall.MinMeters.x && worldMeters.x <= hall.MaxMeters.x
			             && worldMeters.y >= hall.MinMeters.y && worldMeters.y <= hall.MaxMeters.y;
			var bothEnds = _rooms.Contains( hall.RoomA ) && _rooms.Contains( hall.RoomB );
			if ( inside || bothEnds )
			{
				_halls.Add( i );
				changed = true;
			}
		}

		if ( changed )
			Version++;
		return changed;
	}
}
