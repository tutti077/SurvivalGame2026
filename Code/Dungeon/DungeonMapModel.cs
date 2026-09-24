using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>A doorway on the map: its centre and whether the opening runs along world X (a door in a north / south wall).</summary>
public readonly record struct DungeonMapDoor( Vector2 CenterMeters, bool AlongX );

public sealed class DungeonMapRoom
{
	public int Index;
	public int Floor;
	public Vector2 CenterMeters;
	/// <summary>Interior half extents (round rooms: both the radius).</summary>
	public float HalfXMeters;
	public float HalfYMeters;
	public bool Round;
	public bool Corridor;
	public bool StairUp;
	public bool Landing;
	public bool Treasure;
	public bool Entrance;
	public Color Color;
	public readonly List<DungeonMapDoor> Doors = new();
	/// <summary>Stair run (StairUp) / stairwell hole (Landing) footprint; zero-size otherwise.</summary>
	public Vector2 StairMinMeters;
	public Vector2 StairMaxMeters;
}

public sealed class DungeonMapHall
{
	public int Index;
	public int Floor;
	public int RoomA;
	public int RoomB;
	/// <summary>Interior rectangle (walls not included).</summary>
	public Vector2 MinMeters;
	public Vector2 MaxMeters;
	public Color Color;
}

/// <summary>
/// The dungeon as the map page sees it: every room, hall, doorway and stair footprint in <b>world meters</b>
/// (the map's own space), computed once per build from the same layout + geometry parameters the boxes come
/// from, plus the few numbers <see cref="DungeonExploration"/> needs to place the viewer. Assumes the generator
/// object is not yawed (rects are axis-aligned on the map).
/// </summary>
public sealed class DungeonMapModel
{
	public readonly List<DungeonMapRoom> Rooms = new();
	public readonly List<DungeonMapHall> Halls = new();
	public int Floors;
	public float WallMeters;
	public float HallWidthMeters;
	public Vector2 EntranceDoorMeters;

	/// <summary>Generator-local units: where cell (0,0) sits, the cell pitch and floor height — for the cell lookup in <see cref="DungeonExploration"/>.</summary>
	public Vector3 OriginLocalUnits;
	public float PitchUnits;
	public float FloorHeightUnits;

	public static DungeonMapModel Build( DungeonLayout layout, DungeonGeometryParams p, Transform generatorWorld )
	{
		var model = new DungeonMapModel
		{
			Floors = layout.Floors,
			WallMeters = TerrainWorldUnits.EngineToMeters( p.WallThickness ),
			HallWidthMeters = TerrainWorldUnits.EngineToMeters( p.HallWidth ),
			PitchUnits = p.Pitch,
			FloorHeightUnits = p.FloorHeight,
		};

		if ( layout.EntranceRoom < 0 )
			return model;

		var entrance = layout.Rooms[layout.EntranceRoom];
		var origin = new Vector2( -entrance.CenterCellX * p.Pitch, -entrance.CenterCellY * p.Pitch );
		model.OriginLocalUnits = new Vector3( origin.x, origin.y, 0f );

		Vector2 ToMeters( Vector2 localUnits )
		{
			var world = generatorWorld.PointToWorld( new Vector3( localUnits.x, localUnits.y, 0f ) );
			return new Vector2( TerrainWorldUnits.EngineToMeters( world.x ), TerrainWorldUnits.EngineToMeters( world.y ) );
		}

		Vector2 RoomCenter( DungeonRoom room ) => origin + new Vector2( room.CenterCellX * p.Pitch, room.CenterCellY * p.Pitch );
		Vector2 CellCenter( int cx, int cy ) => origin + new Vector2( cx * p.Pitch, cy * p.Pitch );

		foreach ( var room in layout.Rooms )
		{
			var center = RoomCenter( room );
			var hx = TerrainWorldUnits.MetersToEngine( room.HalfXMeters );
			var hy = TerrainWorldUnits.MetersToEngine( room.HalfYMeters );

			var mapRoom = new DungeonMapRoom
			{
				Index = room.Index,
				Floor = room.Floor,
				CenterMeters = ToMeters( center ),
				HalfXMeters = room.HalfXMeters,
				HalfYMeters = room.HalfYMeters,
				Round = room.Shape == DungeonRoomShape.Round,
				Corridor = room.Has( DungeonRoomFlags.Corridor ),
				StairUp = room.Has( DungeonRoomFlags.StairUp ),
				Landing = room.Has( DungeonRoomFlags.Landing ),
				Treasure = room.Has( DungeonRoomFlags.Treasure ),
				Entrance = room.Has( DungeonRoomFlags.Entrance ),
				Color = DungeonGeometry.RoomColor( room, p.ColorByDepth ),
			};

			foreach ( var door in room.Doors )
				mapRoom.Doors.Add( new DungeonMapDoor( ToMeters( DoorPoint( room, door.Side, CellCenter( door.CellX, door.CellY ) - center, center, hx, hy, p ) ), door.Side is DungeonSide.North or DungeonSide.South ) );

			if ( mapRoom.Entrance )
			{
				var outside = DoorPoint( room, layout.EntranceSide, Vector2.Zero, center, hx, hy, p );
				mapRoom.Doors.Add( new DungeonMapDoor( ToMeters( outside ), layout.EntranceSide is DungeonSide.North or DungeonSide.South ) );
				model.EntranceDoorMeters = ToMeters( outside );
			}

			if ( mapRoom.StairUp || mapRoom.Landing )
			{
				var (min, max) = DungeonGeometry.StairFootprint( room, center, hx, p );
				var a = ToMeters( min );
				var b = ToMeters( max );
				mapRoom.StairMinMeters = Vector2.Min( a, b );
				mapRoom.StairMaxMeters = Vector2.Max( a, b );
			}

			model.Rooms.Add( mapRoom );
		}

		for ( var i = 0; i < layout.Halls.Count; i++ )
		{
			var hall = layout.Halls[i];
			var a = layout.Rooms[hall.RoomA];
			var b = layout.Rooms[hall.RoomB];
			var cell = CellCenter( hall.CellX, hall.CellY );
			var step = DungeonLayout.Step[(int)hall.Side];
			var otherCell = CellCenter( hall.CellX + step.dx, hall.CellY + step.dy );

			var v0 = DungeonGeometry.FaceDistance( a, hall.Side, cell - RoomCenter( a ), TerrainWorldUnits.MetersToEngine( a.HalfXMeters ), TerrainWorldUnits.MetersToEngine( a.HalfYMeters ), p );
			var v1 = p.Pitch - DungeonGeometry.FaceDistance( b, DungeonLayout.Opposite( hall.Side ), otherCell - RoomCenter( b ), TerrainWorldUnits.MetersToEngine( b.HalfXMeters ), TerrainWorldUnits.MetersToEngine( b.HalfYMeters ), p );
			if ( v1 - v0 < 1f )
				continue;

			var (min, max) = DungeonGeometry.FrameRect( cell, hall.Side, -p.HallWidth * 0.5f, p.HallWidth * 0.5f, v0, v1 );
			var ma = ToMeters( min );
			var mb = ToMeters( max );
			model.Halls.Add( new DungeonMapHall
			{
				Index = i,
				Floor = a.Floor,
				RoomA = a.Index,
				RoomB = b.Index,
				MinMeters = Vector2.Min( ma, mb ),
				MaxMeters = Vector2.Max( ma, mb ),
				Color = Color.Lerp( DungeonGeometry.RoomColor( a, p.ColorByDepth ), DungeonGeometry.RoomColor( b, p.ColorByDepth ), 0.5f ),
			} );
		}

		return model;
	}

	/// <summary>Point on the room's wall line where a doorway sits (local units): rect walls at the half extent, round rooms on the ring.</summary>
	static Vector2 DoorPoint( DungeonRoom room, DungeonSide side, Vector2 cellOffset, Vector2 center, float hx, float hy, DungeonGeometryParams p )
	{
		var n = DungeonGeometry.Normal( side );
		var t = DungeonGeometry.Tangent( side );
		var u = cellOffset.x * t.x + cellOffset.y * t.y;

		if ( room.Shape == DungeonRoomShape.Round )
		{
			var r = hx;
			var uc = Math.Clamp( u, -r + 1f, r - 1f );
			var v = MathF.Sqrt( MathF.Max( 0f, r * r - uc * uc ) );
			return center + n * v + t * uc;
		}

		var hn = side is DungeonSide.North or DungeonSide.South ? hy : hx;
		return center + n * hn + t * u;
	}
}
