using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Survival;

public enum MeleeStaggerTier : byte
{
	None = 0,
	Light = 1,
	Heavy = 2,
}

/// <summary>One row from <c>data/melee_block_stagger.json</c> (seconds + point costs).</summary>
public sealed class MeleeBlockStaggerOutcomeData
{
	[JsonPropertyName( "tier" )]
	public string Tier { get; set; } = "Light";

	[JsonPropertyName( "durationSeconds" )]
	public float DurationSeconds { get; set; }

	[JsonPropertyName( "healthDamage" )]
	public float HealthDamage { get; set; }

	[JsonPropertyName( "staminaCost" )]
	public float StaminaCost { get; set; }
}

sealed class MeleeBlockStaggerFile
{
	[JsonPropertyName( "outcomes" )]
	public Dictionary<string, MeleeBlockStaggerOutcomeData> Outcomes { get; set; }
}

/// <summary>Resolved block result applied to the defender (duration-based stagger + fixed HP/stamina).</summary>
public readonly struct MeleeBlockOutcome
{
	public string OutcomeId { get; init; }
	public MeleeStaggerTier Tier { get; init; }
	public float DurationSeconds { get; init; }
	public float HealthDamage { get; init; }
	public float StaminaCost { get; init; }
	public bool WasPerfectParry { get; init; }
}
