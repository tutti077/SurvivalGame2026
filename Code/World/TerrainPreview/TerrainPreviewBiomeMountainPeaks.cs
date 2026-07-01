namespace Survival;

/// <summary>Mountain biome peak/ridge lift — only applied where placement weight is significant.</summary>
static class TerrainPreviewBiomeMountainPeaks
{
	public static float ApplyPeakLift01(
		TerrainPreviewSettings settings,
		float shapedHeight01,
		float mountainWeight01,
		float lakeMask01,
		float distMeters,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		out TerrainPreviewMountainHeight.Result mountain )
	{
		mountain = default;
		mountainWeight01 = Math.Clamp( mountainWeight01, 0f, 1f );
		if ( !settings.EnableMountainLayer )
			return shapedHeight01;

		var mountainZone = TerrainPreviewMountainFalloff.Sample01( settings, distMeters );
		if ( mountainZone <= 0.0001f && mountainWeight01 <= 0.0001f )
			return shapedHeight01;

		var mountainShape = TerrainPreviewNoise.RidgedFbm(
			seed + 300,
			nx * settings.MountainFrequency,
			ny * settings.MountainFrequency,
			5 );
		mountain = TerrainPreviewMountainHeight.Sample( settings, mountainShape, mountainZone, nx, ny, seed );

		var lakeClear = Math.Clamp(
			lakeMask01 / Math.Max( 0.04f, settings.InteriorLakeMountainClearCarve01 ),
			0f,
			1f );
		var liftWeight = Math.Clamp( mountainWeight01, 0f, 1f );
		var mountainScale = (1f - (lakeClear * lakeClear)) * liftWeight;
		var lifted = shapedHeight01 + (mountain.TotalLift01 * mountainScale);

		var capMeters = TerrainPreviewBiomeHeightCap.CapLimitMeters(
			settings, TerrainPreviewBiomeId.Mountain, maxTerrainHeightMeters );
		var meters = lifted * maxTerrainHeightMeters;
		meters = ApplySummitFlattenMeters( settings, meters, capMeters );

		return Math.Clamp( meters / Math.Max( 50f, maxTerrainHeightMeters ), 0f, 1f );
	}

	static float ApplySummitFlattenMeters( TerrainPreviewSettings settings, float heightMeters, float capMeters )
	{
		var knee = capMeters * Math.Clamp( settings.BiomeMountainSummitFlattenStart01, 0.75f, 0.98f );
		if ( heightMeters <= knee )
			return heightMeters;

		var headroom = Math.Max( 0.001f, capMeters - knee );
		var above = heightMeters - knee;
		var t = Math.Clamp( above / headroom, 0f, 2f );
		var flatten = Math.Clamp( settings.BiomeMountainSummitFlattenStrength01, 0.1f, 0.9f );
		var compressed = headroom * (1f - MathF.Pow( 1f - Math.Min( t, 1f ), 1f + flatten ));
		return knee + compressed + Math.Max( 0f, above - headroom ) * (1f - flatten) * 0.25f;
	}
}
