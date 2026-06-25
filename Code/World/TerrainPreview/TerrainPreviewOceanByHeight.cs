namespace Survival;

/// <summary>Sea level floor — underwater heights clamp to 0; dry land keeps its value.</summary>
static class TerrainPreviewOceanByHeight
{
	public static float SeaLevel01( TerrainPreviewSettings settings )
		=> Math.Clamp( settings.SeaLevelHeight01, 0f, 1f );

	/// <returns>1 = underwater, 0 = dry land.</returns>
	public static float SampleOcean01( TerrainPreviewSettings settings, float heightBeforeClamp01 )
		=> heightBeforeClamp01 < SeaLevel01( settings ) ? 1f : 0f;

	/// <returns>0 when below sea level, otherwise the original height unchanged.</returns>
	public static float ApplySeaLevelClamp( TerrainPreviewSettings settings, float height01 )
	{
		var sea = SeaLevel01( settings );
		return height01 < sea ? 0f : height01;
	}
}
