namespace Survival;

/// <summary>
/// Converts sculpted height to world meters — one smooth lerp between lowland and mountain elevation.
/// </summary>
static class TerrainPreviewLandHeightDisplay
{
	public static float ApplyDryLandMeters(
		TerrainPreviewSettings settings,
		float baseHeight01,
		float lowlandShaped01,
		float mountainBoost01,
		float mountainInfluence01,
		float maxTerrainHeightMeters )
	{
		maxTerrainHeightMeters = Math.Max( 50f, maxTerrainHeightMeters );
		baseHeight01 = Math.Clamp( baseHeight01, 0f, 1f );
		lowlandShaped01 = Math.Clamp( lowlandShaped01, 0f, 1f );
		mountainBoost01 = Math.Clamp( mountainBoost01, 0f, 1f );
		mountainInfluence01 = Math.Clamp( mountainInfluence01, 0f, 1f );

		var floor = settings.SeaLevelMeters + Math.Max( 0.05f, settings.InlandDryLandSeaMarginMeters );
		var lowlandCap = Math.Clamp( settings.LandRollingHeightMaxMeters, 20f, maxTerrainHeightMeters );
		var headroom = Math.Max( 0f, maxTerrainHeightMeters - lowlandCap );

		var sculptDelta01 = lowlandShaped01 - baseHeight01;
		var lowlandMeters = floor + (baseHeight01 * lowlandCap) + (sculptDelta01 * lowlandCap);
		lowlandMeters = Math.Clamp( lowlandMeters, floor, floor + lowlandCap );

		var mountainMeters = Math.Min( lowlandMeters + (mountainBoost01 * headroom), maxTerrainHeightMeters );
		var blendT = SmoothStep01( mountainInfluence01 );
		return Lerp( lowlandMeters, mountainMeters, blendT );
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t);
}
