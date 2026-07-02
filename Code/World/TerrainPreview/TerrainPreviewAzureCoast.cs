namespace Survival;

/// <summary>
/// Teal shoreline strips — dry land within N meters of inland lakes or rim ocean, sparse angular sectors far from spawn.
/// </summary>
static class TerrainPreviewAzureCoast
{
	const int SectorCount = 24;

	public static void BuildMask(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		bool[] openWater,
		int res,
		float radius,
		float diameter,
		out bool[] azureCoast )
	{
		var count = res * res;
		azureCoast = new bool[count];
		if ( !settings.EnableAzureCoastBiome
			|| landDisk is null
			|| openWater is null
			|| landDisk.Length != count
			|| openWater.Length != count
			|| res <= 0 )
			return;

		var metersPerPixel = diameter / Math.Max( 1, res );
		if ( metersPerPixel <= 0f )
			return;

		var widthMeters = Math.Max( 10f, settings.AzureCoastWidthMeters );
		var widthPixels = Math.Max( 1, (int)MathF.Ceiling( widthMeters / metersPerPixel ) );
		var minSpawn = Math.Max( 0f, settings.AzureCoastMinDistanceFromSpawnMeters );
		var landRadius = Math.Max( 1f, settings.LandRadiusMeters );
		var seed = settings.WorldSeed;

		var distancePixels = new int[count];
		var nearestWaterX = new float[count];
		var nearestWaterY = new float[count];
		BuildDistanceToOpenWater(
			landDisk, openWater, res, radius, diameter, metersPerPixel, widthPixels, distancePixels, nearestWaterX, nearestWaterY );

		var sectorPickThreshold = ComputeSectorPickThreshold( settings );
		var alongScale = 1f / Math.Max( 200f, settings.AzureCoastAlongShoreRunMeters );
		var runCutoff = Math.Clamp( settings.AzureCoastAlongShoreRunCutoff01, 0.1f, 0.9f );

		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				if ( !landDisk[idx] || openWater[idx] )
					continue;

				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px, py, res, radius, diameter, out var wx, out var wy );
				var spawnDist = MathF.Sqrt( (wx * wx) + (wy * wy) );
				if ( spawnDist < minSpawn )
					continue;

				var distToLakeMeters = distancePixels[idx] * metersPerPixel;
				var distToRimMeters = landRadius - spawnDist;

				var isRimCoast = settings.AzureCoastIncludeRimOcean
					&& distToRimMeters > 0.001f
					&& distToRimMeters <= widthMeters
					&& distToRimMeters <= distToLakeMeters;

				var isLakeCoast = distToLakeMeters > 0.001f
					&& distToLakeMeters <= widthMeters
					&& distToLakeMeters < distToRimMeters;

				if ( !isRimCoast && !isLakeCoast )
					continue;

				var sector = WorldAngleToSector( wx, wy );
				if ( SectorPick01( seed + 400, sector ) < sectorPickThreshold )
					continue;

				float waterX;
				float waterY;
				if ( isRimCoast )
				{
					var radialScale = landRadius / Math.Max( spawnDist, 0.001f );
					waterX = wx * radialScale;
					waterY = wy * radialScale;
				}
				else
				{
					waterX = nearestWaterX[idx];
					waterY = nearestWaterY[idx];
				}

				var inlandX = wx - waterX;
				var inlandY = wy - waterY;
				var inlandLen = MathF.Sqrt( (inlandX * inlandX) + (inlandY * inlandY ) );
				if ( inlandLen <= 0.001f )
					continue;

				var tangentX = -inlandY / inlandLen;
				var tangentY = inlandX / inlandLen;
				var alongMeters = (wx * tangentX) + (wy * tangentY);
				var runNoise = TerrainPreviewNoise.Fbm(
					seed + 892,
					alongMeters * alongScale,
					SectorPick01( seed + 17, sector ) * 6f,
					2 );
				if ( runNoise < runCutoff )
					continue;

				azureCoast[idx] = true;
			}
		}
	}

	static int WorldAngleToSector( float wx, float wy )
	{
		var angle = MathF.Atan2( wy, wx );
		var sector = (int)MathF.Floor( ((angle + MathF.PI) / (MathF.PI * 2f)) * SectorCount );
		return Math.Clamp( sector, 0, SectorCount - 1 );
	}

	static float ComputeSectorPickThreshold( TerrainPreviewSettings settings )
	{
		var active = Math.Clamp( settings.AzureCoastTargetRegionCount, 4, 20 );
		return Math.Clamp( 1f - (active / (float)SectorCount), 0.15f, 0.9f );
	}

	static float SectorPick01( int seed, int sector )
	{
		var hash = HashCode.Combine( seed, sector );
		return (hash & 0x7fffffff) / (float)int.MaxValue;
	}

	static void BuildDistanceToOpenWater(
		bool[] landDisk,
		bool[] openWater,
		int res,
		float radius,
		float diameter,
		float metersPerPixel,
		int maxSearchPixels,
		int[] distancePixels,
		float[] nearestWaterX,
		float[] nearestWaterY )
	{
		var count = res * res;
		const int inf = int.MaxValue / 4;
		Array.Fill( distancePixels, inf );

		for ( var i = 0; i < count; i++ )
		{
			if ( !landDisk[i] )
				continue;

			if ( openWater[i] )
			{
				distancePixels[i] = 0;
				continue;
			}
		}

		// Forward pass — chamfer 3-4 distance to nearest open-water land pixel.
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

		maxSearchPixels = Math.Max( maxSearchPixels, 1 );
		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				if ( !landDisk[idx] || openWater[idx] )
					continue;

				if ( distancePixels[idx] > maxSearchPixels )
				{
					distancePixels[idx] = inf;
					continue;
				}

				FindNearestWaterInWindow(
					openWater, landDisk, res, radius, diameter, px, py, maxSearchPixels,
					out nearestWaterX[idx], out nearestWaterY[idx], out var exactPixels );
				if ( exactPixels < inf )
					distancePixels[idx] = exactPixels;
			}
		}

		_ = metersPerPixel;
	}

	static void FindNearestWaterInWindow(
		bool[] openWater,
		bool[] landDisk,
		int res,
		float radius,
		float diameter,
		int px,
		int py,
		int radiusPixels,
		out float nearestWorldX,
		out float nearestWorldY,
		out int nearestPixels )
	{
		nearestWorldX = 0f;
		nearestWorldY = 0f;
		nearestPixels = int.MaxValue / 4;
		var bestDistSq = int.MaxValue;

		for ( var oy = -radiusPixels; oy <= radiusPixels; oy++ )
		{
			var ny = py + oy;
			if ( ny < 0 || ny >= res )
				continue;

			for ( var ox = -radiusPixels; ox <= radiusPixels; ox++ )
			{
				var nx = px + ox;
				if ( nx < 0 || nx >= res )
					continue;

				var nIdx = (ny * res) + nx;
				if ( !landDisk[nIdx] || !openWater[nIdx] )
					continue;

				var distSq = (ox * ox) + (oy * oy );
				if ( distSq >= bestDistSq )
					continue;

				bestDistSq = distSq;
				nearestPixels = (int)MathF.Round( MathF.Sqrt( distSq ) );
				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					nx, ny, res, radius, diameter, out nearestWorldX, out nearestWorldY );
			}
		}
	}
}
