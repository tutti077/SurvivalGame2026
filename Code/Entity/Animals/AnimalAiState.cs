namespace Survival;

/// <summary>
/// Shared host-simulated animal states. Every species runs the same machine
/// (<see cref="AnimalBrain"/>); the species' behavior profile decides which
/// transitions exist and how they are tuned.
/// </summary>
public enum AnimalAiState
{
	/// <summary>Standing still for a short random duration.</summary>
	Idle = 0,
	/// <summary>Walking to a nav point nearby.</summary>
	Wander = 1,
	/// <summary>Head-down eating for a random duration (interruptible by any stimulus).</summary>
	Graze = 2,
	/// <summary>Noticed a threat — stop and face it while committing to track / flee.</summary>
	Alerted = 3,
	/// <summary>Sneaking toward the threat's last seen / heard position.</summary>
	Tracking = 4,
	/// <summary>Rushing the threat; bites while in range.</summary>
	Attacking = 5,
	/// <summary>Running away from the last-sensed threat position.</summary>
	Fleeing = 6,
	/// <summary>Taming: walking slowly up to a crouching player who holds the food it likes.</summary>
	Approaching = 7,
	/// <summary>Taming: eating from the hand; tamed when the meal ends.</summary>
	Eating = 8,
}

/// <summary>How a species reacts to a threat — picks the transition set inside the shared machine.</summary>
public enum AnimalThreatResponse
{
	/// <summary>Any sight / sound → flee immediately; calm down once far enough away.</summary>
	Flee = 0,
	/// <summary>Track → attack N times → flee → resume tracking when the threat backs off (nips at heels).</summary>
	Harass = 1,
	/// <summary>Track → attack until the target dies; flee at low health, re-engage if pressed.</summary>
	Predator = 2,
	/// <summary>Birds: any stimulus → take off, fly away from it and leave the world; killable only while perched.</summary>
	FlyAway = 3,
}
