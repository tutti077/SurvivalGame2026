namespace Survival;

/// <summary>What the ground at a world xy (m) is made of, for the terrain splat: weights = Clover / Redwood / Amber shares + Mountain, splat = (sand, wet).</summary>
public delegate void VoxelSurfaceStyle( float xMeters, float yMeters, out Color weights, out Vector2 splat );

/// <summary>
/// Naive surface nets over one chunk of a <see cref="VoxelField"/> (no marching-cubes tables, smooth, watertight):
/// every cell whose eight corners straddle the surface gets one vertex at the mean of its edge crossings, and every
/// grid edge that crosses the surface becomes a quad between the four cells around it. A chunk meshes the ring of
/// cells one past its own on the low sides too, so the quads on its border find their vertices without asking the
/// neighbour; those border vertices are duplicated, never shared, and the result has no cracks. Pure data in, pure
/// data out — runs on a worker thread; the Mesh / Model are made on the main thread by <see cref="VoxelChunkSet"/>.
/// <para>
/// Vertex extras for materials/terrain/terrain_ground.vmat (pixel_lit F_TERRAIN_SPLAT): Colors = land shares (from the
/// <see cref="VoxelSurfaceStyle"/>, Clover when there is none) with A = mountain / rock (deeper than ~4–8 m under the
/// original surface), Extras = (sand, wet): sand on dug walls for the first metre and a half under the original surface,
/// or the style's beach / bed sand — so a pit shows soil on its sides and rock at the bottom.
/// </para>
/// <para>
/// <see cref="Clip"/>: a live-world column meshes only its own square. Its field carries a margin of cells past the square
/// (the same ground its neighbours hold), and every vertex of a cell outside the square is moved onto the square's edge —
/// along one axis by interpolating toward the cell inside, at a corner to the corner itself. Two neighbouring columns
/// hold identical samples in the shared margin, so both land the same edge vertices and the seam is watertight; quads
/// whose four cells all lie outside on the same side are dropped.
/// </para>
/// </summary>
public static class VoxelMesher
{
	public sealed class MeshData
	{
		public Vector3[] Positions;   // engine units, chunk-local
		public Vector3[] Normals;
		public Color[] Colors;        // splat weights, a = mountain / rock
		public Vector2[] Extras;      // x = sand, y = wet
		public float[] Tilled;        // the hoe's tilled coverage: TexCoord1.x, the splat's darkening channel (brown over sand)
		public int[] Indices;
		public int VertexCount => Positions.Length;
	}

	/// <summary>The cells (field indices) a mesh may cover: [MinX, MaxX) × [MinY, MaxY). <see cref="None"/> = the whole field.</summary>
	public readonly record struct Clip( int MinX, int MinY, int MaxX, int MaxY )
	{
		public static readonly Clip None = new( int.MinValue, int.MinValue, int.MaxValue, int.MaxValue );
		public bool IsNone => MinX == int.MinValue;
	}

	static readonly int[,] EdgeCorners =
	{
		{ 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 },   // x edges
		{ 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 },   // y edges
		{ 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 },   // z edges
	};

	const byte OutXLo = 1, OutXHi = 2, OutYLo = 4, OutYHi = 8;

	public static MeshData Build( VoxelField field, int cx, int cy, int cz, VoxelSurfaceStyle style = null, Clip clip = default )
	{
		if ( clip == default )
			clip = Clip.None;
		const int n = VoxelField.ChunkCells;
		const int span = n + 1;   // cells -1 .. n-1
		var cell = field.CellMeters;
		var chunkOrigin = field.OriginMeters + (new Vector3( cx, cy, cz ) * field.ChunkMeters);
		var gx0 = cx * n;
		var gy0 = cy * n;
		var gz0 = cz * n;
		var cellsX = field.CellsX;
		var cellsY = field.CellsY;
		var cellsZ = field.CellsZ;

		var vmap = new int[span * span * span];
		Array.Fill( vmap, -1 );
		var outside = new byte[span * span * span];
		var raw = new List<Vector3>();      // sample units, before clipping
		var normals = new List<Vector3>();
		var colors = new List<Color>();
		var extras = new List<Vector2>();
		var tilledOut = new List<float>();
		var corner = new float[8];
		var cornerPos = new Vector3[8];

		for ( var k = -1; k < n; k++ )
		{
			var gz = gz0 + k;
			if ( gz < 0 || gz >= cellsZ )
				continue;
			for ( var j = -1; j < n; j++ )
			{
				var gy = gy0 + j;
				if ( gy < 0 || gy >= cellsY )
					continue;
				for ( var i = -1; i < n; i++ )
				{
					var gxi = gx0 + i;
					if ( gxi < 0 || gxi >= cellsX )
						continue;
					var solid = 0;
					for ( var c = 0; c < 8; c++ )
					{
						var dx = c & 1;
						var dy = (c >> 1) & 1;
						var dz = (c >> 2) & 1;
						corner[c] = field.Get( gxi + dx, gy + dy, gz + dz );
						cornerPos[c] = new Vector3( gxi + dx, gy + dy, gz + dz );
						if ( corner[c] > 0f )
							solid++;
					}
					if ( solid == 0 || solid == 8 )
						continue;
					var sum = Vector3.Zero;
					var count = 0;
					for ( var e = 0; e < 12; e++ )
					{
						var a = EdgeCorners[e, 0];
						var b = EdgeCorners[e, 1];
						var fa = corner[a];
						var fb = corner[b];
						if ( (fa > 0f) == (fb > 0f) )
							continue;
						var t = fa / (fa - fb);
						sum += cornerPos[a] + ((cornerPos[b] - cornerPos[a]) * t);
						count++;
					}
					var p = sum / Math.Max( count, 1 );   // in sample units
					var normal = -field.GradientAt( p ).Normal;
					var worldMeters = field.OriginMeters + (p * cell);
					var under = field.GroundZ( Math.Clamp( (int)MathF.Round( p.x ), 0, field.SamplesX - 1 ), Math.Clamp( (int)MathF.Round( p.y ), 0, field.SamplesY - 1 ) ) - worldMeters.z;
					var sandDug = Math.Clamp( under / 1.5f, 0f, 1f );
					var rock = Math.Clamp( (under - 4f) / 4f, 0f, 1f );
					var weights = new Color( 1f, 0f, 0f, 0f );
					var splat = Vector2.Zero;
					style?.Invoke( worldMeters.x, worldMeters.y, out weights, out splat );
					// The hoe's paint blends over the seed style and the depth rule by its coverage at this vertex (bilinear between
					// columns, so a circle's rim reads round): grass = the biome ground, clean; bare = the soil tile
					field.PaintAt( p.x, p.y, out var bare, out var grass, out var tilled, out var cobble );
					var cover = MathF.Max( bare, tilled );   // soil of either kind; tilled is the same soil darkened to earth brown
					var sandHere = MathF.Max( splat.x, sandDug );
					var rockHere = MathF.Max( weights.a, rock );
					sandHere = ((sandHere * (1f - grass) * (1f - cover)) + cover) * (1f - cobble);
					rockHere = (rockHere * (1f - grass) * (1f - cover) * (1f - cobble)) + cobble;   // cobblestone = the rock tile
					splat = new Vector2( sandHere, splat.y );
					weights = new Color( weights.r, weights.g, weights.b, rockHere );
					sandDug = 0f;
					rock = 0f;
					var li = (((k + 1) * span) + (j + 1)) * span + (i + 1);
					vmap[li] = raw.Count;
					outside[li] = (byte)((gxi < clip.MinX ? OutXLo : 0) | (gxi >= clip.MaxX ? OutXHi : 0) | (gy < clip.MinY ? OutYLo : 0) | (gy >= clip.MaxY ? OutYHi : 0));
					raw.Add( p );
					normals.Add( normal );
					colors.Add( new Color( weights.r, weights.g, weights.b, Math.Max( weights.a, rock ) ) );
					extras.Add( new Vector2( Math.Max( splat.x, sandDug ), splat.y ) );
					tilledOut.Add( tilled );
				}
			}
		}

		// Clip: vertices of cells outside the square move onto its edge (see the class summary)
		var positions = new Vector3[raw.Count];
		for ( var k = -1; k < n; k++ )
		{
			for ( var j = -1; j < n; j++ )
			{
				for ( var i = -1; i < n; i++ )
				{
					var li = (((k + 1) * span) + (j + 1)) * span + (i + 1);
					var v = vmap[li];
					if ( v < 0 )
						continue;
					var p = raw[v];
					var o = outside[li];
					if ( o != 0 )
					{
						var planeX = (o & OutXLo) != 0 ? clip.MinX : (o & OutXHi) != 0 ? clip.MaxX : int.MinValue;
						var planeY = (o & OutYLo) != 0 ? clip.MinY : (o & OutYHi) != 0 ? clip.MaxY : int.MinValue;
						if ( planeX != int.MinValue && planeY != int.MinValue )
							p = new Vector3( planeX, planeY, p.z );
						else if ( planeX != int.MinValue )
							p = ClipToward( p, planeX, 0, vmap, outside, raw, span, i + ((o & OutXLo) != 0 ? 1 : -1), j, k );
						else
							p = ClipToward( p, planeY, 1, vmap, outside, raw, span, i, j + ((o & OutYLo) != 0 ? 1 : -1), k );
					}
					var worldMeters = field.OriginMeters + (p * cell);
					positions[v] = (worldMeters - chunkOrigin) * TerrainWorldUnits.UnitsPerMeter;
				}
			}
		}

		var indices = new List<int>();
		if ( raw.Count > 0 )
		{
			for ( var k = 0; k < n; k++ )
			{
				var gz = gz0 + k;
				if ( gz >= field.SamplesZ )
					break;
				for ( var j = 0; j < n; j++ )
				{
					var gy = gy0 + j;
					if ( gy >= field.SamplesY )
						break;
					for ( var i = 0; i < n; i++ )
					{
						var gxi = gx0 + i;
						if ( gxi >= field.SamplesX )
							break;
						var f0 = field.Get( gxi, gy, gz );
						var s0 = f0 > 0f;
						// x edge: cells (i,j,k) (i,j,k-1) (i,j-1,k-1) (i,j-1,k) around it
						if ( gxi + 1 < field.SamplesX && (field.Get( gxi + 1, gy, gz ) > 0f) != s0 )
							Quad( vmap, outside, span, indices, s0, i, j, k, i, j, k - 1, i, j - 1, k - 1, i, j - 1, k );
						// y edge: cells (i,j,k) (i-1,j,k) (i-1,j,k-1) (i,j,k-1)
						if ( gy + 1 < field.SamplesY && (field.Get( gxi, gy + 1, gz ) > 0f) != s0 )
							Quad( vmap, outside, span, indices, s0, i, j, k, i - 1, j, k, i - 1, j, k - 1, i, j, k - 1 );
						// z edge: cells (i,j,k) (i,j-1,k) (i-1,j-1,k) (i-1,j,k)
						if ( gz + 1 < field.SamplesZ && (field.Get( gxi, gy, gz + 1 ) > 0f) != s0 )
							Quad( vmap, outside, span, indices, s0, i, j, k, i, j - 1, k, i - 1, j - 1, k, i - 1, j, k );
					}
				}
			}
		}

		return new MeshData
		{
			Positions = positions,
			Normals = normals.ToArray(),
			Colors = colors.ToArray(),
			Extras = extras.ToArray(),
			Tilled = tilledOut.ToArray(),
			Indices = indices.ToArray(),
		};
	}

	/// <summary>Move an outside vertex onto the clip plane along one axis, interpolating toward the inside neighbour's vertex when it has one.</summary>
	static Vector3 ClipToward( Vector3 p, int plane, int axis, int[] vmap, byte[] outside, List<Vector3> raw, int span, int ni, int nj, int nk )
	{
		var pa = axis == 0 ? p.x : p.y;
		var nv = ni >= -1 && nj >= -1 && nk >= -1 && ni < span - 1 && nj < span - 1 && nk < span - 1 ? vmap[(((nk + 1) * span) + (nj + 1)) * span + (ni + 1)] : -1;
		if ( nv >= 0 && outside[(((nk + 1) * span) + (nj + 1)) * span + (ni + 1)] == 0 )
		{
			var q = raw[nv];
			var qa = axis == 0 ? q.x : q.y;
			if ( MathF.Abs( qa - pa ) > 1e-4f )
			{
				var t = Math.Clamp( (plane - pa) / (qa - pa), 0f, 1f );
				var r = p + ((q - p) * t);
				return axis == 0 ? r.WithX( plane ) : r.WithY( plane );
			}
		}
		return axis == 0 ? p.WithX( plane ) : p.WithY( plane );
	}

	static int VertexAt( int[] vmap, int span, int i, int j, int k )
	{
		if ( i < -1 || j < -1 || k < -1 || i >= span - 1 || j >= span - 1 || k >= span - 1 )
			return -1;
		return vmap[(((k + 1) * span) + (j + 1)) * span + (i + 1)];
	}

	static byte OutsideAt( byte[] outside, int span, int i, int j, int k ) => outside[(((k + 1) * span) + (j + 1)) * span + (i + 1)];

	/// <summary>One quad between four cells listed clockwise about the +axis; flipped when the base sample is air. Dropped when every cell is outside the clip on the same side.</summary>
	static void Quad( int[] vmap, byte[] outside, int span, List<int> indices, bool solidAtBase,
		int i0, int j0, int k0, int i1, int j1, int k1, int i2, int j2, int k2, int i3, int j3, int k3 )
	{
		var a = VertexAt( vmap, span, i0, j0, k0 );
		var b = VertexAt( vmap, span, i1, j1, k1 );
		var c = VertexAt( vmap, span, i2, j2, k2 );
		var d = VertexAt( vmap, span, i3, j3, k3 );
		if ( a < 0 || b < 0 || c < 0 || d < 0 )
			return;
		if ( (OutsideAt( outside, span, i0, j0, k0 ) & OutsideAt( outside, span, i1, j1, k1 ) & OutsideAt( outside, span, i2, j2, k2 ) & OutsideAt( outside, span, i3, j3, k3 )) != 0 )
			return;
		// Engine winding: the first order below faces the air when the base sample is solid (checked in play — the
		// other way round culled every top face and showed the hills from inside)
		if ( solidAtBase )
		{
			indices.Add( a ); indices.Add( c ); indices.Add( b );
			indices.Add( a ); indices.Add( d ); indices.Add( c );
		}
		else
		{
			indices.Add( a ); indices.Add( b ); indices.Add( c );
			indices.Add( a ); indices.Add( c ); indices.Add( d );
		}
	}
}
