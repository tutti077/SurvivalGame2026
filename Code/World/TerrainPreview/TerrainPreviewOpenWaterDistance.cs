namespace Survival;

/// <summary>Chamfer distance from dry land pixels to nearest open-water pixel on the land disk.</summary>
static class TerrainPreviewOpenWaterDistance
{
	public static void BuildDistanceMeters(
		bool[] landDisk,
		bool[] openWater,
		int res,
		float metersPerPixel,
		float[] distanceMetersOut )
	{
		var count = res * res;
		if ( landDisk is null
			|| openWater is null
			|| distanceMetersOut is null
			|| landDisk.Length != count
			|| openWater.Length != count
			|| distanceMetersOut.Length != count
			|| res <= 0
			|| metersPerPixel <= 0f )
			return;

		const int inf = int.MaxValue / 4;
		var distancePixels = new int[count];
		Array.Fill( distancePixels, inf );

		for ( var i = 0; i < count; i++ )
		{
			if ( !landDisk[i] )
				continue;

			if ( openWater[i] )
				distancePixels[i] = 0;
		}

		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				if ( !landDisk[idx] || openWater[idx] )
					continue;

				var best = distancePixels[idx];
				if ( px > 0 )
					best = Math.Min( best, distancePixels[idx - 1] + 3 );

				if ( py > 0 )
					best = Math.Min( best, distancePixels[idx - res] + 3 );

				if ( px > 0 && py > 0 )
					best = Math.Min( best, distancePixels[idx - res - 1] + 4 );

				if ( px < res - 1 && py > 0 )
					best = Math.Min( best, distancePixels[idx - res + 1] + 4 );

				distancePixels[idx] = best;
			}
		}

		for ( var py = res - 1; py >= 0; py-- )
		{
			for ( var px = res - 1; px >= 0; px-- )
			{
				var idx = (py * res) + px;
				if ( !landDisk[idx] || openWater[idx] )
					continue;

				var best = distancePixels[idx];
				if ( px < res - 1 )
					best = Math.Min( best, distancePixels[idx + 1] + 3 );

				if ( py < res - 1 )
					best = Math.Min( best, distancePixels[idx + res] + 3 );

				if ( px < res - 1 && py < res - 1 )
					best = Math.Min( best, distancePixels[idx + res + 1] + 4 );

				if ( px > 0 && py < res - 1 )
					best = Math.Min( best, distancePixels[idx + res - 1] + 4 );

				distancePixels[idx] = best;
			}
		}

		for ( var i = 0; i < count; i++ )
		{
			if ( !landDisk[i] || openWater[i] || distancePixels[i] >= inf )
			{
				distanceMetersOut[i] = float.MaxValue;
				continue;
			}

			distanceMetersOut[i] = (distancePixels[i] / 3f) * metersPerPixel;
		}
	}
}
