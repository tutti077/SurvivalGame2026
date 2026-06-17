namespace Survival;

/// <summary>Host-simulated enemy behaviour states.</summary>
public enum EnemyAiState
{
	Idle = 0,
	Wander = 1,
	Alert = 2,
	Tracking = 3,
	Attacking = 4,
	AttackObstacle = 5,
	Search = 6,
	ReturnHome = 7
}
