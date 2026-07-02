namespace Survival;

/// <summary>
/// Converts sculpted height to world meters.
/// Lowlands: continental/hill base height drives macro rolling up to Lowland Height Max; biome sculpt adds on top.
/// Mountains blend to full peak ceiling.
/// </summary>
static class TerrainPreviewLandHeightDisplay
{
	public static float ApplyDryLandMeters(
		TerrainPreviewSettings settings,
		float baseHeight01,
		float sculptedHeight01,
		float mountainWeight01,
		float maxTerrainHeightMeters )
	{
		maxTerrainHeightMeters = Math.Max( 50f, maxTerrainHeightMeters );
		baseHeight01 = Math.Clamp( baseHeight01, 0f, 1f );
		sculptedHeight01 = Math.Clamp( sculptedHeight01, 0f, 1f );

		var floor = settings.SeaLevelMeters + Math.Max( 0.05f, settings.InlandDryLandSeaMarginMeters );
		var lowlandCap = Math.Clamp( settings.LandRollingHeightMaxMeters, 20f, maxTerrainHeightMeters );

		var macroMeters = floor + (baseHeight01 * lowlandCap);
		var sculptDeltaMeters = (sculptedHeight01 - baseHeight01) * lowlandCap;
		var lowlandMeters = Math.Clamp( macroMeters + sculptDeltaMeters, floor, floor + lowlandCap );

		var peakMeters = sculptedHeight01 * maxTerrainHeightMeters;
		var mountainBlend = SmoothStep01( Math.Clamp( mountainWeight01, 0f, 1f ) );
		return Lerp( lowlandMeters, peakMeters, mountainBlend );
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t);
}
