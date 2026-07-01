namespace Survival;

/// <summary>
/// Soft ceiling on mountain band terrain — only trims excess above typical range; summits may approach world max.
/// </summary>
static class TerrainPreviewMountainPeakCap
{
	public static float ApplyMeters(
		TerrainPreviewSettings settings,
		float nx,
		float ny,
		int seed,
		TerrainPreviewMountainHeight.Result mountain,
		float heightMeters,
		float maxHeightMeters )
	{
		if ( mountain.CombinedInfluence01 <= 0.05f )
			return heightMeters;

		maxHeightMeters = Math.Max( 50f, maxHeightMeters );
		var typicalMax = Math.Clamp( settings.MountainTypicalPeakMax01, 0.2f, 0.95f ) * maxHeightMeters;
		if ( heightMeters <= typicalMax )
			return heightMeters;

		var absoluteMax = Math.Clamp( settings.MountainAbsolutePeakMax01, 0.5f, 1f ) * maxHeightMeters;

		var macroPeak = TerrainPreviewNoise.RidgedFbm(
			seed + 901,
			nx * settings.MountainSummitMacroFrequency,
			ny * settings.MountainSummitMacroFrequency,
			3 );
		var isSummitCell = macroPeak >= settings.MountainSummitMacroThreshold01
			&& mountain.PeakLift01 >= settings.MountainSummitLocalPeakMin01;

		var allowedMax = typicalMax;
		if ( isSummitCell )
		{
			var summitBlend = SmoothStep01( (macroPeak - settings.MountainSummitMacroThreshold01) / 0.08f );
			allowedMax = Lerp( typicalMax, absoluteMax, summitBlend );
		}
		else if ( macroPeak > settings.MountainSummitMacroThreshold01 - 0.12f )
		{
			var nearSummit = SmoothStep01( (macroPeak - (settings.MountainSummitMacroThreshold01 - 0.12f)) / 0.12f );
			allowedMax = Lerp( typicalMax, typicalMax + ((absoluteMax - typicalMax) * 0.45f), nearSummit );
		}

		var zone = Math.Clamp( mountain.CombinedInfluence01, 0f, 1f );
		var zoneAllow = Lerp( typicalMax, allowedMax, zone );
		return Math.Min( heightMeters, zoneAllow );
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t );
	}

	static float Lerp( float a, float b, float t ) => a + (b - a) * t;
}
