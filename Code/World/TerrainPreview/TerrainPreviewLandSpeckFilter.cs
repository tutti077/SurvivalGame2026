namespace Survival;

/// <summary>Removes tiny dry-land islands on raster masks (preview PNG + chunk height grids).</summary>
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

	public static void ApplyToHeightGrid(
		float[] heightsMeters,
		int verticesPerSide,
		float metersPerVertex,
		TerrainPreviewSettings settings )
	{
		if ( !settings.LandSpeckFilterEnabled
			|| heightsMeters is null
			|| heightsMeters.Length != verticesPerSide * verticesPerSide
			|| metersPerVertex <= 0f )
			return;

		var dryLand = new bool[heightsMeters.Length];
		for ( var i = 0; i < heightsMeters.Length; i++ )
			dryLand[i] = heightsMeters[i] >= settings.SeaLevelMeters;

		TerrainPreviewPatchFilter.RemoveSmallPatches(
			dryLand,
			verticesPerSide,
			verticesPerSide,
			metersPerVertex,
			TerrainPreviewSpeckDiameter.ResolveMeters( settings ) );

		for ( var i = 0; i < heightsMeters.Length; i++ )
		{
			if ( !dryLand[i] )
				heightsMeters[i] = settings.SeaLevelMeters;
		}
	}
}
