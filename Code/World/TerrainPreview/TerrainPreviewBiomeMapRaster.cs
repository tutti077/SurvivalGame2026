namespace Survival;

/// <summary>Legacy entry — delegates to <see cref="TerrainBiomeMapPreviewRaster"/> (PNG preview only).</summary>
public static class TerrainPreviewBiomeMapRaster
{
	public static void FillBiomeColors(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		int res,
		float radius,
		float diameter,
		bool[] insideWorld,
		bool[] ocean,
		Color[] colors )
	{
		TerrainBiomeMapPreviewRaster.FillBiomeColors(
			settings,
			backend,
			TerrainBiomeMapPreviewOptions.FromSettings( settings ),
			res,
			insideWorld,
			ocean,
			colors );
	}

	public static void FillBiomeColors(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		int res,
		float radius,
		float diameter,
		bool[] insideWorld,
		Color[] colors )
		=> FillBiomeColors( settings, backend, res, radius, diameter, insideWorld, ocean: null, colors );
}
