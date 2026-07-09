namespace Survival;

/// <summary>
/// Shared display-only edge breakup for biome maps and shorelines (preview PNG + chunk vertex colors).
/// Does not change height, water masks, biome placement, or seed output.
/// </summary>
static class TerrainBiomeEdgeDisplay
{
	public static void ApplyShoreAndBiomeEdgeJitter(
		TerrainPreviewSettings settings,
		int width,
		int height,
		bool[] insideWorld,
		bool[] isWater,
		TerrainPreviewBiomeId[] biomeMap,
		Color[] colors )
	{
		if ( settings is null
			|| insideWorld is null
			|| colors is null
			|| width < 3
			|| height < 3
			|| insideWorld.Length != colors.Length )
			return;

		var hasWater = isWater is not null && isWater.Length == colors.Length;
		var hasBiomes = biomeMap is not null && biomeMap.Length == colors.Length;
		var seed = settings.WorldSeed;
		var stride = width;

		for ( var y = 1; y < height - 1; y++ )
		{
			for ( var x = 1; x < width - 1; x++ )
			{
				var idx = (y * stride) + x;
				if ( !insideWorld[idx] )
					continue;

				var centerWater = hasWater && isWater[idx];
				var centerBiome = hasBiomes ? biomeMap[idx] : TerrainPreviewBiomeId.None;
				if ( !IsDisplayEdge( insideWorld, isWater, biomeMap, idx, stride, centerWater, centerBiome ) )
					continue;

				var jitter = (Hash01( x, y, seed ) - 0.5f) * 0.12f;
				colors[idx] = AdjustBrightness( colors[idx], 1f + jitter );
			}
		}
	}

	static bool IsDisplayEdge(
		bool[] insideWorld,
		bool[] isWater,
		TerrainPreviewBiomeId[] biomeMap,
		int idx,
		int stride,
		bool centerWater,
		TerrainPreviewBiomeId centerBiome )
	{
		var left = idx - 1;
		var right = idx + 1;
		var up = idx - stride;
		var down = idx + stride;

		if ( isWater is not null )
		{
			if ( insideWorld[left] && isWater[left] != centerWater ) return true;
			if ( insideWorld[right] && isWater[right] != centerWater ) return true;
			if ( insideWorld[up] && isWater[up] != centerWater ) return true;
			if ( insideWorld[down] && isWater[down] != centerWater ) return true;
		}

		if ( biomeMap is null )
			return false;

		if ( insideWorld[left] && biomeMap[left] != centerBiome ) return true;
		if ( insideWorld[right] && biomeMap[right] != centerBiome ) return true;
		if ( insideWorld[up] && biomeMap[up] != centerBiome ) return true;
		if ( insideWorld[down] && biomeMap[down] != centerBiome ) return true;
		return false;
	}

	static float Hash01( int x, int y, int seed )
	{
		unchecked
		{
			var h = (uint)(x * 374761393);
			h ^= (uint)(y * 668265263);
			h ^= (uint)(seed * 2246822519);
			h ^= h >> 13;
			h *= 1274126177u;
			h ^= h >> 16;
			return (h & 0x00FFFFFF) / 16777215f;
		}
	}

	static Color AdjustBrightness( Color c, float scale )
	{
		scale = Math.Clamp( scale, 0.78f, 1.22f );
		return new Color(
			Math.Clamp( c.r * scale, 0f, 1f ),
			Math.Clamp( c.g * scale, 0f, 1f ),
			Math.Clamp( c.b * scale, 0f, 1f ),
			c.a );
	}
}
