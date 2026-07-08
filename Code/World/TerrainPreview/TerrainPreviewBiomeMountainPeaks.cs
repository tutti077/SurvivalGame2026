namespace Survival;

/// <summary>Mountain peak boost sampled as 0–1 headroom — final meters come from a single lerp in LandHeightDisplay.</summary>
static class TerrainPreviewBiomeMountainPeaks
{
	/// <summary>Peak boost 0–1 (fraction of mountain headroom above lowland cap).</summary>
	public static float SampleBoost01(
		TerrainPreviewSettings settings,
		float mountainInfluence01,
		float lakeMask01,
		float distMeters,
		float nx,
		float ny,
		int seed,
		out TerrainPreviewMountainHeight.Result mountain )
	{
		mountain = default;
		mountainInfluence01 = Math.Clamp( mountainInfluence01, 0f, 1f );
		if ( !settings.EnableMountainLayer || mountainInfluence01 <= 0.0001f )
			return 0f;

		var mountainZone = TerrainPreviewMountainFalloff.Sample01( settings, distMeters );
		if ( mountainZone <= 0.0001f )
			return 0f;

		var diameter = Math.Max( 500f, settings.WorldDiameterMeters );
		var rangeField = TerrainPreviewMountainSpawnMask.SampleRangeField01( settings, nx, ny, diameter );
		var peakPlacement = TerrainPreviewMountainSpawnMask.SamplePeakPlacement01( settings, nx, ny, diameter );
		mountain = TerrainPreviewMountainHeight.Sample(
			settings, rangeField, peakPlacement, mountainZone, nx, ny, seed );

		var lakeClear = Math.Clamp(
			lakeMask01 / Math.Max( 0.04f, settings.InteriorLakeMountainClearCarve01 ),
			0f,
			1f );
		var liftToSculpt = Math.Clamp( settings.MountainPeakLiftToSculpt01, 0.08f, 0.55f );
		return mountain.TotalLift01 * (1f - (lakeClear * lakeClear)) * mountainInfluence01 * liftToSculpt;
	}
}
