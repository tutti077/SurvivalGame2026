using System;
using System.Diagnostics;
using Game;

namespace Survival;

/// <summary>
/// Streams procedural terrain from world-meter sampling (<see cref="ITerrainPreviewBackend"/>).
/// <see cref="BiomePreviewMap"/> is inspector/display export only — nothing reads it for generation.
/// </summary>
[Title( "Terrain World Manager" )]
public sealed partial class TerrainWorldManager : Component
{
	const float ChunkLoadProgressWeight = 0.3f;
	const float MapLoadProgressWeight = 0.7f;

	sealed class SaplingState
	{
		public GameObject Go;
		public Vector3 PosMeters;
		public ModelRenderer[] Renderers;
		/// <summary>0 hidden … 1 shown; moves toward the range test over Small Object Fade (s).</summary>
		public float Fade;
	}

	/// <summary>A rock / stick (no LODs) fading in after its spawn: TreeFade 0 → 1 over Small Object Fade (s).</summary>
	sealed class SmallFade
	{
		public ModelRenderer[] Renderers;
		public float T;
	}
	readonly List<SmallFade> _smallFades = new();

	sealed class LoadedChunk
	{
		public GameObject GameObject;
		public ModelCollider Collider;
		public TerrainChunkCoord Coord;
		public int VerticesPerSide;
		public bool HasVegetation;
		public bool HasSmallVegetation;
		public bool HasEntityPopulation;
		/// <summary>8×8 Clover-Hills mask for the world grass (TerrainWorldManager.Grass.cs); null = no grass on this chunk.</summary>
		public bool[] GrassMask;
		/// <summary>The meshed height grid (engine units, chunk-local) the grass is planted on — refreshed on every remesh.</summary>
		public float[] GrassHeightsZ;
		public int GrassHeightsSide;
		public float GrassStepUnits;
		/// <summary>Large-tree positions (world meters) — sapling trunk clearance on promotion.</summary>
		public readonly List<Vector2> Trunks = new();
		/// <summary>Scatter planned for this chunk and not yet cloned (TerrainWorldManager.VegetationQueue.cs).</summary>
		public readonly List<TerrainVegetationScatter.SpawnPlan> PendingSpawns = new();
		public readonly List<ModelCollider> VegetationColliders = new();
		/// <summary>Scatter renderers that cast shadows only while the chunk is near (saplings never do).</summary>
		public readonly List<ModelRenderer> VegetationShadowRenderers = new();
		/// <summary>Saplings on this chunk (spawned hidden); TickSaplingVisibility fades each in within Sapling Visible Range (m) and out past it.</summary>
		public readonly List<SaplingState> Saplings = new();
		/// <summary>True while every sapling on the chunk is hidden because the chunk is out of sapling range (skip the per-sapling loop).</summary>
		public bool SaplingsAllHidden = true;
		/// <summary>Trees on this chunk currently cast shadows (inside Tree Shadow Range).</summary>
		public bool ShadowsOn;
		/// <summary>
		/// Frame at which the far ring was told to drop this chunk (its first real tree is about to spawn); -1 = the far
		/// trees still stand here. The spawner waits two frames after it so the tile has rebuilt (no gap, no double tree).
		/// </summary>
		public int FarHandoverFrame = -1;
		/// <summary>Leaving: the far ring has been told to take the chunk back; the real trees are destroyed once its tiles rebuilt.</summary>
		public bool Unloading;
		public int UnloadFrame;
		/// <summary>Large trees whose LOD level this manager cross-fades (TerrainWorldManager.TreeLod.cs).</summary>
		public readonly List<TreeLodState> LodTrees = new();
		public float NextLodCheck;
		/// <summary>Large trees are planted as objects (inside Tree Objects Range); props (rocks / sticks) are planted (inside Prop Objects Range).</summary>
		public bool HasLargeTrees, HasProps;
		/// <summary>The chunk left the tree object range: the far ring draws its trees again and the objects go a few frames later.</summary>
		public bool TreesLeaving;
		public int TreesLeaveFrame;
	}

	[Property, Group( "World" )] public string WorldName { get; set; } = "TestWorld";
	[Property, Group( "World" )] public float WorldDiameterMeters { get; set; } = 40000f;
	[Property, Group( "World" ), Title( "Ocean Ring Width (m)" ), Description( "Flat water band outside land disk. Total world = land + 2× ring (default 25 km)." )]
	public float OceanRingWidthMeters { get; set; } = 2500f;

	[Property, Group( "World" )] public int WorldSeed { get; set; } = 1337;
	[Property, Group( "World" )] public float MaxTerrainHeightMeters { get; set; } = 700f;
	[Property, Group( "World" ), Title( "Settings Source" ), Description( "World Recipe First (default since 2026-10-08) = WorldSaves/<name>/world.json is THE world; the Terrain Noise Preview tool loads from it and writes to it with Apply To World Recipe. Tuned Preview First = the latest editor Generate bundle silently wins over the recipe." )]
	public TerrainWorldSettingsSource SettingsSource { get; set; } = TerrainWorldSettingsSource.WorldRecipeFirst;
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
	[Property, Group( "Vegetation" ), Title( "Tree Objects Range (m)" ), Range( 100f, 800f ), Step( 10f ), Description( "Large trees exist as GameObjects (collision, chopping, LOD cross-fade) only on chunks within this distance; beyond it the instanced far ring draws them (the elm's LOD3 — keep this at or past Tree LOD 3 At so the hand-over swaps identical meshes). 2026-10-20: 1 500 tree objects cost 4.4 ms of CPU a frame; the ring draws the same trees for nothing." )]
	public float VegetationObjectRangeMeters { get; set; } = 330f;

	[Property, Group( "Vegetation" ), Title( "Small Object Fade (s)" ), Range( 0f, 2f ), Step( 0.05f ), Description( "Rocks, sticks and saplings (no LODs) dither-fade in over this when they appear, saplings also out when they leave Sapling Visible Range. Large trees never fade: a LOD change is a one-frame model swap." )]
	public float VegetationSmallFadeSeconds { get; set; } = 0.5f;

	[Property, Group( "Vegetation" ), Title( "Sapling Visible Range (m)" ), Range( 10f, 300f ), Step( 5f ), Description( "A sapling is drawn only while the camera is within this distance of it — the same distance in and out, no hysteresis (Mark 2026-10-20). Checked every frame per sapling on the nearby chunks. Saplings only exist on full-detail chunks (High-Priority Radius 2 × 64 m chunks reaches every direction to ~83 m), so past ~80 m they would show patchily (2026-10-21: 50 → 80, Mark: they faded in too close)." )]
	public float VegetationSaplingVisibleMeters { get; set; } = 80f;

	[Property, Group( "Vegetation" ), Title( "Prop Objects Range (m)" ), Range( 50f, 600f ), Step( 10f ), Description( "Rocks and sticks are planted only once their chunk is within this distance (they are pickups: a chunk keeps them until it unloads)." )]
	public float VegetationPropRangeMeters { get; set; } = 192f;

	[Property, Group( "Vegetation" ), Title( "Tree Shadow Range (m)" ), Range( 25f, 600f ), Step( 5f ), Description( "Trees cast shadows only on chunks within this distance (Mark 2026-10-14: LOD bands 100 / 500 / 2 km / 8 km). Colliders follow Collision Range separately." )]
	public float VegetationShadowRangeMeters { get; set; } = 100f;
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
	[Property, Group( "Chunks" ), Title( "LOD Demote Margin (chunks)" ), Range( 0.5f, 6f ), Step( 0.25f ), Description( "Hysteresis: chunks promote to full detail inside High-Priority Radius and demote back to far LOD only beyond radius + this margin — no flip-flopping at the boundary." )]
	public float StreamLodDemoteMarginChunks { get; set; } = 2f;
	[Property, Group( "Chunks" ), Title( "Far Stream Vertices Per Side" ), Range( 9, 65 ), Step( 4 ), Description( "Height samples for chunks outside the full-detail radius (17 ≈ 4 m on 64 m chunks)." )]
	public int StreamFarVerticesPerSide { get; set; } = 17;
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

	[Property, Group( "Vegetation" ), Title( "Clover — Tree Prefabs" ), Description( "Large trees; each spawn picks one of these at random." )]
	public List<string> VegetationCloverTreePrefabs { get; set; } =
	[
		"prefabs/environment/cloverHills/trees/elm/environment_elm1_v76.prefab",
		"prefabs/environment/cloverHills/trees/elm/environment_elm2_v76.prefab",
		"prefabs/environment/cloverHills/trees/elm/environment_elm3_v76.prefab",
		"prefabs/environment/cloverHills/trees/elm/environment_elm4_v76.prefab",
		"prefabs/environment/cloverHills/trees/elm/environment_elm5_v76.prefab",
		"prefabs/environment/cloverHills/trees/elm/environment_elm6_v76.prefab",
	];

	[Property, Group( "Vegetation" ), Title( "Clover — Sapling Prefabs" ), Description( "Small trees, Clover Hills only; each spawn picks one of these at random. Empty = no saplings." )]
	public List<string> VegetationCloverSaplingPrefabs { get; set; } =
	[
		"prefabs/environment/cloverHills/trees/elm/environment_elmsapling1_v15.prefab",
		"prefabs/environment/cloverHills/trees/elm/environment_elmsapling2_v15.prefab",
		"prefabs/environment/cloverHills/trees/elm/environment_elmsapling3_v15.prefab",
		"prefabs/environment/cloverHills/trees/elm/environment_elmsapling4_v15.prefab",
		"prefabs/environment/cloverHills/trees/elm/environment_elmsapling5_v15.prefab",
	];

	[Property, Group( "Vegetation" ), Title( "Clover — Sapling Cell Spacing (m)" ), Range( 2f, 32f ), Step( 1f ), Description( "Grid for the sapling layer (separate from the large-tree grid). Lower = more candidates." )]
	public float VegetationCloverSaplingSpacingMeters { get; set; } = 8f;

	[Property, Group( "Vegetation" ), Title( "Clover — Sapling Chance (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Per-cell chance inside the Near Trunk ring around a big elm." )]
	public float VegetationCloverSaplingChance01 { get; set; } = 0.5f;

	[Property, Group( "Vegetation" ), Title( "Clover — Sapling Near Trunk Radius (m)" ), Range( 2f, 40f ), Step( 1f ), Description( "Saplings roll Sapling Chance only inside this ring around a big elm trunk (outside Trunk Clearance)." )]
	public float VegetationCloverSaplingNearTrunkMeters { get; set; } = 10f;

	[Property, Group( "Vegetation" ), Title( "Clover — Sapling Clearing Chance (0–1)" ), Range( 0f, 1f ), Step( 0.01f ), Description( "Per-cell chance away from any big elm — a few lone saplings in the open, not a carpet." )]
	public float VegetationCloverSaplingClearingChance01 { get; set; } = 0.03f;

	[Property, Group( "Vegetation" ), Title( "Clover — Sapling Trunk Clearance (m)" ), Range( 0f, 8f ), Step( 0.25f ), Description( "No sapling closer than this to a large tree trunk." )]
	public float VegetationCloverSaplingClearanceMeters { get; set; } = 2.5f;

	[Property, Group( "Vegetation" ), Title( "Clover — Max Saplings Per Chunk" ), Range( 1, 256 ), Step( 1 )]
	public int VegetationCloverSaplingMaxPerChunk { get; set; } = 64;

	[Property, Group( "Vegetation" ), Title( "Clover — Sapling Scale Min (multiplier)" ), Range( 0.3f, 6f ), Step( 0.05f )]
	public float VegetationCloverSaplingScaleMin { get; set; } = 2.4f;

	[Property, Group( "Vegetation" ), Title( "Clover — Sapling Scale Max (multiplier)" ), Range( 0.3f, 6f ), Step( 0.05f )]
	public float VegetationCloverSaplingScaleMax { get; set; } = 3.6f;

	[Property, Group( "Vegetation" ), Title( "Slope Tilt Influence (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "How much trees lean with the ground slope. 0 = always straight up, 1 = perpendicular to the ground (still capped by Max Slope Tilt)." )]
	public float VegetationSlopeTiltInfluence01 { get; set; } = 0.5f;

	[Property, Group( "Vegetation" ), Title( "Max Slope Tilt (deg)" ), Range( 0f, 45f ), Step( 1f ), Description( "Trees never lean further than this from vertical, however steep the slope." )]
	public float VegetationSlopeTiltMaxDegrees { get; set; } = 15f;

	[Property, Group( "Vegetation" ), Title( "Clover — Rock Prefab" )]
	public string VegetationCloverRockPrefab { get; set; } = "prefabs/environment/cloverHills/rocks/rock.prefab";

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
	public string VegetationCloverStickPrefab { get; set; } = "prefabs/environment/cloverHills/resources/samplestick.prefab";

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

	[Property, Group( "Vegetation" ), Title( "Clover — Stick Near Large Tree" ), Description( "Extra sticks beside large Clover trees (not saplings)." )]
	public bool VegetationCloverStickNearLargeTreeEnabled { get; set; } = true;

	[Property, Group( "Vegetation" ), Title( "Clover — Stick Near Large Tree Chance (0–1)" ), Range( 0f, 1f ), Step( 0.01f ), Description( "Chance per large tree to place one stick in open ground beside the trunk." )]
	public float VegetationCloverStickNearLargeTreeChance01 { get; set; } = 0.05f;

	[Property, Group( "Vegetation" ), Title( "Clover — Stick Near Large Tree Min Radius (m)" ), Range( 1f, 12f ), Step( 0.25f )]
	public float VegetationCloverStickNearLargeTreeMinRadiusMeters { get; set; } = 2.5f;

	[Property, Group( "Vegetation" ), Title( "Clover — Stick Near Large Tree Max Radius (m)" ), Range( 1.5f, 16f ), Step( 0.25f )]
	public float VegetationCloverStickNearLargeTreeMaxRadiusMeters { get; set; } = 6f;

	[Property, Group( "Vegetation" ), Title( "Redwood — Tree Prefab A" )]
	public string VegetationRedwoodPrefab { get; set; } = "prefabs/environment/cloverHills/trees/elm/environment_elm3_v76.prefab";

	[Property, Group( "Vegetation" ), Title( "Redwood — Tree Prefab B" )]
	public string VegetationRedwoodPrefabB { get; set; } = "prefabs/environment/cloverHills/trees/elm/environment_elm6_v76.prefab";

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

	[Property, Group( "Vegetation" ), Title( "Scale Min (multiplier)" ), Range( 0.5f, 1.5f ), Step( 0.05f ), Description( "Multiplies each large tree prefab's authored scale." )]
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
	[Property, Group( "Preview Map" ), ReadOnly] public int BiomePreviewMapSeed { get; private set; } = int.MinValue;
	[Property, Group( "Preview Map" ), ReadOnly] public bool IsBiomePreviewMapStale { get; private set; }
	[Property, Group( "Preview Map" ), ReadOnly, Title( "Saved Map Stale (settings changed)" ), Description( "The saved biome_map.png was baked from different generation settings than this world uses now. Play rebuilds it in the background; Regenerate bakes it at full resolution." )]
	public bool IsSavedBiomeMapStale { get; private set; }

	/// <summary>Fingerprint of the settings this world generates with — the saved biome map carries the one it was baked from.</summary>
	public string GenerationSettingsFingerprint
	{
		get
		{
			_ = BuildGenerationSettings();
			return _generationSettingsFingerprint;
		}
	}

	string _generationSettingsFingerprint = "none";
	string _savedMapStampFingerprint;
	string _savedMapStampWorld;

	/// <summary>Re-reads biome_map.json and compares it with the live fingerprint (cheap: one small file).</summary>
	string _recipeFileFingerprint;

	public void RefreshSavedBiomeMapStaleState()
	{
		// The recipe on disk is the world. If it changed under us (tool Apply, an external edit), the cached
		// settings and their fingerprint are from the old file — drop them before comparing (Mark 2026-10-08:
		// "the map didn't update again").
		if ( SettingsSource == TerrainWorldSettingsSource.WorldRecipeFirst )
		{
			var recipeFingerprint = WorldSaveIO.ReadRecipeFileFingerprint( WorldName );
			if ( _recipeFileFingerprint is not null && recipeFingerprint != _recipeFileFingerprint )
			{
				Log.Info( $"[TerrainWorldManager] World recipe '{WorldName}' changed on disk — reloading generation settings." );
				InvalidateGenerationSettingsCache();
				TerrainPreviewLandDiskFields.InvalidateCache();
				_autoMapGenStarted = false;
			}
			_recipeFileFingerprint = recipeFingerprint;
		}

		var stamp = WorldSaveIO.TryReadBiomeMapStamp( WorldName );
		_savedMapStampWorld = WorldName;
		_savedMapStampFingerprint = stamp?.SettingsFingerprint;
		var hasPng = FileSystem.Data.FileExists( WorldSaveIO.GetBiomeMapRelativePath( WorldName ) );
		IsSavedBiomeMapStale = hasPng && !string.Equals( _savedMapStampFingerprint, GenerationSettingsFingerprint, StringComparison.Ordinal );
	}

	/// <summary>The world recipe on disk changed (Terrain Noise Preview: Apply To World Recipe): drop the cached settings and re-check the saved map.</summary>
	public void ReloadWorldRecipe()
	{
		InvalidateGenerationSettingsCache();
		TerrainPreviewLandDiskFields.InvalidateCache();
		_autoMapGenStarted = false;
		RefreshSavedBiomeMapStaleState();
		UpdateBiomePreviewStaleState();
	}
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
	readonly Dictionary<TerrainChunkCoord, float> _chunkGroundZMeters = new();

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
	RealTimeSince _sinceMinimapHostScan;
	TerrainPreviewSettings _loadSettings;
	Vector3 _loadStreamPos;
	Rotation _loadViewRotation;
	int _initialChunksTotal;
	int _initialChunksLoaded;
	int _lastChunkSeed = int.MinValue;
	TerrainChunkCoord _lastStreamRefreshChunk;
	int _lastStreamRefreshHeadingBucket = int.MinValue;
	float _lastStreamRefreshElevationMeters = float.MinValue;
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

	bool _worldLoadStarted;
	bool _waitingForHostWorld;
	bool _autoMapGenStarted;

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

		// Offline / host: start immediately and publish seed for joiners.
		if ( !Networking.IsActive || Networking.IsHost )
		{
			WorldNetworkSession.Instance?.HostPublish( WorldName, WorldSeed );
			BeginWorldLoad();
			_worldLoadStarted = true;
			TryWriteWorldRecipe();
			return;
		}

		// Joining client: wait for host Sync before meshing/scatter.
		_waitingForHostWorld = true;
		TryApplyHostWorldSession();
	}

	/// <summary>Frame counter for latency measurements (no engine frame index is exposed).</summary>
	int _frameIndex;

	protected override void OnUpdate()
	{
		var whole = Stopwatch.StartNew();
		try
		{
			OnUpdateInner();
			TickRiverSurface();   // merged river surface: rebuilds only after a chunk with river loaded / unloaded
		}
		finally
		{
			var ms = (float)whole.Elapsed.TotalMilliseconds;
			if ( ms > Stats.UpdateMaxMs ) Stats.UpdateMaxMs = ms;
			Stats.UpdateSumMs += ms;
			Stats.UpdateFrames++;
		}
	}

	void OnUpdateInner()
	{
		_frameIndex++;
		if ( _waitingForHostWorld )
		{
			TryApplyHostWorldSession();
			return;
		}

		if ( WorldSeed != _lastChunkSeed )
		{
			_lastChunkSeed = WorldSeed;
			InvalidateGenerationSettingsCache();
			UnloadAll();
			BeginWorldLoad();
			return;
		}

		var phase = Stopwatch.StartNew();
		EnsureWorldGrass();
		MarkPhase( ref Stats.GrassMs, phase );
		EnsureFarTreeCards();
		MarkPhase( ref Stats.FarTreesMs, phase );
		TickSettle();

		if ( _isWorldLoading )
		{
			ProcessWorldLoad();
			UpdateStreamInspectorState();
			EnsureMinimapScreen();
			return;
		}

		phase.Restart();
		TryRefreshStreamChunks();
		MarkPhase( ref Stats.RefreshMs, phase );
		TickChunkUnloads();
		TickDeferredUnloads();
		MarkPhase( ref Stats.UnloadMs, phase );
		TickStaleReloads();
		MarkPhase( ref Stats.StaleMs, phase );
		if ( _colliderPassPending && _loadSettings is not null && TryGetStreamTransform( out var colliderStreamPos, out _ ) )
			UpdateChunkColliders( TerrainWorldUnits.EngineToMeters( colliderStreamPos ), BuildGenerationSettings(), Math.Max( 32f, ChunkSizeMeters ) );
		TickVegetationColliderEnables();
		MarkPhase( ref Stats.RefreshCollidersMs, phase );
		ProcessStreamChunkQueue();
		MarkPhase( ref Stats.QueueMs, phase );
		TickVegetationSpawns();
		MarkPhase( ref Stats.SpawnMs, phase );
		TickTreeLods();
		TickTreesLeaving();
		TickSaplingVisibility();
		TickSmallFades();
		MarkPhase( ref Stats.LodMs, phase );
		TickFarTerrain();
		MarkPhase( ref Stats.FarTierMs, phase );
		TickBackgroundMapGeneration();
		if ( IsWorldAuthority() && EntityPopulationEnabled )
			BiomePopulationRespawnQueue.Tick();
		UpdateBiomePreviewStaleState();
		UpdateStreamInspectorState();
		EnsureMinimapScreen();
	}

	void TryApplyHostWorldSession()
	{
		var session = WorldNetworkSession.Instance;
		if ( session is null || !session.IsValid() || !session.HostWorldReady )
			return;

		WorldSeed = session.WorldSeed;
		if ( !string.IsNullOrWhiteSpace( session.WorldName ) )
			WorldName = session.WorldName;

		BeginClientWorldFromHost();
	}

	void BeginClientWorldFromHost()
	{
		if ( _worldLoadStarted )
			return;

		_waitingForHostWorld = false;
		_worldLoadStarted = true;
		_lastChunkSeed = WorldSeed;
		InvalidateGenerationSettingsCache();
		_ = BuildGenerationSettings();
		BeginWorldLoad();
		Log.Info( $"[TerrainWorldManager] Client world load from host seed {WorldSeed}." );
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

		var hasWorldSession = WorldNetworkSession.Instance is not null && WorldNetworkSession.Instance.IsValid();
		if ( !hasWorldSession )
		{
			foreach ( var session in scene.GetAllComponents<WorldNetworkSession>() )
			{
				if ( session is not null && session.IsValid() && session.Enabled )
				{
					hasWorldSession = true;
					break;
				}
			}
		}

		if ( hasCombat && hasVitals && hasWorldSession )
			return;

		GameObject go = null;
		foreach ( var auth in scene.GetAllComponents<CombatAuthority>() )
		{
			if ( auth is not null && auth.IsValid() )
			{
				go = auth.GameObject;
				break;
			}
		}

		if ( go is null || !go.IsValid() )
		{
			go = new GameObject( true, "RuntimeAuthorities" );
			go.Flags |= GameObjectFlags.NotSaved;
			go.Parent = GameObject;
		}

		if ( !hasCombat )
			go.Components.Create<CombatAuthority>();

		if ( !hasVitals )
			go.Components.Create<VitalsAuthority>();

		if ( !hasWorldSession )
			go.Components.Create<WorldNetworkSession>();
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

		// Vertical LOD: climbing/diving changes 3D distances without crossing a chunk in XY.
		var elevationChanged =
			MathF.Abs( streamPosMeters.z - _lastStreamRefreshElevationMeters ) > chunkSize * 0.75f;

		// Uniform radius: only refresh when the player moves to a new chunk (look direction irrelevant).
		if ( !UseForwardConeStreaming )
		{
			if ( chunk == _lastStreamRefreshChunk && !elevationChanged )
				return;

			_lastStreamRefreshChunk = chunk;
			_lastStreamRefreshHeadingBucket = int.MinValue;
			_lastStreamRefreshElevationMeters = streamPosMeters.z;
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
		if ( chunk == _lastStreamRefreshChunk
			&& headingBucket == _lastStreamRefreshHeadingBucket
			&& !elevationChanged )
			return;

		_lastStreamRefreshChunk = chunk;
		_lastStreamRefreshHeadingBucket = headingBucket;
		_lastStreamRefreshElevationMeters = streamPosMeters.z;
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
			// Diameter / height / ring come from the recipe, but the SEED is the component's (the menu writes
			// the chosen world's seed onto it; in the editor you type one). It used to be silently replaced by
			// the recipe's seed, so changing it on the component changed nothing (Mark 2026-10-08).
			if ( _generationSettings.WorldSeed != WorldSeed )
			{
				Log.Info( $"[Terrain] Seed {WorldSeed} from the component overrides recipe seed {_generationSettings.WorldSeed} — recipe updated." );
				_generationSettings.WorldSeed = WorldSeed;
				var recipe = WorldSaveIO.TryReadRecipe( WorldName );
				if ( recipe is not null )
				{
					recipe.WorldSeed = WorldSeed;
					recipe.PreviewSettings.WorldSeed = WorldSeed;
					WorldSaveIO.WriteRecipe( recipe );
					_recipeFileFingerprint = WorldSaveIO.ReadRecipeFileFingerprint( WorldName );
				}
			}
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
		_generationSettingsFingerprint = TerrainPreviewSettingsFingerprint.Compute( _generationSettings );
		return _generationSettings;
	}

	bool _vegetationHidden;
	bool _vegetationObjectsHidden;

	/// <summary>
	/// Perf switch (<c>perf_veg_objects 0|1</c>): hides only the scatter GameObjects (trees, rocks, sticks) on loaded
	/// chunks — the far ring and the impostors keep drawing, so this isolates what the objects themselves cost.
	/// </summary>
	public bool VegetationObjectsHidden
	{
		get => _vegetationObjectsHidden;
		set
		{
			if ( _vegetationObjectsHidden == value )
				return;
			_vegetationObjectsHidden = value;
			foreach ( var entry in _loaded.Values )
			{
				if ( entry.GameObject is null || !entry.GameObject.IsValid() )
					continue;
				foreach ( var child in entry.GameObject.Children )
				{
					if ( child.Name.StartsWith( "veg_", StringComparison.Ordinal ) )
						child.Enabled = !value;
				}
			}
		}
	}

	/// <summary>Perf switch (<c>perf_terrain_shadows 0|1</c>): the loaded chunks' own shadow casting. Returns how many chunks it touched.</summary>
	public int SetTerrainChunkShadows( bool on )
	{
		var count = 0;
		var type = on ? ModelRenderer.ShadowRenderType.On : ModelRenderer.ShadowRenderType.Off;
		foreach ( var entry in _loaded.Values )
		{
			var renderer = entry.GameObject?.Components.Get<ModelRenderer>( FindMode.EverythingInSelf );
			if ( renderer is null || !renderer.IsValid() )
				continue;
			renderer.RenderType = type;
			count++;
		}
		return count;
	}

	/// <summary>
	/// Cumulative streaming counters for <c>perf_watch</c> (chunk loads / unloads / LOD remeshes /
	/// near-state flips, which toggle scatter colliders + shadows). Plain increments, no per-frame cost.
	/// </summary>
	public struct StreamStats
	{
		public int Loads;
		public int Unloads;
		public int Remeshes;
		public int NearFlips;
		public int ScatterSpawns;
		// Worst single-frame cost (ms) of each OnUpdate phase since the last perf_watch line (perf_cpu only shows "Update").
		public float RefreshMs, UnloadMs, StaleMs, QueueMs, SpawnMs, FarTierMs, FarTreesMs, GrassMs;
		// Inside a stream refresh (once per chunk crossed): set collection, stale / LOD pass, synchronous near loads, queue rebuild, collider pass.
		public float RefreshCollectMs, RefreshStaleMs, RefreshSyncMs, RefreshQueueMs, RefreshCollidersMs;
		// Worst single prefab clone (ms) and which plan it was.
		public float SpawnOneMs;
		public string SpawnOneName;
		// Spawner queue state at the last tick: chunks waiting, plans waiting, chunks held by the far-ring hand-over gate.
		public int SpawnPendingChunks, SpawnPendingPlans, SpawnGateBlocked, FrameIndex, LoadedChunks, UnloadingChunks;
		// Worst single chunk finish (ms): model + collider + river, scatter planning, grass + nav notify.
		public float FinishModelMs, FinishScatterMs, FinishGrassNavMs;
		// Far-tree ring: most instanced draw calls / instances drawn in one frame since the last line.
		public int FarDrawCalls, FarDrawInstances, FarHookCalls, FarCachedChunks;
		public float FarBuildMs, FarGatherMs;
		// Impostor cards baked into the loaded far chunks.
		public int FarImpostorInstances;
		// Tree LOD cross-fades started since the last line, and the worst frame of the LOD pass.
		public int LodFades;
		public float LodMs;
		// The manager's whole OnUpdate: worst frame and average since the last line.
		public float UpdateMaxMs, UpdateSumMs;
		public int UpdateFrames;

		public void ResetPhaseMaxima()
		{
			RefreshMs = UnloadMs = StaleMs = QueueMs = SpawnMs = FarTierMs = FarTreesMs = GrassMs = 0f;
			RefreshCollectMs = RefreshStaleMs = RefreshSyncMs = RefreshQueueMs = RefreshCollidersMs = 0f;
			SpawnOneMs = 0f;
			SpawnOneName = null;
			FinishModelMs = FinishScatterMs = FinishGrassNavMs = 0f;
			FarDrawCalls = FarDrawInstances = FarHookCalls = 0;
			FarBuildMs = FarGatherMs = 0f;
			LodFades = 0;
			LodMs = 0f;
			UpdateMaxMs = UpdateSumMs = 0f;
			UpdateFrames = 0;
		}

		public string PhaseLine()
			=> $"manager update avg {(UpdateFrames > 0 ? UpdateSumMs / UpdateFrames : 0f):0.0} max {UpdateMaxMs:0.0} ms | phase max ms: refresh {RefreshMs:0.0} (collect {RefreshCollectMs:0.0} stale {RefreshStaleMs:0.0} sync {RefreshSyncMs:0.0} queue {RefreshQueueMs:0.0} colliders {RefreshCollidersMs:0.0}) unload {UnloadMs:0.0} stale {StaleMs:0.0} queue {QueueMs:0.0} (chunk: model {FinishModelMs:0.0} scatter {FinishScatterMs:0.0} grassNav {FinishGrassNavMs:0.0}) spawn {SpawnMs:0.0} (one {SpawnOneMs:0.0} {SpawnOneName}; queue {SpawnPendingChunks} chunks / {SpawnPendingPlans} plans, loaded {LoadedChunks}, unloading {UnloadingChunks}, frame {FrameIndex}) farTier {FarTierMs:0.0} farTrees {FarTreesMs:0.0} grass {GrassMs:0.0} lod {LodMs:0.0} ({LodFades} fades) | far ring: {FarDrawCalls} draws / {FarDrawInstances} inst, hook calls {FarHookCalls}, cached {FarCachedChunks}, build {FarBuildMs:0.0} ms (gather {FarGatherMs:0.0}); impostor cards {FarImpostorInstances} baked";
	}

	static void MarkPhase( ref float slot, Stopwatch sw )
	{
		var ms = (float)sw.Elapsed.TotalMilliseconds;
		if ( ms > slot )
			slot = ms;
		sw.Restart();
	}

	public StreamStats Stats;

	/// <summary>
	/// Perf A/B switch (<c>perf_trees 0|1</c>): hides every scatter instance (trees, saplings, rocks,
	/// sticks) on loaded chunks and stops new chunks scattering; showing them again re-enables the
	/// hidden ones and lets unscattered chunks catch up through the normal refresh. Runtime only.
	/// </summary>
	public bool VegetationHidden
	{
		get => _vegetationHidden;
		set
		{
			if ( _vegetationHidden == value )
				return;

			_vegetationHidden = value;
			foreach ( var entry in _loaded.Values )
			{
				if ( entry.GameObject is null || !entry.GameObject.IsValid() )
					continue;

				foreach ( var child in entry.GameObject.Children )
				{
					if ( child.Name.StartsWith( "veg_", StringComparison.Ordinal ) )
						child.Enabled = !value;
				}
			}
		}
	}

	/// <summary>Chunks still waiting in the stream build queue (0 = streaming has caught up).</summary>
	public int PendingStreamChunkCount => _streamLoadQueue.Count;

	/// <summary>
	/// Scatter counts over every loaded chunk, by instance-name prefix (see
	/// <see cref="TerrainVegetationScatter"/>): large trees, saplings, rocks / sticks. Debug / benchmark
	/// only — walks the chunk children, so call it once per report, not per frame.
	/// </summary>
	public (int Chunks, int FullDetailChunks, int Trees, int Saplings, int Props) CountScatter()
	{
		int full = 0, trees = 0, saplings = 0, props = 0;
		foreach ( var entry in _loaded.Values )
		{
			if ( entry.VerticesPerSide >= ChunkVerticesPerSide )
				full++;
			if ( entry.GameObject is null || !entry.GameObject.IsValid() )
				continue;

			foreach ( var child in entry.GameObject.Children )
			{
				var name = child.Name;
				if ( name.StartsWith( "veg_clover_small", StringComparison.Ordinal ) )
					saplings++;
				else if ( name.StartsWith( "veg_clover", StringComparison.Ordinal ) || name.StartsWith( "veg_redwood", StringComparison.Ordinal ) )
					trees++;
				else if ( name.StartsWith( "veg_", StringComparison.Ordinal ) )
					props++;
			}
		}

		return (_loaded.Count, full, trees, saplings, props);
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

	public void StartBiomePreviewGeneration() => StartBiomePreviewGeneration( 0 );

	/// <summary>Editor bake resolution for the automatic stale rebuild: quick enough to iterate on (Mark 2026-10-08), Regenerate stays full size.</summary>
	public const int EditorAutoBakeMaxResolution = 1024;

	/// <summary>Editor: begin a budgeted bake the inspector ticks with <see cref="TickMapGenerationBudgeted"/>.</summary>
	public void StartBiomePreviewGenerationBudgeted( int maxResolution )
	{
		_backend ??= TerrainPreviewBackendRegistry.Active;
		CancelBiomePreviewMapGeneration();
		Log.Info( $"[TerrainWorldManager] Saved biome map is stale — editor rebake at up to {maxResolution}px (settings {GenerationSettingsFingerprint})." );
		TerrainPreviewLandDiskFields.EnsureReady( BuildGenerationSettings() );
		StartBiomePreviewGeneration( maxResolution );
	}

	/// <summary>Editor: advance a running bake for at most <paramref name="budgetMs"/>; finishes and saves the PNG + stamp when done.</summary>
	public void TickMapGenerationBudgeted( float budgetMs ) => TickMapGeneration( budgetMs );

	void StartBiomePreviewGeneration( int maxResolution )
	{
		if ( _previewJob is not null )
			return;

		_backend ??= TerrainPreviewBackendRegistry.Active;

		var settings = BuildGenerationSettings();
		var resolution = ComputeBiomePreviewResolution();
		if ( maxResolution > 0 )
			resolution = Math.Min( resolution, maxResolution );
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

	// Background (mid-gameplay) minimap raster: one pipeline sample costs ~30 µs, so one 1024-px row
	// is already ~a frame. Cap resolution and stop after a few ms; the frame budget stays intact.
	const int BackgroundMapMaxResolution = 512;
	const float BackgroundMapBudgetMs = 4f;

	/// <summary>
	/// After load, build the biome map in the background when this world has neither a saved PNG nor
	/// a generated texture — menu-created worlds otherwise never get a minimap with
	/// <see cref="RegeneratePreviewOnStart"/> off. Time-budgeted per frame, reduced resolution;
	/// press Regenerate in the inspector for a full-resolution bake.
	/// </summary>
	void TickBackgroundMapGeneration()
	{
		if ( _previewJob is not null )
		{
			TickMapGeneration( BackgroundMapBudgetMs );
			return;
		}

		if ( _autoMapGenStarted )
			return;

		_autoMapGenStarted = true;
		if ( BiomePreviewMap.IsValid() )
			return;

		// The minimap is ALWAYS rasterised from the live settings in this session. biome_map.png is written
		// for the record but never read back as a texture: the engine caches textures by path, so a reloaded
		// PNG showed whatever this editor process loaded first, hours ago (Mark 2026-10-08: "it should all be
		// based on the same calculation").
		RefreshSavedBiomeMapStaleState();
		Log.Info( $"[TerrainWorldManager] Rasterising minimap for '{WorldName}' in the background (settings {GenerationSettingsFingerprint})." );
		StartBiomePreviewGeneration( BackgroundMapMaxResolution );
	}

	void TickMapGeneration() => TickMapGeneration( float.MaxValue );

	void TickMapGeneration( float budgetMs )
	{
		if ( _previewJob is null )
			return;

		var maxRows = Math.Clamp( PreviewMapRowsPerFrame, 4, 512 );
		var stopwatch = Stopwatch.StartNew();
		var rows = 0;
		while ( rows < maxRows && !_previewJob.IsComplete )
		{
			_previewJob.Step( 1 );
			rows++;
			if ( stopwatch.Elapsed.TotalMilliseconds >= budgetMs )
				break;
		}

		MapGenerationProgress01 = _previewJob.Progress01;
		MapGenerationStatus =
			$"Biome map {_previewJob.RowsCompleted}/{_previewJob.Resolution} ({MapGenerationProgress01 * 100f:0}%)";

		if ( !_previewJob.IsComplete )
			return;

		FinishMapGeneration();
	}

	void BeginWorldLoad()
	{
		_autoMapGenStarted = false;
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
		else
			TickLoadScreenMapGeneration();

		UpdateLoadScreenProgress();

		if ( !IsWorldLoadComplete() )
			return;

		FinishWorldLoad();
	}

	TerrainPreviewSettings _loadGroundSettings;
	float _loadGroundZMeters;

	void ResolveLoadStreamTransform()
	{
		var settings = _loadSettings;
		// Called every load frame; the centre sample only changes with the settings.
		if ( !ReferenceEquals( _loadGroundSettings, settings ) )
		{
			var sample = _backend.Sample( settings, 0f, 0f );
			_loadGroundZMeters = sample.IsInsideWorld ? sample.HeightMeters : 0f;
			_loadGroundSettings = settings;
		}
		_loadStreamPos = new Vector3( 0f, 0f, _loadGroundZMeters );
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

		// Worker builds that finished since last frame land first (main-thread half only).
		var built = StreamAsyncGeometry
			? TickPendingChunkBuilds( settings, streamPosMeters, stopwatch, budgetMs, maxChunks )
			: 0;

		while ( _streamLoadQueue.Count > 0 && built < maxChunks )
		{
			if ( built > 0 && stopwatch.Elapsed.TotalMilliseconds >= budgetMs )
				break;

			var coord = _streamLoadQueue.Peek();
			if ( !_streamNeeded.Contains( coord ) || _loaded.ContainsKey( coord ) || IsChunkBuildPending( coord ) )
			{
				_streamLoadQueue.Dequeue();
				continue;
			}

			if ( StreamAsyncGeometry )
			{
				// Hand the sampling to a worker; the in-flight cap keeps the queue waiting, not the frame.
				if ( !TryBeginChunkBuild( coord, settings, streamPosMeters, visible: true ) )
					break;
				_streamLoadQueue.Dequeue();
				continue;
			}

			_streamLoadQueue.Dequeue();
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
			if ( !_loaded.ContainsKey( coord ) && !IsChunkBuildPending( coord ) )
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

			// 3D: hovering high above should not force synchronous full-detail builds below.
			if ( ChunkLodDistanceMeters( coord, streamPos, settings, chunkSize ) <= priorityRadius )
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

	/// <summary>
	/// 3D distance for mesh LOD only — altitude counts, so climbing demotes the ground below and
	/// diving promotes it. Colliders and entity population stay on horizontal range: physics under a
	/// falling player must never switch off because they are high up.
	/// </summary>
	float ChunkLodDistanceMeters(
		TerrainChunkCoord coord,
		Vector3 streamPosMeters,
		TerrainPreviewSettings settings,
		float chunkSize )
	{
		var worldRadius = settings.TotalWorldRadiusMeters;
		var horizontal = ChunkDistanceMeters( coord, streamPosMeters, worldRadius, chunkSize );
		var dz = streamPosMeters.z - GetChunkGroundZMeters( coord, settings, worldRadius, chunkSize );
		return MathF.Sqrt( (horizontal * horizontal) + (dz * dz) );
	}

	float GetChunkGroundZMeters(
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		float worldRadius,
		float chunkSize )
	{
		if ( _chunkGroundZMeters.TryGetValue( coord, out var cached ) )
			return cached;

		var groundZ = settings.SeaLevelMeters;
		if ( _backend is not null )
		{
			var center = TerrainChunkStreaming.GetChunkCenterWorld( coord, worldRadius, chunkSize );
			var sample = _backend.Sample( settings, center.x, center.y );
			if ( sample.IsInsideWorld )
				groundZ = sample.HeightMeters;
		}

		_chunkGroundZMeters[coord] = groundZ;
		return groundZ;
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

			// Same distance-based LOD as streaming — the world starts in its steady state instead of
			// all-full-detail followed by a wave of demotions on the first refresh.
			LoadChunk( coord, _loadSettings, _loadStreamPos, visible: true, useStreamLod: true );
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
		{
			// The minimap raster used to run AFTER the load screen dropped, at a per-frame budget, and ate the
			// first half minute of play (40 ms frames). The screen now stays up until it is done (Mark 2026-10-13).
			return _previewJob is null && (_autoMapGenStarted || BiomePreviewMap.IsValid());
		}

		if ( !_biomeMapLoadSettled )
			return false;

		return true;
	}

	/// <summary>While the load screen is up the whole frame is ours: raster the minimap at this budget, not the gameplay one.</summary>
	const float LoadScreenMapBudgetMs = 40f;

	/// <summary>
	/// Regenerate-on-start OFF: once the initial chunks are in, build the minimap (same background raster as
	/// <see cref="TickBackgroundMapGeneration"/>, 512 px) behind the load screen instead of behind the player.
	/// </summary>
	void TickLoadScreenMapGeneration()
	{
		if ( _initialChunksQueued && _initialChunkQueue.Count == 0 && _initialChunksLoaded >= _initialChunksTotal && !_autoMapGenStarted )
		{
			_autoMapGenStarted = true;
			if ( !BiomePreviewMap.IsValid() )
			{
				RefreshSavedBiomeMapStaleState();
				Log.Info( $"[TerrainWorldManager] Generating minimap for '{WorldName}' behind the load screen (settings {GenerationSettingsFingerprint})." );
				StartBiomePreviewGeneration( BackgroundMapMaxResolution );
			}
		}

		if ( _previewJob is not null )
			TickMapGeneration( LoadScreenMapBudgetMs );
	}

	void UpdateLoadScreenProgress()
	{
		if ( !ShowWorldLoadScreen )
			return;

		var chunkProgress = _initialChunksTotal > 0
			? Math.Clamp( (float)_initialChunksLoaded / _initialChunksTotal, 0f, 1f )
			: 1f;

		var mapRunning = _previewJob is not null;
		var mapNeeded = RegeneratePreviewOnStart || mapRunning || !BiomePreviewMap.IsValid();
		var mapProgress = mapNeeded ? MapGenerationProgress01 : 1f;
		var totalProgress = (chunkProgress * ChunkLoadProgressWeight) + (mapProgress * MapLoadProgressWeight);

		string status;
		if ( mapRunning )
			status = $"Generating map… {MapGenerationProgress01 * 100f:0}%";
		else if ( chunkProgress < 1f )
			status = $"Terrain {_initialChunksLoaded}/{_initialChunksTotal}";
		else
			status = mapNeeded ? "Generating map…" : $"Terrain {_initialChunksLoaded}/{_initialChunksTotal}";

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

		// Keep the screen up until the frame time settles: the first seconds after load are nav bakes and
		// first-draw stalls, and a lagging first look at the world looks bad (Mark 2026-10-13). TickSettle ends it.
		_settling = true;
		_settleStartedAt = Time.Now;
		_settleGoodFrames = 0;
		ShowLoadScreen( "Loading World", "Settling…", 1f );

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
				// Local write so HUD can LoadFromFileSystem (UI-friendly). Not only authority. The stamp says
				// which settings baked it, so a later settings change marks it stale.
				WorldSaveIO.WriteBiomeMapPng( WorldName, bitmap, new WorldSaveIO.BiomeMapStamp
				{
					SettingsFingerprint = GenerationSettingsFingerprint,
					WorldSeed = WorldSeed,
					Resolution = EffectiveBiomePreviewResolution,
				} );
				_savedMapStampWorld = WorldName;
				_savedMapStampFingerprint = GenerationSettingsFingerprint;
				IsSavedBiomeMapStale = false;
			}
			catch ( Exception e )
			{
				Log.Warning( $"[TerrainWorldManager] Failed to write biome map PNG: {e.Message}" );
			}
		}

		// The engine caches textures by path, so reloading biome_map.png right after rewriting it can hand back
		// the OLD image. The texture just rasterised is the truth — bind that.
		Log.Info( $"[TerrainWorldManager] Biome map rebuilt — {EffectiveBiomePreviewResolution}px, settings {GenerationSettingsFingerprint}, saved WorldSaves/{WorldName}/biome_map.png" );
	}

	void RefreshChunks( Vector3 streamPos, Rotation viewRotation )
	{
		if ( _isWorldLoading )
			return;

		var settings = BuildGenerationSettings();
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var sw = Stopwatch.StartNew();
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
		{
			if ( _unloadQueued.Add( coord ) )
				_unloadQueue.Enqueue( coord );
		}

		MarkPhase( ref Stats.RefreshCollectMs, sw );

		// Far-LOD chunks loaded without trees (or wrong mesh detail) must rebuild when you arrive.
		QueueStaleChunkReloads( streamPos, settings, chunkSize );
		MarkPhase( ref Stats.RefreshStaleMs, sw );

		LoadNearPriorityChunksSync( streamPos, settings, chunkSize );
		MarkPhase( ref Stats.RefreshSyncMs, sw );
		RebuildStreamLoadQueue( streamPos, settings, chunkSize );
		MarkPhase( ref Stats.RefreshQueueMs, sw );

		LoadedChunkCount = _streamNeeded.Count;
		MeshedChunkCount = _loaded.Count;
		UpdateChunkColliders( streamPos, settings, chunkSize );
		MarkPhase( ref Stats.RefreshCollidersMs, sw );
	}

	/// <summary>
	/// Unload loaded chunks whose mesh LOD or vegetation no longer matches the camera distance
	/// so <see cref="LoadChunk"/> runs again with the right detail + scatter.
	/// </summary>
	void QueueStaleChunkReloads( Vector3 streamPosMeters, TerrainPreviewSettings settings, float chunkSize )
	{
		var worldRadius = settings.TotalWorldRadiusMeters;
		var maxRemeshes = Math.Clamp( StreamRemeshesPerFrame, 1, 8 );
		var remeshes = 0;
		_staleReloadPending = false;

		foreach ( var entry in _loaded.Values )
		{
			if ( !_streamNeeded.Contains( entry.Coord ) || _unloadQueued.Contains( entry.Coord ) || entry.Unloading )
				continue;

			if ( entry.GameObject is null || !entry.GameObject.IsValid() )
				continue;

			// Horizontal for colliders/entities; 3D (altitude counts) for mesh LOD.
			var distance = ChunkDistanceMeters( entry.Coord, streamPosMeters, worldRadius, chunkSize );
			var lodDistance = ChunkLodDistanceMeters( entry.Coord, streamPosMeters, settings, chunkSize );
			var desiredVerts = ResolveChunkVerticesPerSide( lodDistance, chunkSize, useStreamLod: true );
			var wantVegetation = ShouldScatterVegetation( desiredVerts );

			// Remesh in place — never destroy/respawn trees when LOD changes (that was the "2nd layer").
			// Distance-based both ways, with hysteresis: promote inside the priority radius, demote
			// only beyond radius + demote margin, so boundary chunks never flip-flop. Watertight
			// border lattice keeps both transitions tear-free.
			var wantsRemesh = desiredVerts > entry.VerticesPerSide
				|| (entry.VerticesPerSide > desiredVerts && lodDistance >= ResolveLodDemoteDistanceMeters( chunkSize ));
			if ( wantsRemesh )
			{
				if ( StreamAsyncGeometry )
				{
					// Sampling on a worker, like a load: the synchronous remesh was the once-a-second 100-150 ms
					// frame while flying (perf_watch 2026-10-14: worst frame tracked the remesh count exactly).
					if ( !IsChunkBuildPending( entry.Coord ) && !TryBeginChunkRemesh( entry, settings, streamPosMeters, desiredVerts ) )
						_staleReloadPending = true;   // in-flight cap: next frame
					continue;
				}

				if ( remeshes >= maxRemeshes )
				{
					_staleReloadPending = true;   // next frame
					continue;
				}
				RemeshChunkLod( entry, settings, streamPosMeters, desiredVerts, distance );
				remeshes++;
			}

			if ( wantVegetation && !entry.HasVegetation )
				ScatterVegetationOnChunk( entry, settings, desiredVerts );
			else if ( !_vegetationHidden && entry.HasVegetation && !entry.HasSmallVegetation && entry.VerticesPerSide >= ChunkVerticesPerSide )
				ScatterSmallVegetationOnChunk( entry, settings );

			if ( !_vegetationHidden && entry.HasVegetation )
			{
				// Tree objects follow the camera: plant them when the chunk comes inside Tree Objects Range, hand
				// them back to the far ring (overlap, then destroy) once it is a chunk past it.
				if ( !entry.HasLargeTrees && !entry.TreesLeaving && distance <= VegetationObjectRangeMeters )
					ScatterLargeTreesOnChunk( entry, settings, desiredVerts );
				else if ( entry.HasLargeTrees && !entry.TreesLeaving && distance > VegetationObjectRangeMeters + chunkSize )
					BeginTreesLeaving( entry );
				else if ( entry.TreesLeaving && distance <= VegetationObjectRangeMeters )
					entry.TreesLeaving = false;   // came straight back: the objects never went

				if ( !entry.HasProps && distance <= VegetationPropRangeMeters )
					ScatterPropsOnChunk( entry, settings, desiredVerts );
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
		if ( !wasEnabled )
			SetVegetationNear( entry, true );
		if ( wasEnabled || !entry.GameObject.IsValid() )
			return;

		var bounds = entry.GameObject.GetBounds();
		if ( bounds.Size.LengthSquared < 1f )
			bounds = BBox.FromPositionAndSize( entry.GameObject.WorldPosition, TerrainWorldUnits.MetersToEngine( ChunkSizeMeters ) );
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
			ChunkHeightSmoothStrength01,
			ResolveBorderLatticeVerticesPerSide( verticesPerSide ) );

		ApplyChunkRemesh( entry, built, verticesPerSide, distance );
	}

	/// <summary>Swap a loaded chunk's model for a rebuilt LOD (sync or from a worker). Main thread.</summary>
	void ApplyChunkRemesh( LoadedChunk entry, TerrainMeshBuilder.BuildResult built, int verticesPerSide, float distance )
	{
		if ( built.Model is null || !built.Model.IsValid || entry.GameObject is null || !entry.GameObject.IsValid() )
			return;

		Stats.Remeshes++;
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
			var enabled = ShouldKeepChunkColliderEnabled( entry, distance, ChunkSizeMeters );
			if ( entry.Collider.Enabled != enabled )
				SetVegetationNear( entry, enabled );
			entry.Collider.Enabled = enabled;
		}

		entry.VerticesPerSide = verticesPerSide;
		// A LOD remesh must NOT regenerate the grass tiles over this chunk: chunks promote at 128 m and demote
		// at 256 m as you travel, both inside the visible grass ring, and every regeneration blanked the
		// clumps for a moment ("the grass is STILL popping in and out", Mark 2026-10-13). The new heights are
		// kept for whatever the engine plants here later; existing clumps stay put (they are off by at most the
		// 4 m → 2 m LOD delta of the ground itself). Only the first mesh of a chunk invalidates (LoadChunk).
		var firstHeights = entry.GrassHeightsZ is null;
		entry.GrassHeightsZ = built.HeightsZ;
		entry.GrassHeightsSide = built.VerticesPerSide;
		entry.GrassStepUnits = built.StepUnits;
		if ( firstHeights )
			NotifyGrassChunkChanged( entry );
	}

	float ResolveForwardViewDistanceMeters( float chunkSize )
	{
		var fromChunks = Math.Max( 1, ForwardViewRadiusChunks ) * chunkSize;
		return ViewDistanceMeters > chunkSize ? ViewDistanceMeters : fromChunks;
	}

	/// <summary>Collider pass left chunks to enable (one per frame): run it again next frame.</summary>
	bool _colliderPassPending;

	/// <summary>
	/// Enabling a chunk's collision is the expensive direction — the chunk's physics mesh, every tree collider
	/// on it, a nav bake request — so at most ONE chunk comes online per call; the rest wait a frame
	/// (perf_watch 2026-10-15: the collider pass inside a stream refresh was 8-11 ms once a second).
	/// </summary>
	void UpdateChunkColliders( Vector3 streamPos, TerrainPreviewSettings settings, float chunkSize )
	{
		var worldRadius = settings.TotalWorldRadiusMeters;
		var enabledThisPass = 0;
		_colliderPassPending = false;

		foreach ( var entry in _loaded.Values )
		{
			if ( entry.Collider is null || !entry.Collider.IsValid() )
				continue;

			var center = TerrainChunkStreaming.GetChunkCenterWorld( entry.Coord, worldRadius, chunkSize );
			var distance = new Vector3( center.x - streamPos.x, center.y - streamPos.y, 0f ).Length;
			var wantEnabled = ShouldKeepChunkColliderEnabled( entry, distance, chunkSize );
			var wasEnabled = entry.Collider.Enabled;
			if ( wantEnabled && !wasEnabled )
			{
				if ( enabledThisPass >= 1 )
				{
					_colliderPassPending = true;
					wantEnabled = false;   // next frame
				}
				else
					enabledThisPass++;
			}
			entry.Collider.Enabled = wantEnabled;
			if ( wasEnabled != wantEnabled )
				SetVegetationNear( entry, wantEnabled );

			var wantShadows = IsChunkInShadowRange( distance, chunkSize );
			if ( wantShadows != entry.ShadowsOn )
				SetVegetationShadows( entry, wantShadows );

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

	bool IsChunkInShadowRange( float distanceMeters, float chunkSize ) =>
		distanceMeters <= Math.Max( 25f, VegetationShadowRangeMeters ) + (chunkSize * 0.75f);

	bool ShouldKeepChunkColliderEnabled( LoadedChunk entry, float distanceMeters, float chunkSize ) =>
		entry.HasEntityPopulation || IsChunkInCollisionRange( distanceMeters, chunkSize );

	bool TryGetStreamTransform( out Vector3 worldPos, out Rotation viewRotation )
	{
		worldPos = default;
		viewRotation = Rotation.Identity;

		var cam = ResolveStreamCamera();
		if ( cam.IsValid() )
		{
			worldPos = cam.WorldPosition + TerrainDevCommands.CurrentStreamOffsetEngine();
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
		var lodDistance = ChunkLodDistanceMeters( coord, streamPos, settings, chunkSize );
		var verticesPerSide = ResolveChunkVerticesPerSide( lodDistance, chunkSize, useStreamLod );
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
			ChunkHeightSmoothStrength01,
			ResolveBorderLatticeVerticesPerSide( verticesPerSide ) );

		FinishChunkLoad( coord, settings, streamPos, visible, verticesPerSide, built );
	}

	/// <summary>Engine side of a chunk load: GameObject, renderer, collider, river water, scatter plan, nav, entities. Main thread.</summary>
	void FinishChunkLoad(
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		Vector3 streamPos,
		bool visible,
		int verticesPerSide,
		TerrainMeshBuilder.BuildResult built )
	{
		Stats.Loads++;
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var worldRadius = settings.TotalWorldRadiusMeters;
		var distance = ChunkDistanceMeters( coord, streamPos, worldRadius, chunkSize );

		if ( built.Model is null || !built.Model.IsValid )
		{
			Log.Warning( $"[TerrainWorldManager] Failed to build terrain mesh for chunk {coord}." );
			return;
		}

		var chunkMinX = -settings.TotalWorldRadiusMeters + (coord.X * ChunkSizeMeters);
		var chunkMinY = -settings.TotalWorldRadiusMeters + (coord.Y * ChunkSizeMeters);
		var chunkOriginEngine = TerrainWorldUnits.MetersToEngine( new Vector3( chunkMinX, chunkMinY, 0f ) );
		var finish = Stopwatch.StartNew();

		var go = new GameObject( true, $"TerrainChunk {coord}" );
		go.Flags |= GameObjectFlags.NotSaved;   // generated from the seed at play time — never part of the scene file (2026-10-20)
		go.Parent = GameObject;
		go.WorldPosition = chunkOriginEngine;
		go.Tags.Add( TerrainTag );

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = built.Model;
		renderer.MaterialOverride = built.Material;
		renderer.Enabled = visible;

		var collider = go.Components.Create<ModelCollider>();
		collider.Model = built.Model;
		collider.Static = true;
		collider.Enabled = IsChunkInCollisionRange( distance, chunkSize );

		TryBuildRiverWater( go, coord, settings, chunkMinX, chunkMinY, ChunkSizeMeters );

		var entry = new LoadedChunk
		{
			GameObject = go,
			Collider = collider,
			Coord = coord,
			VerticesPerSide = verticesPerSide,
			GrassHeightsZ = built.HeightsZ,
			GrassHeightsSide = built.VerticesPerSide,
			GrassStepUnits = built.StepUnits,
		};
		_loaded[coord] = entry;
		MarkPhase( ref Stats.FinishModelMs, finish );

		if ( ShouldScatterVegetation( verticesPerSide ) )
			ScatterVegetationOnChunk( entry, settings, verticesPerSide );
		MarkPhase( ref Stats.FinishScatterMs, finish );

		NotifyGrassChunkChanged( entry );

		// Nav + scavs only when collision is live — far render chunks must not schedule Recast.
		if ( collider.Enabled )
		{
			var chunkBounds = new BBox(
				chunkOriginEngine + built.LocalBounds.Mins,
				chunkOriginEngine + built.LocalBounds.Maxs );
			BuildNavMeshSync.NotifyTerrainChunkLoaded( GameObject.Scene, chunkBounds );
			TryPopulateEntitiesOnChunk( _loaded[coord], settings, distance, chunkSize );
		}
		MarkPhase( ref Stats.FinishGrassNavMs, finish );
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

	/// <summary>
	/// Large trees + props (once per chunk), plus saplings when the chunk is full detail; far chunks get
	/// their saplings on promotion (<see cref="ScatterSmallVegetationOnChunk"/>).
	/// </summary>
	void ScatterVegetationOnChunk( LoadedChunk entry, TerrainPreviewSettings settings, int verticesPerSide )
	{
		var options = BuildVegetationOptions();
		var distance = ChunkDistanceMeters( entry.Coord, _loadStreamPos, settings.TotalWorldRadiusMeters, Math.Max( 32f, ChunkSizeMeters ) );
		if ( TryGetStreamTransform( out var streamPosEngine, out _ ) )
			distance = ChunkDistanceMeters( entry.Coord, TerrainWorldUnits.EngineToMeters( streamPosEngine ), settings.TotalWorldRadiusMeters, Math.Max( 32f, ChunkSizeMeters ) );
		var largeTrees = distance <= VegetationObjectRangeMeters;
		var props = distance <= VegetationPropRangeMeters;
		TerrainVegetationScatter.PopulateChunk(
			entry.GameObject,
			entry.Coord,
			settings,
			_backend,
			ChunkSizeMeters,
			verticesPerSide,
			ChunkVerticesPerSide,
			options,
			entry.Trunks,
			entry.PendingSpawns,
			largeTrees,
			props );
		entry.HasVegetation = true;
		entry.HasLargeTrees = largeTrees;
		entry.HasProps = props;

		if ( verticesPerSide >= ChunkVerticesPerSide )
		{
			TerrainVegetationScatter.PopulateSmallTrees( entry.GameObject, entry.Coord, settings, _backend, ChunkSizeMeters, options, entry.Trunks, entry.PendingSpawns );
			entry.HasSmallVegetation = true;
		}

		QueueVegetationSpawns( entry );
	}

	void ScatterLargeTreesOnChunk( LoadedChunk entry, TerrainPreviewSettings settings, int verticesPerSide )
	{
		TerrainVegetationScatter.PopulateChunk( entry.GameObject, entry.Coord, settings, _backend, ChunkSizeMeters, verticesPerSide, ChunkVerticesPerSide,
			BuildVegetationOptions(), entry.Trunks, entry.PendingSpawns, largeTrees: true, props: false );
		entry.HasLargeTrees = true;
		QueueVegetationSpawns( entry );
	}

	void ScatterPropsOnChunk( LoadedChunk entry, TerrainPreviewSettings settings, int verticesPerSide )
	{
		TerrainVegetationScatter.PopulateChunk( entry.GameObject, entry.Coord, settings, _backend, ChunkSizeMeters, verticesPerSide, ChunkVerticesPerSide,
			BuildVegetationOptions(), entry.Trunks, entry.PendingSpawns, largeTrees: false, props: true );
		entry.HasProps = true;
		QueueVegetationSpawns( entry );
	}

	readonly List<LoadedChunk> _treesLeaving = new();

	void BeginTreesLeaving( LoadedChunk entry )
	{
		entry.TreesLeaving = true;
		entry.TreesLeaveFrame = _frameIndex;
		_treesLeaving.Add( entry );
		_farDirty = true;
	}

	/// <summary>
	/// Saplings appear at exactly Sapling Visible Range (m) from the camera and go at the same distance, no hysteresis
	/// (Mark 2026-10-20) — fading in and out over Small Object Fade (s) (they have no LODs). Chunks beyond range + ¾
	/// chunk are skipped as a whole once their saplings are hidden; the rest test each sapling's 3-D distance every frame.
	/// </summary>
	void TickSaplingVisibility()
	{
		if ( _vegetationHidden || _vegetationObjectsHidden || _loadSettings is null )
			return;
		if ( !TryGetStreamTransform( out var camEngine, out _ ) )
			return;

		var cam = TerrainWorldUnits.EngineToMeters( camEngine );
		var range = Math.Max( 5f, VegetationSaplingVisibleMeters );
		var rangeSq = range * range;
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var worldRadius = _loadSettings.TotalWorldRadiusMeters;
		var chunkReach = range + (chunkSize * 0.75f);
		var step = VegetationSmallFadeSeconds <= 0.001f ? 1f : Time.Delta / VegetationSmallFadeSeconds;

		foreach ( var entry in _loaded.Values )
		{
			if ( entry.Saplings.Count == 0 )
				continue;

			var cx = -worldRadius + ((entry.Coord.X + 0.5f) * chunkSize) - cam.x;
			var cy = -worldRadius + ((entry.Coord.Y + 0.5f) * chunkSize) - cam.y;
			var far = (cx * cx) + (cy * cy) > chunkReach * chunkReach;
			if ( far && entry.SaplingsAllHidden )
				continue;

			var anyShown = false;
			foreach ( var sapling in entry.Saplings )
			{
				var go = sapling.Go;
				if ( go is null || !go.IsValid() )
					continue;
				var target = !far && (sapling.PosMeters - cam).LengthSquared <= rangeSq ? 1f : 0f;
				if ( sapling.Fade == target )
				{
					anyShown |= target > 0f;
					continue;
				}
				if ( !go.Enabled )
					go.Enabled = true;
				sapling.Fade = target > sapling.Fade ? Math.Min( target, sapling.Fade + step ) : Math.Max( target, sapling.Fade - step );
				foreach ( var renderer in sapling.Renderers )
				{
					if ( renderer is not null && renderer.IsValid() )
						renderer.Attributes.Set( "TreeFade", sapling.Fade );
				}
				if ( sapling.Fade <= 0f )
					go.Enabled = false;
				anyShown |= sapling.Fade > 0f;
			}
			entry.SaplingsAllHidden = !anyShown;
		}
	}

	/// <summary>A freshly spawned rock / stick starts fully dithered out and fades in (no LODs, so a fade is allowed).</summary>
	void BeginSmallFadeIn( GameObject instance )
	{
		var renderers = instance.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ).ToArray();
		if ( renderers.Length == 0 || VegetationSmallFadeSeconds <= 0.001f )
			return;
		foreach ( var renderer in renderers )
			renderer.Attributes.Set( "TreeFade", 0f );
		_smallFades.Add( new SmallFade { Renderers = renderers } );
	}

	void TickSmallFades()
	{
		if ( _smallFades.Count == 0 )
			return;
		var step = VegetationSmallFadeSeconds <= 0.001f ? 1f : Time.Delta / VegetationSmallFadeSeconds;
		for ( var i = _smallFades.Count - 1; i >= 0; i-- )
		{
			var fade = _smallFades[i];
			fade.T = Math.Min( 1f, fade.T + step );
			foreach ( var renderer in fade.Renderers )
			{
				if ( renderer is not null && renderer.IsValid() )
					renderer.Attributes.Set( "TreeFade", fade.T );
			}
			if ( fade.T >= 1f )
				_smallFades.RemoveAt( i );
		}
	}

	/// <summary>A chunk that left the tree object range: the ring has drawn its trees for a few frames, now the objects go (felled ones come back as stumps when re-planted — WorldScatterIdentity remembers).</summary>
	void TickTreesLeaving()
	{
		for ( var i = _treesLeaving.Count - 1; i >= 0; i-- )
		{
			var entry = _treesLeaving[i];
			if ( !entry.TreesLeaving || !_loaded.ContainsKey( entry.Coord ) || entry.GameObject is null || !entry.GameObject.IsValid() )
			{
				_treesLeaving.RemoveAt( i );
				continue;
			}
			if ( _farActive && _frameIndex - entry.TreesLeaveFrame < FarUnloadOverlapFrames )
				continue;

			_treesLeaving.RemoveAt( i );
			foreach ( var state in entry.LodTrees )
			{
				if ( state.Go is not null && state.Go.IsValid() )
					state.Go.Destroy();
			}
			entry.LodTrees.Clear();
			entry.Trunks.Clear();
			entry.PendingSpawns.RemoveAll( p => p.Name.StartsWith( "veg_", StringComparison.Ordinal ) && !p.Name.StartsWith( "veg_rock", StringComparison.Ordinal ) && !p.Name.StartsWith( "veg_stick", StringComparison.Ordinal ) && !p.Name.StartsWith( "veg_clover_small", StringComparison.Ordinal ) );
			entry.HasLargeTrees = false;
			entry.TreesLeaving = false;
			entry.FarHandoverFrame = -1;
			CollectVegetationColliders( entry );
		}
	}

	void ScatterSmallVegetationOnChunk( LoadedChunk entry, TerrainPreviewSettings settings )
	{
		TerrainVegetationScatter.PopulateSmallTrees(
			entry.GameObject,
			entry.Coord,
			settings,
			_backend,
			ChunkSizeMeters,
			BuildVegetationOptions(),
			entry.Trunks,
			entry.PendingSpawns );
		entry.HasSmallVegetation = true;
		QueueVegetationSpawns( entry );
	}

	/// <summary>
	/// Near-only scatter cost follows the chunk's terrain collider (collision range, ~200 m): tree / prop
	/// colliders, and tree / prop shadows. Thousands of static tree meshes on render-only far chunks cost
	/// physics for nothing, and every shadow caster is drawn again into each sun cascade. Saplings never
	/// cast shadows (too small to read, and the most numerous scatter). Collected once per scatter,
	/// toggled only when the chunk collider flips (<see cref="UpdateChunkColliders"/>).
	/// </summary>
	void CollectVegetationColliders( LoadedChunk entry )
	{
		entry.VegetationColliders.Clear();
		entry.VegetationShadowRenderers.Clear();
		if ( entry.GameObject is null || !entry.GameObject.IsValid() )
			return;

		foreach ( var child in entry.GameObject.Children )
		{
			if ( !child.Name.StartsWith( "veg_", StringComparison.Ordinal ) )
				continue;

			var sapling = child.Name.StartsWith( "veg_clover_small", StringComparison.Ordinal );
			foreach ( var col in child.Components.GetAll<ModelCollider>( FindMode.EverythingInSelfAndDescendants ) )
			{
				if ( col is not null && col.IsValid() )
					entry.VegetationColliders.Add( col );
			}

			foreach ( var renderer in child.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
			{
				if ( renderer is null || !renderer.IsValid() )
					continue;
				if ( sapling )
					renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
				else
					entry.VegetationShadowRenderers.Add( renderer );
			}
		}

		SetVegetationNear( entry, entry.Collider is not null && entry.Collider.IsValid() && entry.Collider.Enabled );

		var shadowsOn = false;
		if ( _loadSettings is not null && TryGetStreamTransform( out var streamPosEngine, out _ ) )
		{
			var chunkSize = Math.Max( 32f, ChunkSizeMeters );
			var streamPos = TerrainWorldUnits.EngineToMeters( streamPosEngine );
			var center = TerrainChunkStreaming.GetChunkCenterWorld( entry.Coord, _loadSettings.TotalWorldRadiusMeters, chunkSize );
			var distance = new Vector3( center.x - streamPos.x, center.y - streamPos.y, 0f ).Length;
			shadowsOn = IsChunkInShadowRange( distance, chunkSize );
		}
		SetVegetationShadows( entry, shadowsOn );
		CollectLodTrees( entry );
	}

	/// <summary>
	/// Colliders follow the chunk collider (Collision Range); shadows have their own, tighter band. Going near
	/// queues the tree colliders (a few per frame — each builds a physics shape); going far switches them off at once.
	/// </summary>
	void SetVegetationNear( LoadedChunk entry, bool near )
	{
		Stats.NearFlips++;
		foreach ( var col in entry.VegetationColliders )
		{
			if ( col is null || !col.IsValid() )
				continue;
			if ( near )
			{
				if ( !col.Enabled )
					_vegColliderEnableQueue.Add( (entry, col) );
			}
			else
				col.Enabled = false;
		}
	}

	void SetVegetationShadows( LoadedChunk entry, bool on )
	{
		entry.ShadowsOn = on;
		var shadows = on ? ModelRenderer.ShadowRenderType.On : ModelRenderer.ShadowRenderType.Off;
		foreach ( var renderer in entry.VegetationShadowRenderers )
		{
			if ( renderer is not null && renderer.IsValid() )
				renderer.RenderType = shadows;
		}
	}

	TerrainVegetationScatter.Options BuildVegetationOptions() =>
		new()
		{
			Enabled = true,
			Profiles =
			[
				new TerrainVegetationScatter.BiomeScatterProfile
				{
					BiomeId = TerrainPreviewBiomeId.CloverHills,
					Prefabs = VegetationCloverTreePrefabs?.ToArray(),
					NoiseSeedSalt = 0,
					InstancePrefix = "veg_clover",
					Density01 = 1f,
					SmallPrefabs = VegetationCloverSaplingPrefabs?.ToArray(),
					SmallCellSpacingMeters = VegetationCloverSaplingSpacingMeters,
					SmallChance01 = VegetationCloverSaplingChance01,
					SmallNearTrunkMeters = VegetationCloverSaplingNearTrunkMeters,
					SmallClearingChance01 = VegetationCloverSaplingClearingChance01,
					SmallTrunkClearanceMeters = VegetationCloverSaplingClearanceMeters,
					SmallScaleMin = VegetationCloverSaplingScaleMin,
					SmallScaleMax = VegetationCloverSaplingScaleMax,
					SmallMaxPerChunk = VegetationCloverSaplingMaxPerChunk,
				},
				new TerrainVegetationScatter.BiomeScatterProfile
				{
					BiomeId = TerrainPreviewBiomeId.RedwoodForest,
					Prefabs = [VegetationRedwoodPrefab, VegetationRedwoodPrefabB],
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
			SlopeTiltInfluence01 = VegetationSlopeTiltInfluence01,
			SlopeTiltMaxDegrees = VegetationSlopeTiltMaxDegrees,
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
		};

	bool ShouldScatterVegetation( int verticesPerSide )
	{
		// World-seeded vegetation is deterministic per-peer (same WorldSeed → same trees).
		// Player-placed flora (future farming) will NetworkSpawn separately.
		if ( !VegetationScatterEnabled || _vegetationHidden )
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

	float ResolveLodDemoteDistanceMeters( float chunkSize )
		=> chunkSize * (Math.Clamp( StreamHighPriorityRadiusChunks, 1f, 4f )
			+ Math.Clamp( StreamLodDemoteMarginChunks, 0.5f, 6f ));

	/// <summary>
	/// Shared border lattice for chunk edges: every chunk (any LOD) snaps its border verts onto the
	/// far-LOD grid, so full↔far seams are watertight without knowing neighbor LODs.
	/// </summary>
	int ResolveBorderLatticeVerticesPerSide( int verticesPerSide )
	{
		if ( !StreamMeshLodEnabled )
			return 0;

		return Math.Min( verticesPerSide, Math.Clamp( StreamFarVerticesPerSide, 9, verticesPerSide ) );
	}

	/// <summary>Chunks whose far trees were ordered back and whose real trees wait for the tile to rebuild.</summary>
	readonly List<LoadedChunk> _unloadingChunks = new();

	/// <summary>
	/// Unload = the mirror of the load hand-over: the far ring is told FIRST, the chunk (its real trees) is destroyed
	/// only once the far tiles over it have rebuilt. Destroy-then-rebuild left the spot empty for 1-8 frames — the flash
	/// probe's "vanished and returned" pixels at the trailing edge (2026-10-15). A chunk that comes back inside the keep
	/// ring while waiting simply stays.
	/// </summary>
	void UnloadChunk( TerrainChunkCoord coord )
	{
		if ( !_loaded.TryGetValue( coord, out var entry ) )
			return;
		if ( entry.Unloading )
			return;   // already on its way out (TickDeferredUnloads)

		if ( entry.FarHandoverFrame >= 0 && _farActive )
		{
			// The far trees resume drawing this frame (ChunkHasRealTrees is false while Unloading); the real ones go a few frames later.
			entry.Unloading = true;
			entry.UnloadFrame = _frameIndex;
			entry.PendingSpawns.Clear();
			_unloadingChunks.Add( entry );
			_farDirty = true;
			return;
		}

		DestroyChunk( entry );
	}

	void DestroyChunk( LoadedChunk entry )
	{
		var coord = entry.Coord;
		Stats.Unloads++;
		entry.PendingSpawns.Clear();
		if ( entry.GameObject is not null && entry.GameObject.IsValid() )
			entry.GameObject.Destroy();
		_loaded.Remove( coord );
		ReleaseRiverWater( coord );
		// Deterministic per-coord value — drop it with the chunk so the map does not grow with everything ever explored.
		_chunkGroundZMeters.Remove( coord );
	}

	void TickDeferredUnloads()
	{
		Stats.LoadedChunks = _loaded.Count;
		Stats.UnloadingChunks = _unloadingChunks.Count;
		for ( var i = _unloadingChunks.Count - 1; i >= 0; i-- )
		{
			var entry = _unloadingChunks[i];
			if ( !_loaded.ContainsKey( entry.Coord ) )
			{
				_unloadingChunks.RemoveAt( i );
				continue;
			}

			if ( _streamKeep.Contains( entry.Coord ) )
			{
				// Back inside the keep ring: stay; the far trees stop again at once (the real ones never left).
				entry.Unloading = false;
				_unloadingChunks.RemoveAt( i );
				continue;
			}

			// The far trees have been drawing over the real ones for a few frames (our own hook, no engine lag).
			if ( !FarTreesCoverUnload( entry ) )
				continue;

			_unloadingChunks.RemoveAt( i );
			DestroyChunk( entry );
		}
	}

	void UnloadAll()
	{
		UnloadFarTerrain();
		CancelPendingChunkBuilds();
		_spawnPending.Clear();
		_treesLeaving.Clear();
		WorldScatterIdentity.ClearRemembered();
		_unloadQueue.Clear();
		_unloadQueued.Clear();
		_unloadingChunks.Clear();
		_staleReloadPending = false;
		foreach ( var entry in _loaded.Values )
			entry.GameObject.Destroy();

		_loaded.Clear();
		DestroyRiverSurface();
		DestroyWorldGrass();
		DestroyFarTreeCards();
		_streamNeeded.Clear();
		_streamKeep.Clear();
		_streamLoadQueue.Clear();
		_unloadScratch.Clear();
		LoadedChunkCount = 0;
		MeshedChunkCount = 0;
		_lastStreamRefreshHeadingBucket = int.MinValue;
		_lastStreamRefreshElevationMeters = float.MinValue;
		_chunkGroundZMeters.Clear();
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

	bool _settling;
	float _settleStartedAt;
	int _settleGoodFrames;
	/// <summary>A frame slower than this breaks the streak (16.7 ms = 60 fps; the engine's idle throttles still pass).</summary>
	const float SettleFrameSeconds = 0.0167f;
	const int SettleGoodFramesRequired = 90;
	/// <summary>The post-load nav bakes run ~20 s; never hold the screen past this.</summary>
	const float SettleTimeoutSeconds = 30f;

	/// <summary>After the world is ready: hold the load screen until 90 consecutive frames come in under 16.7 ms (or 30 s pass).</summary>
	void TickSettle()
	{
		if ( !_settling )
			return;

		_settleGoodFrames = Time.Delta <= SettleFrameSeconds ? _settleGoodFrames + 1 : 0;
		var waited = Time.Now - _settleStartedAt;
		if ( _settleGoodFrames < SettleGoodFramesRequired && waited < SettleTimeoutSeconds )
		{
			if ( ShowWorldLoadScreen )
				ShowLoadScreen( "Loading World", $"Settling… {1f / Math.Max( Time.Delta, 0.001f ):0} fps", 1f );
			return;
		}

		_settling = false;
		SetStreamerInputEnabled( true );
		HideLoadScreen();
		Log.Info( $"[TerrainWorldManager] Settled after {waited:0.0} s ({(_settleGoodFrames >= SettleGoodFramesRequired ? "steady frames" : "timeout")})." );
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

	/// <summary>The map rasterised this session, or null while it is still baking. Never a PNG read back from disk (path-cached, stale).</summary>
	public Texture GetHudBiomeMapTexture()
		=> BiomePreviewMap is not null && BiomePreviewMap.IsValid() ? BiomePreviewMap : null;

	bool EnsureMinimapScreen()
	{
		// Player HUD owns the minimap when a local pawn ScreenPanel is present: park the camera one.
		if ( HasLocalPlayerMinimapHostCached() )
		{
			if ( _minimapScreen is not null && _minimapScreen.IsValid() )
				_minimapScreen.Suspend();
			return true;
		}

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
		// A "no" is re-checked once a second: the L-spawn invalidation can run before the new pawn is
		// input-owned, and a cached "no" kept the camera minimap ticking under the pawn's forever.
		if ( _minimapHostResolved && (_hasLocalPlayerMinimapHost || _sinceMinimapHostScan < 1f) )
			return _hasLocalPlayerMinimapHost;

		_minimapHostResolved = true;
		_sinceMinimapHostScan = 0f;
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
