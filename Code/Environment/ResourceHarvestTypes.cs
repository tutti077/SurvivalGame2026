namespace Survival;

/// <summary>Host-side outcome of one harvest tick on a world <see cref="ResourceItemDefinition"/>.</summary>
public readonly record struct HarvestTickResult
{
	public bool Success { get; init; }
	public HarvestLootItem[] Loot { get; init; }
	public int RemainingHarvestTicks { get; init; }
	public bool DepletedThisTick { get; init; }
	public string FailReason { get; init; }

	public int YieldAmount => Loot is { Length: > 0 } loot ? loot[0].Amount : 0;
	public string ResourceId => Loot is { Length: > 0 } loot ? loot[0].ResourceId : null;
	public string DisplayName => Loot is { Length: > 0 } loot ? loot[0].DisplayName : null;

	public static HarvestTickResult Failed( string reason ) => new()
	{
		Success = false,
		FailReason = reason ?? "unknown",
	};
}
