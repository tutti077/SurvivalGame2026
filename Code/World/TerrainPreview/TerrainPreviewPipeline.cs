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

		=> Sample( settings, worldXMeters, worldYMeters, applyRivers: true );

	/// <summary>Dry-land height before the river carve — what the river router walks downhill on.</summary>
	public static float SampleDryLandHeightMetersNoRivers( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		var sample = Sample( settings, worldXMeters, worldYMeters, applyRivers: false );
		return sample.IsInsideWorld ? sample.HeightMeters : settings.SeaLevelMeters;
	}

	static TerrainPreviewSample Sample(

		TerrainPreviewSettings settings,

		float worldXMeters,

		float worldYMeters,

		bool applyRivers )

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



		// One cache verification per sample (the shore / coastal / water lookups below then skip theirs),
		// and it guarantees the lake grid below is built rather than falling back to per-sample lake noise.
		TerrainPreviewLandDiskFields.BeginSample( settings );

		var rawLakeMask = TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld(
			settings, worldXMeters, worldYMeters, out var cachedLakeMask )
			? cachedLakeMask
			: TerrainPreviewLakeMap.SampleMaskAtWorldMeters(
				settings, worldXMeters, worldYMeters, seed );

		var baseLayers = TerrainPreviewBaseHeight.Sample(
			settings, worldXMeters, worldYMeters, nx, ny, seed );
		var heightAfterCurve = baseLayers.AfterCurve01;



		var placementWeights = settings.UseContinuousBiomePlacementAtSample
			? TerrainPreviewBiomeResolver.SamplePlacementWeights(
				settings, worldXMeters, worldYMeters, heightAfterCurve )
			: TerrainPreviewLandDiskFields.GetFilteredPlacementWeights(
				settings, worldXMeters, worldYMeters );

		// Mask / field / falloff / slope previews are editor PNG modes; TerrainPreviewGenerator computes them
		// directly. The runtime path only needs the height influence (the mask alone was ~60 % of a mountain sample).
		var mountainHeightInfluence = TerrainPreviewMountainSpawnMask.SampleMountainHeightInfluence01(
			settings, worldXMeters, worldYMeters );

		// Height shaping keeps the raw continuous weights: the speck merge below only decides what a point
		// LOOKS like (colour, trees). Shaping with merged one-hot weights raised whole raster cells into
		// flat-topped blocks with vertical walls where a clover island became redwood (Mark 2026-10-07).
		var lowlandWeights = TerrainPreviewBiomeResolver.LandWeightsWithoutMountain( placementWeights );
		var displayWeights = settings.UseContinuousBiomePlacementAtSample
			? TerrainPreviewBiomeResolver.ApplySpeckMerge( settings, worldXMeters, worldYMeters, placementWeights )
			: placementWeights;
		var lowlandShaped01 = TerrainPreviewBiomeTerrainShaper.ApplyBlendedShape01(
			settings,
			heightAfterCurve,
			lowlandWeights,
			nx,
			ny,
			seed,
			maxHeight,
			out var ledgeInfo );

		var mountainBoost01 = TerrainPreviewBiomeMountainPeaks.SampleBoost01(
			settings,
			mountainHeightInfluence,
			rawLakeMask,
			distMeters,
			nx,
			ny,
			seed,
			out _ );

		var dryLandHeightMeters = TerrainPreviewLandHeightDisplay.ApplyDryLandMeters(
			settings,
			heightAfterCurve,
			lowlandShaped01,
			mountainBoost01,
			mountainHeightInfluence,
			maxHeight );

		dryLandHeightMeters = TerrainPreviewCoastalSmoothing.ApplyMeters(
			settings, dryLandHeightMeters, worldXMeters, worldYMeters, distMeters, nx, ny, seed );

		dryLandHeightMeters = TerrainPreviewLakeShoreHeight.ApplyDryLandNearLake(
			settings, dryLandHeightMeters, rawLakeMask );

		dryLandHeightMeters = TerrainPreviewLakeCombine.Apply(
			settings, dryLandHeightMeters, isFilteredOpenWater: false ).HeightMeters;

		var submerged = TerrainPreviewLakeShoreHeight.IsSubmergedByLakeMask( settings, rawLakeMask );

		// Rivers (after lakes, before the sea clamp below): inside the channel the ground IS the sloped water
		// surface — flat across, falling along — and the bank blends from just above the water up to the land.
		var riverWater = false;
		var riverBank = false;   // the slide wall: ground, but no trees / scatter standing in the water's edge
		if ( applyRivers && !submerged && settings.EnableRivers )
		{
			var river = TerrainPreviewLandDiskFields.SampleRiver( settings, worldXMeters, worldYMeters );
			if ( river.Near )
			{
				var level = Math.Max( settings.SeaLevelMeters, river.Level );
				var slide = Math.Clamp( settings.RiverBankSlideWidthMeters, 0.5f, 30f );
				var rim = level + Math.Clamp( settings.RiverBankHeightMeters, 0f, 10f );
				if ( river.Distance <= river.HalfWidth )
				{
					// The bed: a parabola from the water's edge down to Bed Depth under the surface at the centre
					// (Mark 2026-10-07: "actually carve out the land, slope inward, not a hard drop"). The water
					// mesh fills it to the level. The depth is capped per biome (Mark 2026-10-21: Clover Hills at
					// most 4 m, other biomes gentle for now) on the continuous Clover weight, so the cap never
					// steps at a biome border.
					var total = placementWeights.Total;
					var cloverShare = total > 1e-4f ? Math.Clamp( placementWeights.Clover / total, 0f, 1f ) : 1f;
					var bedCap = settings.RiverBedMaxDepthOtherMeters + ((settings.RiverBedMaxDepthCloverMeters - settings.RiverBedMaxDepthOtherMeters) * cloverShare);
					var bedDepth = Math.Clamp( river.BedDepthMeters, 0f, Math.Max( 0f, bedCap ) );
					var u = river.Distance / Math.Max( 0.5f, river.HalfWidth );
					var bed = level - (bedDepth * (1f - (u * u)));   // deeper in pools
					dryLandHeightMeters = Math.Min( dryLandHeightMeters, bed );
					riverWater = true;
				}
				else if ( river.Distance <= river.HalfWidth + slide )
				{
					// The slide (Mark 2026-10-21: "a water-slide shape I can fill with water" — easing the land
					// straight down to the water's edge gave a 50 m dish with no channel): a curved wall from the
					// water's edge up to the rim, gentle at the water and steepest at the rim. Cut only.
					var t = (river.Distance - river.HalfWidth) / slide;
					var wall = level + ((rim - level) * t * t);
					dryLandHeightMeters = Math.Min( dryLandHeightMeters, wall );
					riverBank = true;
				}
				else
				{
					// Cut only (never lift the ground into a levee): the bank is the ground eased down to the
					// rim over the bank width; land already lower than that stays as it is.
					var t = Math.Clamp( (river.Distance - river.HalfWidth - slide) / Math.Max( 0.5f, river.BankWidth ), 0f, 1f );
					t = t * t * (3f - (2f * t));
					var eased = rim + ((dryLandHeightMeters - rim) * t);
					dryLandHeightMeters = Math.Min( dryLandHeightMeters, eased );
				}
			}
		}
		var height01 = Math.Clamp( dryLandHeightMeters / maxHeight, 0f, 1f );
		var sea = settings.SeaLevelMeters;

		return new TerrainPreviewSample

		{

			Height01 = height01,

			HeightMeters = dryLandHeightMeters,

			OceanHeight01 = submerged ? 1f : 0f,

			ContinentalNoise01 = settings.EnableContinentalLayer ? baseLayers.Continent01 : 0f,

			HillsNoise01 = settings.EnableHillLayer ? baseLayers.Hills01 : 0f,

			ValleysNoise01 = settings.EnableValleyLayer ? baseLayers.Valleys01 : 0f,

			HeightAfterCurve01 = heightAfterCurve,

			HeightAfterBiomeShape01 = lowlandShaped01,

			Ledge = submerged ? TerrainBiomeLedgeInfo.None : ledgeInfo,

			BiomeTransition01 = ComputeBiomeTransition01( displayWeights ),

			LakeMask01 = rawLakeMask,

			IsInsideWorld = true,

			IsOnLand = !submerged && !riverWater && !riverBank && dryLandHeightMeters > sea + 0.05f,

			RiverWater = riverWater,

			HasLandWeights = true,

			LandWeights = displayWeights,

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
