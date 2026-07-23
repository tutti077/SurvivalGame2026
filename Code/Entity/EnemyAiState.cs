namespace Survival;

/// <summary>Host-simulated enemy behaviour states.</summary>
public enum EnemyAiState
{
	/// <summary>Standing still for a short random duration.</summary>
	Idle = 0,
	/// <summary>Walking to a nav point near home.</summary>
	Wander = 1,
	/// <summary>Alerted by noise/sight meter — brief investigate walk toward stimulus.</summary>
	Searching = 2,
	/// <summary>Alert committed — path to live player (LOS irrelevant); flank/break if blocked.</summary>
	Chasing = 3,
	/// <summary>In melee range with LOS — attacking.</summary>
	Attacking = 4,
	/// <summary>Low health — fleeing, then idle.</summary>
	Retreating = 5,
}
