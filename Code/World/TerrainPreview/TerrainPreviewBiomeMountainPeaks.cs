namespace Survival;

/// <summary>Mountain shape for one point — rise 0-1 and the summit height, one lerp in LandHeightDisplay.</summary>
static class TerrainPreviewBiomeMountainPeaks
{
	public static TerrainPreviewMountainHeight.Profile SampleProfile(
		TerrainPreviewSettings settings,
		float mountainInfluence01,
		float distMeters,
		float nx,
		float ny,
		int seed )
	{
		mountainInfluence01 = Math.Clamp( mountainInfluence01, 0f, 1f );
		if ( !settings.EnableMountainLayer || mountainInfluence01 <= 0.0001f )
			return default;

		var mountainZone = TerrainPreviewMountainFalloff.Sample01( settings, distMeters );
		if ( mountainZone <= 0.0001f )
			return default;

		var diameter = Math.Max( 500f, settings.WorldDiameterMeters );
		var blob = TerrainPreviewMountainSpawnMask.SampleBlob( settings, nx, ny, diameter );
		if ( blob.Field01 <= 0f )
			return default;

		// No lake fade any more (2026-10-30): a lake under a mountain is either a tarn on its top or filled in by it
		// (TerrainPreviewMountainLakes), and a lake at its foot meets the wall — the old fade sloped the mountain down
		// into every lake it touched.
		return TerrainPreviewMountainHeight.Sample( settings, blob, mountainZone, nx, ny, seed );
	}
}
