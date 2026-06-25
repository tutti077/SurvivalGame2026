namespace Survival;

/// <summary>Land vs ocean around world spawn (0,0) for valley auto-frequency tuning.</summary>
public static class TerrainPreviewSpawnLandCheck
{
	public readonly struct Result
	{
		public float LandFraction01 { get; init; }
		public int SampleCount { get; init; }

		public bool MeetsLandTarget( float minLandFraction01 )
			=> SampleCount > 0 && LandFraction01 + 0.0001f >= Math.Clamp( minLandFraction01, 0f, 1f );
	}

	public static Result Measure(
		TerrainPreviewSettings settings,
		float radiusMeters,
		ITerrainPreviewBackend backend = null )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;
		radiusMeters = Math.Max( 5f, radiusMeters );

		const int grid = 16;
		var samples = 0;
		var land = 0;

		for ( var iy = 0; iy < grid; iy++ )
		{
			for ( var ix = 0; ix < grid; ix++ )
			{
				var ux = ((ix + 0.5f) / grid * 2f) - 1f;
				var uy = ((iy + 0.5f) / grid * 2f) - 1f;
				if ( (ux * ux) + (uy * uy) > 1f )
					continue;

				var wx = ux * radiusMeters;
				var wy = uy * radiusMeters;
				var sample = backend.Sample( settings, wx, wy );
				if ( !sample.IsInsideWorld )
					continue;

				samples++;
				if ( sample.OceanHeight01 < 0.5f )
					land++;
			}
		}

		return new Result
		{
			LandFraction01 = samples > 0 ? land / (float)samples : 0f,
			SampleCount = samples,
		};
	}

	public static bool MeetsGuardSpawnTarget( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
	{
		var radius = Math.Max( 5f, settings.ValleySpawnLandRadiusMeters );
		var minLand = TerrainPreviewValleyAutoEvaluate.SpawnGuardTargetLand( settings );
		return Measure( settings, radius, backend ).MeetsLandTarget( minLand );
	}

	public static bool MeetsAcceptableSpawnTarget( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
	{
		var radius = Math.Max( 5f, settings.ValleySpawnLandRadiusMeters );
		var minLand = TerrainPreviewValleyAutoEvaluate.SpawnAcceptableLand( settings );
		return Measure( settings, radius, backend ).MeetsLandTarget( minLand );
	}
}
