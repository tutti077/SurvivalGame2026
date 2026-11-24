using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Redwood Forest ground scatter (Mark 2026-11-16, trailer pass), all on the same deterministic cluster grid as the Clover
/// stones and forage (<see cref="TerrainVegetationScatter.PropClusterOptions"/>, one cell per 64 m chunk at most):
///
/// | kind        | prefabs                              | cell | chance | per cluster | notes                                         |
/// |-------------|--------------------------------------|------|--------|-------------|-----------------------------------------------|
/// | boulders    | redwood_slab1–5_v1 (4–8.5 m, mineable) | 64 m | 0.30   | 1–2         | flat, half buried, 0.5–1 m skirt under ground |
/// | sword ferns | fern_sword1–4_v1 (0.45–0.9 m)        | 16 m | 0.70   | 2–5         | decorative carpet                             |
/// | fiddleheads | fiddlehead1–3_v1 (~0.4 m)            | 40 m | 0.45   | 1–3         | hand harvest: 2–3 food_fiddlehead, 300 s      |
/// | raspberry   | raspberry_bush1–3_v1 (1.1–1.4 m)     | 90 m | 0.35   | 1–2         | hand harvest: 2–4 food_raspberry, 600 s       |
///
/// The plants ride the forage switch and density (**Vegetation - Forage**); the boulders have their own switch.
///
/// Large trees (the Redwood profile in <see cref="BuildVegetationOptions"/>): redwood1–5_v2 (81–101 m, ~3x the elm, 4.8–6.6 m
/// trunks, dense crowns over the top ~45 %; redwood2 / 4 with long horizontal limbs — Mark 2026-11-16: "3x taller than the
/// elm ... more brush at the top ... a dark and creepy forest") listed twice and fir1–4_v1 (Douglas fir, 18–29 m) once, one
/// tree per 15 m block (CellStride 3) at Redwood — Density 0.225 (halved twice 2026-11-23: "far far too many"; the forest's dark is the
/// biome air in data/biome_atmosphere.json, not the tree count). Both fell into a log that splits.
///
/// Models: Blender/scripts/create_redwood_trees.py, create_redwood_boulders.py and create_redwood_plants.py.
/// </summary>
public sealed partial class TerrainWorldManager
{
	const string RedwoodRockFolder = "prefabs/environment/redwoodForest/rocks/";
	const string RedwoodPlantFolder = "prefabs/environment/redwoodForest/groundFoliage/";
	const string RedwoodTreeFolder = "prefabs/environment/redwoodForest/trees/";

	/// <summary>The large trees when the scene lists none (a scene saved before the list existed loads it empty).</summary>
	static string[] DefaultRedwoodTreePrefabs
	{
		get
		{
			var list = new List<string>();
			for ( var pass = 0; pass < 2; pass++ )
			{
				for ( var i = 1; i <= 5; i++ )
					list.Add( $"{RedwoodTreeFolder}environment_redwood{i}_v2.prefab" );
			}
			for ( var i = 1; i <= 4; i++ )
				list.Add( $"{RedwoodTreeFolder}environment_fir{i}_v1.prefab" );
			return list.ToArray();
		}
	}

	string[] RedwoodTreePrefabs()
		=> VegetationRedwoodTreePrefabs is { Count: > 0 } ? VegetationRedwoodTreePrefabs.ToArray() : DefaultRedwoodTreePrefabs;

	[Property, Group( "Vegetation" ), Title( "Redwood — Scatter Boulders" )]
	public bool VegetationRedwoodBouldersEnabled { get; set; } = true;

	TerrainVegetationScatter.PropClusterOptions[] BuildRedwoodClusters()
	{
		var density = Math.Clamp( VegetationForageDensity01, 0f, 2f );
		var plants = VegetationForageEnabled && density > 1e-3f;

		TerrainVegetationScatter.PropClusterOptions Plant( string kind, string[] names, int salt, float spacing, float chance, int sizeMin, int sizeMax, float radius, int maxPerChunk )
		{
			var prefabs = new string[names.Length];
			for ( var i = 0; i < names.Length; i++ )
				prefabs[i] = RedwoodPlantFolder + names[i] + ".prefab";

			return new TerrainVegetationScatter.PropClusterOptions
			{
				Enabled = plants,
				BiomeId = TerrainPreviewBiomeId.RedwoodForest,
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
				ScaleMin = 0.85f,
				ScaleMax = 1.2f,
				MaxPerChunk = maxPerChunk,
			};
		}

		return
		[
			Plant( "swordfern", ["fern_sword1_v1", "fern_sword2_v1", "fern_sword3_v1", "fern_sword4_v1"], 9310, 16f, 0.7f, 2, 5, 3f, 60 ),
			Plant( "fiddlehead", ["fiddlehead1_v1", "fiddlehead2_v1", "fiddlehead3_v1"], 9320, 40f, 0.45f, 1, 3, 1.5f, 12 ),
			Plant( "raspberry", ["raspberry_bush1_v1", "raspberry_bush2_v1", "raspberry_bush3_v1"], 9330, 90f, 0.35f, 1, 2, 2.5f, 6 ),
			new TerrainVegetationScatter.PropClusterOptions
			{
				Enabled = VegetationRedwoodBouldersEnabled,
				BiomeId = TerrainPreviewBiomeId.RedwoodForest,
				Prefabs =
				[
					RedwoodRockFolder + "redwood_slab1_v1.prefab",
					RedwoodRockFolder + "redwood_slab2_v1.prefab",
					RedwoodRockFolder + "redwood_slab3_v1.prefab",
					RedwoodRockFolder + "redwood_slab4_v1.prefab",
					RedwoodRockFolder + "redwood_slab5_v1.prefab",
				],
				Prefab = RedwoodRockFolder + "redwood_slab1_v1.prefab",
				InstancePrefix = "veg_rwslab",
				KindLabel = "rwslab",
				NoiseSeedSalt = 8400,
				ClusterSpacingMeters = 64f,
				ClusterChance01 = 0.3f,
				ClusterSizeMin = 1,
				ClusterSizeMax = 2,
				ClusterRadiusMeters = 9f,
				ScaleMin = 0.85f,
				ScaleMax = 1.2f,
				MaxPerChunk = 4,
				WithTrees = true,
				TrunkClearanceMeters = 4.5f,
				FootprintRadiusMeters = 1.5f,
				SinkMeters = 0.1f,
			},
		];
	}
}
