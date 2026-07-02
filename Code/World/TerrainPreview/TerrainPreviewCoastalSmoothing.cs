namespace Survival;

/// <summary>
/// Blends dry land height toward sea level near open water — outer ocean rim and inland lakes.
/// Lake shores ease down to a low bank height, not flat zero across the whole band.
/// </summary>
static class TerrainPreviewCoastalSmoothing
{
	public static float ApplyMeters(
		TerrainPreviewSettings settings,
		float landHeightMeters,
		float worldXMeters,
		float worldYMeters,
		float distFromCenterMeters,
		float nx,
		float ny,
		int seed )
	{
		if ( landHeightMeters <= settings.SeaLevelMeters + 0.01f )
			return landHeightMeters;

		var outerProx = SampleOuterCoastProximity01( settings, distFromCenterMeters );
		var lakeProx = SampleInlandLakeShoreProximity01( settings, worldXMeters, worldYMeters );
		if ( outerProx <= 0.0001f && lakeProx <= 0.0001f )
			return landHeightMeters;

		var sea = settings.SeaLevelMeters;
		var shoreMax = Math.Max( 1f, settings.CoastalMaxShoreHeightMeters );
		var result = landHeightMeters;

		if ( lakeProx > 0.0001f )
		{
			var bankHeight = sea + (shoreMax * (1f - (lakeProx * 0.65f)));
			result = Lerp( result, Math.Min( result, bankHeight ), lakeProx );
		}

		if ( outerProx <= 0.0001f )
			return result;

		var personality = TerrainPreviewNoise.Fbm(
			seed + 777,
			nx * settings.CoastalPersonalityFrequency,
			ny * settings.CoastalPersonalityFrequency,
			3 );
		var isCliff = personality >= settings.CoastalCliffThreshold01;

		if ( isCliff )
		{
			var cliffT = MathF.Pow( outerProx, 0.35f );
			var allowedMax = Lerp( result, sea + shoreMax, cliffT );
			return Math.Min( result, allowedMax );
		}

		return Lerp( result, sea, outerProx );
	}

	static float SampleInlandLakeShoreProximity01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
	{
		if ( !settings.EnableInteriorWaterLayer )
			return 0f;

		var distMeters = TerrainPreviewLandDiskFields.SampleDistanceToOpenWaterMeters(
			settings, worldXMeters, worldYMeters );
		if ( !float.IsFinite( distMeters ) || distMeters >= float.MaxValue * 0.5f )
			return 0f;

		var band = Math.Max( 50f, settings.CoastalInlandBeachBandMeters );
		if ( distMeters >= band )
			return 0f;

		return SmoothStep01( 1f - (distMeters / band) );
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
		return t * t * (3f - (2f * t ));
	}

	static float Lerp( float a, float b, float t ) => a + (b - a) * t;
}
