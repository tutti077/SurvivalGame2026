using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Clover Hills stone (Mark 2026-11-02: "I dont see any stone spires in the terraintest, they should spawn throughout all of
/// clover hills, primarily in those clearings that we make (not mushed between trees etc). Also our other stone models are
/// missing as well!"; 2026-11: mineable rocks, sectioned spires and boulders). Three layers, all
/// <see cref="TerrainVegetationScatter.PropClusterOptions"/> on the deterministic cluster grid (one cell per 64 m chunk at most),
/// all switched by **Vegetation - Clover — Scatter Rocks**:
///
/// | kind            | prefabs                                   | cell  | chance | per cluster | where                         | per km² (seed 1337) |
/// |-----------------|-------------------------------------------|-------|--------|-------------|-------------------------------|---------------------|
/// | stones          | pebble1–3 + rockpickup1 (hand harvest), rock_break1–4_v1 (pickaxe, 0.9–1.5 m) | Rock Cluster Spacing (50 m) | Rock Cluster Chance (0.7) | 2–5 | anywhere (prop range) | ~170 clusters, ~300 breakable rocks (half the members) |
/// | boulders        | rock_boulder1–3_v1 (~3 m, 4–6 sections)   | 64 m  | 0.12   | 1–2         | anywhere, 3.5 m off trunks    | 29.7 clusters       |
/// | stone spires    | rock_spire1–3_v1 (29–40 m authored, ×1.15 → 33–46 m, six sections) | 64 m | 0.30 | 1          | clearings only (12 m disc)    | ~25 (≈ one / 200 m) |
///
/// (Cluster counts measured with <c>world_find</c> over the 21.4 km² of Clover Hills within 4 km of spawn, seed 1337.)
///
/// Everything but the pebbles is a <see cref="MineableRock"/> (Blender/scripts/create_mineable_rocks.py: models, renders and
/// prefabs): the pickaxe breaks a rock_break in 4 hits into 3–5 stone; a boulder section takes 5 hits (3–5 stone), a spire
/// section 8 (6–9 stone), and every section left resting on nothing bursts into stone bits that rain down (2–4 / 4–6 stone
/// each). Spires are grapple targets (prefab and section tag <c>grapple</c>, static mesh collider per section; Mark
/// 2026-11-02: "the goal is for players to grapple them"), are twice as wide as authored (height unchanged), 40 % stand
/// straight up and the rest lean 5–15° toward a hashed heading (2026-11-04), and stand at the lowest ground under their
/// 9 m (4.5 m × width × scale) footprint ring, 1 m (× scale) plus that ring × sin(lean) deeper still, so no side floats
/// (<see cref="TerrainVegetationScatter.PropClusterOptions.FootprintRadiusMeters"/>). The sectioned boulders replace the old
/// scenery boulders (cloverhills_rock1–5, compiled leftovers without sources) at the same density: a rock that looks
/// mineable must be.
///
/// Boulders and spires ride the large-tree pass (<c>WithTrees</c>: planted inside Tree Objects Range, kept until the chunk
/// unloads) so a landmark does not pop in at the 192 m prop range. A clearing is where the forest-patch noise is under
/// Forest Patch Threshold — the planner never puts a large tree there — so a spire whose centre and a 12 m ring are all
/// clearing stands in open grass with no trunk against it. The pebbles are the 2026-08-31 compiled models (sources never
/// committed) on <c>rock_gunmetal_pebble</c>; the mineable set wears <c>rock_gunmetal</c> (256 px, tiling 1) on UVs authored
/// at 2 m per tile = 128 texels / m (Blender/scripts/create_rock_gunmetal_texture.py).
/// </summary>
public sealed partial class TerrainWorldManager
{
	const string CloverRockFolder = "prefabs/environment/cloverHills/rocks/";

	/// <summary>
	/// The stone clusters when the scene lists none (a scene saved before the list existed loads it empty): the pebbles and
	/// rockpickup1 are the hand-harvest stones (~0.2 m); rock_break1–4 (0.9–1.5 m) among them break with the pickaxe - Mark
	/// 2026-11-02: "I dont want the big rocks to be hand harvestable anymore".
	/// </summary>
	static string[] DefaultCloverRockPrefabs =>
	[
		CloverRockFolder + "environment_cloverhills_pebble1.prefab",
		CloverRockFolder + "environment_cloverhills_pebble2.prefab",
		CloverRockFolder + "environment_cloverhills_pebble3.prefab",
		CloverRockFolder + "environment_rockpickup1.prefab",
		CloverRockFolder + "rock_break1_v1.prefab",
		CloverRockFolder + "rock_break2_v1.prefab",
		CloverRockFolder + "rock_break3_v1.prefab",
		CloverRockFolder + "rock_break4_v1.prefab",
	];

	[Property, Group( "Vegetation" ), Title( "Clover — Rock Prefabs" ), Description( "Stones in the rock clusters; each member picks one by hash. Empty = the built-in pebble1–3 + rockpickup1 (hand harvest) and rock_break1–4 (pickaxe)." )]
	public List<string> VegetationCloverRockPrefabs { get; set; } = [];

	string[] CloverRockPrefabs()
		=> VegetationCloverRockPrefabs is { Count: > 0 } ? VegetationCloverRockPrefabs.ToArray() : DefaultCloverRockPrefabs;

	/// <summary>Dev (<c>world_find</c>): planned centres of a prop cluster kind, or <c>camp</c> sites, within a radius — (x, y, ground) metres.</summary>
	internal List<Vector3> DevFindPlanned( string kind, float xMeters, float yMeters, float radiusMeters )
	{
		var result = new List<Vector3>();
		if ( _loadSettings is null || _backend is null || string.IsNullOrWhiteSpace( kind ) )
			return result;
		if ( string.Equals( kind, "camp", StringComparison.OrdinalIgnoreCase ) )
		{
			var forest = TerrainVegetationScatter.ForestPatchField.ForBiome( _loadSettings, BuildVegetationOptions(), TerrainPreviewBiomeId.CloverHills );
			foreach ( var site in EnemyCampSites.PlanSitesNear( _loadSettings, _backend, _loadSettings.WorldSeed, forest, xMeters, yMeters, radiusMeters ) )
				result.Add( new Vector3( site.X, site.Y, site.HeightMeters ) );
			return result;
		}
		TerrainVegetationScatter.FindClusterCenters( _loadSettings, _backend, BuildVegetationOptions(), kind.Trim(), xMeters, yMeters, radiusMeters, ChunkSizeMeters, result );
		return result;
	}

	/// <summary>Boulders and stone spires (the table above).</summary>
	TerrainVegetationScatter.PropClusterOptions[] BuildStoneClusters() =>
	[
		new TerrainVegetationScatter.PropClusterOptions
		{
			Enabled = VegetationCloverRocksEnabled,
			BiomeId = TerrainPreviewBiomeId.CloverHills,
			Prefabs =
			[
				CloverRockFolder + "rock_boulder1_v1.prefab",
				CloverRockFolder + "rock_boulder2_v1.prefab",
				CloverRockFolder + "rock_boulder3_v1.prefab",
			],
			Prefab = CloverRockFolder + "rock_boulder1_v1.prefab",
			InstancePrefix = "veg_boulder",
			KindLabel = "boulder",
			NoiseSeedSalt = 8200,
			ClusterSpacingMeters = 64f,
			ClusterChance01 = 0.12f,
			ClusterSizeMin = 1,
			ClusterSizeMax = 2,
			ClusterRadiusMeters = 6f,
			ScaleMin = 0.85f,
			ScaleMax = 1.15f,
			MaxPerChunk = 4,
			WithTrees = true,
			TrunkClearanceMeters = 3.5f,
			FootprintRadiusMeters = 1.2f,
			SinkMeters = 0.15f,
		},
		new TerrainVegetationScatter.PropClusterOptions
		{
			Enabled = VegetationCloverRocksEnabled,
			BiomeId = TerrainPreviewBiomeId.CloverHills,
			Prefabs =
			[
				CloverRockFolder + "rock_spire1_v1.prefab",
				CloverRockFolder + "rock_spire2_v1.prefab",
				CloverRockFolder + "rock_spire3_v1.prefab",
			],
			Prefab = CloverRockFolder + "rock_spire1_v1.prefab",
			InstancePrefix = "veg_spire",
			KindLabel = "spire",
			NoiseSeedSalt = 8300,
			ClusterSpacingMeters = 64f,
			ClusterChance01 = 0.3f,
			ClusterSizeMin = 1,
			ClusterSizeMax = 1,
			ClusterRadiusMeters = 0.25f,
			// 15 % thicker and longer than the authored needles (Mark 2026-11-03); footprint and sink follow the scale
			ScaleMin = 0.85f * 1.15f,
			ScaleMax = 1.15f * 1.15f,
			MaxPerChunk = 1,
			WithTrees = true,
			ClearingRadiusMeters = 16f,
			FootprintRadiusMeters = 4.5f,
			SinkMeters = 1f,
			// Twice as wide (height unchanged) and only a little variation in angle, some straight up (Mark 2026-11-04: "some
			// of the spires should be straight up … still far too skinny, make them 2x as thick and dont angle them as much")
			ThicknessMul = 2f,
			LeanUprightChance01 = 0.4f,
			LeanMinDegrees = 5f,
			LeanMaxDegrees = 15f,
			// Seen as far as the trees: past Tree Objects Range the far ring draws rock_spireN_v1_far.vmdl (Mark 2026-11-03)
			FarRing = true,
		},
	];
}
