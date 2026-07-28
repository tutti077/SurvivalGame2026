using System;
using System.Diagnostics;
using Game;

namespace Survival;

/// <summary>
/// Streams procedural terrain from world-meter sampling (<see cref="ITerrainPreviewBackend"/>).
/// <see cref="BiomePreviewMap"/> is inspector/display export only — nothing reads it for generation.
/// </summary>
[Title( "Terrain World Manager" )]
public sealed class TerrainWorldManager : Component
{
	const float ChunkLoadProgressWeight = 0.3f;
	const float MapLoadProgressWeight = 0.7f;

	sealed class LoadedChunk
	{
		public GameObject GameObject;
		public ModelCollider Collider;
		public TerrainChunkCoord Coord;
		public int VerticesPerSide;
		public bool HasVegetation;
		public bool HasEntityPopulation;
	}

	[Property, Group( "World" )] public string WorldName { get; set; } = "TestWorld";
	[Property, Group( "World" )] public float WorldDiameterMeters { get; set; } = 20000f;
	[Property, Group( "World" ), Title( "Ocean Ring Width (m)" ), Description( "Flat water band outside land disk. Total world = land + 2× ring (default 25 km)." )]
	public float OceanRingWidthMeters { get; set; } = 2500f;
	[Property, Group( "World" )] public int WorldSeed { get; set; } = 1337;
	[Property, Group( "World" )] public float MaxTerrainHeightMeters { get; set; } = 700f;
	[Property, Group( "World" ), Title( "Settings Source" ), Description( "Tuned Preview First = latest editor Generate bundle, then saved world recipe. Uses solved lake offsets from the bundle — no re-solve on play." )]
	public TerrainWorldSettingsSource SettingsSource { get; set; } = TerrainWorldSettingsSource.TunedPreviewFirst;
	[Property, Group( "World" ), Title( "Override World Scalars From Component" ), Description( "When off, seed/diameter/height/ocean ring come from the tuned bundle or recipe (matches preview PNG)." )]
	public bool OverrideWorldScalarsFromComponent { get; set; }
	[Property, Group( "World" ), Title( "Run Lake Spawn Solve On Load" ), Description( "Only when Settings Source = Component Defaults Only. Re-runs editor lake offset solve on play (slow)." )]
	public bool RunLakeSpawnSolveOnLoad { get; set; }

	[Property, Group( "Chunks" )] public float ChunkSizeMeters { get; set; } = 64f;
	[Property, Group( "Chunks" ), Title( "Vertices Per Side" ), Description( "Height grid resolution per chunk. 33 ≈ 2 m spacing on 64 m chunks; higher = smoother slopes, more cost." )]
	public int ChunkVerticesPerSide { get; set; } = 33;
	[Property, Group( "Chunks" ), Title( "Height Smooth Passes" ), Range( 0, 3 ), Step( 1 ), Description( "Interior-only Laplacian smooth. 0 = keep natural slope between chunk corners (recommended). >0 can flatten chunks into plateaus." )]
	public int ChunkHeightSmoothPasses { get; set; }
	[Property, Group( "Chunks" ), Title( "Height Smooth Strength (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float ChunkHeightSmoothStrength01 { get; set; } = 0.38f;
	[Property, Group( "Chunks" ), Title( "Stream Radius (chunks)" ), Range( 0, 16 ), Step( 1 ), Description( "Uniform square around the camera when forward-cone streaming is off. Radius 8 = 17×17 (~512 m). Matches former forward view distance." )]
	public int StreamRadiusChunks { get; set; } = 8;
	[Property, Group( "Chunks" ), Title( "Unload Margin (chunks)" ), Range( 0, 4 ), Step( 1 ), Description( "Keep chunks until outside Stream Radius + this margin. Load and unload use different distances so they never thrash at the same edge." )]
	public int StreamUnloadMarginChunks { get; set; } = 2;
	[Property, Group( "Chunks" ), Title( "Forward Cone Streaming" ), Description( "OFF = same radius in all directions (no look pop-in). ON = far cone ahead + smaller side/back square." )]
	public bool UseForwardConeStreaming { get; set; }
	[Property, Group( "Chunks" ), Title( "Forward View Radius (chunks)" ), Range( 6, 20 ), Step( 1 ), Description( "Only when Forward Cone Streaming is on. How far ahead to stream along look direction." )]
	public int ForwardViewRadiusChunks { get; set; } = 10;
	[Property, Group( "Chunks" ), Title( "Forward View Distance (m, 0 = radius × chunk size)" ), Range( 0f, 20000f ), Step( 64f )]
	public float ViewDistanceMeters { get; set; }
	[Property, Group( "Chunks" ), Title( "Forward View Cone (deg)" ), Range( 30f, 360f ), Step( 5f )]
	public float ForwardViewConeDegrees { get; set; } = 120f;
	[Property, Group( "Chunks" ), Title( "Side/Back Radius (chunks)" ), Range( 0, 12 ), Step( 1 ), Description( "Only when Forward Cone Streaming is on. Always-loaded square around the camera." )]
	public int SideViewRadiusChunks { get; set; } = 4;
	[Property, Group( "Chunks" ), Title( "Collision Range (m)" ), Range( 64f, 4096f ), Step( 64f ), Description( "Terrain physics + entity population. Scavs only spawn inside this range (after colliders are on)." )]
	public float CollisionRangeMeters { get; set; } = 192f;
	[Property, Group( "Chunks" ), Title( "Max Chunks Per Frame" ), Range( 1, 12 ), Step( 1 ), Description( "Hard cap on chunk mesh builds per frame (initial load + streaming)." )]
	public int ChunksPerFrame { get; set; } = 3;
	[Property, Group( "Chunks" ), Title( "Stream Build Budget (ms)" ), Range( 4f, 32f ), Step( 1f ), Description( "Milliseconds of mesh work per frame while streaming. Spreads cost without one-chunk-per-frame crawl." )]
	public float StreamMeshBuildBudgetMs { get; set; } = 10f;
	[Property, Group( "Chunks" ), Title( "Near Sync Chunks On Refresh" ), Range( 0, 8 ), Step( 1 ), Description( "Missing chunks built immediately when the stream zone updates. 0 = queue only (smoother turns)." )]
	public int StreamMaxSyncChunksPerRefresh { get; set; }
	[Property, Group( "Chunks" ), Title( "High-Priority Radius (chunks)" ), Range( 1f, 4f ), Step( 0.25f ), Description( "Chunk centers within this many chunk lengths get near sync + full mesh detail." )]
	public float StreamHighPriorityRadiusChunks { get; set; } = 2f;
	[Property, Group( "Chunks" ), Title( "Stream Mesh LOD" ), Description( "Coarser height grid for distant streamed chunks (17 verts = 4 m steps, aligns with 33-vert edges)." )]
	public bool StreamMeshLodEnabled { get; set; } = true;
	[Property, Group( "Chunks" ), Title( "Far Stream Vertices Per Side" ), Range( 9, 65 ), Step( 4 ), Description( "Height samples for chunks outside the full-detail radius (17 ≈ 4 m on 64 m chunks)." )]
	public int StreamFarVerticesPerSide { get; set; } = 17;
	[Property, Group( "Chunks" ), Title( "Mesh Border Prefetch" ), Range( 0.05f, 0.5f ), Step( 0.05f ), Description( "Reserved for future border-only mesh LOD." )]
	public float MeshBorderPrefetch01 { get; set; } = 0.35f;

	[Property, Group( "Preview Map" ), Title( "Meters Per Pixel" ), Range( 0f, 64f ), Step( 1f ), Description( "0 = match Terrain Preview Tool resolution (Preview Resolution on solved settings, default 1024)." )]
	public float BiomePreviewMetersPerPixel { get; set; }

	[Property, Group( "Preview Map" ), Title( "Max Map Resolution" ), Range( 512, 8192 ), Step( 256 )]
	public int BiomePreviewMapMaxResolution { get; set; } = 4096;

	[Property, Group( "Preview Map" ), Title( "Map Rows Per Frame" ), Range( 4, 512 ), Step( 4 )]
	public int PreviewMapRowsPerFrame { get; set; } = 128;

	[Property, Group( "Preview Map" ), Title( "Regenerate Biome Map On Play" ), Description( "OFF = reuse saved/HUD biome PNG and only rebuild chunk heights from Sample() (fast sculpt iteration). ON = full biome-map raster every Play (slow)." )]
	public bool RegeneratePreviewOnStart { get; set; } = false;

	[Property, Group( "Vegetation" ), Title( "Scatter Trees" )]
	public bool VegetationScatterEnabled { get; set; } = true;

	[Property, Group( "Vegetation" ), Title( "Clover — Tree Prefab A" )]
	public string VegetationPrefabA { get; set; } = "prefabs/environment/temp_tree_3.prefab";

	[Property, Group( "Vegetation" ), Title( "Clover — Tree Prefab B" )]
	public string VegetationPrefabB { get; set; } = "prefabs/environment/propertree.prefab";

	[Property, Group( "Vegetation" ), Title( "Clover — Prefab A Weight (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float VegetationPrefabAWeight01 { get; set; } = 0.55f;

	[Property, Group( "Vegetation" ), Title( "Clover — Rock Prefab" )]
	public string VegetationCloverRockPrefab { get; set; } = "prefabs/environment/rock.prefab";

	[Property, Group( "Vegetation" ), Title( "Clover — Scatter Rocks" )]
	public bool VegetationCloverRocksEnabled { get; set; } = true;

	[Property, Group( "Vegetation" ), Title( "Clover — Rock Cluster Spacing (m)" ), Range( 8f, 64f ), Step( 1f ), Description( "Average distance between rock cluster centers. Strong random jitter so it does not look gridded." )]
	public float VegetationCloverRockSpacingMeters { get; set; } = 50f;

	[Property, Group( "Vegetation" ), Title( "Clover — Rock Cluster Chance (0–1)" ), Range( 0.1f, 1f ), Step( 0.05f ), Description( "Chance a spacing cell becomes a cluster (lower = scarcer / more irregular)." )]
	public float VegetationCloverRockChance01 { get; set; } = 0.7f;

	[Property, Group( "Vegetation" ), Title( "Clover — Rocks Per Cluster Min" ), Range( 1, 8 ), Step( 1 )]
	public int VegetationCloverRockClusterMin { get; set; } = 2;

	[Property, Group( "Vegetation" ), Title( "Clover — Rocks Per Cluster Max" ), Range( 1, 8 ), Step( 1 )]
	public int VegetationCloverRockClusterMax { get; set; } = 5;

	[Property, Group( "Vegetation" ), Title( "Clover — Rock Cluster Radius (m)" ), Range( 0.5f, 8f ), Step( 0.25f )]
	public float VegetationCloverRockClusterRadiusMeters { get; set; } = 3f;

	[Property, Group( "Vegetation" ), Title( "Clover — Stick Prefab" )]
	public string VegetationCloverStickPrefab { get; set; } = "prefabs/environment/samplestick.prefab";

	[Property, Group( "Vegetation" ), Title( "Clover — Scatter Sticks" )]
	public bool VegetationCloverSticksEnabled { get; set; } = true;

	[Property, Group( "Vegetation" ), Title( "Clover — Stick Cluster Spacing (m)" ), Range( 8f, 64f ), Step( 1f )]
	public float VegetationCloverStickSpacingMeters { get; set; } = 50f;

	[Property, Group( "Vegetation" ), Title( "Clover — Stick Cluster Chance (0–1)" ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float VegetationCloverStickChance01 { get; set; } = 0.7f;

	[Property, Group( "Vegetation" ), Title( "Clover — Sticks Per Cluster Min" ), Range( 1, 4 ), Step( 1 )]
	public int VegetationCloverStickClusterMin { get; set; } = 1;

	[Property, Group( "Vegetation" ), Title( "Clover — Sticks Per Cluster Max" ), Range( 1, 4 ), Step( 1 )]
	public int VegetationCloverStickClusterMax { get; set; } = 2;

	[Property, Group( "Vegetation" ), Title( "Clover — Stick Cluster Radius (m)" ), Range( 0.25f, 4f ), Step( 0.25f )]
	public float VegetationCloverStickClusterRadiusMeters { get; set; } = 1.5f;

	[Property, Group( "Vegetation" ), Title( "Clover — Stick Near Large Tree" ), Description( "Extra sticks beside Prefab A (large) Clover trees. Prefab C / 3rd tree type not wired yet." )]
	public bool VegetationCloverStickNearLargeTreeEnabled { get; set; } = true;

	[Property, Group( "Vegetation" ), Title( "Clover — Stick Near Large Tree Chance (0–1)" ), Range( 0f, 1f ), Step( 0.01f ), Description( "Chance per large tree to place one stick in open ground beside the trunk." )]
	public float VegetationCloverStickNearLargeTreeChance01 { get; set; } = 0.05f;

	[Property, Group( "Vegetation" ), Title( "Clover — Stick Near Large Tree Min Radius (m)" ), Range( 1f, 12f ), Step( 0.25f )]
	public float VegetationCloverStickNearLargeTreeMinRadiusMeters { get; set; } = 2.5f;

	[Property, Group( "Vegetation" ), Title( "Clover — Stick Near Large Tree Max Radius (m)" ), Range( 1.5f, 16f ), Step( 0.25f )]
	public float VegetationCloverStickNearLargeTreeMaxRadiusMeters { get; set; } = 6f;

	[Property, Group( "Vegetation" ), Title( "Redwood — Tree Prefab A" )]
	public string VegetationRedwoodPrefab { get; set; } = "prefabs/environment/temp_tree_2.prefab";

	[Property, Group( "Vegetation" ), Title( "Redwood — Tree Prefab B" )]
	public string VegetationRedwoodPrefabB { get; set; } = "prefabs/environment/temp_tree_4.prefab";

	[Property, Group( "Vegetation" ), Title( "Redwood — Prefab A Weight (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Chance to pick Prefab A (temp_tree_2) vs B (temp_tree_4)." )]
	public float VegetationRedwoodPrefabAWeight01 { get; set; } = 0.5f;

	[Property, Group( "Vegetation" ), Title( "Redwood — Density (0–1)" ), Range( 0.05f, 1f ), Step( 0.05f ), Description( "Fraction of shared forest density for Redwood only. Redwood ignores hard forest patches, so low values still cover the whole biome sparsely." )]
	public float VegetationRedwoodDensity01 { get; set; } = 0.1f;

	[Property, Group( "Vegetation" ), Title( "Forest Patch Wavelength (m)" ), Range( 64f, 1200f ), Step( 16f ), Description( "Shared forest/clearing noise for Clover Hills and Redwood Forest." )]
	public float VegetationPatchWavelengthMeters { get; set; } = 380f;

	[Property, Group( "Vegetation" ), Title( "Forest Patch Threshold (0–1)" ), Range( 0.15f, 0.85f ), Step( 0.05f ), Description( "Higher = more empty clearings / less forest cover. FBM mean is ~0.5 — stay near 0.42–0.48." )]
	public float VegetationPatchThreshold01 { get; set; } = 0.46f;

	[Property, Group( "Vegetation" ), Title( "Tree Cell Spacing (m)" ), Range( 4f, 24f ), Step( 1f ), Description( "Lower = thicker packed forest cores." )]
	public float VegetationCellSpacingMeters { get; set; } = 5f;

	[Property, Group( "Vegetation" ), Title( "Spawn Chance In Forest (0–1)" ), Range( 0.1f, 1f ), Step( 0.05f ), Description( "Per-cell chance inside forest; grove cores auto-fill above this." )]
	public float VegetationSpawnChance01 { get; set; } = 0.98f;

	[Property, Group( "Vegetation" ), Title( "Max Trees Per Chunk" ), Range( 4, 128 ), Step( 1 )]
	public int VegetationMaxTreesPerChunk { get; set; } = 96;

	[Property, Group( "Vegetation" ), Title( "Scale Min (multiplier)" ), Range( 0.5f, 1.5f ), Step( 0.05f ), Description( "Multiplies each prefab's authored scale (temp_tree_3=1, ProperTree=0.25)." )]
	public float VegetationScaleMin { get; set; } = 0.9f;

	[Property, Group( "Vegetation" ), Title( "Scale Max (multiplier)" ), Range( 0.5f, 2f ), Step( 0.05f )]
	public float VegetationScaleMax { get; set; } = 1.15f;

	[Property, Group( "Vegetation" ), Title( "Skip Far LOD Chunks" ), Description( "On = trees only on full-detail chunks (near High-Priority radius) — much cheaper while streaming. Off = scatter trees on every streamed chunk (heavy with Stream Radius 8)." )]
	public bool VegetationSkipFarLodChunks { get; set; } = true;

	[Property, Group( "Entity Population" ), Title( "Scatter Entities" ), Description( "Spawn biome population from data/biome_population.json (host only)." )]
	public bool EntityPopulationEnabled { get; set; } = true;

	[Property, Group( "Loading" )] public bool ShowWorldLoadScreen { get; set; } = true;
	[Property, Group( "Loading" ), ReadOnly] public bool IsWorldLoading { get; private set; }
	[Property, Group( "Chunks" ), ReadOnly] public int LoadedChunkCount { get; private set; }
	[Property, Group( "Chunks" ), ReadOnly] public int MeshedChunkCount { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly] public bool IsMapGenerating { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly] public float MapGenerationProgress01 { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly] public string MapGenerationStatus { get; private set; } = "";
	[Property, Group( "Preview Map" ), ReadOnly] public int EffectiveBiomePreviewResolution { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly] public float EffectiveMetersPerPixel { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly, Title( "Biome Preview Map (display only)" )] public Texture BiomePreviewMap { get; private set; }
	/// <summary>File-backed texture for HUD/UI. <see cref="BiomePreviewMap"/> from Bitmap.ToTexture is inspector-oriented.</summary>
	public Texture HudBiomeMapTexture { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly] public int BiomePreviewMapSeed { get; private set; } = int.MinValue;
	[Property, Group( "Preview Map" ), ReadOnly] public bool IsBiomePreviewMapStale { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly] public bool HasStreamPosition { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly] public Vector3 StreamWorldPosition { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly, Title( "Stream X (m from center)" )] public float StreamXMeters { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly, Title( "Stream Y (m from center)" )] public float StreamYMeters { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly, Title( "Stream Z / elevation (m)" )] public float StreamElevationMeters { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly] public int StreamChunkX { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly] public int StreamChunkY { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly] public float StreamHeadingDegrees { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly] public Vector2 StreamLookDirectionMap { get; private set; }

	readonly Dictionary<TerrainChunkCoord, LoadedChunk> _loaded = new();

	void UpdateBiomePreviewStaleState()
	{
		IsBiomePreviewMapStale = BiomePreviewMap.IsValid()
			&& BiomePreviewMapSeed != int.MinValue
			&& BiomePreviewMapSeed != WorldSeed;
	}

	readonly HashSet<TerrainChunkCoord> _neededScratch = new();
	readonly HashSet<TerrainChunkCoord> _streamNeeded = new();
	readonly HashSet<TerrainChunkCoord> _streamKeep = new();
	readonly Queue<TerrainChunkCoord> _streamLoadQueue = new();
	readonly List<TerrainChunkCoord> _streamSortScratch = new();
	readonly List<TerrainChunkCoord> _unloadScratch = new();
	readonly Queue<TerrainChunkCoord> _initialChunkQueue = new();
	ITerrainPreviewBackend _backend;
	TerrainWorldPreviewJob _previewJob;
	TerrainWorldLoadScreenHost _loadScreen;
	TerrainMinimapScreenHost _minimapScreen;
	GameObject _minimapCameraObject;
	bool _minimapHostResolved;
	bool _hasLocalPlayerMinimapHost;
	int _hudBiomeMapSeed = int.MinValue;
	TerrainPreviewSettings _loadSettings;
	Vector3 _loadStreamPos;
	Rotation _loadViewRotation;
	int _initialChunksTotal;
	int _initialChunksLoaded;
	int _lastPreviewSeed = int.MinValue;
	float _lastPreviewDiameter = -1f;
	float _lastPreviewMetersPerPixel = -1f;
	int _lastChunkSeed = int.MinValue;
	TerrainChunkCoord _lastStreamRefreshChunk;
	int _lastStreamRefreshHeadingBucket = int.MinValue;
	bool _isWorldLoading;
	bool _initialChunksQueued;
	TerrainPreviewSettings _generationSettings;
	int _generationSettingsSeed = int.MinValue;
	float _generationSettingsDiameter = -1f;
	float _generationSettingsMaxHeight = -1f;
	float _generationSettingsOceanRing = -1f;
	bool _generationSettingsRunAutoTune;
	TerrainWorldSettingsSource _generationSettingsSource;
	bool _generationSettingsOverrideScalars;
	bool _worldRecipeWritten;
	bool _biomeMapLoadSettled;

	protected override void OnStart()
	{
		base.OnStart();

		if ( GameSceneIdentity.IsMainMenu( Scene ) )
		{
			Enabled = false;
			return;
		}

		EnsureSceneAuthorities();
		WorldSessionState.ApplyTo( this );
		_backend = TerrainPreviewBackendRegistry.Active;
		_ = BuildGenerationSettings();
		_lastChunkSeed = WorldSeed;
		BeginWorldLoad();
		TryWriteWorldRecipe();
	}

	protected override void OnUpdate()
	{
		if ( WorldSeed != _lastChunkSeed )
		{
			_lastChunkSeed = WorldSeed;
			InvalidateGenerationSettingsCache();
			UnloadAll();
			BeginWorldLoad();
			return;
		}

		if ( _isWorldLoading )
		{
			ProcessWorldLoad();
			UpdateStreamInspectorState();
			EnsureMinimapScreen();
			BuildNavMeshSync.TickPendingLocalBakes( Scene );
			return;
		}

		TryRefreshStreamChunks();
		ProcessStreamChunkQueue();
		if ( IsWorldAuthority() && EntityPopulationEnabled )
			BiomePopulationRespawnQueue.Tick();
		UpdateBiomePreviewStaleState();
		UpdateStreamInspectorState();
		EnsureMinimapScreen();
		BuildNavMeshSync.TickPendingLocalBakes( Scene );
	}

	/// <summary>
	/// terrainTest (and similar) has no NetworkManager — spawn Combat/Vitals authority so scavs and pawns resolve one.
	/// </summary>
	void EnsureSceneAuthorities()
	{
		var scene = Scene;
		if ( !scene.IsValid() )
			return;

		var hasCombat = false;
		foreach ( var auth in scene.GetAllComponents<CombatAuthority>() )
		{
			if ( auth is not null && auth.IsValid() && auth.Enabled )
			{
				hasCombat = true;
				break;
			}
		}

		var hasVitals = false;
		foreach ( var auth in scene.GetAllComponents<VitalsAuthority>() )
		{
			if ( auth is not null && auth.IsValid() && auth.Enabled )
			{
				hasVitals = true;
				break;
			}
		}

		if ( hasCombat && hasVitals )
			return;

		var go = new GameObject( true, "RuntimeAuthorities" );
		go.Parent = GameObject;

		if ( !hasCombat )
			go.Components.Create<CombatAuthority>();

		if ( !hasVitals )
			go.Components.Create<VitalsAuthority>();
	}

	void UpdateStreamInspectorState()
	{
		if ( !TryGetStreamTransform( out var worldPos, out var viewRotation ) )
		{
			HasStreamPosition = false;
			StreamXMeters = 0f;
			StreamYMeters = 0f;
			StreamElevationMeters = 0f;
			return;
		}

		HasStreamPosition = true;
		StreamWorldPosition = worldPos;
		StreamXMeters = TerrainWorldUnits.EngineToMeters( worldPos.x );
		StreamYMeters = TerrainWorldUnits.EngineToMeters( worldPos.y );
		StreamElevationMeters = TerrainWorldUnits.EngineToMeters( worldPos.z );

		var settings = BuildGenerationSettings();
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var streamPosMeters = TerrainWorldUnits.EngineToMeters( worldPos );
		var chunk = TerrainChunkStreaming.WorldToChunkCoord(
			streamPosMeters.x,
			streamPosMeters.y,
			settings.TotalWorldRadiusMeters,
			chunkSize );
		StreamChunkX = chunk.X;
		StreamChunkY = chunk.Y;

		var forward = viewRotation.Forward.WithZ( 0f );
		if ( forward.LengthSquared > 1e-6f )
		{
			forward = forward.Normal;
			StreamHeadingDegrees = MathF.Atan2( forward.y, forward.x ) * (180f / MathF.PI);
			StreamLookDirectionMap = TerrainBiomeMapCoordinates.WorldForwardToPreviewMapDirection( forward );
		}
		else
		{
			StreamHeadingDegrees = 0f;
			StreamLookDirectionMap = Vector2.Zero;
		}
	}

	void TryRefreshStreamChunks()
	{
		if ( !TryGetStreamTransform( out var streamPosEngine, out var viewRotation ) )
			return;

		var streamPosMeters = TerrainWorldUnits.EngineToMeters( streamPosEngine );
		var settings = BuildGenerationSettings();
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var chunk = TerrainChunkStreaming.WorldToChunkCoord(
			streamPosMeters.x,
			streamPosMeters.y,
			settings.TotalWorldRadiusMeters,
			chunkSize );

		// Uniform radius: only refresh when the player moves to a new chunk (look direction irrelevant).
		if ( !UseForwardConeStreaming )
		{
			if ( chunk == _lastStreamRefreshChunk )
				return;

			_lastStreamRefreshChunk = chunk;
			_lastStreamRefreshHeadingBucket = int.MinValue;
			RefreshChunks( streamPosMeters, viewRotation );
			return;
		}

		var forward = viewRotation.Forward.WithZ( 0f );
		var headingDegrees = 0f;
		if ( forward.LengthSquared > 1e-6f )
		{
			forward = forward.Normal;
			headingDegrees = MathF.Atan2( forward.y, forward.x ) * (180f / MathF.PI);
		}

		// Cone mode: ~60° buckets so looking around does not thrash every few degrees.
		var headingBucket = (int)MathF.Floor( (headingDegrees + 180f) / 60f );
		if ( chunk == _lastStreamRefreshChunk && headingBucket == _lastStreamRefreshHeadingBucket )
			return;

		_lastStreamRefreshChunk = chunk;
		_lastStreamRefreshHeadingBucket = headingBucket;
		RefreshChunks( streamPosMeters, viewRotation );
	}

	protected override void OnDestroy()
	{
		_previewJob = null;
		_initialChunkQueue.Clear();
		HideLoadScreen();
		SetStreamerInputEnabled( true );
		UnloadAll();
		base.OnDestroy();
	}

	public TerrainPreviewSettings BuildGenerationSettings()
	{
		if ( _generationSettings is not null
			&& _generationSettingsSeed == WorldSeed
			&& Math.Abs( _generationSettingsDiameter - WorldDiameterMeters ) < 0.01f
			&& Math.Abs( _generationSettingsMaxHeight - MaxTerrainHeightMeters ) < 0.01f
			&& Math.Abs( _generationSettingsOceanRing - OceanRingWidthMeters ) < 0.01f
			&& _generationSettingsRunAutoTune == RunLakeSpawnSolveOnLoad
			&& _generationSettingsSource == SettingsSource
			&& _generationSettingsOverrideScalars == OverrideWorldScalarsFromComponent )
			return _generationSettings;

		_generationSettings = TerrainPreviewSettingsResolver.ResolveForWorldGeneration( new TerrainWorldGenerationRequest
		{
			WorldSeed = WorldSeed,
			WorldDiameterMeters = WorldDiameterMeters,
			MaxTerrainHeightMeters = MaxTerrainHeightMeters,
			OceanRingWidthMeters = OceanRingWidthMeters,
			WorldName = WorldName,
			Source = SettingsSource,
			OverrideWorldScalarsFromComponent = OverrideWorldScalarsFromComponent,
			RunLakeSpawnSolveOnLoad = RunLakeSpawnSolveOnLoad,
		} );

		if ( !OverrideWorldScalarsFromComponent )
		{
			WorldSeed = _generationSettings.WorldSeed;
			WorldDiameterMeters = _generationSettings.WorldDiameterMeters;
			MaxTerrainHeightMeters = _generationSettings.MaxTerrainHeightMeters;
			OceanRingWidthMeters = _generationSettings.OceanRingWidthMeters;
		}

		_generationSettingsSeed = WorldSeed;
		_generationSettingsDiameter = WorldDiameterMeters;
		_generationSettingsMaxHeight = MaxTerrainHeightMeters;
		_generationSettingsOceanRing = OceanRingWidthMeters;
		_generationSettingsRunAutoTune = RunLakeSpawnSolveOnLoad;
		_generationSettingsSource = SettingsSource;
		_generationSettingsOverrideScalars = OverrideWorldScalarsFromComponent;
		return _generationSettings;
	}

	/// <summary>Ground elevation at world meters — same sampler as chunk meshes.</summary>
	public bool TrySampleGroundMeters( float worldXMeters, float worldYMeters, out float groundZMeters )
		=> TerrainHeightQuery.TrySampleGroundMeters( BuildGenerationSettings(), worldXMeters, worldYMeters, out groundZMeters );

	/// <summary>PNG/inspector map post-process only — does not affect streamed meshes.</summary>
	public TerrainBiomeMapPreviewOptions BuildPreviewMapOptions()
		=> TerrainBiomeMapPreviewOptions.FromSettings( BuildGenerationSettings() );

	void InvalidateGenerationSettingsCache()
	{
		_generationSettings = null;
		_generationSettingsSeed = int.MinValue;
		_generationSettingsDiameter = -1f;
		_generationSettingsMaxHeight = -1f;
		_generationSettingsOceanRing = -1f;
		_generationSettingsRunAutoTune = false;
		_generationSettingsSource = SettingsSource;
		_generationSettingsOverrideScalars = OverrideWorldScalarsFromComponent;
		_worldRecipeWritten = false;
	}

	/// <summary>Meters from world center (0,0,0); negatives allowed on all axes.</summary>
	public string FormatStreamPositionMetersFromCenter()
		=> $"X {StreamXMeters:0.#} m · Y {StreamYMeters:0.#} m · Z {StreamElevationMeters:0.#} m";

	public int ComputeBiomePreviewResolution()
	{
		var settings = BuildGenerationSettings();
		if ( BiomePreviewMetersPerPixel <= 0f )
			return settings.ClampedResolution;

		var metersPerPixel = BiomePreviewMetersPerPixel;
		var resolution = (int)MathF.Ceiling( WorldDiameterMeters / metersPerPixel );
		var maxResolution = Math.Max( 512, BiomePreviewMapMaxResolution );
		return Math.Clamp( resolution, 64, maxResolution );
	}

	public void CancelBiomePreviewMapGeneration()
	{
		_previewJob = null;
		IsMapGenerating = false;
	}

	public void StartBiomePreviewGeneration()
	{
		if ( _previewJob is not null )
			return;

		_backend ??= TerrainPreviewBackendRegistry.Active;

		var settings = BuildGenerationSettings();
		var resolution = ComputeBiomePreviewResolution();
		EffectiveBiomePreviewResolution = resolution;
		EffectiveMetersPerPixel = WorldDiameterMeters / resolution;

		Log.Info( $"[TerrainWorldManager] Biome preview map starting — {resolution}×{resolution}, seed {WorldSeed}." );

		_previewJob = TerrainWorldPreviewJob.Create(
			settings,
			_backend,
			BuildPreviewMapOptions(),
			resolution );

		IsMapGenerating = true;
		MapGenerationProgress01 = 0f;
		MapGenerationStatus = $"Biome map {resolution}×{resolution}…";
	}

	/// <summary>Editor helper — builds the biome map immediately.</summary>
	public void RegenerateBiomePreviewMap()
	{
		_backend ??= TerrainPreviewBackendRegistry.Active;

		try
		{
			CancelBiomePreviewMapGeneration();
			var settings = BuildGenerationSettings();
			TerrainPreviewLandDiskFields.EnsureReady( settings );
			StartBiomePreviewGeneration();

			while ( _previewJob is not null && !_previewJob.IsComplete )
				_previewJob.Step( int.MaxValue );

			FinishMapGeneration();
		}
		catch ( Exception e )
		{
			_previewJob = null;
			IsMapGenerating = false;
			MapGenerationStatus = $"Biome map failed: {e.Message}";
			Log.Error( $"[TerrainWorldManager] RegenerateBiomePreviewMap failed: {e}" );
		}
	}

	void TickMapGeneration()
	{
		if ( _previewJob is null )
			return;

		var rows = Math.Clamp( PreviewMapRowsPerFrame, 4, 512 );
		_previewJob.Step( rows );

		MapGenerationProgress01 = _previewJob.Progress01;
		MapGenerationStatus =
			$"Biome map {_previewJob.RowsCompleted}/{_previewJob.Resolution} ({MapGenerationProgress01 * 100f:0}%)";

		if ( !_previewJob.IsComplete )
			return;

		FinishMapGeneration();
	}

	void BeginWorldLoad()
	{
		_initialChunksQueued = false;
		_isWorldLoading = true;
		IsWorldLoading = true;
		_initialChunkQueue.Clear();
		_initialChunksTotal = 0;
		_initialChunksLoaded = 0;
		_loadSettings = BuildGenerationSettings();
		_biomeMapLoadSettled = !RegeneratePreviewOnStart;

		SetStreamerInputEnabled( false );

		if ( ShowWorldLoadScreen )
			ShowLoadScreen( "Loading World", "Preparing terrain…", 0f );

		TerrainPreviewLandDiskFields.EnsureReady( _loadSettings );

		if ( RegeneratePreviewOnStart )
			StartBiomePreviewGeneration();
	}

	void ProcessWorldLoad()
	{
		ResolveLoadStreamTransform();

		if ( !_initialChunksQueued )
		{
			QueueInitialChunks();
			_initialChunksQueued = true;
		}

		ProcessInitialChunkQueue();

		if ( RegeneratePreviewOnStart )
			TickMapGeneration();

		UpdateLoadScreenProgress();

		if ( !IsWorldLoadComplete() )
			return;

		FinishWorldLoad();
	}

	void ResolveLoadStreamTransform()
	{
		var settings = _loadSettings;
		var sample = _backend.Sample( settings, 0f, 0f );
		var groundZMeters = sample.IsInsideWorld ? sample.HeightMeters : 0f;
		_loadStreamPos = new Vector3( 0f, 0f, groundZMeters );
		_loadViewRotation = TryGetStreamTransform( out _, out var viewRotation )
			? viewRotation
			: Rotation.Identity;
	}

	void QueueInitialChunks()
	{
		_initialChunkQueue.Clear();

		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		// Initial load uses the same uniform radius (or side square when cone mode is on).
		TerrainChunkStreaming.CollectSquareChunks(
			_loadStreamPos,
			_loadSettings,
			chunkSize,
			ResolveInitialStreamRadiusChunks(),
			_neededScratch );

		foreach ( var coord in _neededScratch )
			_initialChunkQueue.Enqueue( coord );

		_initialChunksTotal = _initialChunkQueue.Count;
		_initialChunksLoaded = 0;
	}

	void CollectStreamChunks(
		Vector3 streamPos,
		Rotation viewRotation,
		TerrainPreviewSettings settings,
		float chunkSize,
		HashSet<TerrainChunkCoord> needed )
	{
		if ( UseForwardConeStreaming )
		{
			var forwardDistance = ResolveForwardViewDistanceMeters( chunkSize );
			TerrainChunkStreaming.CollectNeededChunks(
				streamPos,
				viewRotation,
				settings,
				chunkSize,
				forwardDistance,
				ForwardViewConeDegrees,
				SideViewRadiusChunks,
				needed );
			return;
		}

		TerrainChunkStreaming.CollectSquareChunks(
			streamPos,
			settings,
			chunkSize,
			Math.Max( 1, StreamRadiusChunks ),
			needed );
	}

	void CollectStreamKeepChunks(
		Vector3 streamPos,
		Rotation viewRotation,
		TerrainPreviewSettings settings,
		float chunkSize,
		HashSet<TerrainChunkCoord> keep )
	{
		if ( UseForwardConeStreaming )
		{
			var forwardDistance = ResolveForwardViewDistanceMeters( chunkSize )
				+ Math.Max( 0, StreamUnloadMarginChunks ) * chunkSize;
			TerrainChunkStreaming.CollectNeededChunks(
				streamPos,
				viewRotation,
				settings,
				chunkSize,
				forwardDistance,
				ForwardViewConeDegrees,
				SideViewRadiusChunks + Math.Max( 0, StreamUnloadMarginChunks ),
				keep );
			return;
		}

		TerrainChunkStreaming.CollectSquareChunks(
			streamPos,
			settings,
			chunkSize,
			Math.Max( 1, StreamRadiusChunks ) + Math.Max( 0, StreamUnloadMarginChunks ),
			keep );
	}

	int ResolveInitialStreamRadiusChunks()
	{
		if ( UseForwardConeStreaming )
			return Math.Max( 1, SideViewRadiusChunks );

		return Math.Max( 1, StreamRadiusChunks );
	}

	void ProcessStreamChunkQueue()
	{
		if ( _isWorldLoading || _streamLoadQueue.Count == 0 )
			return;

		if ( !TryGetStreamTransform( out var streamPosEngine, out _ ) )
			return;

		var streamPosMeters = TerrainWorldUnits.EngineToMeters( streamPosEngine );
		var settings = BuildGenerationSettings();
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var maxChunks = Math.Clamp( ChunksPerFrame, 1, 12 );
		var budgetMs = Math.Clamp( StreamMeshBuildBudgetMs, 4f, 32f );
		var stopwatch = Stopwatch.StartNew();
		var built = 0;

		while ( _streamLoadQueue.Count > 0 && built < maxChunks )
		{
			if ( built > 0 && stopwatch.Elapsed.TotalMilliseconds >= budgetMs )
				break;

			var coord = _streamLoadQueue.Dequeue();
			if ( !_streamNeeded.Contains( coord ) || _loaded.ContainsKey( coord ) )
				continue;

			LoadChunk( coord, settings, streamPosMeters, visible: true, useStreamLod: true );
			built++;
		}

		MeshedChunkCount = _loaded.Count;
	}

	void RebuildStreamLoadQueue( Vector3 streamPos, TerrainPreviewSettings settings, float chunkSize )
	{
		_streamLoadQueue.Clear();
		_streamSortScratch.Clear();

		var worldRadius = settings.TotalWorldRadiusMeters;
		foreach ( var coord in _streamNeeded )
		{
			if ( !_loaded.ContainsKey( coord ) )
				_streamSortScratch.Add( coord );
		}

		_streamSortScratch.Sort( ( a, b ) =>
		{
			var da = ChunkDistanceMeters( a, streamPos, worldRadius, chunkSize );
			var db = ChunkDistanceMeters( b, streamPos, worldRadius, chunkSize );
			return da.CompareTo( db );
		} );

		foreach ( var coord in _streamSortScratch )
			_streamLoadQueue.Enqueue( coord );
	}

	void LoadNearPriorityChunksSync(
		Vector3 streamPos,
		TerrainPreviewSettings settings,
		float chunkSize )
	{
		var syncBudget = Math.Clamp( StreamMaxSyncChunksPerRefresh, 0, 8 );
		if ( syncBudget <= 0 )
			return;

		var worldRadius = settings.TotalWorldRadiusMeters;
		var priorityRadius = chunkSize * Math.Clamp( StreamHighPriorityRadiusChunks, 1f, 4f );
		_streamSortScratch.Clear();

		foreach ( var coord in _streamNeeded )
		{
			if ( _loaded.ContainsKey( coord ) )
				continue;

			if ( ChunkDistanceMeters( coord, streamPos, worldRadius, chunkSize ) <= priorityRadius )
				_streamSortScratch.Add( coord );
		}

		_streamSortScratch.Sort( ( a, b ) =>
		{
			var da = ChunkDistanceMeters( a, streamPos, worldRadius, chunkSize );
			var db = ChunkDistanceMeters( b, streamPos, worldRadius, chunkSize );
			return da.CompareTo( db );
		} );

		for ( var i = 0; i < _streamSortScratch.Count && syncBudget > 0; i++ )
		{
			var coord = _streamSortScratch[i];
			if ( !_streamNeeded.Contains( coord ) || _loaded.ContainsKey( coord ) )
				continue;

			LoadChunk( coord, settings, streamPos, visible: true, useStreamLod: true );
			syncBudget--;
		}
	}

	static float ChunkDistanceMeters(
		TerrainChunkCoord coord,
		Vector3 streamPos,
		float worldRadius,
		float chunkSize )
	{
		var center = TerrainChunkStreaming.GetChunkCenterWorld( coord, worldRadius, chunkSize );
		return new Vector3( center.x - streamPos.x, center.y - streamPos.y, 0f ).Length;
	}

	void ProcessInitialChunkQueue()
	{
		var maxChunks = Math.Clamp( ChunksPerFrame, 1, 12 );
		var budgetMs = Math.Clamp( StreamMeshBuildBudgetMs, 4f, 32f );
		var stopwatch = Stopwatch.StartNew();
		var built = 0;

		while ( _initialChunkQueue.Count > 0 && built < maxChunks )
		{
			if ( built > 0 && stopwatch.Elapsed.TotalMilliseconds >= budgetMs )
				break;

			var coord = _initialChunkQueue.Dequeue();
			if ( _loaded.ContainsKey( coord ) )
			{
				_initialChunksLoaded++;
				continue;
			}

			LoadChunk( coord, _loadSettings, _loadStreamPos, visible: true, useStreamLod: false );
			_initialChunksLoaded++;
			built++;
		}

		LoadedChunkCount = _loaded.Count;
		MeshedChunkCount = _loaded.Count;
		UpdateChunkColliders( _loadStreamPos, _loadSettings, Math.Max( 32f, ChunkSizeMeters ) );
	}

	bool IsWorldLoadComplete()
	{
		if ( !_initialChunksQueued )
			return false;

		if ( _initialChunkQueue.Count > 0 )
			return false;

		if ( _initialChunksTotal <= 0 )
			return false;

		if ( _initialChunksLoaded < _initialChunksTotal )
			return false;

		if ( _loaded.Count <= 0 )
			return false;

		if ( !RegeneratePreviewOnStart )
			return true;

		if ( !_biomeMapLoadSettled )
			return false;

		return true;
	}

	void UpdateLoadScreenProgress()
	{
		if ( !ShowWorldLoadScreen )
			return;

		var chunkProgress = _initialChunksTotal > 0
			? Math.Clamp( (float)_initialChunksLoaded / _initialChunksTotal, 0f, 1f )
			: 1f;

		var mapProgress = RegeneratePreviewOnStart ? MapGenerationProgress01 : 1f;
		var totalProgress = (chunkProgress * ChunkLoadProgressWeight) + (mapProgress * MapLoadProgressWeight);

		var status = RegeneratePreviewOnStart && ( _previewJob is not null || !BiomePreviewMap.IsValid() )
			? $"Terrain {_initialChunksLoaded}/{_initialChunksTotal} · {MapGenerationStatus}"
			: $"Terrain {_initialChunksLoaded}/{_initialChunksTotal}";

		ShowLoadScreen( "Loading World", status, totalProgress );
	}

	void FinishWorldLoad()
	{
		_isWorldLoading = false;
		IsWorldLoading = false;
		_lastChunkSeed = WorldSeed;

		SnapStreamerCameraToTerrain();
		TerrainPreviewHeightDiagnostics.TryLogSpawnPipelineTrace( _loadSettings, 0f, 0f );
		EnsureChunksAroundStream();
		SetStreamerInputEnabled( true );
		HideLoadScreen();

		Log.Info( $"[TerrainWorldManager] World ready — {MeshedChunkCount} meshed chunks ({LoadedChunkCount} needed in stream zone), seed {WorldSeed}." );

		if ( MeshedChunkCount <= 0 )
			Log.Warning( "[TerrainWorldManager] No terrain chunks meshed — check stream position and world seed." );
	}

	void EnsureChunksAroundStream()
	{
		if ( !TryGetStreamTransform( out var streamPosEngine, out var viewRotation ) )
			return;

		// Camera is engine units; chunk streaming / LoadChunk distances are meters.
		var streamPosMeters = TerrainWorldUnits.EngineToMeters( streamPosEngine );
		RefreshChunks( streamPosMeters, viewRotation );
	}

	void FinishMapGeneration()
	{
		if ( _previewJob is null )
			return;

		Bitmap bitmap = null;
		try
		{
			bitmap = _previewJob.FinishBitmap();
			BiomePreviewMap = bitmap.ToTexture( false );
			if ( !BiomePreviewMap.IsValid() )
				Log.Warning( "[TerrainWorldManager] Biome preview map texture invalid after rasterize." );
		}
		catch ( Exception e )
		{
			Log.Error( $"[TerrainWorldManager] Biome preview map finish failed: {e}" );
			MapGenerationStatus = $"Biome map failed: {e.Message}";
			_previewJob = null;
			IsMapGenerating = false;
			_biomeMapLoadSettled = true;
			return;
		}

		_previewJob = null;
		BiomePreviewMapSeed = WorldSeed;
		_lastPreviewSeed = WorldSeed;
		_lastPreviewDiameter = WorldDiameterMeters;
		_lastPreviewMetersPerPixel = BiomePreviewMetersPerPixel;
		IsMapGenerating = false;
		MapGenerationProgress01 = 1f;
		MapGenerationStatus = BiomePreviewMap.IsValid()
			? $"Map ready (seed {WorldSeed})"
			: "Biome map failed (invalid texture)";
		_biomeMapLoadSettled = true;
		UpdateBiomePreviewStaleState();

		if ( bitmap is not null )
		{
			try
			{
				// Local write so HUD can LoadFromFileSystem (UI-friendly). Not only authority.
				WorldSaveIO.WriteBiomeMapPng( WorldName, bitmap );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[TerrainWorldManager] Failed to write biome map PNG: {e.Message}" );
			}
		}

		RefreshHudBiomeMapTexture( forceReload: true );
	}

	void RefreshChunks( Vector3 streamPos, Rotation viewRotation )
	{
		if ( _isWorldLoading )
			return;

		var settings = BuildGenerationSettings();
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		CollectStreamChunks( streamPos, viewRotation, settings, chunkSize, _streamNeeded );
		CollectStreamKeepChunks( streamPos, viewRotation, settings, chunkSize, _streamKeep );

		_unloadScratch.Clear();
		foreach ( var coord in _loaded.Keys )
		{
			// Unload only outside the keep ring (radius + margin) — load uses the tighter needed set.
			if ( !_streamKeep.Contains( coord ) )
				_unloadScratch.Add( coord );
		}

		foreach ( var coord in _unloadScratch )
			UnloadChunk( coord );

		// Far-LOD chunks loaded without trees (or wrong mesh detail) must rebuild when you arrive.
		QueueStaleChunkReloads( streamPos, settings, chunkSize );

		LoadNearPriorityChunksSync( streamPos, settings, chunkSize );
		RebuildStreamLoadQueue( streamPos, settings, chunkSize );

		LoadedChunkCount = _streamNeeded.Count;
		MeshedChunkCount = _loaded.Count;
		UpdateChunkColliders( streamPos, settings, chunkSize );
	}

	/// <summary>
	/// Unload loaded chunks whose mesh LOD or vegetation no longer matches the camera distance
	/// so <see cref="LoadChunk"/> runs again with the right detail + scatter.
	/// </summary>
	void QueueStaleChunkReloads( Vector3 streamPosMeters, TerrainPreviewSettings settings, float chunkSize )
	{
		var worldRadius = settings.TotalWorldRadiusMeters;

		foreach ( var entry in _loaded.Values )
		{
			if ( !_streamNeeded.Contains( entry.Coord ) )
				continue;

			if ( entry.GameObject is null || !entry.GameObject.IsValid() )
				continue;

			var distance = ChunkDistanceMeters( entry.Coord, streamPosMeters, worldRadius, chunkSize );
			var desiredVerts = ResolveChunkVerticesPerSide( distance, chunkSize, useStreamLod: true );
			var wantVegetation = ShouldScatterVegetation( desiredVerts );

			// Remesh in place — never destroy/respawn trees when LOD changes (that was the "2nd layer").
			if ( entry.VerticesPerSide != desiredVerts )
				RemeshChunkLod( entry, settings, streamPosMeters, desiredVerts, distance );

			if ( wantVegetation && !entry.HasVegetation )
			{
				ScatterVegetationOnChunk( entry.GameObject, entry.Coord, settings, desiredVerts );
				entry.HasVegetation = true;
			}

			if ( ShouldScatterEntityPopulation( desiredVerts, distance, chunkSize ) && !entry.HasEntityPopulation )
			{
				TryPopulateEntitiesOnChunk( entry, settings, distance, chunkSize );
			}
		}
	}

	void TryPopulateEntitiesOnChunk(
		LoadedChunk entry,
		TerrainPreviewSettings settings,
		float distanceMeters,
		float chunkSize )
	{
		if ( entry.HasEntityPopulation )
			return;

		if ( !ShouldScatterEntityPopulation( entry.VerticesPerSide, distanceMeters, chunkSize ) )
			return;

		// Only after terrain collision is live — scavs must not appear on render-only far chunks.
		if ( entry.Collider is null || !entry.Collider.IsValid() || !entry.Collider.Enabled )
			return;

		PrepareChunkColliderForEntityPopulation( entry );
		ScatterEntityPopulationOnChunk( entry.GameObject, entry.Coord, settings );
		entry.HasEntityPopulation = true;
	}

	void PrepareChunkColliderForEntityPopulation( LoadedChunk entry )
	{
		if ( entry.Collider is null || !entry.Collider.IsValid() )
			return;

		var wasEnabled = entry.Collider.Enabled;
		entry.Collider.Enabled = true;
		if ( wasEnabled || !entry.GameObject.IsValid() )
			return;

		var bounds = entry.GameObject.GetBounds();
		if ( bounds.Size.LengthSquared < 1f )
			bounds = BBox.FromPositionAndSize( entry.GameObject.WorldPosition, ChunkSizeMeters * 50f );
		BuildNavMeshSync.NotifyTerrainChunkLoaded( GameObject.Scene, bounds );
	}

	void RemeshChunkLod(
		LoadedChunk entry,
		TerrainPreviewSettings settings,
		Vector3 streamPos,
		int verticesPerSide,
		float distance )
	{
		var smoothPasses = verticesPerSide < ChunkVerticesPerSide ? 0 : ChunkHeightSmoothPasses;
		var built = TerrainMeshBuilder.BuildChunk(
			settings,
			_backend,
			entry.Coord,
			ChunkSizeMeters,
			verticesPerSide,
			MaxTerrainHeightMeters,
			smoothPasses,
			ChunkHeightSmoothStrength01 );

		if ( built.Model is null || !built.Model.IsValid )
			return;

		var renderer = entry.GameObject.Components.Get<ModelRenderer>();
		if ( renderer is not null && renderer.IsValid() )
		{
			renderer.Model = built.Model;
			renderer.MaterialOverride = built.Material;
		}

		if ( entry.Collider is not null && entry.Collider.IsValid() )
		{
			entry.Collider.Model = built.Model;
			entry.Collider.Static = true;
			entry.Collider.Enabled = ShouldKeepChunkColliderEnabled( entry, distance, ChunkSizeMeters );
		}

		entry.VerticesPerSide = verticesPerSide;
	}

	float ResolveForwardViewDistanceMeters( float chunkSize )
	{
		var fromChunks = Math.Max( 1, ForwardViewRadiusChunks ) * chunkSize;
		return ViewDistanceMeters > chunkSize ? ViewDistanceMeters : fromChunks;
	}

	void UpdateChunkColliders( Vector3 streamPos, TerrainPreviewSettings settings, float chunkSize )
	{
		var worldRadius = settings.TotalWorldRadiusMeters;

		foreach ( var entry in _loaded.Values )
		{
			if ( entry.Collider is null || !entry.Collider.IsValid() )
				continue;

			var center = TerrainChunkStreaming.GetChunkCenterWorld( entry.Coord, worldRadius, chunkSize );
			var distance = new Vector3( center.x - streamPos.x, center.y - streamPos.y, 0f ).Length;
			var wantEnabled = ShouldKeepChunkColliderEnabled( entry, distance, chunkSize );
			var wasEnabled = entry.Collider.Enabled;
			entry.Collider.Enabled = wantEnabled;

			// Terrain collision just came online — bake nav, then spawn scavs (after terrain).
			if ( !wasEnabled && wantEnabled && entry.GameObject.IsValid() )
			{
				var bounds = entry.GameObject.GetBounds();
				if ( bounds.Size.LengthSquared < 1f )
					bounds = BBox.FromPositionAndSize( entry.GameObject.WorldPosition, chunkSize * 50f );
				BuildNavMeshSync.NotifyTerrainChunkLoaded( GameObject.Scene, bounds );
				TryPopulateEntitiesOnChunk( entry, settings, distance, chunkSize );
			}
		}
	}

	bool IsChunkInCollisionRange( float distanceMeters, float chunkSize ) =>
		distanceMeters <= Math.Max( 0f, CollisionRangeMeters ) + (chunkSize * 0.75f);

	bool ShouldKeepChunkColliderEnabled( LoadedChunk entry, float distanceMeters, float chunkSize ) =>
		entry.HasEntityPopulation || IsChunkInCollisionRange( distanceMeters, chunkSize );

	bool TryGetStreamTransform( out Vector3 worldPos, out Rotation viewRotation )
	{
		worldPos = default;
		viewRotation = Rotation.Identity;

		var cam = ResolveStreamCamera();
		if ( cam.IsValid() )
		{
			worldPos = cam.WorldPosition;
			viewRotation = cam.WorldRotation;
			return true;
		}

		worldPos = GameObject.WorldPosition;
		viewRotation = GameObject.WorldRotation;
		return true;
	}

	CameraComponent ResolveStreamCamera()
	{
		var scene = GameObject.Scene;
		if ( !scene.IsValid() )
			return default;

		return scene.Camera;
	}

	void LoadChunk(
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		Vector3 streamPos,
		bool visible,
		bool useStreamLod )
	{
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var worldRadius = settings.TotalWorldRadiusMeters;
		var distance = ChunkDistanceMeters( coord, streamPos, worldRadius, chunkSize );
		var verticesPerSide = ResolveChunkVerticesPerSide( distance, chunkSize, useStreamLod );
		var smoothPasses = useStreamLod && verticesPerSide < ChunkVerticesPerSide
			? 0
			: ChunkHeightSmoothPasses;

		var built = TerrainMeshBuilder.BuildChunk(
			settings,
			_backend,
			coord,
			ChunkSizeMeters,
			verticesPerSide,
			MaxTerrainHeightMeters,
			smoothPasses,
			ChunkHeightSmoothStrength01 );

		if ( built.Model is null || !built.Model.IsValid )
		{
			Log.Warning( $"[TerrainWorldManager] Failed to build terrain mesh for chunk {coord}." );
			return;
		}

		var chunkMinX = -settings.TotalWorldRadiusMeters + (coord.X * ChunkSizeMeters);
		var chunkMinY = -settings.TotalWorldRadiusMeters + (coord.Y * ChunkSizeMeters);
		var chunkOriginEngine = TerrainWorldUnits.MetersToEngine( new Vector3( chunkMinX, chunkMinY, 0f ) );

		var go = new GameObject( true, $"TerrainChunk {coord}" );
		go.Parent = GameObject;
		go.WorldPosition = chunkOriginEngine;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = built.Model;
		renderer.MaterialOverride = built.Material;
		renderer.Enabled = visible;

		var collider = go.Components.Create<ModelCollider>();
		collider.Model = built.Model;
		collider.Static = true;
		collider.Enabled = IsChunkInCollisionRange( distance, chunkSize );

		var hasVegetation = false;
		if ( ShouldScatterVegetation( verticesPerSide ) )
		{
			ScatterVegetationOnChunk( go, coord, settings, verticesPerSide );
			hasVegetation = true;
		}

		_loaded[coord] = new LoadedChunk
		{
			GameObject = go,
			Collider = collider,
			Coord = coord,
			VerticesPerSide = verticesPerSide,
			HasVegetation = hasVegetation,
			HasEntityPopulation = false,
		};

		// Nav + scavs only when collision is live — far render chunks must not schedule Recast.
		if ( collider.Enabled )
		{
			var chunkBounds = new BBox(
				chunkOriginEngine + built.LocalBounds.Mins,
				chunkOriginEngine + built.LocalBounds.Maxs );
			BuildNavMeshSync.NotifyTerrainChunkLoaded( GameObject.Scene, chunkBounds );
			TryPopulateEntitiesOnChunk( _loaded[coord], settings, distance, chunkSize );
		}
	}

	void ScatterEntityPopulationOnChunk(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings )
	{
		BiomePopulationScatter.PopulateChunk(
			chunkRoot,
			coord,
			settings,
			_backend,
			ChunkSizeMeters,
			settings.WorldSeed );
	}

	void ScatterVegetationOnChunk(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		int verticesPerSide )
	{
		TerrainVegetationScatter.PopulateChunk(
			chunkRoot,
			coord,
			settings,
			_backend,
			ChunkSizeMeters,
			verticesPerSide,
			ChunkVerticesPerSide,
			new TerrainVegetationScatter.Options
			{
				Enabled = true,
				Profiles =
				[
					new TerrainVegetationScatter.BiomeScatterProfile
					{
						BiomeId = TerrainPreviewBiomeId.CloverHills,
						PrefabA = VegetationPrefabA,
						PrefabB = VegetationPrefabB,
						PrefabAWeight01 = VegetationPrefabAWeight01,
						NoiseSeedSalt = 0,
						InstancePrefix = "veg_clover",
						Density01 = 1f,
					},
					new TerrainVegetationScatter.BiomeScatterProfile
					{
						BiomeId = TerrainPreviewBiomeId.RedwoodForest,
						PrefabA = VegetationRedwoodPrefab,
						PrefabB = VegetationRedwoodPrefabB,
						PrefabAWeight01 = VegetationRedwoodPrefabAWeight01,
						NoiseSeedSalt = 5000,
						InstancePrefix = "veg_redwood",
						Density01 = VegetationRedwoodDensity01,
						IgnoreForestPatches = true,
					},
				],
				PatchWavelengthMeters = VegetationPatchWavelengthMeters,
				PatchThreshold01 = VegetationPatchThreshold01,
				CellSpacingMeters = VegetationCellSpacingMeters,
				SpawnChanceInPatch01 = VegetationSpawnChance01,
				YawJitterDegrees = 360f,
				ScaleMin = VegetationScaleMin,
				ScaleMax = VegetationScaleMax,
				MaxTreesPerChunk = VegetationMaxTreesPerChunk,
				SkipFarLodChunks = VegetationSkipFarLodChunks,
				NearLargeTreeSticksEnabled = VegetationCloverSticksEnabled && VegetationCloverStickNearLargeTreeEnabled,
				NearLargeTreeStickPrefab = VegetationCloverStickPrefab,
				NearLargeTreeStickChance01 = VegetationCloverStickNearLargeTreeChance01,
				NearLargeTreeStickMinRadiusMeters = VegetationCloverStickNearLargeTreeMinRadiusMeters,
				NearLargeTreeStickMaxRadiusMeters = VegetationCloverStickNearLargeTreeMaxRadiusMeters,
				PropClusters =
				[
					new TerrainVegetationScatter.PropClusterOptions
					{
						Enabled = VegetationCloverRocksEnabled,
						BiomeId = TerrainPreviewBiomeId.CloverHills,
						Prefab = VegetationCloverRockPrefab,
						InstancePrefix = "veg_rock",
						KindLabel = "rock",
						NoiseSeedSalt = 7000,
						ClusterSpacingMeters = VegetationCloverRockSpacingMeters,
						ClusterChance01 = VegetationCloverRockChance01,
						ClusterSizeMin = VegetationCloverRockClusterMin,
						ClusterSizeMax = VegetationCloverRockClusterMax,
						ClusterRadiusMeters = VegetationCloverRockClusterRadiusMeters,
						ScaleMin = 0.85f,
						ScaleMax = 1.2f,
						MaxPerChunk = 48,
					},
					new TerrainVegetationScatter.PropClusterOptions
					{
						Enabled = VegetationCloverSticksEnabled,
						BiomeId = TerrainPreviewBiomeId.CloverHills,
						Prefab = VegetationCloverStickPrefab,
						InstancePrefix = "veg_stick",
						KindLabel = "stick",
						NoiseSeedSalt = 9100,
						ClusterSpacingMeters = VegetationCloverStickSpacingMeters,
						ClusterChance01 = VegetationCloverStickChance01,
						ClusterSizeMin = VegetationCloverStickClusterMin,
						ClusterSizeMax = VegetationCloverStickClusterMax,
						ClusterRadiusMeters = VegetationCloverStickClusterRadiusMeters,
						ScaleMin = 0.85f,
						ScaleMax = 1.15f,
						MaxPerChunk = 24,
					},
				],
			} );
	}

	bool ShouldScatterVegetation( int verticesPerSide )
	{
		if ( !VegetationScatterEnabled || !IsWorldAuthority() )
			return false;

		if ( VegetationSkipFarLodChunks && verticesPerSide < ChunkVerticesPerSide )
			return false;

		return true;
	}

	bool ShouldScatterEntityPopulation( int verticesPerSide, float distanceMeters, float chunkSize )
	{
		if ( !EntityPopulationEnabled || !IsWorldAuthority() )
			return false;

		// Match vegetation far-LOD skip so sparse AI only lives on detailed chunks.
		if ( VegetationSkipFarLodChunks && verticesPerSide < ChunkVerticesPerSide )
			return false;

		// Never spawn scavs outside collision range — far mesh is render-only.
		return IsChunkInCollisionRange( distanceMeters, chunkSize );
	}

	int ResolveChunkVerticesPerSide( float distanceMeters, float chunkSize, bool useStreamLod )
	{
		var fullDetail = Math.Clamp( ChunkVerticesPerSide, 4, 256 );
		if ( !useStreamLod || !StreamMeshLodEnabled )
			return fullDetail;

		var priorityRadius = chunkSize * Math.Clamp( StreamHighPriorityRadiusChunks, 1f, 4f );
		if ( distanceMeters <= priorityRadius )
			return fullDetail;

		var farDetail = Math.Clamp( StreamFarVerticesPerSide, 9, fullDetail );
		return farDetail;
	}

	void UnloadChunk( TerrainChunkCoord coord )
	{
		if ( !_loaded.TryGetValue( coord, out var entry ) )
			return;

		entry.GameObject.Destroy();
		_loaded.Remove( coord );
	}

	void UnloadAll()
	{
		foreach ( var entry in _loaded.Values )
			entry.GameObject.Destroy();

		_loaded.Clear();
		_streamNeeded.Clear();
		_streamKeep.Clear();
		_streamLoadQueue.Clear();
		_unloadScratch.Clear();
		LoadedChunkCount = 0;
		MeshedChunkCount = 0;
		_lastStreamRefreshHeadingBucket = int.MinValue;
		BiomePopulationRegistry.Clear();
		BiomePopulationRespawnQueue.Clear();
		InvalidateMinimapHostCache();
	}

	void TryWriteWorldRecipe()
	{
		if ( _worldRecipeWritten || !IsWorldAuthority() )
			return;

		try
		{
			var recipe = BuildWorldRecipe();
			WorldSaveIO.WriteRecipe( recipe );
			_worldRecipeWritten = true;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[TerrainWorldManager] Failed to write world recipe: {e.Message}" );
		}
	}

	WorldSaveRecipe BuildWorldRecipe()
	{
		var existing = WorldSaveIO.TryReadRecipe( WorldName );
		return new WorldSaveRecipe
		{
			GameVersion = GameBuildLabel.Display,
			WorldName = WorldName,
			WorldSeed = WorldSeed,
			WorldDiameterMeters = WorldDiameterMeters,
			MaxTerrainHeightMeters = MaxTerrainHeightMeters,
			OceanRingWidthMeters = OceanRingWidthMeters,
			ChunkSizeMeters = ChunkSizeMeters,
			BiomePreviewMetersPerPixel = BiomePreviewMetersPerPixel,
			FirstGeneratedUtc = existing?.FirstGeneratedUtc ?? "",
			LastLoadedUtc = existing?.LastLoadedUtc ?? "",
			DayNumber = existing is not null
				? WorldSaveIO.NormalizeDayNumber( existing.DayNumber )
				: 1,
			PreviewSettings = BuildGenerationSettings(),
		};
	}

	static bool IsWorldAuthority()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return true;

		return scene.Network?.Active != true || Networking.IsHost;
	}

	void SnapStreamerCameraToTerrain()
	{
		var cam = ResolveStreamCamera();
		if ( !cam.IsValid() )
			return;

		var settings = BuildGenerationSettings();
		var sample = _backend.Sample( settings, 0f, 0f );
		var groundZMeters = sample.IsInsideWorld ? sample.HeightMeters : 0f;
		var viewHeight = Math.Max( ChunkSizeMeters * 0.75f, 128f );
		var lookAhead = Math.Max( ChunkSizeMeters * 0.5f, 64f );

		var fly = cam.Components.Get<TerrainTestFlyCamera>();
		if ( fly is not null && fly.IsValid() )
		{
			fly.SnapToTerrainView( groundZMeters, viewHeight, lookAhead );
			return;
		}

		var groundEngine = TerrainWorldUnits.MetersToEngine( groundZMeters );
		var viewHeightEngine = TerrainWorldUnits.MetersToEngine( viewHeight );
		var lookAheadEngine = TerrainWorldUnits.MetersToEngine( lookAhead );
		cam.WorldPosition = new Vector3( 0f, -lookAheadEngine * 0.35f, groundEngine + viewHeightEngine );
		var lookTarget = new Vector3( 0f, lookAheadEngine, groundEngine );
		cam.WorldRotation = Rotation.LookAt( (lookTarget - cam.WorldPosition).Normal, Vector3.Up );
	}

	void SetStreamerInputEnabled( bool enabled )
	{
		var cam = ResolveStreamCamera();
		if ( !cam.IsValid() )
			return;

		var fly = cam.Components.Get<TerrainTestFlyCamera>();
		if ( fly is null || !fly.IsValid() )
			return;

		fly.Enabled = true;
		fly.SetInputLocked( !enabled );
	}

	void ShowLoadScreen( string title, string status, float progress01 )
	{
		if ( !EnsureLoadScreen() )
			return;

		_loadScreen.Show( title, status, progress01 );
	}

	void HideLoadScreen()
	{
		if ( _loadScreen is null || !_loadScreen.IsValid() )
			return;

		_loadScreen.Hide();
	}

	bool EnsureLoadScreen()
	{
		if ( _loadScreen is not null && _loadScreen.IsValid() )
			return true;

		var scene = GameObject.Scene;
		if ( !scene.IsValid() )
			return false;

		var cam = ResolveStreamCamera();
		if ( !cam.IsValid() )
			return false;

		_loadScreen = cam.Components.Get<TerrainWorldLoadScreenHost>();
		if ( _loadScreen is null || !_loadScreen.IsValid() )
			_loadScreen = cam.Components.Create<TerrainWorldLoadScreenHost>();

		return _loadScreen is not null && _loadScreen.IsValid();
	}

	/// <summary>Texture safe to bind on Panels / Image (PNG reload preferred).</summary>
	public Texture GetHudBiomeMapTexture()
	{
		RefreshHudBiomeMapTexture( forceReload: false );
		if ( HudBiomeMapTexture is not null && HudBiomeMapTexture.IsValid() )
			return HudBiomeMapTexture;

		return BiomePreviewMap is not null && BiomePreviewMap.IsValid() ? BiomePreviewMap : null;
	}

	void RefreshHudBiomeMapTexture( bool forceReload )
	{
		if ( !forceReload
		     && HudBiomeMapTexture is not null
		     && HudBiomeMapTexture.IsValid()
		     && _hudBiomeMapSeed == WorldSeed )
			return;

		_hudBiomeMapSeed = WorldSeed;
		HudBiomeMapTexture = null;

		try
		{
			var path = WorldSaveIO.GetBiomeMapRelativePath( WorldName );
			if ( FileSystem.Data.FileExists( path ) )
			{
				var loaded = Texture.LoadFromFileSystem( path, FileSystem.Data, warnOnMissing: false );
				if ( loaded is not null && loaded.IsValid() )
				{
					HudBiomeMapTexture = loaded;
					return;
				}
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[TerrainWorldManager] HUD biome map load failed: {e.Message}" );
		}

		if ( BiomePreviewMap is not null && BiomePreviewMap.IsValid() )
			HudBiomeMapTexture = BiomePreviewMap;
	}

	bool EnsureMinimapScreen()
	{
		// Player HUD owns the minimap when a local pawn ScreenPanel is present.
		if ( HasLocalPlayerMinimapHostCached() )
			return true;

		var scene = GameObject.Scene;
		if ( !scene.IsValid() )
			return false;

		var cam = ResolveStreamCamera();
		if ( !cam.IsValid() )
			return false;

		if ( _minimapScreen is not null && _minimapScreen.IsValid()
		     && _minimapCameraObject is not null && _minimapCameraObject.IsValid()
		     && _minimapCameraObject == cam.GameObject )
			return _minimapScreen.EnsureScreen();

		if ( _minimapScreen is not null && _minimapScreen.IsValid() )
			_minimapScreen.Destroy();

		_minimapScreen = cam.Components.Get<TerrainMinimapScreenHost>();
		if ( _minimapScreen is null || !_minimapScreen.IsValid() )
			_minimapScreen = cam.Components.Create<TerrainMinimapScreenHost>();

		_minimapCameraObject = cam.GameObject;
		return _minimapScreen is not null && _minimapScreen.IsValid() && _minimapScreen.EnsureScreen();
	}

	bool HasLocalPlayerMinimapHostCached()
	{
		if ( _minimapHostResolved )
			return _hasLocalPlayerMinimapHost;

		_minimapHostResolved = true;
		_hasLocalPlayerMinimapHost = ScanLocalPlayerMinimapHost();
		return _hasLocalPlayerMinimapHost;
	}

	/// <summary>Call when a local pawn HUD may have appeared (e.g. L spawn) so minimap ownership refreshes.</summary>
	public void InvalidateMinimapHostCache()
	{
		_minimapHostResolved = false;
	}

	bool ScanLocalPlayerMinimapHost()
	{
		var scene = GameObject.Scene;
		if ( !scene.IsValid() )
			return false;

		foreach ( var hud in scene.GetAllComponents<PlayerScreenHud>() )
		{
			if ( hud is null || !hud.IsValid() )
				continue;

			var vitals = hud.Components.Get<PlayerVitals>( FindMode.EverythingInSelfAndAncestors );
			if ( vitals is not null && vitals.IsLocalInputOwnedPawn() )
				return true;
		}

		return false;
	}
}
