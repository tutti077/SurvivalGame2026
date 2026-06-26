namespace Survival;

/// <summary>
/// Ensures spawn is not a land island surrounded by ocean — at least one radial path
/// must stay dry for <see cref="TerrainPreviewSettings.SpawnEscapeMinDistanceMeters"/>.
/// </summary>
public static class TerrainPreviewSpawnLandEscapeCheck
{
	const int SearchAngles = 48;

	public readonly struct Result
	{
		public bool HasEscape { get; init; }
		public float BestContinuousLandMeters { get; init; }
		public int SuccessfulDirections { get; init; }
	}

	public static Result Measure(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend = null )
	{
		if ( !settings.SpawnRequireLandEscape )
		{
			var need = Math.Max( 50f, settings.SpawnEscapeMinDistanceMeters );
			return new Result
			{
				HasEscape = true,
				BestContinuousLandMeters = need,
				SuccessfulDirections = SearchAngles,
			};
		}

		backend ??= TerrainPreviewBackendRegistry.Active;
		var minEscape = Math.Max( 50f, settings.SpawnEscapeMinDistanceMeters );
		var step = Math.Clamp( minEscape / 16f, 10f, 50f );

		var bestRun = 0f;
		var successes = 0;

		for ( var a = 0; a < SearchAngles; a++ )
		{
			var angle = (a / (float)SearchAngles) * MathF.PI * 2f;
			var dx = MathF.Cos( angle );
			var dy = MathF.Sin( angle );
			var run = 0f;

			for ( var dist = 0f; dist <= minEscape + 0.001f; dist += step )
			{
				var sample = backend.Sample( settings, dx * dist, dy * dist );
				if ( !sample.IsInsideWorld || sample.OceanHeight01 > 0.5f )
					break;

				run = dist;
			}

			bestRun = Math.Max( bestRun, run );
			if ( run + 0.5f >= minEscape )
				successes++;
		}

		return new Result
		{
			HasEscape = successes > 0,
			BestContinuousLandMeters = bestRun,
			SuccessfulDirections = successes,
		};
	}

	public static bool MeetsTarget( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
		=> Measure( settings, backend ).HasEscape;
}
