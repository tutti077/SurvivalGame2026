namespace Survival;

/// <summary>
/// Softens high-frequency faceting on chunk height grids.
/// Border vertices stay locked so adjacent chunks still share exact edge heights.
/// Avoid on gentle slopes — interior averaging flattens each chunk into a plateau when corners share similar height.
/// </summary>
static class TerrainPreviewChunkHeightSmooth
{
	public static void ApplyInteriorGrid(
		float[] heightsMeters,
		int verticesPerSide,
		int passes,
		float strength01 )
	{
		if ( heightsMeters is null
			|| heightsMeters.Length != verticesPerSide * verticesPerSide
			|| passes <= 0
			|| strength01 <= 0.0001f
			|| verticesPerSide < 5 )
			return;

		strength01 = Math.Clamp( strength01, 0f, 1f );
		var scratch = new float[heightsMeters.Length];

		for ( var pass = 0; pass < passes; pass++ )
		{
			Array.Copy( heightsMeters, scratch, heightsMeters.Length );

			for ( var iy = 1; iy < verticesPerSide - 1; iy++ )
			{
				for ( var ix = 1; ix < verticesPerSide - 1; ix++ )
				{
					var idx = (iy * verticesPerSide) + ix;
					var left = scratch[idx - 1];
					var right = scratch[idx + 1];
					var down = scratch[idx - verticesPerSide];
					var up = scratch[idx + verticesPerSide];
					var avg = (left + right + down + up) * 0.25f;
					heightsMeters[idx] = Lerp( scratch[idx], avg, strength01 );
				}
			}
		}
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t);
}
