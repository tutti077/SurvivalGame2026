using System;
using Sandbox;

namespace Survival;

/// <summary>Named recovery / shove presentation clips (citizen model sequences).</summary>
public enum CombatRecoveryAnim : byte
{
	None = 0,
	/// <summary><c>rpg_2h_attack_moving_01</c> — attacker after block or parry.</summary>
	Rpg2hAttackMoving = 1,
	/// <summary><c>pistol_2h_pose_standing_idle_01</c> — miss / clean hit / shove self recovery.</summary>
	Pistol2hStandingIdle = 2,
	/// <summary><c>melee_punch_attack_left</c> — shove windup/strike presentation.</summary>
	MeleePunchLeft = 4,
}

public static class CombatRecoveryAnims
{
	public const string Rpg2hAttackMoving = "rpg_2h_attack_moving_01";
	public const string Pistol2hStandingIdle = "pistol_2h_pose_standing_idle_01";
	public const string MeleePunchLeft = "melee_punch_attack_left";

	public static string SequenceName( CombatRecoveryAnim anim ) => anim switch
	{
		CombatRecoveryAnim.Rpg2hAttackMoving => Rpg2hAttackMoving,
		CombatRecoveryAnim.Pistol2hStandingIdle => Pistol2hStandingIdle,
		CombatRecoveryAnim.MeleePunchLeft => MeleePunchLeft,
		_ => null
	};
}

/// <summary>Host finish classification for an attack action (drives attacker recovery).</summary>
public readonly struct MeleeAttackFinishOutcome
{
	public bool AnyHit { get; init; }
	public bool WasBlocked { get; init; }
	public bool WasParried { get; init; }
	public bool IsHeavy { get; init; }
	public bool IsShove { get; init; }
}
