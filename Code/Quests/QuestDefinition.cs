using System.Collections.Generic;

namespace Survival;

/// <summary>
/// One quest from <c>data/quests.json</c>. <see cref="Requires"/> gates when the quest becomes
/// active (all listed quests must be completed first); <see cref="Objectives"/> are all required.
/// </summary>
public sealed class QuestDefinition
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;

	/// <summary>One-line task shown in the list / detail ("Craft an axe").</summary>
	public string Summary { get; set; } = string.Empty;

	/// <summary>Flavor text — hidden while the quest is locked.</summary>
	public string Description { get; set; } = string.Empty;

	/// <summary>Quest ids that must be completed before this one unlocks. Empty = available from the start.</summary>
	public List<string> Requires { get; set; } = new();

	/// <summary>
	/// Side quest (repeat-kill tallies etc.): listed under its own header, always visible with full
	/// details, and never part of the main chain. Main quests hide everything until unlocked.
	/// </summary>
	public bool Side { get; set; }

	/// <summary>
	/// Authored but not live: the catalog drops it and strips it from other quests' <see cref="Requires"/>,
	/// so the chain heals around it. Flip to false when the content it needs exists.
	/// </summary>
	public bool Disabled { get; set; }

	public List<QuestObjective> Objectives { get; set; } = new();

	public List<string> Rewards { get; set; } = new();
}
