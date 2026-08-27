using System;
using Sandbox;

namespace Survival;

/// <summary>Locked attack pattern selected from the directional cursor (see <see cref="FromCursorDir"/>).</summary>
public static class MeleeAttackTypes
{
	public const byte Left = 0;
	public const byte Right = 1;
	public const byte Forward = 2;

	/// <summary>
	/// Maps cursor cardinal to attack type. Default: right→Right, left→Left, up→Forward.
	/// When <paramref name="southpawSwing"/> is true, left/right are swapped (legacy inverted mapping).
	/// </summary>
	public static byte FromCursorDir( byte cursorDir, bool southpawSwing = false )
	{
		if ( cursorDir == SwingDirs.Right )
			return southpawSwing ? Left : Right;
		if ( cursorDir == SwingDirs.Left )
			return southpawSwing ? Right : Left;
		return Forward;
	}

	public static string Label( byte attackType )
	{
		if ( attackType == Left ) return "LeftAttack";
		if ( attackType == Right ) return "RightAttack";
		if ( attackType == Forward ) return "ForwardAttack";
		return "?";
	}
}

/// <summary>Attack timeline phases: windup (no damage) → active (the sweep deals damage) → recovery.</summary>
public static class MeleeAttackStates
{
	public const byte Windup = 0;
	public const byte Active = 1;
	public const byte Recovery = 2;

	public static string Label( byte state )
	{
		return state switch
		{
			Windup => "Windup",
			Active => "Active",
			Recovery => "Recovery",
			_ => "?"
		};
	}

	public static bool DealsDamage( byte state ) =>
		state == Active;
}

/// <summary>Server-authoritative hit data for one target during one attack instance.</summary>
public readonly record struct MeleeHitResult
{
	public Guid AttackerId { get; init; }
	public Guid TargetId { get; init; }
	public ushort AttackInstanceId { get; init; }
	public byte AttackType { get; init; }
	public bool IsHeavy { get; init; }
	public byte AttackState { get; init; }
	public Vector3 HitPosition { get; init; }
	public float DamageApplied { get; init; }
	public int TargetsHitCount { get; init; }
	public bool TargetWasAlreadyHit { get; init; }
	public bool WasBlocked { get; init; }
	/// <summary>True when the hit was intercepted by a perfect parry (subset of <see cref="WasBlocked"/>).</summary>
	public bool WasParried { get; init; }
	public float IncomingAngleDegrees { get; init; }
}
