using System;

namespace Survival;

/// <summary>One boss from <c>data/bosses.json</c>: which entity prefab it is, its pools and its forms.</summary>
public sealed class BossData
{
	public string Id { get; set; } = string.Empty;
	/// <summary>Name shown over the screen-top bar and on the world health bar.</summary>
	public string DisplayName { get; set; } = string.Empty;
	/// <summary>Entity prefab to clone. It must carry a <see cref="BossEntity"/> next to the usual entity components.</summary>
	public string Prefab { get; set; } = "prefabs/entity/bossT1.prefab";
	/// <summary>Archetype name from <see cref="EnemyType"/> ("Scav", "Boneback", "Howler") — move speed, attack timing.</summary>
	public string Archetype { get; set; } = "Scav";
	public int Tier { get; set; } = 1;
	/// <summary>First-form health pool.</summary>
	public float Health { get; set; } = 300f;
	/// <summary>Players within this distance of the spawn point when the boss appears get the screen-top bar.</summary>
	public float HealthBarRangeMeters { get; set; } = 500f;
	/// <summary>No live player inside <see cref="HealthBarRangeMeters"/> for this long → the host despawns the boss (0 = never).</summary>
	public float AbandonDespawnSeconds { get; set; } = 60f;
	/// <summary>True: at 0 HP the boss refills to <see cref="SecondFormHealth"/> and fights on instead of dying.</summary>
	public bool HasSecondForm { get; set; }
	/// <summary>Second-form health pool (only read when <see cref="HasSecondForm"/>).</summary>
	public float SecondFormHealth { get; set; }
	/// <summary>Bar fill colour for the second form (the first form is always red).</summary>
	public string SecondFormBarColor { get; set; } = "#8a2be2";

	public EnemyType ResolveEnemyType() =>
		!string.IsNullOrWhiteSpace( Archetype ) && Enum.TryParse( Archetype.Trim(), ignoreCase: true, out EnemyType parsed )
			? parsed
			: EnemyType.Scav;
}
