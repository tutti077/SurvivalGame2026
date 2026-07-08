namespace Survival;

/// <summary>
/// Blends dry land height toward sea level near open water — outer ocean rim and inland lakes.
/// Drain influence fades in smoothly from the band edge (near-zero at the outer rim) to full strength at the water.
/// </summary>
static class TerrainPreviewCoastalSmoothing
{
	const float DrainFadeBandMultiplier = 2.5f;

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

		var sea = settings.SeaLevelMeters;
		var result = landHeightMeters;

		if ( settings.EnableInteriorWaterLayer )
		{
			var waterDist = TerrainPreviewLandDiskFields.SampleDistanceToOpenWaterMetersSmooth(
				settings, worldXMeters, worldYMeters );
			var lakeBand = Math.Max( 50f, settings.CoastalInlandBeachBandMeters );
			if ( float.IsFinite( waterDist ) && waterDist < lakeBand * DrainFadeBandMultiplier )
			{
				var shoreMax = Math.Max( 1f, settings.CoastalMaxShoreHeightMeters );
				var slope = shoreMax / lakeBand;
				result = ApplyWeightedDrain(
					result, sea, waterDist, lakeBand, slope );
			}
		}

		var landRadius = settings.LandRadiusMeters;
		var distFromLandEdge = landRadius - distFromCenterMeters;
		if ( distFromLandEdge > 0f )
		{
			var beachBand = Math.Max( 50f, settings.CoastalBeachBlendBandMeters );
			var cliffBand = Math.Max( 25f, settings.CoastalCliffBlendBandMeters );
			var personality = TerrainPreviewNoise.Fbm(
				seed + 777,
				nx * settings.CoastalPersonalityFrequency,
				ny * settings.CoastalPersonalityFrequency,
				3 );
			var isCliff = personality >= settings.CoastalCliffThreshold01;
			var outerBand = isCliff ? cliffBand : beachBand;
			var fadeLimit = outerBand * DrainFadeBandMultiplier;
			if ( distFromLandEdge < fadeLimit )
			{
				var shoreMax = Math.Max( 1f, settings.CoastalMaxShoreHeightMeters );
				var slope = (isCliff ? shoreMax * 2.2f : shoreMax) / outerBand;
				result = ApplyWeightedDrain(
					result, sea, distFromLandEdge, outerBand, slope );
			}
		}

		return result;
	}

	/// <summary>
	/// Soft drain: at the outer fade edge weight ≈ 0 (terrain barely touched); at water weight = 1 (full cap).
	/// </summary>
	static float ApplyWeightedDrain(
		float landHeightMeters,
		float seaLevelMeters,
		float distMeters,
		float coreBandMeters,
		float drainSlope )
	{
		if ( distMeters <= 0f )
			return Math.Min( landHeightMeters, seaLevelMeters );

		var fadeBand = Math.Max( coreBandMeters * DrainFadeBandMultiplier, coreBandMeters + 1f );
		if ( distMeters >= fadeBand )
			return landHeightMeters;

		var drainedCap = seaLevelMeters + (distMeters * drainSlope);
		var drainedHeight = Math.Min( landHeightMeters, drainedCap );

		var t = 1f - (distMeters / fadeBand);
		var weight = SmoothStep01( SmoothStep01( t ) );

		return Lerp( landHeightMeters, drainedHeight, weight );
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t);
}
