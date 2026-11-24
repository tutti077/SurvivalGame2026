using System.Text.Json.Serialization;

namespace Survival;

/// <summary>One material/gatherable row from <c>data/resources.json</c> (not crafted-only outputs).</summary>
public sealed class ResourceDefinitionData
{
	[JsonPropertyName( "id" )]
	public string Id { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	/// <summary>Optional flavor/help text shown in the item hover tooltip.</summary>
	public string Description { get; set; } = string.Empty;

	public string Icon { get; set; } = string.Empty;

	public int MaxStack { get; set; } = 64;

	public string FallbackColor { get; set; } = "0.45,0.48,0.52,1";

	/// <summary>
	/// Carry weight of ONE unit, in tenths (0.1 steps). Unset (negative) resolves through
	/// <see cref="InventoryWeight.UnitWeight"/>: single-stack rows (gear) 5, stackable goods 0.1.
	/// </summary>
	[JsonPropertyName( "weight" )]
	public float Weight { get; set; } = -1f;

	/// <summary>Catchable by <see cref="PlayerFishing"/>. Fish rows live here rather than in their own catalog file.</summary>
	[JsonPropertyName( "fish" )]
	public bool Fish { get; set; }

	/// <summary>Relative roll weight among <see cref="Fish"/> rows — higher is more common. Ignored when not a fish.</summary>
	[JsonPropertyName( "fishWeight" )]
	public int FishWeight { get; set; } = 1;

	/// <summary>How this species swims in the fishing minigame. Null = playable defaults.</summary>
	[JsonPropertyName( "fishMotion" )]
	public FishMotionData FishMotion { get; set; }

	/// <summary>Present on seed rows: sowable on tilled soil, grows the described plant (<see cref="PlayerFarming"/>).</summary>
	[JsonPropertyName( "seed" )]
	public FarmPlantData Seed { get; set; }

	/// <summary>Present on a folded vehicle: the vehicle build piece id (<c>build_pieces.json</c>) this item unfolds into on Attack1 (<see cref="Vehicle.HostTryDeployFromItem"/>).</summary>
	[JsonPropertyName( "deployVehicle" )]
	public string DeployVehicle { get; set; } = string.Empty;
}
