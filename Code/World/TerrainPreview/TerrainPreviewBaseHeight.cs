namespace Survival;

/// <summary>Continental + hills − valleys + optional height curve — input to biome sculpting only.</summary>
static class TerrainPreviewBaseHeight
{
	public static float SampleAfterCurve01(
		TerrainPreviewSettings settings,
		float nx,
		float ny,
		int seed,
		out float beforeCurve01 )
	{
		var continent = TerrainPreviewNoise.Fbm( seed, nx * settings.ContinentalFrequency, ny * settings.ContinentalFrequency, 5 );
		var hills = TerrainPreviewNoise.Fbm( seed + 100, nx * settings.HillFrequency, ny * settings.HillFrequency, 4 );
		var valleys = TerrainPreviewNoise.Fbm( seed + 200, nx * settings.ValleyFrequency, ny * settings.ValleyFrequency, 3 );

		var terrain =
			(settings.EnableContinentalLayer ? continent * settings.ContinentalWeight : 0f)
			+ (settings.EnableHillLayer ? hills * settings.HillWeight : 0f)
			- (settings.EnableValleyLayer ? valleys * settings.ValleyWeight : 0f);

		beforeCurve01 = Math.Clamp( terrain, 0f, 1f );
		return settings.EnableHeightCurveLayer
			? MathF.Pow( beforeCurve01, Math.Clamp( settings.HeightCurvePower, 0.25f, 4f ) )
			: beforeCurve01;
	}
}
