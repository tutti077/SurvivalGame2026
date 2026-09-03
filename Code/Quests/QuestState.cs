using System;
using System.Collections.Generic;

namespace Survival;

public enum QuestState
{
	/// <summary>Prerequisites not met — title visible, description hidden.</summary>
	Locked = 0,

	/// <summary>Available and counting progress.</summary>
	Active = 1,

	Completed = 2,
}

/// <summary>On-disk shape of one player's quest progress (<see cref="QuestSaveStore"/>).</summary>
public sealed class QuestSaveFile
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;

	/// <summary>Steam id the file belongs to ("local" when no Steam identity was available).</summary>
	public string PlayerKey { get; set; } = string.Empty;

	public string UpdatedUtc { get; set; } = string.Empty;

	public Dictionary<string, QuestSaveEntry> Quests { get; set; } = new( StringComparer.OrdinalIgnoreCase );
}

public sealed class QuestSaveEntry
{
	public bool Completed { get; set; }

	public string CompletedUtc { get; set; } = string.Empty;

	/// <summary>Per-objective counts, in <see cref="QuestDefinition.Objectives"/> order.</summary>
	public int[] Progress { get; set; } = Array.Empty<int>();
}
