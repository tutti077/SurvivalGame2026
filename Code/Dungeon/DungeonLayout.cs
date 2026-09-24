using System;
using System.Collections.Generic;

namespace Survival;

/// <summary>Cardinal side of a room / grid step. Order matches <see cref="DungeonLayout.Step"/>.</summary>
public enum DungeonSide
{
	North = 0, // +Y
	East = 1,  // +X
	South = 2, // -Y
	West = 3,  // -X
}

[Flags]
public enum DungeonRoomFlags
{
	None = 0,
	/// <summary>Has the outside doorway + sign; tree root.</summary>
	Entrance = 1 << 0,
	/// <summary>Holds a stair run up to the landing directly above (always a 1×1 rectangle).</summary>
	StairUp = 1 << 1,
	/// <summary>Sits directly above a StairUp room; its floor plate has the stairwell hole.</summary>
	Landing = 1 << 2,
	/// <summary>Dead end that spawns a chest.</summary>
	Treasure = 1 << 3,
	/// <summary>A hallway cell: interior is one hall width and its open sides have no wall at all. Runs of these are the long stretches.</summary>
	Corridor = 1 << 4,
}

public enum DungeonRoomShape
{
	Rect,
	Round,
}

public enum DungeonPillarLayout
{
	None,
	Center,
	Square,
	Corners,
	Row,
	Ring,
	/// <summary>Pillars on a regular grid across the whole room (big halls).</summary>
	Grid,
	/// <summary>Two rows of pillars along the room's long axis.</summary>
	Colonnade,
}

/// <summary>A doorway cut in one perimeter cell-side of a room.</summary>
public sealed class DungeonDoor
{
	public DungeonSide Side;
	/// <summary>The room's own cell the doorway sits in (absolute grid cell).</summary>
	public int CellX;
	public int CellY;
	public int TargetRoom;
}

public sealed class DungeonRoom
{
	public int Index;
	/// <summary>Anchor cell (minimum X / Y of the footprint).</summary>
	public int X;
	public int Y;
	public int CellsW = 1;
	public int CellsH = 1;
	public int Floor;
	public DungeonRoomShape Shape;
	/// <summary>Interior half extents in meters. Round rooms use HalfX as the radius (HalfY equal).</summary>
	public float HalfXMeters;
	public float HalfYMeters;
	public DungeonRoomFlags Flags;
	public DungeonPillarLayout Pillars;
	/// <summary>Tree distance from the entrance (halls, corridor cells and stairs each count one).</summary>
	public int Depth;
	/// <summary>Real rooms on the path from the entrance; corridor cells inherit their parent's. Drives the colour cycle.</summary>
	public int ColorDepth;
	/// <summary>Parent room index in the tree; -1 for the entrance.</summary>
	public int Parent = -1;
	public readonly List<DungeonDoor> Doors = new();
	/// <summary>Side the stair run hugs (StairUp) / the hole lies along (Landing). Both rooms share it; never a door side.</summary>
	public DungeonSide StairSide;
	/// <summary>No doorway may be cut on this side (landing: the stairwell hole; entrance: the outside doorway).</summary>
	public DungeonSide? BlockedSide;

	public bool Has( DungeonRoomFlags flag ) => (Flags & flag) != 0;
	public int DoorCount => Doors.Count;
	public bool IsSingleCell => CellsW == 1 && CellsH == 1;
	public float RadiusMeters => HalfXMeters;

	/// <summary>Footprint centre in cell coordinates (may be fractional for even footprints).</summary>
	public float CenterCellX => X + (CellsW - 1) * 0.5f;
	public float CenterCellY => Y + (CellsH - 1) * 0.5f;

	public bool Contains( int cx, int cy ) => cx >= X && cx < X + CellsW && cy >= Y && cy < Y + CellsH;

	public bool HasDoorOn( DungeonSide side )
	{
		foreach ( var d in Doors )
			if ( d.Side == side ) return true;
		return false;
	}

	public bool HasDoorAt( DungeonSide side, int cx, int cy )
	{
		foreach ( var d in Doors )
			if ( d.Side == side && d.CellX == cx && d.CellY == cy ) return true;
		return false;
	}
}

/// <summary>Straight hallway leaving room A's cell (CellX, CellY) through <see cref="Side"/> into the neighbouring cell, which belongs to room B.</summary>
public sealed class DungeonHall
{
	public int RoomA;
	public int RoomB;
	public DungeonSide Side;
	public int CellX;
	public int CellY;
}

/// <summary>Designer inputs for one generation. Everything in meters or counts; the geometry builder converts once.</summary>
public sealed class DungeonLayoutSettings
{
	public int Width = 10;
	public int Depth = 10;
	public int Floors = 3;
	public int RoomsPerFloor = 12;
	public int StairsPerFloor = 2;
	public float PitchMeters = 16f;
	/// <summary>Base interior size of a single-cell room; multi-cell rooms add one pitch per extra cell.</summary>
	public float MediumRoomMeters = 9f;
	public float LargeRoomMeters = 12f;
	public float HallWidthMeters = 2.5f;
	/// <summary>Share of dead ends (besides the deepest, which always gets one) that receive a chest.</summary>
	public float ChestDeadEndFraction = 0.5f;
	/// <summary>0..1 — how often growth continues from the newest room (long winding paths) instead of a random open room (bushy branching).</summary>
	public float WindingBias = 0.6f;
	/// <summary>0..1 — chance a growth step lays a corridor run (1..MaxCorridorRun cells) before the next room.</summary>
	public float CorridorChance = 0.5f;
	public int MaxCorridorRun = 3;
	/// <summary>0..1 — chance a new room tries a multi-cell footprint (2×1, 2×2, 3×2, 3×3) before falling back to one cell.</summary>
	public float BigRoomChance = 0.4f;
	/// <summary>0..1 — chance a room is round (1×1 or 2×2 footprints only).</summary>
	public float RoundRoomChance = 0.2f;
}

/// <summary>
/// Pure, seeded dungeon graph: a tree of rooms on a cell grid per floor (rooms may span several cells), joined by
/// straight halls and corridor-cell runs, with 1×1 stair rooms linking floor <c>f</c> to a landing directly above on
/// <c>f + 1</c>. No engine types — every peer builds the identical layout from the same seed.
/// </summary>
public sealed class DungeonLayout
{
	public readonly List<DungeonRoom> Rooms = new();
	public readonly List<DungeonHall> Halls = new();
	public int EntranceRoom = -1;
	public DungeonSide EntranceSide = DungeonSide.West;
	public int Seed;
	public int Width;
	public int Depth;
	public int Floors;
	public float PitchMeters;

	int[,,] _cells;

	public static readonly (int dx, int dy)[] Step =
	{
		(0, 1),   // North
		(1, 0),   // East
		(0, -1),  // South
		(-1, 0),  // West
	};

	public static DungeonSide Opposite( DungeonSide side ) => (DungeonSide)(((int)side + 2) % 4);

	public int RoomAt( int floor, int x, int y )
	{
		if ( floor < 0 || floor >= Floors || x < 0 || x >= Width || y < 0 || y >= Depth )
			return -1;
		return _cells[floor, x, y];
	}

	bool IsFree( int floor, int x, int y ) =>
		floor >= 0 && floor < Floors && x >= 0 && x < Width && y >= 0 && y < Depth && _cells[floor, x, y] == -1;

	bool IsFree( int floor, int x, int y, int w, int h )
	{
		for ( var cx = x; cx < x + w; cx++ )
			for ( var cy = y; cy < y + h; cy++ )
				if ( !IsFree( floor, cx, cy ) ) return false;
		return true;
	}

	public static DungeonLayout Generate( DungeonLayoutSettings s, int seed )
	{
		var layout = new DungeonLayout
		{
			Seed = seed,
			Width = Math.Max( 2, s.Width ),
			Depth = Math.Max( 2, s.Depth ),
			Floors = Math.Max( 1, s.Floors ),
			PitchMeters = MathF.Max( s.LargeRoomMeters + 3f, s.PitchMeters ),
		};
		layout._cells = new int[layout.Floors, layout.Width, layout.Depth];
		for ( var f = 0; f < layout.Floors; f++ )
			for ( var x = 0; x < layout.Width; x++ )
				for ( var y = 0; y < layout.Depth; y++ )
					layout._cells[f, x, y] = -1;

		var rng = new Random( seed );

		// Entrance on the west edge, middle row, ground floor: a 12 m square with the outside doorway on its west wall.
		var entrance = layout.AddRoom( 0, 0, layout.Depth / 2, 1, 1, DungeonRoomShape.Rect, s.LargeRoomMeters * 0.5f, s.LargeRoomMeters * 0.5f, DungeonRoomFlags.Entrance, parent: -1 );
		entrance.BlockedSide = DungeonSide.West;
		layout.EntranceRoom = entrance.Index;
		layout.EntranceSide = DungeonSide.West;

		var seeds = new List<int> { entrance.Index };
		for ( var f = 0; f < layout.Floors; f++ )
		{
			if ( seeds.Count == 0 )
				break;

			layout.GrowFloor( s, rng, f, seeds );

			seeds = f + 1 < layout.Floors
				? layout.PlaceStairs( s, rng, f )
				: new List<int>();
		}

		layout.AssignPillars( rng );
		layout.AssignTreasure( s, rng );
		return layout;
	}

	DungeonRoom AddRoom( int floor, int x, int y, int w, int h, DungeonRoomShape shape, float halfX, float halfY, DungeonRoomFlags flags, int parent )
	{
		var room = new DungeonRoom
		{
			Index = Rooms.Count,
			X = x,
			Y = y,
			CellsW = w,
			CellsH = h,
			Floor = floor,
			Shape = shape,
			HalfXMeters = halfX,
			HalfYMeters = halfY,
			Flags = flags,
			Parent = parent,
			Depth = parent >= 0 ? Rooms[parent].Depth + 1 : 0,
		};
		var isCorridor = (flags & DungeonRoomFlags.Corridor) != 0;
		room.ColorDepth = parent < 0 ? 0 : Rooms[parent].ColorDepth + (isCorridor ? 0 : 1);

		Rooms.Add( room );
		for ( var cx = x; cx < x + w; cx++ )
			for ( var cy = y; cy < y + h; cy++ )
				_cells[floor, cx, cy] = room.Index;
		return room;
	}

	// ---- growth ----------------------------------------------------------------------------------

	readonly record struct GrowthSlot( int CellX, int CellY, DungeonSide Side );

	/// <summary>Every perimeter cell-side of the room whose outside neighbour is a free cell and that has no doorway yet.</summary>
	List<GrowthSlot> FreeSlots( DungeonRoom room )
	{
		var slots = new List<GrowthSlot>();
		for ( var cx = room.X; cx < room.X + room.CellsW; cx++ )
		{
			for ( var cy = room.Y; cy < room.Y + room.CellsH; cy++ )
			{
				for ( var i = 0; i < 4; i++ )
				{
					var side = (DungeonSide)i;
					if ( room.BlockedSide == side )
						continue;

					var nx = cx + Step[i].dx;
					var ny = cy + Step[i].dy;
					if ( room.Contains( nx, ny ) )
						continue; // interior cell-side
					if ( !IsFree( room.Floor, nx, ny ) )
						continue;
					if ( room.HasDoorAt( side, cx, cy ) )
						continue;

					slots.Add( new GrowthSlot( cx, cy, side ) );
				}
			}
		}

		return slots;
	}

	/// <summary>
	/// Growing-tree maze over the floor's free cells, started from every seed room already on it. A step out of an
	/// open slot either places a room next door or lays a straight corridor run first (bends and branches come from
	/// later steps out of corridor cells). Big rooms have more perimeter slots and are picked proportionally more
	/// often, which is what gives them their many doors. Corridor stubs that never reached a room are pruned.
	/// </summary>
	void GrowFloor( DungeonLayoutSettings s, Random rng, int floor, List<int> seeds )
	{
		var active = new List<int>( seeds );
		var roomsOnFloor = 0;
		foreach ( var seed in seeds )
			if ( !Rooms[seed].Has( DungeonRoomFlags.Corridor ) ) roomsOnFloor++;
		var target = Math.Max( roomsOnFloor + 1, s.RoomsPerFloor );

		var guard = 0;
		while ( active.Count > 0 && roomsOnFloor < target && guard++ < 4096 )
		{
			var pick = PickActive( rng, active, s.WindingBias );
			var room = Rooms[active[pick]];

			var slots = FreeSlots( room );
			if ( slots.Count == 0 )
			{
				active.RemoveAt( pick );
				continue;
			}

			var slot = slots[rng.Next( slots.Count )];
			var child = StepFrom( s, rng, floor, room, slot, active );
			if ( child is null )
				continue;

			roomsOnFloor++;

			// A big room is a hub: branch out of it right away so it ends up with several doors (Mark: "6 doors off it").
			if ( !child.IsSingleCell )
				roomsOnFloor += GrowHub( s, rng, floor, child, active );
		}

		PruneDeadEnds( floor );
	}

	/// <summary>
	/// One growth step out of <paramref name="slot"/>: optionally a straight corridor run (a later step out of any
	/// of its cells can bend or branch), then a room at the end. Returns the new room, or null when nothing fit.
	/// </summary>
	DungeonRoom StepFrom( DungeonLayoutSettings s, Random rng, int floor, DungeonRoom room, GrowthSlot slot, List<int> active )
	{
		var step = Step[(int)slot.Side];
		var current = room;
		var cx = slot.CellX;
		var cy = slot.CellY;

		if ( rng.NextDouble() < s.CorridorChance )
		{
			var run = rng.Next( 1, Math.Max( 1, s.MaxCorridorRun ) + 1 );
			for ( var k = 0; k < run; k++ )
			{
				var nx = cx + step.dx;
				var ny = cy + step.dy;
				if ( !IsFree( floor, nx, ny ) )
					break;

				var corridor = AddRoom( floor, nx, ny, 1, 1, DungeonRoomShape.Rect, s.HallWidthMeters * 0.5f, s.HallWidthMeters * 0.5f, DungeonRoomFlags.Corridor, current.Index );
				Link( current, cx, cy, slot.Side, corridor );
				active.Add( corridor.Index );
				current = corridor;
				cx = nx;
				cy = ny;
			}
		}

		var tx = cx + step.dx;
		var ty = cy + step.dy;
		if ( !IsFree( floor, tx, ty ) )
			return null;

		var child = PlaceRoom( s, rng, floor, tx, ty, current.Index );
		if ( child is null )
			return null;

		Link( current, cx, cy, slot.Side, child );
		active.Add( child.Index );
		return child;
	}

	/// <summary>Grow extra branches out of a multi-cell room until it has 3–6 doors or runs out of free slots. Returns rooms added.</summary>
	int GrowHub( DungeonLayoutSettings s, Random rng, int floor, DungeonRoom hub, List<int> active )
	{
		var perimeter = (hub.CellsW + hub.CellsH) * 2;
		var wanted = Math.Min( perimeter, rng.Next( 3, 7 ) );
		var added = 0;
		var guard = 0;

		while ( hub.DoorCount < wanted && guard++ < 16 )
		{
			var slots = FreeSlots( hub );
			if ( slots.Count == 0 )
				break;

			var child = StepFrom( s, rng, floor, hub, slots[rng.Next( slots.Count )], active );
			if ( child is not null )
				added++;
		}

		return added;
	}

	/// <summary>Newest room by <paramref name="windingBias"/>, otherwise a random room weighted by its perimeter (big rooms branch more).</summary>
	int PickActive( Random rng, List<int> active, float windingBias )
	{
		if ( rng.NextDouble() < windingBias )
			return active.Count - 1;

		var total = 0;
		foreach ( var index in active )
		{
			var r = Rooms[index];
			total += (r.CellsW + r.CellsH) * 2;
		}

		var roll = rng.Next( total );
		for ( var i = 0; i < active.Count; i++ )
		{
			var r = Rooms[active[i]];
			roll -= (r.CellsW + r.CellsH) * 2;
			if ( roll < 0 )
				return i;
		}

		return active.Count - 1;
	}

	readonly record struct Footprint( int W, int H, DungeonRoomShape Shape );

	/// <summary>Place a new room containing cell (<paramref name="tx"/>, <paramref name="ty"/>), trying a big footprint first and shrinking until one fits.</summary>
	DungeonRoom PlaceRoom( DungeonLayoutSettings s, Random rng, int floor, int tx, int ty, int parent )
	{
		var round = rng.NextDouble() < s.RoundRoomChance;
		var attempts = new List<Footprint>();

		if ( rng.NextDouble() < s.BigRoomChance )
		{
			if ( round )
			{
				attempts.Add( new Footprint( 2, 2, DungeonRoomShape.Round ) );
			}
			else
			{
				var roll = rng.NextDouble();
				if ( roll < 0.10 ) attempts.Add( new Footprint( 3, 3, DungeonRoomShape.Rect ) );
				if ( roll < 0.35 ) attempts.Add( rng.Next( 2 ) == 0 ? new Footprint( 3, 2, DungeonRoomShape.Rect ) : new Footprint( 2, 3, DungeonRoomShape.Rect ) );
				if ( roll < 0.70 ) attempts.Add( new Footprint( 2, 2, DungeonRoomShape.Rect ) );
				attempts.Add( rng.Next( 2 ) == 0 ? new Footprint( 2, 1, DungeonRoomShape.Rect ) : new Footprint( 1, 2, DungeonRoomShape.Rect ) );
			}
		}

		attempts.Add( new Footprint( 1, 1, round ? DungeonRoomShape.Round : DungeonRoomShape.Rect ) );

		foreach ( var fp in attempts )
		{
			// Every anchor whose footprint covers the target cell and is entirely free.
			var anchors = new List<(int x, int y)>();
			for ( var ax = tx - fp.W + 1; ax <= tx; ax++ )
				for ( var ay = ty - fp.H + 1; ay <= ty; ay++ )
					if ( IsFree( floor, ax, ay, fp.W, fp.H ) )
						anchors.Add( (ax, ay) );

			if ( anchors.Count == 0 )
				continue;

			var (x, y) = anchors[rng.Next( anchors.Count )];
			var baseMeters = fp.W == 1 && fp.H == 1 && fp.Shape == DungeonRoomShape.Rect && rng.NextDouble() < 0.5
				? s.MediumRoomMeters
				: s.LargeRoomMeters;
			var halfX = ((fp.W - 1) * PitchMeters + baseMeters) * 0.5f;
			var halfY = ((fp.H - 1) * PitchMeters + baseMeters) * 0.5f;
			return AddRoom( floor, x, y, fp.W, fp.H, fp.Shape, halfX, halfY, DungeonRoomFlags.None, parent );
		}

		return null;
	}

	void Link( DungeonRoom a, int cellX, int cellY, DungeonSide sideFromA, DungeonRoom b )
	{
		var nx = cellX + Step[(int)sideFromA].dx;
		var ny = cellY + Step[(int)sideFromA].dy;
		a.Doors.Add( new DungeonDoor { Side = sideFromA, CellX = cellX, CellY = cellY, TargetRoom = b.Index } );
		b.Doors.Add( new DungeonDoor { Side = Opposite( sideFromA ), CellX = nx, CellY = ny, TargetRoom = a.Index } );
		Halls.Add( new DungeonHall { RoomA = a.Index, RoomB = b.Index, Side = sideFromA, CellX = cellX, CellY = cellY } );
	}

	/// <summary>
	/// Remove what leads nowhere on this floor: corridor cells with a single door (cascading back along the run) and
	/// landings that never grew a door (their stair room below becomes a plain room again). Then re-index.
	/// </summary>
	void PruneDeadEnds( int floor )
	{
		var removed = new HashSet<int>();
		var changed = true;
		while ( changed )
		{
			changed = false;
			foreach ( var room in Rooms )
			{
				if ( room.Floor != floor || removed.Contains( room.Index ) )
					continue;

				if ( room.Has( DungeonRoomFlags.Corridor ) && room.DoorCount == 1 )
				{
					var door = room.Doors[0];
					Rooms[door.TargetRoom].Doors.RemoveAll( d => d.TargetRoom == room.Index );
					room.Doors.Clear();
					Halls.RemoveAll( h => h.RoomA == room.Index || h.RoomB == room.Index );
				}
				else if ( room.Has( DungeonRoomFlags.Landing ) && room.DoorCount == 0 )
				{
					var below = Rooms[room.Parent];
					below.Flags &= ~DungeonRoomFlags.StairUp;
				}
				else
				{
					continue;
				}

				_cells[floor, room.X, room.Y] = -1;
				removed.Add( room.Index );
				changed = true;
			}
		}

		if ( removed.Count == 0 )
			return;

		// Compact: halls, parents, doors, cells and the entrance all hold indices.
		var remap = new int[Rooms.Count];
		var kept = new List<DungeonRoom>( Rooms.Count - removed.Count );
		for ( var i = 0; i < Rooms.Count; i++ )
		{
			if ( removed.Contains( i ) )
			{
				remap[i] = -1;
				continue;
			}

			remap[i] = kept.Count;
			kept.Add( Rooms[i] );
		}

		foreach ( var room in kept )
		{
			room.Index = remap[room.Index];
			room.Parent = room.Parent >= 0 ? remap[room.Parent] : -1;
			foreach ( var door in room.Doors )
				door.TargetRoom = remap[door.TargetRoom];
			for ( var cx = room.X; cx < room.X + room.CellsW; cx++ )
				for ( var cy = room.Y; cy < room.Y + room.CellsH; cy++ )
					_cells[room.Floor, cx, cy] = room.Index;
		}

		foreach ( var hall in Halls )
		{
			hall.RoomA = remap[hall.RoomA];
			hall.RoomB = remap[hall.RoomB];
		}

		EntranceRoom = remap[EntranceRoom];
		Rooms.Clear();
		Rooms.AddRange( kept );
	}

	// ---- stairs ----------------------------------------------------------------------------------

	/// <summary>
	/// Pick stair rooms on <paramref name="floor"/> (single-cell rooms, deepest first, then random from the deeper half),
	/// turn them into 12 m squares and create their landings on the floor above. Returns the landing indices — the
	/// seeds for the next floor's growth.
	/// </summary>
	List<int> PlaceStairs( DungeonLayoutSettings s, Random rng, int floor )
	{
		var candidates = new List<DungeonRoom>();
		foreach ( var room in Rooms )
		{
			if ( room.Floor != floor || !room.IsSingleCell )
				continue;
			if ( room.Has( DungeonRoomFlags.Entrance ) || room.Has( DungeonRoomFlags.Landing ) || room.Has( DungeonRoomFlags.StairUp ) || room.Has( DungeonRoomFlags.Corridor ) )
				continue;
			if ( room.DoorCount > 2 )
				continue;
			if ( !IsFree( floor + 1, room.X, room.Y ) )
				continue;
			// The landing must have room to grow: at least two free cells next to it upstairs, or the stairs lead nowhere.
			if ( FreeNeighboursAbove( floor, room ) < 2 )
				continue;
			candidates.Add( room );
		}

		candidates.Sort( ( a, b ) => b.Depth.CompareTo( a.Depth ) );

		var landings = new List<int>();
		var wanted = Math.Max( 1, s.StairsPerFloor );
		while ( landings.Count < wanted && candidates.Count > 0 )
		{
			var pickIndex = landings.Count == 0 ? 0 : rng.Next( Math.Max( 1, candidates.Count / 2 ) );
			var room = candidates[pickIndex];
			candidates.RemoveAt( pickIndex );

			// Keep stairs apart: not in a cell adjacent to another stair room on this floor.
			var tooClose = false;
			foreach ( var other in landings )
			{
				var below = Rooms[Rooms[other].Parent];
				if ( Math.Abs( below.X - room.X ) + Math.Abs( below.Y - room.Y ) <= 1 )
				{
					tooClose = true;
					break;
				}
			}
			if ( tooClose )
				continue;

			// Stair wall: a side with no door here. Prefer one whose upstairs neighbour cell is already taken or off-grid,
			// so blocking that side on the landing costs no growth room.
			var freeSides = new List<DungeonSide>( 4 );
			var preferred = new List<DungeonSide>( 4 );
			for ( var i = 0; i < 4; i++ )
			{
				var side = (DungeonSide)i;
				if ( room.HasDoorOn( side ) )
					continue;
				freeSides.Add( side );
				if ( !IsFree( floor + 1, room.X + Step[i].dx, room.Y + Step[i].dy ) )
					preferred.Add( side );
			}
			if ( freeSides.Count == 0 )
				continue;

			room.Flags |= DungeonRoomFlags.StairUp;
			room.Shape = DungeonRoomShape.Rect;
			room.HalfXMeters = room.HalfYMeters = s.LargeRoomMeters * 0.5f;
			room.StairSide = preferred.Count > 0 ? preferred[rng.Next( preferred.Count )] : freeSides[rng.Next( freeSides.Count )];

			var landing = AddRoom( floor + 1, room.X, room.Y, 1, 1, DungeonRoomShape.Rect, s.LargeRoomMeters * 0.5f, s.LargeRoomMeters * 0.5f, DungeonRoomFlags.Landing, room.Index );
			landing.StairSide = room.StairSide;
			landing.BlockedSide = room.StairSide;
			landings.Add( landing.Index );
		}

		return landings;
	}

	int FreeNeighboursAbove( int floor, DungeonRoom room )
	{
		var count = 0;
		for ( var i = 0; i < 4; i++ )
			if ( IsFree( floor + 1, room.X + Step[i].dx, room.Y + Step[i].dy ) ) count++;
		return count;
	}

	// ---- dressing --------------------------------------------------------------------------------

	void AssignPillars( Random rng )
	{
		foreach ( var room in Rooms )
		{
			if ( room.Has( DungeonRoomFlags.Corridor ) || room.Has( DungeonRoomFlags.Entrance ) )
			{
				room.Pillars = DungeonPillarLayout.None;
				continue;
			}

			// Stairwells keep their strip along the wall clear.
			if ( room.Has( DungeonRoomFlags.StairUp ) || room.Has( DungeonRoomFlags.Landing ) )
			{
				room.Pillars = rng.NextDouble() < 0.5 ? DungeonPillarLayout.Center : DungeonPillarLayout.None;
				continue;
			}

			var roll = rng.NextDouble();
			if ( room.Shape == DungeonRoomShape.Round )
			{
				room.Pillars = room.IsSingleCell
					? (roll < 0.5 ? DungeonPillarLayout.None : DungeonPillarLayout.Center)
					: roll switch
					{
						< 0.20 => DungeonPillarLayout.None,
						< 0.45 => DungeonPillarLayout.Center,
						_ => DungeonPillarLayout.Ring,
					};
				continue;
			}

			if ( !room.IsSingleCell )
			{
				room.Pillars = roll switch
				{
					< 0.35 => DungeonPillarLayout.Grid,
					< 0.65 => DungeonPillarLayout.Colonnade,
					< 0.80 => DungeonPillarLayout.Square,
					< 0.90 => DungeonPillarLayout.Ring,
					_ => DungeonPillarLayout.None,
				};
				continue;
			}

			if ( room.HalfXMeters < 5.5f )
			{
				room.Pillars = roll switch
				{
					< 0.40 => DungeonPillarLayout.None,
					< 0.70 => DungeonPillarLayout.Center,
					_ => DungeonPillarLayout.Square,
				};
				continue;
			}

			room.Pillars = roll switch
			{
				< 0.15 => DungeonPillarLayout.None,
				< 0.30 => DungeonPillarLayout.Center,
				< 0.55 => DungeonPillarLayout.Square,
				< 0.70 => DungeonPillarLayout.Corners,
				< 0.85 => DungeonPillarLayout.Row,
				_ => DungeonPillarLayout.Ring,
			};
		}
	}

	/// <summary>Dead ends (one doorway, no stairs, not the entrance, not a corridor): the deepest always gets a chest, the rest by fraction.</summary>
	void AssignTreasure( DungeonLayoutSettings s, Random rng )
	{
		var deadEnds = new List<DungeonRoom>();
		foreach ( var room in Rooms )
		{
			if ( room.DoorCount != 1 )
				continue;
			if ( room.Has( DungeonRoomFlags.Entrance ) || room.Has( DungeonRoomFlags.StairUp ) || room.Has( DungeonRoomFlags.Landing ) || room.Has( DungeonRoomFlags.Corridor ) )
				continue;
			deadEnds.Add( room );
		}

		if ( deadEnds.Count == 0 )
			return;

		deadEnds.Sort( ( a, b ) => b.Depth.CompareTo( a.Depth ) );
		deadEnds[0].Flags |= DungeonRoomFlags.Treasure;

		for ( var i = 1; i < deadEnds.Count; i++ )
		{
			if ( rng.NextDouble() < s.ChestDeadEndFraction )
				deadEnds[i].Flags |= DungeonRoomFlags.Treasure;
		}
	}
}
