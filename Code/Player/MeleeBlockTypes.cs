using Sandbox;

namespace Survival;

public enum CombatState : byte
{
	Idle = 0,
	Attacking = 1,
	Blocking = 2,
	PostBlocking = 3,
	PostAttack = 4,
}

public enum MeleeBlockRejectReason : byte
{
	None = 0,
	NotBlocking = 1,
	BlockStartedAfterHit = 2,
	IncomingFromBackArc = 4,
	RayMissedBlockRegion = 7,
}

/// <summary>Server-side contact data for facing-arc + body-shell block validation (geometry only — no client trust).</summary>
public readonly struct MeleeBlockContact
{
	public GameObject AttackerRoot { get; init; }
	public Vector3 AttackerPosition { get; init; }
	public GameObject DefenderRoot { get; init; }
	public PlayerCombat DefenderCombat { get; init; }
	public Vector3 HitPosition { get; init; }
	public byte AttackType { get; init; }
	/// <summary>Attacker cursor swing at release (<see cref="AttackReleaseIntent.SwingDir"/>); logging only for blocks.</summary>
	public byte AttackSwingDir { get; init; }
	public bool AttackWasHeavy { get; init; }
	public double HitSandboxTime { get; init; }

	/// <summary>Host attack ray entered the body-radius block shell before the body.</summary>
	public bool AttackRayGeometryValidated { get; init; }
}

public readonly struct MeleeBlockValidationTrace
{
	public float IncomingAngleDegrees { get; init; }
	public byte BlockDirection { get; init; }
	public bool WasBlocking { get; init; }
	public bool WasPerfectParry { get; init; }
	public string OutcomeId { get; init; }
	public float StaggerDurationSeconds { get; init; }
	public float HealthDamage { get; init; }
	public float StaminaCost { get; init; }
	public MeleeBlockRejectReason RejectReason { get; init; }
}
