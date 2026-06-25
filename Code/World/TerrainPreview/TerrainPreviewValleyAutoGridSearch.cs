namespace Survival;

/// <summary>
/// Tries every valley frequency / weight step in range when greedy tuning fails or exhaustive mode is on.
/// </summary>
public static class TerrainPreviewValleyAutoGridSearch
{
	public readonly struct SolveResult
	{
		public bool Solved { get; init; }
		public float Frequency { get; init; }
		public float Weight { get; init; }
		public int CombinationsTried { get; init; }
		public ValleyAutoUnmetGoal UnmetGoals { get; init; }
		public ValleyAutoLimitHit LimitsHit { get; init; }
		public TerrainPreviewValleyAutoEvaluate.Snapshot Snapshot { get; init; }
	}

	public static SolveResult Solve( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;
		if ( !TerrainPreviewValleyAutoEvaluate.AutoActive( settings ) )
		{
			var snapshot = TerrainPreviewValleyAutoEvaluate.Measure( settings, backend );
			return new SolveResult
			{
				Solved = true,
				Frequency = settings.ValleyFrequency,
				Weight = settings.ValleyWeight,
				Snapshot = snapshot,
			};
		}

		var freqMin = settings.EnableValleySpawnAutoFrequency
			? Math.Clamp( settings.ValleyAutoFrequencyMin, 0.5f, 64f )
			: TerrainPreviewValleyDefaults.Frequency;
		var freqMax = settings.EnableValleySpawnAutoFrequency
			? Math.Clamp( settings.ValleyAutoFrequencyMax, freqMin, 64f )
			: TerrainPreviewValleyDefaults.Frequency;
		var freqStep = Math.Max( 0.5f, settings.ValleyAutoFrequencyStep );

		var weightMin = settings.EnableValleyOceanAutoWeight ? 0f : TerrainPreviewValleyDefaults.Weight;
		var weightMax = 2f;
		var weightStep = Math.Max( 0.001f, settings.ValleyOceanWeightStep );

		SolveResult? best = null;
		var tried = 0;

		for ( var frequency = freqMin; frequency <= freqMax + 0.0001f; frequency += freqStep )
		{
			if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
				break;

			for ( var weight = weightMin; weight <= weightMax + 0.0001f; weight += weightStep )
			{
				if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
					break;

				settings.ValleyFrequency = frequency;
				settings.ValleyWeight = weight;
				tried++;

				var snapshot = TerrainPreviewValleyAutoEvaluate.Measure( settings, backend );
				if ( !snapshot.SpawnAcceptableOk )
					continue;

				if ( snapshot.IsHardFail( settings ) )
					continue;

				if ( snapshot.IsSolved( settings ) )
				{
					return new SolveResult
					{
						Solved = true,
						Frequency = frequency,
						Weight = weight,
						CombinationsTried = tried,
						UnmetGoals = ValleyAutoUnmetGoal.None,
						LimitsHit = ValleyAutoLimitHit.None,
						Snapshot = snapshot,
					};
				}

				var candidate = new SolveResult
				{
					Solved = false,
					Frequency = frequency,
					Weight = weight,
					CombinationsTried = tried,
					UnmetGoals = snapshot.ComputeUnmet( settings ),
					LimitsHit = BuildLimitsHit( snapshot, settings, frequency, freqMax, weight, weightMax ),
					Snapshot = snapshot,
				};

				if ( best is null || IsBetter( candidate, best.Value, settings ) )
					best = candidate;
			}
		}

		var fallback = best ?? new SolveResult
		{
			Frequency = TerrainPreviewValleyDefaults.Frequency,
			Weight = TerrainPreviewValleyDefaults.Weight,
			Snapshot = TerrainPreviewValleyAutoEvaluate.Measure( settings, backend ),
		};

		fallback = fallback with
		{
			UnmetGoals = fallback.Snapshot.ComputeUnmet( settings ),
		};

		settings.ValleyFrequency = fallback.Frequency;
		settings.ValleyWeight = fallback.Weight;

		var limits = fallback.LimitsHit | ValleyAutoLimitHit.GridExhausted;
		if ( TerrainPreviewMapIterationTracker.TimedOut )
			limits |= ValleyAutoLimitHit.SearchTimedOut;
		if ( TerrainPreviewMapIterationTracker.IterationCapped )
			limits |= ValleyAutoLimitHit.SearchIterationCap;

		return new SolveResult
		{
			Solved = false,
			Frequency = fallback.Frequency,
			Weight = fallback.Weight,
			CombinationsTried = tried,
			UnmetGoals = fallback.UnmetGoals,
			LimitsHit = limits,
			Snapshot = fallback.Snapshot,
		};
	}

	static bool IsBetter( SolveResult candidate, SolveResult current, TerrainPreviewSettings settings )
	{
		var candidateScore = candidate.Snapshot.PriorityScore( settings );
		var currentScore = current.Snapshot.PriorityScore( settings );
		if ( candidateScore != currentScore )
			return candidateScore < currentScore;

		return candidate.Snapshot.TotalOceanFraction01 < current.Snapshot.TotalOceanFraction01;
	}

	static ValleyAutoLimitHit BuildLimitsHit(
		TerrainPreviewValleyAutoEvaluate.Snapshot snapshot,
		TerrainPreviewSettings settings,
		float frequency,
		float freqMax,
		float weight,
		float weightMax )
	{
		var limits = ValleyAutoLimitHit.GridExhausted;
		var unmet = snapshot.ComputeUnmet( settings );
		if ( unmet.HasFlag( ValleyAutoUnmetGoal.AbsoluteTotalOceanExceeded ) )
			limits |= ValleyAutoLimitHit.AbsoluteTotalOceanCap;
		if ( frequency >= freqMax - 0.0001f )
			limits |= ValleyAutoLimitHit.MaxValleyFrequency;
		if ( weight >= weightMax - 0.0001f )
			limits |= ValleyAutoLimitHit.MaxValleyWeight;

		return limits;
	}

	public static string FormatStatus( SolveResult result )
	{
		if ( result.CombinationsTried <= 0 )
			return null;

		if ( result.Solved )
			return $" · grid solved @ freq {result.Frequency:0.#} weight {result.Weight:0.###} ({result.CombinationsTried} tries)";

		return $" · grid failed ({result.CombinationsTried} tries) unmet: {TerrainPreviewValleyAutoLimits.FormatUnmetGoals( result.UnmetGoals )}"
			+ $" · limits: {TerrainPreviewValleyAutoLimits.FormatLimitsHit( result.LimitsHit )}"
			+ $" · spawn land {result.Snapshot.SpawnLandFraction01 * 100f:0.#}%"
			+ $" · total ocean {result.Snapshot.TotalOceanFraction01 * 100f:0.#}%";
	}
}
