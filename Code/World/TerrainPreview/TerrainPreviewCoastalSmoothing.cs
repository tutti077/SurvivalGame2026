namespace Survival;

/// <summary>
/// Blends dry land height toward the water near open water — outer ocean rim and inland lakes.
/// Drain influence fades in smoothly from the band edge (near-zero at the outer rim) to full strength at the water.
/// Mountains are exempt from the lake drain (2026-10-30: it capped a mountain foot within ~500 m of any lake at a few
/// metres — the "terrain sloped toward the water" Mark saw; a mountain meets a lake as a wall now), and so is land whose
/// nearest lake is a tarn on a mountain top (its level is the tarn's, not the sea's).
/// On an ocean shore under a sea-cliff stretch (<see cref="TerrainPreviewSeaCliffs"/>) the drain is off and the land is
/// instead held up to the headland height over Sea Cliff Band inland of the water — the Cliffs of Moher wedge, rising
/// toward the edge; the wall itself is the shore bank's doing.
/// Around a lake at a mountain's foot (<see cref="TerrainPreviewMountainLakes"/>, Mark 2026-10-30: "trim the mountain
/// terrain a little so it's clearly not a cutout of the mountain where the lake spawns") the exemption gives way to the
/// <b>apron</b>: within <c>Mountains - Lake Apron (m)</c> of the water the ground is capped to a beach grade, the cap
/// fading out toward the apron's edge with a doubled smoothstep — a bench at the water, then the face rises steeply
/// over the apron's outer half, which is where a river coming down the mountain falls.
/// </summary>
static class TerrainPreviewCoastalSmoothing
{
	const float DrainFadeBandMultiplier = 2.5f;
	/// <summary>The apron's beach: this high above the water at the shore …</summary>
	const float ApronBeachMeters = 1.5f;
	/// <summary>… climbing this much per metre inland (≈ 4.5°) before the face takes over.</summary>
	const float ApronBeachGrade = 0.08f;

	public static float ApplyMeters(
		TerrainPreviewSettings settings,
		float landHeightMeters,
		float worldXMeters,
		float worldYMeters,
		float distFromCenterMeters,
		float nx,
		float ny,
		int seed,
		float mountainBlend01,
		bool includeLakeDrain )
	{
		if ( landHeightMeters <= settings.SeaLevelMeters + 0.01f )
			return landHeightMeters;

		var sea = settings.SeaLevelMeters;
		var result = landHeightMeters;
		var cliffBand = Math.Max( 20f, settings.CoastCliffBandMeters );
		var mountainKeep = 1f - Math.Clamp( mountainBlend01, 0f, 1f );

		if ( includeLakeDrain && settings.EnableInteriorWaterLayer )
		{
			// The traced lake shoreline (TerrainPreviewLakeShoreDistance): continuous, so the drain never steps (the 40–150 m
			// open-water raster read 0 across whole cells and drained them to water level in squares)
			var waterDist = TerrainPreviewLakeShoreDistance.SignedMeters( worldXMeters, worldYMeters );
			var lakeBand = Math.Max( 50f, settings.CoastalInlandBeachBandMeters );
			var apron = Math.Clamp( settings.MountainLakeApronMeters, 0f, 800f );
			if ( apron > 0f && float.IsFinite( waterDist ) && waterDist < apron
				&& TerrainPreviewLandDiskFields.IsNearMountainFootLake( settings, worldXMeters, worldYMeters ) )
			{
				var cap = sea + ApronBeachMeters + (Math.Max( 0f, waterDist ) * ApronBeachGrade);
				var keep = SmoothStep01( SmoothStep01( Math.Max( 0f, waterDist ) / apron ) );
				result = Lerp( Math.Min( result, cap ), result, keep );
			}
			if ( mountainKeep > 0.001f && float.IsFinite( waterDist ) && waterDist < lakeBand * DrainFadeBandMultiplier
				&& TerrainPreviewLandDiskFields.SampleWaterLevelMeters( settings, worldXMeters, worldYMeters ) <= sea + 0.5f )
			{
				// A sea cliff only on a shore joined to the ocean (a bay), never on an inland lake
				var gate = waterDist < cliffBand && TerrainPreviewLandDiskFields.IsOceanWaterWithinMeters( settings, worldXMeters, worldYMeters, Math.Max( 0f, waterDist ) + 120f )
					? TerrainPreviewSeaCliffs.Gate01( settings, worldXMeters, worldYMeters )
					: 0f;
				var shoreMax = Math.Max( 1f, settings.CoastalMaxShoreHeightMeters );
				var slope = shoreMax / lakeBand;
				result = ApplyWeightedDrain( result, sea, waterDist, lakeBand, slope, mountainKeep * (1f - gate) );
				if ( gate > 0f )
					result = HoldHeadland( settings, result, sea, Math.Max( 0f, waterDist ), cliffBand, gate * mountainKeep, worldXMeters, worldYMeters );
			}
		}

		var landRadius = settings.LandRadiusMeters;
		var distFromLandEdge = landRadius - distFromCenterMeters;
		var beachBand = Math.Max( 50f, settings.CoastalBeachBlendBandMeters );
		if ( distFromLandEdge > 0f && distFromLandEdge < beachBand * DrainFadeBandMultiplier )
		{
			var gate = distFromLandEdge < cliffBand ? TerrainPreviewSeaCliffs.Gate01( settings, worldXMeters, worldYMeters ) : 0f;
			var shoreMax = Math.Max( 1f, settings.CoastalMaxShoreHeightMeters );
			var slope = shoreMax / beachBand;
			result = ApplyWeightedDrain( result, sea, distFromLandEdge, beachBand, slope, 1f - gate );
			if ( gate > 0f )
				result = HoldHeadland( settings, result, sea, distFromLandEdge, cliffBand, gate, worldXMeters, worldYMeters );
		}

		return result;
	}

	/// <summary>
	/// The Moher wedge: at the water's edge the land stands at least at the headland height, easing back to its own height
	/// over the cliff band inland (never lowered — a hill on the coast keeps its height).
	/// </summary>
	static float HoldHeadland( TerrainPreviewSettings settings, float heightMeters, float seaLevelMeters, float metersFromShore, float bandMeters, float weight, float worldXMeters, float worldYMeters )
	{
		if ( weight <= 0f || metersFromShore >= bandMeters )
			return heightMeters;
		var top = seaLevelMeters + TerrainPreviewSeaCliffs.TopMeters( settings, worldXMeters, worldYMeters );
		var w = SmoothStep01( 1f - (metersFromShore / bandMeters) ) * weight;
		var held = Math.Max( heightMeters, top );
		return Lerp( heightMeters, held, w );
	}

	/// <summary>
	/// Soft drain: at the outer fade edge weight ≈ 0 (terrain barely touched); at water weight = 1 (full cap).
	/// </summary>
	static float ApplyWeightedDrain(
		float landHeightMeters,
		float seaLevelMeters,
		float distMeters,
		float coreBandMeters,
		float drainSlope,
		float weightScale )
	{
		if ( weightScale <= 0f )
			return landHeightMeters;
		if ( distMeters <= 0f )
			return Lerp( landHeightMeters, Math.Min( landHeightMeters, seaLevelMeters ), weightScale );

		var fadeBand = Math.Max( coreBandMeters * DrainFadeBandMultiplier, coreBandMeters + 1f );
		if ( distMeters >= fadeBand )
			return landHeightMeters;

		var drainedCap = seaLevelMeters + (distMeters * drainSlope);
		var drainedHeight = Math.Min( landHeightMeters, drainedCap );

		var t = 1f - (distMeters / fadeBand);
		var weight = SmoothStep01( SmoothStep01( t ) ) * weightScale;

		return Lerp( landHeightMeters, drainedHeight, weight );
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t);
}
