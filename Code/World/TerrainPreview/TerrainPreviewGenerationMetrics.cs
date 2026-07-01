namespace Survival;

/// <summary>
/// Auto-computed shape stats for tuning loops — written to <c>generation_metrics.json</c> on each PNG export.
/// Lower <see cref="LakeArchipelagoScore"/> and fewer <see cref="LakePatchCount"/> usually mean less speckle.
/// </summary>
public readonly struct TerrainPreviewGenerationMetrics
{
	public const int TargetLakePatchCountMax = 24;
	public const float TargetMedianLakeDiameterMetersMin = 600f;
	public const float TargetLakeArchipelagoScoreMax = 1.6f;
	public const float TargetMountainLandFractionMax = 0.38f;

	public int LakePatchCount { get; init; }
	public float MedianLakeDiameterMeters { get; init; }
	public float MeanLakeDiameterMeters { get; init; }
	public float LakeArchipelagoScore { get; init; }
	public float MountainLandFraction01 { get; init; }
	public float WaterOnLandFraction01 { get; init; }

	public bool LakesLookCohesive =>
		LakePatchCount <= TargetLakePatchCountMax
		&& MedianLakeDiameterMeters >= TargetMedianLakeDiameterMetersMin
		&& LakeArchipelagoScore <= TargetLakeArchipelagoScoreMax;

	public bool MountainsLookReasonable => MountainLandFraction01 <= TargetMountainLandFractionMax;

	public static TerrainPreviewGenerationMetrics Measure( TerrainPreviewSettings settings )
		=> TerrainPreviewLandDiskFields.MeasureGenerationMetrics( settings );

	public string FormatStatsBlock()
	{
		var lakeFlag = LakesLookCohesive ? "ok" : "check";
		var mountainFlag = MountainsLookReasonable ? "ok" : "high";
		return
			$"Lakes ({lakeFlag}): {LakePatchCount} patches · med {MedianLakeDiameterMeters:0} m · arch {LakeArchipelagoScore:0.00}\n"
			+ $"Mountains ({mountainFlag}): {MountainLandFraction01 * 100f:0.#}% land";
	}
}
