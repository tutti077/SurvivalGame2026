namespace Survival;

/// <summary>
/// Which points lie under a lake: the continuous lake mask past the open-water threshold — the same contour as the
/// display water tint. (The land beside a lake is eased to the water by <c>TerrainPreviewPipeline.ApplyShoreBank</c>
/// from the distance to the shore; the old mask-band easing here followed the 40–150 m raster cells and drew
/// straight-edged ridges near the water — removed 2026-10-24.)
/// </summary>
static class TerrainPreviewLakeShoreHeight
{
	/// <summary>
	/// Under a lake: the mask over the open-water threshold AND inside the traced shoreline (TerrainPreviewLakeShoreDistance)
	/// — a speck the open-water raster filtered out is dry land, not a lake floor with no water over it.
	/// </summary>
	public static bool IsSubmergedByLakeMask( TerrainPreviewSettings settings, float lakeMask01, float worldXMeters, float worldYMeters )
	{
		if ( !settings.EnableInteriorWaterLayer )
			return false;

		TerrainPreviewLandDiskFields.EnsureReady( settings );
		return lakeMask01 >= TerrainPreviewLandDiskFields.GetOpenWaterThreshold01( settings )
			&& TerrainPreviewLakeShoreDistance.SignedMeters( worldXMeters, worldYMeters ) < 0f;
	}
}
