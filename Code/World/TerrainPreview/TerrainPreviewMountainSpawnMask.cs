namespace Survival;

/// <summary>
/// Mountain biome spawn mask — ridged range chains inside spawn band; falloff is eligibility only.
/// </summary>
static class TerrainPreviewMountainSpawnMask
{
	public static void AddSettingsFingerprint( HashCode hash, TerrainPreviewSettings settings )
	{
		hash.Add( settings.EnableMountainLayer );
		hash.Add( settings.BiomeMinMountainMask01 );
		hash.Add( settings.BiomeMountainPlacementStrength01 );
		hash.Add( settings.MountainInnerRadius01 );
		hash.Add( settings.MountainOuterRadius01 );
		hash.Add( settings.MountainBandFade01 );
		hash.Add( settings.MountainSpawnMacroOctaves );
		hash.Add( settings.MountainSpawnMediumOctaves );
		hash.Add( settings.MountainSpawnMacroWavelengthMeters );
		hash.Add( settings.MountainSpawnMediumWavelengthMeters );
		hash.Add( settings.MountainSpawnRidgeSharpness );
		hash.Add( settings.MountainSpawnFieldFloor01 );
		hash.Add( settings.MountainSpawnMediumFrequencyScale );
		hash.Add( settings.MountainSpawnMediumMix01 );
		hash.Add( settings.MountainSpawnBreakerFrequencyScale );
		hash.Add( settings.MountainSpawnBreakerMin01 );
		hash.Add( settings.MountainSpawnBreakerSpan01 );
		hash.Add( settings.MountainSpawnBreakerStrength01 );
		hash.Add( settings.MountainSpawnWarpStrength01 );
		hash.Add( settings.MountainSpawnRangeStretch01 );
		hash.Add( settings.MountainSpawnRangePower01 );
		hash.Add( settings.MountainSpawnSpeckFilterEnabled );
		hash.Add( settings.MountainSpawnMinPatchDiameterMeters );
		hash.Add( settings.MountainSpawnMinPatchSupport01 );
		hash.Add( settings.MountainSpawnMinPatchGridSteps );
	}

	public static float SampleMask01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
	{
		if ( !settings.EnableMountainLayer )
			return 0f;

		var placement = SamplePlacement01( settings, worldXMeters, worldYMeters );
		if ( placement <= 0.0001f )
			return 0f;

		var threshold = Math.Clamp( settings.BiomeMinMountainMask01, 0.05f, 0.95f );
		if ( placement < threshold )
			return 0f;

		if ( settings.MountainSpawnSpeckFilterEnabled
			&& !PassesMinPatchDisk( settings, worldXMeters, worldYMeters, threshold ) )
			return 0f;

		return 1f;
	}

	static bool PassesThreshold01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		float threshold )
	{
		var placement = SamplePlacement01( settings, worldXMeters, worldYMeters );
		return placement >= threshold;
	}

	static bool PassesMinPatchDisk(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		float threshold )
	{
		var patchDiameter = Math.Max( 40f, settings.MountainSpawnMinPatchDiameterMeters );
		var gridSteps = Math.Clamp( settings.MountainSpawnMinPatchGridSteps, 3, 7 );
		var step = patchDiameter / gridSteps;
		var pass = 0;
		var total = 0;
		var half = gridSteps * 0.5f;

		for ( var iy = 0; iy <= gridSteps; iy++ )
		{
			for ( var ix = 0; ix <= gridSteps; ix++ )
			{
				total++;
				var ox = worldXMeters + ((ix - half) * step);
				var oy = worldYMeters + ((iy - half) * step);
				if ( PassesThreshold01( settings, ox, oy, threshold ) )
					pass++;
			}
		}

		var required = Math.Clamp( settings.MountainSpawnMinPatchSupport01, 0.2f, 0.95f );
		return pass >= MathF.Ceiling( total * required );
	}

	public static float SamplePlacement01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
	{
		if ( !settings.EnableMountainLayer )
			return 0f;

		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		if ( radius <= 0f || diameter <= 0f )
			return 0f;

		var distMeters = MathF.Sqrt( worldXMeters * worldXMeters + worldYMeters * worldYMeters );
		if ( TerrainPreviewMountainFalloff.SampleSpawnBand01( settings, distMeters ) <= 0.0001f )
			return 0f;

		var nx = (worldXMeters + radius) / diameter;
		var ny = (worldYMeters + radius) / diameter;
		var field = SampleRangeField01( settings, nx, ny, diameter );
		return field * Math.Clamp( settings.BiomeMountainPlacementStrength01, 0f, 1f );
	}

	public static float SampleRangeField01( TerrainPreviewSettings settings, float nx, float ny, float worldDiameterMeters )
	{
		var seed = settings.WorldSeed;
		var warp = Math.Clamp( settings.MountainSpawnWarpStrength01, 0f, 0.65f );
		var warpX = TerrainPreviewNoise.Fbm( seed + 640, nx * 1.05f, ny * 1.05f, 3 );
		var warpY = TerrainPreviewNoise.Fbm( seed + 641, nx * 1.05f, ny * 1.05f, 3 );
		var wx = nx + ((warpX - 0.5f) * warp);
		var wy = ny + ((warpY - 0.5f) * warp);

		var angle = TerrainPreviewNoise.Fbm( seed + 642, nx * 0.9f, ny * 0.9f, 2 ) * MathF.PI;
		var cos = MathF.Cos( angle );
		var sin = MathF.Sin( angle );
		var stretch = Math.Clamp( settings.MountainSpawnRangeStretch01, 1f, 4f );
		var rx = (wx * cos) + (wy * sin);
		var ry = ((-wx * sin) + (wy * cos)) / stretch;

		var macroOctaves = Math.Clamp( settings.MountainSpawnMacroOctaves, 1, 6 );
		var mediumOctaves = Math.Clamp( settings.MountainSpawnMediumOctaves, 1, 5 );
		var macroFreq = ResolveMacroFrequency( settings, worldDiameterMeters );
		var macro = TerrainPreviewNoise.RidgedFbm( seed + 643, rx * macroFreq, ry * macroFreq, macroOctaves );

		var ridgeSharp = Math.Clamp( settings.MountainSpawnRidgeSharpness, 1f, 4f );
		var ridgePeak = MathF.Pow( Math.Clamp( macro, 0f, 1f ), ridgeSharp );

		var mediumFreq = ResolveMediumFrequency( settings, worldDiameterMeters, macroFreq );
		var medium = TerrainPreviewNoise.RidgedFbm( seed + 644, wx * mediumFreq, wy * mediumFreq, mediumOctaves );
		var medMix = Math.Clamp( settings.MountainSpawnMediumMix01, 0f, 1f );
		var ridgeBody = ridgePeak * Lerp( 0.78f, 0.58f + (medium * 0.48f), medMix );

		var floor = Math.Clamp( settings.MountainSpawnFieldFloor01, 0f, 0.45f );
		var shaped = Math.Max( 0f, ridgeBody - floor );

		var breakerScale = Math.Clamp( settings.MountainSpawnBreakerFrequencyScale, 1.5f, 8f );
		var breakerFreq = macroFreq * breakerScale;
		var breaker = TerrainPreviewNoise.RidgedFbm( seed + 645, wx * breakerFreq, wy * breakerFreq, 2 );
		var breakerMin = Math.Clamp( settings.MountainSpawnBreakerMin01, 0.2f, 0.85f );
		var breakerSpan = Math.Max( 0.04f, settings.MountainSpawnBreakerSpan01 );
		var breakerCut = SmoothStep01( (breaker - breakerMin) / breakerSpan );
		var breakerStrength = Math.Clamp( settings.MountainSpawnBreakerStrength01, 0f, 0.9f );
		shaped *= Lerp( 1f - breakerStrength, 1f, breakerCut );

		var power = Math.Clamp( settings.MountainSpawnRangePower01, 0.55f, 1.6f );
		return MathF.Pow( Math.Clamp( shaped, 0f, 1f ), power );
	}

	static float ResolveMacroFrequency( TerrainPreviewSettings settings, float worldDiameterMeters )
	{
		var wavelength = Math.Max( 350f, settings.MountainSpawnMacroWavelengthMeters );
		return worldDiameterMeters / wavelength;
	}

	static float ResolveMediumFrequency(
		TerrainPreviewSettings settings,
		float worldDiameterMeters,
		float macroFreq )
	{
		if ( settings.MountainSpawnMediumWavelengthMeters > 1f )
			return worldDiameterMeters / Math.Max( 120f, settings.MountainSpawnMediumWavelengthMeters );

		return macroFreq * Math.Clamp( settings.MountainSpawnMediumFrequencyScale, 1.2f, 8f );
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t );
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t );
}
