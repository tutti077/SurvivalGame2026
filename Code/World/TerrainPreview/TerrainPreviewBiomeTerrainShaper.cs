namespace Survival;

/// <summary>
/// Per-biome terrain personality — each biome sculpts base height rather than only capping at the end.
/// </summary>
static class TerrainPreviewBiomeTerrainShaper
{
	public static float ApplyBlendedShape01(
		TerrainPreviewSettings settings,
		float baseHeight01,
		TerrainPreviewBiomeResolver.LandBiomeWeights weights,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		out float blendedDetail01 )
	{
		blendedDetail01 = 0f;
		var total = weights.Total;
		if ( total <= 0.0001f )
			return baseHeight01;

		var sum = 0f;
		var detailSum = 0f;

		if ( weights.Clover > 0.0001f )
		{
			var shaped = ShapeCloverHills( settings, baseHeight01, nx, ny, seed, maxTerrainHeightMeters, out var detail );
			sum += weights.Clover * shaped;
			detailSum += weights.Clover * detail;
		}

		if ( weights.Redwood > 0.0001f )
		{
			var shaped = ShapeRedwoodForest( settings, baseHeight01, nx, ny, seed, maxTerrainHeightMeters, out var detail );
			sum += weights.Redwood * shaped;
			detailSum += weights.Redwood * detail;
		}

		if ( weights.Amber > 0.0001f )
		{
			var shaped = ShapeAmberDunes( settings, baseHeight01, nx, ny, seed, maxTerrainHeightMeters, out var detail );
			sum += weights.Amber * shaped;
			detailSum += weights.Amber * detail;
		}

		if ( weights.Mountain > 0.0001f )
		{
			var shaped = ShapeMountainBase( settings, baseHeight01, nx, ny, seed, maxTerrainHeightMeters, out var detail );
			sum += weights.Mountain * shaped;
			detailSum += weights.Mountain * detail;
		}

		blendedDetail01 = detailSum / total;
		return Math.Clamp( sum / total, 0f, 1f );
	}

	static float ShapeCloverHills(
		TerrainPreviewSettings settings,
		float baseHeight01,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		out float detail01 )
	{
		var rollFreq = Math.Clamp( settings.BiomeCloverRollFrequency, 1f, 12f );
		var roll = TerrainPreviewNoise.Fbm( seed + 600, nx * rollFreq, ny * rollFreq, 3 );
		var gentle = TerrainPreviewNoise.Fbm( seed + 601, nx * rollFreq * 0.45f, ny * rollFreq * 0.45f, 2 );

		var rollAmp = Math.Clamp( settings.BiomeCloverRollAmplitude01, 0.02f, 0.18f );
		var shaped = baseHeight01 + ((roll - 0.5f) * rollAmp) + ((gentle - 0.5f) * rollAmp * 0.35f);
		shaped = Lerp( baseHeight01, shaped, Math.Clamp( settings.BiomeCloverShapeBlend01, 0.25f, 1f ) );

		detail01 = Math.Abs( roll - gentle ) * 0.5f;
		return TerrainPreviewBiomeHeightCap.SoftCapForBiome(
			settings, shaped * maxTerrainHeightMeters, TerrainPreviewBiomeId.CloverHills, maxTerrainHeightMeters )
			/ Math.Max( 50f, maxTerrainHeightMeters );
	}

	static float ShapeRedwoodForest(
		TerrainPreviewSettings settings,
		float baseHeight01,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		out float detail01 )
	{
		var hillFreq = Math.Clamp( settings.BiomeRedwoodHillFrequency, 1.5f, 14f );
		var hills = TerrainPreviewNoise.Fbm( seed + 610, nx * hillFreq, ny * hillFreq, 4 );
		var ridge = TerrainPreviewNoise.RidgedFbm( seed + 611, nx * hillFreq * 0.65f, ny * hillFreq * 0.65f, 3 );

		var hillAmp = Math.Clamp( settings.BiomeRedwoodHillAmplitude01, 0.03f, 0.22f );
		var ridgeAmp = Math.Clamp( settings.BiomeRedwoodRidgeAmplitude01, 0.01f, 0.12f );
		var shaped = baseHeight01 + ((hills - 0.5f) * hillAmp) + (ridge * ridgeAmp);

		detail01 = ridge * 0.65f + Math.Abs( hills - 0.5f );
		return TerrainPreviewBiomeHeightCap.SoftCapForBiome(
			settings, shaped * maxTerrainHeightMeters, TerrainPreviewBiomeId.RedwoodForest, maxTerrainHeightMeters )
			/ Math.Max( 50f, maxTerrainHeightMeters );
	}

	static float ShapeAmberDunes(
		TerrainPreviewSettings settings,
		float baseHeight01,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		out float detail01 )
	{
		var duneFreq = Math.Clamp( settings.BiomeAmberDuneFrequency, 0.75f, 8f );
		var warpX = nx + (TerrainPreviewNoise.Fbm( seed + 620, nx * 2f, ny * 2f, 2 ) - 0.5f) * 0.08f;
		var dune = TerrainPreviewNoise.Fbm( seed + 621, warpX * duneFreq, ny * duneFreq * 0.55f, 4 );
		var flow = TerrainPreviewNoise.Fbm( seed + 622, warpX * duneFreq * 0.35f, ny * duneFreq * 0.35f, 2 );

		var duneFloor = Math.Clamp( settings.BiomeAmberDuneFloor01, 0.05f, 0.45f );
		var duneAmp = Math.Clamp( settings.BiomeAmberDuneAmplitude01, 0.08f, 0.35f );
		var duneHeight = duneFloor + (dune * duneAmp);
		var blend = Math.Clamp( settings.BiomeAmberDuneReshapeBlend01, 0.35f, 0.95f );
		var shaped = Lerp( baseHeight01, duneHeight, blend ) + ((flow - 0.5f) * duneAmp * 0.12f);

		detail01 = Math.Abs( dune - flow ) * 0.35f;
		return TerrainPreviewBiomeHeightCap.SoftCapForBiome(
			settings, shaped * maxTerrainHeightMeters, TerrainPreviewBiomeId.AmberDunes, maxTerrainHeightMeters )
			/ Math.Max( 50f, maxTerrainHeightMeters );
	}

	/// <summary>Pre-peak mountain biome base — ruggedness comes from mountain lift pass.</summary>
	static float ShapeMountainBase(
		TerrainPreviewSettings settings,
		float baseHeight01,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		out float detail01 )
	{
		var rugged = TerrainPreviewNoise.RidgedFbm( seed + 630, nx * 5f, ny * 5f, 3 );
		var lift = rugged * Math.Clamp( settings.BiomeMountainBaseRuggedAmplitude01, 0.02f, 0.14f );
		var shaped = baseHeight01 + lift;

		detail01 = rugged;
		return TerrainPreviewBiomeHeightCap.SoftCapForBiome(
			settings, shaped * maxTerrainHeightMeters, TerrainPreviewBiomeId.Mountain, maxTerrainHeightMeters )
			/ Math.Max( 50f, maxTerrainHeightMeters );
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t );
}
