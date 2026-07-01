namespace Survival;

/// <summary>Land vs lake-mask water around world spawn (0,0). Uses lake field cache only — not full height pipeline.</summary>
public static class TerrainPreviewSpawnLandCheck
{
	public readonly struct Result
	{
		public float LandFraction01 { get; init; }
		public int SampleCount { get; init; }

		public bool MeetsLandTarget( float minLandFraction01 )
			=> SampleCount > 0 && LandFraction01 + 0.0001f >= Math.Clamp( minLandFraction01, 0f, 1f );
	}

	public static Result MeasureLakeDisk( TerrainPreviewSettings settings, float radiusMeters )
	{
		radiusMeters = Math.Max( 5f, radiusMeters );
		const int grid = 16;
		var samples = 0;
		var land = 0;

		for ( var iy = 0; iy < grid; iy++ )
		{
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				break;

			for ( var ix = 0; ix < grid; ix++ )
			{
				var ux = ((ix + 0.5f) / grid * 2f) - 1f;
				var uy = ((iy + 0.5f) / grid * 2f) - 1f;
				if ( (ux * ux) + (uy * uy) > 1f )
					continue;

				var wx = ux * radiusMeters;
				var wy = uy * radiusMeters;
				var dist = MathF.Sqrt( (wx * wx) + (wy * wy) );
				if ( dist > settings.LandRadiusMeters )
					continue;

				samples++;
				if ( !TerrainPreviewLandDiskFields.IsOpenWater( settings, wx, wy ) )
					land++;
			}
		}

		return new Result
		{
			LandFraction01 = samples > 0 ? land / (float)samples : 0f,
			SampleCount = samples,
		};
	}

	public static bool IsSpawnOnDryLand( TerrainPreviewSettings settings )
	{
		if ( TerrainPreviewGenerateProgress.ShouldAbort() )
			return false;

		if ( !settings.EnableInteriorWaterLayer )
			return TerrainPreviewLandDiskFields.IsOnLand( settings, 0f, 0f );

		if ( !TerrainPreviewLandDiskFields.IsOnLand( settings, 0f, 0f ) )
			return false;

		if ( TerrainPreviewLandDiskFields.IsOpenWater( settings, 0f, 0f ) )
			return false;

		var radius = Math.Max( 5f, settings.LakeSpawnCheckRadiusMeters );
		return MeasureLakeDisk( settings, radius ).MeetsLandTarget( 0.5f );
	}

	/// <summary>Distance from spawn to nearest open lake on land, or -1 if none within search radius.</summary>
	public static float MeasureNearestOpenWaterMeters( TerrainPreviewSettings settings, float searchRadiusMeters )
	{
		if ( TerrainPreviewGenerateProgress.ShouldAbort() )
			return -1f;

		if ( !settings.EnableInteriorWaterLayer )
			return -1f;

		return TerrainPreviewLandDiskFields.MeasureNearestOpenWaterMeters( settings, searchRadiusMeters );
	}

	/// <summary>
	/// Showcase water = open lake shoreline within <see cref="TerrainPreviewSettings.LakeSpawnShowcaseWaterRadiusMeters"/>
	/// of spawn (0,0). Spawn itself stays dry; distance is edge-to-edge on the lake mask raster.
	/// </summary>
	public static bool HasShowcaseWaterNearSpawn( TerrainPreviewSettings settings )
	{
		var radius = Math.Max( 50f, settings.LakeSpawnShowcaseWaterRadiusMeters );
		var nearest = MeasureNearestOpenWaterMeters( settings, radius );
		return nearest >= 1f && nearest <= radius + 0.5f;
	}
}
