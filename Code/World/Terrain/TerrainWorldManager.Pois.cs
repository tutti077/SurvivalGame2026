using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// The landmark points of interest of <c>data/pois.json</c> as terrain scatter (Mark 2026-11-19: "Distant + visible POIs: a
/// trail of smoke that trails into the air ... smoke trails that lead players to crashed stasis pods that are like totally
/// crumpled and exploded. Maybe some burnt grass around them ... They can then whack it with a pickaxe and get some
/// resources, like scrap").
/// <para>Every <see cref="PoiLandmarkData"/> row becomes one prop cluster kind (kind label = the row id, so
/// <c>world_find crashed_pod</c> lists them) on the tree pass like the spires (<c>WithTrees</c>: an object inside Tree
/// Objects Range, 330 m in the shipped scenes), so the <see cref="SmokeColumn"/> on its prefab stands long before the
/// player is near. One candidate per 64 m chunk at the row's <c>chancePerChunk</c>; <c>clearingRadiusMeters</c> keeps it
/// out of the trees; <c>bareGroundRadiusMeters</c> lays a bare patch (<see cref="TerrainGroundPatch.Bare"/>: the wheat
/// beds' mechanism without the soil paint) so no grass stands in the burnt ground. The crashed pod's prefab is a
/// <see cref="MineableRock"/>; its broken sections ride <see cref="WorldScatterIdentity"/> like any rock.</para>
/// <para>The structure POIs (abandoned buildings) are build pieces, not scatter: <see cref="PoiSites"/>.</para>
/// </summary>
public sealed partial class TerrainWorldManager
{
	[Property, Group( "Points Of Interest" ), Title( "Landmarks (crashed pods)" ), Description( "Scatter the landmark rows of data/pois.json." )]
	public bool PoiLandmarksEnabled { get; set; } = true;

	/// <summary>The landmark rows of <c>pois.json</c> as scatter cluster kinds.</summary>
	TerrainVegetationScatter.PropClusterOptions[] BuildPoiClusters()
	{
		var rows = PoiCatalog.Landmarks;
		var clusters = new List<TerrainVegetationScatter.PropClusterOptions>( rows.Count );
		for ( var i = 0; i < rows.Count; i++ )
		{
			var row = rows[i];
			var biome = row.ResolveBiome();
			if ( biome is TerrainPreviewBiomeId.None or TerrainPreviewBiomeId.Water )
				continue;
			const float memberRadius = 0.25f;
			clusters.Add( new TerrainVegetationScatter.PropClusterOptions
			{
				Enabled = PoiLandmarksEnabled,
				BiomeId = biome,
				Prefabs = [row.Prefab],
				Prefab = row.Prefab,
				// "veg_" = the manager's scatter bookkeeping (colliders, shadows, counts) finds its objects by that prefix
				InstancePrefix = "veg_poi_" + row.Id,
				KindLabel = row.Id,
				NoiseSeedSalt = 8700 + row.SeedSalt,
				ClusterSpacingMeters = 64f,
				ClusterChance01 = Math.Clamp( row.ChancePerChunk, 0.005f, 1f ),
				ClusterSizeMin = 1,
				ClusterSizeMax = 1,
				ClusterRadiusMeters = memberRadius,
				ScaleMin = 1f,
				ScaleMax = 1f,
				MaxPerChunk = 1,
				WithTrees = true,
				// Past Tree Objects Range the far-tree ring plans this landmark out to Far Trees - Range (m), and for a prefab
				// under a SmokeColumn stands the same moving column there (TerrainWorldManager.FarSmoke.cs), so the smoke is
				// seen from over a kilometre (Mark 2026-11-23: "see it from 1000m away ... right now players can barely see it")
				FarRing = true,
				ClearingRadiusMeters = Math.Max( 0f, row.ClearingRadiusMeters ),
				TilledPatchPadMeters = Math.Max( 0f, row.BareGroundRadiusMeters - memberRadius ),
				PatchIsBare = true,
			} );
		}
		return clusters.ToArray();
	}

	TerrainPreviewSettings _poiPatchForestSettings;
	TerrainVegetationScatter.PropClusterOptions[] _poiPatchClusters;

	/// <summary>
	/// The landmark kinds for the ground-patch planner, each carrying its biome's forest field so the planner can apply the
	/// same clearing rule the scatter does (a patch must lie exactly where its wreck is planted). Built once per world load.
	/// </summary>
	TerrainVegetationScatter.PropClusterOptions[] PoiPatchClusters()
	{
		if ( _poiPatchClusters is not null && ReferenceEquals( _poiPatchForestSettings, _loadSettings ) )
			return _poiPatchClusters;

		var clusters = BuildPoiClusters();
		if ( _loadSettings is not null )
		{
			var options = BuildVegetationOptions();
			for ( var i = 0; i < clusters.Length; i++ )
				clusters[i] = clusters[i] with { PatchForest = TerrainVegetationScatter.ForestPatchField.ForBiome( _loadSettings, options, clusters[i].BiomeId ) };
			_poiPatchForestSettings = _loadSettings;
			_poiPatchClusters = clusters;
		}
		return clusters;
	}

	TerrainPreviewSettings _poiForestSettings;
	TerrainVegetationScatter.ForestPatchField _poiForest;

	/// <summary>What <see cref="PoiFarView"/> plans structure sites with: this world's settings, sampler and Clover Hills forest field
	/// (built once per world load). False until a world is loaded.</summary>
	internal bool TryGetPoiPlanning( out TerrainPreviewSettings settings, out ITerrainPreviewBackend backend, out TerrainVegetationScatter.ForestPatchField forest )
	{
		settings = _loadSettings;
		backend = _backend;
		forest = default;
		if ( settings is null || backend is null )
			return false;
		if ( !ReferenceEquals( _poiForestSettings, settings ) )
		{
			_poiForest = TerrainVegetationScatter.ForestPatchField.ForBiome( settings, BuildVegetationOptions(), TerrainPreviewBiomeId.CloverHills );
			_poiForestSettings = settings;
		}
		forest = _poiForest;
		return true;
	}

	/// <summary>Dev (<c>world_find poi</c>): the planned structure POI sites within a radius - (x, y, ground) metres.</summary>
	internal List<Vector3> DevFindPoiStructures( float xMeters, float yMeters, float radiusMeters, List<string> labels = null )
	{
		var result = new List<Vector3>();
		if ( _loadSettings is null || _backend is null )
			return result;
		var forest = TerrainVegetationScatter.ForestPatchField.ForBiome( _loadSettings, BuildVegetationOptions(), TerrainPreviewBiomeId.CloverHills );
		foreach ( var site in PoiSites.PlanSitesNear( _loadSettings, _backend, _loadSettings.WorldSeed, forest, xMeters, yMeters, radiusMeters ) )
		{
			result.Add( new Vector3( site.X, site.Y, site.HeightMeters ) );
			labels?.Add( site.LayoutId );
		}
		return result;
	}
}
