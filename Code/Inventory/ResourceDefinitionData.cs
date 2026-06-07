using System.Text.Json.Serialization;

namespace Survival;

/// <summary>One material/gatherable row from <c>data/resources.json</c> (not crafted-only outputs).</summary>
public sealed class ResourceDefinitionData
{
	[JsonPropertyName( "id" )]
	public string Id { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	public string Icon { get; set; } = string.Empty;

	public int MaxStack { get; set; } = 64;

	public string FallbackColor { get; set; } = "0.45,0.48,0.52,1";
}
