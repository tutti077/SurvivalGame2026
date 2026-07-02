namespace Survival;

/// <summary>Rasterizes a PNG/inspector biome map from world-meter samples. Never used for streamed terrain.</summary>
public static class TerrainBiomeMapPreviewRaster
{
	public static void FillBiomeColors(
		TerrainPreviewSettings worldSettings,
		ITerrainPreviewBackend backend,
		TerrainBiomeMapPreviewOptions preview,
		int resolution,
		bool[] insideWorld,
		bool[] oceanMask,
		Color[] colors )
	{
		var radius = worldSettings.WorldRadiusMeters;
		var diameter = worldSettings.WorldDiameterMeters;
		var res = resolution;
		var biomeMap = new TerrainPreviewBiomeId[res * res];
		var shadeMap = new float[res * res];
		var heightMap = new float[res * res];
		var hasOceanMask = oceanMask is not null && oceanMask.Length == res * res;

		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				if ( !insideWorld[idx] )
				{
					biomeMap[idx] = TerrainPreviewBiomeId.None;
					shadeMap[idx] = 1f;
					heightMap[idx] = 0f;
					continue;
				}

				if ( hasOceanMask && oceanMask[idx] )
				{
					biomeMap[idx] = TerrainPreviewBiomeId.Water;
					shadeMap[idx] = 1f;
					heightMap[idx] = TerrainPreviewOceanByHeight.MetersToHeight01(
						worldSettings, worldSettings.SeaLevelMeters );
					continue;
				}

				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px,
					py,
					res,
					radius,
					diameter,
					out var wx,
					out var wy );
				var sample = backend.Sample( worldSettings, wx, wy );

				var resolved = TerrainPreviewBiomeResolver.Resolve( worldSettings, sample, wx, wy );
				biomeMap[idx] = resolved.BiomeId;
				shadeMap[idx] = resolved.Shade01;
				heightMap[idx] = sample.Height01;
			}
		}

		ApplyPreviewSpeckFilter( biomeMap, res, diameter, preview );
		ApplyBlackwaterPunch( worldSettings, res, radius, diameter, insideWorld, oceanMask, biomeMap, shadeMap );

		for ( var i = 0; i < colors.Length; i++ )
		{
			if ( !insideWorld[i] )
			{
				colors[i] = Color.Black;
				continue;
			}

			colors[i] = TerrainPreviewBiomeColors.ColorizeOverlay(
				worldSettings,
				biomeMap[i],
				shadeMap[i],
				heightMap[i] );
		}
	}

	public static void FillBiomeColors(
		TerrainPreviewSettings worldSettings,
		ITerrainPreviewBackend backend,
		TerrainBiomeMapPreviewOptions preview,
		int resolution,
		bool[] insideWorld,
		Color[] colors )
		=> FillBiomeColors( worldSettings, backend, preview, resolution, insideWorld, oceanMask: null, colors );

	internal static void ApplyPreviewSpeckFilter(
		TerrainPreviewBiomeId[] biomeMap,
		int resolution,
		float worldDiameterMeters,
		TerrainBiomeMapPreviewOptions preview )
	{
		if ( preview is null || !preview.SpeckFilterEnabled )
			return;

		var metersPerPixel = worldDiameterMeters / Math.Max( 64, resolution );
		TerrainPreviewBiomeSpeckFilter.MergeSmallPatches(
			biomeMap,
			resolution,
			resolution,
			metersPerPixel,
			preview.MinPatchDiameterMeters );
	}

	static void ApplyBlackwaterPunch(
		TerrainPreviewSettings settings,
		int resolution,
		float worldRadiusMeters,
		float worldDiameterMeters,
		bool[] insideWorld,
		bool[] oceanMask,
		TerrainPreviewBiomeId[] biomeMap,
		float[] shadeMap )
	{
		if ( !settings.EnableBlackwaterBiome )
			return;

		TerrainPreviewLandDiskFields.EnsureReady( settings );
		var res = resolution;
		var hasOceanMask = oceanMask is not null && oceanMask.Length == res * res;

		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				if ( !insideWorld[idx] )
					continue;

				if ( hasOceanMask && oceanMask[idx] )
					continue;

				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px, py, res, worldRadiusMeters, worldDiameterMeters, out var wx, out var wy );
				if ( !TerrainPreviewLandDiskFields.IsBlackwater( settings, wx, wy ) )
					continue;

				biomeMap[idx] = TerrainPreviewBiomeId.Blackwater;
				shadeMap[idx] = 1f;
			}
		}
	}
}
