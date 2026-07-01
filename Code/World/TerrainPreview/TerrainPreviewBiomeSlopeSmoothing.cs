namespace Survival;

/// <summary>Biome-aware slope softening — pulls high-frequency roughness down per biome tolerance.</summary>
static class TerrainPreviewBiomeSlopeSmoothing
{
	public static float Apply01(
		TerrainPreviewSettings settings,
		float height01,
		TerrainPreviewBiomeResolver.LandBiomeWeights weights,
		float nx,
		float ny,
		int seed,
		float terrainDetail01 )
	{
		var total = weights.Total;
		if ( total <= 0.0001f )
			return height01;

		var smoothStrength =
			(weights.Clover * Math.Clamp( settings.BiomeCloverSlopeSmooth01, 0f, 1f ))
			+ (weights.Redwood * Math.Clamp( settings.BiomeRedwoodSlopeSmooth01, 0f, 1f ))
			+ (weights.Amber * Math.Clamp( settings.BiomeAmberSlopeSmooth01, 0f, 1f ))
			+ (weights.Mountain * Math.Clamp( settings.BiomeMountainSlopeSmooth01, 0f, 1f ));

		smoothStrength /= total;
		if ( smoothStrength <= 0.0001f || terrainDetail01 <= 0.0001f )
			return height01;

		var lowFreq = TerrainPreviewNoise.Fbm( seed + 640, nx * 2.5f, ny * 2.5f, 3 );
		var smoothTarget = Lerp( height01, lowFreq, 0.35f );
		var detailGate = Math.Clamp( terrainDetail01 / Math.Max( 0.05f, settings.BiomeSlopeDetailGate01 ), 0f, 1f );
		var t = smoothStrength * detailGate;

		return Math.Clamp( Lerp( height01, smoothTarget, t ), 0f, 1f );
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t );
}
