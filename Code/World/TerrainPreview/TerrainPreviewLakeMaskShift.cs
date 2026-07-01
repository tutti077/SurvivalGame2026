namespace Survival;

/// <summary>
/// Reuses a lake mask grid sampled at offset (0,0) to evaluate wet/dry at other mask offsets
/// without re-running noise. noise(world − offset) equals the grid value at world − offset.
/// </summary>
static class TerrainPreviewLakeMaskShift
{
	public static bool TrySampleMaskAtWorld(
		float worldXMeters,
		float worldYMeters,
		float offsetXMeters,
		float offsetYMeters,
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		float[] lakeMaskGrid,
		out float mask01 )
	{
		mask01 = 0f;
		var noiseX = worldXMeters - offsetXMeters;
		var noiseY = worldYMeters - offsetYMeters;
		if ( !TryWorldToRasterIndex( noiseX, noiseY, res, radius, diameter, out var idx ) )
			return false;

		if ( idx < 0 || idx >= lakeMaskGrid.Length )
			return false;

		mask01 = lakeMaskGrid[idx];
		return true;
	}

	public static bool IsWetAtWorld(
		float worldXMeters,
		float worldYMeters,
		float offsetXMeters,
		float offsetYMeters,
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		float[] lakeMaskGrid,
		float threshold01 )
	{
		if ( !TryWorldToRasterIndex( worldXMeters, worldYMeters, res, radius, diameter, out var destIdx ) )
			return false;

		if ( destIdx < 0 || destIdx >= landDisk.Length || !landDisk[destIdx] )
			return false;

		if ( !TrySampleMaskAtWorld(
				worldXMeters, worldYMeters, offsetXMeters, offsetYMeters,
				landDisk, res, radius, diameter, lakeMaskGrid, out var mask01 ) )
			return false;

		return mask01 >= threshold01;
	}

	public static TerrainPreviewSpawnLandCheck.Result MeasureSpawnDiskDry(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		float[] lakeMaskGrid,
		float threshold01,
		float offsetXMeters,
		float offsetYMeters,
		float checkRadiusMeters )
	{
		checkRadiusMeters = Math.Max( 5f, checkRadiusMeters );
		const int grid = 16;
		var samples = 0;
		var dry = 0;

		for ( var iy = 0; iy < grid; iy++ )
		{
			for ( var ix = 0; ix < grid; ix++ )
			{
				var ux = ((ix + 0.5f) / grid * 2f) - 1f;
				var uy = ((iy + 0.5f) / grid * 2f) - 1f;
				if ( (ux * ux) + (uy * uy) > 1f )
					continue;

				var wx = ux * checkRadiusMeters;
				var wy = uy * checkRadiusMeters;
				var dist = MathF.Sqrt( (wx * wx) + (wy * wy) );
				if ( dist > settings.LandRadiusMeters )
					continue;

				samples++;
				if ( !IsWetAtWorld(
						wx, wy, offsetXMeters, offsetYMeters,
						landDisk, res, radius, diameter, lakeMaskGrid, threshold01 ) )
					dry++;
			}
		}

		return new TerrainPreviewSpawnLandCheck.Result
		{
			LandFraction01 = samples > 0 ? dry / (float)samples : 0f,
			SampleCount = samples,
		};
	}

	public static float MeasureNearestWetMeters(
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		float[] lakeMaskGrid,
		float threshold01,
		float offsetXMeters,
		float offsetYMeters,
		float searchRadiusMeters )
	{
		searchRadiusMeters = Math.Max( 10f, searchRadiusMeters );
		var nearest = float.MaxValue;

		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				if ( !landDisk[idx] )
					continue;

				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px, py, res, radius, diameter, out var wx, out var wy );
				var dist = MathF.Sqrt( (wx * wx) + (wy * wy) );
				if ( dist < 1f || dist > searchRadiusMeters )
					continue;

				if ( !IsWetAtWorld(
						wx, wy, offsetXMeters, offsetYMeters,
						landDisk, res, radius, diameter, lakeMaskGrid, threshold01 ) )
					continue;

				nearest = Math.Min( nearest, dist );
			}
		}

		return nearest < float.MaxValue ? nearest : -1f;
	}

	public static bool TryFindNearestWetWorld(
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		float[] lakeMaskGrid,
		float threshold01,
		float offsetXMeters,
		float offsetYMeters,
		float searchRadiusMeters,
		out float wetWorldX,
		out float wetWorldY,
		out float distanceMeters )
	{
		wetWorldX = 0f;
		wetWorldY = 0f;
		distanceMeters = -1f;
		searchRadiusMeters = Math.Max( 10f, searchRadiusMeters );
		var nearest = float.MaxValue;

		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				if ( !landDisk[idx] )
					continue;

				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px, py, res, radius, diameter, out var wx, out var wy );
				var dist = MathF.Sqrt( (wx * wx) + (wy * wy) );
				if ( dist < 1f || dist > searchRadiusMeters )
					continue;

				if ( !IsWetAtWorld(
						wx, wy, offsetXMeters, offsetYMeters,
						landDisk, res, radius, diameter, lakeMaskGrid, threshold01 ) )
					continue;

				if ( dist >= nearest )
					continue;

				nearest = dist;
				wetWorldX = wx;
				wetWorldY = wy;
				distanceMeters = dist;
			}
		}

		return nearest < float.MaxValue;
	}

	static bool TryWorldToRasterIndex(
		float worldXMeters,
		float worldYMeters,
		int res,
		float radius,
		float diameter,
		out int index )
	{
		index = 0;
		if ( diameter <= 0f )
			return false;

		var dist = MathF.Sqrt( worldXMeters * worldXMeters + worldYMeters * worldYMeters );
		if ( dist > radius )
			return false;

		var py = (int)MathF.Floor( ((worldYMeters + radius) / diameter) * res );
		var pxMirror = (int)MathF.Floor( ((worldXMeters + radius) / diameter) * res );
		var px = (res - 1) - pxMirror;
		if ( px < 0 || py < 0 || px >= res || py >= res )
			return false;

		index = (py * res) + px;
		return true;
	}
}
