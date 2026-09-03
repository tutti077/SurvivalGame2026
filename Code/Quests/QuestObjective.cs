namespace Survival;

/// <summary>
/// One counted requirement inside a quest. Progress advances when <see cref="QuestTracker.Report"/>
/// is called with a matching <see cref="Event"/> (see <see cref="QuestEventIds"/>) and, when
/// <see cref="Match"/> is set, a matching subject id (resource id, recipe id, piece id, species, biome).
/// </summary>
public sealed class QuestObjective
{
	public string Event { get; set; } = string.Empty;

	/// <summary>Optional subject filter compared case-insensitively. Empty matches any subject.</summary>
	public string Match { get; set; } = string.Empty;

	/// <summary>How many matching reports complete this objective (amount-weighted for pickups).</summary>
	public int Count { get; set; } = 1;

	/// <summary>Designer text shown next to the progress counter.</summary>
	public string Label { get; set; } = string.Empty;

	public int RequiredCount => Count < 1 ? 1 : Count;
}
