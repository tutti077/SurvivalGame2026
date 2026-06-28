namespace Survival;

/// <summary>Rasterizes the inspector biome map PNG. Display export only — never read back for generation.</summary>
public static class TerrainWorldPreviewMap
{
	public readonly struct Result
	{
		public Color[] Colors { get; init; }
		public int Resolution { get; init; }
	}

	public static Result Rasterize(
		TerrainPreviewSettings worldSettings,
		ITerrainPreviewBackend backend = null,
		TerrainBiomeMapPreviewOptions preview = null,
		int? resolutionOverride = null )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;
		preview ??= TerrainBiomeMapPreviewOptions.FromSettings( worldSettings );

		var res = resolutionOverride ?? worldSettings.ClampedResolution;
		var colors = new Color[res * res];
		var radius = worldSettings.WorldRadiusMeters;
		var diameter = worldSettings.WorldDiameterMeters;
		var insideWorld = new bool[res * res];

		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px,
					py,
					res,
					radius,
					diameter,
					out var wx,
					out var wy );
				var sample = backend.Sample( worldSettings, wx, wy );
				insideWorld[idx] = sample.IsInsideWorld;
			}
		}

		TerrainBiomeMapPreviewRaster.FillBiomeColors(
			worldSettings,
			backend,
			preview,
			res,
			insideWorld,
			colors );

		return new Result
		{
			Colors = colors,
			Resolution = res,
		};
	}

	public static Bitmap ToBitmap( Result result )
	{
		var bitmap = new Bitmap( result.Resolution, result.Resolution );
		bitmap.SetPixels( result.Colors );
		return bitmap;
	}
}
