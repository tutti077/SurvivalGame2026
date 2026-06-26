namespace Survival;

/// <summary>Rasterizes the biome preview map used by <see cref="TerrainWorldManager"/>.</summary>
public static class TerrainWorldPreviewMap
{
	public readonly struct Result
	{
		public Color[] Colors { get; init; }
		public int Resolution { get; init; }
	}

	public static Result Rasterize( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null, int? resolutionOverride = null )
	{
		backend ??= TerrainPreviewBackendRegistry.Active;
		settings.PreviewMode = TerrainPreviewMode.Biomes;

		var res = resolutionOverride ?? settings.ClampedResolution;
		var colors = new Color[res * res];
		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		var insideWorld = new bool[res * res];

		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				var wx = (px + 0.5f) / res * diameter - radius;
				var wy = (py + 0.5f) / res * diameter - radius;
				var sample = backend.Sample( settings, wx, wy );
				insideWorld[idx] = sample.IsInsideWorld;
			}
		}

		TerrainPreviewBiomeMapRaster.FillBiomeColors(
			settings,
			backend,
			res,
			radius,
			diameter,
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
