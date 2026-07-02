namespace Survival;

/// <summary>
/// Sparse black circular patches punched over land biomes — not water-related; avoids lakes and mountain clearance.
/// Spots are placed one per angular wedge (seed-shuffled) so they spread around the disk while staying randomized within each wedge.
/// </summary>
static class TerrainPreviewBlackwater
{
	public readonly struct Spot
	{
		public float CenterXMeters { get; init; }
		public float CenterYMeters { get; init; }
		public float RadiusMeters { get; init; }
	}

	public static void BuildMask(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		bool[] openWater,
		int res,
		float radius,
		float diameter,
		out bool[] blackwater,
		out Spot[] spots )
	{
		var count = res * res;
		blackwater = new bool[count];
		spots = Array.Empty<Spot>();
		if ( !settings.EnableBlackwaterBiome
			|| landDisk is null
			|| openWater is null
			|| landDisk.Length != count
			|| openWater.Length != count
			|| res <= 0 )
			return;

		var metersPerPixel = diameter / Math.Max( 1, res );
		if ( metersPerPixel <= 0f )
			return;

		var minDiameter = Math.Min( settings.BlackwaterMinDiameterMeters, settings.BlackwaterMaxDiameterMeters );
		var maxDiameter = Math.Max( settings.BlackwaterMinDiameterMeters, settings.BlackwaterMaxDiameterMeters );
		minDiameter = Math.Max( 20f, minDiameter );
		maxDiameter = Math.Max( minDiameter, maxDiameter );

		var clearanceMeters = Math.Max( 0f, settings.BlackwaterMountainClearanceMeters );
		var minSeparation = Math.Max( 0f, settings.BlackwaterMinDistanceFromOtherMeters );
		var picked = PickSpots( settings, minDiameter, maxDiameter, clearanceMeters, minSeparation );
		spots = picked;

		foreach ( var spot in spots )
			StampCircle( blackwater, landDisk, openWater, res, radius, diameter, spot );
	}

	static Spot[] PickSpots(
		TerrainPreviewSettings settings,
		float minDiameter,
		float maxDiameter,
		float clearanceMeters,
		float minSeparationMeters )
	{
		var target = Math.Clamp( settings.BlackwaterSpotCount, 0, 64 );
		if ( target <= 0 )
			return Array.Empty<Spot>();

		var seed = settings.WorldSeed;
		var landRadius = Math.Max( 1f, settings.LandRadiusMeters );
		var minSpawn = Math.Max( 0f, settings.BlackwaterMinDistanceFromSpawnMeters );
		var maxSpawn = settings.BlackwaterMaxDistanceFromSpawnMeters;
		if ( maxSpawn <= minSpawn )
			maxSpawn = landRadius;

		var spots = new List<Spot>( target );
		var sectorCount = target;
		var sectorWidth = (MathF.PI * 2f) / sectorCount;
		var sectorOrder = BuildShuffledSectorOrder( seed, sectorCount );
		var attemptsPerSector = 250;
		var filledSectors = new bool[sectorCount];

		foreach ( var sectorIndex in sectorOrder )
		{
			if ( spots.Count >= target )
				break;

			var sectorStart = sectorIndex * sectorWidth;
			if ( TryPlaceSpotInAngularRange(
				settings,
				seed,
				sectorIndex,
				sectorStart,
				sectorWidth,
				0,
				minDiameter,
				maxDiameter,
				minSpawn,
				maxSpawn,
				landRadius,
				clearanceMeters,
				minSeparationMeters,
				attemptsPerSector,
				spots ) )
			{
				filledSectors[sectorIndex] = true;
				continue;
			}

			// Widen into neighboring wedges when mountains/water block the core sector.
			var bleed = sectorWidth * 0.35f;
			if ( TryPlaceSpotInAngularRange(
				settings,
				seed,
				sectorIndex,
				sectorStart - bleed,
				sectorWidth + (bleed * 2f),
				1,
				minDiameter,
				maxDiameter,
				minSpawn,
				maxSpawn,
				landRadius,
				clearanceMeters,
				minSeparationMeters,
				attemptsPerSector,
				spots ) )
				filledSectors[sectorIndex] = true;
		}

		// Retry empty wedges before falling back to unconstrained random placement.
		var maxUnfilledPasses = sectorCount * attemptsPerSector;
		for ( var pass = 0; pass < maxUnfilledPasses && spots.Count < target; pass++ )
		{
			var sectorIndex = sectorOrder[pass % sectorCount];
			if ( filledSectors[sectorIndex] )
				continue;

			var sectorStart = sectorIndex * sectorWidth;
			var bleed = sectorWidth * 0.75f;
			if ( !TryPlaceSpotInAngularRange(
				settings,
				seed,
				sectorIndex,
				sectorStart - bleed,
				sectorWidth + (bleed * 2f),
				100000 + pass,
				minDiameter,
				maxDiameter,
				minSpawn,
				maxSpawn,
				landRadius,
				clearanceMeters,
				minSeparationMeters,
				1,
				spots ) )
				continue;

			filledSectors[sectorIndex] = true;
		}

		var maxAttempts = target * 250;
		for ( var attempt = 0; attempt < maxAttempts && spots.Count < target; attempt++ )
		{
			var sectorIndex = PickUnfilledSectorIndex( seed, attempt, sectorOrder, filledSectors );
			float angleMin;
			float angleSpan;
			if ( sectorIndex >= 0 )
			{
				var sectorStart = sectorIndex * sectorWidth;
				angleMin = sectorStart;
				angleSpan = sectorWidth;
			}
			else
			{
				angleMin = 0f;
				angleSpan = MathF.PI * 2f;
			}

			if ( !TryPlaceSpotInAngularRange(
				settings,
				seed,
				sectorIndex >= 0 ? sectorIndex : attempt,
				angleMin,
				angleSpan,
				200000 + attempt,
				minDiameter,
				maxDiameter,
				minSpawn,
				maxSpawn,
				landRadius,
				clearanceMeters,
				minSeparationMeters,
				1,
				spots ) )
				continue;

			if ( sectorIndex >= 0 )
				filledSectors[sectorIndex] = true;
		}

		return spots.ToArray();
	}

	static bool TryPlaceSpotInAngularRange(
		TerrainPreviewSettings settings,
		int seed,
		int sectorIndex,
		float angleMinRadians,
		float angleSpanRadians,
		int saltBase,
		float minDiameter,
		float maxDiameter,
		float minSpawn,
		float maxSpawn,
		float landRadius,
		float clearanceMeters,
		float minSeparationMeters,
		int maxAttempts,
		List<Spot> spots )
	{
		if ( angleSpanRadians <= 0f || maxAttempts <= 0 )
			return false;

		var distSpan = Math.Max( 1f, maxSpawn - minSpawn );
		for ( var attempt = 0; attempt < maxAttempts; attempt++ )
		{
			var salt = saltBase + (sectorIndex * 10007) + (attempt * 5);
			var spotDiameter = minDiameter + (Hash01( seed + 7013, salt ) * (maxDiameter - minDiameter));
			var spotRadius = spotDiameter * 0.5f;
			var angle = angleMinRadians + (Hash01( seed + 7013, salt + 1 ) * angleSpanRadians);
			var spawnDist = minSpawn + (Hash01( seed + 7013, salt + 2 ) * distSpan);
			spawnDist = Math.Clamp( spawnDist, minSpawn, landRadius - spotRadius );
			if ( spawnDist < minSpawn || spawnDist + spotRadius > landRadius )
				continue;

			var wx = MathF.Cos( angle ) * spawnDist;
			var wy = MathF.Sin( angle ) * spawnDist;
			var spot = new Spot
			{
				CenterXMeters = wx,
				CenterYMeters = wy,
				RadiusMeters = spotRadius,
			};

			if ( !SpotIsValid( settings, spot, clearanceMeters, landRadius ) )
				continue;

			if ( !HasSeparationFromOtherSpots( spot, spots, minSeparationMeters ) )
				continue;

			spots.Add( spot );
			return true;
		}

		return false;
	}

	static int[] BuildShuffledSectorOrder( int seed, int sectorCount )
	{
		var order = new int[sectorCount];
		for ( var i = 0; i < sectorCount; i++ )
			order[i] = i;

		for ( var i = sectorCount - 1; i > 0; i-- )
		{
			var j = (int)(Hash01( seed + 9001, i ) * (i + 1));
			(order[i], order[j]) = (order[j], order[i]);
		}

		return order;
	}

	static int PickUnfilledSectorIndex( int seed, int attempt, int[] sectorOrder, bool[] filledSectors )
	{
		var unfilled = new List<int>();
		for ( var i = 0; i < sectorOrder.Length; i++ )
		{
			if ( !filledSectors[sectorOrder[i]] )
				unfilled.Add( sectorOrder[i] );
		}

		if ( unfilled.Count == 0 )
			return -1;

		var pick = (int)(Hash01( seed + 9103, attempt ) * unfilled.Count);
		if ( pick >= unfilled.Count )
			pick = unfilled.Count - 1;

		return unfilled[pick];
	}

	static bool SpotIsValid(
		TerrainPreviewSettings settings,
		Spot spot,
		float clearanceMeters,
		float landRadius )
	{
		var innerRadius = spot.RadiusMeters;
		var innerRadiusSq = innerRadius * innerRadius;
		var mountainBuffer = innerRadius + clearanceMeters;
		var mountainBufferSq = mountainBuffer * mountainBuffer;
		var sampleStep = Math.Clamp( Math.Min( 35f, innerRadius * 0.35f ), 18f, 50f );

		for ( var offsetY = -mountainBuffer; offsetY <= mountainBuffer; offsetY += sampleStep )
		{
			for ( var offsetX = -mountainBuffer; offsetX <= mountainBuffer; offsetX += sampleStep )
			{
				var distSq = (offsetX * offsetX) + (offsetY * offsetY );
				if ( distSq > mountainBufferSq )
					continue;

				var wx = spot.CenterXMeters + offsetX;
				var wy = spot.CenterYMeters + offsetY;
				var spawnDist = MathF.Sqrt( (wx * wx) + (wy * wy ) );

				if ( distSq <= innerRadiusSq )
				{
					if ( spawnDist > landRadius )
						return false;

					if ( settings.EnableInteriorWaterLayer
						&& TerrainPreviewLandDiskFields.IsOpenWater( settings, wx, wy ) )
						return false;
				}

				if ( IsMountainAt( settings, wx, wy ) )
					return false;
			}
		}

		return true;
	}

	static bool HasSeparationFromOtherSpots( Spot candidate, List<Spot> existing, float minGapMeters )
	{
		if ( minGapMeters <= 0f || existing.Count == 0 )
			return true;

		foreach ( var other in existing )
		{
			var dx = candidate.CenterXMeters - other.CenterXMeters;
			var dy = candidate.CenterYMeters - other.CenterYMeters;
			var centerDist = MathF.Sqrt( (dx * dx) + (dy * dy ) );
			var minCenterDist = candidate.RadiusMeters + other.RadiusMeters + minGapMeters;
			if ( centerDist < minCenterDist )
				return false;
		}

		return true;
	}

	static bool IsMountainAt( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
		=> TerrainPreviewBiomeResolver.SampleMountainSpawnMask01( settings, worldXMeters, worldYMeters ) >= 0.5f;

	static void StampCircle(
		bool[] blackwater,
		bool[] landDisk,
		bool[] openWater,
		int res,
		float radius,
		float diameter,
		Spot spot )
	{
		var metersPerPixel = diameter / Math.Max( 1, res );
		if ( !TryWorldToRasterBounds(
			spot, res, radius, diameter, metersPerPixel, out var minPx, out var maxPx, out var minPy, out var maxPy ) )
			return;

		var radiusSq = spot.RadiusMeters * spot.RadiusMeters;
		for ( var py = minPy; py <= maxPy; py++ )
		{
			for ( var px = minPx; px <= maxPx; px++ )
			{
				var idx = (py * res) + px;
				if ( !landDisk[idx] || openWater[idx] )
					continue;

				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px, py, res, radius, diameter, out var wx, out var wy );
				var dx = wx - spot.CenterXMeters;
				var dy = wy - spot.CenterYMeters;
				if ( (dx * dx) + (dy * dy ) > radiusSq )
					continue;

				blackwater[idx] = true;
			}
		}
	}

	static bool TryWorldToRasterBounds(
		Spot spot,
		int res,
		float radius,
		float diameter,
		float metersPerPixel,
		out int minPx,
		out int maxPx,
		out int minPy,
		out int maxPy )
	{
		minPx = 0;
		maxPx = 0;
		minPy = 0;
		maxPy = 0;
		if ( diameter <= 0f || metersPerPixel <= 0f || res <= 0 )
			return false;

		var pad = spot.RadiusMeters + metersPerPixel;
		WorldMetersToRasterPixelClamped(
			spot.CenterXMeters - pad, spot.CenterYMeters - pad, res, radius, diameter, out minPx, out minPy );
		WorldMetersToRasterPixelClamped(
			spot.CenterXMeters + pad, spot.CenterYMeters + pad, res, radius, diameter, out maxPx, out maxPy );

		minPx = Math.Clamp( Math.Min( minPx, maxPx ), 0, res - 1 );
		maxPx = Math.Clamp( Math.Max( minPx, maxPx ), 0, res - 1 );
		minPy = Math.Clamp( Math.Min( minPy, maxPy ), 0, res - 1 );
		maxPy = Math.Clamp( Math.Max( minPy, maxPy ), 0, res - 1 );
		return true;
	}

	static void WorldMetersToRasterPixelClamped(
		float worldXMeters,
		float worldYMeters,
		int res,
		float radius,
		float diameter,
		out int px,
		out int py )
	{
		py = (int)MathF.Floor( ((worldYMeters + radius) / diameter) * res );
		var pxMirror = (int)MathF.Floor( ((worldXMeters + radius) / diameter) * res );
		px = (res - 1) - pxMirror;
		px = Math.Clamp( px, 0, res - 1 );
		py = Math.Clamp( py, 0, res - 1 );
	}

	static float Hash01( int seed, int salt )
	{
		var hash = HashCode.Combine( seed, salt );
		return (hash & 0x7fffffff) / (float)int.MaxValue;
	}
}
