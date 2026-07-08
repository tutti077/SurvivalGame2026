namespace Survival;

/// <summary>One-shot height pipeline traces for playtest / mesh debugging.</summary>
static class TerrainPreviewHeightDiagnostics
{
	static bool _loggedSpawnTrace;
	static int _loggedChunkVertexTraces;

	public static void TryLogSpawnPipelineTrace( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		if ( _loggedSpawnTrace )
			return;

		_loggedSpawnTrace = true;

		var landRadius = settings.LandRadiusMeters;
		var totalRadius = settings.TotalWorldRadiusMeters;
		var dist = MathF.Sqrt( (worldXMeters * worldXMeters) + (worldYMeters * worldYMeters ) );
		var diameter = settings.WorldDiameterMeters;
		var nx = (worldXMeters + landRadius) / diameter;
		var ny = (worldYMeters + landRadius) / diameter;
		var seed = settings.WorldSeed;
		var maxHeight = Math.Max( 50f, settings.MaxTerrainHeightMeters );

		Log.Info(
			$"[TerrainHeight] Trace @ ({worldXMeters:0.#},{worldYMeters:0.#}) m — seed {seed} · landR {landRadius:0.#} · totalR {totalRadius:0.#} · dist {dist:0.#} · speckMin {TerrainPreviewSpeckDiameter.ResolveMeters( settings ):0.#} m" );

		if ( landRadius <= 0f || totalRadius <= 0f )
		{
			Log.Warning( "[TerrainHeight] ABORT — land or total radius <= 0." );
			return;
		}

		if ( dist > totalRadius )
		{
			Log.Warning( "[TerrainHeight] ABORT — outside total world radius (default sample, HeightMeters=0)." );
			return;
		}

		if ( dist > landRadius )
		{
			Log.Info( "[TerrainHeight] Rim ocean sample — HeightMeters = SeaLevel." );
			return;
		}

		var isOpenWater = TerrainPreviewLandDiskFields.IsOpenWater( settings, worldXMeters, worldYMeters );
		var lakeDist = TerrainPreviewLandDiskFields.SampleDistanceToOpenWaterMeters(
			settings, worldXMeters, worldYMeters );
		Log.Info( $"[TerrainHeight] openWater={isOpenWater} · lakeDist={(float.IsFinite( lakeDist ) ? lakeDist : -1f):0.#} m" );

		if ( isOpenWater )
		{
			Log.Info( "[TerrainHeight] Lake/raster water — HeightMeters = SeaLevel." );
			return;
		}

		var baseLayers = TerrainPreviewBaseHeight.Sample( settings, worldXMeters, worldYMeters, nx, ny, seed );
		var placementWeights = settings.UseContinuousBiomePlacementAtSample
			? TerrainPreviewBiomeResolver.SamplePlacementWeights(
				settings, worldXMeters, worldYMeters, baseLayers.AfterCurve01 )
			: TerrainPreviewLandDiskFields.GetFilteredPlacementWeights(
				settings, worldXMeters, worldYMeters );

		var lowlandWeights = TerrainPreviewBiomeResolver.LandWeightsWithoutMountain( placementWeights );
		var lowlandShaped01 = TerrainPreviewBiomeTerrainShaper.ApplyBlendedShape01(
			settings,
			baseLayers.AfterCurve01,
			lowlandWeights,
			nx,
			ny,
			seed,
			maxHeight,
			out _ );

		var mountainInfluence = TerrainPreviewMountainSpawnMask.SampleMountainHeightInfluence01(
			settings, worldXMeters, worldYMeters );
		var rawLakeMask = TerrainPreviewLakeMap.SampleMaskAtWorldMeters(
			settings, worldXMeters, worldYMeters, seed );
		var mountainBoost01 = TerrainPreviewBiomeMountainPeaks.SampleBoost01(
			settings,
			mountainInfluence,
			rawLakeMask,
			dist,
			nx,
			ny,
			seed,
			out _ );

		var afterLandDisplay = TerrainPreviewLandHeightDisplay.ApplyDryLandMeters(
			settings,
			baseLayers.AfterCurve01,
			lowlandShaped01,
			mountainBoost01,
			mountainInfluence,
			maxHeight );

		var afterCoast = TerrainPreviewCoastalSmoothing.ApplyMeters(
			settings, afterLandDisplay, worldXMeters, worldYMeters, dist, nx, ny, seed );

		var finalMeters = TerrainPreviewLakeCombine.Apply(
			settings, afterCoast, isFilteredOpenWater: false ).HeightMeters;

		var sample = TerrainPreviewPipeline.Sample( settings, worldXMeters, worldYMeters );

		Log.Info(
			$"[TerrainHeight] base01={baseLayers.AfterCurve01:F3} · lowlandSculpt01={lowlandShaped01:F3} · boost01={mountainBoost01:F3} · influence={mountainInfluence:F3} · landDisplay={afterLandDisplay:F1} m · coast={afterCoast:F1} m · final={finalMeters:F1} m · pipeline.HeightMeters={sample.HeightMeters:F1} m · onLand={sample.IsOnLand}" );
	}

	public static void TryLogChunkVertexTrace(
		TerrainPreviewSettings settings,
		TerrainChunkCoord coord,
		float worldX,
		float worldY,
		float heightBeforeSpeckMeters,
		float heightAfterSpeckMeters,
		ITerrainPreviewBackend backend )
	{
		if ( _loggedChunkVertexTraces >= 2 )
			return;

		_loggedChunkVertexTraces++;
		var sample = backend.Sample( settings, worldX, worldY );
		Log.Info(
			$"[TerrainHeight] Chunk {coord} corner ({worldX:0.#},{worldY:0.#}) m — sample.HeightMeters={sample.HeightMeters:F1} · meshZ before speck={heightBeforeSpeckMeters:F1} · after speck={heightAfterSpeckMeters:F1} · water={sample.OceanHeight01 > 0.5f}" );
	}
}
