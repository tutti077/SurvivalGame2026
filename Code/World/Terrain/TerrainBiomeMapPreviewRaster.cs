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
				var landResolved = TerrainPreviewBiomeResolver.ResolveLandOverlay( worldSettings, sample, wx, wy );
				biomeMap[idx] = landResolved.BiomeId;
				shadeMap[idx] = landResolved.Shade01;
			}
		}

		ApplyPreviewSpeckFilter( biomeMap, res, diameter, preview );
		ApplyBlackwaterPunch( worldSettings, res, radius, diameter, insideWorld, oceanMask, biomeMap, shadeMap );

		var isWater = new bool[res * res];
		for ( var i = 0; i < colors.Length; i++ )
		{
			if ( !insideWorld[i] )
			{
				colors[i] = Color.Black;
				isWater[i] = false;
				continue;
			}

			if ( biomeMap[i] == TerrainPreviewBiomeId.Blackwater )
			{
				colors[i] = Color.Black;
				isWater[i] = false;
				continue;
			}

			var px = i % res;
			var py = i / res;
			TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
				px, py, res, radius, diameter, out var wx, out var wy );
			var sample = backend.Sample( worldSettings, wx, wy );
			var displayWater = TerrainShorelineDisplay.IsDisplayWaterColor( worldSettings, wx, wy );
			if ( displayWater )
			{
				colors[i] = TerrainPreviewBiomeColors.PaletteColor( TerrainPreviewBiomeId.Water, 1f );
				biomeMap[i] = TerrainPreviewBiomeId.Water;
				isWater[i] = true;
				continue;
			}

			var landResolved = TerrainPreviewBiomeResolver.ResolveLandOverlay( worldSettings, sample, wx, wy );
			colors[i] = TerrainPreviewBiomeColors.SampleBiomeOverlay(
				worldSettings, sample, wx, wy, landResolved );
			biomeMap[i] = landResolved.BiomeId;
			isWater[i] = false;
		}

		TerrainBiomeEdgeDisplay.ApplyShoreAndBiomeEdgeJitter(
			worldSettings,
			res,
			res,
			insideWorld,
			isWater,
			biomeMap,
			colors );
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
