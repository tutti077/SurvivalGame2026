namespace Survival;

/// <summary>
/// Display-only water tint — follows the continuous lake mask contour at each sample point,
/// not the boolean open-water raster (which stair-steps on the land disk).
/// </summary>
static class TerrainShorelineDisplay
{
	/// <summary>Map, preview and chunk vertex colour: lakes, the rim ocean and river channels read as water (river beds paint blue under the water strip too — Mark 2026-10-07).</summary>
	public static bool IsDisplayWaterColor( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		if ( settings is null )
			return false;

		var landRadius = settings.LandRadiusMeters;
		var totalRadius = settings.TotalWorldRadiusMeters;
		if ( landRadius <= 0f || totalRadius <= 0f )
			return false;

		var distFromCenter = MathF.Sqrt( (worldXMeters * worldXMeters) + (worldYMeters * worldYMeters) );
		if ( distFromCenter > totalRadius )
			return false;

		if ( distFromCenter > landRadius )
			return true;

		if ( TerrainPreviewLandDiskFields.IsRiverWaterAtWorld( settings, worldXMeters, worldYMeters ) )
			return true;
		return IsDisplayLakeWaterAtSample( settings, worldXMeters, worldYMeters );
	}

	public static bool IsDisplayLakeWaterAtSample(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
		=> TerrainPreviewLandDiskFields.IsDisplayLakeWaterAtWorld( settings, worldXMeters, worldYMeters );

	/// <summary>
	/// Meters inland from the nearest OCEAN shore: the rim ring, or display water whose open-water body is joined to
	/// the rim (bays, inland seas that run into it). Inland lakes never count (Mark 2026-10-29: Azure Coast only on
	/// the edge of oceans). 0 on water, MaxValue when far inland.
	/// </summary>
	public static float SampleInlandDistanceFromOceanShoreMeters(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
	{
		if ( IsDisplayWaterColor( settings, worldXMeters, worldYMeters ) )
			return 0f;

		var landRadius = settings.LandRadiusMeters;
		var distFromCenter = MathF.Sqrt( (worldXMeters * worldXMeters) + (worldYMeters * worldYMeters) );
		if ( distFromCenter > landRadius )
			return float.MaxValue;

		var best = float.MaxValue;
		var rimInlandMeters = landRadius - distFromCenter;
		if ( rimInlandMeters > 0f )
			best = rimInlandMeters;

		if ( settings.EnableInteriorWaterLayer )
		{
			// The smooth contour distance gives the exact shore; the coarse ocean raster only says whether that
			// shore belongs to rim-joined water (within a couple of cells of slack for the mask-vs-speck difference).
			var lakeDist = TerrainPreviewLandDiskFields.SampleDistanceToDisplayLakeMetersSmooth(
				settings, worldXMeters, worldYMeters );
			if ( float.IsFinite( lakeDist )
				&& TerrainPreviewLandDiskFields.IsOceanWaterWithinMeters( settings, worldXMeters, worldYMeters, lakeDist ) )
				best = Math.Min( best, lakeDist );
		}

		return best;
	}
}
