namespace Survival;

/// <summary>
/// The voxel ground (Mark 2026-10-26: "I want this game to be a digging game for sure" — true voxels, caves and
/// tunnels, not height deltas). One dense grid of "solidness" samples in metres: f &gt; 0 inside the ground, f &lt; 0 in
/// the air, |f| capped at <see cref="ClampMeters"/> so an edit only ever touches a thin shell around the surface.
/// The grid is seeded from a height function (the real world's sampler or procedural hills), so the undug ground is
/// exactly the heightfield; digging and filling are rounded-puck brushes on the samples (<see cref="ApplyBrush"/>), and the
/// chunks whose cells they touch are reported dirty for <see cref="VoxelMesher"/>.
/// <para>
/// Layout: <see cref="ChunkCells"/>³ cells per chunk, <see cref="ChunksX"/> × <see cref="ChunksY"/> × <see cref="ChunksZ"/>
/// chunks, sample (x, y, z) at <see cref="OriginMeters"/> + (x, y, z) × <see cref="CellMeters"/>. One global array:
/// an edit writes one sample once and every chunk reads its borders from the same array, so there are no seams.
/// </para>
/// </summary>
/// <summary>What a brush stroke does to the ground.</summary>
public enum VoxelBrushMode
{
	Dig = 0,
	Fill = 1,
	/// <summary>The rake, flat (Mark 2026-10-29, Enshrouded): the ground inside the circle becomes a horizontal plane at the surrounding surface height — a hole fills up to it, a lump is cut down to it.</summary>
	Flatten = 2,
	/// <summary>The rake, slanted: the same, but the plane follows the surrounding slope, capped at 45° (the steepest walkable ground).</summary>
	Slope = 3,
	/// <summary>The pickaxe (Mark 2026-11-02: "removes just a little bit at a time in a circle projected onto the surface of where you're looking. not a sphere!"): a shallow puck whose axis is the surface normal, so it chips a disc off a wall or a floor alike. Carries a normal and a depth (<see cref="VoxelEdit"/>).</summary>
	Chip = 4,
	/// <summary>The hoe: paint the ground inside the circle as the biome's grass (no sand, no rock; grass clutter where the biome has it).</summary>
	PaintGrass = 5,
	/// <summary>The hoe: paint the ground inside the circle bare - the soil tile, no grass clutter.</summary>
	PaintBare = 6,
	/// <summary>The hoe: paint the ground inside the circle tilled - dark wet soil, no grass clutter, seeds sow on it (Mark 2026-11-02: "Tilled ground should work the same as the bare ground just be dark brown").</summary>
	PaintTilled = 7,
	/// <summary>The hoe: raise the ground inside the circle by one thin layer of dirt (<see cref="VoxelEdit.D"/> m), costing dirt (Mark 2026-11-02: "a way to build upward a little bit").</summary>
	Raise = 8,
	/// <summary>The hoe: pave the circle with cobblestone - the rock tile, no grass clutter - for one stone per stroke (Mark 2026-11-02).</summary>
	PaintCobble = 9,
}

/// <summary>What the hoe painted on a ground column (per xy sample of a <see cref="VoxelField"/>). <see cref="None"/> = whatever the seed says.</summary>
public enum VoxelPaint : byte
{
	None = 0,
	Grass = 1,
	Bare = 2,
	Tilled = 3,
	Cobble = 4,
}

/// <summary>Where a rake stroke puts the ground: z = <see cref="OriginZ"/> + <see cref="Gradient"/> · (xy − <see cref="CenterXy"/>), metres.</summary>
public readonly record struct VoxelPlane( Vector2 CenterXy, float OriginZ, Vector2 Gradient )
{
	public float ZAt( float x, float y ) => OriginZ + (Gradient.x * (x - CenterXy.x)) + (Gradient.y * (y - CenterXy.y));
}

public sealed class VoxelField
{
	public const int ChunkCells = 32;
	public const float ClampMeters = 4f;

	public float CellMeters { get; }
	/// <summary>Cells per axis (any count — the last mesh chunk on an axis may be partial).</summary>
	public int CellsX { get; }
	public int CellsY { get; }
	public int CellsZ { get; }
	/// <summary>Mesh chunks per axis: <see cref="ChunkCells"/> cells each, the last one clipped to the cell count.</summary>
	public int ChunksX => (CellsX + ChunkCells - 1) / ChunkCells;
	public int ChunksY => (CellsY + ChunkCells - 1) / ChunkCells;
	public int ChunksZ => (CellsZ + ChunkCells - 1) / ChunkCells;
	public Vector3 OriginMeters { get; }
	public int SamplesX => CellsX + 1;
	public int SamplesY => CellsY + 1;
	public int SamplesZ => CellsZ + 1;
	public float ChunkMeters => ChunkCells * CellMeters;

	readonly float[] _f;
	/// <summary>The seed height per column (m) — how far below the original surface a dug face is (the mesher's dirt / rock weights).</summary>
	readonly float[] _groundZ;
	/// <summary>The hoe's paint per xy column, as coverage 0..1: how bare (the soil tile) and how grassed (the biome ground forced clean) the column is. The rim of a stroke lands fractional values, so the mesher blends a round edge instead of a stair-stepped one.</summary>
	readonly float[] _bare;
	readonly float[] _grass;
	readonly float[] _tilled;
	readonly float[] _cobble;

	public VoxelField( float cellMeters, int cellsX, int cellsY, int cellsZ, Vector3 originMeters )
	{
		CellMeters = cellMeters;
		CellsX = Math.Max( 1, cellsX );
		CellsY = Math.Max( 1, cellsY );
		CellsZ = Math.Max( 1, cellsZ );
		OriginMeters = originMeters;
		_f = new float[SamplesX * SamplesY * SamplesZ];
		_groundZ = new float[SamplesX * SamplesY];
		_bare = new float[SamplesX * SamplesY];
		_grass = new float[SamplesX * SamplesY];
		_tilled = new float[SamplesX * SamplesY];
		_cobble = new float[SamplesX * SamplesY];
	}

	public int Index( int x, int y, int z ) => (((z * SamplesY) + y) * SamplesX) + x;

	public float Get( int x, int y, int z ) => _f[Index( x, y, z )];

	public float GroundZ( int x, int y ) => _groundZ[(y * SamplesX) + x];

	/// <summary>The hoe's paint at a point in sample units (bilinear between the four columns round it, clamped to the field): bare, grass and tilled coverage 0..1.</summary>
	public void PaintAt( float sx, float sy, out float bare, out float grass, out float tilled, out float cobble )
	{
		var fx = Math.Clamp( sx, 0f, SamplesX - 1.001f );
		var fy = Math.Clamp( sy, 0f, SamplesY - 1.001f );
		var x = (int)fx;
		var y = (int)fy;
		var tx = fx - x;
		var ty = fy - y;
		var i00 = (y * SamplesX) + x;
		var i10 = i00 + 1;
		var i01 = i00 + SamplesX;
		var i11 = i01 + 1;
		bare = (((_bare[i00] * (1f - tx)) + (_bare[i10] * tx)) * (1f - ty)) + (((_bare[i01] * (1f - tx)) + (_bare[i11] * tx)) * ty);
		grass = (((_grass[i00] * (1f - tx)) + (_grass[i10] * tx)) * (1f - ty)) + (((_grass[i01] * (1f - tx)) + (_grass[i11] * tx)) * ty);
		tilled = (((_tilled[i00] * (1f - tx)) + (_tilled[i10] * tx)) * (1f - ty)) + (((_tilled[i01] * (1f - tx)) + (_tilled[i11] * tx)) * ty);
		cobble = (((_cobble[i00] * (1f - tx)) + (_cobble[i10] * tx)) * (1f - ty)) + (((_cobble[i01] * (1f - tx)) + (_cobble[i11] * tx)) * ty);
	}

	/// <summary>How much of a world point (m) is painted over (bare, tilled or cobble, 0..1) - the grass clutter asks this and stays off from 0.5 up.</summary>
	public float GroundCoverAt( float xMeters, float yMeters )
	{
		PaintAt( (xMeters - OriginMeters.x) / CellMeters, (yMeters - OriginMeters.y) / CellMeters, out var bare, out _, out var tilled, out var cobble );
		return MathF.Max( bare, MathF.Max( tilled, cobble ) );
	}

	/// <summary>Tilled coverage (0..1) at a world point (m) - farming sows where it is 0.5 or more.</summary>
	public float TilledAt( float xMeters, float yMeters )
	{
		PaintAt( (xMeters - OriginMeters.x) / CellMeters, (yMeters - OriginMeters.y) / CellMeters, out _, out _, out var tilled, out _ );
		return tilled;
	}

	/// <summary>Forget every paint stroke (reset).</summary>
	public void ClearPaint()
	{
		Array.Clear( _bare, 0, _bare.Length );
		Array.Clear( _grass, 0, _grass.Length );
		Array.Clear( _tilled, 0, _tilled.Length );
		Array.Clear( _cobble, 0, _cobble.Length );
	}

	/// <summary>
	/// The field's gradient at a point in sample units (central differences on the nearest samples) - points INTO the
	/// ground (f grows inward), so the surface normal is its negation. The mesher's vertex normals and the grass clutter's
	/// ground normal over a voxel column both come from here.
	/// </summary>
	public Vector3 GradientAt( Vector3 p )
	{
		float S( float x, float y, float z )
		{
			var xi = Math.Clamp( (int)MathF.Round( x ), 0, SamplesX - 1 );
			var yi = Math.Clamp( (int)MathF.Round( y ), 0, SamplesY - 1 );
			var zi = Math.Clamp( (int)MathF.Round( z ), 0, SamplesZ - 1 );
			return _f[Index( xi, yi, zi )];
		}
		var g = new Vector3(
			S( p.x + 1f, p.y, p.z ) - S( p.x - 1f, p.y, p.z ),
			S( p.x, p.y + 1f, p.z ) - S( p.x, p.y - 1f, p.z ),
			S( p.x, p.y, p.z + 1f ) - S( p.x, p.y, p.z - 1f ) );
		return g.LengthSquared > 1e-8f ? g : Vector3.Down;
	}

	/// <summary>The top surface of the nearest column between two heights (m) with its normal - for things that stand on the ground (grass clutter). False when the column has no surface there.</summary>
	public bool TrySurface( float xMeters, float yMeters, float zLo, float zHi, out float zMeters, out Vector3 normal )
	{
		normal = Vector3.Up;
		zMeters = SurfaceZ( xMeters, yMeters, zLo, zHi );
		if ( float.IsNaN( zMeters ) )
			return false;
		var p = (new Vector3( xMeters, yMeters, zMeters ) - OriginMeters) / CellMeters;
		normal = -GradientAt( p ).Normal;
		if ( normal.z <= 0f )
			normal = Vector3.Up;
		return true;
	}

	public Vector3 SamplePositionMeters( int x, int y, int z ) => OriginMeters + (new Vector3( x, y, z ) * CellMeters);

	/// <summary>Fill every sample from a height function (metres): f = ground − z, clamped. Worker-thread safe.</summary>
	public void SeedFromHeights( Func<float, float, float> groundZMeters )
	{
		for ( var y = 0; y < SamplesY; y++ )
		{
			for ( var x = 0; x < SamplesX; x++ )
			{
				var px = OriginMeters.x + (x * CellMeters);
				var py = OriginMeters.y + (y * CellMeters);
				var g = groundZMeters( px, py );
				_groundZ[(y * SamplesX) + x] = g;
				for ( var z = 0; z < SamplesZ; z++ )
				{
					var pz = OriginMeters.z + (z * CellMeters);
					_f[Index( x, y, z )] = Math.Clamp( g - pz, -ClampMeters, ClampMeters );
				}
			}
		}
	}

	/// <summary>
	/// The dig / fill brush: a rounded puck (Mark 2026-10-29: "it should do it in a circle but not a perfect sphere") — a
	/// circle of radius r on the ground with a flat floor, edges rounded by a cell, as a true signed distance in metres
	/// (<see cref="PuckDistance"/>): dig writes min( f, d ), fill writes max( f, −d ), so the field stays a distance-like
	/// function on both sides of the cut and the surface nets find every crossing on a clean curve. The puck is lopsided
	/// on purpose: h = r × flatness is the floor of a dig under its centre, but its top reaches 2 h up, so where the ground
	/// surface rises above the brush centre the well still cuts straight up through it — a symmetric puck (and the sphere
	/// and ellipsoid before it) stopped under a higher surface and left a thin overhanging lip round the rim that read as a
	/// black crack from above: those were the "tears". A fill is the mirror (flat top at h, reaching 2 h down) so a mound
	/// sits into the ground instead of hanging its edge over it. After every stroke, <see cref="RemoveFloating"/> sweeps away any
	/// piece of ground left hanging in the air (a pure function of the field, so the edit-log replay and every peer land on
	/// the same result). Returns the chunks whose meshes changed (a chunk reads one sample past its cells on every side, so a
	/// sample on a chunk border dirties both).
	/// </summary>
	/// <param name="flatness">Half height as a share of the radius: 1 = as tall as it is wide, 0.55 = the flat scoop a shovel takes (Mark 2026-10-27: a perfect sphere "will feel weird").</param>
	public HashSet<(int X, int Y, int Z)> ApplyBrush( Vector3 centerMeters, float radiusMeters, VoxelBrushMode mode, float flatness = 0.55f )
	{
		var dirty = new HashSet<(int, int, int)>();
		var r = Math.Max( radiusMeters, CellMeters * 0.5f );
		var flat = Math.Clamp( flatness, 0.2f, 1f );
		if ( mode is VoxelBrushMode.Flatten or VoxelBrushMode.Slope )
		{
			if ( TryPlanFlatten( centerMeters, r, mode == VoxelBrushMode.Slope, out var plane ) )
				ApplyPlane( centerMeters, r, plane, dirty );
			return dirty;
		}
		var dig = mode == VoxelBrushMode.Dig;
		var h = r * flat;
		var zLo = dig ? -h : -2f * h;
		var zHi = dig ? 2f * h : h;
		var round = Math.Min( CellMeters, Math.Min( r, h ) * 0.5f );
		var reachXy = r + ClampMeters;
		var reachZ = (2f * h) + ClampMeters;
		var min = (new Vector3( centerMeters.x - reachXy, centerMeters.y - reachXy, centerMeters.z - reachZ ) - OriginMeters) / CellMeters;
		var max = (new Vector3( centerMeters.x + reachXy, centerMeters.y + reachXy, centerMeters.z + reachZ ) - OriginMeters) / CellMeters;
		var x0 = Math.Max( 0, (int)MathF.Floor( min.x ) );
		var y0 = Math.Max( 0, (int)MathF.Floor( min.y ) );
		var z0 = Math.Max( 0, (int)MathF.Floor( min.z ) );
		var x1 = Math.Min( SamplesX - 1, (int)MathF.Ceiling( max.x ) );
		var y1 = Math.Min( SamplesY - 1, (int)MathF.Ceiling( max.y ) );
		var z1 = Math.Min( SamplesZ - 1, (int)MathF.Ceiling( max.z ) );
		for ( var z = z0; z <= z1; z++ )
		{
			for ( var y = y0; y <= y1; y++ )
			{
				for ( var x = x0; x <= x1; x++ )
				{
					var d = PuckDistance( SamplePositionMeters( x, y, z ) - centerMeters, r, zLo, zHi, round );
					if ( d >= ClampMeters )
						continue;
					var i = Index( x, y, z );
					var old = _f[i];
					var value = Math.Clamp( dig ? Math.Min( old, d ) : Math.Max( old, -d ), -ClampMeters, ClampMeters );
					if ( MathF.Abs( value - old ) < 1e-5f )
						continue;
					_f[i] = value;
					MarkDirty( x, y, z, dirty );
				}
			}
		}
		RemoveFloating( x0, y0, z0, x1, y1, z1, dirty );
		return dirty;
	}

	/// <summary>Signed distance (m) from an offset to a rounded puck: radius r in the ground plane, floor at zLo and top at zHi (relative to the centre), edges rounded by <paramref name="round"/>. Negative inside.</summary>
	static float PuckDistance( Vector3 d, float r, float zLo, float zHi, float round )
	{
		var qx = MathF.Sqrt( (d.x * d.x) + (d.y * d.y) ) - r + round;
		var qz = MathF.Abs( d.z - (0.5f * (zLo + zHi)) ) - (0.5f * (zHi - zLo)) + round;
		var ox = MathF.Max( qx, 0f );
		var oz = MathF.Max( qz, 0f );
		return MathF.Min( MathF.Max( qx, qz ), 0f ) + MathF.Sqrt( (ox * ox) + (oz * oz) ) - round;
	}

	/// <summary>
	/// The pickaxe stroke (Mark 2026-11-02: "removes just a little bit at a time in a circle projected onto the surface of
	/// where you're looking. not a sphere!"): the same rounded puck as a dig, but its axis is the surface normal where the
	/// pick lands, so the circle lies ON the surface - flat on a floor, upright on a cliff - and it cuts <paramref name="depthMeters"/>
	/// into the ground under that circle. Like the dig puck it reaches well past the surface on the air side (max( 2 depth, r ))
	/// so ground that rises above the disc inside the circle is cut too instead of leaving a lip. dig = min( f, d ).
	/// </summary>
	public HashSet<(int X, int Y, int Z)> ApplyChip( Vector3 centerMeters, Vector3 normal, float radiusMeters, float depthMeters )
	{
		var dirty = new HashSet<(int, int, int)>();
		var r = Math.Max( radiusMeters, CellMeters * 0.5f );
		var depth = Math.Clamp( depthMeters, CellMeters * 0.25f, 2f * r );
		var n = normal.LengthSquared > 1e-6f ? normal.Normal : Vector3.Up;
		var t = MathF.Abs( n.z ) < 0.9f ? Vector3.Cross( n, Vector3.Up ).Normal : Vector3.Cross( n, Vector3.Forward ).Normal;
		var b = Vector3.Cross( n, t ).Normal;
		var zLo = -depth;
		var zHi = MathF.Max( 2f * depth, r );
		var round = Math.Min( CellMeters, Math.Min( r, depth ) * 0.5f );
		var reach = MathF.Sqrt( (r * r) + (zHi * zHi) ) + ClampMeters;   // the tilted puck's bounding sphere
		var min = (centerMeters - new Vector3( reach, reach, reach ) - OriginMeters) / CellMeters;
		var max = (centerMeters + new Vector3( reach, reach, reach ) - OriginMeters) / CellMeters;
		var x0 = Math.Max( 0, (int)MathF.Floor( min.x ) );
		var y0 = Math.Max( 0, (int)MathF.Floor( min.y ) );
		var z0 = Math.Max( 0, (int)MathF.Floor( min.z ) );
		var x1 = Math.Min( SamplesX - 1, (int)MathF.Ceiling( max.x ) );
		var y1 = Math.Min( SamplesY - 1, (int)MathF.Ceiling( max.y ) );
		var z1 = Math.Min( SamplesZ - 1, (int)MathF.Ceiling( max.z ) );
		if ( x1 < x0 || y1 < y0 || z1 < z0 )
			return dirty;
		for ( var z = z0; z <= z1; z++ )
		{
			for ( var y = y0; y <= y1; y++ )
			{
				for ( var x = x0; x <= x1; x++ )
				{
					var d = SamplePositionMeters( x, y, z ) - centerMeters;
					var local = new Vector3( Vector3.Dot( d, t ), Vector3.Dot( d, b ), Vector3.Dot( d, n ) );
					var dist = PuckDistance( local, r, zLo, zHi, round );
					if ( dist >= ClampMeters )
						continue;
					var i = Index( x, y, z );
					var old = _f[i];
					var value = Math.Clamp( Math.Min( old, dist ), -ClampMeters, ClampMeters );
					if ( MathF.Abs( value - old ) < 1e-5f )
						continue;
					_f[i] = value;
					MarkDirty( x, y, z, dirty );
				}
			}
		}
		RemoveFloating( x0, y0, z0, x1, y1, z1, dirty );
		return dirty;
	}

	/// <summary>
	/// The hoe's paint stroke (Mark 2026-11-02: "should be like a circle for the most part but i want players to be able to make
	/// a perfect square patch on the ground too"): every column inside the circle of radius r - or, with <paramref name="square"/>,
	/// the axis-aligned square of side 2 r whose centre snaps to the cell grid so its edges land exactly on sample columns -
	/// takes the stroke's coverage. The rim of a circle gets fractional coverage over one cell (an anti-aliased edge the mesher
	/// blends to a round outline); a square's edge is hard. Bare raises the bare coverage and lowers grass; grass does the
	/// opposite, so the two strokes undo each other. The mesh chunks over the touched columns remesh; the samples are untouched.
	/// </summary>
	public HashSet<(int X, int Y, int Z)> ApplyPaint( Vector3 centerMeters, float radiusMeters, VoxelPaint paint, bool square )
	{
		var dirty = new HashSet<(int, int, int)>();
		var r = Math.Max( radiusMeters, CellMeters * 0.5f );
		var cx = centerMeters.x;
		var cy = centerMeters.y;
		if ( square )
		{
			cx = OriginMeters.x + (MathF.Round( (cx - OriginMeters.x) / CellMeters ) * CellMeters);
			cy = OriginMeters.y + (MathF.Round( (cy - OriginMeters.y) / CellMeters ) * CellMeters);
		}
		var reach = r + CellMeters;
		var x0 = Math.Max( 0, (int)MathF.Floor( (cx - reach - OriginMeters.x) / CellMeters ) );
		var y0 = Math.Max( 0, (int)MathF.Floor( (cy - reach - OriginMeters.y) / CellMeters ) );
		var x1 = Math.Min( SamplesX - 1, (int)MathF.Ceiling( (cx + reach - OriginMeters.x) / CellMeters ) );
		var y1 = Math.Min( SamplesY - 1, (int)MathF.Ceiling( (cy + reach - OriginMeters.y) / CellMeters ) );
		for ( var y = y0; y <= y1; y++ )
		{
			for ( var x = x0; x <= x1; x++ )
			{
				var dx = OriginMeters.x + (x * CellMeters) - cx;
				var dy = OriginMeters.y + (y * CellMeters) - cy;
				float w;
				if ( square )
					w = MathF.Abs( dx ) <= r + (CellMeters * 0.25f) && MathF.Abs( dy ) <= r + (CellMeters * 0.25f) ? 1f : 0f;
				else
					w = Math.Clamp( ((r - MathF.Sqrt( (dx * dx) + (dy * dy) )) / CellMeters) + 0.5f, 0f, 1f );
				if ( w <= 0f )
					continue;
				var i = (y * SamplesX) + x;
				// The painted kind rises to the stroke's coverage, the other two fall away under it
				var newBare = paint == VoxelPaint.Bare ? MathF.Max( _bare[i], w ) : MathF.Min( _bare[i], 1f - w );
				var newGrass = paint == VoxelPaint.Grass ? MathF.Max( _grass[i], w ) : MathF.Min( _grass[i], 1f - w );
				var newTilled = paint == VoxelPaint.Tilled ? MathF.Max( _tilled[i], w ) : MathF.Min( _tilled[i], 1f - w );
				var newCobble = paint == VoxelPaint.Cobble ? MathF.Max( _cobble[i], w ) : MathF.Min( _cobble[i], 1f - w );
				if ( MathF.Abs( newBare - _bare[i] ) < 1e-4f && MathF.Abs( newGrass - _grass[i] ) < 1e-4f && MathF.Abs( newTilled - _tilled[i] ) < 1e-4f && MathF.Abs( newCobble - _cobble[i] ) < 1e-4f )
					continue;
				_bare[i] = newBare;
				_grass[i] = newGrass;
				_tilled[i] = newTilled;
				_cobble[i] = newCobble;
				for ( var cz = 0; cz < ChunksZ; cz++ )
					MarkDirty( x, y, cz * ChunkCells, dirty );
			}
		}
		return dirty;
	}

	/// <summary>The rake's plan on this field alone (the prototype): see <see cref="VoxelRake.Plan"/>.</summary>
	public bool TryPlanFlatten( Vector3 centerMeters, float radiusMeters, bool slope, out VoxelPlane plane )
	{
		var r = Math.Max( radiusMeters, CellMeters * 0.5f );
		var window = VoxelRake.WindowMeters( r );
		return VoxelRake.Plan( ( x, y ) => SurfaceZ( x, y, centerMeters.z - window, centerMeters.z + window ), centerMeters, r, slope, out plane );
	}

	/// <summary>The top surface of the nearest column between two heights (m): the highest air-over-ground crossing, interpolated. NaN when the column has none there.</summary>
	public float SurfaceZ( float xMeters, float yMeters, float zLo, float zHi )
	{
		var x = (int)MathF.Round( (xMeters - OriginMeters.x) / CellMeters );
		var y = (int)MathF.Round( (yMeters - OriginMeters.y) / CellMeters );
		if ( x < 0 || y < 0 || x >= SamplesX || y >= SamplesY )
			return float.NaN;
		var kHi = Math.Min( SamplesZ - 2, (int)MathF.Ceiling( (zHi - OriginMeters.z) / CellMeters ) );
		var kLo = Math.Max( 0, (int)MathF.Floor( (zLo - OriginMeters.z) / CellMeters ) );
		for ( var k = kHi; k >= kLo; k-- )
		{
			var below = _f[Index( x, y, k )];
			var above = _f[Index( x, y, k + 1 )];
			if ( below <= 0f || above > 0f )
				continue;
			var t = below / (below - above);
			return OriginMeters.z + ((k + t) * CellMeters);
		}
		return float.NaN;
	}

	/// <summary>
	/// The rake stroke: inside the circle the ground becomes the planned plane. Above it, ground is cut away (up to 2 r + 1 m
	/// over the plane — a taller lump keeps its top); below it, air is filled down to the first ground already there, so a
	/// hole fills to its own floor and a cave under the plane keeps its roof instead of getting a slab bridged over it. Both
	/// write the plane's own signed height (plane z − sample z) with min / max against the old value, so the field stays
	/// distance-like and the rim meshes clean. Full strength inside the radius, fading over half a metre past it.
	/// </summary>
	public HashSet<(int X, int Y, int Z)> ApplyPlane( Vector3 centerMeters, float radiusMeters, VoxelPlane plane )
	{
		var dirty = new HashSet<(int, int, int)>();
		ApplyPlane( centerMeters, Math.Max( radiusMeters, CellMeters * 0.5f ), plane, dirty );
		return dirty;
	}

	void ApplyPlane( Vector3 centerMeters, float r, VoxelPlane plane, HashSet<(int, int, int)> dirty )
	{
		const float fadeMeters = 0.5f;
		var reachXy = r + fadeMeters;
		var up = (2f * r) + 1f;
		var down = (2f * r) + 1f;
		var tilt = plane.Gradient.Length * reachXy;
		var min = (new Vector3( centerMeters.x - reachXy, centerMeters.y - reachXy, plane.OriginZ - tilt - down ) - OriginMeters) / CellMeters;
		var max = (new Vector3( centerMeters.x + reachXy, centerMeters.y + reachXy, plane.OriginZ + tilt + up ) - OriginMeters) / CellMeters;
		var x0 = Math.Max( 0, (int)MathF.Floor( min.x ) );
		var y0 = Math.Max( 0, (int)MathF.Floor( min.y ) );
		var z0 = Math.Max( 0, (int)MathF.Floor( min.z ) );
		var x1 = Math.Min( SamplesX - 1, (int)MathF.Ceiling( max.x ) );
		var y1 = Math.Min( SamplesY - 1, (int)MathF.Ceiling( max.y ) );
		var z1 = Math.Min( SamplesZ - 1, (int)MathF.Ceiling( max.z ) );
		if ( x1 < x0 || y1 < y0 || z1 < z0 )
			return;
		for ( var y = y0; y <= y1; y++ )
		{
			for ( var x = x0; x <= x1; x++ )
			{
				var p = SamplePositionMeters( x, y, 0 );
				var dx = p.x - centerMeters.x;
				var dy = p.y - centerMeters.y;
				var dist = MathF.Sqrt( (dx * dx) + (dy * dy) );
				if ( dist > reachXy )
					continue;
				var w = 1f - SmoothStep( r, reachXy, dist );
				if ( w <= 0f )
					continue;
				LayColumn( x, y, plane.ZAt( p.x, p.y ), w, z0, z1, up, down, cutAbove: true, dirty );
			}
		}
		RemoveFloating( x0, y0, z0, x1, y1, z1, dirty );
	}

	/// <summary>
	/// The hoe's raise (Mark 2026-11-02: "add an option to elevate the terrain on the hoe ... only like a thin layer of it gets
	/// increased per action"): inside the circle of radius r (or the grid-snapped square of side 2 r) every column's surface
	/// comes up by <paramref name="heightMeters"/> - the layer follows the ground it sits on, so a slope stays a slope; a
	/// circle's rim fades the layer to nothing over half a metre (a low dome edge), a square's edge is a hard step. Air is
	/// filled down to the ground already there (never cut), the sample just above the new surface takes the exact signed
	/// height so the mesh lands on it.
	/// </summary>
	public HashSet<(int X, int Y, int Z)> ApplyRaise( Vector3 centerMeters, float radiusMeters, float heightMeters, bool square )
	{
		var dirty = new HashSet<(int, int, int)>();
		var r = Math.Max( radiusMeters, CellMeters * 0.5f );
		var h = Math.Clamp( heightMeters, 0.05f, 2f );
		const float fadeMeters = 0.5f;
		var cx = centerMeters.x;
		var cy = centerMeters.y;
		if ( square )
		{
			cx = OriginMeters.x + (MathF.Round( (cx - OriginMeters.x) / CellMeters ) * CellMeters);
			cy = OriginMeters.y + (MathF.Round( (cy - OriginMeters.y) / CellMeters ) * CellMeters);
		}
		var reachXy = r + fadeMeters;
		var window = VoxelRake.WindowMeters( r );
		var x0 = Math.Max( 0, (int)MathF.Floor( (cx - reachXy - OriginMeters.x) / CellMeters ) );
		var y0 = Math.Max( 0, (int)MathF.Floor( (cy - reachXy - OriginMeters.y) / CellMeters ) );
		var x1 = Math.Min( SamplesX - 1, (int)MathF.Ceiling( (cx + reachXy - OriginMeters.x) / CellMeters ) );
		var y1 = Math.Min( SamplesY - 1, (int)MathF.Ceiling( (cy + reachXy - OriginMeters.y) / CellMeters ) );
		var z0 = Math.Max( 0, (int)MathF.Floor( (centerMeters.z - window - h - 1f - OriginMeters.z) / CellMeters ) );
		var z1 = Math.Min( SamplesZ - 1, (int)MathF.Ceiling( (centerMeters.z + window + h + 1f - OriginMeters.z) / CellMeters ) );
		if ( x1 < x0 || y1 < y0 || z1 < z0 )
			return dirty;
		for ( var y = y0; y <= y1; y++ )
		{
			for ( var x = x0; x <= x1; x++ )
			{
				var px = OriginMeters.x + (x * CellMeters);
				var py = OriginMeters.y + (y * CellMeters);
				var dx = px - cx;
				var dy = py - cy;
				float w;
				if ( square )
					w = MathF.Abs( dx ) <= r + (CellMeters * 0.25f) && MathF.Abs( dy ) <= r + (CellMeters * 0.25f) ? 1f : 0f;
				else
					w = 1f - SmoothStep( r, reachXy, MathF.Sqrt( (dx * dx) + (dy * dy) ) );
				if ( w <= 0f )
					continue;
				var surface = SurfaceZ( px, py, centerMeters.z - window, centerMeters.z + window );
				if ( float.IsNaN( surface ) )
					continue;
				LayColumn( x, y, surface + (h * w), 1f, z0, z1, h + 1f, h + 1f, cutAbove: false, dirty );
			}
		}
		RemoveFloating( x0, y0, z0, x1, y1, z1, dirty );
		return dirty;
	}

	/// <summary>
	/// One column of a rake / raise stroke: the ground surface becomes height <paramref name="pz"/>. The two samples bracketing
	/// it take its exact signed height (so the mesh crossing lands ON it - min / max alone left a dish); with
	/// <paramref name="cutAbove"/> everything up to <paramref name="up"/> above it becomes air, otherwise nothing above is touched
	/// but that bracketing sample; below it air is filled down to the first sample that was already ground, at most
	/// <paramref name="down"/> deep, so a hole fills to its own floor and a cave underneath keeps its roof.
	/// </summary>
	void LayColumn( int x, int y, float pz, float w, int z0, int z1, float up, float down, bool cutAbove, HashSet<(int, int, int)> dirty )
	{
		var kPlane = (pz - OriginMeters.z) / CellMeters;
		var kAbove = (int)MathF.Ceiling( kPlane );
		var kBelow = kAbove - 1;
		if ( cutAbove )
		{
			var kTop = Math.Min( z1, (int)MathF.Ceiling( kPlane + (up / CellMeters) ) );
			for ( var k = Math.Max( z0, kAbove ); k <= kTop; k++ )
			{
				var target = pz - (OriginMeters.z + (k * CellMeters));
				Write( x, y, k, k == kAbove ? target : Math.Min( _f[Index( x, y, k )], target ), w, dirty );
			}
		}
		else if ( kAbove >= z0 && kAbove <= z1 )
			Write( x, y, kAbove, pz - (OriginMeters.z + (kAbove * CellMeters)), w, dirty );
		var kBottom = Math.Max( z0, (int)MathF.Floor( kPlane - (down / CellMeters) ) );
		for ( var k = Math.Min( z1, kBelow ); k >= kBottom; k-- )
		{
			var old = _f[Index( x, y, k )];
			var target = pz - (OriginMeters.z + (k * CellMeters));
			Write( x, y, k, k == kBelow ? target : Math.Max( old, target ), w, dirty );
			if ( old > 0f )
				break;
		}
	}

	void Write( int x, int y, int z, float target, float w, HashSet<(int, int, int)> dirty )
	{
		var i = Index( x, y, z );
		var old = _f[i];
		var value = Math.Clamp( old + ((target - old) * w), -ClampMeters, ClampMeters );
		if ( MathF.Abs( value - old ) < 1e-5f )
			return;
		_f[i] = value;
		MarkDirty( x, y, z, dirty );
	}

	static float SmoothStep( float a, float b, float x )
	{
		var t = Math.Clamp( (x - a) / Math.Max( 1e-6f, b - a ), 0f, 1f );
		return t * t * (3f - (2f * t));
	}

	/// <summary>Samples this many cells (8 m) around a stroke are checked for pieces of ground left floating. Mark 2026-10-28: at 8 cells a
	/// ledge cut loose by a second stroke next to the first still reached the sweep box and survived.</summary>
	const int FloatingSweepCells = 16;

	/// <summary>
	/// Flood-fill the ground inside the stroke's box grown by <see cref="FloatingSweepCells"/>: every connected piece that never
	/// reaches the box's boundary is attached to nothing and becomes air. Six-connected on the samples — ground that only
	/// meets at a corner or an edge counts as hanging too. Runs after every mode (dig, fill, flatten, slope).
	/// </summary>
	void RemoveFloating( int x0, int y0, int z0, int x1, int y1, int z1, HashSet<(int, int, int)> dirty )
	{
		x0 = Math.Max( 0, x0 - FloatingSweepCells );
		y0 = Math.Max( 0, y0 - FloatingSweepCells );
		z0 = Math.Max( 0, z0 - FloatingSweepCells );
		x1 = Math.Min( SamplesX - 1, x1 + FloatingSweepCells );
		y1 = Math.Min( SamplesY - 1, y1 + FloatingSweepCells );
		z1 = Math.Min( SamplesZ - 1, z1 + FloatingSweepCells );
		var nx = x1 - x0 + 1;
		var ny = y1 - y0 + 1;
		var nz = z1 - z0 + 1;
		var label = new int[nx * ny * nz];   // 0 unvisited, else the component id
		var stack = new Stack<int>();
		var component = new List<int>();
		var next = 1;
		for ( var lz = 0; lz < nz; lz++ )
		{
			for ( var ly = 0; ly < ny; ly++ )
			{
				for ( var lx = 0; lx < nx; lx++ )
				{
					var li = ((lz * ny) + ly) * nx + lx;
					if ( label[li] != 0 || _f[Index( x0 + lx, y0 + ly, z0 + lz )] <= 0f )
						continue;
					var id = next++;
					var touchesEdge = false;
					component.Clear();
					stack.Push( li );
					label[li] = id;
					while ( stack.Count > 0 )
					{
						var c = stack.Pop();
						component.Add( c );
						var cx = c % nx;
						var cy = (c / nx) % ny;
						var cz = c / (nx * ny);
						if ( cx == 0 || cy == 0 || cz == 0 || cx == nx - 1 || cy == ny - 1 || cz == nz - 1 )
							touchesEdge = true;
						void Visit( int ax, int ay, int az )
						{
							if ( ax < 0 || ay < 0 || az < 0 || ax >= nx || ay >= ny || az >= nz )
								return;
							var ai = ((az * ny) + ay) * nx + ax;
							if ( label[ai] != 0 || _f[Index( x0 + ax, y0 + ay, z0 + az )] <= 0f )
								return;
							label[ai] = id;
							stack.Push( ai );
						}
						Visit( cx - 1, cy, cz ); Visit( cx + 1, cy, cz );
						Visit( cx, cy - 1, cz ); Visit( cx, cy + 1, cz );
						Visit( cx, cy, cz - 1 ); Visit( cx, cy, cz + 1 );
					}
					if ( touchesEdge )
						continue;
					// Floating: connected to nothing outside the box — gone
					foreach ( var c in component )
					{
						var cx = x0 + (c % nx);
						var cy = y0 + ((c / nx) % ny);
						var cz = z0 + (c / (nx * ny));
						_f[Index( cx, cy, cz )] = -ClampMeters;
						MarkDirty( cx, cy, cz, dirty );
					}
				}
			}
		}
	}

	/// <summary>Every chunk whose mesh reads sample (x, y, z): its own plus the neighbour on each lower side it borders.</summary>
	void MarkDirty( int x, int y, int z, HashSet<(int, int, int)> dirty )
	{
		var cx = Math.Min( x / ChunkCells, ChunksX - 1 );
		var cy = Math.Min( y / ChunkCells, ChunksY - 1 );
		var cz = Math.Min( z / ChunkCells, ChunksZ - 1 );
		var bx = x % ChunkCells == 0 && cx > 0;
		var by = y % ChunkCells == 0 && cy > 0;
		var bz = z % ChunkCells == 0 && cz > 0;
		for ( var dx = 0; dx <= (bx ? 1 : 0); dx++ )
		{
			for ( var dy = 0; dy <= (by ? 1 : 0); dy++ )
			{
				for ( var dz = 0; dz <= (bz ? 1 : 0); dz++ )
					dirty.Add( (cx - dx, cy - dy, cz - dz) );
			}
		}
	}

	/// <summary>Is a world point (m) inside the ground? Trilinear on the samples; false outside the field.</summary>
	public bool IsSolidAt( Vector3 worldMeters )
	{
		var p = (worldMeters - OriginMeters) / CellMeters;
		if ( p.x < 0f || p.y < 0f || p.z < 0f || p.x >= SamplesX - 1 || p.y >= SamplesY - 1 || p.z >= SamplesZ - 1 )
			return false;
		var x = (int)p.x;
		var y = (int)p.y;
		var z = (int)p.z;
		var t = p - new Vector3( x, y, z );
		float L( int dx, int dy, int dz ) => Get( x + dx, y + dy, z + dz );
		var c00 = L( 0, 0, 0 ) + ((L( 1, 0, 0 ) - L( 0, 0, 0 )) * t.x);
		var c10 = L( 0, 1, 0 ) + ((L( 1, 1, 0 ) - L( 0, 1, 0 )) * t.x);
		var c01 = L( 0, 0, 1 ) + ((L( 1, 0, 1 ) - L( 0, 0, 1 )) * t.x);
		var c11 = L( 0, 1, 1 ) + ((L( 1, 1, 1 ) - L( 0, 1, 1 )) * t.x);
		var c0 = c00 + ((c10 - c00) * t.y);
		var c1 = c01 + ((c11 - c01) * t.y);
		return c0 + ((c1 - c0) * t.z) > 0f;
	}

	/// <summary>World bounds of the field (m).</summary>
	public BBox BoundsMeters => new( OriginMeters, OriginMeters + new Vector3( CellsX, CellsY, CellsZ ) * CellMeters );
}

/// <summary>
/// Where a flatten / slope stroke will put the ground — the rake's plan, shared by the preview ring and the stroke so the
/// ring shows exactly what a click does, and by the prototype field and the live world (which plans over voxel columns and
/// heightfield chunks alike). Mark 2026-10-29 (Enshrouded's rake): "displays a circle on the terrain where it will be
/// adjusted and then when you click it just fills the hole into the same height as the surrounding terrain or flattens out
/// a lump to the surrounding terrain". The surroundings are the surface heights on a ring just outside the brush (two
/// radii, 24 directions each, from <paramref name="surfaceZ"/>: NaN = no surface there). Flatten: the plane is horizontal at
/// their median (robust: a hole or a lump inside the brush never pulls it). Slope: the least-squares plane through them,
/// its grade capped at <see cref="MaxSlopeGrade"/> (45°, the steepest walkable ground) so a hole on a hillside fills to the
/// hillside instead of a shelf. False when fewer than six ring columns have a surface (the brush is hanging over a cliff or
/// outside the ground) — then the stroke does nothing.
/// </summary>
public static class VoxelRake
{
	/// <summary>The steepest plane the slope rake lays: rise over run 1 = 45°, the steepest walkable ground (Mark 2026-10-29).</summary>
	public const float MaxSlopeGrade = 1f;

	/// <summary>How far above / below the aim a ring column is searched for its surface.</summary>
	public static float WindowMeters( float r ) => (2f * r) + 2f;

	public static bool Plan( Func<float, float, float> surfaceZ, Vector3 centerMeters, float r, bool slope, out VoxelPlane plane )
	{
		const int directions = 24;
		var xs = new List<float>( directions * 2 );
		var ys = new List<float>( directions * 2 );
		var zs = new List<float>( directions * 2 );
		for ( var ring = 0; ring < 2; ring++ )
		{
			var ringR = r + (ring == 0 ? 0.25f : 0.75f);
			for ( var k = 0; k < directions; k++ )
			{
				var a = k * (MathF.PI * 2f / directions);
				var px = centerMeters.x + (MathF.Cos( a ) * ringR);
				var py = centerMeters.y + (MathF.Sin( a ) * ringR);
				var z = surfaceZ( px, py );
				if ( float.IsNaN( z ) )
					continue;
				xs.Add( px - centerMeters.x );
				ys.Add( py - centerMeters.y );
				zs.Add( z );
			}
		}
		plane = default;
		if ( zs.Count < 6 )
			return false;
		var centerXy = new Vector2( centerMeters.x, centerMeters.y );
		if ( !slope )
		{
			var sorted = zs.ToArray();
			Array.Sort( sorted );
			var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : 0.5f * (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]);
			plane = new VoxelPlane( centerXy, median, Vector2.Zero );
			return true;
		}
		// Least squares z = a + b x + c y over the ring points (normal equations, 3 × 3)
		double n = zs.Count, sx = 0, sy = 0, sz = 0, sxx = 0, sxy = 0, syy = 0, sxz = 0, syz = 0;
		for ( var i = 0; i < zs.Count; i++ )
		{
			double x = xs[i], y = ys[i], z = zs[i];
			sx += x; sy += y; sz += z; sxx += x * x; sxy += x * y; syy += y * y; sxz += x * z; syz += y * z;
		}
		var det = (n * ((sxx * syy) - (sxy * sxy))) - (sx * ((sx * syy) - (sxy * sy))) + (sy * ((sx * sxy) - (sxx * sy)));
		if ( Math.Abs( det ) < 1e-9 )
			return false;
		var a0 = ((sz * ((sxx * syy) - (sxy * sxy))) - (sx * ((sxz * syy) - (sxy * syz))) + (sy * ((sxz * sxy) - (sxx * syz)))) / det;
		var b0 = ((n * ((sxz * syy) - (sxy * syz))) - (sz * ((sx * syy) - (sxy * sy))) + (sy * ((sx * syz) - (sxz * sy)))) / det;
		var c0 = ((n * ((sxx * syz) - (sxz * sxy))) - (sx * ((sx * syz) - (sxz * sy))) + (sz * ((sx * sxy) - (sxx * sy)))) / det;
		var grade = new Vector2( (float)b0, (float)c0 );
		if ( grade.Length > MaxSlopeGrade )
			grade = grade.Normal * MaxSlopeGrade;
		plane = new VoxelPlane( centerXy, (float)a0, grade );
		return true;
	}
}
