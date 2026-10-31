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

		// The domain warp scales with the basin, not the map: a fixed map-fraction warp (3–7 km on 40 km) with a
		// global stretch tore 2 km basins into diagonal streaks once Macro Frequency went up for "water everywhere"
		// (Mark 2026-10-29). At the old 10 km basins this is the same ~0.1 map-fraction warp as before.
		var basinWavelengthN = 1f / Math.Max( 0.5f, macroFreq );
		var warpAmp = basinWavelengthN * (0.35f + (shoreDetail * 0.45f));
		var warpFreq = macroFreq * 0.7f;
		var warpX = nx + (TerrainPreviewNoise.Fbm( seed + 880, nx * warpFreq, ny * warpFreq, 2 ) - 0.5f) * warpAmp;
		var warpY = ny + (TerrainPreviewNoise.Fbm( seed + 881, (nx * warpFreq) + 19f, (ny * warpFreq) + 37f, 2 ) - 0.5f) * warpAmp;

		var macro = TerrainPreviewNoise.Fbm( seed + 884, warpX * macroFreq, warpY * macroFreq, octaves );

		// Oceans outward (Mark 2026-10-30: "water blobs are bigger the further out you go ... massive oceans
		// instead of just lakes everywhere"). Past Ocean Zone Start the basin noise cross-fades to a field
		// Ocean Basin Size times bigger and is lifted by Ocean Lift, so the outer land holds a few huge
		// basins that run into the rim ring (and so count as ocean for the Azure Coast) while the inner
		// map keeps its ponds. The fade is radial but the basins are not, so there is no ring to see.
		var oceanMul = Math.Max( 1f, settings.LakeOceanBasinSizeMul );
		var oceanLift = Math.Clamp( settings.LakeOceanLift01, 0f, 1f );
		var zoneStart = Math.Clamp( settings.LakeOceanZoneStart01, 0f, 1f );
		var zoneEnd = Math.Max( zoneStart + 0.05f, settings.LakeOceanZoneEnd01 );
		var r01 = MathF.Sqrt( ((nx - 0.5f) * (nx - 0.5f)) + ((ny - 0.5f) * (ny - 0.5f)) ) * 2f;
		var oceanT = SmoothStep01( (r01 - zoneStart) / (zoneEnd - zoneStart) );
		if ( oceanT > 0f && (oceanMul > 1.001f || oceanLift > 0f) )
		{
			var oceanFreq = macroFreq / oceanMul;
			var oceanWarpAmp = (1f / oceanFreq) * 0.3f;
			var oceanWarpFreq = oceanFreq * 0.8f;
			var owx = nx + (TerrainPreviewNoise.Fbm( seed + 882, nx * oceanWarpFreq, ny * oceanWarpFreq, 2 ) - 0.5f) * oceanWarpAmp;
			var owy = ny + (TerrainPreviewNoise.Fbm( seed + 883, (nx * oceanWarpFreq) + 7f, (ny * oceanWarpFreq) + 11f, 2 ) - 0.5f) * oceanWarpAmp;
			var ocean = TerrainPreviewNoise.Fbm( seed + 887, owx * oceanFreq, owy * oceanFreq, 2 );
			macro = Lerp( macro, Math.Min( 1f, ocean + oceanLift ), oceanT );
		}

		var medium = TerrainPreviewNoise.Fbm( seed + 885, warpX * mediumFreq, warpY * mediumFreq, 3 );
		// Medium noise only dents the basin (0.7–1): at 0.5–1 it split small lakes into ragged pieces.
		var basin = macro * Lerp( 0.7f, 1f, medium );

		basin = MathF.Pow( Math.Clamp( basin, 0f, 1f ), 0.82f );

		var ridge = TerrainPreviewNoise.RidgedFbm( seed + 886, warpX * macroFreq * 1.35f, warpY * macroFreq * 1.35f, 2 );
		var shoreMix = shoreDetail * 0.42f;
		var carved = basin * (1f - ridge * shoreMix * 0.55f);
		var lifted = basin + ridge * shoreMix * 0.12f;
		var shaped = Math.Clamp( Lerp( carved, lifted, shoreDetail * 0.35f ), 0f, 1f );

		return shaped;
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t );

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}
}
