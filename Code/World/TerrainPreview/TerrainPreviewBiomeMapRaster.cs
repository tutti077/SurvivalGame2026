namespace Survival;

/// <summary>Rasterizes biome ids, optional speck cleanup, then colors the preview layer.</summary>
public static class TerrainPreviewBiomeMapRaster
{
	public static void FillBiomeColors(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		int res,
		float radius,
		float diameter,
		bool[] insideWorld,
		Color[] colors )
	{
		var biomeMap = new TerrainPreviewBiomeId[res * res];
		var shadeMap = new float[res * res];
		var heightMap = new float[res * res];

		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				var wx = (px + 0.5f) / res * diameter - radius;
				var wy = (py + 0.5f) / res * diameter - radius;
				var sample = backend.Sample( settings, wx, wy );

				if ( !sample.IsInsideWorld )
				{
					biomeMap[idx] = TerrainPreviewBiomeId.None;
					shadeMap[idx] = 1f;
					heightMap[idx] = 0f;
					continue;
				}

				var resolved = TerrainPreviewBiomeResolver.Resolve( settings, sample, wx, wy );
				biomeMap[idx] = resolved.BiomeId;
				shadeMap[idx] = resolved.Shade01;
				heightMap[idx] = sample.Height01;
			}
		}

		if ( settings.BiomeSpeckFilterEnabled )
		{
			var minPixels = TerrainPreviewBiomeSpeckFilter.ComputeMinPatchPixels( settings, res );
			TerrainPreviewBiomeSpeckFilter.MergeSmallPatches( biomeMap, res, res, minPixels );
		}

		for ( var i = 0; i < colors.Length; i++ )
		{
			if ( !insideWorld[i] )
			{
				colors[i] = Color.Black;
				continue;
			}

			colors[i] = TerrainPreviewBiomeColors.ColorizeOverlay(
				settings,
				biomeMap[i],
				shadeMap[i],
				heightMap[i] );
		}
	}
}
