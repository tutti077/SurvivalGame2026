using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Clover Hills forage and beehives (Mark 2026-11-02, prompts 20 + 22). Models by the Opus art sessions; the
/// how-many lives here. Every forage kind is a <see cref="TerrainVegetationScatter.PropClusterOptions"/>
/// cluster on the same deterministic cell grid as the rocks and sticks: a cell of <c>spacing</c> metres rolls
/// a cluster with <c>chance</c>, and a cluster is one to a few plants round a centre - the "chance to spawn
/// 2 or 3 with it" Mark asked for on the daisies, hobbit's leaf and mushrooms. Densities, per km² of
/// Clover Hills at <see cref="VegetationForageDensity01"/> = 1 (clusters = chance / spacing²):
///
/// | kind            | spacing | chance | per cluster | clusters / km² |
/// |-----------------|---------|--------|-------------|----------------|
/// | wild wheat      | 90 m    | 0.025  | 3–6 tufts   | ~6             |
/// | night crawlers  | 110 m   | 0.40   | 2–4 casts   | ~33            |
/// | daisies         | 35 m    | 0.60   | 1–3         | ~490           |
/// | hobbit's leaf   | 120 m   | 0.35   | 1–3         | ~24            |
/// | blueberry bush  | 150 m   | 0.40   | 1–2         | ~18            |
/// | red mushrooms   | 60 m    | 0.50   | 1–3         | ~139           |
///
/// Note the cell never exceeds a chunk: <c>ClusterCell</c> clamps every spacing to the 64 m chunk, so the 90 / 110 /
/// 120 / 150 m rows above really roll one cell per chunk (the column on the right is chance / 64²; wheat measured 122 / km² at the old 0.5 chance on seed 1337, see AGENTS.md
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

	/// <summary>The six forage clusters, chances scaled by <see cref="VegetationForageDensity01"/>.</summary>
	TerrainVegetationScatter.PropClusterOptions[] BuildForageClusters()
	{
		var density = Math.Clamp( VegetationForageDensity01, 0f, 2f );
		var enabled = VegetationForageEnabled && density > 1e-3f;

		TerrainVegetationScatter.PropClusterOptions Make( string kind, string[] names, int salt, float spacing, float chance, int sizeMin, int sizeMax, float radius )
		{
			var prefabs = new string[names.Length];
			for ( var i = 0; i < names.Length; i++ )
				prefabs[i] = ForagePrefabFolder + names[i] + ".prefab";

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
				ScaleMin = 0.9f,
				ScaleMax = 1.15f,
				MaxPerChunk = 40,
			};
		}

		TerrainVegetationScatter.PropClusterOptions[] clusters =
		[
			Make( "wheat", ["wheat_wild1_v2", "wheat_wild2_v2", "wheat_wild3_v2"], 9300, 90f, 0.025f, 3, 6, 1.5f ),
			Make( "nightcrawler", ["nightcrawler1_v2", "nightcrawler2_v2"], 9400, 110f, 0.4f, 2, 4, 1.2f ),
			Make( "daisy", ["daisy1", "daisy2", "daisy3"], 9500, 35f, 0.6f, 1, 3, 1f ),
			Make( "hobbitsleaf", ["hobbits_leaf1", "hobbits_leaf2"], 9600, 120f, 0.35f, 1, 3, 1.5f ),
			Make( "blueberry", ["blueberry_bush1", "blueberry_bush2"], 9700, 150f, 0.4f, 1, 2, 2f ),
			Make( "mushroom", ["mushroom_red1", "mushroom_red2", "mushroom_red3"], 9800, 60f, 0.5f, 1, 3, 1f ),
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
		var all = BuildForageClusters();
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
