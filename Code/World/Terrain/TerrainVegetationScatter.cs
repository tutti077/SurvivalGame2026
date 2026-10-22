using Sandbox;

namespace Survival;

/// <summary>
/// Deterministic per-chunk vegetation scatter per land biome.
/// Trees: forest/clearing patches + clump noise, one prefab picked at random from the biome's list.
/// Small trees (saplings): optional second layer on its own grid, biased toward forest edges and kept
/// clear of the large trunks; full-detail chunks only (<see cref="PopulateSmallTrees"/>). Trees lean with the ground slope (partial influence, capped).
/// Clover props: sparse random clusters (rocks, sticks) plus optional sticks beside large Clover trees.
/// Runs once on chunk load (not per frame).
/// </summary>
public static class TerrainVegetationScatter
{
	/// <summary>One planned scatter instance: everything a clone needs, computed at chunk load, spawned later under a budget.</summary>
	public readonly struct SpawnPlan
	{
		public string Path { get; init; }
		public string Name { get; init; }
		public Vector3 LocalPosition { get; init; }
		public Rotation LocalRotation { get; init; }
		public Vector3 LocalScale { get; init; }
		public string StableKey { get; init; }
		/// <summary>Small-tree layer: spawned hidden, shown by TerrainWorldManager.TickSaplingVisibility within Sapling Visible Range (m).</summary>
		public bool Sapling { get; init; }
		/// <summary>"tree" (large, LOD-switched, never fades), "sapling", "stick", "rock" … (no LODs: fade in / out).</summary>
		public string Kind { get; init; }
	}

	// The plan list the populate calls write into (main thread only). Null = spawn immediately (legacy callers).
	static List<SpawnPlan> _plans;
	static readonly Dictionary<string, Vector3> _authoredScale = new();

	/// <summary>
	/// Clone and place a planned instance. Main thread; called by the manager's budgeted spawner.
	/// <paramref name="collidersEnabled"/> is the chunk's collision state: a tree's mesh collider (the elm's is the
	/// 4k-triangle wood) is switched off BEFORE the clone is enabled when the chunk is out of collision range, so the
	/// physics shape is never built for a tree 400 m away only to be disabled a few frames later (single elm clones
	/// cost 6-13 ms that way, perf_watch 2026-10-15).
	/// </summary>
	public static GameObject SpawnPlanned( GameObject chunkRoot, in SpawnPlan plan, bool collidersEnabled = true )
	{
		if ( chunkRoot is null || !chunkRoot.IsValid() )
			return null;

		var instance = ClonePrefab( plan.Path );
		if ( instance is null || !instance.IsValid() )
			return null;

		instance.NetworkMode = NetworkMode.Never;
		instance.Flags |= GameObjectFlags.NotSaved;
		instance.Parent = chunkRoot;
		instance.Name = plan.Name;
		MakeScatterStatic( instance );
		instance.LocalPosition = plan.LocalPosition;
		instance.LocalRotation = plan.LocalRotation;
		instance.LocalScale = plan.LocalScale;
		if ( !collidersEnabled )
		{
			foreach ( var col in instance.Components.GetAll<ModelCollider>( FindMode.EverythingInSelfAndDescendants ) )
				col.Enabled = false;
		}
		instance.Enabled = !plan.Sapling;   // saplings wait for the 50 m visibility tick (no flash at chunk promotion distance)

		var identity = instance.Components.Get<WorldScatterIdentity>()
			?? instance.Components.Create<WorldScatterIdentity>();
		identity.Configure( plan.StableKey );
		WorldScatterIdentity.ReapplyRemembered( identity );   // a felled tree comes back as its stump, a harvested node empty
		return instance;
	}

	static Vector3 AuthoredScale( string path )
	{
		if ( _authoredScale.TryGetValue( path, out var cached ) )
			return cached;

		var scale = Vector3.One;
		var template = GameObject.GetPrefab( path );
		if ( template is { IsValid: true } )
		{
			scale = template.LocalScale;
			if ( scale.x < 0.001f || scale.y < 0.001f || scale.z < 0.001f )
				scale = Vector3.One;
		}

		_authoredScale[path] = scale;
		return scale;
	}

	public readonly struct BiomeScatterProfile
	{
		public TerrainPreviewBiomeId BiomeId { get; init; }
		/// <summary>Large-tree prefabs; each tree picks one uniformly at random.</summary>
		public string[] Prefabs { get; init; }
		/// <summary>Offsets patch/clump noise per biome (same pattern, different phase).</summary>
		public int NoiseSeedSalt { get; init; }
		public string InstancePrefix { get; init; }
		/// <summary>1 = full shared density; 0.3 ≈ 30% as many trees in this biome.</summary>
		public float Density01 { get; init; }
		/// <summary>When true, patch noise only varies density instead of creating hard no-tree clearings.</summary>
		public bool IgnoreForestPatches { get; init; }
		/// <summary>Small-tree (sapling) prefabs; empty = no small-tree layer in this biome.</summary>
		public string[] SmallPrefabs { get; init; }
		public float SmallCellSpacingMeters { get; init; }
		public float SmallChance01 { get; init; }
		/// <summary>0 = saplings anywhere in the biome; 1 = only along forest edges (patch-noise threshold).</summary>
		/// <summary>Saplings roll SmallChance01 only within this of a large trunk (and outside the clearance).</summary>
		public float SmallNearTrunkMeters { get; init; }
		/// <summary>Per-cell chance away from every large trunk — the odd lone sapling in a clearing.</summary>
		public float SmallClearingChance01 { get; init; }
		/// <summary>No sapling closer than this to a large trunk spawned in the same chunk.</summary>
		public float SmallTrunkClearanceMeters { get; init; }
		public float SmallScaleMin { get; init; }
		public float SmallScaleMax { get; init; }
		public int SmallMaxPerChunk { get; init; }
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
		/// <summary>Fraction of the ground slope a tree leans with (0 = always upright, 1 = perpendicular to the ground).</summary>
		public float SlopeTiltInfluence01 { get; init; }
		/// <summary>Hard cap on the lean, degrees from vertical.</summary>
		public float SlopeTiltMaxDegrees { get; init; }
		/// <summary>
		/// Clover Hills only: after each large tree, roll this chance to drop one stick
		/// in open ground beside the trunk.
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
		public string[] Paths;
		public string[] SmallPaths;
		public int NoiseSalt;
		public string InstancePrefix;
		public float Density01;
		public bool IgnoreForestPatches;
		public BiomeScatterProfile Source;
	}

	/// <summary>Finite-difference step for the slope normal under a tree.</summary>
	const float SlopeProbeMeters = 2f;

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
		Options options,
		List<Vector2> trunks,
		List<SpawnPlan> plans = null,
		bool largeTrees = true,
		bool props = true )
	{
		if ( !options.Enabled || chunkRoot is null || !chunkRoot.IsValid() )
			return;

		if ( options.SkipFarLodChunks && verticesPerSide < fullDetailVertices )
			return;

		_plans = plans;
		try
		{
			PopulateChunkCore( chunkRoot, coord, settings, backend, chunkSizeMeters, options, trunks, largeTrees, props );
		}
		finally
		{
			_plans = null;
		}
	}

	static void PopulateChunkCore(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float chunkSizeMeters,
		Options options,
		List<Vector2> trunks,
		bool largeTrees,
		bool props )
	{
		var profiles = ResolveProfiles( options.Profiles );
		var chunkSize = Math.Max( 32f, chunkSizeMeters );
		var worldRadius = settings.TotalWorldRadiusMeters;
		var chunkMinX = -worldRadius + (coord.X * chunkSize);
		var chunkMinY = -worldRadius + (coord.Y * chunkSize);
		var seed = settings.WorldSeed;

		if ( largeTrees && profiles.Count > 0 )
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
				options,
				trunks ?? [] );

		if ( !props || options.PropClusters is null )
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
		Options options,
		List<Vector2> trunks )
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
				if ( !TryPlanTree( settings, backend, coord, chunkMinX, chunkMinY, cell, ix, iy, seed, profiles, patchWave, clumpWave, patchThreshold, spawnChance, out var plan ) )
					continue;

				var wx = plan.X;
				var wy = plan.Y;
				var sample = plan.Sample;
				var profile = plan.Profile;
				var noiseSeed = plan.NoiseSeed;
				var pick = plan.Pick;
				var path = profile.Paths[pick];
				var variant = pick.ToString();
				var tilt = SlopeTilt( settings, backend, wx, wy, sample.HeightMeters, options );

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
					    tilt,
					    ref _loggedFirstTree,
					    "tree" ) )
					continue;

				spawned++;
				trunks.Add( new Vector2( wx, wy ) );

				if ( profile.BiomeId == TerrainPreviewBiomeId.CloverHills
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
	/// Small-tree (sapling) layer for one chunk. The manager runs it once the chunk is inside sapling reach
	/// (Sapling Visible Range + ¾ chunk, large trees planned), whatever the chunk's mesh detail.
	/// <paramref name="trunks"/> = large-tree positions from <see cref="PopulateChunk"/> (clearance).
	/// </summary>
	public static void PopulateSmallTrees(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float chunkSizeMeters,
		Options options,
		List<Vector2> trunks,
		List<SpawnPlan> plans = null )
	{
		if ( !options.Enabled || chunkRoot is null || !chunkRoot.IsValid() )
			return;

		var chunkSize = Math.Max( 32f, chunkSizeMeters );
		var worldRadius = settings.TotalWorldRadiusMeters;
		var chunkMinX = -worldRadius + (coord.X * chunkSize);
		var chunkMinY = -worldRadius + (coord.Y * chunkSize);
		_plans = plans;
		try
		{
			foreach ( var profile in ResolveProfiles( options.Profiles ) )
			{
				if ( profile.SmallPaths.Length > 0 )
					ScatterSmallTrees( chunkRoot, coord, settings, backend, chunkSize, chunkMinX, chunkMinY, settings.WorldSeed, profile, trunks ?? [], options );
			}
		}
		finally
		{
			_plans = null;
		}
	}

	/// <summary>
	/// Small-tree layer: its own jittered grid. Saplings cluster under the big elms — the full chance only
	/// within <see cref="BiomeScatterProfile.SmallNearTrunkMeters"/> of a large trunk placed in this chunk,
	/// <see cref="BiomeScatterProfile.SmallClearingChance01"/> anywhere else — and never inside the trunk
	/// clearance (one list scan per candidate, chunk load only).
	/// </summary>
	static void ScatterSmallTrees(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float chunkSize,
		float chunkMinX,
		float chunkMinY,
		int seed,
		ResolvedProfile profile,
		List<Vector2> trunks,
		Options options )
	{
		var src = profile.Source;
		var cell = Math.Clamp( src.SmallCellSpacingMeters, 2f, chunkSize * 0.5f );
		var chance = Math.Clamp( src.SmallChance01, 0f, 1f );
		var clearingChance = Math.Clamp( src.SmallClearingChance01, 0f, 1f );
		if ( chance <= 1e-6f && clearingChance <= 1e-6f )
			return;

		// Saplings cluster under the big elms (Mark 2026-10-13): the full chance only inside Near Trunk of a large
		// trunk (this chunk's trunks — the edge of a neighbour's grove is the one blind spot), a small chance anywhere else.
		var nearTrunk = Math.Max( 0f, src.SmallNearTrunkMeters );
		var nearTrunkSq = nearTrunk * nearTrunk;
		var clearance = Math.Max( 0f, src.SmallTrunkClearanceMeters );
		var clearanceSq = clearance * clearance;
		var maxSmall = Math.Clamp( src.SmallMaxPerChunk, 1, 256 );
		var scaleMin = Math.Clamp( src.SmallScaleMin, 0.05f, 4f );
		var scaleMax = Math.Max( scaleMin, Math.Clamp( src.SmallScaleMax, 0.05f, 4f ) );
		var noiseSeed = seed + profile.NoiseSalt;
		var cells = Math.Max( 1, (int)MathF.Floor( chunkSize / cell ) );
		var prefix = profile.InstancePrefix + "_small";
		var spawned = 0;

		for ( var iy = 0; iy < cells && spawned < maxSmall; iy++ )
		{
			for ( var ix = 0; ix < cells && spawned < maxSmall; ix++ )
			{
				var jitterX = (TerrainPreviewNoise.Hash01( noiseSeed + 1901, coord.X * 64 + ix, coord.Y * 64 + iy ) - 0.5f) * cell * 0.8f;
				var jitterY = (TerrainPreviewNoise.Hash01( noiseSeed + 1902, coord.X * 64 + ix, coord.Y * 64 + iy ) - 0.5f) * cell * 0.8f;
				var wx = chunkMinX + ((ix + 0.5f) * cell) + jitterX;
				var wy = chunkMinY + ((iy + 0.5f) * cell) + jitterY;

				if ( !IsInsideLandDisk( settings, wx, wy ) )
					continue;

				// Cheap rolls before the terrain sample.
				if ( clearanceSq > 0f && NearAnyTrunk( trunks, wx, wy, clearanceSq ) )
					continue;

				var localChance = nearTrunkSq > 0f && NearAnyTrunk( trunks, wx, wy, nearTrunkSq ) ? chance : clearingChance;
				if ( TerrainPreviewNoise.Hash01( noiseSeed + 1920, coord.X * 128 + ix, coord.Y * 128 + iy ) > localChance )
					continue;

				var sample = backend.Sample( settings, wx, wy );
				if ( !sample.IsInsideWorld || !sample.IsOnLand || sample.OceanHeight01 > 0.5f )
					continue;

				var biome = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, wx, wy );
				if ( biome.BiomeId != profile.BiomeId )
					continue;

				var pick = PickIndex( profile.SmallPaths.Length, TerrainPreviewNoise.Hash01( noiseSeed + 1930, coord.X * 256 + ix, coord.Y * 256 + iy ) );
				var tilt = SlopeTilt( settings, backend, wx, wy, sample.HeightMeters, options );

				if ( !TrySpawnInstance(
					    chunkRoot,
					    profile.SmallPaths[pick],
					    prefix,
					    pick.ToString(),
					    chunkMinX,
					    chunkMinY,
					    wx,
					    wy,
					    sample.HeightMeters,
					    noiseSeed + 1000,
					    coord,
					    ix,
					    iy,
					    0,
					    360f,
					    scaleMin,
					    scaleMax,
					    tilt,
					    ref _loggedFirstTree,
					    "sapling" ) )
					continue;

				spawned++;
			}
		}
	}

	/// <summary>One large tree the deterministic grid decided to put down: shared by the real scatter and the far card ring.</summary>
	readonly struct TreePlan
	{
		public float X { get; init; }
		public float Y { get; init; }
		public TerrainPreviewSample Sample { get; init; }
		public ResolvedProfile Profile { get; init; }
		public int NoiseSeed { get; init; }
		public int Pick { get; init; }
	}

	/// <summary>
	/// The per-cell decision of <see cref="ScatterTrees"/> (jitter, land test, biome profile, forest patch, clump,
	/// chance roll, variant pick) with nothing spawned — pure, seeded, thread-safe, so the far tree cards
	/// (TerrainWorldManager.FarTrees.cs) stand exactly where the real trees will.
	/// </summary>
	static bool TryPlanTree(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		TerrainChunkCoord coord,
		float chunkMinX,
		float chunkMinY,
		float cell,
		int ix,
		int iy,
		int seed,
		List<ResolvedProfile> profiles,
		float patchWave,
		float clumpWave,
		float patchThreshold,
		float spawnChance,
		out TreePlan plan )
	{
		plan = default;
		var jitterX = (TerrainPreviewNoise.Hash01( seed + 901, coord.X * 64 + ix, coord.Y * 64 + iy ) - 0.5f) * cell * 0.7f;
		var jitterY = (TerrainPreviewNoise.Hash01( seed + 902, coord.X * 64 + ix, coord.Y * 64 + iy ) - 0.5f) * cell * 0.7f;
		var wx = chunkMinX + ((ix + 0.5f) * cell) + jitterX;
		var wy = chunkMinY + ((iy + 0.5f) * cell) + jitterY;

		if ( !IsInsideLandDisk( settings, wx, wy ) )
			return false;

		var sample = backend.Sample( settings, wx, wy );
		if ( !sample.IsInsideWorld || !sample.IsOnLand || sample.OceanHeight01 > 0.5f )
			return false;

		var biome = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, wx, wy );
		var profile = FindProfile( profiles, biome.BiomeId );
		if ( profile is null )
			return false;

		var noiseSeed = seed + profile.NoiseSalt;

		var patch = TerrainPreviewNoise.Fbm( noiseSeed + 910, wx / patchWave, wy / patchWave, 4, 2.05f, 0.5f );
		if ( !profile.IgnoreForestPatches && patch < patchThreshold )
			return false;

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
			return false;

		var pick = PickIndex( profile.Paths.Length, TerrainPreviewNoise.Hash01( noiseSeed + 930, coord.X * 256 + ix, coord.Y * 256 + iy ) );
		plan = new TreePlan { X = wx, Y = wy, Sample = sample, Profile = profile, NoiseSeed = noiseSeed, Pick = pick };
		return true;
	}

	/// <summary>
	/// Forest cover 0-1 at a point for the far canopy tint: the same forest-patch noise and threshold the tree
	/// planner uses, times the biome's density, with the clump term averaged out. 0 where this biome plants
	/// nothing. Thread-safe (noise only).
	/// </summary>
	public static float SampleForestCover01( TerrainPreviewSettings settings, Options options, TerrainPreviewBiomeId biome, float wx, float wy )
	{
		if ( !options.Enabled || options.Profiles is null )
			return 0f;

		var profile = FindProfile( ResolveProfiles( options.Profiles ), biome );
		if ( profile is null )
			return 0f;

		var patchWave = Math.Max( 32f, options.PatchWavelengthMeters );
		var patchThreshold = Math.Clamp( options.PatchThreshold01, 0.05f, 0.95f );
		var noiseSeed = settings.WorldSeed + profile.NoiseSalt;
		var patch = TerrainPreviewNoise.Fbm( noiseSeed + 910, wx / patchWave, wy / patchWave, 4, 2.05f, 0.5f );
		if ( !profile.IgnoreForestPatches && patch < patchThreshold )
			return 0f;

		var forestT = profile.IgnoreForestPatches
			? Math.Clamp( patch, 0f, 1f )
			: Math.Clamp( (patch - patchThreshold) / Math.Max( 0.05f, 1f - patchThreshold ), 0f, 1f );
		forestT = forestT * MathF.Sqrt( forestT );
		return Math.Clamp( (0.35f + (0.65f * forestT)) * Math.Clamp( profile.Density01, 0.05f, 1f ), 0f, 1f );
	}

	/// <summary>A planned large tree for the far card ring: world metres, ground height, the real tree's scale multiplier and yaw.</summary>
	public readonly struct FarTree
	{
		public float X { get; init; }
		public float Y { get; init; }
		public float HeightMeters { get; init; }
		public float ScaleMul { get; init; }
		public float YawDegrees { get; init; }
		/// <summary>The prefab the real scatter would clone here — the far ring draws its model's _far sibling.</summary>
		public string PrefabPath { get; init; }
	}

	/// <summary>
	/// Every large tree the real scatter would put on this chunk, without spawning anything. Same grid, same
	/// hashes, same chance rolls as <see cref="ScatterTrees"/>, so a card and the tree that later replaces it
	/// share position, size and heading. Thread-safe (pipeline samples + hashes only).
	/// </summary>
	public static void EnumerateTrees(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		TerrainChunkCoord coord,
		float chunkSizeMeters,
		Options options,
		List<FarTree> result )
	{
		if ( !options.Enabled || options.Profiles is null )
			return;

		var profiles = ResolveProfiles( options.Profiles );
		if ( profiles.Count == 0 )
			return;

		var chunkSize = Math.Max( 32f, chunkSizeMeters );
		var worldRadius = settings.TotalWorldRadiusMeters;
		var chunkMinX = -worldRadius + (coord.X * chunkSize);
		var chunkMinY = -worldRadius + (coord.Y * chunkSize);
		var seed = settings.WorldSeed;
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
		var count = 0;

		for ( var iy = 0; iy < cells && count < maxTrees; iy++ )
		{
			for ( var ix = 0; ix < cells && count < maxTrees; ix++ )
			{
				if ( !TryPlanTree( settings, backend, coord, chunkMinX, chunkMinY, cell, ix, iy, seed, profiles, patchWave, clumpWave, patchThreshold, spawnChance, out var plan ) )
					continue;

				count++;
				var yaw = TerrainPreviewNoise.Hash01( plan.NoiseSeed + 940, coord.X * 512 + ix, coord.Y * 512 + iy ) * yawJitter;
				var scaleT = TerrainPreviewNoise.Hash01( plan.NoiseSeed + 950, coord.X * 1024 + ix, coord.Y * 1024 + iy );
				result.Add( new FarTree
				{
					X = plan.X,
					Y = plan.Y,
					HeightMeters = plan.Sample.HeightMeters,
					ScaleMul = scaleMin + ((scaleMax - scaleMin) * scaleT),
					YawDegrees = yaw,
					PrefabPath = plan.Profile.Paths[plan.Pick],
				} );
			}
		}
	}

	static bool NearAnyTrunk( List<Vector2> trunks, float wx, float wy, float radiusSq )
	{
		for ( var i = 0; i < trunks.Count; i++ )
		{
			var dx = trunks[i].x - wx;
			var dy = trunks[i].y - wy;
			if ( (dx * dx) + (dy * dy) < radiusSq )
				return true;
		}

		return false;
	}

	static int PickIndex( int count, float hash01 )
		=> Math.Clamp( (int)(hash01 * count), 0, count - 1 );

	/// <summary>
	/// Tilted "up" for a tree at (wx, wy): the ground normal from two forward-difference samples,
	/// leaned toward by <see cref="Options.SlopeTiltInfluence01"/> of the slope angle, capped at
	/// <see cref="Options.SlopeTiltMaxDegrees"/>. Returns <see cref="Vector3.Up"/> when disabled or flat.
	/// </summary>
	static Vector3 SlopeTilt(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float wx,
		float wy,
		float heightMeters,
		Options options )
	{
		var influence = Math.Clamp( options.SlopeTiltInfluence01, 0f, 1f );
		var maxDeg = Math.Clamp( options.SlopeTiltMaxDegrees, 0f, 45f );
		if ( influence <= 1e-4f || maxDeg <= 1e-3f )
			return Vector3.Up;

		var hx = backend.Sample( settings, wx + SlopeProbeMeters, wy ).HeightMeters;
		var hy = backend.Sample( settings, wx, wy + SlopeProbeMeters ).HeightMeters;
		var downhill = new Vector3( -(hx - heightMeters) / SlopeProbeMeters, -(hy - heightMeters) / SlopeProbeMeters, 0f );
		var gradient = downhill.Length;
		if ( gradient < 1e-4f )
			return Vector3.Up;

		var slopeDeg = MathF.Atan( gradient ) * (180f / MathF.PI);
		var tiltRad = Math.Min( slopeDeg * influence, maxDeg ) * (MathF.PI / 180f);
		// Leaning toward the ground normal = leaning downhill.
		return (Vector3.Up * MathF.Cos( tiltRad )) + (downhill / gradient * MathF.Sin( tiltRad ));
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
		// Roll first: the prefab lookup is the expensive part and 95 % of trees never need it.
		var chance = Math.Clamp( options.NearLargeTreeStickChance01, 0f, 1f );
		if ( chance <= 1e-6f )
			return;
		var roll = TerrainPreviewNoise.Hash01( noiseSeed + 960, coord.X * 384 + ix, coord.Y * 384 + iy );
		if ( roll > chance )
			return;
		var stickPath = NormalizePrefabPath( options.NearLargeTreeStickPrefab );
		if ( !PrefabPathResolves( stickPath ) )
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
			Vector3.Up,
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
						    Vector3.Up,
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
			var paths = ResolvePaths( profile.Prefabs, profile.BiomeId, "tree" );
			if ( paths.Length == 0 )
			{
				Log.Warning( $"[Vegetation] No tree prefabs resolve for biome {profile.BiomeId}." );
				continue;
			}

			resolved.Add( new ResolvedProfile
			{
				BiomeId = profile.BiomeId,
				Paths = paths,
				SmallPaths = ResolvePaths( profile.SmallPrefabs, profile.BiomeId, "small tree" ),
				NoiseSalt = profile.NoiseSeedSalt,
				InstancePrefix = string.IsNullOrWhiteSpace( profile.InstancePrefix )
					? "veg_tree"
					: profile.InstancePrefix.Trim(),
				// Default 1 when unset (struct default 0 would otherwise clamp to the floor).
				Density01 = profile.Density01 > 0f ? profile.Density01 : 1f,
				IgnoreForestPatches = profile.IgnoreForestPatches,
				Source = profile,
			} );
		}

		return resolved;
	}

	static string[] ResolvePaths( string[] prefabs, TerrainPreviewBiomeId biomeId, string kind )
	{
		if ( prefabs is null || prefabs.Length == 0 )
			return [];

		var paths = new List<string>( prefabs.Length );
		foreach ( var raw in prefabs )
		{
			var path = NormalizePrefabPath( raw );
			if ( PrefabPathResolves( path ) )
				paths.Add( path );
			else if ( !string.IsNullOrEmpty( path ) )
				Log.Warning( $"[Vegetation] {biomeId} {kind} prefab missing ('{path}')." );
		}

		return paths.ToArray();
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
		Vector3 up,
		ref bool loggedFirst,
		string kindLabel )
	{
		_ = loggedFirst;
		_ = kindLabel;

		if ( string.IsNullOrWhiteSpace( path ) || !PrefabPathResolves( path ) )
			return false;

		var name = memberIndex > 0
			? $"{instancePrefix}_{variant}_{memberIndex}"
			: $"{instancePrefix}_{variant}";

		var localMeters = new Vector3( wx - chunkMinX, wy - chunkMinY, heightMeters );
		var localPosition = TerrainWorldUnits.MetersToEngine( localMeters );

		var yaw = TerrainPreviewNoise.Hash01(
			seed + 940 + memberIndex,
			coord.X * 512 + ix,
			coord.Y * 512 + iy ) * yawJitter;
		var yawRotation = Rotation.FromYaw( yaw );
		Rotation localRotation;
		if ( up.z > 0.9999f )
		{
			localRotation = yawRotation;
		}
		else
		{
			// Keep the random heading, stand the model on the tilted up axis.
			var forward = yawRotation.Forward;
			forward = (forward - (up * Vector3.Dot( forward, up ))).Normal;
			localRotation = Rotation.LookAt( forward, up );
		}

		var scaleT = TerrainPreviewNoise.Hash01(
			seed + 950 + memberIndex,
			coord.X * 1024 + ix,
			coord.Y * 1024 + iy );
		var mul = scaleMin + ((scaleMax - scaleMin) * scaleT);
		var localScale = AuthoredScale( path ) * mul;

		var plan = new SpawnPlan
		{
			Path = path,
			Name = name,
			LocalPosition = localPosition,
			LocalRotation = localRotation,
			LocalScale = localScale,
			StableKey = $"veg:{coord.X}:{coord.Y}:{ix}:{iy}:{memberIndex}:{instancePrefix}:{variant}",
			Sapling = kindLabel == "sapling",
			Kind = kindLabel,
		};

		// Planned: the manager clones it later under its per-frame budget. No list = spawn right now (legacy).
		if ( _plans is not null )
			_plans.Add( plan );
		else
			SpawnPlanned( chunkRoot, plan );

		return true;
	}

	internal static string NormalizePrefabPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return "";

		path = path.Trim().Replace( '\\', '/' );
		if ( path.StartsWith( "assets/", StringComparison.OrdinalIgnoreCase ) )
			path = path[7..];
		return path;
	}

	// Profiles are re-resolved on every chunk populate (several per chunk while streaming): remember each path's answer.
	static readonly Dictionary<string, bool> _prefabResolves = new();

	static bool PrefabPathResolves( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return false;
		if ( _prefabResolves.TryGetValue( path, out var known ) )
			return known;

		var resolves = GameObject.GetPrefab( path ) is { IsValid: true } || ResourceLibrary.Get<PrefabFile>( path ) is not null;
		_prefabResolves[path] = resolves;
		return resolves;
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
		if ( root is null || !root.IsValid() )
			return;

		// Prop + Rigidbody on tree prefabs fight static scatter: destroy Prop entirely so
		// ModelCollider (static) is the only physics surface. Prefab Tags (e.g. grapple) stay on the clone.
		var props = new List<Prop>();
		foreach ( var prop in root.Components.GetAll<Prop>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( prop is not null && prop.IsValid() )
				props.Add( prop );
		}

		for ( var i = 0; i < props.Count; i++ )
			props[i].Destroy();

		var bodies = new List<Rigidbody>();
		foreach ( var body in root.Components.GetAll<Rigidbody>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( body is not null && body.IsValid() )
				bodies.Add( body );
		}

		for ( var i = 0; i < bodies.Count; i++ )
			bodies[i].Destroy();

		foreach ( var col in root.Components.GetAll<ModelCollider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( col is null || !col.IsValid() )
				continue;

			col.Enabled = true;
			col.Static = true;
		}
	}
}
