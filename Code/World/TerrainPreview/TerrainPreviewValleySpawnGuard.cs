namespace Survival;

/// <summary>
/// Post-tuning spawn protection — raises land layers and lowers valley carve until spawn disk is dry.
/// Runs even when auto-frequency is off; spawn land outranks ocean targets.
/// </summary>
public static class TerrainPreviewValleySpawnGuard
{
	const float MaxValleyWeight = 2f;
	const float MinValleyWeight = 0f;
	const float MaxLayerWeight = 2f;
	const float SpawnFrequencyMax = 64f;
	const float HillStep = 0.05f;
	const float ContinentalStep = 0.05f;
	const int MaxAdjustIterations = 192;

	public readonly struct GuardResult
	{
		public bool SpawnLandOk { get; init; }
		public bool SpawnEscapeOk { get; init; }
		public float SpawnLandFraction01 { get; init; }
		public float SpawnEscapeBestLandMeters { get; init; }
		public int FrequencyStepsUp { get; init; }
		public int WeightStepsDown { get; init; }
		public int InteriorWaterStepsDown { get; init; }
		public int HillStepsUp { get; init; }
		public int ContinentalStepsUp { get; init; }
		public ValleyAutoLimitHit LimitsHit { get; init; }
	}

	public static GuardResult Ensure( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;

		if ( !settings.EnableValleyLayer )
			return new GuardResult { SpawnLandOk = true, SpawnEscapeOk = true };

		var minLand = TerrainPreviewValleyAutoEvaluate.SpawnAcceptableLand( settings );
		var radius = Math.Max( 5f, settings.ValleySpawnLandRadiusMeters );
		var spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
		var spawnEscape = TerrainPreviewSpawnLandEscapeCheck.Measure( settings, backend );
		if ( spawnLand.MeetsLandTarget( minLand ) && spawnEscape.HasEscape )
		{
			return new GuardResult
			{
				SpawnLandOk = true,
				SpawnEscapeOk = true,
				SpawnLandFraction01 = spawnLand.LandFraction01,
				SpawnEscapeBestLandMeters = spawnEscape.BestContinuousLandMeters,
			};
		}

		var limitsHit = ValleyAutoLimitHit.None;
		var freqSteps = 0;
		var weightSteps = 0;
		var interiorWaterSteps = 0;
		var hillSteps = 0;
		var continentalSteps = 0;
		var freqStep = Math.Max( 0.5f, settings.ValleyAutoFrequencyStep );
		var weightStep = Math.Max( 0.001f, settings.ValleyOceanWeightStep );
		var interiorWaterStep = Math.Max( 0.01f, settings.InteriorWaterAutoStep );
		var frequency = settings.ValleyFrequency;
		var weight = settings.ValleyWeight;
		var interiorWaterWeight = settings.InteriorWaterWeight;
		var hillWeight = settings.HillWeight;
		var continentalWeight = settings.ContinentalWeight;

		for ( var i = 0; i < MaxAdjustIterations; i++ )
		{
			if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
				break;

			spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
			spawnEscape = TerrainPreviewSpawnLandEscapeCheck.Measure( settings, backend );
			if ( spawnLand.MeetsLandTarget( minLand ) && spawnEscape.HasEscape )
				break;

			var progressed = false;

			if ( frequency < SpawnFrequencyMax - 0.0001f )
			{
				frequency = Math.Min( SpawnFrequencyMax, frequency + freqStep );
				settings.ValleyFrequency = frequency;
				freqSteps++;
				progressed = true;
			}
			else
			{
				limitsHit |= ValleyAutoLimitHit.MaxValleyFrequency;
			}

			spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
			spawnEscape = TerrainPreviewSpawnLandEscapeCheck.Measure( settings, backend );
			if ( spawnLand.MeetsLandTarget( minLand ) && spawnEscape.HasEscape )
				break;

			if ( weight > MinValleyWeight + 0.0001f )
			{
				weight = Math.Max( MinValleyWeight, weight - weightStep );
				settings.ValleyWeight = weight;
				weightSteps++;
				progressed = true;
			}
			else
			{
				limitsHit |= ValleyAutoLimitHit.MinValleyWeight;
			}

			spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
			spawnEscape = TerrainPreviewSpawnLandEscapeCheck.Measure( settings, backend );
			if ( spawnLand.MeetsLandTarget( minLand ) && spawnEscape.HasEscape )
				break;

			if ( settings.EnableInteriorWaterLayer
				&& !spawnEscape.HasEscape
				&& interiorWaterWeight > 0.0001f )
			{
				interiorWaterWeight = Math.Max( 0f, interiorWaterWeight - interiorWaterStep );
				settings.InteriorWaterWeight = interiorWaterWeight;
				interiorWaterSteps++;
				progressed = true;
			}

			spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
			spawnEscape = TerrainPreviewSpawnLandEscapeCheck.Measure( settings, backend );
			if ( spawnLand.MeetsLandTarget( minLand ) && spawnEscape.HasEscape )
				break;

			if ( settings.EnableHillLayer && hillWeight < MaxLayerWeight - 0.0001f )
			{
				hillWeight = Math.Min( MaxLayerWeight, hillWeight + HillStep );
				settings.HillWeight = hillWeight;
				hillSteps++;
				progressed = true;
			}

			spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
			spawnEscape = TerrainPreviewSpawnLandEscapeCheck.Measure( settings, backend );
			if ( spawnLand.MeetsLandTarget( minLand ) && spawnEscape.HasEscape )
				break;

			if ( settings.EnableContinentalLayer && continentalWeight < MaxLayerWeight - 0.0001f )
			{
				continentalWeight = Math.Min( MaxLayerWeight, continentalWeight + ContinentalStep );
				settings.ContinentalWeight = continentalWeight;
				continentalSteps++;
				progressed = true;
			}

			if ( !progressed )
				break;
		}

		spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
		spawnEscape = TerrainPreviewSpawnLandEscapeCheck.Measure( settings, backend );
		if ( !spawnLand.MeetsLandTarget( minLand ) && limitsHit == ValleyAutoLimitHit.None )
			limitsHit |= ValleyAutoLimitHit.MaxValleyFrequency;

		return new GuardResult
		{
			SpawnLandOk = spawnLand.MeetsLandTarget( minLand ),
			SpawnEscapeOk = spawnEscape.HasEscape,
			SpawnLandFraction01 = spawnLand.LandFraction01,
			SpawnEscapeBestLandMeters = spawnEscape.BestContinuousLandMeters,
			FrequencyStepsUp = freqSteps,
			WeightStepsDown = weightSteps,
			InteriorWaterStepsDown = interiorWaterSteps,
			HillStepsUp = hillSteps,
			ContinentalStepsUp = continentalSteps,
			LimitsHit = limitsHit,
		};
	}

	public static string FormatStatus( GuardResult result, float minLandFraction01, float radiusMeters )
	{
		if ( result.FrequencyStepsUp == 0 && result.WeightStepsDown == 0
			&& result.InteriorWaterStepsDown == 0
			&& result.HillStepsUp == 0 && result.ContinentalStepsUp == 0 )
			return null;

		var line = " · spawn guard";
		if ( result.FrequencyStepsUp > 0 )
			line += $" freq +{result.FrequencyStepsUp}";
		if ( result.WeightStepsDown > 0 )
			line += $" weight −{result.WeightStepsDown}";
		if ( result.InteriorWaterStepsDown > 0 )
			line += $" interior −{result.InteriorWaterStepsDown}";
		if ( result.HillStepsUp > 0 )
			line += $" hills +{result.HillStepsUp}";
		if ( result.ContinentalStepsUp > 0 )
			line += $" continental +{result.ContinentalStepsUp}";
		line += $" · land {result.SpawnLandFraction01 * 100f:0.#}% @ {radiusMeters:0.#}m";

		if ( !result.SpawnLandOk )
			line += $" · need {Math.Clamp( minLandFraction01, 0f, 1f ) * 100f:0.#}% land";

		if ( !result.SpawnEscapeOk )
			line += $" · escape {result.SpawnEscapeBestLandMeters:0.#}m";

		return line;
	}
}
