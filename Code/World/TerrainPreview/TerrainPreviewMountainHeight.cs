namespace Survival;

/// <summary>
/// Mountain peak height variation + foothill uplift around ranges.
/// Separated so the prompt's changes can be reverted in one file if needed.
/// </summary>
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
		float mountainShape01,
		float mountainZone01,
		float nx,
		float ny,
		int seed )
	{
		if ( mountainZone01 <= 0f )
			return default;

		var threshold = Math.Clamp( settings.MountainThreshold, 0f, 0.98f );
		var regionCap = SampleRegionPeakCap( settings, nx, ny, seed );
		var localPeak = SampleLocalPeakIntensity( mountainShape01, threshold );

		var presence = mountainZone01 * SmoothRange( mountainShape01, threshold, threshold + 0.18f );
		var peakLift = presence * localPeak * regionCap * settings.MountainPeakBoost;

		var foothillStart = Math.Max( 0f, threshold - settings.MountainFoothillSpread );
		var foothillPresence = mountainZone01 * SmoothRange( mountainShape01, foothillStart, threshold );
		var foothillFalloffFromCore = 1f - localPeak * 0.65f;
		var foothillLift = foothillPresence * foothillFalloffFromCore * regionCap * settings.MountainFoothillBoost;

		var combined = Math.Clamp( peakLift + foothillLift, 0f, 1f );

		return new Result
		{
			PeakLift01 = peakLift,
			FoothillLift01 = foothillLift,
			CombinedInfluence01 = combined,
			RegionPeakCap01 = regionCap,
		};
	}

	/// <summary>Slow noise — some ranges can reach white (1), others cap lower.</summary>
	static float SampleRegionPeakCap( TerrainPreviewSettings settings, float nx, float ny, int seed )
	{
		var variation = TerrainPreviewNoise.Fbm(
			seed + 400,
			nx * settings.MountainPeakVariationFrequency,
			ny * settings.MountainPeakVariationFrequency,
			3 );

		var minPeak = Math.Clamp( settings.MountainMinPeakHeight01, 0f, 1f );
		return Lerp( minPeak, 1f, variation );
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
