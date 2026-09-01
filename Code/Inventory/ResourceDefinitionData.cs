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

	/// <summary>Catchable by <see cref="PlayerFishing"/>. Fish rows live here rather than in their own catalog file.</summary>
	[JsonPropertyName( "fish" )]
	public bool Fish { get; set; }

	/// <summary>Relative roll weight among <see cref="Fish"/> rows — higher is more common. Ignored when not a fish.</summary>
	[JsonPropertyName( "fishWeight" )]
	public int FishWeight { get; set; } = 1;

	/// <summary>How this species swims in the fishing minigame. Null = playable defaults.</summary>
	[JsonPropertyName( "fishMotion" )]
	public FishMotionData FishMotion { get; set; }
}
