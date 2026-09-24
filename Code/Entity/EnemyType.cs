namespace Survival;

/// <summary>Enemy archetype — tuning lives on <see cref="EntityVitals"/> / <see cref="EntityArchetype"/>.</summary>
public enum EnemyType
{
	Scav = 0,
	Boneback = 1,
	Howler = 2,
	/// <summary>Robot patrol unit: scav stats, but a machine (Medusa Eye, Hackd and friends care).</summary>
	PatrolBot = 3
}
