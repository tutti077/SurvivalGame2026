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

	// The plan list the populate calls write into, per thread: the main thread's chunk populate and the far ring's worker
	// planner (EnumerateTrees → far-ring landmarks) each set their own. Null = spawn immediately (legacy callers).
	[ThreadStatic] static List<SpawnPlan> _plans;
	static readonly Dictionary<string, Vector3> _authoredScale = new();
	static readonly object _authoredScaleLock = new();

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
		lock ( _authoredScaleLock )
		{
			if ( _authoredScale.TryGetValue( path, out var cached ) )
				return cached;
		}

		var scale = Vector3.One;
		var template = GameObject.GetPrefab( path );
		if ( template is { IsValid: true } )
		{
			scale = template.LocalScale;
			if ( scale.x < 0.001f || scale.y < 0.001f || scale.z < 0.001f )
				scale = Vector3.One;
		}

		lock ( _authoredScaleLock )
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
		/// <summary>Variants picked per member by hash (forage kinds with several models). Null / empty = <see cref="Prefab"/> only.</summary>
		public string[] Prefabs { get; init; }
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
		/// <summary>&gt; 0 = the cluster stands on generated tilled soil reaching this far past its radius (<see cref="TerrainGroundPatch"/>, <see cref="PlanGroundPatches"/>): the wild-wheat beds.</summary>
		public float TilledPatchPadMeters { get; init; }
		/// <summary>Planted with the large trees (inside Tree Objects Range, once per chunk load, kept until the chunk unloads) instead of the small-prop range: landmarks that must be seen from further than a pebble.</summary>
		public bool WithTrees { get; init; }
		/// <summary>&gt; 0 = a wide base (metres at scale 1, × the mean scale): the member stands at the LOWEST ground of six points on this
		/// ring round it (then <see cref="SinkMeters"/> lower), so no side of a spire hangs over a slope. Six samples once per placement.</summary>
		public float FootprintRadiusMeters { get; init; }
		/// <summary>&gt; 0 = clearings only: the cluster centre and six points on a ring this wide must all lie outside the biome's forest patches (where no large tree is ever planted).</summary>
		public float ClearingRadiusMeters { get; init; }
		/// <summary>No member closer than this to a large trunk planted in the same chunk.</summary>
		public float TrunkClearanceMeters { get; init; }
		/// <summary>Members are lowered this far (× their scale) so a wide base on a slope does not float on its downhill side.</summary>
		public float SinkMeters { get; init; }
		/// <summary>&gt; 0 = each member leans this many degrees (min..max by hash) off vertical toward a hashed heading, and sinks a further
		/// <see cref="FootprintRadiusMeters"/> × sin(lean) so the raised edge of its base stays in the ground: the stone spires
		/// (Mark 2026-11-03: "make them angled on the ground — it'll help with grappling").</summary>
		public float LeanMinDegrees { get; init; }
		public float LeanMaxDegrees { get; init; }
		/// <summary>Share of members that stand straight up instead of leaning (Mark 2026-11-04: "some of the spires should be straight up").</summary>
		public float LeanUprightChance01 { get; init; }
		/// <summary>&gt; 1 = members are this much wider (x / y) than their scale makes them, height unchanged; the footprint ring widens
		/// with them. The spires: 2 (Mark 2026-11-04: "still far too skinny, make them 2x as thick").</summary>
		public float ThicknessMul { get; init; }
		/// <summary>Also drawn by the far ring past Tree Objects Range, as the prefab's <c>_far.vmdl</c> (a <see cref="WithTrees"/> landmark
		/// that must be seen as far as the trees: the stone spires, Mark 2026-11-03 "they pop in from too close up").</summary>
		public bool FarRing { get; init; }
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
		/// <summary>Clover Hills only: after each large tree, roll this chance to hang a beehive from a low limb (0.02 = one tree in fifty).</summary>
		public string[] BeehivePrefabs { get; init; }
		public float BeehiveChance01 { get; init; }
		public float BeehiveHangMinMeters { get; init; }
		public float BeehiveHangMaxMeters { get; init; }
		public float BeehiveReachMinMeters { get; init; }
		public float BeehiveReachMaxMeters { get; init; }
	}

	internal sealed class ResolvedProfile
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
		bool props = true,
		bool landmarks = false )
	{
		if ( !options.Enabled || chunkRoot is null || !chunkRoot.IsValid() )
			return;

		if ( options.SkipFarLodChunks && verticesPerSide < fullDetailVertices )
			return;

		_plans = plans;
		try
		{
			PopulateChunkCore( chunkRoot, coord, settings, backend, chunkSizeMeters, options, trunks, largeTrees, props, landmarks );
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
		bool props,
		bool landmarks )
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

		if ( (!props && !landmarks) || options.PropClusters is null )
			return;

		var forest = ForestPatchField.For( settings, options, profiles );
		foreach ( var cluster in options.PropClusters )
		{
			// Landmarks (spires, boulders) ride the tree pass; everything else the prop pass
			if ( cluster.WithTrees ? !landmarks : !props )
				continue;
			ScatterPropClusters( chunkRoot, coord, settings, backend, chunkSize, chunkMinX, chunkMinY, seed, cluster, forest, trunks ?? [] );
		}
	}

	/// <summary>
	/// The large-tree forest patches as a pure noise field (thread-safe): the planner's own Fbm, wavelength and threshold for
	/// one biome profile. A point is a <b>clearing</b> where the patch value is under the threshold — no large tree is ever
	/// planted there (<see cref="TryPlanTree"/>). Biomes without hard patches (Redwood) have no clearings.
	/// </summary>
	public readonly struct ForestPatchField
	{
		public bool Valid { get; init; }
		public TerrainPreviewBiomeId BiomeId { get; init; }
		public int NoiseSeed { get; init; }
		public float Wave { get; init; }
		public float Threshold { get; init; }

		/// <summary>The Clover Hills field (the only biome with hard clearings) for these options.</summary>
		static ForestPatchField For( TerrainPreviewSettings settings, Options options, List<ResolvedProfile> profiles, TerrainPreviewBiomeId biome )
		{
			var profile = FindProfile( profiles, biome );
			if ( profile is null || profile.IgnoreForestPatches )
				return default;
			return new ForestPatchField
			{
				Valid = true,
				BiomeId = biome,
				NoiseSeed = settings.WorldSeed + profile.NoiseSalt,
				Wave = Math.Max( 32f, options.PatchWavelengthMeters ),
				Threshold = Math.Clamp( options.PatchThreshold01, 0.05f, 0.95f ),
			};
		}

		internal static ForestPatchField For( TerrainPreviewSettings settings, Options options, List<ResolvedProfile> profiles )
			=> For( settings, options, profiles, TerrainPreviewBiomeId.CloverHills );

		/// <summary>Public entry for callers outside the scatter (camp sites): resolves the profiles once (main thread — prefab lookups).</summary>
		public static ForestPatchField ForBiome( TerrainPreviewSettings settings, Options options, TerrainPreviewBiomeId biome )
			=> For( settings, options, ResolveProfiles( options.Profiles ), biome );

		public bool IsClearing( float x, float y )
			=> Valid && TerrainPreviewNoise.Fbm( NoiseSeed + 910, x / Wave, y / Wave, 4, 2.05f, 0.5f ) < Threshold;

		/// <summary>The centre and six points on a ring of <paramref name="radiusMeters"/> are all clearing.</summary>
		public bool IsClearingDisc( float x, float y, float radiusMeters )
		{
			if ( !IsClearing( x, y ) )
				return false;
			for ( var k = 0; k < 6; k++ )
			{
				var a = k * (MathF.Tau / 6f);
				if ( !IsClearing( x + (MathF.Cos( a ) * radiusMeters), y + (MathF.Sin( a ) * radiusMeters) ) )
					return false;
			}
			return true;
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

				if ( profile.BiomeId == TerrainPreviewBiomeId.CloverHills )
					TrySpawnBeehiveOnTree( chunkRoot, coord, chunkMinX, chunkMinY, wx, wy, sample.HeightMeters, noiseSeed, ix, iy, options );
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
		/// <summary>Full rotation when it is not upright (a leaning spire); null = <see cref="YawDegrees"/> about the vertical.</summary>
		public Rotation? Rotation { get; init; }
		/// <summary>Non-uniform scale (a widened spire); null = <see cref="ScaleMul"/> on every axis.</summary>
		public Vector3? Scale { get; init; }
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

		EnumerateFarRingLandmarks( settings, backend, coord, chunkSize, chunkMinX, chunkMinY, seed, options, profiles, result );
	}

	/// <summary>
	/// The far-ring landmarks of this chunk (<see cref="PropClusterOptions.FarRing"/>: the spires): the very plans the tree pass
	/// would make, recorded on this thread's plan list and turned into far entries — same spot, lean and size as the real one.
	/// </summary>
	static void EnumerateFarRingLandmarks(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		TerrainChunkCoord coord,
		float chunkSize,
		float chunkMinX,
		float chunkMinY,
		int seed,
		Options options,
		List<ResolvedProfile> profiles,
		List<FarTree> result )
	{
		if ( options.PropClusters is null )
			return;

		List<Vector2> trunks = null;
		var forest = default( ForestPatchField );
		var plans = new List<SpawnPlan>();
		var previous = _plans;
		_plans = plans;
		try
		{
			foreach ( var cluster in options.PropClusters )
			{
				if ( !cluster.Enabled || !cluster.WithTrees || !cluster.FarRing )
					continue;
				if ( trunks is null )
				{
					// the trunks this chunk's tree pass planted (the far entries so far), for the clusters' trunk clearance
					trunks = new List<Vector2>( result.Count );
					foreach ( var tree in result )
						trunks.Add( new Vector2( tree.X, tree.Y ) );
					forest = ForestPatchField.For( settings, options, profiles );
				}
				ScatterPropClusters( null, coord, settings, backend, chunkSize, chunkMinX, chunkMinY, seed, cluster, forest, trunks );
			}
		}
		finally
		{
			_plans = previous;
		}

		foreach ( var plan in plans )
		{
			var local = TerrainWorldUnits.EngineToMeters( plan.LocalPosition );
			result.Add( new FarTree
			{
				X = chunkMinX + local.x,
				Y = chunkMinY + local.y,
				HeightMeters = local.z,
				ScaleMul = plan.LocalScale.z,
				Scale = plan.LocalScale,
				PrefabPath = plan.Path,
				Rotation = plan.LocalRotation,
			} );
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

	/// <summary>
	/// One large Clover tree in <c>1 / BeehiveChance01</c> carries a wild beehive: hung by its origin (the top of
	/// its twig stub) <c>BeehiveHang</c> metres above the trunk base and <c>BeehiveReach</c> metres out from it, at
	/// a hashed yaw - a low limb, where a player walking under the tree spots it.
	/// </summary>
	static void TrySpawnBeehiveOnTree(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		float chunkMinX,
		float chunkMinY,
		float treeWx,
		float treeWy,
		float groundMeters,
		int noiseSeed,
		int ix,
		int iy,
		Options options )
	{
		var chance = Math.Clamp( options.BeehiveChance01, 0f, 1f );
		if ( chance <= 1e-6f || options.BeehivePrefabs is null || options.BeehivePrefabs.Length == 0 )
			return;

		var roll = TerrainPreviewNoise.Hash01( noiseSeed + 970, coord.X * 384 + ix, coord.Y * 384 + iy );
		if ( roll > chance )
			return;

		var paths = ResolvePaths( options.BeehivePrefabs, TerrainPreviewBiomeId.CloverHills, "beehive" );
		if ( paths.Length == 0 )
			return;

		var pick = Math.Clamp( (int)(TerrainPreviewNoise.Hash01( noiseSeed + 971, coord.X * 400 + ix, coord.Y * 400 + iy ) * paths.Length), 0, paths.Length - 1 );
		var yaw = TerrainPreviewNoise.Hash01( noiseSeed + 972, coord.X * 416 + ix, coord.Y * 416 + iy ) * MathF.Tau;
		var reachMin = Math.Clamp( options.BeehiveReachMinMeters, 0.3f, 6f );
		var reachMax = Math.Max( reachMin, Math.Clamp( options.BeehiveReachMaxMeters, 0.3f, 6f ) );
		var hangMin = Math.Clamp( options.BeehiveHangMinMeters, 1f, 12f );
		var hangMax = Math.Max( hangMin, Math.Clamp( options.BeehiveHangMaxMeters, 1f, 12f ) );
		var reach = reachMin + (reachMax - reachMin) * TerrainPreviewNoise.Hash01( noiseSeed + 973, coord.X * 432 + ix, coord.Y * 432 + iy );
		var hang = hangMin + (hangMax - hangMin) * TerrainPreviewNoise.Hash01( noiseSeed + 974, coord.X * 448 + ix, coord.Y * 448 + iy );

		TrySpawnInstance(
			chunkRoot,
			paths[pick],
			"veg_beehive",
			pick.ToString(),
			chunkMinX,
			chunkMinY,
			treeWx + MathF.Cos( yaw ) * reach,
			treeWy + MathF.Sin( yaw ) * reach,
			groundMeters + hang,
			noiseSeed,
			coord,
			ix,
			iy,
			2,
			360f,
			1f,
			1f,
			Vector3.Up,
			ref _loggedFirstRock,
			"beehive" );
	}

	/// <summary>A cluster kind's cell edge (m): its spacing, never more than a chunk (one cell per chunk at most).</summary>
	static float ClusterCell( in PropClusterOptions props, float chunkSize )
		=> Math.Clamp( props.ClusterSpacingMeters, 8f, Math.Max( 8f, chunkSize ) );

	/// <summary>
	/// The centre of one prop cluster cell, or false: strong jitter (centres never read as a grid), the chance roll, the land
	/// disc, land under it, the cluster's biome and — for <see cref="PropClusterOptions.ClearingRadiusMeters"/> kinds — a
	/// clearing round it. Pure and thread-safe; <see cref="ScatterPropClusters"/> and <see cref="PlanGroundPatches"/> both
	/// ask it, so a wheat cluster and its tilled bed always share one centre.
	/// </summary>
	static bool TryPlanClusterCenter(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		TerrainChunkCoord coord,
		float chunkMinX,
		float chunkMinY,
		float cell,
		int ix,
		int iy,
		int noiseSeed,
		in PropClusterOptions props,
		in ForestPatchField forest,
		out float cx,
		out float cy )
	{
		var jitterX = (TerrainPreviewNoise.Hash01( noiseSeed + 10, coord.X * 48 + ix, coord.Y * 48 + iy ) - 0.5f) * cell * 0.85f;
		var jitterY = (TerrainPreviewNoise.Hash01( noiseSeed + 11, coord.X * 48 + ix, coord.Y * 48 + iy ) - 0.5f) * cell * 0.85f;
		cx = chunkMinX + ((ix + 0.5f) * cell) + jitterX;
		cy = chunkMinY + ((iy + 0.5f) * cell) + jitterY;

		// Cheapest first: the roll, then the clearing noise, then the terrain sample
		var chance = Math.Clamp( props.ClusterChance01, 0.005f, 1f );
		if ( TerrainPreviewNoise.Hash01( noiseSeed + 20, coord.X * 96 + ix, coord.Y * 96 + iy ) > chance )
			return false;
		if ( !IsInsideLandDisk( settings, cx, cy ) )
			return false;
		if ( props.ClearingRadiusMeters > 0f && !forest.IsClearingDisc( cx, cy, props.ClearingRadiusMeters ) )
			return false;

		var centerSample = backend.Sample( settings, cx, cy );
		if ( !centerSample.IsInsideWorld || !centerSample.IsOnLand || centerSample.OceanHeight01 > 0.5f )
			return false;

		return TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, centerSample, cx, cy ).BiomeId == props.BiomeId;
	}

	/// <summary>
	/// The generated tilled beds (<see cref="TerrainGroundPatch"/>) that reach one chunk: every cluster kind with a
	/// <see cref="PropClusterOptions.TilledPatchPadMeters"/>, planned on this chunk's cells and its eight neighbours' (a bed by
	/// a border spills over), kept when it reaches the chunk square plus <paramref name="marginMeters"/> (a voxel column's
	/// margin). Pure and thread-safe: the chunk sampling worker calls it, so the splat, the grass, farming and a voxel column
	/// all read the same array. Null = no bed here.
	/// </summary>
	public static TerrainGroundPatch[] PlanGroundPatches(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		TerrainChunkCoord coord,
		float chunkSizeMeters,
		PropClusterOptions[] sources,
		float marginMeters = 2f )
	{
		if ( sources is null || sources.Length == 0 || settings is null || backend is null )
			return null;

		var chunkSize = Math.Max( 32f, chunkSizeMeters );
		var worldRadius = settings.TotalWorldRadiusMeters;
		var minX = -worldRadius + (coord.X * chunkSize) - marginMeters;
		var minY = -worldRadius + (coord.Y * chunkSize) - marginMeters;
		var maxX = minX + chunkSize + (2f * marginMeters);
		var maxY = minY + chunkSize + (2f * marginMeters);
		List<TerrainGroundPatch> result = null;

		foreach ( var props in sources )
		{
			if ( !props.Enabled || props.TilledPatchPadMeters <= 0f )
				continue;
			var cell = ClusterCell( props, chunkSize );
			var cells = Math.Max( 1, (int)MathF.Floor( chunkSize / cell ) );
			var radius = Math.Clamp( props.ClusterRadiusMeters, 0.25f, cell * 0.45f ) + props.TilledPatchPadMeters;
			var noiseSeed = settings.WorldSeed + props.NoiseSeedSalt;
			// Reach test on the cell's widest jitter before any sampling
			var reach = (radius * 1.2f) + TerrainGroundPatch.EdgeMeters + (cell * 0.5f);
			for ( var ny = -1; ny <= 1; ny++ )
			{
				for ( var nx = -1; nx <= 1; nx++ )
				{
					var c = new TerrainChunkCoord( coord.X + nx, coord.Y + ny );
					var cMinX = -worldRadius + (c.X * chunkSize);
					var cMinY = -worldRadius + (c.Y * chunkSize);
					for ( var iy = 0; iy < cells; iy++ )
					{
						for ( var ix = 0; ix < cells; ix++ )
						{
							var ccx = cMinX + ((ix + 0.5f) * cell);
							var ccy = cMinY + ((iy + 0.5f) * cell);
							if ( ccx + reach < minX || ccx - reach > maxX || ccy + reach < minY || ccy - reach > maxY )
								continue;
							if ( !TryPlanClusterCenter( settings, backend, c, cMinX, cMinY, cell, ix, iy, noiseSeed, props, default, out var px, out var py ) )
								continue;
							var patch = new TerrainGroundPatch
							{
								X = px,
								Y = py,
								RadiusMeters = radius,
								Phase = TerrainPreviewNoise.Hash01( noiseSeed + 70, c.X * 48 + ix, c.Y * 48 + iy ) * MathF.Tau,
							};
							var r = patch.ReachMeters;
							if ( px + r < minX || px - r > maxX || py + r < minY || py - r > maxY )
								continue;
							(result ??= new List<TerrainGroundPatch>()).Add( patch );
						}
					}
				}
			}
		}

		return result?.ToArray();
	}

	/// <summary>
	/// Dev (<c>world_find</c>): the planned cluster centres of one kind (its <see cref="PropClusterOptions.KindLabel"/>) within
	/// <paramref name="radiusMeters"/> of a point, as (x, y, ground height) metres — the same planner the scatter runs, so a
	/// centre found here is where the cluster stands once its chunk is planted. Main thread (resolves the tree profiles).
	/// </summary>
	public static void FindClusterCenters( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, Options options, string kind, float xMeters, float yMeters, float radiusMeters, float chunkSizeMeters, List<Vector3> result )
	{
		if ( options.PropClusters is null || settings is null || backend is null )
			return;
		var chunkSize = Math.Max( 32f, chunkSizeMeters );
		var worldRadius = settings.TotalWorldRadiusMeters;
		var forest = ForestPatchField.For( settings, options, ResolveProfiles( options.Profiles ) );
		foreach ( var props in options.PropClusters )
		{
			if ( !props.Enabled || !string.Equals( props.KindLabel, kind, StringComparison.OrdinalIgnoreCase ) )
				continue;
			var cell = ClusterCell( props, chunkSize );
			var cells = Math.Max( 1, (int)MathF.Floor( chunkSize / cell ) );
			var noiseSeed = settings.WorldSeed + props.NoiseSeedSalt;
			var lo = TerrainChunkStreaming.WorldToChunkCoord( xMeters - radiusMeters, yMeters - radiusMeters, worldRadius, chunkSize );
			var hi = TerrainChunkStreaming.WorldToChunkCoord( xMeters + radiusMeters, yMeters + radiusMeters, worldRadius, chunkSize );
			for ( var y = lo.Y; y <= hi.Y; y++ )
			{
				for ( var x = lo.X; x <= hi.X; x++ )
				{
					var c = new TerrainChunkCoord( x, y );
					var cMinX = -worldRadius + (x * chunkSize);
					var cMinY = -worldRadius + (y * chunkSize);
					for ( var iy = 0; iy < cells; iy++ )
					{
						for ( var ix = 0; ix < cells; ix++ )
						{
							if ( !TryPlanClusterCenter( settings, backend, c, cMinX, cMinY, cell, ix, iy, noiseSeed, props, forest, out var px, out var py ) )
								continue;
							var dx = px - xMeters;
							var dy = py - yMeters;
							if ( (dx * dx) + (dy * dy) <= radiusMeters * radiusMeters )
								result.Add( new Vector3( px, py, backend.Sample( settings, px, py ).HeightMeters ) );
						}
					}
				}
			}
		}
	}

	/// <summary>Most members one prop cluster can plant (a wild-wheat bed fills with up to 14 tufts).</summary>
	const int MaxClusterSize = 16;

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
		in ForestPatchField forest,
		List<Vector2> trunks )
	{
		if ( !props.Enabled )
			return;

		var paths = props.Prefabs is { Length: > 0 }
			? ResolvePaths( props.Prefabs, props.BiomeId, props.KindLabel )
			: ResolvePaths( [props.Prefab], props.BiomeId, props.KindLabel );
		if ( paths.Length == 0 )
		{
			Log.Warning( $"[Vegetation] {props.KindLabel} prefab missing ('{props.Prefab}')." );
			return;
		}

		var cell = ClusterCell( props, chunkSize );
		var sizeMin = Math.Clamp( props.ClusterSizeMin, 1, MaxClusterSize );
		var sizeMax = Math.Max( sizeMin, Math.Clamp( props.ClusterSizeMax, 1, MaxClusterSize ) );
		var radius = Math.Clamp( props.ClusterRadiusMeters, 0.25f, cell * 0.45f );
		var scaleMin = Math.Clamp( props.ScaleMin, 0.05f, 4f );
		var scaleMax = Math.Max( scaleMin, Math.Clamp( props.ScaleMax, 0.05f, 4f ) );
		var maxPerChunk = Math.Clamp( props.MaxPerChunk, 1, 96 );
		var cells = Math.Max( 1, (int)MathF.Floor( chunkSize / cell ) );
		var noiseSeed = seed + props.NoiseSeedSalt;
		var prefix = string.IsNullOrWhiteSpace( props.InstancePrefix ) ? "veg_prop" : props.InstancePrefix.Trim();
		var kind = string.IsNullOrWhiteSpace( props.KindLabel ) ? "prop" : props.KindLabel.Trim();
		var clearanceSq = props.TrunkClearanceMeters > 0f ? props.TrunkClearanceMeters * props.TrunkClearanceMeters : 0f;
		var sink = Math.Max( 0f, props.SinkMeters ) * 0.5f * (scaleMin + scaleMax);
		var thickness = Math.Max( 1f, props.ThicknessMul );
		var footprint = Math.Max( 0f, props.FootprintRadiusMeters ) * 0.5f * (scaleMin + scaleMax) * thickness;
		var spawned = 0;

		for ( var iy = 0; iy < cells && spawned < maxPerChunk; iy++ )
		{
			for ( var ix = 0; ix < cells && spawned < maxPerChunk; ix++ )
			{
				if ( !TryPlanClusterCenter( settings, backend, coord, chunkMinX, chunkMinY, cell, ix, iy, noiseSeed, props, forest, out var cx, out var cy ) )
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
					if ( clearanceSq > 0f && NearAnyTrunk( trunks, wx, wy, clearanceSq ) )
						continue;

					var sample = backend.Sample( settings, wx, wy );
					if ( !sample.IsInsideWorld || !sample.IsOnLand || sample.OceanHeight01 > 0.5f )
						continue;

					var memberBiome = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, wx, wy );
					if ( memberBiome.BiomeId != props.BiomeId )
						continue;

					var pick = paths.Length == 1
						? 0
						: Math.Clamp( (int)(TerrainPreviewNoise.Hash01( noiseSeed + 60 + r, coord.X * 192 + ix, coord.Y * 192 + iy ) * paths.Length), 0, paths.Length - 1 );

					var groundZ = sample.HeightMeters;
					if ( footprint > 0f )
					{
						for ( var k = 0; k < 6; k++ )
						{
							var a = k * (MathF.PI / 3f);
							var rim = backend.Sample( settings, wx + (MathF.Cos( a ) * footprint), wy + (MathF.Sin( a ) * footprint) );
							if ( rim.IsInsideWorld )
								groundZ = MathF.Min( groundZ, rim.HeightMeters );
						}
					}

					var up = Vector3.Up;
					var leanSink = 0f;
					var upright = props.LeanUprightChance01 > 0f
						&& TerrainPreviewNoise.Hash01( noiseSeed + 90 + r, coord.X * 240 + ix, coord.Y * 240 + iy ) < props.LeanUprightChance01;
					if ( props.LeanMaxDegrees > 0f && !upright )
					{
						var leanT = TerrainPreviewNoise.Hash01( noiseSeed + 70 + r, coord.X * 208 + ix, coord.Y * 208 + iy );
						var lean = MathX.Lerp( Math.Max( 0f, props.LeanMinDegrees ), props.LeanMaxDegrees, leanT ) * (MathF.PI / 180f);
						var heading = TerrainPreviewNoise.Hash01( noiseSeed + 80 + r, coord.X * 224 + ix, coord.Y * 224 + iy ) * MathF.PI * 2f;
						up = new Vector3( MathF.Cos( heading ) * MathF.Sin( lean ), MathF.Sin( heading ) * MathF.Sin( lean ), MathF.Cos( lean ) );
						leanSink = footprint * MathF.Sin( lean );
					}

					if ( !TrySpawnInstance(
						    chunkRoot,
						    paths[pick],
						    prefix,
						    paths.Length == 1 ? "a" : pick.ToString(),
						    chunkMinX,
						    chunkMinY,
						    wx,
						    wy,
						    groundZ - sink - leanSink,
						    noiseSeed,
						    coord,
						    ix,
						    iy,
						    r + 1,
						    360f,
						    scaleMin,
						    scaleMax,
						    up,
						    ref _loggedFirstRock,
						    kind,
						    thickness ) )
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
		string kindLabel,
		float thicknessMul = 1f )
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
		var localScale = AuthoredScale( path ) * mul * new Vector3( thicknessMul, thicknessMul, 1f );

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
	// Locked: SampleForestCover01 resolves profiles on the far / horizon tier workers (four horizon quadrants at once on
	// load) while the main thread populates chunks — the unguarded dictionary corrupted and threw on every later read.
	static readonly Dictionary<string, bool> _prefabResolves = new();
	static readonly object _prefabResolvesLock = new();

	static bool PrefabPathResolves( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return false;
		lock ( _prefabResolvesLock )
		{
			if ( _prefabResolves.TryGetValue( path, out var known ) )
				return known;
		}

		var resolves = GameObject.GetPrefab( path ) is { IsValid: true } || ResourceLibrary.Get<PrefabFile>( path ) is not null;
		lock ( _prefabResolvesLock )
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
