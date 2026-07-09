namespace Survival;

/// <summary>
/// Blends dry land height toward sea level using the continuous lake mask — same contour as display water tint.
/// Replaces the coarse boolean open-water height cutoff at sample time.
/// </summary>
static class TerrainPreviewLakeShoreHeight
{
	public static float ApplyDryLandNearLake(
		TerrainPreviewSettings settings,
		float dryLandHeightMeters,
		float lakeMask01 )
	{
		if ( !settings.EnableInteriorWaterLayer )
			return dryLandHeightMeters;

		TerrainPreviewLandDiskFields.EnsureReady( settings );
		var threshold = TerrainPreviewLandDiskFields.GetOpenWaterThreshold01( settings );
		if ( lakeMask01 >= threshold )
			return settings.SeaLevelMeters;

		var shoreDetail = Math.Clamp( settings.LakeShoreDetail01, 0f, 1f );
		var maskBand = Math.Max(
			0.012f,
			(0.035f + (shoreDetail * 0.08f)) * Math.Clamp( settings.LakeShoreBlendWidth01 * 3f, 0.5f, 2f ) );
		var delta = threshold - lakeMask01;
		if ( delta >= maskBand )
			return dryLandHeightMeters;

		var t = 1f - (delta / maskBand);
		t = SmoothStep( t ) * Math.Clamp( settings.LakeShoreBlendStrength01, 0.35f, 1f );

		var sea = settings.SeaLevelMeters;
		return dryLandHeightMeters + ((sea - dryLandHeightMeters) * t);
	}

	public static bool IsSubmergedByLakeMask( TerrainPreviewSettings settings, float lakeMask01 )
	{
		if ( !settings.EnableInteriorWaterLayer )
			return false;

		TerrainPreviewLandDiskFields.EnsureReady( settings );
		return lakeMask01 >= TerrainPreviewLandDiskFields.GetOpenWaterThreshold01( settings );
	}

	static float SmoothStep( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}
}
