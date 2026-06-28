namespace Survival;

/// <summary>Removes tiny land-biome islands on PNG preview maps (world-meter threshold).</summary>
public static class TerrainPreviewBiomeSpeckFilter
{
	public static void MergeSmallPatches(
		TerrainPreviewBiomeId[] map,
		int width,
		int height,
		float metersPerPixel,
		float minPatchDiameterMeters,
		int maxPasses = 2 )
	{
		if ( map is null || map.Length != width * height || metersPerPixel <= 0f )
			return;

		var minDiameterMeters = Math.Max( 8f, minPatchDiameterMeters );
		var visited = new bool[map.Length];
		var component = new List<int>( 256 );
		var componentSet = new HashSet<int>();
		var neighborVotes = new Dictionary<TerrainPreviewBiomeId, int>();

		for ( var pass = 0; pass < maxPasses; pass++ )
		{
			Array.Clear( visited );

			for ( var idx = 0; idx < map.Length; idx++ )
			{
				if ( visited[idx] || !IsMergeableLandBiome( map[idx] ) )
					continue;

				component.Clear();
				componentSet.Clear();
				FloodFill( map, width, height, idx, map[idx], visited, component, componentSet );
				if ( ComputeComponentDiameterMeters( width, component, metersPerPixel ) >= minDiameterMeters )
					continue;

				neighborVotes.Clear();
				foreach ( var pixel in component )
					VoteNeighborBiomes( map, width, height, pixel, map[idx], componentSet, neighborVotes );

				if ( !TryPickReplacementBiome( neighborVotes, out var replacement ) )
					continue;

				foreach ( var pixel in component )
					map[pixel] = replacement;
			}
		}
	}

	public static void MergeSmallPatches(
		TerrainPreviewBiomeId[] map,
		int width,
		int height,
		float metersPerPixel,
		TerrainBiomeMapPreviewOptions preview,
		int maxPasses = 2 )
	{
		if ( preview is null || !preview.SpeckFilterEnabled )
			return;

		MergeSmallPatches( map, width, height, metersPerPixel, preview.MinPatchDiameterMeters, maxPasses );
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

	static void FloodFill(
		TerrainPreviewBiomeId[] map,
		int width,
		int height,
		int startIdx,
		TerrainPreviewBiomeId biomeId,
		bool[] visited,
		List<int> component,
		HashSet<int> componentSet )
	{
		var queue = new Queue<int>();
		queue.Enqueue( startIdx );
		visited[startIdx] = true;
		componentSet.Add( startIdx );

		while ( queue.Count > 0 )
		{
			var idx = queue.Dequeue();
			component.Add( idx );

			var x = idx % width;
			var y = idx / width;
			TryEnqueue( x - 1, y, width, height, biomeId, map, visited, componentSet, queue );
			TryEnqueue( x + 1, y, width, height, biomeId, map, visited, componentSet, queue );
			TryEnqueue( x, y - 1, width, height, biomeId, map, visited, componentSet, queue );
			TryEnqueue( x, y + 1, width, height, biomeId, map, visited, componentSet, queue );
		}
	}

	static void TryEnqueue(
		int x,
		int y,
		int width,
		int height,
		TerrainPreviewBiomeId biomeId,
		TerrainPreviewBiomeId[] map,
		bool[] visited,
		HashSet<int> componentSet,
		Queue<int> queue )
	{
		if ( x < 0 || y < 0 || x >= width || y >= height )
			return;

		var idx = (y * width) + x;
		if ( visited[idx] || map[idx] != biomeId )
			return;

		visited[idx] = true;
		componentSet.Add( idx );
		queue.Enqueue( idx );
	}

	static void VoteNeighborBiomes(
		TerrainPreviewBiomeId[] map,
		int width,
		int height,
		int idx,
		TerrainPreviewBiomeId componentBiome,
		HashSet<int> component,
		Dictionary<TerrainPreviewBiomeId, int> votes )
	{
		var x = idx % width;
		var y = idx / width;
		VoteAt( x - 1, y, width, height, componentBiome, map, component, votes );
		VoteAt( x + 1, y, width, height, componentBiome, map, component, votes );
		VoteAt( x, y - 1, width, height, componentBiome, map, component, votes );
		VoteAt( x, y + 1, width, height, componentBiome, map, component, votes );
	}

	static void VoteAt(
		int x,
		int y,
		int width,
		int height,
		TerrainPreviewBiomeId componentBiome,
		TerrainPreviewBiomeId[] map,
		HashSet<int> component,
		Dictionary<TerrainPreviewBiomeId, int> votes )
	{
		if ( x < 0 || y < 0 || x >= width || y >= height )
			return;

		var idx = (y * width) + x;
		if ( map[idx] == componentBiome || component.Contains( idx ) )
			return;

		if ( map[idx] == TerrainPreviewBiomeId.None )
			return;

		votes.TryGetValue( map[idx], out var count );
		votes[map[idx]] = count + 1;
	}

	static bool TryPickReplacementBiome(
		Dictionary<TerrainPreviewBiomeId, int> votes,
		out TerrainPreviewBiomeId replacement )
	{
		replacement = TerrainPreviewBiomeId.None;
		var bestCount = 0;
		foreach ( var pair in votes )
		{
			if ( pair.Value <= bestCount )
				continue;

			bestCount = pair.Value;
			replacement = pair.Key;
		}

		return replacement != TerrainPreviewBiomeId.None;
	}

	static bool IsMergeableLandBiome( TerrainPreviewBiomeId biomeId )
		=> biomeId is TerrainPreviewBiomeId.CloverHills
			or TerrainPreviewBiomeId.RedwoodForest
			or TerrainPreviewBiomeId.AmberDunes;
}
