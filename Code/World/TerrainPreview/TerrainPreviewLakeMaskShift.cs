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

	/// <summary>World position of the closest dry land pixel to spawn (mask below threshold at offset 0).</summary>
	public static bool TryFindNearestDryLandWorld(
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		float[] lakeMaskGrid,
		float threshold01,
		float searchRadiusMeters,
		out float dryWorldX,
		out float dryWorldY,
		out float distanceMeters )
	{
		dryWorldX = 0f;
		dryWorldY = 0f;
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
				var dist = MathF.Sqrt( (wx * wx) + (wy * wy ) );
				if ( dist > searchRadiusMeters )
					continue;

				if ( lakeMaskGrid[idx] >= threshold01 )
					continue;

				if ( dist >= nearest )
					continue;

				nearest = dist;
				dryWorldX = wx;
				dryWorldY = wy;
				distanceMeters = dist;
			}
		}

		return nearest < float.MaxValue;
	}

	/// <summary>Wet-pixel centroid in a spawn check disk (world meters, offset 0 mask sample).</summary>
	public static bool TryMeasureWetCentroidInDisk(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		float[] lakeMaskGrid,
		float threshold01,
		float checkRadiusMeters,
		out float centroidXMeters,
		out float centroidYMeters,
		out float wetFraction01 )
	{
		centroidXMeters = 0f;
		centroidYMeters = 0f;
		wetFraction01 = 0f;
		checkRadiusMeters = Math.Max( 5f, checkRadiusMeters );
		const int grid = 16;
		var wetMass = 0f;
		var weightedX = 0f;
		var weightedY = 0f;
		var samples = 0;

		for ( var iy = 0; iy < grid; iy++ )
		{
			for ( var ix = 0; ix < grid; ix++ )
			{
				var ux = ((ix + 0.5f) / grid * 2f) - 1f;
				var uy = ((iy + 0.5f) / grid * 2f) - 1f;
				if ( (ux * ux) + (uy * uy ) > 1f )
					continue;

				var wx = ux * checkRadiusMeters;
				var wy = uy * checkRadiusMeters;
				if ( MathF.Sqrt( (wx * wx) + (wy * wy ) ) > settings.LandRadiusMeters )
					continue;

				samples++;
				if ( !TrySampleMaskAtWorld( wx, wy, 0f, 0f, landDisk, res, radius, diameter, lakeMaskGrid, out var mask01 ) )
					continue;

				if ( mask01 < threshold01 )
					continue;

				var weight = mask01 - threshold01;
				wetMass += weight;
				weightedX += wx * weight;
				weightedY += wy * weight;
			}
		}

		if ( wetMass <= 0.0001f || samples <= 0 )
			return false;

		centroidXMeters = weightedX / wetMass;
		centroidYMeters = weightedY / wetMass;
		wetFraction01 = wetMass / samples;
		return true;
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
