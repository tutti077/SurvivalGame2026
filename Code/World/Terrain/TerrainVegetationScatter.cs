using Sandbox;

namespace Survival;

/// <summary>
/// Deterministic per-chunk vegetation scatter per land biome.
/// Trees: forest/clearing patches + clump noise. Clover props: sparse random clusters (rocks, sticks)
/// plus optional sticks beside large Clover trees (Prefab A).
/// Runs once on chunk load (not per frame).
/// </summary>
public static class TerrainVegetationScatter
{
	public readonly struct BiomeScatterProfile
	{
		public TerrainPreviewBiomeId BiomeId { get; init; }
		public string PrefabA { get; init; }
		public string PrefabB { get; init; }
		public float PrefabAWeight01 { get; init; }
		/// <summary>Offsets patch/clump noise per biome (same pattern, different phase).</summary>
		public int NoiseSeedSalt { get; init; }
		public string InstancePrefix { get; init; }
		/// <summary>1 = full shared density; 0.3 ≈ 30% as many trees in this biome.</summary>
		public float Density01 { get; init; }
		/// <summary>When true, patch noise only varies density instead of creating hard no-tree clearings.</summary>
		public bool IgnoreForestPatches { get; init; }
	}

	public readonly struct PropClusterOptions
	{
		public bool Enabled { get; init; }
		public TerrainPreviewBiomeId BiomeId { get; init; }
		public string Prefab { get; init; }
		public string InstancePrefix { get; init; }
		public string KindLabel { get; init; }
		public int NoiseSeedSalt { get; init; }
		public float ClusterSpacingMeters { get; init; }
		public float ClusterChance01 { get; init; }
		public int ClusterSizeMin { get; init; }
		public int ClusterSizeMax { get; init; }
		public float ClusterRadiusMeters { get; init; }
		public float ScaleMin { get; init; }
		public float ScaleMax { get; init; }
		public int MaxPerChunk { get; init; }
	}

	public readonly struct Options
	{
		public bool Enabled { get; init; }
		public BiomeScatterProfile[] Profiles { get; init; }
		public float PatchWavelengthMeters { get; init; }
		public float PatchThreshold01 { get; init; }
		public float CellSpacingMeters { get; init; }
		public float SpawnChanceInPatch01 { get; init; }
		public float YawJitterDegrees { get; init; }
		public float ScaleMin { get; init; }
		public float ScaleMax { get; init; }
		public int MaxTreesPerChunk { get; init; }
		public bool SkipFarLodChunks { get; init; }
		public PropClusterOptions[] PropClusters { get; init; }
		/// <summary>
		/// Clover Hills only: after each large tree (Prefab A), roll this chance to drop one stick
		/// in open ground beside the trunk. Prefab C (3rd tree type) is not wired yet.
		/// </summary>
		public bool NearLargeTreeSticksEnabled { get; init; }
		public string NearLargeTreeStickPrefab { get; init; }
		public float NearLargeTreeStickChance01 { get; init; }
		public float NearLargeTreeStickMinRadiusMeters { get; init; }
		public float NearLargeTreeStickMaxRadiusMeters { get; init; }
	}

	sealed class ResolvedProfile
	{
		public TerrainPreviewBiomeId BiomeId;
		public string PathA;
		public string PathB;
		public bool HasA;
		public bool HasB;
		public float AWeight;
		public int NoiseSalt;
		public string InstancePrefix;
		public float Density01;
		public bool IgnoreForestPatches;
	}

	static bool _loggedFirstTree;
	static bool _loggedFirstRock;
	static bool _loggedFirstStick;

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

		var profiles = ResolveProfiles( options.Profiles );
		var chunkSize = Math.Max( 32f, chunkSizeMeters );
		var worldRadius = settings.TotalWorldRadiusMeters;
		var chunkMinX = -worldRadius + (coord.X * chunkSize);
		var chunkMinY = -worldRadius + (coord.Y * chunkSize);
		var seed = settings.WorldSeed;

		if ( profiles.Count > 0 )
			ScatterTrees(
				chunkRoot,
				coord,
				settings,
				backend,
				chunkSize,
				chunkMinX,
				chunkMinY,
				seed,
				profiles,
				options );

		if ( options.PropClusters is null )
			return;

		foreach ( var cluster in options.PropClusters )
		{
			if ( string.Equals( cluster.KindLabel, "stick", StringComparison.OrdinalIgnoreCase ) )
			{
				ScatterPropClusters(
					chunkRoot,
					coord,
					settings,
					backend,
					chunkSize,
					chunkMinX,
					chunkMinY,
					seed,
					cluster,
					ref _loggedFirstStick );
			}
			else
			{
				ScatterPropClusters(
					chunkRoot,
					coord,
					settings,
					backend,
					chunkSize,
					chunkMinX,
					chunkMinY,
					seed,
					cluster,
					ref _loggedFirstRock );
			}
		}
	}

	static void ScatterTrees(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float chunkSize,
		float chunkMinX,
		float chunkMinY,
		int seed,
		List<ResolvedProfile> profiles,
		Options options )
	{
		// Same cell grid at every LOD — density does not change when a chunk refines.
		var cell = Math.Clamp( options.CellSpacingMeters, 4f, chunkSize * 0.5f );

		var patchWave = Math.Max( 32f, options.PatchWavelengthMeters );
		var clumpWave = Math.Max( 24f, patchWave * 0.22f );
		var patchThreshold = Math.Clamp( options.PatchThreshold01, 0.05f, 0.95f );
		var spawnChance = Math.Clamp( options.SpawnChanceInPatch01, 0.05f, 1f );
		var maxTrees = Math.Clamp( options.MaxTreesPerChunk, 1, 128 );
		var scaleMin = Math.Clamp( options.ScaleMin, 0.05f, 4f );
		var scaleMax = Math.Max( scaleMin, Math.Clamp( options.ScaleMax, 0.05f, 4f ) );
		var yawJitter = Math.Max( 0f, options.YawJitterDegrees );

		var cells = Math.Max( 1, (int)MathF.Floor( chunkSize / cell ) );
		var spawned = 0;

		for ( var iy = 0; iy < cells && spawned < maxTrees; iy++ )
		{
			for ( var ix = 0; ix < cells && spawned < maxTrees; ix++ )
			{
				var jitterX = (TerrainPreviewNoise.Hash01( seed + 901, coord.X * 64 + ix, coord.Y * 64 + iy ) - 0.5f) * cell * 0.7f;
				var jitterY = (TerrainPreviewNoise.Hash01( seed + 902, coord.X * 64 + ix, coord.Y * 64 + iy ) - 0.5f) * cell * 0.7f;
				var wx = chunkMinX + ((ix + 0.5f) * cell) + jitterX;
				var wy = chunkMinY + ((iy + 0.5f) * cell) + jitterY;

				if ( !IsInsideLandDisk( settings, wx, wy ) )
					continue;

				var sample = backend.Sample( settings, wx, wy );
				if ( !sample.IsInsideWorld || !sample.IsOnLand || sample.OceanHeight01 > 0.5f )
					continue;

				var biome = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, wx, wy );
				var profile = FindProfile( profiles, biome.BiomeId );
				if ( profile is null )
					continue;

				var noiseSeed = seed + profile.NoiseSalt;

				var patch = TerrainPreviewNoise.Fbm( noiseSeed + 910, wx / patchWave, wy / patchWave, 4, 2.05f, 0.5f );
				if ( !profile.IgnoreForestPatches && patch < patchThreshold )
					continue;

				var forestT = profile.IgnoreForestPatches
					? Math.Clamp( patch, 0f, 1f )
					: Math.Clamp(
						(patch - patchThreshold) / Math.Max( 0.05f, 1f - patchThreshold ),
						0f,
						1f );
				forestT = forestT * MathF.Sqrt( forestT );

				var clump = TerrainPreviewNoise.Fbm( noiseSeed + 915, wx / clumpWave, wy / clumpWave, 3, 2.1f, 0.5f );
				var clumpPeak = clump * clump;
				var clumpMul = 0.42f + (0.58f * clumpPeak);

				var densify = TerrainPreviewNoise.Hash01( noiseSeed + 920, coord.X * 128 + ix, coord.Y * 128 + iy );
				var density = Math.Clamp( profile.Density01, 0.05f, 1f );
				var localChance = spawnChance
					* Math.Clamp( 0.65f + (0.35f * forestT), 0f, 1f )
					* clumpMul
					* density;

				// Dense grove cores skip the chance roll only at full density; thinned biomes always roll.
				var denseCore = !profile.IgnoreForestPatches && density >= 0.999f && forestT > 0.55f && clump > 0.58f;
				if ( !denseCore && densify > localChance )
					continue;

				var preferA = TerrainPreviewNoise.Hash01( noiseSeed + 930, coord.X * 256 + ix, coord.Y * 256 + iy ) < profile.AWeight;
				var path = preferA
					? (profile.HasA ? profile.PathA : profile.PathB)
					: (profile.HasB ? profile.PathB : profile.PathA);
				var variant = preferA && profile.HasA ? "a" : "b";

				if ( !TrySpawnInstance(
					    chunkRoot,
					    path,
					    profile.InstancePrefix,
					    variant,
					    chunkMinX,
					    chunkMinY,
					    wx,
					    wy,
					    sample.HeightMeters,
					    noiseSeed,
					    coord,
					    ix,
					    iy,
					    0,
					    yawJitter,
					    scaleMin,
					    scaleMax,
					    ref _loggedFirstTree,
					    "tree" ) )
					continue;

				spawned++;

				// Clover Prefab A = large tree for now (Prefab C / 3rd type later).
				if ( profile.BiomeId == TerrainPreviewBiomeId.CloverHills
				     && string.Equals( variant, "a", StringComparison.Ordinal )
				     && options.NearLargeTreeSticksEnabled )
				{
					TrySpawnStickNearLargeTree(
						chunkRoot,
						coord,
						settings,
						backend,
						chunkMinX,
						chunkMinY,
						wx,
						wy,
						noiseSeed,
						ix,
						iy,
						options );
				}
			}
		}
	}

	/// <summary>
	/// ~5% (tunable) chance: one stick on open ground in a ring around a large Clover tree.
	/// </summary>
	static void TrySpawnStickNearLargeTree(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float chunkMinX,
		float chunkMinY,
		float treeWx,
		float treeWy,
		int noiseSeed,
		int ix,
		int iy,
		Options options )
	{
		var stickPath = NormalizePrefabPath( options.NearLargeTreeStickPrefab );
		if ( !PrefabPathResolves( stickPath ) )
			return;

		var chance = Math.Clamp( options.NearLargeTreeStickChance01, 0f, 1f );
		if ( chance <= 1e-6f )
			return;

		var roll = TerrainPreviewNoise.Hash01( noiseSeed + 960, coord.X * 384 + ix, coord.Y * 384 + iy );
		if ( roll > chance )
			return;

		var minR = Math.Clamp( options.NearLargeTreeStickMinRadiusMeters, 1f, 24f );
		var maxR = Math.Max( minR + 0.25f, Math.Clamp( options.NearLargeTreeStickMaxRadiusMeters, 1.5f, 32f ) );
		var angle01 = TerrainPreviewNoise.Hash01( noiseSeed + 961, coord.X * 400 + ix, coord.Y * 400 + iy );
		var radius01 = TerrainPreviewNoise.Hash01( noiseSeed + 962, coord.X * 416 + ix, coord.Y * 416 + iy );
		var yaw = angle01 * MathF.Tau;
		var radius = minR + ((maxR - minR) * radius01);
		var stickWx = treeWx + (MathF.Cos( yaw ) * radius);
		var stickWy = treeWy + (MathF.Sin( yaw ) * radius);

		if ( !IsInsideLandDisk( settings, stickWx, stickWy ) )
			return;

		var sample = backend.Sample( settings, stickWx, stickWy );
		if ( !sample.IsInsideWorld || !sample.IsOnLand || sample.OceanHeight01 > 0.5f )
			return;

		var biome = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, stickWx, stickWy );
		if ( biome.BiomeId != TerrainPreviewBiomeId.CloverHills )
			return;

		TrySpawnInstance(
			chunkRoot,
			stickPath,
			"veg_stick_near",
			"a",
			chunkMinX,
			chunkMinY,
			stickWx,
			stickWy,
			sample.HeightMeters,
			noiseSeed,
			coord,
			ix,
			iy,
			1,
			360f,
			0.85f,
			1.15f,
			ref _loggedFirstStick,
			"stick" );
	}

	static void ScatterPropClusters(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float chunkSize,
		float chunkMinX,
		float chunkMinY,
		int seed,
		PropClusterOptions props,
		ref bool loggedFirst )
	{
		if ( !props.Enabled )
			return;

		var path = NormalizePrefabPath( props.Prefab );
		if ( !PrefabPathResolves( path ) )
		{
			Log.Warning( $"[Vegetation] {props.KindLabel} prefab missing ('{path}')." );
			return;
		}

		var cell = Math.Clamp( props.ClusterSpacingMeters, 8f, Math.Max( 8f, chunkSize ) );
		var chance = Math.Clamp( props.ClusterChance01, 0.05f, 1f );
		var sizeMin = Math.Clamp( props.ClusterSizeMin, 1, 8 );
		var sizeMax = Math.Max( sizeMin, Math.Clamp( props.ClusterSizeMax, 1, 8 ) );
		var radius = Math.Clamp( props.ClusterRadiusMeters, 0.25f, cell * 0.45f );
		var scaleMin = Math.Clamp( props.ScaleMin, 0.05f, 4f );
		var scaleMax = Math.Max( scaleMin, Math.Clamp( props.ScaleMax, 0.05f, 4f ) );
		var maxPerChunk = Math.Clamp( props.MaxPerChunk, 1, 96 );
		var cells = Math.Max( 1, (int)MathF.Floor( chunkSize / cell ) );
		var noiseSeed = seed + props.NoiseSeedSalt;
		var prefix = string.IsNullOrWhiteSpace( props.InstancePrefix ) ? "veg_prop" : props.InstancePrefix.Trim();
		var kind = string.IsNullOrWhiteSpace( props.KindLabel ) ? "prop" : props.KindLabel.Trim();
		var spawned = 0;

		for ( var iy = 0; iy < cells && spawned < maxPerChunk; iy++ )
		{
			for ( var ix = 0; ix < cells && spawned < maxPerChunk; ix++ )
			{
				// Strong jitter so cluster centers don't read as a grid.
				var jitterX = (TerrainPreviewNoise.Hash01( noiseSeed + 10, coord.X * 48 + ix, coord.Y * 48 + iy ) - 0.5f) * cell * 0.85f;
				var jitterY = (TerrainPreviewNoise.Hash01( noiseSeed + 11, coord.X * 48 + ix, coord.Y * 48 + iy ) - 0.5f) * cell * 0.85f;
				var cx = chunkMinX + ((ix + 0.5f) * cell) + jitterX;
				var cy = chunkMinY + ((iy + 0.5f) * cell) + jitterY;

				if ( !IsInsideLandDisk( settings, cx, cy ) )
					continue;

				var centerSample = backend.Sample( settings, cx, cy );
				if ( !centerSample.IsInsideWorld || !centerSample.IsOnLand || centerSample.OceanHeight01 > 0.5f )
					continue;

				var biome = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, centerSample, cx, cy );
				if ( biome.BiomeId != props.BiomeId )
					continue;

				var roll = TerrainPreviewNoise.Hash01( noiseSeed + 20, coord.X * 96 + ix, coord.Y * 96 + iy );
				if ( roll > chance )
					continue;

				var countRoll = TerrainPreviewNoise.Hash01( noiseSeed + 30, coord.X * 112 + ix, coord.Y * 112 + iy );
				var count = Math.Clamp( sizeMin + (int)(countRoll * (sizeMax - sizeMin + 1)), sizeMin, sizeMax );

				for ( var r = 0; r < count && spawned < maxPerChunk; r++ )
				{
					var ang = TerrainPreviewNoise.Hash01( noiseSeed + 40 + r, coord.X * 160 + ix, coord.Y * 160 + iy ) * MathF.PI * 2f;
					var distT = TerrainPreviewNoise.Hash01( noiseSeed + 50 + r, coord.X * 176 + ix, coord.Y * 176 + iy );
					// Keep at least one near the center; others fan out inside the cluster radius.
					var dist = r == 0 ? distT * radius * 0.25f : (0.25f + (0.75f * distT)) * radius;
					var wx = cx + (MathF.Cos( ang ) * dist);
					var wy = cy + (MathF.Sin( ang ) * dist);

					if ( !IsInsideLandDisk( settings, wx, wy ) )
						continue;

					var sample = backend.Sample( settings, wx, wy );
					if ( !sample.IsInsideWorld || !sample.IsOnLand || sample.OceanHeight01 > 0.5f )
						continue;

					var memberBiome = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, wx, wy );
					if ( memberBiome.BiomeId != props.BiomeId )
						continue;

					if ( !TrySpawnInstance(
						    chunkRoot,
						    path,
						    prefix,
						    "a",
						    chunkMinX,
						    chunkMinY,
						    wx,
						    wy,
						    sample.HeightMeters,
						    noiseSeed,
						    coord,
						    ix,
						    iy,
						    r + 1,
						    360f,
						    scaleMin,
						    scaleMax,
						    ref loggedFirst,
						    kind ) )
						continue;

					spawned++;
				}
			}
		}
	}

	static List<ResolvedProfile> ResolveProfiles( BiomeScatterProfile[] profiles )
	{
		var resolved = new List<ResolvedProfile>();
		if ( profiles is null || profiles.Length == 0 )
			return resolved;

		foreach ( var profile in profiles )
		{
			var pathA = NormalizePrefabPath( profile.PrefabA );
			var pathB = NormalizePrefabPath( profile.PrefabB );
			var hasA = PrefabPathResolves( pathA );
			var hasB = PrefabPathResolves( pathB );
			if ( !hasA && !hasB )
			{
				Log.Warning( $"[Vegetation] No prefabs for biome {profile.BiomeId} (A='{pathA}', B='{pathB}')." );
				continue;
			}

			resolved.Add( new ResolvedProfile
			{
				BiomeId = profile.BiomeId,
				PathA = pathA,
				PathB = pathB,
				HasA = hasA,
				HasB = hasB,
				AWeight = Math.Clamp( profile.PrefabAWeight01, 0f, 1f ),
				NoiseSalt = profile.NoiseSeedSalt,
				InstancePrefix = string.IsNullOrWhiteSpace( profile.InstancePrefix )
					? "veg_tree"
					: profile.InstancePrefix.Trim(),
				// Default 1 when unset (struct default 0 would otherwise clamp to the floor).
				Density01 = profile.Density01 > 0f ? profile.Density01 : 1f,
				IgnoreForestPatches = profile.IgnoreForestPatches,
			} );
		}

		return resolved;
	}

	static ResolvedProfile FindProfile( List<ResolvedProfile> profiles, TerrainPreviewBiomeId biomeId )
	{
		foreach ( var profile in profiles )
		{
			if ( profile.BiomeId == biomeId )
				return profile;
		}

		return null;
	}

	static bool TrySpawnInstance(
		GameObject chunkRoot,
		string path,
		string instancePrefix,
		string variant,
		float chunkMinX,
		float chunkMinY,
		float wx,
		float wy,
		float heightMeters,
		int seed,
		TerrainChunkCoord coord,
		int ix,
		int iy,
		int memberIndex,
		float yawJitter,
		float scaleMin,
		float scaleMax,
		ref bool loggedFirst,
		string kindLabel )
	{
		_ = loggedFirst;
		_ = kindLabel;

		var instance = ClonePrefab( path );
		if ( instance is null || !instance.IsValid() )
			return false;

		instance.NetworkMode = NetworkMode.Never;
		instance.Parent = chunkRoot;
		instance.Name = memberIndex > 0
			? $"{instancePrefix}_{variant}_{memberIndex}"
			: $"{instancePrefix}_{variant}";
		MakeScatterStatic( instance );

		var localMeters = new Vector3( wx - chunkMinX, wy - chunkMinY, heightMeters );
		instance.LocalPosition = TerrainWorldUnits.MetersToEngine( localMeters );

		var yaw = TerrainPreviewNoise.Hash01(
			seed + 940 + memberIndex,
			coord.X * 512 + ix,
			coord.Y * 512 + iy ) * yawJitter;
		instance.LocalRotation = Rotation.FromYaw( yaw );

		var authored = instance.LocalScale;
		if ( authored.x < 0.001f || authored.y < 0.001f || authored.z < 0.001f )
			authored = Vector3.One;
		var scaleT = TerrainPreviewNoise.Hash01(
			seed + 950 + memberIndex,
			coord.X * 1024 + ix,
			coord.Y * 1024 + iy );
		var mul = scaleMin + ((scaleMax - scaleMin) * scaleT);
		instance.LocalScale = authored * mul;

		instance.Enabled = true;

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
