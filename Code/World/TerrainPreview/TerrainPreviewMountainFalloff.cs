namespace Survival;

/// <summary>
/// Donut-shaped mountain zone: no peaks in the center spawn area or near the world rim.
/// White (1) in the band between inner and outer radius, black (0) inside and outside.
/// </summary>
static class TerrainPreviewMountainFalloff
{
	public static float Sample01( TerrainPreviewSettings settings, float distanceMetersFromCenter )
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

	static float ApplyEdgePower( float t, float power )
	{
		t = Math.Clamp( t, 0f, 1f );
		var smooth = t * t * (3f - 2f * t );
		return MathF.Pow( smooth, power );
	}
}
