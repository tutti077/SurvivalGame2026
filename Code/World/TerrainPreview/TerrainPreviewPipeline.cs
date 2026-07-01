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



		var continent = TerrainPreviewNoise.Fbm( seed, nx * settings.ContinentalFrequency, ny * settings.ContinentalFrequency, 5 );

		var hills = TerrainPreviewNoise.Fbm( seed + 100, nx * settings.HillFrequency, ny * settings.HillFrequency, 4 );

		var valleys = TerrainPreviewNoise.Fbm( seed + 200, nx * settings.ValleyFrequency, ny * settings.ValleyFrequency, 3 );

		var baseBeforeCurve = BuildBaseHeight01( settings, continent, hills, valleys, out var heightAfterCurve );



		var placementWeights = settings.UseContinuousBiomePlacementAtSample
			? TerrainPreviewBiomeResolver.SamplePlacementWeights(
				settings, worldXMeters, worldYMeters, heightAfterCurve )
			: TerrainPreviewLandDiskFields.GetFilteredPlacementWeights(
				settings, worldXMeters, worldYMeters );

		var mountainSpawnMask = placementWeights.Mountain;

		var mountainField = TerrainPreviewMountainSpawnMask.SamplePlacement01(

			settings, worldXMeters, worldYMeters );



		var shaped01 = TerrainPreviewBiomeTerrainShaper.ApplyBlendedShape01(

			settings,

			heightAfterCurve,

			placementWeights,

			nx,

			ny,

			seed,

			maxHeight,

			out var terrainDetail01 );



		var height01 = TerrainPreviewBiomeMountainPeaks.ApplyPeakLift01(

			settings,

			shaped01,

			placementWeights.Mountain,

			rawLakeMask,

			distMeters,

			nx,

			ny,

			seed,

			maxHeight,

			out var mountain );



		var mountainZone = TerrainPreviewMountainFalloff.SampleSpawnBand01( settings, distMeters );

		var slopeDegrees = TerrainPreviewMountainSlope.SampleSlopeDegrees(

			settings, nx, ny, seed, maxHeight, diameter );



		height01 = TerrainPreviewBiomeSlopeSmoothing.Apply01(

			settings, height01, placementWeights, nx, ny, seed, terrainDetail01 );



		var dryLandHeightMeters = TerrainPreviewLandHeightDisplay.ApplyDryLandMeters(
			settings,
			nx,
			ny,
			seed,
			height01,
			placementWeights.Mountain,
			maxHeight );

		dryLandHeightMeters = TerrainPreviewCoastalSmoothing.ApplyMeters(

			settings, dryLandHeightMeters, distMeters, nx, ny, seed );

		dryLandHeightMeters = TerrainPreviewLakeCombine.Apply(

			settings, dryLandHeightMeters, isFilteredOpenWater: false ).HeightMeters;



		height01 = Math.Clamp( dryLandHeightMeters / maxHeight, 0f, 1f );



		return new TerrainPreviewSample

		{

			Height01 = height01,

			OceanHeight01 = 0f,

			ContinentalNoise01 = settings.EnableContinentalLayer ? continent : 0f,

			HillsNoise01 = settings.EnableHillLayer ? hills : 0f,

			ValleysNoise01 = settings.EnableValleyLayer ? valleys : 0f,

			BaseHeightBeforeCurve01 = baseBeforeCurve,

			HeightAfterCurve01 = heightAfterCurve,

			HeightAfterBiomeShape01 = shaped01,

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



	static float BuildBaseHeight01(

		TerrainPreviewSettings settings,

		float continent,

		float hills,

		float valleys,

		out float heightAfterCurve )

	{

		var terrain =

			(settings.EnableContinentalLayer ? continent * settings.ContinentalWeight : 0f)

			+ (settings.EnableHillLayer ? hills * settings.HillWeight : 0f)

			- (settings.EnableValleyLayer ? valleys * settings.ValleyWeight : 0f);



		var baseBeforeCurve = Math.Clamp( terrain, 0f, 1f );

		heightAfterCurve = settings.EnableHeightCurveLayer

			? MathF.Pow( baseBeforeCurve, Math.Clamp( settings.HeightCurvePower, 0.25f, 4f ) )

			: baseBeforeCurve;



		return baseBeforeCurve;

	}

}

