namespace Survival;

/// <summary>Raster patch filters for mountain specks and oversized lake basins.</summary>
public static class TerrainPreviewMountainSpeckFilter
{
	public static void RemoveOversizedPatches(
		bool[] mask,
		int width,
		int height,
		float metersPerPixel,
		float maxPatchDiameterMeters,
		int maxPasses = 2 )
	{
		if ( mask is null || mask.Length != width * height || metersPerPixel <= 0f )
			return;

		var maxDiameterMeters = Math.Max( 64f, maxPatchDiameterMeters );
		var visited = new bool[mask.Length];
		var component = new List<int>( 256 );

		for ( var pass = 0; pass < maxPasses; pass++ )
		{
			Array.Clear( visited );

			for ( var idx = 0; idx < mask.Length; idx++ )
			{
				if ( visited[idx] || !mask[idx] )
					continue;

				component.Clear();
				FloodFill( mask, width, height, idx, visited, component );
				if ( ComputeComponentDiameterMeters( width, component, metersPerPixel ) <= maxDiameterMeters )
					continue;

				foreach ( var pixel in component )
					mask[pixel] = false;
			}
		}
	}

	public static void RemoveSmallPatches(
		bool[] mountainMask,
		int width,
		int height,
		float metersPerPixel,
		float minPatchDiameterMeters,
		int maxPasses = 2 )
	{
		if ( mountainMask is null || mountainMask.Length != width * height || metersPerPixel <= 0f )
			return;

		var minDiameterMeters = Math.Max( 16f, minPatchDiameterMeters );
		var visited = new bool[mountainMask.Length];
		var component = new List<int>( 256 );

		for ( var pass = 0; pass < maxPasses; pass++ )
		{
			Array.Clear( visited );

			for ( var idx = 0; idx < mountainMask.Length; idx++ )
			{
				if ( visited[idx] || !mountainMask[idx] )
					continue;

				component.Clear();
				FloodFill( mountainMask, width, height, idx, visited, component );
				if ( ComputeComponentDiameterMeters( width, component, metersPerPixel ) >= minDiameterMeters )
					continue;

				foreach ( var pixel in component )
					mountainMask[pixel] = false;
			}
		}
	}

	static float ComputeComponentDiameterMeters( int width, List<int> component, float metersPerPixel )
	{
		var minX = int.MaxValue;
		var maxX = int.MinValue;
		var minY = int.MaxValue;
		var maxY = int.MinValue;

		foreach ( var idx in component )
		{
			var x = idx % width;
			var y = idx / width;
			minX = Math.Min( minX, x );
			maxX = Math.Max( maxX, x );
			minY = Math.Min( minY, y );
			maxY = Math.Max( maxY, y );
		}

		var spanPixels = Math.Max( maxX - minX + 1, maxY - minY + 1 );
		return spanPixels * metersPerPixel;
	}

	/// <summary>Flood dry islands fully enclosed by water; fill with water when narrower than min diameter.</summary>
	public static void FillSmallDryIslandsInWater(
		bool[] openWater,
		bool[] landDisk,
		int width,
		int height,
		float metersPerPixel,
		float minIslandDiameterMeters,
		int maxPasses = 2 )
	{
		if ( openWater is null || landDisk is null || openWater.Length != width * height || metersPerPixel <= 0f )
			return;

		var minDiameterMeters = Math.Max( 16f, minIslandDiameterMeters );
		var dry = new bool[openWater.Length];
		for ( var i = 0; i < dry.Length; i++ )
			dry[i] = landDisk[i] && !openWater[i];

		for ( var pass = 0; pass < maxPasses; pass++ )
		{
			var exteriorDry = new bool[dry.Length];
			var queue = new Queue<int>();

			for ( var y = 0; y < height; y++ )
			{
				for ( var x = 0; x < width; x++ )
				{
					var idx = (y * width) + x;
					if ( !dry[idx] )
						continue;

					if ( !landDisk[idx] || IsExteriorDrySeed( dry, landDisk, width, height, x, y ) )
					{
						exteriorDry[idx] = true;
						queue.Enqueue( idx );
					}
				}
			}

			while ( queue.Count > 0 )
			{
				var idx = queue.Dequeue();
				var x = idx % width;
				var y = idx / width;
				TryEnqueueDry( x - 1, y, dry, exteriorDry, queue, width, height );
				TryEnqueueDry( x + 1, y, dry, exteriorDry, queue, width, height );
				TryEnqueueDry( x, y - 1, dry, exteriorDry, queue, width, height );
				TryEnqueueDry( x, y + 1, dry, exteriorDry, queue, width, height );
			}

			var visited = new bool[dry.Length];
			var component = new List<int>( 256 );
			for ( var idx = 0; idx < dry.Length; idx++ )
			{
				if ( visited[idx] || !dry[idx] || exteriorDry[idx] )
					continue;

				component.Clear();
				FloodFill( dry, width, height, idx, visited, component );
				if ( ComputeComponentDiameterMeters( width, component, metersPerPixel ) >= minDiameterMeters )
					continue;

				foreach ( var pixel in component )
					openWater[pixel] = true;
			}
		}
	}

	static bool IsExteriorDrySeed( bool[] dry, bool[] landDisk, int width, int height, int x, int y )
	{
		if ( x == 0 || y == 0 || x == width - 1 || y == height - 1 )
			return true;

		var neighbors = new (int X, int Y)[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) };
		foreach ( var (nx, ny) in neighbors )
		{
			var nIdx = (ny * width) + nx;
			if ( !landDisk[nIdx] )
				return true;
		}

		return false;
	}

	static void TryEnqueueDry(
		int x,
		int y,
		bool[] dry,
		bool[] exteriorDry,
		Queue<int> queue,
		int width,
		int height )
	{
		if ( x < 0 || y < 0 || x >= width || y >= height )
			return;

		var idx = (y * width) + x;
		if ( exteriorDry[idx] || !dry[idx] )
			return;

		exteriorDry[idx] = true;
		queue.Enqueue( idx );
	}

	static void FloodFill(
		bool[] mask,
		int width,
		int height,
		int startIdx,
		bool[] visited,
		List<int> component )
	{
		var queue = new Queue<int>();
		queue.Enqueue( startIdx );
		visited[startIdx] = true;

		while ( queue.Count > 0 )
		{
			var idx = queue.Dequeue();
			component.Add( idx );

			var x = idx % width;
			var y = idx / width;
			TryEnqueue( x - 1, y, width, height, mask, visited, queue );
			TryEnqueue( x + 1, y, width, height, mask, visited, queue );
			TryEnqueue( x, y - 1, width, height, mask, visited, queue );
			TryEnqueue( x, y + 1, width, height, mask, visited, queue );
		}
	}

	static void TryEnqueue(
		int x,
		int y,
		int width,
		int height,
		bool[] mask,
		bool[] visited,
		Queue<int> queue )
	{
		if ( x < 0 || y < 0 || x >= width || y >= height )
			return;

		var idx = (y * width) + x;
		if ( visited[idx] || !mask[idx] )
			return;

		visited[idx] = true;
		queue.Enqueue( idx );
	}
}
