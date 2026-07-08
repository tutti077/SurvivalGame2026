namespace Survival;



/// <summary>

/// Single orchestrator for terrain preview sampling. Stage order and contracts are documented in

/// <c>docs/TERRAIN_PREVIEW.md</c>.

/// </summary>

public static class TerrainPreviewPipeline

{

	public static TerrainPreviewSample Sample(

		TerrainPreviewSettings settings,

		float worldXMeters,

		float worldYMeters )

	{

		var landRadius = settings.LandRadiusMeters;

		var totalRadius = settings.TotalWorldRadiusMeters;

		if ( landRadius <= 0f || totalRadius <= 0f )

			return default;



		var distMeters = MathF.Sqrt( worldXMeters * worldXMeters + worldYMeters * worldYMeters );

		if ( distMeters > totalRadius )

			return default;



		if ( distMeters > landRadius )

			return SampleRimOcean( settings );



		var diameter = settings.WorldDiameterMeters;

		var nx = (worldXMeters + landRadius) / diameter;

		var ny = (worldYMeters + landRadius) / diameter;

		var seed = settings.WorldSeed;

		var maxHeight = Math.Max( 50f, settings.MaxTerrainHeightMeters );



		var rawLakeMask = TerrainPreviewLakeMap.SampleMaskAtWorldMeters(

			settings, worldXMeters, worldYMeters, seed );

		var isOpenWater = TerrainPreviewLandDiskFields.IsOpenWater( settings, worldXMeters, worldYMeters );



		if ( isOpenWater )

			return BuildOpenWaterSample( settings, maxHeight, rawLakeMask );



		var baseLayers = TerrainPreviewBaseHeight.Sample(
			settings, worldXMeters, worldYMeters, nx, ny, seed );
		var baseBeforeCurve = baseLayers.BeforeCurve01;
		var heightAfterCurve = baseLayers.AfterCurve01;



		var placementWeights = settings.UseContinuousBiomePlacementAtSample
			? TerrainPreviewBiomeResolver.SamplePlacementWeights(
				settings, worldXMeters, worldYMeters, heightAfterCurve )
			: TerrainPreviewLandDiskFields.GetFilteredPlacementWeights(
				settings, worldXMeters, worldYMeters );

		var mountainSpawnMask = TerrainPreviewMountainSpawnMask.SampleMask01(
			settings, worldXMeters, worldYMeters );

		var mountainField = TerrainPreviewMountainSpawnMask.SamplePlacement01(
			settings, worldXMeters, worldYMeters );

		var mountainHeightInfluence = TerrainPreviewMountainSpawnMask.SampleMountainHeightInfluence01(
			settings, worldXMeters, worldYMeters );

		var lowlandWeights = TerrainPreviewBiomeResolver.LandWeightsWithoutMountain( placementWeights );
		var lowlandShaped01 = TerrainPreviewBiomeTerrainShaper.ApplyBlendedShape01(
			settings,
			heightAfterCurve,
			lowlandWeights,
			nx,
			ny,
			seed,
			maxHeight,
			out var terrainDetail01 );

		var mountainBoost01 = TerrainPreviewBiomeMountainPeaks.SampleBoost01(
			settings,
			mountainHeightInfluence,
			rawLakeMask,
			distMeters,
			nx,
			ny,
			seed,
			out var mountain );

		var dryLandHeightMeters = TerrainPreviewLandHeightDisplay.ApplyDryLandMeters(
			settings,
			heightAfterCurve,
			lowlandShaped01,
			mountainBoost01,
			mountainHeightInfluence,
			maxHeight );

		dryLandHeightMeters = TerrainPreviewCoastalSmoothing.ApplyMeters(
			settings, dryLandHeightMeters, worldXMeters, worldYMeters, distMeters, nx, ny, seed );

		dryLandHeightMeters = TerrainPreviewLakeCombine.Apply(
			settings, dryLandHeightMeters, isFilteredOpenWater: false ).HeightMeters;

		var height01 = Math.Clamp( dryLandHeightMeters / maxHeight, 0f, 1f );

		var mountainZone = TerrainPreviewMountainFalloff.SampleSpawnBand01( settings, distMeters );
		var slopeDegrees = TerrainPreviewMountainSlope.SampleSlopeDegrees(
			settings, nx, ny, seed, maxHeight, diameter );

		return new TerrainPreviewSample

		{

			Height01 = height01,

			HeightMeters = dryLandHeightMeters,

			OceanHeight01 = 0f,

			ContinentalNoise01 = settings.EnableContinentalLayer ? baseLayers.Continent01 : 0f,

			HillsNoise01 = settings.EnableHillLayer ? baseLayers.Hills01 : 0f,

			ValleysNoise01 = settings.EnableValleyLayer ? baseLayers.Valleys01 : 0f,

			BaseHeightBeforeCurve01 = baseBeforeCurve,

			HeightAfterCurve01 = heightAfterCurve,

			HeightAfterBiomeShape01 = lowlandShaped01,

			TerrainDetail01 = terrainDetail01,

			BiomeTransition01 = ComputeBiomeTransition01( placementWeights ),

			MountainMask01 = mountainSpawnMask,

			MountainField01 = mountainField,

			MountainFalloff01 = mountainZone,

			MountainPeakHeight01 = mountain.PeakLift01,

			MountainFoothillLift01 = mountain.FoothillLift01,

			MountainSlopeDegrees = slopeDegrees,

			LakeMask01 = rawLakeMask,

			IsInsideWorld = true,

			IsOnLand = true,

			HasLandWeights = true,

			LandWeights = placementWeights,

		};

	}



	static TerrainPreviewSample BuildOpenWaterSample(

		TerrainPreviewSettings settings,

		float maxHeight,

		float rawLakeMask )

	{

		var seaHeight01 = Math.Clamp( settings.SeaLevelMeters / maxHeight, 0f, 1f );

		return new TerrainPreviewSample

		{

			Height01 = seaHeight01,

			HeightMeters = settings.SeaLevelMeters,

			OceanHeight01 = 1f,

			LakeMask01 = rawLakeMask,

			IsInsideWorld = true,

			IsOnLand = false,

		};

	}



	static TerrainPreviewSample SampleRimOcean( TerrainPreviewSettings settings )

		=> BuildOpenWaterSample( settings, Math.Max( 50f, settings.MaxTerrainHeightMeters ), 0f );



	static float ComputeBiomeTransition01( TerrainPreviewBiomeResolver.LandBiomeWeights weights )

	{

		var total = weights.Total;

		if ( total <= 0.0001f )

			return 0f;



		var maxW = Math.Max(

			Math.Max( weights.Clover, weights.Redwood ),

			Math.Max( weights.Amber, weights.Mountain ) );

		return Math.Clamp( 1f - (maxW / total), 0f, 1f );

	}

}
