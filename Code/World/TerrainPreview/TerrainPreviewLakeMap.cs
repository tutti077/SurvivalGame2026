namespace Survival;

/// <summary>
/// Lake placement noise (0–1). High values = wet basins. Threshold + morphology happen in <see cref="TerrainPreviewLandDiskFields"/>.
/// </summary>
static class TerrainPreviewLakeMap
{
	public static float SampleMaskAtWorldMeters(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		int seed )
	{
		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		if ( radius <= 0f || diameter <= 0f )
			return 0f;

		worldXMeters -= settings.LakeOffsetXMeters;
		worldYMeters -= settings.LakeOffsetYMeters;

		var nx = (worldXMeters + radius) / diameter;
		var ny = (worldYMeters + radius) / diameter;
		return SampleMask01( settings, nx, ny, seed );
	}

	public static bool IsOpenWaterAtWorldMeters(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
		=> TerrainPreviewLandDiskFields.IsOpenWater( settings, worldXMeters, worldYMeters );

	public static float SampleMask01(
		TerrainPreviewSettings settings,
		float nx,
		float ny,
		int seed )
	{
		if ( !settings.EnableInteriorWaterLayer )
			return 0f;

		var macroFreq = TerrainPreviewLakeFrequency.ResolveMacroSampleFrequency( settings );
		var mediumFreq = TerrainPreviewLakeFrequency.ResolveMediumSampleFrequency( settings );
		var octaves = Math.Clamp( settings.LakeMacroOctaves, 1, 5 );
		var shoreDetail = Math.Clamp( settings.LakeShoreDetail01, 0f, 1f );

		var warpAmp = 0.08f + shoreDetail * 0.1f;
		var warpX = nx + (TerrainPreviewNoise.Fbm( seed + 880, nx * 1.6f, ny * 1.6f, 2 ) - 0.5f) * warpAmp;
		var warpY = ny + (TerrainPreviewNoise.Fbm( seed + 881, nx * 1.6f + 19f, ny * 1.6f + 37f, 2 ) - 0.5f) * warpAmp;

		var angle = (TerrainPreviewNoise.Fbm( seed + 882, nx * 0.35f, ny * 0.35f, 2 ) - 0.5f) * 0.65f;
		var stretch = 1f + TerrainPreviewNoise.Fbm( seed + 883, nx * 0.3f, ny * 0.3f, 2 ) * 0.75f;
		var cos = MathF.Cos( angle );
		var sin = MathF.Sin( angle );
		var rx = ((warpX - 0.5f) * cos) - ((warpY - 0.5f) * sin);
		var ry = ((warpX - 0.5f) * sin) + ((warpY - 0.5f) * cos);
		rx *= stretch;
		warpX = rx + 0.5f;
		warpY = ry + 0.5f;

		var macro = TerrainPreviewNoise.Fbm( seed + 884, warpX * macroFreq, warpY * macroFreq, octaves );
		var medium = TerrainPreviewNoise.Fbm( seed + 885, warpX * mediumFreq, warpY * mediumFreq, 3 );
		var basin = macro * Lerp( 0.5f, 1f, medium );

		basin = MathF.Pow( Math.Clamp( basin, 0f, 1f ), 0.82f );

		var ridge = TerrainPreviewNoise.RidgedFbm( seed + 886, warpX * macroFreq * 1.35f, warpY * macroFreq * 1.35f, 2 );
		var shoreMix = shoreDetail * 0.42f;
		var carved = basin * (1f - ridge * shoreMix * 0.55f);
		var lifted = basin + ridge * shoreMix * 0.12f;
		var shaped = Math.Clamp( Lerp( carved, lifted, shoreDetail * 0.35f ), 0f, 1f );

		return shaped;
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t );
}
