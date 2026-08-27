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
		backend ??= TerrainPreviewBackendRegistry.Active;
		verticesPerSide = Math.Clamp( verticesPerSide, 4, 256 );
		chunkSizeMeters = Math.Max( 16f, chunkSizeMeters );
		maxTerrainHeightMeters = Math.Max( 50f, maxTerrainHeightMeters );

		var worldRadius = settings.TotalWorldRadiusMeters;
		var chunkMinX = -worldRadius + (coord.X * chunkSizeMeters);
		var chunkMinY = -worldRadius + (coord.Y * chunkSizeMeters);
		var step = chunkSizeMeters / (verticesPerSide - 1);
		var maxHeightMeters = Math.Max( 50f, settings.MaxTerrainHeightMeters );
		maxTerrainHeightMeters = Math.Max( maxHeightMeters, maxTerrainHeightMeters );

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

		SnapBordersToCoarseLattice( heights, verticesPerSide, borderLatticeVerticesPerSide );

		TerrainPreviewChunkHeightSmooth.ApplyInteriorGrid(
			heights, verticesPerSide, heightSmoothPasses, heightSmoothStrength01 );

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
			samples );

		TerrainCloverGroundTexture.ApplyToChunkColors(
			settings,
			colors,
			biomeMap,
			chunkMinX,
			chunkMinY,
			step,
			verticesPerSide );

		TerrainPreviewHeightDiagnostics.TryLogChunkVertexTrace(
			settings,
			coord,
			chunkMinX,
			chunkMinY,
			heights[0],
			heights[0],
			backend );

		var minHeightAfter = float.MaxValue;
		var maxHeightAfter = float.MinValue;
		for ( var i = 0; i < vertexCount; i++ )
		{
			minHeightAfter = Math.Min( minHeightAfter, heights[i] );
			maxHeightAfter = Math.Max( maxHeightAfter, heights[i] );
		}

		if ( _heightRangeLogs < 3 && minHeight < float.MaxValue )
		{
			_heightRangeLogs++;
			Log.Info(
				$"[TerrainMeshBuilder] Chunk {coord} sampled {minHeight:0.#}…{maxHeightSample:0.#} m → mesh {minHeightAfter:0.#}…{maxHeightAfter:0.#} m (Δ{maxHeightAfter - minHeightAfter:0.#} m / {chunkSizeMeters:0.#} m)." );
		}

		for ( var i = 0; i < vertexCount; i++ )
			colors[i] = colors[i].WithAlpha( 1f );

		var indexCount = (verticesPerSide - 1) * (verticesPerSide - 1) * 6;
		var material = GetTerrainMaterial();
		var mesh = new Mesh( material, MeshPrimitiveType.Triangles );
		mesh.CreateVertexBuffer<Vertex>( vertexCount );
		mesh.CreateIndexBuffer( indexCount );

		var collisionPositions = new List<Vector3>( vertexCount );
		var collisionIndices = new List<int>( indexCount );

		mesh.LockVertexBuffer<Vertex>( vertices =>
		{
			for ( var i = 0; i < vertexCount; i++ )
			{
				var ix = i % verticesPerSide;
				var iy = i / verticesPerSide;
				var localX = ix * step;
				var localY = iy * step;
				var normal = ComputeNormal( heights, verticesPerSide, i, step );
				var position = TerrainWorldUnits.MetersToEngine( new Vector3( localX, localY, heights[i] ) );

				collisionPositions.Add( position );
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

		for ( var iy = 0; iy < verticesPerSide - 1; iy++ )
		{
			for ( var ix = 0; ix < verticesPerSide - 1; ix++ )
			{
				var i0 = (iy * verticesPerSide) + ix;
				var i1 = i0 + 1;
				var i2 = i0 + verticesPerSide;
				var i3 = i2 + 1;

				collisionIndices.Add( i0 );
				collisionIndices.Add( i1 );
				collisionIndices.Add( i3 );
				collisionIndices.Add( i0 );
				collisionIndices.Add( i3 );
				collisionIndices.Add( i2 );
			}
		}

		mesh.LockIndexBuffer( indices =>
		{
			for ( var i = 0; i < indexCount; i++ )
				indices[i] = collisionIndices[i];
		} );

		var bounds = new BBox(
			Vector3.Zero,
			TerrainWorldUnits.MetersToEngine( new Vector3( chunkSizeMeters, chunkSizeMeters, maxTerrainHeightMeters ) ) );
		mesh.Bounds = bounds;

		var model = new ModelBuilder()
			.AddMesh( mesh )
			.AddCollisionMesh( collisionPositions, collisionIndices )
			.AddTraceMesh( collisionPositions, collisionIndices )
			.Create();

		return new BuildResult
		{
			Model = model,
			Material = material,
			LocalBounds = bounds,
		};
	}

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

	static Vector3 ComputeNormal( float[] heights, int verticesPerSide, int index, float step )
	{
		var ix = index % verticesPerSide;
		var iy = index / verticesPerSide;
		var left = ix > 0 ? heights[index - 1] : heights[index];
		var right = ix < verticesPerSide - 1 ? heights[index + 1] : heights[index];
		var down = iy > 0 ? heights[index - verticesPerSide] : heights[index];
		var up = iy < verticesPerSide - 1 ? heights[index + verticesPerSide] : heights[index];

		var dhdx = (right - left) / Math.Max( step * 2f, 0.001f );
		var dhdy = (up - down) / Math.Max( step * 2f, 0.001f );
		var normal = new Vector3( -dhdx, -dhdy, 1f ).Normal;
		return normal.LengthSquared > 1e-8f ? normal : Vector3.Up;
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
			_terrainMaterial = Material.Load( "materials/terrain/terrain_vertexcolor.vmat" );
			if ( !_terrainMaterial.IsValid )
				_terrainMaterial = Material.FromShader( "shaders/vertex_color.shader" );

			if ( !_terrainMaterial.IsValid )
				Log.Warning( "[TerrainMeshBuilder] Terrain material failed to load — chunks may be invisible." );
		}

		return _terrainMaterial;
	}
}
