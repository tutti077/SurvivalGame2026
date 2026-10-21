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

		TerrainPreviewSample[] prefetchedSamples = null,

		bool riverBedAsGround = true )

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



				// River beds: the near chunks carry a see-through water surface now, so the bed under it is the biome
				// ground darkened like wet soil (Mark 2026-10-21: "see-through but not totally clear" — a palette-blue
				// bed under refracting water read as solid teal). Lakes stay palette-blue (no surface yet), and so do
				// the far tier's river creases, which have no water strip (riverBedAsGround false).
				if ( displayWater && riverBedAsGround && TerrainPreviewLandDiskFields.IsRiverWaterAtWorld( settings, wx, wy ) )

				{

					var bedResolved = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, wx, wy );

					colors[idx] = RiverBedColor( TerrainPreviewBiomeColors.SampleBiomeOverlay( settings, sample, wx, wy, bedResolved ) );

					continue;

				}



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

	/// <summary>Sandy light grey-brown riverbed (Mark 2026-10-21: "more grey or light brown, sandy-ish").</summary>
	static readonly Color RiverBedSand = new( 0.60f, 0.54f, 0.44f );

	/// <summary>
	/// The riverbed under the clear water: sand, with a fifth of the biome ground mixed in so a Clover bed is a touch
	/// warmer than a Redwood one, and never the bank's own grass colour (the first pass darkened the ground instead and
	/// the Clover bed stayed a green sheet).
	/// </summary>
	static Color RiverBedColor( Color ground )
		=> new(
			(RiverBedSand.r * 0.8f) + (ground.r * 0.2f),
			(RiverBedSand.g * 0.8f) + (ground.g * 0.2f),
			(RiverBedSand.b * 0.8f) + (ground.b * 0.2f),
			ground.a );
}
