namespace Survival;

public readonly struct TerrainPreviewGenerateResult
{
	public Color[] Colors { get; init; }
	public TerrainPreviewWaterCoverageStats WaterCoverage { get; init; }
	public TerrainPreviewGenerationMetrics Metrics { get; init; }
}