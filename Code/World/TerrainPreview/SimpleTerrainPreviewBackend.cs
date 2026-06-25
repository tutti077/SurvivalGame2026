namespace Survival;

/// <summary>Continental / hill / valley / mountain height sampler for the editor preview tool.</summary>
public sealed class SimpleTerrainPreviewBackend : ITerrainPreviewBackend
{
	public TerrainPreviewSample Sample( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		var radius = settings.WorldRadiusMeters;
		if ( radius <= 0f )
			return default;

		var distMeters = MathF.Sqrt( worldXMeters * worldXMeters + worldYMeters * worldYMeters );
		if ( distMeters / radius > 1f )
			return default;

		var diameter = settings.WorldDiameterMeters;
		var nx = (worldXMeters + radius) / diameter;
		var ny = (worldYMeters + radius) / diameter;
		var seed = settings.WorldSeed;

		var continent = TerrainPreviewNoise.Fbm( seed, nx * settings.ContinentalFrequency, ny * settings.ContinentalFrequency, 5 );
		var hills = TerrainPreviewNoise.Fbm( seed + 100, nx * settings.HillFrequency, ny * settings.HillFrequency, 4 );
		var valleys = TerrainPreviewNoise.Fbm( seed + 200, nx * settings.ValleyFrequency, ny * settings.ValleyFrequency, 3 );
		var interiorCarve = TerrainPreviewInteriorWater.SampleCarve01( settings, worldXMeters, worldYMeters, seed );

		var baseBeforeCurve = BuildBaseHeight01( settings, continent, hills, valleys, interiorCarve, out var heightAfterCurve );

		var mountainShape = TerrainPreviewNoise.RidgedFbm(
			seed + 300,
			nx * settings.MountainFrequency,
			ny * settings.MountainFrequency,
			5 );

		var mountainZone = TerrainPreviewMountainFalloff.Sample01( settings, distMeters );
		var mountain = TerrainPreviewMountainHeight.Sample( settings, mountainShape, mountainZone, nx, ny, seed );

		var height01 = heightAfterCurve;
		if ( settings.EnableMountainLayer )
			height01 = Math.Clamp( height01 + mountain.TotalLift01, 0f, 1f );

		height01 = TerrainPreviewOceanByHeight.ApplySeaLevelClamp( settings, height01 );
		var oceanHeight = TerrainPreviewOceanByHeight.SampleOcean01( settings, height01 );

		return new TerrainPreviewSample
		{
			Height01 = height01,
			OceanHeight01 = oceanHeight,
			ContinentalNoise01 = settings.EnableContinentalLayer ? continent : 0f,
			HillsNoise01 = settings.EnableHillLayer ? hills : 0f,
			ValleysNoise01 = settings.EnableValleyLayer ? valleys : 0f,
			BaseHeightBeforeCurve01 = baseBeforeCurve,
			HeightAfterCurve01 = heightAfterCurve,
			MountainMask01 = mountain.CombinedInfluence01,
			MountainFalloff01 = mountainZone,
			MountainPeakHeight01 = mountain.PeakLift01,
			MountainFoothillLift01 = mountain.FoothillLift01,
			IsInsideWorld = true,
		};
	}

	static float BuildBaseHeight01(
		TerrainPreviewSettings settings,
		float continent,
		float hills,
		float valleys,
		float interiorCarve,
		out float heightAfterCurve )
	{
		var terrain =
			(settings.EnableContinentalLayer ? continent * settings.ContinentalWeight : 0f)
			+ (settings.EnableHillLayer ? hills * settings.HillWeight : 0f)
			- (settings.EnableValleyLayer ? valleys * settings.ValleyWeight : 0f )
			- interiorCarve;

		var baseBeforeCurve = Math.Clamp( terrain, 0f, 1f );
		heightAfterCurve = settings.EnableHeightCurveLayer
			? MathF.Pow( baseBeforeCurve, Math.Clamp( settings.HeightCurvePower, 0.25f, 4f ) )
			: baseBeforeCurve;

		return baseBeforeCurve;
	}
}
