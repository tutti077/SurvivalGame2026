namespace Survival;

/// <summary>
/// Connected-component patch filters (min / max diameter). Shared by lakes, mountains, biomes.
/// </summary>
static class TerrainPreviewPatchFilter
{
	public static void RemoveSmallPatches(
		bool[] mask,
		int width,
		int height,
		float metersPerPixel,
		float minPatchDiameterMeters )
		=> TerrainPreviewMountainSpeckFilter.RemoveSmallPatches(
			mask, width, height, metersPerPixel, minPatchDiameterMeters );

	public static void FillSmallDryIslandsInWater(
		bool[] openWater,
		bool[] landDisk,
		int width,
		int height,
		float metersPerPixel,
		float minIslandDiameterMeters,
		int maxPasses = 2 )
		=> TerrainPreviewMountainSpeckFilter.FillSmallDryIslandsInWater(
			openWater, landDisk, width, height, metersPerPixel, minIslandDiameterMeters, maxPasses );
}
