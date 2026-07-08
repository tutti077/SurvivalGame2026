namespace Survival;

/// <summary>
/// Mountain biome spawn mask — ridged range chains inside spawn band; falloff is eligibility only.
/// Peak placement: multi-peak chains along elongated ridges, small peak clusters on chunky blobs.
/// </summary>
static class TerrainPreviewMountainSpawnMask
{
	readonly struct RidgeFrame
	{
		public float Nx { get; init; }
		public float Ny { get; init; }
		public float Wx { get; init; }
		public float Wy { get; init; }
		public float Cos { get; init; }
		public float Sin { get; init; }
		public float Stretch { get; init; }
		public float AlongRidge01 { get; init; }
		public float CrossRidge01 { get; init; }
		public float RangeField01 { get; init; }
	}

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
		hash.Add( settings.MountainHeightInfluenceLow01 );
		hash.Add( settings.MountainHeightInfluenceHigh01 );
		hash.Add( settings.MountainPeakChainSpacingMeters );
		hash.Add( settings.MountainPeakClusterSpacingMeters );
		hash.Add( settings.MountainPeakShapeProbeMeters );
		hash.Add( settings.MountainPeakShapeBlendStart01 );
		hash.Add( settings.MountainPeakPlacementStrength01 );
	}

	/// <summary>
	/// Wide soft ramp from mountain field — height/peaks only, no binary mask threshold.
	/// Keeps foothills continuous with neighboring biomes.
	/// </summary>
	public static float SampleMountainHeightInfluence01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
	{
		if ( !settings.EnableMountainLayer )
			return 0f;

		var placement = SamplePlacement01( settings, worldXMeters, worldYMeters );
		if ( placement <= 0.0001f )
			return 0f;

		var rampLow = Math.Clamp( settings.MountainHeightInfluenceLow01, 0.02f, 0.45f );
		var rampHigh = Math.Clamp(
			settings.MountainHeightInfluenceHigh01,
			rampLow + 0.08f,
			0.98f );
		var t = SmoothStep01( (placement - rampLow) / Math.Max( 0.001f, rampHigh - rampLow ) );
		t = SmoothStep01( t );
		return t * Math.Clamp( settings.BiomeMountainPlacementStrength01, 0f, 1f );
	}

	/// <summary>
	/// Where summits land: ridge chains on streaky ranges, tight peak groups on chunky blobs.
	/// </summary>
	public static float SamplePeakPlacement01(
		TerrainPreviewSettings settings,
		float nx,
		float ny,
		float worldDiameterMeters )
	{
		worldDiameterMeters = Math.Max( 500f, worldDiameterMeters );
		var frame = BuildRidgeFrame( settings, nx, ny, worldDiameterMeters );
		if ( frame.RangeField01 < 0.06f )
			return 0f;

		var elongation = SampleRidgeElongation01( settings, frame, worldDiameterMeters );
		var chain = SampleRidgePeakChain01( settings, frame, worldDiameterMeters );
		var cluster = SampleChunkyPeakCluster01( settings, nx, ny, worldDiameterMeters, frame );

		var blendStart = Math.Clamp( settings.MountainPeakShapeBlendStart01, 0.2f, 0.7f );
		var ridgeWeight = SmoothStep01( (elongation - blendStart) / Math.Max( 0.05f, 1f - blendStart ) );
		var placement = Lerp( cluster, chain, ridgeWeight );
		var strength = Math.Clamp( settings.MountainPeakPlacementStrength01, 0.35f, 1f );
		return Math.Clamp( placement * strength, 0f, 1f );
	}

	static float SampleRidgeElongation01(
		TerrainPreviewSettings settings,
		RidgeFrame frame,
		float worldDiameterMeters )
	{
		var probeMeters = Math.Max( 80f, settings.MountainPeakShapeProbeMeters );
		var step = probeMeters / worldDiameterMeters;

		var gx = (
			SampleRangeField01( settings, frame.Nx + step, frame.Ny, worldDiameterMeters )
			- SampleRangeField01( settings, frame.Nx - step, frame.Ny, worldDiameterMeters ) )
			/ Math.Max( 0.0001f, step * 2f );
		var gy = (
			SampleRangeField01( settings, frame.Nx, frame.Ny + step, worldDiameterMeters )
			- SampleRangeField01( settings, frame.Nx, frame.Ny - step, worldDiameterMeters ) )
			/ Math.Max( 0.0001f, step * 2f );

		var gradAlong = Math.Abs( (gx * frame.Cos) + (gy * frame.Sin) );
		var gradCross = Math.Abs( (-gx * frame.Sin) + (gy * frame.Cos) );
		var gradientAniso = gradCross / (gradAlong + gradCross + 0.02f );

		var stretchBias = SmoothStep01( (frame.Stretch - 1f) / 3f );
		return Math.Clamp( (gradientAniso * 0.62f) + (stretchBias * 0.38f), 0f, 1f );
	}

	static float SampleRidgePeakChain01(
		TerrainPreviewSettings settings,
		RidgeFrame frame,
		float worldDiameterMeters )
	{
		var seed = settings.WorldSeed;
		var spacing = Math.Max( 120f, settings.MountainPeakChainSpacingMeters );
		var chainFreq = worldDiameterMeters / spacing;
		var along = frame.AlongRidge01 * chainFreq;
		var crossTight = Math.Clamp( settings.MountainPeakChainCrossTightness, 0.12f, 1.5f );
		var chain = TerrainPreviewNoise.RidgedFbm(
			seed + 700, along, frame.CrossRidge01 * crossTight, 4 );
		chain = MathF.Pow( Math.Clamp( chain, 0f, 1f ), 1.35f );

		var crossFalloff = Math.Clamp( settings.MountainPeakChainCrossFalloff, 0.8f, 3.5f );
		var onSpine = SmoothStep01( 1f - (Math.Abs( frame.CrossRidge01 ) * crossFalloff) );
		var gate = SmoothRange( frame.RangeField01, 0.22f, 0.58f );
		return chain * onSpine * gate;
	}

	static float SampleChunkyPeakCluster01(
		TerrainPreviewSettings settings,
		float nx,
		float ny,
		float worldDiameterMeters,
		RidgeFrame frame )
	{
		var seed = settings.WorldSeed;
		var spacing = Math.Max( 160f, settings.MountainPeakClusterSpacingMeters );
		var clusterFreq = worldDiameterMeters / spacing;

		var macro = TerrainPreviewNoise.RidgedFbm(
			seed + 720, nx * clusterFreq * 1.75f, ny * clusterFreq * 1.75f, 4 );
		var detail = TerrainPreviewNoise.RidgedFbm(
			seed + 721, nx * clusterFreq * 3.1f, ny * clusterFreq * 3.1f, 3 );
		var combined = (macro * 0.7f) + (detail * 0.3f );

		var rarity = MathF.Pow(
			Math.Clamp( combined, 0f, 1f ),
			Math.Max( 1.1f, settings.MountainPeakClusterRarityPower ) );

		var crest = SampleLocalFieldCrest01( settings, nx, ny, worldDiameterMeters, spacing * 0.35f );
		var gate = SmoothRange( frame.RangeField01, 0.26f, 0.64f );
		return rarity * gate * Lerp( 0.42f, 1f, crest );
	}

	static float SampleLocalFieldCrest01(
		TerrainPreviewSettings settings,
		float nx,
		float ny,
		float worldDiameterMeters,
		float blurMeters )
	{
		var field = SampleRangeField01( settings, nx, ny, worldDiameterMeters );
		var step = Math.Max( 0.0008f, blurMeters / worldDiameterMeters );
		var avg = (
			field
			+ SampleRangeField01( settings, nx + step, ny, worldDiameterMeters )
			+ SampleRangeField01( settings, nx - step, ny, worldDiameterMeters )
			+ SampleRangeField01( settings, nx, ny + step, worldDiameterMeters )
			+ SampleRangeField01( settings, nx, ny - step, worldDiameterMeters ) ) / 5f;

		return SmoothStep01( 0.5f + ((field - avg) * 3.2f) );
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
		=> BuildRidgeFrame( settings, nx, ny, worldDiameterMeters ).RangeField01;

	static RidgeFrame BuildRidgeFrame(
		TerrainPreviewSettings settings,
		float nx,
		float ny,
		float worldDiameterMeters )
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
		var rangeField = MathF.Pow( Math.Clamp( shaped, 0f, 1f ), power );

		return new RidgeFrame
		{
			Nx = nx,
			Ny = ny,
			Wx = wx,
			Wy = wy,
			Cos = cos,
			Sin = sin,
			Stretch = stretch,
			AlongRidge01 = rx,
			CrossRidge01 = ry,
			RangeField01 = rangeField,
		};
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

	static float SmoothRange( float value, float edge0, float edge1 )
	{
		if ( edge1 <= edge0 )
			return value >= edge0 ? 1f : 0f;

		var t = Math.Clamp( (value - edge0) / (edge1 - edge0), 0f, 1f );
		return t * t * (3f - (2f * t));
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t );
}
