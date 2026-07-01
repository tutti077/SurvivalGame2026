namespace Survival;

/// <summary>
/// Radial spawn band (inner–outer) plus optional mid-map emphasis for peak height only.
/// </summary>
static class TerrainPreviewMountainFalloff
{
	/// <summary>Flat donut band for spawn eligibility — no mid-map radial emphasis.</summary>
	public static float SampleSpawnBand01( TerrainPreviewSettings settings, float distanceMetersFromCenter )
		=> SampleBand01( settings, distanceMetersFromCenter );

	public static float Sample01( TerrainPreviewSettings settings, float distanceMetersFromCenter )
	{
		var band = SampleBand01( settings, distanceMetersFromCenter );
		if ( band <= 0.0001f )
			return 0f;

		var emphasis = SampleMidMapEmphasis( settings, distanceMetersFromCenter );
		return Math.Clamp( band * emphasis, 0f, 1f );
	}

	static float SampleBand01( TerrainPreviewSettings settings, float distanceMetersFromCenter )
	{
		var inner = settings.MountainInnerRadiusMeters;
		var outer = settings.MountainOuterRadiusMeters;
		if ( outer <= 0f || inner >= outer )
			return 0f;

		var fade = Math.Max( 1f, settings.MountainBandFadeMeters );
		var power = Math.Clamp( settings.MountainFalloffRimPower, 0.25f, 4f );

		if ( distanceMetersFromCenter >= outer )
			return 0f;

		if ( distanceMetersFromCenter <= inner - fade )
			return 0f;

		if ( distanceMetersFromCenter < inner )
			return ApplyEdgePower( (distanceMetersFromCenter - (inner - fade)) / fade, power );

		if ( distanceMetersFromCenter <= outer - fade )
			return 1f;

		return ApplyEdgePower( (outer - distanceMetersFromCenter) / fade, power );
	}

	static float SampleMidMapEmphasis( TerrainPreviewSettings settings, float distanceMetersFromCenter )
	{
		var blend = Math.Clamp( settings.MountainMidMapEmphasis01, 0f, 1f );
		if ( blend <= 0.0001f )
			return 1f;

		var radius = Math.Max( 1f, settings.WorldRadiusMeters );
		var dist01 = distanceMetersFromCenter / radius;
		var peakAt = Math.Clamp( settings.MountainMidMapRadialPeak01, 0.12f, 0.75f );
		var spread = Math.Max( 0.06f, settings.MountainMidMapRadialSpread01 );
		var radial = MathF.Exp( -MathF.Pow( (dist01 - peakAt) / spread, 2f ) );
		var floor = Math.Clamp( settings.MountainMidMapRadialFloor01, 0.05f, 0.85f );

		return Lerp( 1f, Math.Max( floor, radial ), blend );
	}

	static float ApplyEdgePower( float t, float power )
	{
		t = Math.Clamp( t, 0f, 1f );
		var smooth = t * t * (3f - 2f * t );
		return MathF.Pow( smooth, power );
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t );
}
