namespace Survival;

/// <summary>Slides the lake mask on generate — offset computed from mask geometry, not a spiral search.</summary>
public static class TerrainPreviewLakeSpawnSolver
{
	public const float MaxLakeCoverageOnLand01 = 0.33f;
	const int MaxPostBuildRefineSteps = 10;
	const int PostBuildRefineDirections = 8;
	const float PostBuildRefineStepMeters = 80f;

	public readonly struct RunResult
	{
		public bool Solved { get; init; }
		public bool Cancelled { get; init; }
		public int SeedAttempts { get; init; }
		public float LakeOffsetXMeters { get; init; }
		public float LakeOffsetYMeters { get; init; }
		public float LakeCoverageOnLand01 { get; init; }
		public float NearestShowcaseWaterMeters { get; init; }
		public bool ShowcaseWaterMet { get; init; }
		public string Status { get; init; }
		public string FailureReason { get; init; }
	}

	public static RunResult Run( TerrainPreviewSettings settings )
	{
		if ( TerrainPreviewGenerateProgress.ShouldAbort() )
			return CancelledResult();

		if ( !settings.EnableLakeSpawnSolveOnGenerate )
		{
			TerrainPreviewGenerateProgress.SetStage( "Lake fields" );
			TerrainPreviewLandDiskFields.InvalidateWaterCache();
			TerrainPreviewLandDiskFields.EnsureReady( settings );
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				return CancelledResult();

			var nearest = TerrainPreviewSpawnLandCheck.MeasureNearestOpenWaterMeters(
				settings, settings.LakeSpawnShowcaseWaterRadiusMeters );

			return new RunResult
			{
				Solved = true,
				SeedAttempts = 1,
				LakeOffsetXMeters = settings.LakeOffsetXMeters,
				LakeOffsetYMeters = settings.LakeOffsetYMeters,
				LakeCoverageOnLand01 = TerrainPreviewLandDiskFields.GetLakeCoverageOnLand01( settings ),
				NearestShowcaseWaterMeters = nearest,
				ShowcaseWaterMet = nearest >= 1f && nearest <= settings.LakeSpawnShowcaseWaterRadiusMeters + 0.5f,
				Status = "spawn solve off",
			};
		}

		var baseSeed = settings.WorldSeed;
		var maxSeeds = settings.RetryLakeSeedsUntilSpawn
			? Math.Clamp( settings.LakeMaxSeedAttempts, 1, 256 )
			: 1;

		TerrainPreviewGenerateProgress.SetStage( "Spawn solve — lake offset" );

		var lastFailureReason = "unknown";
		var lastOffsetX = settings.LakeOffsetXMeters;
		var lastOffsetY = settings.LakeOffsetYMeters;

		for ( var attempt = 0; attempt < maxSeeds; attempt++ )
		{
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				return CancelledResult();

			TerrainPreviewGenerateProgress.ReportSeedSearch( attempt + 1, maxSeeds );
			if ( attempt > 0 )
			{
				settings.WorldSeed = baseSeed + attempt;
				TerrainPreviewLandDiskFields.InvalidateBiomePlacementCache();
			}

			if ( TrySolveOffset( settings, out var offsetX, out var offsetY, out var coverage, out var nearestWater, out var showcaseMet, out var failureReason ) )
			{
				settings.LakeOffsetXMeters = offsetX;
				settings.LakeOffsetYMeters = offsetY;
				return new RunResult
				{
					Solved = true,
					SeedAttempts = attempt + 1,
					LakeOffsetXMeters = offsetX,
					LakeOffsetYMeters = offsetY,
					LakeCoverageOnLand01 = coverage,
					NearestShowcaseWaterMeters = nearestWater,
					ShowcaseWaterMet = showcaseMet,
					Status = FormatSolveStatus( offsetX, offsetY, settings.WorldSeed, nearestWater, showcaseMet, settings ),
				};
			}

			lastFailureReason = failureReason;
			lastOffsetX = settings.LakeOffsetXMeters;
			lastOffsetY = settings.LakeOffsetYMeters;
		}

		settings.WorldSeed = baseSeed;
		settings.LakeOffsetXMeters = lastOffsetX;
		settings.LakeOffsetYMeters = lastOffsetY;
		TerrainPreviewLandDiskFields.InvalidateWaterCache();
		TerrainPreviewLandDiskFields.EnsureReady( settings );

		var fallbackNearest = TerrainPreviewSpawnLandCheck.MeasureNearestOpenWaterMeters(
			settings, settings.LakeSpawnShowcaseWaterRadiusMeters );

		return new RunResult
		{
			Solved = false,
			SeedAttempts = maxSeeds,
			LakeOffsetXMeters = lastOffsetX,
			LakeOffsetYMeters = lastOffsetY,
			LakeCoverageOnLand01 = TerrainPreviewLandDiskFields.GetLakeCoverageOnLand01( settings ),
			NearestShowcaseWaterMeters = fallbackNearest,
			ShowcaseWaterMet = TerrainPreviewSpawnLandCheck.HasShowcaseWaterNearSpawn( settings ),
			FailureReason = lastFailureReason,
			Status = lastFailureReason,
		};
	}

	static string FormatSolveStatus(
		float offsetX,
		float offsetY,
		int seed,
		float nearestWaterMeters,
		bool showcaseMet,
		TerrainPreviewSettings settings )
	{
		var showcaseRadius = Math.Max( 50f, settings.LakeSpawnShowcaseWaterRadiusMeters );
		var waterLine = showcaseMet && nearestWaterMeters >= 1f
			? $" · water {nearestWaterMeters:0} m"
			: nearestWaterMeters >= 1f
				? $" · nearest water {nearestWaterMeters:0} m (>{showcaseRadius:0} m showcase)"
				: " · no lake within showcase radius";
		return $"offset ({offsetX:0},{offsetY:0}) seed {seed}{waterLine}";
	}

	static RunResult CancelledResult()
		=> new() { Cancelled = true, Status = "cancelled" };

	public static string FormatStatus( RunResult result, TerrainPreviewSettings settings )
	{
		if ( result.Cancelled )
			return "lake spawn: cancelled";

		if ( !settings.EnableLakeSpawnSolveOnGenerate )
			return "lake spawn solve: off";

		if ( result.Solved )
		{
			var showcase = result.ShowcaseWaterMet
				? $" · showcase water {result.NearestShowcaseWaterMeters:0} m"
				: result.NearestShowcaseWaterMeters >= 1f
					? $" · nearest water {result.NearestShowcaseWaterMeters:0} m (outside {settings.LakeSpawnShowcaseWaterRadiusMeters:0} m)"
					: " · showcase water missed";
			return $"lake spawn: ok — {result.Status} — lakes {result.LakeCoverageOnLand01 * 100f:0.#}%{showcase}";
		}

		return $"lake spawn: FAILED after {result.SeedAttempts} seed(s) — {result.FailureReason ?? result.Status}";
	}

	static bool TrySolveOffset(
		TerrainPreviewSettings settings,
		out float offsetX,
		out float offsetY,
		out float coverage,
		out float nearestWaterMeters,
		out bool showcaseMet,
		out string failureReason )
	{
		offsetX = 0f;
		offsetY = 0f;
		coverage = 0f;
		nearestWaterMeters = -1f;
		showcaseMet = false;
		failureReason = "unknown";

		if ( !TerrainPreviewLandDiskFields.TryGetLandDiskForSolve(
				settings, out var landDisk, out var res, out var radius, out var diameter ) )
		{
			failureReason = "land disk raster unavailable";
			return false;
		}

		TerrainPreviewGenerateProgress.ReportOffsetSearch( 0, 1 );

		settings.LakeOffsetXMeters = 0f;
		settings.LakeOffsetYMeters = 0f;
		var metersPerPixel = diameter / Math.Max( 1, res );

		TerrainPreviewGenerateProgress.SetStage( "Spawn solve — mask sample" );
		var lakeMaskGrid = TerrainPreviewLakeThresholdSolver.SampleLakeMaskGrid( settings, landDisk, res );
		if ( TerrainPreviewGenerateProgress.ShouldAbort() )
		{
			failureReason = "cancelled";
			return false;
		}

		var threshold01 = TerrainPreviewLakeThresholdSolver.ResolveThreshold01(
			settings, landDisk, res, metersPerPixel, lakeMaskGrid );

		TerrainPreviewGenerateProgress.SetStage( "Spawn solve — offset math" );
		TerrainPreviewLakeSpawnOffsetSolver.ComputeOffsetMeters(
			settings, landDisk, res, radius, diameter, lakeMaskGrid, threshold01,
			out offsetX, out offsetY );

		TerrainPreviewGenerateProgress.ReportOffsetSearch( 1, 1 );

		if ( !ApplyOffsetAndValidate(
				settings, offsetX, offsetY, out coverage, out nearestWaterMeters, out showcaseMet )
			&& RefineOffsetAfterBuild( settings, ref offsetX, ref offsetY ) )
		{
			ApplyOffsetAndValidate(
				settings, offsetX, offsetY, out coverage, out nearestWaterMeters, out showcaseMet );
		}

		settings.LakeOffsetXMeters = offsetX;
		settings.LakeOffsetYMeters = offsetY;

		if ( coverage <= MaxLakeCoverageOnLand01 + 0.0001f
			&& TerrainPreviewSpawnLandCheck.IsSpawnOnDryLand( settings ) )
			return true;

		failureReason = DescribeSolveFailure(
			settings, landDisk, res, radius, diameter, lakeMaskGrid, threshold01, offsetX, offsetY, coverage );
		return false;
	}

	static string DescribeSolveFailure(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		float[] lakeMaskGrid,
		float threshold01,
		float offsetX,
		float offsetY,
		float coverage )
	{
		if ( !TerrainPreviewLandDiskFields.IsOnLand( settings, 0f, 0f ) )
			return "spawn is outside the land disk (ocean / void)";

		if ( coverage > MaxLakeCoverageOnLand01 + 0.0001f )
		{
			return $"lake coverage on land {coverage * 100f:0.#}% exceeds max {MaxLakeCoverageOnLand01 * 100f:0.#}%"
				+ $" at offset ({offsetX:0},{offsetY:0})";
		}

		var spawnRadius = Math.Max( 5f, settings.LakeSpawnCheckRadiusMeters );
		var disk = TerrainPreviewSpawnLandCheck.MeasureLakeDisk( settings, spawnRadius );
		var dryPercent = disk.LandFraction01 * 100f;
		var centerWet = settings.EnableInteriorWaterLayer
			&& TerrainPreviewLandDiskFields.IsOpenWater( settings, 0f, 0f );

		var maxOffset = Math.Max( 0f, settings.LakeMaxOffsetMeters );
		var nearestDry = -1f;
		if ( TerrainPreviewLakeMaskShift.TryFindNearestDryLandWorld(
				landDisk, res, radius, diameter, lakeMaskGrid, threshold01, maxOffset,
				out _, out _, out var dryDist ) )
			nearestDry = dryDist;

		var dryHint = nearestDry >= 0f
			? $"nearest dry land {nearestDry:0} m (max offset {maxOffset:0} m)"
			: $"no dry land within {maxOffset:0} m of spawn";

		if ( centerWet )
		{
			return $"spawn center is open water — {dryPercent:0}% dry in {spawnRadius:0} m disk"
				+ $" · offset ({offsetX:0},{offsetY:0}) · {dryHint}";
		}

		return $"spawn lake disk too wet — {dryPercent:0}% dry in {spawnRadius:0} m disk (need ≥50%)"
			+ $" · offset ({offsetX:0},{offsetY:0}) · {dryHint}";
	}

	static bool ApplyOffsetAndValidate(
		TerrainPreviewSettings settings,
		float offsetX,
		float offsetY,
		out float coverage,
		out float nearestWaterMeters,
		out bool showcaseMet )
	{
		settings.LakeOffsetXMeters = offsetX;
		settings.LakeOffsetYMeters = offsetY;
		TerrainPreviewLandDiskFields.InvalidateWaterCache();
		TerrainPreviewLandDiskFields.EnsureReady( settings );

		coverage = TerrainPreviewLandDiskFields.GetLakeCoverageOnLand01( settings );
		var showcaseRadius = Math.Max( 50f, settings.LakeSpawnShowcaseWaterRadiusMeters );
		nearestWaterMeters = TerrainPreviewSpawnLandCheck.MeasureNearestOpenWaterMeters( settings, showcaseRadius );
		showcaseMet = TerrainPreviewSpawnLandCheck.HasShowcaseWaterNearSpawn( settings );
		return coverage <= MaxLakeCoverageOnLand01 + 0.0001f
			&& TerrainPreviewSpawnLandCheck.IsSpawnOnDryLand( settings );
	}

	static bool RefineOffsetAfterBuild( TerrainPreviewSettings settings, ref float offsetX, ref float offsetY )
	{
		if ( TerrainPreviewSpawnLandCheck.IsSpawnOnDryLand( settings ) )
			return false;

		var maxOffset = Math.Max( 0f, settings.LakeMaxOffsetMeters );
		var spawnRadius = Math.Max( 5f, settings.LakeSpawnCheckRadiusMeters );
		var improved = false;

		for ( var step = 0; step < MaxPostBuildRefineSteps; step++ )
		{
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				return improved;

			if ( TerrainPreviewSpawnLandCheck.IsSpawnOnDryLand( settings ) )
				return improved;

			var disk = TerrainPreviewSpawnLandCheck.MeasureLakeDisk( settings, spawnRadius );
			var bestOx = offsetX;
			var bestOy = offsetY;
			var bestLand = disk.LandFraction01;

			for ( var dir = 0; dir < PostBuildRefineDirections; dir++ )
			{
				var angle = (dir / (float)PostBuildRefineDirections) * MathF.PI * 2f;
				var trialOx = offsetX + (MathF.Cos( angle ) * PostBuildRefineStepMeters);
				var trialOy = offsetY + (MathF.Sin( angle ) * PostBuildRefineStepMeters);
				if ( OffsetMagnitude( trialOx, trialOy ) > maxOffset )
					continue;

				settings.LakeOffsetXMeters = trialOx;
				settings.LakeOffsetYMeters = trialOy;
				TerrainPreviewLandDiskFields.InvalidateWaterCache();
				TerrainPreviewLandDiskFields.EnsureReady( settings );

				if ( TerrainPreviewLandDiskFields.GetLakeCoverageOnLand01( settings ) > MaxLakeCoverageOnLand01 + 0.0001f )
					continue;

				var trialDisk = TerrainPreviewSpawnLandCheck.MeasureLakeDisk( settings, spawnRadius );
				if ( trialDisk.LandFraction01 <= bestLand + 0.0001f )
					continue;

				bestLand = trialDisk.LandFraction01;
				bestOx = trialOx;
				bestOy = trialOy;
			}

			if ( bestLand <= disk.LandFraction01 + 0.0001f )
				break;

			offsetX = bestOx;
			offsetY = bestOy;
			improved = true;
			settings.LakeOffsetXMeters = offsetX;
			settings.LakeOffsetYMeters = offsetY;
			TerrainPreviewLandDiskFields.InvalidateWaterCache();
			TerrainPreviewLandDiskFields.EnsureReady( settings );
		}

		return improved;
	}

	static float OffsetMagnitude( float offsetX, float offsetY )
		=> MathF.Sqrt( (offsetX * offsetX) + (offsetY * offsetY) );
}
