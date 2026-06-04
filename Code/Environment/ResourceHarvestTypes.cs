namespace Survival;

/// <summary>Host-side outcome of one harvest tick on a <see cref="ResourceHarvestNode"/>.</summary>
public readonly record struct HarvestTickResult
{
	public bool Success { get; init; }
	public int YieldAmount { get; init; }
	public string ResourceId { get; init; }
	public string DisplayName { get; init; }
	public int RemainingHarvestTicks { get; init; }
	public bool DepletedThisTick { get; init; }
	public string FailReason { get; init; }

	public static HarvestTickResult Failed( string reason ) => new()
	{
		Success = false,
		FailReason = reason ?? "unknown",
	};
}
