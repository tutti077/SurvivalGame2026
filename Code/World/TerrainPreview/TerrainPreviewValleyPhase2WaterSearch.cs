namespace Survival;

/// <summary>
/// Phase 2 water search — alternates interior-water and valley-weight steps until solved or the per-seed iteration budget is spent.
/// </summary>
public static class TerrainPreviewValleyPhase2WaterSearch
{
	const float MaxInteriorWaterWeight = 1.5f;
	const float MaxValleyWeight = 2f;
	const float MinValleyWeight = 0f;
	const float OceanTargetEpsilon = 0.0001f;
	const float MinInteriorStep = 0.0025f;

	public readonly struct Result
	{
		public TerrainPreviewValleyInteriorWaterAuto.ResolveResult InteriorWater { get; init; }
		public TerrainPreviewValleyOceanAutoWeight.ResolveResult Weight { get; init; }
		public ValleyAutoLimitHit LimitsHit { get; init; }
	}

	public static Result Search( TerrainPreviewSettings settings, ITerrainPreviewBackend backend )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;

		var minInterior = Math.Clamp( settings.ValleyOceanAutoMinInteriorFraction01, 0f, 1f );
		var maxExterior = TerrainPreviewValleyAutoEvaluate.MaxExteriorOcean( settings );
		var (preferredMaxTotal, absoluteMaxTotal) = TerrainPreviewValleyAutoEvaluate.TotalOceanCaps( settings );
		var wantsInterior = settings.EnableInteriorWaterLayer || settings.EnableValleyOceanAutoWeight;

		if ( !wantsInterior || !settings.EnableValleyLayer )
			return default;

		var startInteriorWeight = 0f;
		var startValleyWeight = settings.ValleyWeight;
		settings.InteriorWaterWeight = 0f;

		var interiorWeight = 0f;
		var valleyWeight = startValleyWeight;
		var interiorStep = Math.Max( MinInteriorStep, settings.InteriorWaterAutoStep );
		var weightStep = Math.Max( 0.001f, settings.ValleyOceanWeightStep );
		var interiorStepsUp = 0;
		var weightStepsUp = 0;
		var limitsHit = ValleyAutoLimitHit.None;

		var coverage = Measure( settings );
		if ( IsWaterBandMet( coverage, settings, minInterior, maxExterior, absoluteMaxTotal ) )
		{
			return BuildResult(
				settings,
				startInteriorWeight,
				interiorWeight,
				startValleyWeight,
				valleyWeight,
				interiorStepsUp,
				weightStepsUp,
				coverage,
				minInterior,
				maxExterior,
				preferredMaxTotal,
				absoluteMaxTotal,
				limitsHit );
		}

		while ( !TerrainPreviewMapIterationTracker.IsAbortRequested )
		{
			if ( IsWaterBandMet( coverage, settings, minInterior, maxExterior, absoluteMaxTotal ) )
				break;

			var progressed = false;

			if ( settings.EnableInteriorWaterLayer
				&& coverage.IsBelowInteriorOceanTarget( minInterior )
				&& interiorWeight < MaxInteriorWaterWeight - OceanTargetEpsilon )
			{
				progressed = TryStepInterior(
					settings,
					backend,
					ref coverage,
					ref interiorWeight,
					ref interiorStep,
					ref interiorStepsUp,
					maxExterior,
					absoluteMaxTotal,
					ref limitsHit );
			}

			if ( !progressed
				&& settings.EnableValleyOceanAutoWeight
				&& coverage.IsBelowInteriorOceanTarget( minInterior )
				&& valleyWeight < MaxValleyWeight - OceanTargetEpsilon )
			{
				progressed = TryStepValleyWeight(
					settings,
					backend,
					ref coverage,
					ref valleyWeight,
					weightStep,
					ref weightStepsUp,
					maxExterior,
					absoluteMaxTotal,
					ref limitsHit );
			}

			if ( !progressed
				&& settings.EnableValleyOceanAutoWeight
				&& !coverage.IsBelowInteriorOceanTarget( minInterior )
				&& coverage.IsAtOrAboveTotalOceanCap( preferredMaxTotal )
				&& valleyWeight > MinValleyWeight + OceanTargetEpsilon )
			{
				valleyWeight = Math.Max( MinValleyWeight, valleyWeight - weightStep );
				settings.ValleyWeight = valleyWeight;
				coverage = Measure( settings );
				progressed = true;
			}

			if ( !progressed )
				break;
		}

		if ( TerrainPreviewMapIterationTracker.TimedOut )
			limitsHit |= ValleyAutoLimitHit.SearchTimedOut;
		if ( TerrainPreviewMapIterationTracker.IterationCapped )
			limitsHit |= ValleyAutoLimitHit.SearchIterationCap;

		if ( interiorWeight >= MaxInteriorWaterWeight - OceanTargetEpsilon
			&& coverage.IsBelowInteriorOceanTarget( minInterior ) )
			limitsHit |= ValleyAutoLimitHit.MaxInteriorWaterWeight;

		if ( valleyWeight >= MaxValleyWeight - OceanTargetEpsilon
			&& coverage.IsBelowInteriorOceanTarget( minInterior ) )
			limitsHit |= ValleyAutoLimitHit.MaxValleyWeight;

		return BuildResult(
			settings,
			startInteriorWeight,
			interiorWeight,
			startValleyWeight,
			valleyWeight,
			interiorStepsUp,
			weightStepsUp,
			coverage,
			minInterior,
			maxExterior,
			preferredMaxTotal,
			absoluteMaxTotal,
			limitsHit );
	}

	static bool TryStepInterior(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		ref TerrainPreviewWaterCoverageStats coverage,
		ref float interiorWeight,
		ref float interiorStep,
		ref int interiorStepsUp,
		float maxExterior,
		float absoluteMaxTotal,
		ref ValleyAutoLimitHit limitsHit )
	{
		var step = interiorStep;
		for ( var shrink = 0; shrink < 4; shrink++ )
		{
			if ( step < MinInteriorStep )
				return false;

			var nextWeight = Math.Min( MaxInteriorWaterWeight, interiorWeight + step );
			if ( nextWeight <= interiorWeight + OceanTargetEpsilon )
				return false;

			var previousInterior = settings.InteriorWaterWeight;
			settings.InteriorWaterWeight = nextWeight;
			var probe = Measure( settings );

			if ( probe.IsAtOrAboveTotalOceanCap( absoluteMaxTotal ) )
			{
				settings.InteriorWaterWeight = previousInterior;
				limitsHit |= ValleyAutoLimitHit.AbsoluteTotalOceanCap;
				return false;
			}

			if ( probe.IsAtOrAboveExteriorOceanCap( maxExterior ) )
			{
				settings.InteriorWaterWeight = previousInterior;
				limitsHit |= ValleyAutoLimitHit.MaxExteriorOcean;
				step *= 0.5f;
				interiorStep = step;
				continue;
			}

			if ( !TerrainPreviewSpawnLandCheck.MeetsAcceptableSpawnTarget( settings, backend ) )
			{
				settings.InteriorWaterWeight = previousInterior;
				step *= 0.5f;
				interiorStep = step;
				continue;
			}

			interiorWeight = nextWeight;
			coverage = probe;
			interiorStepsUp++;
			return true;
		}

		return false;
	}

	static bool TryStepValleyWeight(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		ref TerrainPreviewWaterCoverageStats coverage,
		ref float valleyWeight,
		float weightStep,
		ref int weightStepsUp,
		float maxExterior,
		float absoluteMaxTotal,
		ref ValleyAutoLimitHit limitsHit )
	{
		var nextWeight = Math.Min( MaxValleyWeight, valleyWeight + weightStep );
		if ( nextWeight <= valleyWeight + OceanTargetEpsilon )
			return false;

		var previousValley = settings.ValleyWeight;
		settings.ValleyWeight = nextWeight;
		var probe = Measure( settings );

		if ( probe.IsAtOrAboveTotalOceanCap( absoluteMaxTotal ) )
		{
			settings.ValleyWeight = previousValley;
			limitsHit |= ValleyAutoLimitHit.AbsoluteTotalOceanCap;
			return false;
		}

		if ( probe.IsAtOrAboveExteriorOceanCap( maxExterior ) )
		{
			settings.ValleyWeight = previousValley;
			limitsHit |= ValleyAutoLimitHit.MaxExteriorOcean;
			return false;
		}

		if ( !TerrainPreviewSpawnLandCheck.MeetsAcceptableSpawnTarget( settings, backend ) )
		{
			settings.ValleyWeight = previousValley;
			return false;
		}

		valleyWeight = nextWeight;
		coverage = probe;
		weightStepsUp++;
		return true;
	}

	static bool IsWaterBandMet(
		TerrainPreviewWaterCoverageStats coverage,
		TerrainPreviewSettings settings,
		float minInterior,
		float maxExterior,
		float absoluteMaxTotal )
	{
		if ( coverage.IsBelowInteriorOceanTarget( minInterior ) )
			return false;
		if ( coverage.IsAtOrAboveExteriorOceanCap( maxExterior ) )
			return false;
		if ( coverage.IsAtOrAboveTotalOceanCap( absoluteMaxTotal ) )
			return false;
		if ( !TerrainPreviewSpawnLandCheck.MeetsAcceptableSpawnTarget( settings ) )
			return false;

		return true;
	}

	static TerrainPreviewWaterCoverageStats Measure( TerrainPreviewSettings settings )
		=> TerrainPreviewGenerator.MeasureWaterCoverage( settings );

	static Result BuildResult(
		TerrainPreviewSettings settings,
		float startInteriorWeight,
		float interiorWeight,
		float startValleyWeight,
		float valleyWeight,
		int interiorStepsUp,
		int weightStepsUp,
		TerrainPreviewWaterCoverageStats coverage,
		float minInterior,
		float maxExterior,
		float preferredMaxTotal,
		float absoluteMaxTotal,
		ValleyAutoLimitHit limitsHit )
	{
		settings.InteriorWaterWeight = interiorWeight;
		settings.ValleyWeight = valleyWeight;

		var interior = new TerrainPreviewValleyInteriorWaterAuto.ResolveResult
		{
			StartingWeight = startInteriorWeight,
			ResolvedWeight = interiorWeight,
			StepsUp = interiorStepsUp,
			InteriorOceanFraction01 = coverage.InteriorOceanFraction01,
			ExteriorOceanFraction01 = coverage.ExteriorOceanFraction01,
			AutoSkipped = interiorStepsUp == 0 && MathF.Abs( interiorWeight - startInteriorWeight ) < 0.0005f,
			TargetMet = !coverage.IsBelowInteriorOceanTarget( minInterior ),
			UnmetGoals = coverage.IsBelowInteriorOceanTarget( minInterior )
				? ValleyAutoUnmetGoal.InteriorOcean
				: ValleyAutoUnmetGoal.None,
			LimitsHit = limitsHit,
		};

		var inBand = !coverage.IsBelowInteriorOceanTarget( minInterior )
			&& !coverage.IsAtOrAboveExteriorOceanCap( maxExterior )
			&& !coverage.IsAtOrAboveTotalOceanCap( absoluteMaxTotal );
		var weightUnmet = ValleyAutoUnmetGoal.None;
		if ( coverage.IsBelowInteriorOceanTarget( minInterior ) )
			weightUnmet |= ValleyAutoUnmetGoal.InteriorOcean;
		if ( coverage.IsAtOrAboveExteriorOceanCap( maxExterior ) )
			weightUnmet |= ValleyAutoUnmetGoal.ExteriorOceanExceeded;
		if ( coverage.IsAtOrAboveTotalOceanCap( absoluteMaxTotal ) )
			weightUnmet |= ValleyAutoUnmetGoal.AbsoluteTotalOceanExceeded;

		var weight = new TerrainPreviewValleyOceanAutoWeight.ResolveResult
		{
			StartingWeight = startValleyWeight,
			ResolvedWeight = valleyWeight,
			StepsUp = weightStepsUp,
			StepsDown = 0,
			OceanFraction01 = coverage.OceanFraction01,
			InteriorOceanFraction01 = coverage.InteriorOceanFraction01,
			AutoSkipped = weightStepsUp == 0 && MathF.Abs( valleyWeight - startValleyWeight ) < 0.0005f,
			TargetMet = inBand,
			UnmetGoals = weightUnmet,
			LimitsHit = limitsHit,
		};

		return new Result
		{
			InteriorWater = interior,
			Weight = weight,
			LimitsHit = limitsHit,
		};
	}
}
