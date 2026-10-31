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

	/// <summary>Lakes that met a mountain (TerrainPreviewMountainLakes): tarns + filled + trimmed.</summary>
	public int MountainLakeCount { get; init; }
	/// <summary>Lakes sitting on a mountain top at their own level.</summary>
	public int TarnCount { get; init; }
	/// <summary>Lakes the mountain filled in completely.</summary>
	public int FilledLakeCount { get; init; }
	/// <summary>Lakes cut back to the mountain's foot.</summary>
	public int TrimmedLakeCount { get; init; }
	public float HighestTarnMeters { get; init; }
	/// <summary>Rivers with at least one segment falling at 35° or steeper (a waterfall over a mountain wall).</summary>
	public int WaterfallRiverCount { get; init; }
	/// <summary>Lakes at a mountain's foot (touching the mountain, kept at sea level): each gets an apron and a river of its own.</summary>
	public int MountainFootLakeCount { get; init; }
	/// <summary>Mountain-foot lakes a river of their own reaches.</summary>
	public int FootLakeRiverCount { get; init; }
	/// <summary>Mountain-foot lakes whose own river has a waterfall segment.</summary>
	public int FootLakeWaterfallCount { get; init; }
	/// <summary>Share of the ocean shoreline (rim + bays) that is a sea cliff.</summary>
	public float SeaCliffShoreFraction01 { get; init; }
	/// <summary>Share of the ocean shoreline (rim + bays) whose land is Azure Coast.</summary>
	public float AzureShoreFraction01 { get; init; }
	/// <summary>Share of all dry land that is Azure Coast.</summary>
	public float AzureLandFraction01 { get; init; }

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
			+ $"Mountains ({mountainFlag}): {MountainLandFraction01 * 100f:0.#}% land\n"
			+ $"Mountain lakes: {TarnCount} tarns (highest {HighestTarnMeters:0} m) · {FilledLakeCount} filled · {TrimmedLakeCount} trimmed · waterfalls {WaterfallRiverCount} · sea cliffs {SeaCliffShoreFraction01 * 100f:0}% of ocean shore\n"
			+ $"Foot lakes: {MountainFootLakeCount} · {FootLakeRiverCount} with their own river · {FootLakeWaterfallCount} with a waterfall\n"
			+ $"Azure Coast: {AzureShoreFraction01 * 100f:0}% of the ocean shore, {AzureLandFraction01 * 100f:0.#}% of land";
	}
}
