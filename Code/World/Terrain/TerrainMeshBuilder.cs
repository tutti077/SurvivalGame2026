namespace Survival;

using System.Collections.Generic;

/// <summary>Builds a colored heightfield mesh for one terrain chunk — samples world meters, never reads preview PNG.</summary>
public static class TerrainMeshBuilder
{
	static Material _terrainMaterial;
	static int _heightRangeLogs;

	public readonly struct BuildResult
	{
		public Model Model { get; init; }
		public Material Material { get; init; }
		public BBox LocalBounds { get; init; }
		/// <summary>Final vertex z (engine units, chunk-local, sink applied) on the VerticesPerSide² grid — the grass samples this instead of tracing physics.</summary>
		public float[] HeightsZ { get; init; }
		public int VerticesPerSide { get; init; }
		/// <summary>Grid spacing in engine units.</summary>
		public float StepUnits { get; init; }
		/// <summary>Per-vertex splat weights / extras of the grid (<see cref="ChunkGeometry.Colors"/>, <see cref="ChunkGeometry.Splat"/>) — a voxel column seeds its ground style from them.</summary>
		public Color[] Colors { get; init; }
		public Vector4[] Splat { get; init; }
		/// <summary>Generated tilled soil reaching this chunk (<see cref="ChunkGeometry.GroundPatches"/>): grass, farming and a voxel column read it.</summary>
		public TerrainGroundPatch[] GroundPatches { get; init; }
	}

	/// <summary>Everything a chunk mesh is made of, in chunk-local metres — shared by <see cref="BuildChunk"/> and the dev dump command.</summary>
	public sealed class ChunkGeometry
	{
		public float ChunkMinX { get; init; }
		public float ChunkMinY { get; init; }
		public float Step { get; init; }
		public int VerticesPerSide { get; init; }
		public float[] Heights { get; init; }
		public float[] OffsetX { get; init; }
		public float[] OffsetY { get; init; }
		public TerrainPreviewSample[] Samples { get; init; }
		/// <summary>Splat weights per vertex (<see cref="TerrainGroundSplat"/>): Clover / Redwood / Amber shares + Mountain.</summary>
		public Color[] Colors { get; init; }
		/// <summary>Splat extras per vertex: sand, wet, far canopy darkening (w unused).</summary>
		public Vector4[] Splat { get; init; }
		/// <summary>Generated tilled soil reaching this chunk (wild-wheat beds), already painted into <see cref="Splat"/>; null = none.</summary>
		public TerrainGroundPatch[] GroundPatches { get; init; }
		public int[] Indices { get; init; }
		/// <summary>Heights as sampled, before border snapping and smoothing (edge normals are built from these).</summary>
		public float[] RawHeights { get; init; }
		/// <summary>
		/// Raw heights one step OUTSIDE the chunk: [left n][right n][down n][up n] (left / right indexed by row, down / up
		/// by column). With them an edge vertex gets a central-difference normal from the same world samples its
		/// neighbour chunk uses, so two chunks shade their shared border identically. One-sided differences clamped at
		/// the edge drew every chunk border as a seam when looking down (Mark 2026-10-20).
		/// </summary>
		public float[] EdgeRing { get; init; }
	}

	public static BuildResult BuildChunk(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		TerrainChunkCoord coord,
		float chunkSizeMeters,
		int verticesPerSide,
		float maxTerrainHeightMeters,
		int heightSmoothPasses = 1,
		float heightSmoothStrength01 = 0.38f,
		int borderLatticeVerticesPerSide = 0,
		int fineBorderSides = 0,
		TerrainGroundPatch[] groundPatches = null )
	{
		chunkSizeMeters = Math.Max( 16f, chunkSizeMeters );
		maxTerrainHeightMeters = Math.Max( 50f, maxTerrainHeightMeters );
		var maxHeightMeters = Math.Max( 50f, settings.MaxTerrainHeightMeters );
		maxTerrainHeightMeters = Math.Max( maxHeightMeters, maxTerrainHeightMeters );

		var g = BuildChunkGeometry( settings, backend, coord, chunkSizeMeters, verticesPerSide, heightSmoothPasses, heightSmoothStrength01, borderLatticeVerticesPerSide, fineBorderSides, groundPatches );
		return BuildModelFromGeometry( g, chunkSizeMeters, maxTerrainHeightMeters, withCollision: true, sinkMeters: 0f );
	}

	/// <summary>
	/// Engine side of a chunk: mesh, model, optional collision from already-sampled geometry. Main thread only
	/// (the geometry itself may come from a worker). <paramref name="sinkMeters"/> lowers every vertex — the
	/// far tier uses it so its coarse surface stays under the detailed near mesh.
	/// </summary>
	public static BuildResult BuildModelFromGeometry(
		ChunkGeometry g,
		float chunkSizeMeters,
		float maxTerrainHeightMeters,
		bool withCollision,
		float sinkMeters,
		Mesh extraMesh = null,
		bool farTier = false,
		bool horizonTier = false )
	{
		chunkSizeMeters = Math.Max( 16f, chunkSizeMeters );
		maxTerrainHeightMeters = Math.Max( 50f, maxTerrainHeightMeters );
		var verticesPerSide = g.VerticesPerSide;
		var vertexCount = verticesPerSide * verticesPerSide;
		var step = g.Step;
		var heights = g.Heights;
		var offsetX = g.OffsetX;
		var offsetY = g.OffsetY;
		var colors = g.Colors;
		var splat = g.Splat;
		// TEXCOORD1.y: which tier this is (pixel_lit buries the horizon tier inside the far tier's circle)
		var tier = horizonTier ? 2f : farTier ? 1f : 0f;

		var indexCount = g.Indices.Length;
		var material = farTier ? GetFarTerrainMaterial() : GetTerrainMaterial();
		var mesh = new Mesh( material, MeshPrimitiveType.Triangles );
		// Skirt: a strip hanging SkirtMeters down from every border edge (both windings). Where a detailed chunk meets a
		// coarse one the shared edge is the same line, but the extra vertex on it leaves pixel-thin rasterisation gaps
		// (T-junctions) — sky-coloured cracks along chunk borders (Mark 2026-10-22: "tearing and splitting"). The skirt
		// fills them with ground. Render mesh only; collision keeps the plain grid.
		var borderCount = 4 * (verticesPerSide - 1);
		var skirtVertexCount = borderCount * 2;
		var skirtIndexCount = borderCount * 12;
		mesh.CreateVertexBuffer<Vertex>( vertexCount + skirtVertexCount );
		mesh.CreateIndexBuffer( indexCount + skirtIndexCount );

		var collisionPositions = new List<Vector3>( vertexCount );
		var collisionIndices = new List<int>( g.Indices );
		var heightsZ = new float[vertexCount];

		var minZ = float.MaxValue;
		var maxZ = float.MinValue;
		var border = BorderVertexLoop( verticesPerSide );
		mesh.LockVertexBuffer<Vertex>( vertices =>
		{
			for ( var i = 0; i < vertexCount; i++ )
			{
				var ix = i % verticesPerSide;
				var iy = i / verticesPerSide;
				var localX = (ix * step) + offsetX[i];
				var localY = (iy * step) + offsetY[i];
				var normal = ComputeNormal( g, i );
				var position = TerrainWorldUnits.MetersToEngine( new Vector3( localX, localY, heights[i] - sinkMeters ) );

				collisionPositions.Add( position );
				heightsZ[i] = position.z;
				minZ = Math.Min( minZ, position.z );
				maxZ = Math.Max( maxZ, position.z );
				vertices[i] = new Vertex
				{
					Position = position,
					Normal = normal,
					Tangent = ComputeTangent( normal ),
					// The terrain material tiles in world space, so the UVs carry the splat extras instead
					TexCoord0 = new Vector2( splat[i].x, splat[i].y ),
					TexCoord1 = new Vector2( splat[i].z, tier ),
					Color = colors[i],
				};
			}

			// Skirt: each border segment gets its own top pair (copies, so the skirt's flat normals do not touch the
			// ground's) and bottom pair
			var drop = TerrainWorldUnits.MetersToEngine( SkirtMeters );
			for ( var k = 0; k < borderCount; k++ )
			{
				var top = vertices[border[k]];
				var bottom = top;
				bottom.Position = top.Position - new Vector3( 0f, 0f, drop );
				vertices[vertexCount + (k * 2)] = top;
				vertices[vertexCount + (k * 2) + 1] = bottom;
			}
		} );

		mesh.LockIndexBuffer( indices =>
		{
			for ( var i = 0; i < indexCount; i++ )
				indices[i] = collisionIndices[i];
			var n = indexCount;
			for ( var k = 0; k < borderCount; k++ )
			{
				var a = vertexCount + (k * 2);                       // top / bottom at this border vertex
				var b = vertexCount + (((k + 1) % borderCount) * 2); // top / bottom at the next one
				// both windings, so the strip shows from either side of the crack
				indices[n++] = a; indices[n++] = b; indices[n++] = a + 1;
				indices[n++] = b; indices[n++] = b + 1; indices[n++] = a + 1;
				indices[n++] = a; indices[n++] = a + 1; indices[n++] = b;
				indices[n++] = b; indices[n++] = a + 1; indices[n++] = b + 1;
			}
		} );

		// The real vertical extent: lake beds and the ocean floor lie below z 0, and a box starting at 0 let the engine
		// cull chunks whose visible ground was under it — whole squares missing (2026-10-22)
		var skirtUnits = TerrainWorldUnits.MetersToEngine( SkirtMeters );
		var bounds = new BBox(
			new Vector3( 0f, 0f, Math.Min( 0f, minZ - skirtUnits ) ),
			new Vector3( TerrainWorldUnits.MetersToEngine( chunkSizeMeters ), TerrainWorldUnits.MetersToEngine( chunkSizeMeters ),
				Math.Max( maxZ, TerrainWorldUnits.MetersToEngine( maxTerrainHeightMeters ) ) ) );
		mesh.Bounds = bounds;

		var builder = new ModelBuilder().AddMesh( mesh );
		if ( extraMesh is not null )
			builder = builder.AddMesh( extraMesh );   // the far tier's baked impostor cards (own material)
		if ( withCollision )
		{
			builder = builder
				.AddCollisionMesh( collisionPositions, collisionIndices )
				.AddTraceMesh( collisionPositions, collisionIndices );
		}
		var model = builder.Create();

		return new BuildResult
		{
			Model = model,
			Material = material,
			LocalBounds = bounds,
			HeightsZ = heightsZ,
			VerticesPerSide = verticesPerSide,
			StepUnits = TerrainWorldUnits.MetersToEngine( step ),
			Colors = colors,
			Splat = splat,
			GroundPatches = g.GroundPatches,
		};
	}

	/// <summary>Depth of the crack-filling skirt along every chunk border (render mesh only).</summary>
	const float SkirtMeters = 1.5f;

	/// <summary>Grid indices of a chunk's border, walked once round (bottom row, right column, top row back, left column down).</summary>
	static int[] BorderVertexLoop( int n )
	{
		var loop = new int[4 * (n - 1)];
		var k = 0;
		for ( var x = 0; x < n - 1; x++ ) loop[k++] = x;                              // bottom, left → right
		for ( var y = 0; y < n - 1; y++ ) loop[k++] = (y * n) + (n - 1);              // right, bottom → top
		for ( var x = n - 1; x > 0; x-- ) loop[k++] = ((n - 1) * n) + x;              // top, right → left
		for ( var y = n - 1; y > 0; y-- ) loop[k++] = y * n;                          // left, top → bottom
		return loop;
	}

	/// <summary>Samples the vertex grid, snaps ledge vertices, colours and triangulates — no engine objects created.</summary>
	public static ChunkGeometry BuildChunkGeometry(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		TerrainChunkCoord coord,
		float chunkSizeMeters,
		int verticesPerSide,
		int heightSmoothPasses,
		float heightSmoothStrength01,
		int borderLatticeVerticesPerSide,
		int fineBorderSides = 0,
		TerrainGroundPatch[] groundPatches = null )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;
		verticesPerSide = Math.Clamp( verticesPerSide, 4, 256 );
		chunkSizeMeters = Math.Max( 16f, chunkSizeMeters );

		var worldRadius = settings.TotalWorldRadiusMeters;
		var chunkMinX = -worldRadius + (coord.X * chunkSizeMeters);
		var chunkMinY = -worldRadius + (coord.Y * chunkSizeMeters);
		var step = chunkSizeMeters / (verticesPerSide - 1);

		var vertexCount = verticesPerSide * verticesPerSide;
		var heights = new float[vertexCount];
		var samples = new TerrainPreviewSample[vertexCount];
		var colors = new Color[vertexCount];
		var splat = new Vector4[vertexCount];
		var minHeight = float.MaxValue;
		var maxHeightSample = float.MinValue;

		for ( var iy = 0; iy < verticesPerSide; iy++ )
		{
			for ( var ix = 0; ix < verticesPerSide; ix++ )
			{
				var idx = (iy * verticesPerSide) + ix;
				var worldX = chunkMinX + (ix * step);
				var worldY = chunkMinY + (iy * step );
				var sample = backend.Sample( settings, worldX, worldY );
				samples[idx] = sample;

				var heightMeters = sample.IsInsideWorld
					? sample.HeightMeters
					: OutsideWorldFloorMeters( settings );
				heights[idx] = heightMeters;
				minHeight = Math.Min( minHeight, heightMeters );
				maxHeightSample = Math.Max( maxHeightSample, heightMeters );
			}
		}

		var rawHeights = heights.ToArray();
		var edgeRing = new float[verticesPerSide * 4];
		var span = (verticesPerSide - 1) * step;
		for ( var k = 0; k < verticesPerSide; k++ )
		{
			var along = k * step;
			edgeRing[k] = RingHeight( settings, backend, chunkMinX - step, chunkMinY + along );
			edgeRing[verticesPerSide + k] = RingHeight( settings, backend, chunkMinX + span + step, chunkMinY + along );
			edgeRing[(2 * verticesPerSide) + k] = RingHeight( settings, backend, chunkMinX + along, chunkMinY - step );
			edgeRing[(3 * verticesPerSide) + k] = RingHeight( settings, backend, chunkMinX + along, chunkMinY + span + step );
		}

		SnapBordersToCoarseLattice( heights, verticesPerSide, borderLatticeVerticesPerSide, fineBorderSides );

		TerrainPreviewChunkHeightSmooth.ApplyInteriorGrid(
			heights, verticesPerSide, heightSmoothPasses, heightSmoothStrength01 );

		var offsetX = new float[vertexCount];
		var offsetY = new float[vertexCount];
		// Ledge faces are plain heightfield rows now (face width >= height / tan(Face Slope)); the vertex snapping that
		// made 1 m faces vertical is gone with it — it was the source of every fin and spike along a ledge (2026-10-07).

		TerrainGroundSplat.Fill( settings, samples, chunkMinX, chunkMinY, step, verticesPerSide, colors, splat );
		TerrainGroundSplat.ApplyTilledPatches( groundPatches, chunkMinX, chunkMinY, step, verticesPerSide, splat );

		TerrainPreviewHeightDiagnostics.TryLogChunkVertexTrace(
			settings,
			coord,
			chunkMinX,
			chunkMinY,
			heights[0],
			heights[0],
			backend );

		// Diagnostics only: the range scan feeds a log capped at three lines, so skip it afterwards.
		if ( _heightRangeLogs < 3 && minHeight < float.MaxValue )
		{
			_heightRangeLogs++;
			var minHeightAfter = float.MaxValue;
			var maxHeightAfter = float.MinValue;
			for ( var i = 0; i < vertexCount; i++ )
			{
				minHeightAfter = Math.Min( minHeightAfter, heights[i] );
				maxHeightAfter = Math.Max( maxHeightAfter, heights[i] );
			}
			Log.Info(
				$"[TerrainMeshBuilder] Chunk {coord} sampled {minHeight:0.#}…{maxHeightSample:0.#} m → mesh {minHeightAfter:0.#}…{maxHeightAfter:0.#} m (Δ{maxHeightAfter - minHeightAfter:0.#} m / {chunkSizeMeters:0.#} m)." );
		}

		var indices = new int[(verticesPerSide - 1) * (verticesPerSide - 1) * 6];
		var w = 0;
		for ( var iy = 0; iy < verticesPerSide - 1; iy++ )
		{
			for ( var ix = 0; ix < verticesPerSide - 1; ix++ )
			{
				var i0 = (iy * verticesPerSide) + ix;
				var i1 = i0 + 1;
				var i2 = i0 + verticesPerSide;
				var i3 = i2 + 1;
				if ( LedgeAwareDiagonals && UseLedgeAwareAltDiagonal( samples, i0, i1, i2, i3 ) )
				{
					// Diagonal i1–i2: keeps a lone i0 / i3 corner off the diagonal.
					indices[w++] = i0; indices[w++] = i1; indices[w++] = i2;
					indices[w++] = i1; indices[w++] = i3; indices[w++] = i2;
					continue;
				}
				indices[w++] = i0; indices[w++] = i1; indices[w++] = i3;
				indices[w++] = i0; indices[w++] = i3; indices[w++] = i2;
			}
		}

		return new ChunkGeometry
		{
			ChunkMinX = chunkMinX,
			ChunkMinY = chunkMinY,
			Step = step,
			VerticesPerSide = verticesPerSide,
			Heights = heights,
			OffsetX = offsetX,
			OffsetY = offsetY,
			Samples = samples,
			Colors = colors,
			Splat = splat,
			GroundPatches = groundPatches is { Length: > 0 } ? groundPatches : null,
			Indices = indices,
			RawHeights = rawHeights,
			EdgeRing = edgeRing,
		};
	}

	/// <summary>Dev switch (console <c>terrain_ledge_diagonals</c>) to A/B the ledge-aware triangulation.</summary>
	public static bool LedgeAwareDiagonals { get; set; } = true;

	/// <summary>Border sides (bit flags for <c>fineBorderSides</c>): the −Y row, +Y row, −X column, +X column.</summary>
	public const int BorderMinY = 1, BorderMaxY = 2, BorderMinX = 4, BorderMaxX = 8;

	/// <summary>
	/// Snap border vertices that are not on the shared coarse lattice onto the straight line between
	/// their coarse neighbours. Every chunk edge then carries identical geometry regardless of the
	/// LOD mix on either side — full↔far seams become watertight with no neighbor tracking.
	/// Sides in <paramref name="fineBorderSides"/> are left alone: both chunks there are full detail (water chunks and
	/// their neighbours), so the raw samples already match — and snapping flattened a river channel crossing the border
	/// into a ridge with the skirt showing under it (Mark 2026-10-25: "weird seams between the chunks").
	/// </summary>
	static void SnapBordersToCoarseLattice( float[] heights, int verticesPerSide, int latticeVerticesPerSide, int fineBorderSides )
	{
		if ( latticeVerticesPerSide <= 1 || latticeVerticesPerSide >= verticesPerSide )
			return;

		if ( (verticesPerSide - 1) % (latticeVerticesPerSide - 1) != 0 )
			return;

		var ratio = (verticesPerSide - 1) / (latticeVerticesPerSide - 1);
		if ( (fineBorderSides & BorderMinY) == 0 )
			SnapBorderLine( heights, verticesPerSide, ratio, startIndex: 0, stride: 1 );
		if ( (fineBorderSides & BorderMaxY) == 0 )
			SnapBorderLine( heights, verticesPerSide, ratio, startIndex: (verticesPerSide - 1) * verticesPerSide, stride: 1 );
		if ( (fineBorderSides & BorderMinX) == 0 )
			SnapBorderLine( heights, verticesPerSide, ratio, startIndex: 0, stride: verticesPerSide );
		if ( (fineBorderSides & BorderMaxX) == 0 )
			SnapBorderLine( heights, verticesPerSide, ratio, startIndex: verticesPerSide - 1, stride: verticesPerSide );
	}

	static void SnapBorderLine( float[] heights, int count, int ratio, int startIndex, int stride )
	{
		// Lattice verts (offset 0) are never written, so reading them mid-loop stays canonical.
		for ( var i = 1; i < count - 1; i++ )
		{
			var offset = i % ratio;
			if ( offset == 0 )
				continue;

			var i0 = i - offset;
			var i1 = Math.Min( i0 + ratio, count - 1 );
			var t = offset / (float)ratio;
			var h0 = heights[startIndex + (i0 * stride)];
			var h1 = heights[startIndex + (i1 * stride)];
			heights[startIndex + (i * stride)] = h0 + ((h1 - h0) * t);
		}
	}

	/// <summary>
	/// Ledge-aware triangulation. Where a ledge line clips one corner of a quad, that lone corner sits on
	/// the line at one terrace height and the other three corners are on the other terrace. If the quad's
	/// diagonal touches the lone corner, the triangle it forms spans both terraces with its third vertex
	/// off the line: a slanted fin hanging off the foot (or lip) of the cliff. Picking the diagonal that
	/// avoids the lone corner makes that quad a vertical triangle on the line plus a flat terrace triangle.
	/// Default diagonal is i0–i3; returns true to use i1–i2 instead.
	/// </summary>
	static bool UseLedgeAwareAltDiagonal( TerrainPreviewSample[] samples, int i0, int i1, int i2, int i3 )
	{
		var s0 = LedgeSide( samples[i0] );
		var s1 = LedgeSide( samples[i1] );
		var s2 = LedgeSide( samples[i2] );
		var s3 = LedgeSide( samples[i3] );
		if ( (s0 | s1 | s2 | s3) == 0 )
			return false;

		// Lone corner on the default diagonal (i0 or i3) → switch. Lone corner on i1 / i2 → keep default.
		if ( IsLone( s0, s1, s2, s3 ) || IsLone( s3, s0, s1, s2 ) )
			return true;
		return false;
	}

	static int LedgeSide( in TerrainPreviewSample s )
		=> !s.Ledge.Present ? 0 : s.Ledge.DistanceMeters >= 0f ? 1 : -1;

	/// <summary>True when <paramref name="a"/> is known and every other known corner is on the opposite side.</summary>
	static bool IsLone( int a, int b, int c, int d )
	{
		if ( a == 0 )
			return false;
		var others = 0;
		if ( b != 0 ) { if ( b == a ) return false; others++; }
		if ( c != 0 ) { if ( c == a ) return false; others++; }
		if ( d != 0 ) { if ( d == a ) return false; others++; }
		return others >= 2;
	}

	/// <summary>
	/// Vertex normal from the actual neighbour positions (XY offsets included), so a vertical ledge
	/// face gets a sideways normal instead of the heightfield average.
	/// </summary>
	static float RingHeight( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, float worldX, float worldY )
	{
		var sample = backend.Sample( settings, worldX, worldY );
		return sample.IsInsideWorld ? sample.HeightMeters : OutsideWorldFloorMeters( settings );
	}

	/// <summary>
	/// Ground past the world's edge: the open-ocean floor (Ocean Max Depth under the sea), not ground at sea level —
	/// flat ground there poked through the sea surface's wave troughs as black specks on the horizon (2026-10-22).
	/// </summary>
	static float OutsideWorldFloorMeters( TerrainPreviewSettings settings )
		=> settings.SeaLevelMeters - Math.Max( 0f, settings.OceanMaxDepthMeters );

	/// <summary>Raw height at grid coords that may lie one step outside the chunk (the edge ring).</summary>
	static float RawHeightAt( ChunkGeometry g, int ix, int iy )
	{
		var n = g.VerticesPerSide;
		if ( ix < 0 ) return g.EdgeRing[Math.Clamp( iy, 0, n - 1 )];
		if ( ix >= n ) return g.EdgeRing[n + Math.Clamp( iy, 0, n - 1 )];
		if ( iy < 0 ) return g.EdgeRing[(2 * n) + Math.Clamp( ix, 0, n - 1 )];
		if ( iy >= n ) return g.EdgeRing[(3 * n) + Math.Clamp( ix, 0, n - 1 )];
		return g.RawHeights[(iy * n) + ix];
	}

	static Vector3 ComputeNormal( ChunkGeometry g, int index )
	{
		var verticesPerSide = g.VerticesPerSide;
		var step = g.Step;
		var ix = index % verticesPerSide;
		var iy = index / verticesPerSide;

		// Every vertex: central differences from the raw samples (the edge ring past the border is shared with the
		// neighbour chunk). Edge-only raw normals next to smoothed interior normals shaded a 2 m band along every chunk
		// border differently — thin light lines across lake beds and fields (2026-10-22).
		if ( g.EdgeRing is not null && g.RawHeights is not null )
		{
			var ex = new Vector3( 2f * step, 0f, RawHeightAt( g, ix + 1, iy ) - RawHeightAt( g, ix - 1, iy ) );
			var ey = new Vector3( 0f, 2f * step, RawHeightAt( g, ix, iy + 1 ) - RawHeightAt( g, ix, iy - 1 ) );
			var en = Vector3.Cross( ex, ey );
			if ( en.LengthSquared < 1e-8f )
				return Vector3.Up;
			en = en.Normal;
			return en.z < 0f ? -en : en;
		}

		var heights = g.Heights;
		var offsetX = g.OffsetX;
		var offsetY = g.OffsetY;
		var left = ix > 0 ? index - 1 : index;
		var right = ix < verticesPerSide - 1 ? index + 1 : index;
		var down = iy > 0 ? index - verticesPerSide : index;
		var up = iy < verticesPerSide - 1 ? index + verticesPerSide : index;

		var tx = new Vector3(
			((right % verticesPerSide) - (left % verticesPerSide)) * step + offsetX[right] - offsetX[left],
			offsetY[right] - offsetY[left],
			heights[right] - heights[left] );
		var ty = new Vector3(
			offsetX[up] - offsetX[down],
			((up / verticesPerSide) - (down / verticesPerSide)) * step + offsetY[up] - offsetY[down],
			heights[up] - heights[down] );

		var normal = Vector3.Cross( tx, ty );
		if ( normal.LengthSquared < 1e-8f )
			return Vector3.Up;
		normal = normal.Normal;
		return normal.z < 0f ? -normal : normal;
	}

	static Vector4 ComputeTangent( Vector3 normal )
	{
		var tangent = Vector3.Cross( normal, Vector3.Up );
		if ( tangent.LengthSquared < 1e-6f )
			tangent = Vector3.Cross( normal, Vector3.Forward );

		return new Vector4( tangent.Normal, 1f );
	}

	static Material _farTerrainMaterial;

	/// <summary>
	/// The far tier's ground: the same material with F_FAR_SINK — sunk on the GPU under the near chunks only, so the
	/// far surface needs no CPU sink and there is no step where the near square ends.
	/// </summary>
	public static Material GetFarTerrainMaterial()
	{
		if ( _farTerrainMaterial is null || !_farTerrainMaterial.IsValid )
		{
			_farTerrainMaterial = Material.Load( "materials/terrain/terrain_ground_far.vmat" );
			if ( !_farTerrainMaterial.IsValid )
				_farTerrainMaterial = GetTerrainMaterial();
		}
		return _farTerrainMaterial;
	}

	public static Material GetTerrainMaterial()
	{
		if ( _terrainMaterial is null || !_terrainMaterial.IsValid )
		{
			// pixel_lit with F_TERRAIN_SPLAT: the per-pixel biome blend (TerrainGroundSplat writes what each vertex is),
			// every ground layer tiled on the world plane — the grass is the same ground as lightingTest.
			_terrainMaterial = Material.Load( "materials/terrain/terrain_ground.vmat" );
			if ( !_terrainMaterial.IsValid )
				_terrainMaterial = Material.FromShader( "shaders/pixel_lit.shader" );

			if ( !_terrainMaterial.IsValid )
				Log.Warning( "[TerrainMeshBuilder] Terrain material failed to load — chunks may be invisible." );
		}

		return _terrainMaterial;
	}
}
