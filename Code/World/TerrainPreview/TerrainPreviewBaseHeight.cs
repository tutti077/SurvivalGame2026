namespace Survival;

/// <summary>Continental + hills − valleys + optional height curve — input to biome sculpting only.</summary>
static class TerrainPreviewBaseHeight
{
	public readonly struct Layers
	{
		public float Continent01 { get; init; }
		public float Hills01 { get; init; }
		public float Valleys01 { get; init; }
		public float BeforeCurve01 { get; init; }
		public float AfterCurve01 { get; init; }
	}

	public static Layers Sample(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		int seed )
	{
		var diameter = Math.Max( 1f, settings.WorldDiameterMeters );
		var landRadius = settings.LandRadiusMeters;
		var nx = (worldXMeters + landRadius) / diameter;
		var ny = (worldYMeters + landRadius) / diameter;
		return Sample( settings, worldXMeters, worldYMeters, nx, ny, seed );
	}

	public static Layers Sample(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		float nx,
		float ny,
		int seed )
	{
		var continentalWavelength = Math.Max(
			500f,
			settings.WorldDiameterMeters / Math.Max( 0.25f, settings.ContinentalFrequency ) );
		var hillWavelength = ResolveHillWavelengthMeters( settings );
		var valleyWavelength = ResolveValleyWavelengthMeters( settings );

		var continent = settings.EnableContinentalLayer
			? TerrainPreviewNoise.Fbm(
				seed,
				worldXMeters / continentalWavelength,
				worldYMeters / continentalWavelength,
				5 )
			: 0f;

		var hills = settings.EnableHillLayer
			? TerrainPreviewNoise.Fbm(
				seed + 100,
				worldXMeters / hillWavelength,
				worldYMeters / hillWavelength,
				4 )
			: 0f;

		var valleys = settings.EnableValleyLayer
			? TerrainPreviewNoise.Fbm(
				seed + 200,
				worldXMeters / valleyWavelength,
				worldYMeters / valleyWavelength,
				3 )
			: 0f;

		var terrain =
			(settings.EnableContinentalLayer ? continent * settings.ContinentalWeight : 0f)
			+ (settings.EnableHillLayer ? hills * settings.HillWeight : 0f)
			- (settings.EnableValleyLayer ? valleys * settings.ValleyWeight : 0f);

		var beforeCurve01 = Math.Clamp( terrain, 0f, 1f );
		var afterCurve01 = settings.EnableHeightCurveLayer
			? MathF.Pow( beforeCurve01, Math.Clamp( settings.HeightCurvePower, 0.25f, 4f ) )
			: beforeCurve01;

		return new Layers
		{
			Continent01 = continent,
			Hills01 = hills,
			Valleys01 = valleys,
			BeforeCurve01 = beforeCurve01,
			AfterCurve01 = afterCurve01,
		};
	}

	public static float SampleAfterCurve01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		float nx,
		float ny,
		int seed,
		out float beforeCurve01 )
	{
		var layers = Sample( settings, worldXMeters, worldYMeters, nx, ny, seed );
		beforeCurve01 = layers.BeforeCurve01;
		return layers.AfterCurve01;
	}

	static float ResolveHillWavelengthMeters( TerrainPreviewSettings settings )
	{
		if ( settings.HillWavelengthMeters > 1f )
			return settings.HillWavelengthMeters;

		return 400f;
	}

	static float ResolveValleyWavelengthMeters( TerrainPreviewSettings settings )
	{
		if ( settings.ValleyWavelengthMeters > 1f )
			return settings.ValleyWavelengthMeters;

		return 550f;
	}
}
