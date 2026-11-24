using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Clover Hills forage and beehives (Mark 2026-11-02, prompts 20 + 22). Models by the Opus art sessions; the
/// how-many lives here. Every forage kind is a <see cref="TerrainVegetationScatter.PropClusterOptions"/>
/// cluster on the same deterministic cell grid as the rocks and sticks: a cell of <c>spacing</c> metres rolls
/// a cluster with <c>chance</c>, and a cluster is one to a few plants round a centre - the "chance to spawn
/// 2 or 3 with it" Mark asked for on the daisies and mushrooms (hobbit's leaf was scattered here until Mark pulled it 2026-11-19, to come back later). Densities, per km² of
/// Clover Hills at <see cref="VegetationForageDensity01"/> = 1 (clusters = chance / spacing²):
///
/// | kind            | spacing | chance | per cluster | clusters / km² |
/// |-----------------|---------|--------|-------------|----------------|
/// | wild wheat      | 90 m    | 0.025  | 3–6 tufts   | ~6             |
/// | night crawlers  | 110 m   | 0.40   | 2–4 casts   | ~33            |
/// | daisies         | 35 m    | 0.60   | 1–3         | ~490           |
/// | blueberry bush  | 150 m   | 0.40   | 1–2         | ~18            |
/// | leafy bush      | 32 m    | 0.60   | 1–3         | ~586 (≈ 1,170 bushes) |
/// | red mushrooms   | 60 m    | 0.50   | 1–3         | ~139           |
///
/// Note the cell never exceeds a chunk: <c>ClusterCell</c> clamps every spacing to the 64 m chunk, so the 90 / 110 /
/// 150 m rows above really roll one cell per chunk (the column on the right is chance / 64²; wheat measured 122 / km² at the old 0.5 chance on seed 1337, see AGENTS.md
/// *Forage*; Mark 2026-11-02 then cut it to 5 % - "too many wheat seeds everywhere" - so ~6 / km², one bed per ~400 m).
///
/// Every wild-wheat cluster stands on its own bed of dark tilled soil (Mark 2026-11-02: "can you make the wheat spawn on
/// tilled soil?"): a <see cref="TerrainGroundPatch"/> of cluster radius + <see cref="WildWheatTilledPadMeters"/> (≈ 2.7 m,
/// wobbled rim) planned from the same cluster cell (<see cref="TerrainVegetationScatter.PlanGroundPatches"/> on the chunk
/// worker) — painted into the chunk splat, no grass clumps on it, sowable, and carried into a voxel column dug there.
///
/// Beehives hang from a limb of one large Clover tree in fifty (<see cref="VegetationCloverBeehiveChance01"/>
/// = 0.02, rolled per spawned tree) at 3.2–4.4 m up and 1.0–1.8 m out from the trunk - low enough to catch
/// the eye and to be reached from the ground.
/// </summary>
public sealed partial class TerrainWorldManager
{
	const string ForagePrefabFolder = "prefabs/environment/cloverHills/groundFoliage/";
	const string NestPrefabFolder = "prefabs/environment/cloverHills/resources/";

	[Property, Group( "Vegetation - Forage" ), Title( "Forage scatter enabled" )]
	public bool VegetationForageEnabled { get; set; } = true;

	[Property, Group( "Vegetation - Forage" ), Title( "Forage density (0-2, scales every kind's cluster chance)" ), Range( 0f, 2f ), Step( 0.05f )]
	public float VegetationForageDensity01 { get; set; } = 1f;

	[Property, Group( "Vegetation - Forage" ), Title( "Beehive prefabs" )]
	public List<string> VegetationCloverBeehivePrefabs { get; set; } =
	[
		"prefabs/environment/cloverHills/resources/beehive1.prefab",
		"prefabs/environment/cloverHills/resources/beehive2.prefab",
	];

	[Property, Group( "Vegetation - Forage" ), Title( "Beehive chance per large tree (0-1)" ), Range( 0f, 0.25f ), Step( 0.005f ), Description( "0.02 = one tree in fifty carries a hive." )]
	public float VegetationCloverBeehiveChance01 { get; set; } = 0.02f;

	[Property, Group( "Vegetation - Forage" ), Title( "Beehive hang height min (m)" ), Range( 1.5f, 8f ), Step( 0.1f )]
	public float VegetationCloverBeehiveHangMinMeters { get; set; } = 3.2f;

	[Property, Group( "Vegetation - Forage" ), Title( "Beehive hang height max (m)" ), Range( 1.5f, 8f ), Step( 0.1f )]
	public float VegetationCloverBeehiveHangMaxMeters { get; set; } = 4.4f;

	[Property, Group( "Vegetation - Forage" ), Title( "Beehive limb reach min (m)" ), Range( 0.3f, 4f ), Step( 0.1f )]
	public float VegetationCloverBeehiveReachMinMeters { get; set; } = 1f;

	[Property, Group( "Vegetation - Forage" ), Title( "Beehive limb reach max (m)" ), Range( 0.3f, 4f ), Step( 0.1f )]
	public float VegetationCloverBeehiveReachMaxMeters { get; set; } = 1.8f;

	// Bird nests (Mark 2026-11-10, prompt 28): one large Clover tree in ~35 carries a nest in a limb fork, and a ground
	// nest sits in one 120 m cell in three. Both are the hand-harvest ResourceItemDefinition on the prefab (E: 0-2 eggs,
	// 0-4 feathers, respawn 15 min); a tree nest also spills on the ground when its tree is felled (BirdNest).
	[Property, Group( "Vegetation - Forage" ), Title( "Tree nest prefabs" )]
	public List<string> VegetationCloverNestPrefabs { get; set; } =
	[
		"prefabs/environment/cloverHills/resources/nest_tree.prefab",
	];

	[Property, Group( "Vegetation - Forage" ), Title( "Tree nest chance per large tree (0-1)" ), Range( 0f, 0.25f ), Step( 0.005f ), Description( "0.03 = one tree in about thirty-five holds a nest." )]
	public float VegetationCloverNestChance01 { get; set; } = 0.03f;

	[Property, Group( "Vegetation - Forage" ), Title( "Tree nest height min (m)" ), Range( 1.5f, 8f ), Step( 0.1f )]
	public float VegetationCloverNestHangMinMeters { get; set; } = 2.8f;

	[Property, Group( "Vegetation - Forage" ), Title( "Tree nest height max (m)" ), Range( 1.5f, 8f ), Step( 0.1f )]
	public float VegetationCloverNestHangMaxMeters { get; set; } = 4f;

	[Property, Group( "Vegetation - Forage" ), Title( "Tree nest limb reach min (m)" ), Range( 0.3f, 4f ), Step( 0.1f )]
	public float VegetationCloverNestReachMinMeters { get; set; } = 0.9f;

	[Property, Group( "Vegetation - Forage" ), Title( "Tree nest limb reach max (m)" ), Range( 0.3f, 4f ), Step( 0.1f )]
	public float VegetationCloverNestReachMaxMeters { get; set; } = 1.6f;

	[Property, Group( "Vegetation - Forage" ), Title( "Ground nest cell (m)" ), Range( 30f, 400f ), Step( 10f )]
	public float VegetationCloverGroundNestSpacingMeters { get; set; } = 120f;

	[Property, Group( "Vegetation - Forage" ), Title( "Ground nest chance per cell (0-1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float VegetationCloverGroundNestChance01 { get; set; } = 0.35f;

	/// <summary>The forage clusters, chances scaled by <see cref="VegetationForageDensity01"/>.</summary>
	TerrainVegetationScatter.PropClusterOptions[] BuildForageClusters()
	{
		var density = Math.Clamp( VegetationForageDensity01, 0f, 2f );
		var enabled = VegetationForageEnabled && density > 1e-3f;

		TerrainVegetationScatter.PropClusterOptions Make( string kind, string[] names, int salt, float spacing, float chance, int sizeMin, int sizeMax, float radius, string folder = ForagePrefabFolder, float scaleMin = 0.9f, float scaleMax = 1.15f )
		{
			var prefabs = new string[names.Length];
			for ( var i = 0; i < names.Length; i++ )
				prefabs[i] = folder + names[i] + ".prefab";

			return new TerrainVegetationScatter.PropClusterOptions
			{
				Enabled = enabled,
				BiomeId = TerrainPreviewBiomeId.CloverHills,
				Prefabs = prefabs,
				Prefab = prefabs[0],
				InstancePrefix = "veg_" + kind,
				KindLabel = kind,
				NoiseSeedSalt = salt,
				ClusterSpacingMeters = spacing,
				ClusterChance01 = Math.Clamp( chance * density, 0f, 1f ),
				ClusterSizeMin = sizeMin,
				ClusterSizeMax = sizeMax,
				ClusterRadiusMeters = radius,
				ScaleMin = scaleMin,
				ScaleMax = scaleMax,
				MaxPerChunk = 40,
			};
		}

		TerrainVegetationScatter.PropClusterOptions[] clusters =
		[
			Make( "wheat", ["wheat_wild1_v2", "wheat_wild2_v2", "wheat_wild3_v2"], 9300, 90f, 0.025f, 3, 6, 1.5f ),
			Make( "nightcrawler", ["nightcrawler1_v2", "nightcrawler2_v2"], 9400, 110f, 0.4f, 2, 4, 1.2f ),
			// Daisies and mushrooms stand about 5x life size (0.7 - 0.95 m and 0.45 - 0.65 m) so they read from a distance
			// (Mark 2026-11-19: "so small i could step on them and not know it").
			Make( "daisy", ["daisy1", "daisy2", "daisy3"], 9500, 35f, 0.6f, 1, 3, 1.6f, scaleMin: 2f, scaleMax: 2.4f ),
			Make( "blueberry", ["blueberry_bush1", "blueberry_bush2"], 9700, 150f, 0.4f, 1, 2, 2f ),
			// Leafy bushes (Mark 2026-11-19): plain green shrubs, waist to shoulder high; hand harvest gives plant fiber.
			// Spread through the whole biome since 2026-11-23 (Mark: "add those randomly throughout the biome? Its empty at
			// the moment" - the first pass was one clump of 3-6 per ~108 m): a 32 m cell, 0.6, 1-3 bushes across a 3 m disc,
			// so a small group every ~40 m. Scale kept near 1 so the 128 texels / m leaf cards stay at that density.
			Make( "bush", ["leafy_bush1_v1", "leafy_bush2_v1", "leafy_bush3_v1", "leafy_bush4_v1", "leafy_bush5_v1"], 9650, 32f, 0.6f, 1, 3, 3f, scaleMin: 0.95f, scaleMax: 1.05f )
				with { TrunkClearanceMeters = 1.5f },
			Make( "mushroom", ["mushroom_red1", "mushroom_red2", "mushroom_red3"], 9800, 60f, 0.5f, 1, 3, 1.6f, scaleMin: 2f, scaleMax: 2.4f ),
			// Ground bird nests (prompt 28): one per cell.
			Make( "nest", ["nest_ground"], 9900, Math.Clamp( VegetationCloverGroundNestSpacingMeters, 30f, 400f ), Math.Clamp( VegetationCloverGroundNestChance01, 0f, 1f ), 1, 1, 0.5f, NestPrefabFolder ),
		];

		// Wild wheat grows on its own bed of dark tilled soil (Mark 2026-11-02: "can you make the wheat spawn on tilled soil?")
		for ( var i = 0; i < clusters.Length; i++ )
		{
			if ( clusters[i].KindLabel == "wheat" )
				clusters[i] = clusters[i] with { TilledPatchPadMeters = WildWheatTilledPadMeters };
		}
		return clusters;
	}

	/// <summary>How far the wheat's tilled bed reaches past the cluster radius (1.5 m): a 2.7 m-radius bed, ±19 % wobble, 0.75 m soft rim (Mark 2026-11-02: smaller patches).</summary>
	const float WildWheatTilledPadMeters = 1.2f;

	/// <summary>The cluster kinds that lay generated tilled soil (today: wild wheat), for the chunk worker's <see cref="TerrainVegetationScatter.PlanGroundPatches"/>.</summary>
	TerrainVegetationScatter.PropClusterOptions[] GroundPatchSources()
	{
		var all = ConcatClusters( BuildForageClusters(), PoiPatchClusters() );   // + the crashed pod's burnt ground
		var list = new List<TerrainVegetationScatter.PropClusterOptions>( 1 );
		foreach ( var c in all )
		{
			if ( c.Enabled && c.TilledPatchPadMeters > 0f )
				list.Add( c );
		}
		return list.ToArray();
	}

	static TerrainVegetationScatter.PropClusterOptions[] ConcatClusters( TerrainVegetationScatter.PropClusterOptions[] a, TerrainVegetationScatter.PropClusterOptions[] b )
	{
		var result = new TerrainVegetationScatter.PropClusterOptions[a.Length + b.Length];
		a.CopyTo( result, 0 );
		b.CopyTo( result, a.Length );
		return result;
	}
}
