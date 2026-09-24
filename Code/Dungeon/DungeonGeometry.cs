using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Geometry inputs in engine units — the component converts its meter properties once and hands these over.</summary>
public sealed class DungeonGeometryParams
{
	public float Pitch;
	public float FloorHeight;
	public float PlateThickness;
	public float WallThickness;
	public float HallWidth;
	public float DoorHeight;
	public float StepRise;
	public float StepRun;
	public float PillarSize;
	public bool ColorByDepth = true;

	/// <summary>Wall clear height: from the walking surface to the underside of the plate above.</summary>
	public float ClearHeight => FloorHeight - PlateThickness;
	public int StepCount => Math.Max( 1, (int)MathF.Round( FloorHeight / MathF.Max( 1f, StepRise ) ) );
}

/// <summary>Where a chest should go, in the generator's local space.</summary>
public readonly record struct DungeonChestSlot( int RoomIndex, Vector3 LocalPosition, float LocalYaw );

/// <summary>Result of one geometry build — everything the component needs to place the sign, spawn and chests.</summary>
public sealed class DungeonGeometryResult
{
	public readonly List<GameObject> FloorRoots = new();
	public readonly List<DungeonChestSlot> ChestSlots = new();
	/// <summary>Outer face of the entrance doorway, at the walking surface (local space).</summary>
	public Vector3 EntranceDoorLocal;
	/// <summary>Unit vector pointing out of the entrance doorway (local space).</summary>
	public Vector3 EntranceOutwardLocal;
	public float EntranceDoorHeight;
	public int BoxCount;
}

/// <summary>
/// Turns a <see cref="DungeonLayout"/> into tinted dev boxes: floor plates, walls with doorways + lintels, corridor
/// cells, round rooms (inscribed floor strips + a ring of rotated wall segments), pillars, stair runs and stairwell
/// holes. One child object per floor so a floor can be hidden from the console. Pure local geometry: every peer
/// builds it from the synced seed; nothing here is networked.
/// </summary>
public static class DungeonGeometry
{
	const string BoxModelPath = "models/dev/box.vmdl";
	const string BoxMaterialPath = "materials/default.vmat";
	/// <summary>The dev box is a 50 u cube centred on its origin.</summary>
	const float BoxModelSize = 50f;
	/// <summary>Hall plates sit this far below room plates so abutting / overlapping tops never z-fight (a 2.5 cm lip).</summary>
	const float HallPlateDrop = 1f;

	/// <summary>Ten well-separated hues; a room's colour is its room-depth modulo this (walking back = cycling backwards).</summary>
	static readonly Color[] Palette =
	{
		new( 0.90f, 0.25f, 0.25f ), // red
		new( 0.95f, 0.55f, 0.15f ), // orange
		new( 0.95f, 0.85f, 0.20f ), // yellow
		new( 0.35f, 0.80f, 0.30f ), // green
		new( 0.20f, 0.75f, 0.65f ), // teal
		new( 0.25f, 0.60f, 0.95f ), // blue
		new( 0.45f, 0.35f, 0.90f ), // indigo
		new( 0.80f, 0.35f, 0.85f ), // purple
		new( 0.95f, 0.45f, 0.65f ), // pink
		new( 0.65f, 0.85f, 0.30f ), // lime
	};

	static readonly Color TreasureFloor = new( 1.00f, 0.80f, 0.25f );

	/// <summary>Corridor cells share their parent room's colour, so a long hallway stays one colour until the next room.</summary>
	public static Color RoomColor( DungeonRoom room, bool byDepth ) =>
		Palette[(byDepth ? room.ColorDepth : room.Index) % Palette.Length];

	public static DungeonGeometryResult Build( GameObject root, DungeonLayout layout, DungeonGeometryParams p )
	{
		var result = new DungeonGeometryResult();
		if ( root is null || !root.IsValid() || layout is null || layout.EntranceRoom < 0 )
			return result;

		var model = Model.Load( BoxModelPath );
		var material = Material.Load( BoxMaterialPath );

		for ( var f = 0; f < layout.Floors; f++ )
		{
			var floorRoot = new GameObject( true, $"Floor_{f}" );
			floorRoot.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
			floorRoot.Parent = root;
			floorRoot.LocalPosition = Vector3.Zero;
			floorRoot.LocalRotation = Rotation.Identity;
			floorRoot.LocalScale = Vector3.One;
			result.FloorRoots.Add( floorRoot );
		}

		var ctx = new BuildContext( layout, p, model, material, result );

		foreach ( var room in layout.Rooms )
		{
			if ( room.Shape == DungeonRoomShape.Round )
				BuildRoundRoom( ctx, room );
			else
				BuildRectRoom( ctx, room );
		}

		foreach ( var hall in layout.Halls )
			BuildHall( ctx, hall );

		result.BoxCount = ctx.BoxCount;
		return result;
	}

	sealed class BuildContext
	{
		public readonly DungeonLayout Layout;
		public readonly DungeonGeometryParams P;
		public readonly Model Model;
		public readonly Material Material;
		public readonly DungeonGeometryResult Result;
		public readonly Vector3 Origin;
		public int BoxCount;

		public BuildContext( DungeonLayout layout, DungeonGeometryParams p, Model model, Material material, DungeonGeometryResult result )
		{
			Layout = layout;
			P = p;
			Model = model;
			Material = material;
			Result = result;

			// The entrance room's centre is the generator's local origin, so the object's transform is the entrance.
			var entrance = layout.Rooms[layout.EntranceRoom];
			Origin = new Vector3( -entrance.CenterCellX * p.Pitch, -entrance.CenterCellY * p.Pitch, 0f );
		}

		public Vector3 RoomCenter( DungeonRoom room ) =>
			Origin + new Vector3( room.CenterCellX * P.Pitch, room.CenterCellY * P.Pitch, room.Floor * P.FloorHeight );

		public Vector2 CellCenter( int cx, int cy ) =>
			new( Origin.x + cx * P.Pitch, Origin.y + cy * P.Pitch );

		public float HalfX( DungeonRoom room ) => TerrainWorldUnits.MetersToEngine( room.HalfXMeters );
		public float HalfY( DungeonRoom room ) => TerrainWorldUnits.MetersToEngine( room.HalfYMeters );
	}

	// ---- frames ----------------------------------------------------------------------------------

	/// <summary>Outward unit vector for a side.</summary>
	static Vector2 Normal( DungeonSide side )
	{
		var s = DungeonLayout.Step[(int)side];
		return new Vector2( s.dx, s.dy );
	}

	/// <summary>Tangent along the wall of a side (rotated clockwise from the normal); axis-aligned like the normal.</summary>
	static Vector2 Tangent( DungeonSide side )
	{
		var n = Normal( side );
		return new Vector2( n.y, -n.x );
	}

	/// <summary>Axis-aligned XY rect from a wall frame: <c>u</c> along the tangent, <c>v</c> along the outward normal.</summary>
	static (Vector2 min, Vector2 max) FrameRect( Vector2 center, DungeonSide side, float u0, float u1, float v0, float v1 )
	{
		var n = Normal( side );
		var t = Tangent( side );
		var a = center + n * v0 + t * u0;
		var b = center + n * v1 + t * u1;
		return (Vector2.Min( a, b ), Vector2.Max( a, b ));
	}

	static float Dot( Vector2 a, Vector2 b ) => a.x * b.x + a.y * b.y;

	/// <summary>Half extent of a rect room along a side's normal / tangent.</summary>
	static float HalfAlongNormal( BuildContext ctx, DungeonRoom room, DungeonSide side ) =>
		side is DungeonSide.North or DungeonSide.South ? ctx.HalfY( room ) : ctx.HalfX( room );

	static float HalfAlongTangent( BuildContext ctx, DungeonRoom room, DungeonSide side ) =>
		side is DungeonSide.North or DungeonSide.South ? ctx.HalfX( room ) : ctx.HalfY( room );

	/// <summary>Tangent offsets (from the room centre) of every doorway on <paramref name="side"/>, plus the outside doorway on the entrance.</summary>
	static List<float> DoorOffsets( BuildContext ctx, DungeonRoom room, DungeonSide side )
	{
		var center = ctx.RoomCenter( room );
		var xy = new Vector2( center.x, center.y );
		var t = Tangent( side );
		var offsets = new List<float>();

		foreach ( var door in room.Doors )
		{
			if ( door.Side != side )
				continue;
			offsets.Add( Dot( ctx.CellCenter( door.CellX, door.CellY ) - xy, t ) );
		}

		if ( room.Has( DungeonRoomFlags.Entrance ) && side == ctx.Layout.EntranceSide )
			offsets.Add( 0f );

		offsets.Sort();
		return offsets;
	}

	// ---- rect rooms ------------------------------------------------------------------------------

	static void BuildRectRoom( BuildContext ctx, DungeonRoom room )
	{
		var p = ctx.P;
		var center = ctx.RoomCenter( room );
		var hx = ctx.HalfX( room );
		var hy = ctx.HalfY( room );
		var wt = p.WallThickness;
		var zf = center.z;
		var floorRoot = ctx.Result.FloorRoots[room.Floor];

		var color = RoomColor( room, p.ColorByDepth );
		var floorColor = room.Has( DungeonRoomFlags.Treasure ) ? TreasureFloor : color;
		var wallColor = color * 0.7f;
		var pillarColor = color * 0.45f;
		var xy = new Vector2( center.x, center.y );

		// Floor plate — full, or minus the stairwell hole on a landing.
		var plateMin = xy - new Vector2( hx + wt, hy + wt );
		var plateMax = xy + new Vector2( hx + wt, hy + wt );
		if ( room.Has( DungeonRoomFlags.Landing ) )
		{
			var (holeMin, holeMax) = StairRect( ctx, room, center, hx );
			foreach ( var (min, max) in SubtractRect( plateMin, plateMax, holeMin, holeMax ) )
				AddRect( ctx, floorRoot, $"plate_{room.Index}", min, max, zf - p.PlateThickness, zf, floorColor );
		}
		else
		{
			AddRect( ctx, floorRoot, $"plate_{room.Index}", plateMin, plateMax, zf - p.PlateThickness, zf, floorColor );
		}

		// Walls per side, split around every doorway on that side. North / South walls cover the corners; East / West
		// stop at the interior span so nothing overlaps. On a corridor cell the doorway is the whole side, so only the
		// N/S corner posts remain and there is no lintel.
		var isCorridor = room.Has( DungeonRoomFlags.Corridor );
		for ( var i = 0; i < 4; i++ )
		{
			var side = (DungeonSide)i;
			var hn = HalfAlongNormal( ctx, room, side );
			var ht = HalfAlongTangent( ctx, room, side );
			var extend = side is DungeonSide.North or DungeonSide.South ? wt : 0f;
			BuildWall( ctx, floorRoot, room, xy, side, -ht - extend, ht + extend, hn, hn + wt, zf, DoorOffsets( ctx, room, side ), wallColor, lintel: !isCorridor );
		}

		BuildPillars( ctx, floorRoot, room, xy, hx, hy, zf, pillarColor );

		if ( room.Has( DungeonRoomFlags.StairUp ) )
			BuildStairs( ctx, floorRoot, room, center, hx, color );

		if ( room.Has( DungeonRoomFlags.Entrance ) )
		{
			var n = Normal( ctx.Layout.EntranceSide );
			var outward = new Vector3( n.x, n.y, 0f );
			var hn = HalfAlongNormal( ctx, room, ctx.Layout.EntranceSide );
			ctx.Result.EntranceDoorLocal = center + outward * (hn + wt);
			ctx.Result.EntranceOutwardLocal = outward;
			ctx.Result.EntranceDoorHeight = p.DoorHeight;

			// A welcome slab outside the doorway so the first step in is not off the ground plane's edge.
			var (min, max) = FrameRect( xy, ctx.Layout.EntranceSide, -p.HallWidth, p.HallWidth, hn + wt, hn + wt + p.HallWidth * 2f );
			AddRect( ctx, floorRoot, "entrance_slab", min, max, zf - p.PlateThickness - HallPlateDrop, zf - HallPlateDrop, color );
		}

		if ( room.Has( DungeonRoomFlags.Treasure ) && room.Doors.Count > 0 )
		{
			// Against the wall opposite the single door, lined up with it.
			var door = room.Doors[0];
			var n = Normal( door.Side );
			var t = Tangent( door.Side );
			var hn = HalfAlongNormal( ctx, room, door.Side );
			var u = Dot( ctx.CellCenter( door.CellX, door.CellY ) - xy, t );
			var flat = xy - n * (hn - p.PillarSize) + t * u;
			var yaw = MathF.Atan2( n.y, n.x ).RadianToDegree();
			ctx.Result.ChestSlots.Add( new DungeonChestSlot( room.Index, new Vector3( flat.x, flat.y, zf ), yaw ) );
		}
	}

	static void BuildWall( BuildContext ctx, GameObject parent, DungeonRoom room, Vector2 center, DungeonSide side,
		float u0, float u1, float v0, float v1, float zf, List<float> doorOffsets, Color color, bool lintel )
	{
		var p = ctx.P;
		var top = zf + p.ClearHeight;
		var name = $"wall_{room.Index}_{side}";
		var halfDoor = p.HallWidth * 0.5f;

		var cursor = u0;
		foreach ( var d in doorOffsets )
		{
			var (min, max) = FrameRect( center, side, cursor, d - halfDoor, v0, v1 );
			AddRect( ctx, parent, name, min, max, zf, top, color );

			if ( lintel && top > zf + p.DoorHeight + 1f )
			{
				var (lMin, lMax) = FrameRect( center, side, d - halfDoor, d + halfDoor, v0, v1 );
				AddRect( ctx, parent, name + "_lintel", lMin, lMax, zf + p.DoorHeight, top, color );
			}

			cursor = d + halfDoor;
		}

		var (eMin, eMax) = FrameRect( center, side, cursor, u1, v0, v1 );
		AddRect( ctx, parent, name, eMin, eMax, zf, top, color );
	}

	// ---- round rooms -----------------------------------------------------------------------------

	static void BuildRoundRoom( BuildContext ctx, DungeonRoom room )
	{
		var p = ctx.P;
		var center = ctx.RoomCenter( room );
		var r = ctx.HalfX( room );
		var wt = p.WallThickness;
		var zf = center.z;
		var top = zf + p.ClearHeight;
		var floorRoot = ctx.Result.FloorRoots[room.Floor];
		var xy = new Vector2( center.x, center.y );

		var color = RoomColor( room, p.ColorByDepth );
		var floorColor = room.Has( DungeonRoomFlags.Treasure ) ? TreasureFloor : color;
		var wallColor = color * 0.7f;
		var pillarColor = color * 0.45f;

		// Floor: axis-aligned strips inscribed in the outer wall circle (a couple of units over, so the ring never overhangs).
		var plateRadius = r + wt + 2f;
		var strip = Math.Clamp( plateRadius / 24f, 8f, 20f );
		for ( var y0 = -plateRadius; y0 < plateRadius; y0 += strip )
		{
			var y1 = MathF.Min( y0 + strip, plateRadius );
			var farY = MathF.Max( MathF.Abs( y0 ), MathF.Abs( y1 ) );
			var halfW = MathF.Sqrt( MathF.Max( 0f, plateRadius * plateRadius - farY * farY ) );
			if ( halfW < 1f )
				continue;
			AddRect( ctx, floorRoot, $"plate_{room.Index}", xy + new Vector2( -halfW, y0 ), xy + new Vector2( halfW, y1 ), zf - p.PlateThickness, zf, floorColor );
		}

		// Doorways as angular spans around the ring.
		var doors = new List<(float mid, float half)>();
		foreach ( var door in room.Doors )
		{
			var n = Normal( door.Side );
			var t = Tangent( door.Side );
			var u = Dot( ctx.CellCenter( door.CellX, door.CellY ) - xy, t );
			var a0 = AngleOnRing( n, t, u - p.HallWidth * 0.5f, r );
			var a1 = AngleOnRing( n, t, u + p.HallWidth * 0.5f, r );
			var mid = AngleOnRing( n, t, u, r );
			var half = MathF.Max( MathF.Abs( WrapRadians( a0 - mid ) ), MathF.Abs( WrapRadians( a1 - mid ) ) );
			doors.Add( (mid, half) );
		}

		// Ring: ~1 m segments, rotated boxes overlapping a hair so the polygon closes.
		var rMid = r + wt * 0.5f;
		var segments = Math.Max( 12, (int)MathF.Ceiling( 2f * MathF.PI * rMid / 40f ) );
		var stepAngle = 2f * MathF.PI / segments;
		var segLength = 2f * (r + wt) * MathF.Sin( stepAngle * 0.5f ) + 1.5f;
		for ( var i = 0; i < segments; i++ )
		{
			var angle = (i + 0.5f) * stepAngle;
			var inDoor = false;
			foreach ( var (mid, half) in doors )
			{
				if ( MathF.Abs( WrapRadians( angle - mid ) ) <= half + stepAngle * 0.5f )
				{
					inDoor = true;
					break;
				}
			}
			if ( inDoor )
				continue;

			var c = xy + new Vector2( MathF.Cos( angle ), MathF.Sin( angle ) ) * rMid;
			AddBox( ctx, floorRoot, $"ring_{room.Index}", new Vector3( c.x, c.y, (zf + top) * 0.5f ), new Vector3( segLength, wt, top - zf ), angle.RadianToDegree() + 90f, wallColor );
		}

		// Lintel over each gap: a chord above door height, a little wider than the gap so it seats into the ring ends.
		foreach ( var (mid, half) in doors )
		{
			var chord = 2f * (r + wt) * MathF.Sin( half + stepAngle ) + 4f;
			var c = xy + new Vector2( MathF.Cos( mid ), MathF.Sin( mid ) ) * rMid;
			var z0 = zf + p.DoorHeight;
			if ( top <= z0 + 1f )
				continue;
			AddBox( ctx, floorRoot, $"ring_{room.Index}_lintel", new Vector3( c.x, c.y, (z0 + top) * 0.5f ), new Vector3( chord, wt, top - z0 ), mid.RadianToDegree() + 90f, wallColor );
		}

		BuildPillars( ctx, floorRoot, room, xy, r, r, zf, pillarColor );

		if ( room.Has( DungeonRoomFlags.Treasure ) && room.Doors.Count > 0 )
		{
			var door = room.Doors[0];
			var n = Normal( door.Side );
			var flat = xy - n * (r - p.PillarSize * 1.5f);
			var yaw = MathF.Atan2( n.y, n.x ).RadianToDegree();
			ctx.Result.ChestSlots.Add( new DungeonChestSlot( room.Index, new Vector3( flat.x, flat.y, zf ), yaw ) );
		}
	}

	/// <summary>Angle (radians) of the ring point at tangent offset <paramref name="u"/> from the axis of a side.</summary>
	static float AngleOnRing( Vector2 n, Vector2 t, float u, float radius )
	{
		var uc = Math.Clamp( u, -radius + 1f, radius - 1f );
		var v = MathF.Sqrt( MathF.Max( 0f, radius * radius - uc * uc ) );
		var pt = n * v + t * uc;
		return MathF.Atan2( pt.y, pt.x );
	}

	static float WrapRadians( float a )
	{
		while ( a > MathF.PI ) a -= 2f * MathF.PI;
		while ( a < -MathF.PI ) a += 2f * MathF.PI;
		return a;
	}

	// ---- shared dressing -------------------------------------------------------------------------

	static void BuildPillars( BuildContext ctx, GameObject parent, DungeonRoom room, Vector2 center, float hx, float hy, float zf, Color color )
	{
		if ( room.Pillars == DungeonPillarLayout.None )
			return;

		var p = ctx.P;
		var half = p.PillarSize * 0.5f;
		var points = new List<Vector2>();
		var hmin = MathF.Min( hx, hy );

		switch ( room.Pillars )
		{
			case DungeonPillarLayout.Center:
				points.Add( Vector2.Zero );
				break;
			case DungeonPillarLayout.Square:
				points.Add( new Vector2( hx * 0.5f, hy * 0.5f ) );
				points.Add( new Vector2( -hx * 0.5f, hy * 0.5f ) );
				points.Add( new Vector2( hx * 0.5f, -hy * 0.5f ) );
				points.Add( new Vector2( -hx * 0.5f, -hy * 0.5f ) );
				break;
			case DungeonPillarLayout.Corners:
			{
				var ix = hx - p.PillarSize * 2f;
				var iy = hy - p.PillarSize * 2f;
				points.Add( new Vector2( ix, iy ) );
				points.Add( new Vector2( -ix, iy ) );
				points.Add( new Vector2( ix, -iy ) );
				points.Add( new Vector2( -ix, -iy ) );
				break;
			}
			case DungeonPillarLayout.Row:
			{
				var alongX = hx >= hy ? (room.Index & 1) == 0 || hx > hy : false;
				var h = alongX ? hx : hy;
				for ( var i = -1; i <= 1; i++ )
					points.Add( alongX ? new Vector2( i * h * 0.5f, 0f ) : new Vector2( 0f, i * h * 0.5f ) );
				break;
			}
			case DungeonPillarLayout.Ring:
			{
				var radius = hmin * 0.55f;
				var count = hmin > 400f ? 12 : 8;
				for ( var i = 0; i < count; i++ )
				{
					var angle = i * 2f * MathF.PI / count;
					points.Add( new Vector2( MathF.Cos( angle ), MathF.Sin( angle ) ) * radius );
				}
				break;
			}
			case DungeonPillarLayout.Grid:
			{
				// A pillar every ~6 m, never within 2 m of a wall.
				var spacing = TerrainWorldUnits.MetersToEngine( 6f );
				var margin = p.PillarSize * 2f;
				var nx = Math.Max( 1, (int)MathF.Floor( (hx - margin) / spacing ) );
				var ny = Math.Max( 1, (int)MathF.Floor( (hy - margin) / spacing ) );
				for ( var ix = -nx; ix <= nx; ix++ )
				{
					if ( ix == 0 && nx > 1 ) continue; // leave a centre aisle in wide halls
					for ( var iy = -ny; iy <= ny; iy++ )
					{
						if ( iy == 0 && ny > 1 ) continue;
						points.Add( new Vector2( ix * spacing, iy * spacing ) );
					}
				}
				break;
			}
			case DungeonPillarLayout.Colonnade:
			{
				var alongX = hx >= hy;
				var longHalf = alongX ? hx : hy;
				var shortHalf = alongX ? hy : hx;
				var spacing = TerrainWorldUnits.MetersToEngine( 5f );
				var count = Math.Max( 2, (int)MathF.Floor( (longHalf - p.PillarSize * 2f) / spacing ) );
				var offset = shortHalf * 0.45f;
				for ( var i = -count; i <= count; i++ )
				{
					var along = i * spacing;
					points.Add( alongX ? new Vector2( along, offset ) : new Vector2( offset, along ) );
					points.Add( alongX ? new Vector2( along, -offset ) : new Vector2( -offset, along ) );
				}
				break;
			}
		}

		foreach ( var pt in points )
		{
			var c = center + pt;
			AddRect( ctx, parent, $"pillar_{room.Index}", c - new Vector2( half, half ), c + new Vector2( half, half ), zf, zf + p.ClearHeight, color );
		}
	}

	/// <summary>Footprint of the stair run (StairUp room) / the stairwell hole (Landing): hugging <c>StairSide</c>, running along its tangent from one corner.</summary>
	static (Vector2 min, Vector2 max) StairRect( BuildContext ctx, DungeonRoom room, Vector3 center, float h )
	{
		var p = ctx.P;
		var runLength = p.StepCount * p.StepRun;
		return FrameRect( new Vector2( center.x, center.y ), room.StairSide, -h, -h + runLength, h - p.HallWidth, h );
	}

	static void BuildStairs( BuildContext ctx, GameObject parent, DungeonRoom room, Vector3 center, float h, Color color )
	{
		var p = ctx.P;
		var stepColor = Color.Lerp( color, Color.White, 0.25f );
		var xy = new Vector2( center.x, center.y );
		var steps = p.StepCount;

		for ( var i = 1; i <= steps; i++ )
		{
			var u0 = -h + (i - 1) * p.StepRun;
			var u1 = -h + i * p.StepRun;
			var (min, max) = FrameRect( xy, room.StairSide, u0, u1, h - p.HallWidth, h );
			// Solid block from the floor to the tread, so the run reads as one staircase.
			AddRect( ctx, parent, $"step_{room.Index}_{i}", min, max, center.z, center.z + i * p.StepRise, stepColor );
		}
	}

	// ---- halls -----------------------------------------------------------------------------------

	/// <summary>
	/// Distance from a door cell's centre, along the side's outward normal, to the room's outer wall face there.
	/// Rect: half extent + wall, minus the cell's own offset from the room centre. Round: the ring point at the
	/// doorway's outer tangent edge, so the hall walls meet the ring rather than stopping short of the curve.
	/// </summary>
	static float FaceDistance( BuildContext ctx, DungeonRoom room, DungeonSide side, Vector2 cellCenter )
	{
		var p = ctx.P;
		var center = ctx.RoomCenter( room );
		var xy = new Vector2( center.x, center.y );
		var n = Normal( side );
		var t = Tangent( side );
		var along = Dot( cellCenter - xy, n );

		if ( room.Shape == DungeonRoomShape.Round )
		{
			var r = ctx.HalfX( room ) + p.WallThickness;
			var u = MathF.Abs( Dot( cellCenter - xy, t ) ) + p.HallWidth * 0.5f + p.WallThickness;
			return MathF.Sqrt( MathF.Max( 0f, r * r - u * u ) ) - along;
		}

		return HalfAlongNormal( ctx, room, side ) + p.WallThickness - along;
	}

	static void BuildHall( BuildContext ctx, DungeonHall hall )
	{
		var p = ctx.P;
		var a = ctx.Layout.Rooms[hall.RoomA];
		var b = ctx.Layout.Rooms[hall.RoomB];
		var cell = ctx.CellCenter( hall.CellX, hall.CellY );
		var step = DungeonLayout.Step[(int)hall.Side];
		var otherCell = ctx.CellCenter( hall.CellX + step.dx, hall.CellY + step.dy );
		var zf = ctx.RoomCenter( a ).z;
		var floorRoot = ctx.Result.FloorRoots[a.Floor];

		var wt = p.WallThickness;
		var v0 = FaceDistance( ctx, a, hall.Side, cell );
		var v1 = p.Pitch - FaceDistance( ctx, b, DungeonLayout.Opposite( hall.Side ), otherCell );
		if ( v1 - v0 < 1f )
			return;

		// Hall walls bury their ends in a round room's ring (rotated boxes, no coplanar faces) instead of stopping at a chord.
		var w0 = a.Shape == DungeonRoomShape.Round ? v0 - wt : v0;
		var w1 = b.Shape == DungeonRoomShape.Round ? v1 + wt : v1;

		var color = Color.Lerp( RoomColor( a, p.ColorByDepth ), RoomColor( b, p.ColorByDepth ), 0.5f );
		var wallColor = color * 0.7f;
		var halfW = p.HallWidth * 0.5f;
		var name = $"hall_{a.Index}_{b.Index}";

		var (fMin, fMax) = FrameRect( cell, hall.Side, -halfW - wt, halfW + wt, v0, v1 );
		AddRect( ctx, floorRoot, name + "_floor", fMin, fMax, zf - p.PlateThickness - HallPlateDrop, zf - HallPlateDrop, color );

		var (lMin, lMax) = FrameRect( cell, hall.Side, halfW, halfW + wt, w0, w1 );
		AddRect( ctx, floorRoot, name + "_wall", lMin, lMax, zf, zf + p.ClearHeight, wallColor );

		var (rMin, rMax) = FrameRect( cell, hall.Side, -halfW - wt, -halfW, w0, w1 );
		AddRect( ctx, floorRoot, name + "_wall", rMin, rMax, zf, zf + p.ClearHeight, wallColor );
	}

	// ---- primitives ------------------------------------------------------------------------------

	/// <summary>Outer rect minus an inner rect as up to four strips (left, right, bottom, top).</summary>
	static IEnumerable<(Vector2 min, Vector2 max)> SubtractRect( Vector2 outerMin, Vector2 outerMax, Vector2 holeMin, Vector2 holeMax )
	{
		holeMin = Vector2.Max( holeMin, outerMin );
		holeMax = Vector2.Min( holeMax, outerMax );

		if ( holeMin.x > outerMin.x )
			yield return (outerMin, new Vector2( holeMin.x, outerMax.y ));
		if ( holeMax.x < outerMax.x )
			yield return (new Vector2( holeMax.x, outerMin.y ), outerMax);
		if ( holeMin.y > outerMin.y )
			yield return (new Vector2( holeMin.x, outerMin.y ), new Vector2( holeMax.x, holeMin.y ));
		if ( holeMax.y < outerMax.y )
			yield return (new Vector2( holeMin.x, holeMax.y ), new Vector2( holeMax.x, outerMax.y ));
	}

	static void AddRect( BuildContext ctx, GameObject parent, string name, Vector2 min, Vector2 max, float z0, float z1, Color color )
	{
		var size = new Vector3( max.x - min.x, max.y - min.y, z1 - z0 );
		if ( size.x < 0.5f || size.y < 0.5f || size.z < 0.5f )
			return;

		var center = new Vector3( (min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, (z0 + z1) * 0.5f );
		AddBox( ctx, parent, name, center, size, 0f, color );
	}

	static void AddBox( BuildContext ctx, GameObject parent, string name, Vector3 localCenter, Vector3 size, float yawDegrees, Color color )
	{
		if ( size.x < 0.5f || size.y < 0.5f || size.z < 0.5f )
			return;

		var go = new GameObject( true, name );
		go.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.Parent = parent;
		go.LocalRotation = Rotation.FromYaw( yawDegrees );
		go.LocalPosition = localCenter;
		go.LocalScale = size / BoxModelSize;
		go.Tags.Add( "solid" );
		go.Tags.Add( "dungeon" );

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = ctx.Model;
		renderer.MaterialOverride = ctx.Material;
		renderer.Tint = color.WithAlpha( 1f );

		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = new Vector3( BoxModelSize, BoxModelSize, BoxModelSize );
		collider.Static = true;

		ctx.BoxCount++;
	}
}
