using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Survival;

/// <summary>One spawn row from <c>data/biome_population.json</c>.</summary>
public sealed class BiomePopulationEntryData
{
	[JsonPropertyName( "entityId" )]
	public string EntityId { get; set; } = "scavT1";

	[JsonPropertyName( "prefab" )]
	public string Prefab { get; set; } = "prefabs/entity/scavT1.prefab";

	[JsonPropertyName( "enemyType" )]
	public string EnemyType { get; set; } = "Scav";

	[JsonPropertyName( "tier" )]
	public int Tier { get; set; } = 1;

	/// <summary>Approximate one entity per this many meters (target density ≈ 1 / spacing²).</summary>
	[JsonPropertyName( "spacingMeters" )]
	public float SpacingMeters { get; set; } = 250f;

	/// <summary>0–1 chance a valid spacing cell actually spawns (tiny biomes often roll 0–1).</summary>
	[JsonPropertyName( "spawnWeight" )]
	public float SpawnWeight { get; set; } = 1f;

	[JsonPropertyName( "respawn" )]
	public bool Respawn { get; set; } = true;

	[JsonPropertyName( "respawnDelaySeconds" )]
	public float RespawnDelaySeconds { get; set; } = 90f;

	/// <summary>Optional future anchor (e.g. near a tree prefab). Null = anywhere in biome.</summary>
	[JsonPropertyName( "near" )]
	public string Near { get; set; }
}

sealed class BiomePopulationBiomeData
{
	[JsonPropertyName( "entries" )]
	public List<BiomePopulationEntryData> Entries { get; set; }
}

sealed class BiomePopulationFile
{
	[JsonPropertyName( "biomes" )]
	public Dictionary<string, BiomePopulationBiomeData> Biomes { get; set; }
}

/// <summary>Resolved population entry ready for scatter.</summary>
public readonly struct BiomePopulationEntry
{
	public string EntityId { get; init; }
	public string PrefabPath { get; init; }
	public EnemyType EnemyType { get; init; }
	public int Tier { get; init; }
	public float SpacingMeters { get; init; }
	public float SpawnWeight { get; init; }
	public bool Respawn { get; init; }
	public float RespawnDelaySeconds { get; init; }
	public string Near { get; init; }
}
