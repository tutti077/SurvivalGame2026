namespace Survival;

/// <summary>Connected-component size stats for bool masks (lakes, biomes, mountains).</summary>
static class TerrainPreviewPatchMetrics
{
	public readonly struct PatchStats
	{
		public int PatchCount { get; init; }
		public float MeanDiameterMeters { get; init; }
		public float MedianDiameterMeters { get; init; }
		public float SmallestDiameterMeters { get; init; }
		public float LargestDiameterMeters { get; init; }
		public int MaskedPixelCount { get; init; }
	}

	public static PatchStats Measure(
		bool[] mask,
		int width,
		int height,
		float metersPerPixel )
	{
		if ( mask is null || mask.Length != width * height || metersPerPixel <= 0f )
			return default;

		var diameters = new List<float>( 64 );
		var visited = new bool[mask.Length];
		var component = new List<int>( 256 );

		for ( var idx = 0; idx < mask.Length; idx++ )
		{
			if ( visited[idx] || !mask[idx] )
				continue;

			component.Clear();
			FloodFill( mask, width, height, idx, visited, component );
			diameters.Add( ComputeComponentDiameterMeters( width, component, metersPerPixel ) );
		}

		if ( diameters.Count == 0 )
			return default;

		diameters.Sort();
		var sum = 0f;
		var pixels = 0;
		for ( var i = 0; i < mask.Length; i++ )
		{
			if ( !mask[i] )
				continue;

			pixels++;
		}

		foreach ( var d in diameters )
			sum += d;

		return new PatchStats
		{
			PatchCount = diameters.Count,
			MeanDiameterMeters = sum / diameters.Count,
			MedianDiameterMeters = diameters[diameters.Count / 2],
			SmallestDiameterMeters = diameters[0],
			LargestDiameterMeters = diameters[^1],
			MaskedPixelCount = pixels,
		};
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
			TryEnqueue( x - 1, y, mask, visited, queue, width, height );
			TryEnqueue( x + 1, y, mask, visited, queue, width, height );
			TryEnqueue( x, y - 1, mask, visited, queue, width, height );
			TryEnqueue( x, y + 1, mask, visited, queue, width, height );
		}
	}

	static void TryEnqueue(
		int x,
		int y,
		bool[] mask,
		bool[] visited,
		Queue<int> queue,
		int width,
		int height )
	{
		if ( x < 0 || y < 0 || x >= width || y >= height )
			return;

		var idx = (y * width) + x;
		if ( visited[idx] || !mask[idx] )
			return;

		visited[idx] = true;
		queue.Enqueue( idx );
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
}
