namespace Survival;

/// <summary>
/// Blends land height toward sea level near the outer ocean rim only — not inland lakes (those use a water mask).
/// </summary>
static class TerrainPreviewCoastalSmoothing
{
	public static float ApplyMeters(
		TerrainPreviewSettings settings,
		float landHeightMeters,
		float distFromCenterMeters,
		float nx,
		float ny,
		int seed )
	{
		if ( landHeightMeters <= settings.SeaLevelMeters + 0.01f )
			return landHeightMeters;

		var outerProx = SampleOuterCoastProximity01( settings, distFromCenterMeters );
		if ( outerProx <= 0.0001f )
			return landHeightMeters;

		var sea = settings.SeaLevelMeters;
		var personality = TerrainPreviewNoise.Fbm(
			seed + 777,
			nx * settings.CoastalPersonalityFrequency,
			ny * settings.CoastalPersonalityFrequency,
			3 );
		var isCliff = personality >= settings.CoastalCliffThreshold01;
		var shoreMax = Math.Max( 1f, settings.CoastalMaxShoreHeightMeters );

		if ( isCliff )
		{
			var cliffT = MathF.Pow( outerProx, 0.35f );
			var allowedMax = Lerp( landHeightMeters, sea + shoreMax, cliffT );
			return Math.Min( landHeightMeters, allowedMax );
		}

		return Lerp( landHeightMeters, sea, outerProx );
	}

	static float SampleOuterCoastProximity01( TerrainPreviewSettings settings, float distFromCenterMeters )
	{
		var landRadius = settings.LandRadiusMeters;
		var distFromLandEdge = landRadius - distFromCenterMeters;

		var beachBand = Math.Max( 50f, settings.CoastalBeachBlendBandMeters );
		var cliffBand = Math.Max( 25f, settings.CoastalCliffBlendBandMeters );
		var outerBlendBand = Math.Max( beachBand, cliffBand );

		if ( distFromLandEdge < 0f || distFromLandEdge >= outerBlendBand )
			return 0f;

		return SmoothStep01( 1f - (distFromLandEdge / outerBlendBand) );
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t );
	}

	static float Lerp( float a, float b, float t ) => a + (b - a) * t;
}
