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
		public Color[] Colors { get; init; }
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
		int borderLatticeVerticesPerSide = 0 )
	{
		chunkSizeMeters = Math.Max( 16f, chunkSizeMeters );
		maxTerrainHeightMeters = Math.Max( 50f, maxTerrainHeightMeters );
		var maxHeightMeters = Math.Max( 50f, settings.MaxTerrainHeightMeters );
		maxTerrainHeightMeters = Math.Max( maxHeightMeters, maxTerrainHeightMeters );

		var g = BuildChunkGeometry( settings, backend, coord, chunkSizeMeters, verticesPerSide, heightSmoothPasses, heightSmoothStrength01, borderLatticeVerticesPerSide );
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
		Mesh extraMesh = null )
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

		var indexCount = g.Indices.Length;
		var material = GetTerrainMaterial();
		var mesh = new Mesh( material, MeshPrimitiveType.Triangles );
		mesh.CreateVertexBuffer<Vertex>( vertexCount );
		mesh.CreateIndexBuffer( indexCount );

		var collisionPositions = new List<Vector3>( vertexCount );
		var collisionIndices = new List<int>( g.Indices );
		var heightsZ = new float[vertexCount];

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
				vertices[i] = new Vertex
				{
					Position = position,
					Normal = normal,
					Tangent = ComputeTangent( normal ),
					TexCoord0 = new Vector2( ix / (float)(verticesPerSide - 1), iy / (float)(verticesPerSide - 1) ),
					Color = colors[i],
				};
			}
		} );

		mesh.LockIndexBuffer( indices =>
		{
			for ( var i = 0; i < indexCount; i++ )
				indices[i] = collisionIndices[i];
		} );

		var bounds = new BBox(
			Vector3.Zero,
			TerrainWorldUnits.MetersToEngine( new Vector3( chunkSizeMeters, chunkSizeMeters, maxTerrainHeightMeters ) ) );
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
		};
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
		bool riverBedAsGround = true )
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
		var isWater = new bool[vertexCount];
		var insideWorld = new bool[vertexCount];
		var biomeMap = new TerrainPreviewBiomeId[vertexCount];
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
					: settings.SeaLevelMeters;
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

		SnapBordersToCoarseLattice( heights, verticesPerSide, borderLatticeVerticesPerSide );

		TerrainPreviewChunkHeightSmooth.ApplyInteriorGrid(
			heights, verticesPerSide, heightSmoothPasses, heightSmoothStrength01 );

		var offsetX = new float[vertexCount];
		var offsetY = new float[vertexCount];
		// Ledge faces are plain heightfield rows now (face width >= height / tan(Face Slope)); the vertex snapping that
		// made 1 m faces vertical is gone with it — it was the source of every fin and spike along a ledge (2026-10-07).

		TerrainChunkBiomeDisplay.FillChunkVertexColors(
			settings,
			backend,
			verticesPerSide,
			verticesPerSide,
			chunkMinX,
			chunkMinY,
			step,
			insideWorld,
			isWater,
			biomeMap,
			colors,
			samples,
			riverBedAsGround );

		TerrainGroundTextureMask.Apply( settings, colors, biomeMap, verticesPerSide );

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

		// Vertex alpha is the ground-texture mask written by TerrainGroundTextureMask above — never reset it
		// (a leftover alpha=1 pass here painted every biome with the Clover grass texture, 2026-10-13).
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
			Indices = indices,
			RawHeights = rawHeights,
			EdgeRing = edgeRing,
		};
	}

	/// <summary>Dev switch (console <c>terrain_ledge_diagonals</c>) to A/B the ledge-aware triangulation.</summary>
	public static bool LedgeAwareDiagonals { get; set; } = true;

	/// <summary>
	/// Snap border vertices that are not on the shared coarse lattice onto the straight line between
	/// their coarse neighbours. Every chunk edge then carries identical geometry regardless of the
	/// LOD mix on either side — full↔far seams become watertight with no neighbor tracking.
	/// </summary>
	static void SnapBordersToCoarseLattice( float[] heights, int verticesPerSide, int latticeVerticesPerSide )
	{
		if ( latticeVerticesPerSide <= 1 || latticeVerticesPerSide >= verticesPerSide )
			return;

		if ( (verticesPerSide - 1) % (latticeVerticesPerSide - 1) != 0 )
			return;

		var ratio = (verticesPerSide - 1) / (latticeVerticesPerSide - 1);
		SnapBorderLine( heights, verticesPerSide, ratio, startIndex: 0, stride: 1 );
		SnapBorderLine( heights, verticesPerSide, ratio, startIndex: (verticesPerSide - 1) * verticesPerSide, stride: 1 );
		SnapBorderLine( heights, verticesPerSide, ratio, startIndex: 0, stride: verticesPerSide );
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
		return sample.IsInsideWorld ? sample.HeightMeters : settings.SeaLevelMeters;
	}

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

		// Edge vertices: central differences from the raw samples on both sides of the border (shared with the
		// neighbour chunk), so both chunks compute the same normal there.
		if ( g.EdgeRing is not null && g.RawHeights is not null && (ix == 0 || iy == 0 || ix == verticesPerSide - 1 || iy == verticesPerSide - 1) )
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

	public static Material GetTerrainMaterial()
	{
		if ( _terrainMaterial is null || !_terrainMaterial.IsValid )
		{
			// pixel_lit with F_VERTEX_PAINT: biome vertex colours, and the Clover ground texture (vertex alpha =
			// TerrainGroundTextureMask) tiled on the world plane — the same ground as lightingTest.
			_terrainMaterial = Material.Load( "materials/terrain/terrain_ground.vmat" );
			if ( !_terrainMaterial.IsValid )
				_terrainMaterial = Material.FromShader( "shaders/pixel_lit.shader" );

			if ( !_terrainMaterial.IsValid )
				Log.Warning( "[TerrainMeshBuilder] Terrain material failed to load — chunks may be invisible." );
		}

		return _terrainMaterial;
	}
}
