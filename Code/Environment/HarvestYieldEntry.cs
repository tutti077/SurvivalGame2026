using Sandbox;

namespace Survival;

/// <summary>One possible loot line from a harvest tick on a <see cref="ResourceItemDefinition"/>.</summary>
public sealed class HarvestYieldEntry
{
	[Property, Title( "Resource Id" )]
	public string ResourceId { get; set; } = string.Empty;

	[Property, Title( "Amount Low" ), Range( 0, 200 )]
	public int AmountLow { get; set; } = 1;

	[Property, Title( "Amount High" ), Range( 0, 200 )]
	public int AmountHigh { get; set; } = 1;

	/// <summary>0–100. 100 = always granted; lower values roll each harvest tick.</summary>
	[Property, Title( "Chance %" ), Range( 0, 100 )]
	public float ChancePercent { get; set; } = 100f;
}
