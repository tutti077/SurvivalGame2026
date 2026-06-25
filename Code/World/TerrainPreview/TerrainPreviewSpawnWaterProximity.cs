namespace Survival;

/// <summary>Nearest ocean and inner-disk water for spawn-adjacent tuning.</summary>
public static class TerrainPreviewSpawnWaterProximity
{
	const int SearchAngles = 48;
	const float SearchStepMeters = 200f;
	const int InnerDiskGrid = 20;

	public readonly struct Result
	{
		public float NearestOceanDistanceMeters { get; init; }
		public float InnerHalfOceanFraction01 { get; init; }

		public bool HasWaterWithin( float maxDistanceMeters )
			=> NearestOceanDistanceMeters <= Math.Max( 50f, maxDistanceMeters ) + 0.5f;

		public bool MeetsInnerHalfOcean( float minFraction01 )
			=> InnerHalfOceanFraction01 + 0.0001f >= Math.Clamp( minFraction01, 0f, 1f );
	}

	public static Result Measure(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend = null )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;

		var innerHalfRadius = settings.WorldRadiusMeters * Math.Clamp( settings.ValleyInnerHalfRadius01, 0.1f, 1f );
		var maxSearch = Math.Max( innerHalfRadius, settings.ValleyNearWaterMaxDistanceMeters );

		return new Result
		{
			NearestOceanDistanceMeters = MeasureNearestOceanDistanceMeters( settings, maxSearch, backend ),
			InnerHalfOceanFraction01 = MeasureInnerDiskOceanFraction01( settings, innerHalfRadius, backend ),
		};
	}

	static float MeasureNearestOceanDistanceMeters(
		TerrainPreviewSettings settings,
		float maxSearchMeters,
		ITerrainPreviewBackend backend )
	{
		maxSearchMeters = Math.Max( 100f, maxSearchMeters );
		var start = Math.Max( settings.ValleySpawnLandRadiusMeters, SearchStepMeters );
		var best = float.MaxValue;

		for ( var a = 0; a < SearchAngles; a++ )
		{
			var angle = (a / (float)SearchAngles) * MathF.PI * 2f;
			var dx = MathF.Cos( angle );
			var dy = MathF.Sin( angle );

			for ( var dist = start; dist <= maxSearchMeters; dist += SearchStepMeters )
			{
				var sample = backend.Sample( settings, dx * dist, dy * dist );
				if ( !sample.IsInsideWorld )
					break;

				if ( sample.OceanHeight01 > 0.5f )
				{
					best = Math.Min( best, dist );
					break;
				}
			}
		}

		return best == float.MaxValue ? maxSearchMeters + SearchStepMeters : best;
	}

	static float MeasureInnerDiskOceanFraction01(
		TerrainPreviewSettings settings,
		float innerRadiusMeters,
		ITerrainPreviewBackend backend )
	{
		innerRadiusMeters = Math.Max( 50f, innerRadiusMeters );
		var samples = 0;
		var ocean = 0;

		for ( var iy = 0; iy < InnerDiskGrid; iy++ )
		{
			for ( var ix = 0; ix < InnerDiskGrid; ix++ )
			{
				var ux = ((ix + 0.5f) / InnerDiskGrid * 2f) - 1f;
				var uy = ((iy + 0.5f) / InnerDiskGrid * 2f) - 1f;
				if ( (ux * ux) + (uy * uy) > 1f )
					continue;

				var wx = ux * innerRadiusMeters;
				var wy = uy * innerRadiusMeters;
				var sample = backend.Sample( settings, wx, wy );
				if ( !sample.IsInsideWorld )
					continue;

				samples++;
				if ( sample.OceanHeight01 > 0.5f )
					ocean++;
			}
		}

		return samples > 0 ? ocean / (float)samples : 0f;
	}
}
