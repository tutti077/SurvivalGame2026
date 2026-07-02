namespace Survival;

/// <summary>
/// Removes tiny dry islands on raster masks. Chunk height grids use border-aware filtering
/// so mainland connected to chunk edges is never treated as a removable speck.
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

	/// <summary>
	/// Floods interior dry specks (fully surrounded by water inside the chunk) narrower than min speck.
	/// Dry land touching the chunk border is kept — it continues into neighbor chunks.
	/// </summary>
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

		var width = verticesPerSide;
		var height = verticesPerSide;
		var count = heightsMeters.Length;
		var sea = settings.SeaLevelMeters;
		var minSpeckMeters = TerrainPreviewSpeckDiameter.ResolveMeters( settings );

		var dry = new bool[count];
		for ( var i = 0; i < count; i++ )
			dry[i] = heightsMeters[i] >= sea;

		var exteriorDry = new bool[count];
		var queue = new Queue<int>();

		for ( var y = 0; y < height; y++ )
		{
			for ( var x = 0; x < width; x++ )
			{
				var idx = (y * width) + x;
				if ( !dry[idx] )
					continue;

				if ( x == 0 || y == 0 || x == width - 1 || y == height - 1 )
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
			TryEnqueueExteriorDry( x - 1, y, dry, exteriorDry, queue, width, height );
			TryEnqueueExteriorDry( x + 1, y, dry, exteriorDry, queue, width, height );
			TryEnqueueExteriorDry( x, y - 1, dry, exteriorDry, queue, width, height );
			TryEnqueueExteriorDry( x, y + 1, dry, exteriorDry, queue, width, height );
		}

		var visited = new bool[count];
		var component = new List<int>( 64 );
		for ( var idx = 0; idx < count; idx++ )
		{
			if ( visited[idx] || !dry[idx] || exteriorDry[idx] )
				continue;

			component.Clear();
			FloodFillDry( dry, width, height, idx, visited, component );
			if ( ComputeComponentDiameterMeters( width, component, metersPerVertex ) >= minSpeckMeters )
				continue;

			foreach ( var pixel in component )
				heightsMeters[pixel] = sea;
		}
	}

	static void TryEnqueueExteriorDry(
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

	static void FloodFillDry(
		bool[] dry,
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
			TryEnqueueFill( x - 1, y, dry, visited, queue, width, height );
			TryEnqueueFill( x + 1, y, dry, visited, queue, width, height );
			TryEnqueueFill( x, y - 1, dry, visited, queue, width, height );
			TryEnqueueFill( x, y + 1, dry, visited, queue, width, height );
		}
	}

	static void TryEnqueueFill(
		int x,
		int y,
		bool[] dry,
		bool[] visited,
		Queue<int> queue,
		int width,
		int height )
	{
		if ( x < 0 || y < 0 || x >= width || y >= height )
			return;

		var idx = (y * width) + x;
		if ( visited[idx] || !dry[idx] )
			return;

		visited[idx] = true;
		queue.Enqueue( idx );
	}

	static float ComputeComponentDiameterMeters( int width, List<int> component, float metersPerCell )
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

		var spanCells = Math.Max( maxX - minX + 1, maxY - minY + 1 );
		return spanCells * metersPerCell;
	}
}
