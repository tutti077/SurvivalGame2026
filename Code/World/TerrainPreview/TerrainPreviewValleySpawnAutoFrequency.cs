namespace Survival;

/// <summary>
/// Raises valley frequency from the baseline when spawn surroundings are too wet.
/// </summary>
public static class TerrainPreviewValleySpawnAutoFrequency
{
	const int MaxAdjustIterations = 64;

	public readonly struct ResolveResult
	{
		public float StartingFrequency { get; init; }
		public float ResolvedFrequency { get; init; }
		public int StepsUp { get; init; }
		public bool TargetMet { get; init; }
		public bool AutoSkipped { get; init; }
		public float SpawnLandFraction01 { get; init; }
		public ValleyAutoUnmetGoal UnmetGoals { get; init; }
		public ValleyAutoLimitHit LimitsHit { get; init; }
	}

	public static ResolveResult Apply( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;

		var minLand = TerrainPreviewValleyAutoEvaluate.SpawnAcceptableLand( settings );
		var radius = Math.Max( 5f, settings.ValleySpawnLandRadiusMeters );
		var maxFrequency = Math.Clamp( settings.ValleyAutoFrequencyMax, 0.5f, 64f );

		if ( !settings.EnableValleySpawnAutoFrequency || !settings.EnableValleyLayer )
		{
			var snapshot = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
			return BuildResult( settings, backend, settings.ValleyFrequency, settings.ValleyFrequency, 0, snapshot, autoSkipped: false, ValleyAutoLimitHit.None );
		}

		settings.ValleyFrequency = TerrainPreviewValleyDefaults.Frequency;
		var startingFrequency = TerrainPreviewValleyDefaults.Frequency;

		var step = Math.Max( 0.5f, settings.ValleyAutoFrequencyStep );
		var floor = Math.Clamp( settings.ValleyAutoFrequencyMin, 0.5f, maxFrequency );
		var frequency = Math.Clamp( Math.Max( startingFrequency, floor ), 0.5f, maxFrequency );
		settings.ValleyFrequency = frequency;
		var stepsUp = 0;
		var spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );

		var limitsHit = ValleyAutoLimitHit.None;

		if ( spawnLand.MeetsLandTarget( minLand )
			&& TerrainPreviewSpawnLandEscapeCheck.MeetsTarget( settings, backend ) )
			return BuildResult( settings, backend, startingFrequency, frequency, 0, spawnLand, autoSkipped: true, limitsHit );

		for ( var i = 0; i < MaxAdjustIterations; i++ )
		{
			if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
				break;

			if ( spawnLand.MeetsLandTarget( minLand )
				&& TerrainPreviewSpawnLandEscapeCheck.MeetsTarget( settings, backend ) )
				break;

			if ( frequency >= maxFrequency - 0.0001f )
			{
				limitsHit |= ValleyAutoLimitHit.MaxValleyFrequency;
				break;
			}

			frequency = Math.Min( maxFrequency, frequency + step );
			settings.ValleyFrequency = frequency;
			stepsUp++;
			spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
		}

		if ( stepsUp >= MaxAdjustIterations )
			limitsHit |= ValleyAutoLimitHit.GreedyIterationCap;
		if ( TerrainPreviewMapIterationTracker.TimedOut )
			limitsHit |= ValleyAutoLimitHit.SearchTimedOut;
		if ( TerrainPreviewMapIterationTracker.IterationCapped )
			limitsHit |= ValleyAutoLimitHit.SearchIterationCap;

		settings.ValleyFrequency = frequency;

		if ( !TerrainPreviewSpawnLandCheck.MeetsAcceptableSpawnTarget( settings, backend ) && limitsHit == ValleyAutoLimitHit.None )
			limitsHit |= ValleyAutoLimitHit.MaxValleyFrequency;

		return BuildResult( settings, backend, startingFrequency, frequency, stepsUp, spawnLand, autoSkipped: false, limitsHit );
	}

	static ResolveResult BuildResult(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float startingFrequency,
		float resolvedFrequency,
		int stepsUp,
		TerrainPreviewSpawnLandCheck.Result spawnLand,
		bool autoSkipped,
		ValleyAutoLimitHit limitsHit )
	{
		var targetMet = TerrainPreviewSpawnLandCheck.MeetsAcceptableSpawnTarget( settings, backend );
		return new()
		{
			StartingFrequency = startingFrequency,
			ResolvedFrequency = resolvedFrequency,
			StepsUp = stepsUp,
			SpawnLandFraction01 = spawnLand.LandFraction01,
			AutoSkipped = autoSkipped,
			TargetMet = targetMet,
			UnmetGoals = targetMet ? ValleyAutoUnmetGoal.None : ValleyAutoUnmetGoal.SpawnLand,
			LimitsHit = limitsHit,
		};
	}

	public static string FormatStatus( ResolveResult result, float minLandFraction01, float radiusMeters )
	{
		var minLand = Math.Clamp( minLandFraction01, 0f, 1f );

		if ( result.AutoSkipped )
			return $" · spawn land {result.SpawnLandFraction01 * 100f:0.#}% @ {radiusMeters:0.#}m ok";

		if ( result.StepsUp == 0
			&& MathF.Abs( result.StartingFrequency - result.ResolvedFrequency ) < 0.0005f )
			return null;

		var arrow = result.StepsUp > 0
			? $" · valley freq {result.StartingFrequency:0.#}→{result.ResolvedFrequency:0.#} (+{result.StepsUp})"
			: $" · valley freq {result.ResolvedFrequency:0.#}";

		arrow += $" · spawn land {result.SpawnLandFraction01 * 100f:0.#}%";

		if ( !result.TargetMet )
			arrow += $" · need {minLand * 100f:0.#}% land @ {radiusMeters:0.#}m";

		return arrow;
	}
}
