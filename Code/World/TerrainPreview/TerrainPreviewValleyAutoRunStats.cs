namespace Survival;

/// <summary>Structured generate stats for the editor stats column.</summary>
public static class TerrainPreviewValleyAutoRunStats
{
	public readonly struct Column
	{
		public string Result { get; init; }
		public string Seed { get; init; }
		public string SpawnLand { get; init; }
		public string InteriorOcean { get; init; }
		public string ExteriorOcean { get; init; }
		public string TotalOcean { get; init; }
		public string Targets { get; init; }
		public string Unmet { get; init; }
		public string Limits { get; init; }
		public string Tune { get; init; }
		public string Note { get; init; }
	}

	public static Column Build(
		TerrainPreviewValleyAutoPipeline.RunResult result,
		TerrainPreviewSettings settings,
		TerrainPreviewWaterCoverageStats coverage )
	{
		var spawnPct = result.Snapshot.SpawnLandFraction01 * 100f;
		var interiorPct = coverage.InteriorOceanFraction01 * 100f;
		var exteriorPct = coverage.ExteriorOceanFraction01 * 100f;
		var totalPct = coverage.OceanFraction01 * 100f;
		var spawnNeed = TerrainPreviewValleyAutoEvaluate.SpawnAcceptableLand( settings ) * 100f;
		var interiorNeed = settings.ValleyOceanAutoMinInteriorFraction01 * 100f;
		var exteriorMax = settings.ValleyOceanMaxExteriorFraction01 * 100f;
		var totalMax = TerrainPreviewValleyAutoEvaluate.AbsoluteMaxTotalOcean( settings ) * 100f;
		var radius = settings.ValleySpawnLandRadiusMeters;
		var zone = settings.InteriorZoneRadius01 * 100f;

		var resultLabel = result.Solved
			? "SOLVED"
			: result.SeedRejected
				? "REJECTED"
				: result.RevertedToLandCheckpoint
					? "LAND ONLY"
					: "FAILED";

		var tune = BuildTuneLine( result, settings );
		var note = "";
		if ( result.SeedRejected )
			note += "Seed rejected after all attempts\n";
		if ( result.SeedsAttempted > 1 )
			note += $"Seeds tried: {result.SeedsAttempted} (retry +1 until solved)\n";
		if ( result.SearchIterationCapped )
			note += $"Tune hit iteration cap ({TerrainPreviewMapIterationTracker.MaxIterations})\n";
		else if ( result.SeedsAttempted > 0 && !result.Solved )
			note += "Tune stopped early (limit reached)\n";
		if ( result.SearchTimedOut )
			note += "Search timed out\n";
		if ( result.RevertedToLandCheckpoint )
			note += "Reverted to land checkpoint (interior tune failed)\n";

		return new Column
		{
			Result = resultLabel,
			Seed = settings.WorldSeed.ToString(),
			SpawnLand = $"{spawnPct:0.#}% @ {radius:0.#}m (need ≥{spawnNeed:0.#}%)",
			InteriorOcean = $"{interiorPct:0.#}% (need ≥{interiorNeed:0.#}% @ {zone:0.#}% radius)",
			ExteriorOcean = $"{exteriorPct:0.#}% (max {exteriorMax:0.#}%)",
			TotalOcean = $"{totalPct:0.#}% (max {totalMax:0.#}%)",
			Targets = $"Spawn ≥{spawnNeed:0.#}% · Interior ≥{interiorNeed:0.#}% · Rim ≤{exteriorMax:0.#}% · Total <{totalMax:0.#}%",
			Unmet = TerrainPreviewValleyAutoLimits.FormatUnmetGoals( result.UnmetGoals ),
			Limits = TerrainPreviewValleyAutoLimits.FormatLimitsHit( result.LimitsHit ),
			Tune = string.IsNullOrWhiteSpace( tune ) ? "—" : tune,
			Note = note.Trim(),
		};
	}

	static string BuildTuneLine( TerrainPreviewValleyAutoPipeline.RunResult result, TerrainPreviewSettings settings )
	{
		if ( result.UsedGridSearch )
			return TerrainPreviewValleyAutoGridSearch.FormatStatus( result.Grid ) ?? "grid search";

		var parts = new List<string>( 4 );
		var freq = TerrainPreviewValleySpawnAutoFrequency.FormatStatus(
			result.Frequency,
			settings.ValleySpawnAcceptableLandFraction01,
			settings.ValleySpawnLandRadiusMeters );
		if ( !string.IsNullOrWhiteSpace( freq ) )
			parts.Add( freq.Trim().TrimStart( '·' ).Trim() );

		var interior = TerrainPreviewValleyInteriorWaterAuto.FormatStatus(
			result.InteriorWater, settings.ValleyOceanAutoMinInteriorFraction01 );
		if ( !string.IsNullOrWhiteSpace( interior ) )
			parts.Add( interior.Trim().TrimStart( '·' ).Trim() );

		var weight = TerrainPreviewValleyOceanAutoWeight.FormatStatus(
			result.Weight,
			settings.ValleyOceanAutoMinInteriorFraction01,
			settings.ValleyOceanAutoMaxTotalFraction01,
			settings.ValleyOceanAbsoluteMaxTotalFraction01 );
		if ( !string.IsNullOrWhiteSpace( weight ) )
			parts.Add( weight.Trim().TrimStart( '·' ).Trim() );

		var guard = TerrainPreviewValleySpawnGuard.FormatStatus(
			result.SpawnGuard,
			settings.ValleySpawnAcceptableLandFraction01,
			settings.ValleySpawnLandRadiusMeters );
		if ( !string.IsNullOrWhiteSpace( guard ) )
			parts.Add( guard.Trim().TrimStart( '·' ).Trim() );

		return parts.Count == 0 ? null : string.Join( "\n", parts );
	}

	public static string FormatColumnText( Column column )
	{
		var lines = new List<string>( 12 )
		{
			$"Result: {column.Result}",
			$"Seed: {column.Seed}",
			"",
			"Spawn land",
			column.SpawnLand,
			"",
			"Interior ocean",
			column.InteriorOcean,
			"",
			"Rim ocean",
			column.ExteriorOcean,
			"",
			"Total ocean",
			column.TotalOcean,
			"",
			"Unmet",
			column.Unmet,
			"",
			"Limits hit",
			column.Limits,
			"",
			"Tune",
			column.Tune,
		};

		if ( !string.IsNullOrWhiteSpace( column.Note ) )
		{
			lines.Add( "" );
			lines.Add( column.Note );
		}

		return string.Join( "\n", lines );
	}
}
