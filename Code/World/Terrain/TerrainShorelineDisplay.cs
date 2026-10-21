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



	/// <summary>Meters inland from the nearest display-water shore (rim ocean or lake). MaxValue when far inland.</summary>

	public static float SampleInlandDistanceFromDisplayWaterMeters(

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



		if ( settings.AzureCoastIncludeRimOcean )

		{

			var rimInlandMeters = landRadius - distFromCenter;

			if ( rimInlandMeters > 0f )

				best = rimInlandMeters;

		}



		if ( settings.EnableInteriorWaterLayer )

		{

			var lakeDist = TerrainPreviewLandDiskFields.SampleDistanceToDisplayLakeMetersSmooth(

				settings, worldXMeters, worldYMeters );

			if ( float.IsFinite( lakeDist ) )

				best = Math.Min( best, lakeDist );

		}



		return best;

	}

}

