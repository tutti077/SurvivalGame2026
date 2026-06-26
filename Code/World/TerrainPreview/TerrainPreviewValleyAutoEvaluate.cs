namespace Survival;

public static class TerrainPreviewValleyAutoEvaluate
{
	public readonly struct Snapshot
	{
		public bool SpawnAcceptableOk { get; init; }
		public bool SpawnIdealOk { get; init; }
		public bool SpawnLandFractionOk { get; init; }
		public bool SpawnEscapeOk { get; init; }
		public bool InteriorOceanOk { get; init; }
		public bool ExteriorOceanOk { get; init; }
		public bool AbsoluteTotalOceanOk { get; init; }
		public float SpawnLandFraction01 { get; init; }
		public float SpawnEscapeBestLandMeters { get; init; }
		public float InteriorOceanFraction01 { get; init; }
		public float ExteriorOceanFraction01 { get; init; }
		public float TotalOceanFraction01 { get; init; }

		public bool IsSolved( TerrainPreviewSettings settings )
			=> TerrainPreviewValleyAutoEvaluate.IsSolved( settings, this );

		public bool IsHardFail( TerrainPreviewSettings settings )
			=> TerrainPreviewValleyAutoEvaluate.IsHardFail( settings, this );

		public ValleyAutoUnmetGoal ComputeUnmet( TerrainPreviewSettings settings )
		{
			if ( !AutoActive( settings ) )
				return ValleyAutoUnmetGoal.None;

			var unmet = ValleyAutoUnmetGoal.None;
			if ( !SpawnAcceptableOk )
			{
				if ( !SpawnLandFractionOk )
					unmet |= ValleyAutoUnmetGoal.SpawnLand;
				if ( !SpawnEscapeOk && settings.SpawnRequireLandEscape )
					unmet |= ValleyAutoUnmetGoal.SpawnLandlocked;
			}
			if ( WantsInteriorOcean( settings ) && !InteriorOceanOk )
				unmet |= ValleyAutoUnmetGoal.InteriorOcean;
			if ( !ExteriorOceanOk )
				unmet |= ValleyAutoUnmetGoal.ExteriorOceanExceeded;
			if ( !AbsoluteTotalOceanOk )
				unmet |= ValleyAutoUnmetGoal.AbsoluteTotalOceanExceeded;
			return unmet;
		}

		public int PriorityScore( TerrainPreviewSettings settings )
		{
			var score = 0;
			if ( !SpawnAcceptableOk )
				score += 10000;
			if ( !AbsoluteTotalOceanOk )
				score += 1000;
			if ( !ExteriorOceanOk )
				score += 500;
			if ( WantsInteriorOcean( settings ) && !InteriorOceanOk )
				score += 100;
			if ( SpawnAcceptableOk && !SpawnIdealOk )
				score += 1;
			return score;
		}
	}

	public static bool AutoActive( TerrainPreviewSettings settings )
		=> settings.EnableValleyLayer
			&& (settings.EnableValleySpawnAutoFrequency
				|| settings.EnableValleyOceanAutoWeight
				|| settings.EnableInteriorWaterLayer
				|| settings.EnableValleyAutoExhaustiveSearch);

	public static bool WantsInteriorOcean( TerrainPreviewSettings settings )
		=> settings.EnableValleyOceanAutoWeight
			|| settings.EnableInteriorWaterLayer
			|| settings.EnableValleyAutoExhaustiveSearch;

	public static float SpawnGuardTargetLand( TerrainPreviewSettings settings )
		=> Math.Clamp( settings.ValleySpawnMinLandFraction01, 0.5f, 1f );

	public static float SpawnAcceptableLand( TerrainPreviewSettings settings )
		=> Math.Clamp( settings.ValleySpawnAcceptableLandFraction01, 0.5f, 1f );

	public static float AbsoluteMaxTotalOcean( TerrainPreviewSettings settings )
		=> Math.Clamp( settings.ValleyOceanAbsoluteMaxTotalFraction01, 0.05f, 0.75f );

	public static float MaxExteriorOcean( TerrainPreviewSettings settings )
		=> Math.Clamp( settings.ValleyOceanMaxExteriorFraction01, 0.05f, 0.75f );

	public static bool IsSolved( TerrainPreviewSettings settings, Snapshot snapshot )
	{
		if ( !AutoActive( settings ) )
			return true;

		if ( !snapshot.SpawnAcceptableOk || !snapshot.AbsoluteTotalOceanOk || !snapshot.ExteriorOceanOk )
			return false;

		if ( WantsInteriorOcean( settings ) && !snapshot.InteriorOceanOk )
			return false;

		return true;
	}

	/// <summary>Spawn below floor, rim over cap, or total ocean at/above absolute cap.</summary>
	public static bool IsHardFail( TerrainPreviewSettings settings, Snapshot snapshot )
	{
		if ( !AutoActive( settings ) )
			return false;

		return !snapshot.SpawnAcceptableOk
			|| !snapshot.AbsoluteTotalOceanOk
			|| !snapshot.ExteriorOceanOk;
	}

	public static (float PreferredMax, float AbsoluteMax) TotalOceanCaps( TerrainPreviewSettings settings )
	{
		var preferred = Math.Clamp( settings.ValleyOceanAutoMaxTotalFraction01, 0f, 1f );
		var absolute = Math.Max( preferred, AbsoluteMaxTotalOcean( settings ) );
		return (preferred, absolute);
	}

	public static string FormatRequiredGoals( TerrainPreviewSettings settings )
	{
		if ( !AutoActive( settings ) )
			return "none (auto off)";

		var threshold = SpawnAcceptableLand( settings ) * 100f;
		var radius = Math.Max( 5f, settings.ValleySpawnLandRadiusMeters );
		var parts = new List<string>( 6 )
		{
			$"≥{threshold:0.#}% land @ {radius:0.#}m spawn",
		};

		if ( settings.SpawnRequireLandEscape )
		{
			var escape = Math.Max( 50f, settings.SpawnEscapeMinDistanceMeters );
			parts.Add( $"≥{escape:0.#}m dry escape route" );
		}

		if ( WantsInteriorOcean( settings ) )
		{
			var interior = Math.Clamp( settings.ValleyOceanAutoMinInteriorFraction01, 0f, 1f ) * 100f;
			var zone = Math.Clamp( settings.InteriorZoneRadius01, 0.1f, 0.95f ) * 100f;
			parts.Add( $"≥{interior:0.#}% interior @ {zone:0.#}% radius" );
		}

		var exteriorMax = MaxExteriorOcean( settings ) * 100f;
		parts.Add( $"≤{exteriorMax:0.#}% rim ocean" );

		var absolute = AbsoluteMaxTotalOcean( settings ) * 100f;
		parts.Add( $"<{absolute:0.#}% total ocean" );

		return string.Join( " · ", parts );
	}

	public static Snapshot Measure( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;

		var acceptableLand = SpawnAcceptableLand( settings );
		var guardLand = SpawnGuardTargetLand( settings );
		var minInterior = Math.Clamp( settings.ValleyOceanAutoMinInteriorFraction01, 0f, 1f );
		var maxExterior = MaxExteriorOcean( settings );
		var absoluteMaxTotal = AbsoluteMaxTotalOcean( settings );
		var spawnRadius = Math.Max( 5f, settings.ValleySpawnLandRadiusMeters );

		var spawnLand = TerrainPreviewSpawnLandCheck.Measure( settings, spawnRadius, backend );
		var spawnEscape = TerrainPreviewSpawnLandEscapeCheck.Measure( settings, backend );
		var coverage = TerrainPreviewGenerator.MeasureWaterCoverage( settings );
		var landFractionOk = spawnLand.MeetsLandTarget( acceptableLand );
		var escapeOk = spawnEscape.HasEscape;

		return new Snapshot
		{
			SpawnLandFraction01 = spawnLand.LandFraction01,
			SpawnEscapeBestLandMeters = spawnEscape.BestContinuousLandMeters,
			SpawnLandFractionOk = landFractionOk,
			SpawnEscapeOk = escapeOk,
			SpawnAcceptableOk = landFractionOk && escapeOk,
			SpawnIdealOk = spawnLand.MeetsLandTarget( guardLand ) && escapeOk,
			InteriorOceanOk = !coverage.IsBelowInteriorOceanTarget( minInterior ),
			ExteriorOceanOk = !coverage.IsAtOrAboveExteriorOceanCap( maxExterior ),
			InteriorOceanFraction01 = coverage.InteriorOceanFraction01,
			ExteriorOceanFraction01 = coverage.ExteriorOceanFraction01,
			AbsoluteTotalOceanOk = !coverage.IsAtOrAboveTotalOceanCap( absoluteMaxTotal ),
			TotalOceanFraction01 = coverage.OceanFraction01,
		};
	}
}
