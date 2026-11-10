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

	/// <summary>Animal rows: an <see cref="AnimalSpecies"/> name (fox, whitetail, jay …). Set = the prefab is configured through <see cref="AnimalSetup"/>, not the enemy setup.</summary>
	[JsonPropertyName( "species" )]
	public string Species { get; set; }

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

	/// <summary>Herd rows: chance (0-1) a cell spawns a group of <see cref="GroupMin"/>..<see cref="GroupMax"/> instead of one (Mark 2026-11-02: deer in 2-4, "75% chance for there to be 2 near each other").</summary>
	[JsonPropertyName( "groupChance" )]
	public float GroupChance { get; set; }

	[JsonPropertyName( "groupMin" )]
	public int GroupMin { get; set; } = 2;

	[JsonPropertyName( "groupMax" )]
	public int GroupMax { get; set; } = 2;

	/// <summary>Group members stand within this of the cell point (metres).</summary>
	[JsonPropertyName( "groupRadiusMeters" )]
	public float GroupRadiusMeters { get; set; } = 8f;

	/// <summary>Optional future anchor (e.g. near a tree prefab). Null = anywhere in biome.</summary>
	[JsonPropertyName( "near" )]
	public string Near { get; set; }

	/// <summary>Camp rows: an <c>enemy_camps.json</c> id. Set = each cell stamps that camp (pieces + guards, <see cref="EnemyCampSites"/>) on open, flat, dry ground instead of one entity; <c>prefab</c> is ignored, every guard is its own respawning slot.</summary>
	[JsonPropertyName( "camp" )]
	public string Camp { get; set; }
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
	/// <summary>Set for animal rows (fox, whitetail, jay); null = an enemy row.</summary>
	public AnimalSpecies? Species { get; init; }
	public int Tier { get; init; }
	public float SpacingMeters { get; init; }
	public float SpawnWeight { get; init; }
	public bool Respawn { get; init; }
	public float RespawnDelaySeconds { get; init; }
	public string Near { get; init; }
	/// <summary>Camp rows: the <c>enemy_camps.json</c> id this cell stamps (null = a single entity row).</summary>
	public string CampId { get; init; }
	/// <summary>0 = always one per cell.</summary>
	public float GroupChance { get; init; }
	public int GroupMin { get; init; }
	public int GroupMax { get; init; }
	public float GroupRadiusMeters { get; init; }
}
