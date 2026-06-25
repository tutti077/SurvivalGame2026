namespace Survival;

/// <summary>Deterministic value noise + FBM for terrain preview (no engine deps).</summary>
static class TerrainPreviewNoise
{
	public static float Fbm( int seed, float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f )
	{
		var sum = 0f;
		var amp = 1f;
		var freq = 1f;
		var norm = 0f;

		for ( var i = 0; i < octaves; i++ )
		{
			sum += ValueNoise( seed + i * 7919, x * freq, y * freq ) * amp;
			norm += amp;
			amp *= gain;
			freq *= lacunarity;
		}

		return norm > 0f ? sum / norm : 0f;
	}

	public static float RidgedFbm( int seed, float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f )
	{
		var sum = 0f;
		var amp = 1f;
		var freq = 1f;
		var norm = 0f;

		for ( var i = 0; i < octaves; i++ )
		{
			var n = ValueNoise( seed + i * 7919, x * freq, y * freq );
			n = 1f - MathF.Abs( n * 2f - 1f );
			sum += n * amp;
			norm += amp;
			amp *= gain;
			freq *= lacunarity;
		}

		return norm > 0f ? sum / norm : 0f;
	}

	static float ValueNoise( int seed, float x, float y )
	{
		var x0 = (int)MathF.Floor( x );
		var y0 = (int)MathF.Floor( y );
		var fx = x - x0;
		var fy = y - y0;

		var sx = Smooth( fx );
		var sy = Smooth( fy );

		var n00 = Hash01( seed, x0, y0 );
		var n10 = Hash01( seed, x0 + 1, y0 );
		var n01 = Hash01( seed, x0, y0 + 1 );
		var n11 = Hash01( seed, x0 + 1, y0 + 1 );

		var nx0 = Lerp( n00, n10, sx );
		var nx1 = Lerp( n01, n11, sx );
		return Lerp( nx0, nx1, sy );
	}

	static float Hash01( int seed, int x, int y )
	{
		unchecked
		{
			var n = seed;
			n = (n << 13) ^ n;
			n = n * (n * n * 15731 + 789221) + 1376312589;
			n += x * 374761393 + y * 668265263;
			n = (n ^ (n >> 13)) * 1274126177;
			return (n & 0x7fffffff) / (float)0x7fffffff;
		}
	}

	static float Smooth( float t ) => t * t * (3f - 2f * t );

	static float Lerp( float a, float b, float t ) => a + (b - a) * t;
}
