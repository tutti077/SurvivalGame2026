namespace Survival;

/// <summary>Phase 2 — raises interior-water carve weight until interior ocean target or a cap is hit.</summary>
public static class TerrainPreviewValleyInteriorWaterAuto
{
	const float MaxInteriorWaterWeight = 1.5f;
	const float OceanTargetEpsilon = 0.0001f;
	const int MaxAdjustIterations = 128;

	public readonly struct ResolveResult
	{
		public float StartingWeight { get; init; }
		public float ResolvedWeight { get; init; }
		public int StepsUp { get; init; }
		public bool TargetMet { get; init; }
		public bool AutoSkipped { get; init; }
		public float InteriorOceanFraction01 { get; init; }
		public float ExteriorOceanFraction01 { get; init; }
		public ValleyAutoUnmetGoal UnmetGoals { get; init; }
		public ValleyAutoLimitHit LimitsHit { get; init; }
	}

	public static ResolveResult Apply( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;

		var minInterior = Math.Clamp( settings.ValleyOceanAutoMinInteriorFraction01, 0f, 1f );
		var maxExterior = Math.Clamp( settings.ValleyOceanMaxExteriorFraction01, 0.05f, 0.75f );
		var absoluteMaxTotal = TerrainPreviewValleyAutoEvaluate.AbsoluteMaxTotalOcean( settings );

		if ( !settings.EnableInteriorWaterLayer || !settings.EnableValleyLayer )
		{
			var skippedCoverage = TerrainPreviewGenerator.MeasureWaterCoverage( settings );
			return BuildResult( 0f, 0f, 0, skippedCoverage, minInterior, maxExterior, autoSkipped: true, ValleyAutoLimitHit.None );
		}

		settings.InteriorWaterWeight = 0f;
		var startingWeight = 0f;
		var step = Math.Max( 0.01f, settings.InteriorWaterAutoStep );
		var weight = 0f;
		var stepsUp = 0;
		var coverage = MeasureAt( settings, weight );
		var limitsHit = ValleyAutoLimitHit.None;

		if ( !coverage.IsBelowInteriorOceanTarget( minInterior ) )
			return BuildResult( startingWeight, weight, 0, coverage, minInterior, maxExterior, autoSkipped: true, limitsHit );

		for ( var i = 0; i < MaxAdjustIterations; i++ )
		{
			if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
				break;

			if ( !coverage.IsBelowInteriorOceanTarget( minInterior ) )
				break;

			if ( weight >= MaxInteriorWaterWeight - OceanTargetEpsilon )
			{
				limitsHit |= ValleyAutoLimitHit.MaxInteriorWaterWeight;
				break;
			}

			var nextWeight = Math.Min( MaxInteriorWaterWeight, weight + step );
			var probeCoverage = MeasureAt( settings, nextWeight );

			if ( probeCoverage.IsAtOrAboveTotalOceanCap( absoluteMaxTotal ) )
			{
				limitsHit |= ValleyAutoLimitHit.AbsoluteTotalOceanCap;
				break;
			}

			if ( probeCoverage.IsAtOrAboveExteriorOceanCap( maxExterior ) )
			{
				limitsHit |= ValleyAutoLimitHit.MaxExteriorOcean;
				break;
			}

			if ( !TerrainPreviewSpawnLandCheck.MeetsAcceptableSpawnTarget( settings, backend ) )
				break;

			weight = nextWeight;
			stepsUp++;
			coverage = probeCoverage;
		}

		if ( stepsUp >= MaxAdjustIterations )
			limitsHit |= ValleyAutoLimitHit.GreedyIterationCap;
		if ( TerrainPreviewMapIterationTracker.TimedOut )
			limitsHit |= ValleyAutoLimitHit.SearchTimedOut;
		if ( TerrainPreviewMapIterationTracker.IterationCapped )
			limitsHit |= ValleyAutoLimitHit.SearchIterationCap;

		settings.InteriorWaterWeight = weight;

		return BuildResult( startingWeight, weight, stepsUp, coverage, minInterior, maxExterior, autoSkipped: false, limitsHit );
	}

	static TerrainPreviewWaterCoverageStats MeasureAt( TerrainPreviewSettings settings, float weight )
	{
		settings.InteriorWaterWeight = weight;
		return TerrainPreviewGenerator.MeasureWaterCoverage( settings );
	}

	static ResolveResult BuildResult(
		float startingWeight,
		float resolvedWeight,
		int stepsUp,
		TerrainPreviewWaterCoverageStats coverage,
		float minInterior,
		float maxExterior,
		bool autoSkipped,
		ValleyAutoLimitHit limitsHit )
	{
		var unmet = ValleyAutoUnmetGoal.None;
		if ( coverage.IsBelowInteriorOceanTarget( minInterior ) )
			unmet |= ValleyAutoUnmetGoal.InteriorOcean;
		if ( coverage.IsAtOrAboveExteriorOceanCap( maxExterior ) )
			unmet |= ValleyAutoUnmetGoal.ExteriorOceanExceeded;

		return new ResolveResult
		{
			StartingWeight = startingWeight,
			ResolvedWeight = resolvedWeight,
			StepsUp = stepsUp,
			InteriorOceanFraction01 = coverage.InteriorOceanFraction01,
			ExteriorOceanFraction01 = coverage.ExteriorOceanFraction01,
			AutoSkipped = autoSkipped,
			TargetMet = !coverage.IsBelowInteriorOceanTarget( minInterior ),
			UnmetGoals = unmet,
			LimitsHit = limitsHit,
		};
	}

	public static string FormatStatus( ResolveResult result, float minInteriorFraction01 )
	{
		if ( result.AutoSkipped )
			return $" · interior water ok ({result.InteriorOceanFraction01 * 100f:0.#}%)";

		if ( result.StepsUp == 0 && result.ResolvedWeight < 0.0005f )
			return null;

		var line = result.StepsUp > 0
			? $" · interior water {result.StartingWeight:0.###}→{result.ResolvedWeight:0.###} (+{result.StepsUp})"
			: $" · interior water {result.ResolvedWeight:0.###}";
		line += $" · interior {result.InteriorOceanFraction01 * 100f:0.#}% · rim {result.ExteriorOceanFraction01 * 100f:0.#}%";

		if ( !result.TargetMet )
			line += $" · need {minInteriorFraction01 * 100f:0.#}% interior";

		return line;
	}
}
