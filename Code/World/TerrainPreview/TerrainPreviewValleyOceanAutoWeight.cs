namespace Survival;

/// <summary>
/// Adjusts <see cref="TerrainPreviewSettings.ValleyWeight"/> from the baseline each generate until
/// interior ocean is met while respecting spawn land (post-guard) and absolute total ocean cap.
/// Preferred total ocean (13%) may be exceeded to reach interior targets.
/// </summary>
public static class TerrainPreviewValleyOceanAutoWeight
{
	const float MaxValleyWeight = 2f;
	const float MinValleyWeight = 0f;
	const float OceanTargetEpsilon = 0.0001f;
	const int MaxAdjustIterations = 512;

	public readonly struct ResolveResult
	{
		public float StartingWeight { get; init; }
		public float ResolvedWeight { get; init; }
		public int StepsUp { get; init; }
		public int StepsDown { get; init; }
		public bool TargetMet { get; init; }
		public bool AutoSkipped { get; init; }
		public float OceanFraction01 { get; init; }
		public float InteriorOceanFraction01 { get; init; }
		public ValleyAutoUnmetGoal UnmetGoals { get; init; }
		public ValleyAutoLimitHit LimitsHit { get; init; }
	}

	public static ResolveResult Apply(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend = null,
		bool resetValleyWeight = true )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;

		var minInterior = Math.Clamp( settings.ValleyOceanAutoMinInteriorFraction01, 0f, 1f );
		var maxExterior = TerrainPreviewValleyAutoEvaluate.MaxExteriorOcean( settings );
		var (preferredMaxTotal, absoluteMaxTotal) = TerrainPreviewValleyAutoEvaluate.TotalOceanCaps( settings );

		if ( !settings.EnableValleyOceanAutoWeight || !settings.EnableValleyLayer )
		{
			var snapshot = TerrainPreviewGenerator.MeasureWaterCoverage( settings );
			return BuildResult(
				settings.ValleyWeight,
				settings.ValleyWeight,
				0,
				0,
				snapshot,
				minInterior,
				maxExterior,
				preferredMaxTotal,
				absoluteMaxTotal,
				autoSkipped: false,
				ValleyAutoLimitHit.None );
		}

		var startingWeight = resetValleyWeight
			? TerrainPreviewValleyDefaults.Weight
			: settings.ValleyWeight;
		if ( resetValleyWeight )
			settings.ValleyWeight = startingWeight;

		var step = Math.Max( 0.001f, settings.ValleyOceanWeightStep );
		var weight = startingWeight;
		var stepsUp = 0;
		var stepsDown = 0;
		var coverage = MeasureAt( settings, weight );

		if ( IsInTargetBand( coverage, minInterior, maxExterior, preferredMaxTotal, absoluteMaxTotal ) )
		{
			return BuildResult(
				startingWeight,
				weight,
				0,
				0,
				coverage,
				minInterior,
				maxExterior,
				preferredMaxTotal,
				absoluteMaxTotal,
				autoSkipped: true,
				ValleyAutoLimitHit.None );
		}

		var limitsHit = ValleyAutoLimitHit.None;
		var hitAbsoluteCapOnStepUp = false;

		for ( var i = 0; i < MaxAdjustIterations; i++ )
		{
			if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
				break;

			if ( coverage.IsAtOrAboveTotalOceanCap( absoluteMaxTotal ) && weight > MinValleyWeight + OceanTargetEpsilon )
			{
				weight = Math.Max( MinValleyWeight, weight - step );
				stepsDown++;
				coverage = MeasureAt( settings, weight );
				continue;
			}

			if ( coverage.IsBelowInteriorOceanTarget( minInterior )
				&& weight < MaxValleyWeight - OceanTargetEpsilon )
			{
				var nextWeight = Math.Min( MaxValleyWeight, weight + step );
				var probeCoverage = MeasureAt( settings, nextWeight );
				if ( probeCoverage.IsAtOrAboveTotalOceanCap( absoluteMaxTotal ) )
				{
					hitAbsoluteCapOnStepUp = true;
					break;
				}

				if ( probeCoverage.IsAtOrAboveExteriorOceanCap( maxExterior ) )
					break;

				if ( !TerrainPreviewSpawnLandCheck.MeetsAcceptableSpawnTarget( settings, backend ) )
				{
					settings.ValleyWeight = weight;
					break;
				}

				weight = nextWeight;
				stepsUp++;
				coverage = probeCoverage;
				continue;
			}

			if ( !coverage.IsBelowInteriorOceanTarget( minInterior )
				&& coverage.IsAtOrAboveTotalOceanCap( preferredMaxTotal )
				&& weight > MinValleyWeight + OceanTargetEpsilon )
			{
				weight = Math.Max( MinValleyWeight, weight - step );
				stepsDown++;
				coverage = MeasureAt( settings, weight );
				continue;
			}

			break;
		}

		if ( stepsUp + stepsDown >= MaxAdjustIterations )
			limitsHit |= ValleyAutoLimitHit.GreedyIterationCap;
		if ( TerrainPreviewMapIterationTracker.TimedOut )
			limitsHit |= ValleyAutoLimitHit.SearchTimedOut;
		if ( TerrainPreviewMapIterationTracker.IterationCapped )
			limitsHit |= ValleyAutoLimitHit.SearchIterationCap;

		settings.ValleyWeight = weight;

		var inBand = IsInTargetBand( coverage, minInterior, maxExterior, preferredMaxTotal, absoluteMaxTotal );
		if ( !inBand )
			limitsHit |= ComputeLimitsHit( coverage, weight, minInterior, maxExterior, preferredMaxTotal, absoluteMaxTotal, hitAbsoluteCapOnStepUp );

		return BuildResult(
			startingWeight,
			weight,
			stepsUp,
			stepsDown,
			coverage,
			minInterior,
			maxExterior,
			preferredMaxTotal,
			absoluteMaxTotal,
			autoSkipped: false,
			limitsHit );
	}

	static ValleyAutoLimitHit ComputeLimitsHit(
		TerrainPreviewWaterCoverageStats coverage,
		float weight,
		float minInterior,
		float maxExterior,
		float preferredMaxTotal,
		float absoluteMaxTotal,
		bool hitAbsoluteCapOnStepUp )
	{
		var limits = ValleyAutoLimitHit.None;

		if ( hitAbsoluteCapOnStepUp
			|| (coverage.IsBelowInteriorOceanTarget( minInterior )
				&& coverage.IsAtOrAboveTotalOceanCap( absoluteMaxTotal ) ) )
			limits |= ValleyAutoLimitHit.AbsoluteTotalOceanCap;

		if ( coverage.IsBelowInteriorOceanTarget( minInterior )
			&& coverage.IsAtOrAboveExteriorOceanCap( maxExterior ) )
			limits |= ValleyAutoLimitHit.MaxExteriorOcean;

		if ( coverage.IsBelowInteriorOceanTarget( minInterior )
			&& weight >= MaxValleyWeight - OceanTargetEpsilon )
			limits |= ValleyAutoLimitHit.MaxValleyWeight;

		if ( coverage.IsAtOrAboveTotalOceanCap( absoluteMaxTotal ) && weight <= MinValleyWeight + OceanTargetEpsilon )
			limits |= ValleyAutoLimitHit.MinValleyWeight;

		if ( !coverage.IsBelowInteriorOceanTarget( minInterior )
			&& coverage.IsAtOrAboveTotalOceanCap( preferredMaxTotal ) )
			limits |= ValleyAutoLimitHit.TotalOceanCap;

		return limits;
	}

	static bool IsInTargetBand(
		TerrainPreviewWaterCoverageStats coverage,
		float minInterior,
		float maxExterior,
		float preferredMaxTotal,
		float absoluteMaxTotal )
		=> !coverage.IsBelowInteriorOceanTarget( minInterior )
			&& !coverage.IsAtOrAboveExteriorOceanCap( maxExterior )
			&& !coverage.IsAtOrAboveTotalOceanCap( absoluteMaxTotal );

	static TerrainPreviewWaterCoverageStats MeasureAt(
		TerrainPreviewSettings settings,
		float weight,
		ITerrainPreviewBackend backend = null )
	{
		settings.ValleyWeight = weight;
		return TerrainPreviewGenerator.MeasureWaterCoverage( settings );
	}

	static ResolveResult BuildResult(
		float startingWeight,
		float resolvedWeight,
		int stepsUp,
		int stepsDown,
		TerrainPreviewWaterCoverageStats coverage,
		float minInterior,
		float maxExterior,
		float preferredMaxTotal,
		float absoluteMaxTotal,
		bool autoSkipped,
		ValleyAutoLimitHit limitsHit )
	{
		var inBand = IsInTargetBand( coverage, minInterior, maxExterior, preferredMaxTotal, absoluteMaxTotal );
		var unmet = ValleyAutoUnmetGoal.None;
		if ( coverage.IsBelowInteriorOceanTarget( minInterior ) )
			unmet |= ValleyAutoUnmetGoal.InteriorOcean;
		if ( coverage.IsAtOrAboveExteriorOceanCap( maxExterior ) )
			unmet |= ValleyAutoUnmetGoal.ExteriorOceanExceeded;
		if ( coverage.IsAtOrAboveTotalOceanCap( absoluteMaxTotal ) )
			unmet |= ValleyAutoUnmetGoal.AbsoluteTotalOceanExceeded;

		return new ResolveResult
		{
			StartingWeight = startingWeight,
			ResolvedWeight = resolvedWeight,
			StepsUp = stepsUp,
			StepsDown = stepsDown,
			OceanFraction01 = coverage.OceanFraction01,
			InteriorOceanFraction01 = coverage.InteriorOceanFraction01,
			AutoSkipped = autoSkipped,
			TargetMet = inBand,
			UnmetGoals = unmet,
			LimitsHit = limitsHit,
		};
	}

	public static string FormatStatus(
		ResolveResult result,
		float minInteriorFraction01,
		float preferredMaxTotalFraction01,
		float absoluteMaxTotalFraction01 )
	{
		if ( result.AutoSkipped )
			return $" · water ok (total {result.OceanFraction01 * 100f:0.#}% · interior {result.InteriorOceanFraction01 * 100f:0.#}%)";

		if ( result.StepsUp == 0 && result.StepsDown == 0
			&& MathF.Abs( result.StartingWeight - result.ResolvedWeight ) < 0.0005f )
			return null;

		var adjust = result.StepsUp > 0 && result.StepsDown > 0
			? $"(+{result.StepsUp}/−{result.StepsDown})"
			: result.StepsUp > 0
				? $"(+{result.StepsUp})"
				: result.StepsDown > 0
					? $"(−{result.StepsDown})"
					: "";

		var arrow = $" · valley weight {result.StartingWeight:0.###}→{result.ResolvedWeight:0.###} {adjust}".TrimEnd();
		arrow += $" · total {result.OceanFraction01 * 100f:0.#}%";

		if ( !result.TargetMet )
		{
			if ( result.UnmetGoals.HasFlag( ValleyAutoUnmetGoal.InteriorOcean ) )
				arrow += $" · need {minInteriorFraction01 * 100f:0.#}% interior";
			if ( result.UnmetGoals.HasFlag( ValleyAutoUnmetGoal.AbsoluteTotalOceanExceeded ) )
				arrow += $" · over {absoluteMaxTotalFraction01 * 100f:0.#}% max";
			else if ( result.UnmetGoals.HasFlag( ValleyAutoUnmetGoal.TotalOceanTooHigh ) )
				arrow += $" · over {preferredMaxTotalFraction01 * 100f:0.#}% preferred";
		}

		return arrow;
	}
}
