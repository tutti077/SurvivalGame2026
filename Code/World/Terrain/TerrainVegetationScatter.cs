using Sandbox;

namespace Survival;

/// <summary>
/// Deterministic per-chunk tree scatter for Clover Hills only.
/// Large noise picks forest vs clearing; smaller clump noise packs dense groves inside forests.
/// Runs once on chunk load (not per frame).
/// </summary>
public static class TerrainVegetationScatter
{
	public readonly struct Options
	{
		public bool Enabled { get; init; }
		public string PrefabA { get; init; }
		public string PrefabB { get; init; }
		public float PatchWavelengthMeters { get; init; }
		public float PatchThreshold01 { get; init; }
		public float CellSpacingMeters { get; init; }
		public float SpawnChanceInPatch01 { get; init; }
		public float PrefabAWeight01 { get; init; }
		public float YawJitterDegrees { get; init; }
		public float ScaleMin { get; init; }
		public float ScaleMax { get; init; }
		public int MaxTreesPerChunk { get; init; }
		public bool SkipFarLodChunks { get; init; }
	}

	static int _diagChunks;
	static bool _loggedFirstTree;

	public static void PopulateChunk(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float chunkSizeMeters,
		int verticesPerSide,
		int fullDetailVertices,
		Options options )
	{
		if ( !options.Enabled || chunkRoot is null || !chunkRoot.IsValid() )
			return;

		if ( options.SkipFarLodChunks && verticesPerSide < fullDetailVertices )
			return;

		if ( string.IsNullOrWhiteSpace( options.PrefabA ) && string.IsNullOrWhiteSpace( options.PrefabB ) )
			return;

		var pathA = NormalizePrefabPath( options.PrefabA );
		var pathB = NormalizePrefabPath( options.PrefabB );
		var hasA = PrefabPathResolves( pathA );
		var hasB = PrefabPathResolves( pathB );
		if ( !hasA && !hasB )
		{
			Log.Warning( $"[Vegetation] No tree prefabs found (A='{pathA}', B='{pathB}')." );
			return;
		}

		var chunkSize = Math.Max( 32f, chunkSizeMeters );
		// Same cell grid at every LOD — density does not change when a chunk refines.
		var cell = Math.Clamp( options.CellSpacingMeters, 4f, chunkSize * 0.5f );

		var patchWave = Math.Max( 32f, options.PatchWavelengthMeters );
		var clumpWave = Math.Max( 24f, patchWave * 0.22f );
		var patchThreshold = Math.Clamp( options.PatchThreshold01, 0.05f, 0.95f );
		var spawnChance = Math.Clamp( options.SpawnChanceInPatch01, 0.05f, 1f );
		var aWeight = Math.Clamp( options.PrefabAWeight01, 0f, 1f );
		var maxTrees = Math.Clamp( options.MaxTreesPerChunk, 1, 128 );
		var scaleMin = Math.Clamp( options.ScaleMin, 0.05f, 4f );
		var scaleMax = Math.Max( scaleMin, Math.Clamp( options.ScaleMax, 0.05f, 4f ) );
		var yawJitter = Math.Max( 0f, options.YawJitterDegrees );

		var worldRadius = settings.TotalWorldRadiusMeters;
		var chunkMinX = -worldRadius + (coord.X * chunkSize);
		var chunkMinY = -worldRadius + (coord.Y * chunkSize);
		var seed = settings.WorldSeed;
		var cells = Math.Max( 1, (int)MathF.Floor( chunkSize / cell ) );
		var spawned = 0;
		var rejectedPatch = 0;
		var rejectedChance = 0;
		var rejectedLand = 0;
		var rejectedClover = 0;
		var rejectedClone = 0;

		for ( var iy = 0; iy < cells && spawned < maxTrees; iy++ )
		{
			for ( var ix = 0; ix < cells && spawned < maxTrees; ix++ )
			{
				var jitterX = (TerrainPreviewNoise.Hash01( seed + 901, coord.X * 64 + ix, coord.Y * 64 + iy ) - 0.5f) * cell * 0.7f;
				var jitterY = (TerrainPreviewNoise.Hash01( seed + 902, coord.X * 64 + ix, coord.Y * 64 + iy ) - 0.5f) * cell * 0.7f;
				var wx = chunkMinX + ((ix + 0.5f) * cell) + jitterX;
				var wy = chunkMinY + ((iy + 0.5f) * cell) + jitterY;

				if ( !IsInsideLandDisk( settings, wx, wy ) )
				{
					rejectedLand++;
					continue;
				}

				var sample = backend.Sample( settings, wx, wy );
				if ( !sample.IsInsideWorld || !sample.IsOnLand || sample.OceanHeight01 > 0.5f )
				{
					rejectedLand++;
					continue;
				}

				// Clover Hills only — same resolver as biome display colors.
				var biome = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, wx, wy );
				if ( biome.BiomeId != TerrainPreviewBiomeId.CloverHills )
				{
					rejectedClover++;
					continue;
				}

				// Macro forest vs clearing (FBM ~0–1, mean ~0.5). Threshold ~0.48 leaves clearings
				// without wiping most of the biome; clump noise only modulates density.
				var patch = TerrainPreviewNoise.Fbm( seed + 910, wx / patchWave, wy / patchWave, 4, 2.05f, 0.5f );
				if ( patch < patchThreshold )
				{
					rejectedPatch++;
					continue;
				}

				var forestT = Math.Clamp(
					(patch - patchThreshold) / Math.Max( 0.05f, 1f - patchThreshold ),
					0f,
					1f );
				// Mild contrast: cores denser, fringe thinner — not a hard wipe.
				forestT = forestT * MathF.Sqrt( forestT );

				var clump = TerrainPreviewNoise.Fbm( seed + 915, wx / clumpWave, wy / clumpWave, 3, 2.1f, 0.5f );
				var clumpPeak = clump * clump;
				var clumpMul = 0.42f + (0.58f * clumpPeak);

				var densify = TerrainPreviewNoise.Hash01( seed + 920, coord.X * 128 + ix, coord.Y * 128 + iy );
				var localChance = spawnChance
					* Math.Clamp( 0.65f + (0.35f * forestT), 0f, 1f )
					* clumpMul;

				// Dense grove cores always fill; probability gate only thins forest fringes and clump valleys.
				var denseCore = forestT > 0.55f && clump > 0.58f;
				if ( !denseCore && densify > localChance )
				{
					rejectedChance++;
					continue;
				}

				var preferA = TerrainPreviewNoise.Hash01( seed + 930, coord.X * 256 + ix, coord.Y * 256 + iy ) < aWeight;
				var path = preferA
					? (hasA ? pathA : pathB)
					: (hasB ? pathB : pathA);

				if ( !TrySpawnTree(
					    chunkRoot,
					    path,
					    chunkMinX,
					    chunkMinY,
					    wx,
					    wy,
					    sample.HeightMeters,
					    seed,
					    coord,
					    ix,
					    iy,
					    yawJitter,
					    scaleMin,
					    scaleMax ) )
				{
					rejectedClone++;
					continue;
				}

				spawned++;
			}
		}

		_diagChunks++;
		if ( _diagChunks <= 12 || spawned > 0 )
		{
			Log.Info(
				$"[Vegetation] chunk {coord} spawned={spawned} "
				+ $"skipPatch={rejectedPatch} skipChance={rejectedChance} skipLand={rejectedLand} "
				+ $"skipClover={rejectedClover} skipClone={rejectedClone}" );
		}
	}

	static bool TrySpawnTree(
		GameObject chunkRoot,
		string path,
		float chunkMinX,
		float chunkMinY,
		float wx,
		float wy,
		float heightMeters,
		int seed,
		TerrainChunkCoord coord,
		int ix,
		int iy,
		float yawJitter,
		float scaleMin,
		float scaleMax )
	{
		var instance = ClonePrefab( path );
		if ( instance is null || !instance.IsValid() )
			return false;

		// Local to chunk — same space as TerrainMeshBuilder vertices (not WorldPosition).
		instance.NetworkMode = NetworkMode.Never;
		instance.Parent = chunkRoot;
		instance.Name = path.Contains( "propertree", StringComparison.OrdinalIgnoreCase ) ? "veg_tree_b" : "veg_tree_a";
		MakeScatterStatic( instance );

		var localMeters = new Vector3( wx - chunkMinX, wy - chunkMinY, heightMeters );
		instance.LocalPosition = TerrainWorldUnits.MetersToEngine( localMeters );

		var yaw = TerrainPreviewNoise.Hash01( seed + 940, coord.X * 512 + ix, coord.Y * 512 + iy ) * yawJitter;
		instance.LocalRotation = Rotation.FromYaw( yaw );

		// Keep each prefab's authored scale (ProperTree≈0.25, temp_tree_3≈1); only jitter a multiplier.
		var authored = instance.LocalScale;
		if ( authored.x < 0.001f || authored.y < 0.001f || authored.z < 0.001f )
			authored = Vector3.One;
		var scaleT = TerrainPreviewNoise.Hash01( seed + 950, coord.X * 1024 + ix, coord.Y * 1024 + iy );
		var mul = scaleMin + ((scaleMax - scaleMin) * scaleT);
		instance.LocalScale = authored * mul;

		instance.Enabled = true;

		if ( !_loggedFirstTree )
		{
			_loggedFirstTree = true;
			Log.Info(
				$"[Vegetation] first tree '{instance.Name}' localMeters=({localMeters.x:0.#},{localMeters.y:0.#},{localMeters.z:0.#}) "
				+ $"localUnits={instance.LocalPosition} world={instance.WorldPosition} "
				+ $"authored={authored} ×{mul:0.##} path={path}" );
		}

		return true;
	}

	static string NormalizePrefabPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return "";

		path = path.Trim().Replace( '\\', '/' );
		if ( path.StartsWith( "assets/", StringComparison.OrdinalIgnoreCase ) )
			path = path[7..];
		return path;
	}

	static bool PrefabPathResolves( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return false;

		if ( GameObject.GetPrefab( path ) is { IsValid: true } )
			return true;

		return ResourceLibrary.Get<PrefabFile>( path ) is not null;
	}

	static GameObject ClonePrefab( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return null;

		var template = GameObject.GetPrefab( path );
		if ( template is { IsValid: true } )
			return template.Clone();

		var prefabFile = ResourceLibrary.Get<PrefabFile>( path );
		if ( prefabFile is null )
			return null;

		var prefabScene = SceneUtility.GetPrefabScene( prefabFile );
		return prefabScene?.Clone();
	}

	static bool IsInsideLandDisk( TerrainPreviewSettings settings, float wx, float wy )
	{
		var r = settings.LandRadiusMeters;
		return (wx * wx) + (wy * wy) <= (r * r);
	}

	static void MakeScatterStatic( GameObject root )
	{
		foreach ( var body in root.Components.GetAll<Rigidbody>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( body is not null && body.IsValid() )
				body.Destroy();
		}

		foreach ( var prop in root.Components.GetAll<Prop>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( prop is null || !prop.IsValid() )
				continue;

			prop.IsStatic = true;
			prop.StartAsleep = true;
		}

		foreach ( var col in root.Components.GetAll<ModelCollider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( col is null || !col.IsValid() )
				continue;

			col.Static = true;
		}
	}
}
