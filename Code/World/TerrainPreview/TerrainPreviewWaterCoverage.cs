namespace Survival;

/// <summary>
/// Splits ocean by radial zone: interior disk vs outer rim band (not flood-fill connectivity).
/// </summary>
static class TerrainPreviewWaterCoverage
{
	public static void ClassifyOceanZones(
		bool[] oceanInsideWorld,
		bool[] insideWorld,
		int resolution,
		TerrainPreviewSettings settings,
		float worldRadiusMeters,
		float worldDiameterMeters,
		out bool[] interiorOcean,
		out bool[] exteriorOcean )
	{
		interiorOcean = new bool[oceanInsideWorld.Length];
		exteriorOcean = new bool[oceanInsideWorld.Length];

		var interiorRadius01 = Math.Clamp( settings.InteriorZoneRadius01, 0.1f, 0.95f );
		var interiorRadiusMeters = worldRadiusMeters * interiorRadius01;

		for ( var py = 0; py < resolution; py++ )
		{
			for ( var px = 0; px < resolution; px++ )
			{
				var idx = (py * resolution) + px;
				if ( !insideWorld[idx] || !oceanInsideWorld[idx] )
					continue;

				var wx = (px + 0.5f) / resolution * worldDiameterMeters - worldRadiusMeters;
				var wy = (py + 0.5f) / resolution * worldDiameterMeters - worldRadiusMeters;
				var dist = MathF.Sqrt( wx * wx + wy * wy );

				if ( dist < interiorRadiusMeters )
					interiorOcean[idx] = true;
				else
					exteriorOcean[idx] = true;
			}
		}
	}

	public static TerrainPreviewWaterCoverageStats ComputeStats(
		bool[] oceanInsideWorld,
		bool[] interiorOcean,
		bool[] exteriorOcean,
		bool[] insideWorld )
	{
		var inside = 0;
		var land = 0;
		var ocean = 0;
		var interior = 0;
		var exterior = 0;

		for ( var i = 0; i < insideWorld.Length; i++ )
		{
			if ( !insideWorld[i] )
				continue;

			inside++;

			if ( oceanInsideWorld[i] )
			{
				ocean++;
				if ( interiorOcean[i] )
					interior++;
				if ( exteriorOcean[i] )
					exterior++;
			}
			else
			{
				land++;
			}
		}

		return new TerrainPreviewWaterCoverageStats
		{
			InsideWorldPixels = inside,
			LandPixels = land,
			OceanPixels = ocean,
			InteriorOceanPixels = interior,
			ExteriorOceanPixels = exterior,
		};
	}

	/// <summary>Lake water on land circle — authoritative; matches auto-threshold target.</summary>
	public static float MeasureLandDiskLakeFraction( TerrainPreviewSettings settings )
	{
		if ( !settings.EnableInteriorWaterLayer )
			return 0f;

		return TerrainPreviewLandDiskFields.GetLakeCoverageOnLand01( settings );
	}
}

static class TerrainPreviewWaterCoverageStatsExtensions
{
	public static TerrainPreviewWaterCoverageStats WithLandDiskLakeFraction(
		this TerrainPreviewWaterCoverageStats stats,
		float landDiskLakeFraction01 )
		=> new TerrainPreviewWaterCoverageStats
		{
			InsideWorldPixels = stats.InsideWorldPixels,
			LandPixels = stats.LandPixels,
			OceanPixels = stats.OceanPixels,
			InteriorOceanPixels = stats.InteriorOceanPixels,
			ExteriorOceanPixels = stats.ExteriorOceanPixels,
			LandDiskLakeFraction01 = Math.Clamp( landDiskLakeFraction01, 0f, 1f ),
		};
}
