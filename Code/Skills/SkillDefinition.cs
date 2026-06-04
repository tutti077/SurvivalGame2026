using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Survival;

/// <summary>One node in the skills web (layout, copy, and graph links).</summary>
public sealed class SkillDefinition
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string Icon { get; set; } = string.Empty;

	/// <summary>0–1 position in the web canvas (center of node).</summary>
	public float X { get; set; }

	/// <summary>0–1 position in the web canvas (center of node).</summary>
	public float Y { get; set; }

	/// <summary>Skill ids that must come before this one (lines draw parent → this).</summary>
	public List<string> Parents { get; set; } = new();

	/// <summary>Skill ids unlocked downstream from this one (lines draw this → child).</summary>
	public List<string> Children { get; set; } = new();

	/// <summary>Legacy alias; merged into <see cref="Parents"/> on load.</summary>
	[JsonPropertyName( "requires" )]
	public List<string> RequiresLegacy { get; set; } = new();
}
