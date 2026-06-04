using System.Collections.Generic;

namespace Survival;

/// <summary>One quest entry for the quests menu (display only until progression is wired).</summary>
public sealed class QuestDefinition
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string Task { get; set; } = string.Empty;
	public List<string> Rewards { get; set; } = new();
}
