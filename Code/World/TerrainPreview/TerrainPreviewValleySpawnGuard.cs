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
		public float SpawnLandFraction01 { get; init; }
		public int FrequencyStepsUp { get; init; }
		public int WeightStepsDown { get; init; }
		public int HillStepsUp { get; init; }
		public int ContinentalStepsUp { get; init; }
		public ValleyAutoLimitHit LimitsHit { get; init; }
	}

	public static GuardResult Ensure( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;

		if ( !settings.EnableValleyLayer )
			return new GuardResult { SpawnLandOk = true };

		var minLand = TerrainPreviewValleyAutoEvaluate.SpawnAcceptableLand( settings );
		var radius = Math.Max( 5f, settings.ValleySpawnLandRadiusMeters );
		var spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
		if ( spawnLand.MeetsLandTarget( minLand ) )
		{
			return new GuardResult
			{
				SpawnLandOk = true,
				SpawnLandFraction01 = spawnLand.LandFraction01,
			};
		}

		var limitsHit = ValleyAutoLimitHit.None;
		var freqSteps = 0;
		var weightSteps = 0;
		var hillSteps = 0;
		var continentalSteps = 0;
		var freqStep = Math.Max( 0.5f, settings.ValleyAutoFrequencyStep );
		var weightStep = Math.Max( 0.001f, settings.ValleyOceanWeightStep );
		var frequency = settings.ValleyFrequency;
		var weight = settings.ValleyWeight;
		var hillWeight = settings.HillWeight;
		var continentalWeight = settings.ContinentalWeight;

		for ( var i = 0; i < MaxAdjustIterations; i++ )
		{
			if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
				break;

			spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
			if ( spawnLand.MeetsLandTarget( minLand ) )
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
			if ( spawnLand.MeetsLandTarget( minLand ) )
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
			if ( spawnLand.MeetsLandTarget( minLand ) )
				break;

			if ( settings.EnableHillLayer && hillWeight < MaxLayerWeight - 0.0001f )
			{
				hillWeight = Math.Min( MaxLayerWeight, hillWeight + HillStep );
				settings.HillWeight = hillWeight;
				hillSteps++;
				progressed = true;
			}

			spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, radius, backend );
			if ( spawnLand.MeetsLandTarget( minLand ) )
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
		if ( !spawnLand.MeetsLandTarget( minLand ) && limitsHit == ValleyAutoLimitHit.None )
			limitsHit |= ValleyAutoLimitHit.MaxValleyFrequency;

		return new GuardResult
		{
			SpawnLandOk = spawnLand.MeetsLandTarget( minLand ),
			SpawnLandFraction01 = spawnLand.LandFraction01,
			FrequencyStepsUp = freqSteps,
			WeightStepsDown = weightSteps,
			HillStepsUp = hillSteps,
			ContinentalStepsUp = continentalSteps,
			LimitsHit = limitsHit,
		};
	}

	public static string FormatStatus( GuardResult result, float minLandFraction01, float radiusMeters )
	{
		if ( result.FrequencyStepsUp == 0 && result.WeightStepsDown == 0
			&& result.HillStepsUp == 0 && result.ContinentalStepsUp == 0 )
			return null;

		var line = " · spawn guard";
		if ( result.FrequencyStepsUp > 0 )
			line += $" freq +{result.FrequencyStepsUp}";
		if ( result.WeightStepsDown > 0 )
			line += $" weight −{result.WeightStepsDown}";
		if ( result.HillStepsUp > 0 )
			line += $" hills +{result.HillStepsUp}";
		if ( result.ContinentalStepsUp > 0 )
			line += $" continental +{result.ContinentalStepsUp}";
		line += $" · land {result.SpawnLandFraction01 * 100f:0.#}% @ {radiusMeters:0.#}m";

		if ( !result.SpawnLandOk )
			line += $" · need {Math.Clamp( minLandFraction01, 0f, 1f ) * 100f:0.#}% land";

		return line;
	}
}
