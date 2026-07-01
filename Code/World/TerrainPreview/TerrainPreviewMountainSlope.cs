namespace Survival;

/// <summary>Estimates local peak steepness from ridged mountain noise — cheap finite difference.</summary>
static class TerrainPreviewMountainSlope
{
	public static float SampleSlopeDegrees(
		TerrainPreviewSettings settings,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		float worldDiameterMeters )
	{
		worldDiameterMeters = Math.Max( 500f, worldDiameterMeters );
		maxTerrainHeightMeters = Math.Max( 50f, maxTerrainHeightMeters );

		var stepMeters = Math.Clamp( settings.MountainSlopeSampleStepMeters, 24f, 256f );
		var step = stepMeters / worldDiameterMeters;
		var freq = Math.Clamp( settings.MountainFrequency, 0.25f, 32f );

		var center = SampleShape( seed, nx, ny, freq );
		var shapeXp = SampleShape( seed, nx + step, ny, freq );
		var shapeXm = SampleShape( seed, nx - step, ny, freq );
		var shapeYp = SampleShape( seed, nx, ny + step, freq );
		var shapeYm = SampleShape( seed, nx, ny - step, freq );

		var riseX = Math.Max( Math.Abs( shapeXp - center ), Math.Abs( shapeXm - center ) ) * maxTerrainHeightMeters;
		var riseY = Math.Max( Math.Abs( shapeYp - center ), Math.Abs( shapeYm - center ) ) * maxTerrainHeightMeters;
		var gradient = MathF.Sqrt( (riseX * riseX) + (riseY * riseY) ) / stepMeters;

		return MathF.Atan( gradient ) * (180f / MathF.PI);
	}

	static float SampleShape( int seed, float nx, float ny, float freq )
		=> TerrainPreviewNoise.RidgedFbm( seed + 300, nx * freq, ny * freq, 5 );
}
