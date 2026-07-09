namespace Survival;

/// <summary>
/// Removes tiny dry islands on the world-scale lake/ocean raster (preview PNG + cached open-water mask).
/// Never run per chunk — chunk size (~64 m) is smaller than the minimum speck diameter (~80 m).
/// </summary>
static class TerrainPreviewLandSpeckFilter
{
	public static void ApplyToOceanMask(
		bool[] ocean,
		bool[] insideWorld,
		int width,
		int height,
		float metersPerPixel,
		TerrainPreviewSettings settings )
	{
		if ( !settings.LandSpeckFilterEnabled
			|| ocean is null
			|| insideWorld is null
			|| ocean.Length != insideWorld.Length
			|| ocean.Length != width * height
			|| metersPerPixel <= 0f )
			return;

		var dryLand = new bool[ocean.Length];
		for ( var i = 0; i < ocean.Length; i++ )
			dryLand[i] = insideWorld[i] && !ocean[i];

		TerrainPreviewPatchFilter.RemoveSmallPatches(
			dryLand,
			width,
			height,
			metersPerPixel,
			TerrainPreviewSpeckDiameter.ResolveMeters( settings ) );

		for ( var i = 0; i < ocean.Length; i++ )
		{
			if ( insideWorld[i] && !dryLand[i] )
				ocean[i] = true;
		}
	}
}
