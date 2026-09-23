using System;
using System.Collections.Generic;

namespace Survival;

/// <summary>
/// One base raid from <c>data/raids.json</c>: how many raiders come, how fast they arrive, where
/// they spawn and how they pick between the beds and the players. Distances are world meters
/// (40 u/m, <see cref="TerrainWorldUnits"/>) — converted once in <see cref="BaseRaidSession"/>.
/// </summary>
public sealed class BaseRaidData
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;

	/// <summary>Raiders spawned over the whole raid.</summary>
	public int TotalEnemies { get; set; } = 20;
	/// <summary>Raiders spawned the moment the raid starts.</summary>
	public int InitialEnemies { get; set; } = 10;
	/// <summary>Raiders per follow-up wave until <see cref="TotalEnemies"/> have come.</summary>
	public int WaveSize { get; set; } = 5;
	public float WaveIntervalSeconds { get; set; } = 30f;

	/// <summary>The raided base: every bed within this distance of the raided bed is a target (ground + minimap ring).</summary>
	public float BaseRadiusMeters { get; set; } = 20f;
	/// <summary>Raiders spawn on a ring this far out from the base.</summary>
	public float SpawnMinDistanceMeters { get; set; } = 60f;
	public float SpawnMaxDistanceMeters { get; set; } = 80f;

	/// <summary>A player this close to a raider pulls it off the beds (hits from further out are ignored).</summary>
	public float AggroRangeMeters { get; set; } = 3f;
	/// <summary>A pulled raider whose player gets further than this away goes back to the beds.</summary>
	public float DropAggroRangeMeters { get; set; } = 3f;

	public List<BaseRaidEnemyData> Enemies { get; set; } = new();
}

/// <summary>A raider type in a raid's mix; each spawn picks one by <see cref="Weight"/>.</summary>
public sealed class BaseRaidEnemyData
{
	/// <summary>Archetype name from <see cref="EnemyType"/> ("Scav", "Boneback", "Howler").</summary>
	public string Archetype { get; set; } = "Scav";
	public int Tier { get; set; } = 1;
	/// <summary>Entity prefab to clone (the archetype only tunes components, it does not pick the model).</summary>
	public string Prefab { get; set; } = "prefabs/entity/scavT1.prefab";
	/// <summary>Override max HP (0 = archetype default).</summary>
	public float Health { get; set; }
	public float Weight { get; set; } = 1f;

	public EnemyType ResolveEnemyType() =>
		!string.IsNullOrWhiteSpace( Archetype ) && Enum.TryParse( Archetype.Trim(), ignoreCase: true, out EnemyType parsed )
			? parsed
			: EnemyType.Scav;
}
