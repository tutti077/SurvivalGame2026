namespace Survival;



/// <summary>

/// Chunk vertex colors — same resolver path as the biome preview map (no per-chunk ocean speck).

/// </summary>

static class TerrainChunkBiomeDisplay

{

	public static void FillChunkVertexColors(

		TerrainPreviewSettings settings,

		ITerrainPreviewBackend backend,

		int width,

		int height,

		float worldMinX,

		float worldMinY,

		float metersPerCell,

		bool[] insideWorld,

		bool[] isWater,

		TerrainPreviewBiomeId[] biomeMap,

		Color[] colors,

		TerrainPreviewSample[] prefetchedSamples = null )

	{

		backend ??= TerrainPreviewBackendRegistry.Active;

		var count = width * height;

		if ( colors is null || colors.Length != count )

			return;

		biomeMap ??= new TerrainPreviewBiomeId[count];

		insideWorld ??= new bool[count];

		isWater ??= new bool[count];

		var hasPrefetch = prefetchedSamples is not null && prefetchedSamples.Length == count;



		for ( var iy = 0; iy < height; iy++ )

		{

			for ( var ix = 0; ix < width; ix++ )

			{

				var idx = (iy * width) + ix;

				var wx = worldMinX + (ix * metersPerCell);

				var wy = worldMinY + (iy * metersPerCell);

				var sample = hasPrefetch

					? prefetchedSamples[idx]

					: backend.Sample( settings, wx, wy );

				insideWorld[idx] = sample.IsInsideWorld;



				if ( !sample.IsInsideWorld )

				{

					colors[idx] = Color.Black;

					biomeMap[idx] = TerrainPreviewBiomeId.None;

					isWater[idx] = false;

					continue;

				}



				var displayWater = TerrainShorelineDisplay.IsDisplayWaterColor( settings, wx, wy );

				isWater[idx] = displayWater;

				biomeMap[idx] = displayWater ? TerrainPreviewBiomeId.Water : TerrainPreviewBiomeId.None;



				if ( !displayWater )

				{

					var landResolved = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, wx, wy );

					biomeMap[idx] = landResolved.BiomeId;



					if ( landResolved.BiomeId == TerrainPreviewBiomeId.Blackwater )

					{

						colors[idx] = Color.Black;

						continue;

					}



					colors[idx] = TerrainPreviewBiomeColors.SampleBiomeOverlay(

						settings, sample, wx, wy, landResolved );

					continue;

				}



				colors[idx] = TerrainPreviewBiomeColors.PaletteColor( TerrainPreviewBiomeId.Water, 1f );

			}

		}



		TerrainBiomeEdgeDisplay.ApplyShoreAndBiomeEdgeJitter(

			settings,

			width,

			height,

			insideWorld,

			isWater,

			biomeMap,

			colors );

	}

}

