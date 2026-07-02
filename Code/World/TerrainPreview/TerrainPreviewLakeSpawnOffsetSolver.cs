namespace Survival;

/// <summary>
/// Computes lake mask X/Y slide from one mask sample — no spiral offset search.
/// Positive offset shifts noise east; wet regions move west in world space.
/// </summary>
static class TerrainPreviewLakeSpawnOffsetSolver
{
	const int DrySpawnProbeDirections = 8;
	const float DrySpawnProbeStepMeters = 120f;
	const int MaxDrySpawnNudgeSteps = 12;

	public static void ComputeOffsetMeters(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		float[] lakeMaskGrid,
		float threshold01,
		out float offsetXMeters,
		out float offsetYMeters )
	{
		offsetXMeters = 0f;
		offsetYMeters = 0f;

		var maxOffset = Math.Max( 0f, settings.LakeMaxOffsetMeters );
		if ( maxOffset <= 0f )
			return;

		var spawnRadius = Math.Max( 5f, settings.LakeSpawnCheckRadiusMeters );
		ApplyAnalyticDrySpawnOffset(
			settings, landDisk, res, radius, diameter, lakeMaskGrid, threshold01, spawnRadius, maxOffset,
			ref offsetXMeters, ref offsetYMeters );
		NudgeOffsetForDrySpawn(
			settings, landDisk, res, radius, diameter, lakeMaskGrid, threshold01, spawnRadius, maxOffset,
			ref offsetXMeters, ref offsetYMeters );
		SlideOffsetForShowcaseWater(
			settings, landDisk, res, radius, diameter, lakeMaskGrid, threshold01, maxOffset,
			ref offsetXMeters, ref offsetYMeters );

		ClampOffsetMagnitude( ref offsetXMeters, ref offsetYMeters, maxOffset );
	}

	/// <summary>
	/// Slide the mask so spawn samples a dry grid cell: offset = −nearestDryWorld (clamped).
	/// noise(world − offset) — world 0 reads the mask value that was dry at world nearestDry.
	/// </summary>
	static void ApplyAnalyticDrySpawnOffset(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		float[] lakeMaskGrid,
		float threshold01,
		float spawnRadiusMeters,
		float maxOffset,
		ref float offsetX,
		ref float offsetY )
	{
		var disk = TerrainPreviewLakeMaskShift.MeasureSpawnDiskDry(
			settings, landDisk, res, radius, diameter, lakeMaskGrid, threshold01,
			offsetX, offsetY, spawnRadiusMeters );

		if ( disk.MeetsLandTarget( 0.5f )
			&& !TerrainPreviewLakeMaskShift.IsWetAtWorld(
				0f, 0f, offsetX, offsetY, landDisk, res, radius, diameter, lakeMaskGrid, threshold01 ) )
			return;

		if ( TerrainPreviewLakeMaskShift.TryFindNearestDryLandWorld(
				landDisk, res, radius, diameter, lakeMaskGrid, threshold01, maxOffset,
				out var dryX, out var dryY, out _ ) )
		{
			var ox = -dryX;
			var oy = -dryY;
			if ( OffsetMagnitude( ox, oy ) > maxOffset )
				ClampOffsetMagnitude( ref ox, ref oy, maxOffset );

			offsetX = ox;
			offsetY = oy;

			disk = TerrainPreviewLakeMaskShift.MeasureSpawnDiskDry(
				settings, landDisk, res, radius, diameter, lakeMaskGrid, threshold01,
				offsetX, offsetY, spawnRadiusMeters );
			if ( disk.MeetsLandTarget( 0.5f )
				&& !TerrainPreviewLakeMaskShift.IsWetAtWorld(
					0f, 0f, offsetX, offsetY, landDisk, res, radius, diameter, lakeMaskGrid, threshold01 ) )
				return;
		}

		if ( TerrainPreviewLakeMaskShift.TryMeasureWetCentroidInDisk(
				settings, landDisk, res, radius, diameter, lakeMaskGrid, threshold01, spawnRadiusMeters,
				out var wetCx, out var wetCy, out _ ) )
		{
			offsetX += wetCx;
			offsetY += wetCy;
			ClampOffsetMagnitude( ref offsetX, ref offsetY, maxOffset );
		}
	}

	static void NudgeOffsetForDrySpawn(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		float[] lakeMaskGrid,
		float threshold01,
		float spawnRadiusMeters,
		float maxOffset,
		ref float offsetX,
		ref float offsetY )
	{
		for ( var step = 0; step < MaxDrySpawnNudgeSteps; step++ )
		{
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				return;

			var disk = TerrainPreviewLakeMaskShift.MeasureSpawnDiskDry(
				settings, landDisk, res, radius, diameter, lakeMaskGrid, threshold01,
				offsetX, offsetY, spawnRadiusMeters );

			if ( disk.MeetsLandTarget( 0.5f )
				&& !TerrainPreviewLakeMaskShift.IsWetAtWorld(
					0f, 0f, offsetX, offsetY, landDisk, res, radius, diameter, lakeMaskGrid, threshold01 ) )
				return;

			var bestOx = offsetX;
			var bestOy = offsetY;
			var bestLand = disk.LandFraction01;

			for ( var dir = 0; dir < DrySpawnProbeDirections; dir++ )
			{
				var angle = (dir / (float)DrySpawnProbeDirections) * MathF.PI * 2f;
				var trialOx = offsetX + (MathF.Cos( angle ) * DrySpawnProbeStepMeters);
				var trialOy = offsetY + (MathF.Sin( angle ) * DrySpawnProbeStepMeters);
				if ( OffsetMagnitude( trialOx, trialOy ) > maxOffset )
					continue;

				var trialDisk = TerrainPreviewLakeMaskShift.MeasureSpawnDiskDry(
					settings, landDisk, res, radius, diameter, lakeMaskGrid, threshold01,
					trialOx, trialOy, spawnRadiusMeters );

				if ( trialDisk.LandFraction01 <= bestLand + 0.0001f )
					continue;

				bestLand = trialDisk.LandFraction01;
				bestOx = trialOx;
				bestOy = trialOy;
			}

			if ( bestLand <= disk.LandFraction01 + 0.0001f )
				return;

			offsetX = bestOx;
			offsetY = bestOy;
		}
	}

	static void SlideOffsetForShowcaseWater(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		float[] lakeMaskGrid,
		float threshold01,
		float maxOffset,
		ref float offsetX,
		ref float offsetY )
	{
		var showcaseRadius = Math.Max( 50f, settings.LakeSpawnShowcaseWaterRadiusMeters );
		if ( !TerrainPreviewLakeMaskShift.TryFindNearestWetWorld(
				landDisk, res, radius, diameter, lakeMaskGrid, threshold01, offsetX, offsetY, showcaseRadius * 2.5f,
				out var wetX, out var wetY, out var wetDist ) )
			return;

		if ( wetDist >= 1f && wetDist <= showcaseRadius + 0.5f )
			return;

		var targetDist = Math.Clamp( showcaseRadius * 0.72f, 40f, showcaseRadius );
		var targetX = wetDist > 0.001f ? (wetX / wetDist) * targetDist : wetX;
		var targetY = wetDist > 0.001f ? (wetY / wetDist) * targetDist : wetY;

		// Slide mask so nearest wet pixel moves toward the showcase ring around spawn.
		offsetX += wetX - targetX;
		offsetY += wetY - targetY;

		if ( OffsetMagnitude( offsetX, offsetY ) > maxOffset )
			ClampOffsetMagnitude( ref offsetX, ref offsetY, maxOffset );
	}

	static float OffsetMagnitude( float offsetX, float offsetY )
		=> MathF.Sqrt( (offsetX * offsetX) + (offsetY * offsetY) );

	static void ClampOffsetMagnitude( ref float offsetX, ref float offsetY, float maxOffset )
	{
		var mag = OffsetMagnitude( offsetX, offsetY );
		if ( mag <= maxOffset || mag <= 0.0001f )
			return;

		var scale = maxOffset / mag;
		offsetX *= scale;
		offsetY *= scale;
	}
}
