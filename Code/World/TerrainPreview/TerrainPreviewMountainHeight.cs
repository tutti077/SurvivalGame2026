namespace Survival;

/// <summary>Peak-focused mountain lift — sharp summits, minimal plateau foothills.</summary>
static class TerrainPreviewMountainHeight
{
	public readonly struct Result
	{
		public float PeakLift01 { get; init; }
		public float FoothillLift01 { get; init; }
		public float CombinedInfluence01 { get; init; }
		public float RegionPeakCap01 { get; init; }

		public float TotalLift01 => Math.Clamp( PeakLift01 + FoothillLift01, 0f, 1f );
	}

	public static Result Sample(
		TerrainPreviewSettings settings,
		float rangeField01,
		float peakPlacement01,
		float mountainZone01,
		float nx,
		float ny,
		int seed )
	{
		if ( mountainZone01 <= 0f )
			return default;

		rangeField01 = Math.Clamp( rangeField01, 0f, 1f );
		peakPlacement01 = Math.Clamp( peakPlacement01, 0f, 1f );
		var mountainShape01 = rangeField01;

		var threshold = Math.Clamp( settings.MountainThreshold, 0f, 0.98f );
		var regionCap = SampleRegionPeakCap( settings, nx, ny, seed );
		var peakIntensity = SampleLocalPeakIntensity( mountainShape01, threshold );
		peakIntensity *= Lerp( 0.22f, 1f, peakPlacement01 );
		var spike = MathF.Pow( peakIntensity, Math.Max( 1f, settings.MountainPeakSharpnessPower ) );

		var peakBand = Math.Max( 0.05f, settings.MountainPeakBandWidth01 * 0.55f );
		var peakPresence = SmoothRange( mountainShape01, threshold, threshold + peakBand );
		var peakLift = peakPresence * spike * regionCap * settings.MountainPeakBoost * peakPlacement01;

		var summitMacro = TerrainPreviewNoise.RidgedFbm(
			seed + 901,
			nx * settings.MountainSummitMacroFrequency,
			ny * settings.MountainSummitMacroFrequency,
			3 );
		if ( summitMacro >= settings.MountainSummitMacroThreshold01 && spike > 0.08f )
		{
			var summitT = SmoothRange( summitMacro, settings.MountainSummitMacroThreshold01, 1f );
			peakLift += peakPresence * spike * summitT * settings.MountainSummitExtraLift01 * regionCap;
		}

		var foothillSpread = Math.Min( settings.MountainFoothillSpread, 0.18f );
		var foothillStart = Math.Max( 0f, threshold - foothillSpread );
		var foothillPresence = SmoothRange( mountainShape01, foothillStart, threshold ) * (1f - spike);
		var foothillLift = foothillPresence * regionCap * settings.MountainFoothillBoost * 0.3f;

		var zone = Math.Clamp( mountainZone01, 0f, 1f );
		peakLift *= zone;
		foothillLift *= zone;
		var combined = Math.Clamp( peakLift + foothillLift, 0f, 1f );

		return new Result
		{
			PeakLift01 = peakLift,
			FoothillLift01 = foothillLift,
			CombinedInfluence01 = combined,
			RegionPeakCap01 = regionCap,
		};
	}

	static float SampleRegionPeakCap( TerrainPreviewSettings settings, float nx, float ny, int seed )
	{
		var variation = TerrainPreviewNoise.Fbm(
			seed + 400,
			nx * settings.MountainPeakVariationFrequency,
			ny * settings.MountainPeakVariationFrequency,
			3 );

		var minPeak = Math.Clamp( settings.MountainMinPeakHeight01, 0f, 1f );
		var t = MathF.Pow( Math.Clamp( variation, 0f, 1f ), Math.Max( 1f, settings.MountainPeakRarityPower ) );
		return Lerp( minPeak, 1f, t );
	}

	static float SampleLocalPeakIntensity( float shape, float threshold )
	{
		if ( shape <= threshold )
			return 0f;

		var span = Math.Max( 0.001f, 1f - threshold );
		var t = Math.Clamp( (shape - threshold) / span, 0f, 1f );
		t = t * t * (3f - 2f * t );
		return t;
	}

	static float SmoothRange( float value, float edge0, float edge1 )
	{
		if ( edge1 <= edge0 )
			return value >= edge0 ? 1f : 0f;

		var t = Math.Clamp( (value - edge0) / (edge1 - edge0), 0f, 1f );
		return t * t * (3f - 2f * t );
	}

	static float Lerp( float a, float b, float t ) => a + (b - a) * t;
}
