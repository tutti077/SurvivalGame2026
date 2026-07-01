namespace Survival;

/// <summary>
/// Picks lake mask cutoff from a one-time noise sample via coverage quantile.
/// Speck morphology runs once after threshold — not iterated.
/// </summary>
static class TerrainPreviewLakeThresholdSolver
{
	public static float ResolveThreshold01(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		int res,
		float metersPerPixel,
		float[] lakeMaskGrid )
	{
		if ( !settings.EnableInteriorWaterLayer )
			return 2f;

		if ( !settings.LakeAutoThreshold )
			return Math.Clamp( settings.LakeMaskThreshold01, 0.05f, 0.95f );

		var target = Math.Clamp( settings.TargetLakeCoverageOnLand01, 0.05f, 0.33f );
		_ = res;
		_ = metersPerPixel;
		return QuantileThresholdForCoverage( lakeMaskGrid, landDisk, target );
	}

	/// <summary>One noise sample per land pixel — reuse for every threshold trial.</summary>
	public static float[] SampleLakeMaskGrid(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		int res )
	{
		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		var masks = new float[res * res];

		for ( var py = 0; py < res; py++ )
		{
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				return masks;

			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				if ( !landDisk[idx] )
					continue;

				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px, py, res, radius, diameter, out var wx, out var wy );
				masks[idx] = TerrainPreviewLakeMap.SampleMaskAtWorldMeters(
					settings, wx, wy, settings.WorldSeed );
			}
		}

		return masks;
	}

	public static void ApplyThresholdWithSpeck(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		int res,
		float metersPerPixel,
		float[] lakeMaskGrid,
		float threshold01,
		out bool[] openWater,
		out float lakeCoverageOnLand01 )
	{
		openWater = new bool[res * res];
		lakeCoverageOnLand01 = 0f;
		ApplyThresholdInPlace( landDisk, lakeMaskGrid, threshold01, openWater );

		var speckMeters = TerrainPreviewSpeckDiameter.ResolveMeters( settings );
		var landCount = 0;
		for ( var i = 0; i < landDisk.Length; i++ )
		{
			if ( !landDisk[i] )
				continue;

			landCount++;
		}

		if ( landCount <= 0 )
			return;

		TerrainPreviewPatchFilter.RemoveSmallPatches( openWater, res, res, metersPerPixel, speckMeters );
		TerrainPreviewPatchFilter.FillSmallDryIslandsInWater(
			openWater, landDisk, res, res, metersPerPixel, speckMeters );

		lakeCoverageOnLand01 = MeasureLakeFraction( landDisk, openWater );
	}

	static float QuantileThresholdForCoverage(
		float[] lakeMaskGrid,
		bool[] landDisk,
		float targetCoverage01 )
	{
		var landValues = new List<float>( 4096 );
		for ( var i = 0; i < lakeMaskGrid.Length; i++ )
		{
			if ( !landDisk[i] )
				continue;

			landValues.Add( lakeMaskGrid[i] );
		}

		var landCount = landValues.Count;
		if ( landCount <= 0 )
			return 0.5f;

		landValues.Sort();
		var wetCount = (int)MathF.Round( targetCoverage01 * landCount );
		wetCount = Math.Clamp( wetCount, 0, landCount );

		if ( wetCount <= 0 )
			return 0.98f;

		if ( wetCount >= landCount )
			return 0.02f;

		return landValues[landCount - wetCount];
	}

	static void ApplyThresholdInPlace(
		bool[] landDisk,
		float[] lakeMaskGrid,
		float threshold01,
		bool[] openWater )
	{
		for ( var i = 0; i < openWater.Length; i++ )
			openWater[i] = landDisk[i] && lakeMaskGrid[i] >= threshold01;
	}

	static float MeasureLakeFraction( bool[] landDisk, bool[] openWater )
	{
		var landCount = 0;
		var lakeCount = 0;
		for ( var i = 0; i < openWater.Length; i++ )
		{
			if ( !landDisk[i] )
				continue;

			landCount++;
			if ( openWater[i] )
				lakeCount++;
		}

		return landCount > 0 ? lakeCount / (float)landCount : 0f;
	}
}
