namespace Survival;

/// <summary>Large-scale rolling dry land (e.g. 0–200 m) from macro Perlin; mountains keep sculpted peaks.</summary>
static class TerrainPreviewLandHeightDisplay
{
	public static float ApplyDryLandMeters(
		TerrainPreviewSettings settings,
		float nx,
		float ny,
		int seed,
		float sculptedHeight01,
		float mountainWeight01,
		float maxTerrainHeightMeters )
	{
		maxTerrainHeightMeters = Math.Max( 50f, maxTerrainHeightMeters );
		sculptedHeight01 = Math.Clamp( sculptedHeight01, 0f, 1f );

		if ( !settings.NormalizeLandRollingHeights )
			return sculptedHeight01 * maxTerrainHeightMeters;

		var rollingMeters = SampleRollingHillsMeters( settings, nx, ny, seed );
		var sculptedMeters = sculptedHeight01 * maxTerrainHeightMeters;
		var mountainBlend = SmoothStep01( Math.Clamp( mountainWeight01, 0f, 1f ) );
		return Lerp( rollingMeters, sculptedMeters, mountainBlend );
	}

	public static float SampleRollingHillsMeters(
		TerrainPreviewSettings settings,
		float nx,
		float ny,
		int seed )
	{
		var rollMax = Math.Clamp( settings.LandRollingHeightMaxMeters, 20f, settings.MaxTerrainHeightMeters );
		var floor = settings.SeaLevelMeters + Math.Max( 0.05f, settings.InlandDryLandSeaMarginMeters );

		var freq = Math.Max( 0.05f, settings.LandRollingMacroFrequency );
		var octaves = Math.Clamp( settings.LandRollingMacroOctaves, 1, 6 );
		var macro = TerrainPreviewNoise.Fbm( seed + 740, nx * freq, ny * freq, octaves );

		var detailFreq = freq * Math.Max( 1f, settings.LandRollingDetailFrequencyScale );
		var detailAmp = Math.Clamp( settings.LandRollingDetailAmplitude01, 0f, 0.3f );
		var detail = TerrainPreviewNoise.Fbm( seed + 741, nx * detailFreq, ny * detailFreq, 2 );
		var rolling01 = Math.Clamp( macro + ((detail - 0.5f) * detailAmp), 0f, 1f );

		return floor + (rolling01 * rollMax);
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t);
}
