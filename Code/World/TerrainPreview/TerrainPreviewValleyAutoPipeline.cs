namespace Survival;

public static class TerrainPreviewValleyAutoPipeline
{
	public readonly struct RunResult
	{
		public bool Solved { get; init; }
		public bool SeedRejected { get; init; }
		public bool SearchTimedOut { get; init; }
		public bool SearchIterationCapped { get; init; }
		public bool HardFail { get; init; }
		public bool RevertedToLandCheckpoint { get; init; }
		public int SeedsAttempted { get; init; }
		public bool UsedGridSearch { get; init; }
		public int GridCombinationsTried { get; init; }
		public ValleyAutoUnmetGoal UnmetGoals { get; init; }
		public ValleyAutoLimitHit LimitsHit { get; init; }
		public TerrainPreviewValleyAutoEvaluate.Snapshot Snapshot { get; init; }
		public TerrainPreviewValleySpawnAutoFrequency.ResolveResult Frequency { get; init; }
		public TerrainPreviewValleyInteriorWaterAuto.ResolveResult InteriorWater { get; init; }
		public TerrainPreviewValleyOceanAutoWeight.ResolveResult Weight { get; init; }
		public TerrainPreviewValleySpawnGuard.GuardResult SpawnGuard { get; init; }
		public TerrainPreviewValleyAutoGridSearch.SolveResult Grid { get; init; }
	}

	public static RunResult Run( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;
		if ( !TerrainPreviewValleyAutoEvaluate.AutoActive( settings ) )
		{
			var snapshot = TerrainPreviewValleyAutoEvaluate.Measure( settings, backend );
			return new RunResult { Solved = true, Snapshot = snapshot, SeedsAttempted = 1 };
		}

		var retrySeeds = settings.RetrySeedsUntilSolved;
		var maxSeedAttempts = Math.Clamp( settings.ValleyAutoMaxSeedAttempts, 1, 256 );
		var timeout = Math.Max( 0f, settings.ValleyAutoSearchTimeoutSeconds );
		var maxIterations = Math.Max( 1, settings.ValleyAutoMaxIterationsPerSeed );
		RunResult last = default;

		TerrainPreviewMapIterationTracker.ResetTotal();
		TerrainPreviewMapIterationTracker.BeginSeedSearch( maxSeedAttempts );

		for ( var attempt = 0; attempt < maxSeedAttempts; attempt++ )
		{
			if ( attempt > 0 )
				settings.WorldSeed++;

			TerrainPreviewMapIterationTracker.NotifySeedAttempt( attempt + 1 );

			last = RunSingleSeed( settings, backend, timeout, maxIterations )
				with { SeedsAttempted = attempt + 1 };

			if ( last.Solved )
				return last with { SeedRejected = false };

			if ( !retrySeeds )
				return FinalizeRunResult( last, settings, seedsExhausted: true );
		}

		return FinalizeRunResult( last, settings, seedsExhausted: true );
	}

	static RunResult FinalizeRunResult(
		RunResult last,
		TerrainPreviewSettings settings,
		bool seedsExhausted )
	{
		var reject = settings.RejectSeedOnAutoFailure
			&& !last.Solved
			&& seedsExhausted;

		return last with { SeedRejected = reject };
	}

	static RunResult RunSingleSeed(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float timeoutSeconds,
		int maxIterations )
	{
		if ( TerrainPreviewValleyAutoEvaluate.AutoActive( settings ) )
			TerrainPreviewValleyDefaults.ResetAutoBaselines( settings );

		TerrainPreviewValleyAutoGridSearch.SolveResult grid = default;
		TerrainPreviewValleySpawnAutoFrequency.ResolveResult frequency = default;
		TerrainPreviewValleyInteriorWaterAuto.ResolveResult interiorWater = default;
		TerrainPreviewValleyOceanAutoWeight.ResolveResult weight = default;
		var usedGrid = false;
		var timedOut = false;
		var iterationCapped = false;
		var revertedToLand = false;
		TerrainPreviewAutoTuneCheckpoint? landCheckpoint = null;

		using ( TerrainPreviewMapIterationTracker.BeginSession( timeoutSeconds, maxIterations ) )
		using ( TerrainPreviewAutoTuneScope.Begin( settings ) )
		{
			if ( settings.EnableValleyAutoExhaustiveSearch )
			{
				usedGrid = true;
				grid = TerrainPreviewValleyAutoGridSearch.Solve( settings, backend );
			}
			else
			{
				TerrainPreviewValleySpawnGuard.Ensure( settings, backend );

				frequency = settings.EnableValleySpawnAutoFrequency
					? TerrainPreviewValleySpawnAutoFrequency.Apply( settings, backend )
					: default;

				if ( !TerrainPreviewMapIterationTracker.IsAbortRequested )
					TerrainPreviewValleySpawnGuard.Ensure( settings, backend );

				if ( TerrainPreviewSpawnLandCheck.MeetsAcceptableSpawnTarget( settings, backend ) )
					landCheckpoint = TerrainPreviewAutoTuneCheckpoint.Capture( settings );

				if ( landCheckpoint.HasValue && !TerrainPreviewMapIterationTracker.IsAbortRequested )
				{
					if ( settings.EnableInteriorWaterLayer )
						interiorWater = TerrainPreviewValleyInteriorWaterAuto.Apply( settings, backend );

					if ( !TerrainPreviewMapIterationTracker.IsAbortRequested
						&& settings.EnableValleyOceanAutoWeight )
						weight = TerrainPreviewValleyOceanAutoWeight.Apply( settings, backend, resetValleyWeight: false );
				}
			}

			timedOut = TerrainPreviewMapIterationTracker.TimedOut;
			iterationCapped = TerrainPreviewMapIterationTracker.IterationCapped;
		}

		var spawnGuard = BuildFinalSpawnGuard( settings, backend );
		var snapshot = TerrainPreviewValleyAutoEvaluate.Measure( settings, backend );
		var solved = snapshot.IsSolved( settings );
		var hardFail = snapshot.IsHardFail( settings );

		if ( !solved && landCheckpoint.HasValue && !hardFail )
		{
			landCheckpoint.Value.Restore( settings );
			revertedToLand = true;
			spawnGuard = BuildFinalSpawnGuard( settings, backend );
			snapshot = TerrainPreviewValleyAutoEvaluate.Measure( settings, backend );
			solved = snapshot.IsSolved( settings );
			hardFail = snapshot.IsHardFail( settings );
		}

		var unmet = snapshot.ComputeUnmet( settings );
		var limits = spawnGuard.LimitsHit;
		if ( timedOut )
			limits |= ValleyAutoLimitHit.SearchTimedOut;
		if ( iterationCapped )
			limits |= ValleyAutoLimitHit.SearchIterationCap;
		limits |= frequency.LimitsHit | interiorWater.LimitsHit | weight.LimitsHit;

		if ( usedGrid )
		{
			limits |= grid.LimitsHit;
			return new RunResult
			{
				Solved = solved,
				SeedRejected = false,
				SearchTimedOut = timedOut,
				SearchIterationCapped = iterationCapped,
				HardFail = hardFail,
				RevertedToLandCheckpoint = revertedToLand,
				UsedGridSearch = true,
				GridCombinationsTried = grid.CombinationsTried,
				UnmetGoals = unmet,
				LimitsHit = limits,
				Snapshot = snapshot,
				Grid = grid,
				SpawnGuard = spawnGuard,
			};
		}

		return new RunResult
		{
			Solved = solved,
			SeedRejected = false,
			SearchTimedOut = timedOut,
			SearchIterationCapped = iterationCapped,
			HardFail = hardFail,
			RevertedToLandCheckpoint = revertedToLand,
			UsedGridSearch = false,
			UnmetGoals = unmet,
			LimitsHit = limits,
			Snapshot = snapshot,
			Frequency = frequency,
			InteriorWater = interiorWater,
			Weight = weight,
			SpawnGuard = spawnGuard,
		};
	}

	static TerrainPreviewValleySpawnGuard.GuardResult BuildFinalSpawnGuard(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend )
	{
		if ( TerrainPreviewSpawnLandCheck.MeetsAcceptableSpawnTarget( settings, backend ) )
		{
			var radius = Math.Max( 5f, settings.ValleySpawnLandRadiusMeters );
			var spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
			return new TerrainPreviewValleySpawnGuard.GuardResult
			{
				SpawnLandOk = true,
				SpawnLandFraction01 = spawnLand.LandFraction01,
			};
		}

		return TerrainPreviewValleySpawnGuard.Ensure( settings, backend );
	}

	public static string FormatStatus(
		RunResult result,
		TerrainPreviewSettings settings,
		TerrainPreviewWaterCoverageStats coverage )
		=> TerrainPreviewValleyAutoRunStats.FormatColumnText(
			TerrainPreviewValleyAutoRunStats.Build( result, settings, coverage ) );
}
