using System;

namespace Survival;

/// <summary>Owner→host intent for a primary attack release (timing + pose for server checks).</summary>
/// <remarks>
/// Camera uses explicit floats so RPC/codegen reliably carries world positions (some builds are picky about <see cref="Vector3"/> on structs).
/// Swing-from uses two floats for horizontal XZ for the same RPC reliability; vertical arc intent is a separate float so it is not confused with <see cref="SwingFromY"/> (world +Z on XZ).
/// <see cref="SwingDirs"/> is authoritative for discrete **L / R / U only** (no forward); floats mirror that for traces.
/// Primary: attack direction is locked on press; post-release <see cref="PostSwingDragScreenX"/>/<see cref="PostSwingDragScreenY"/> (pixels during the swing window) feed <see cref="MeleeCameraSwingDamage"/> on the host <see cref="CombatAuthority"/> using the attacker’s <see cref="PlayerCombat"/> damage tuning.
/// </remarks>
public readonly record struct AttackReleaseIntent
{
	public double PressedGlobalSeconds { get; init; }
	public double ReleasedGlobalSeconds { get; init; }

	public float ClientCameraPressX { get; init; }
	public float ClientCameraPressY { get; init; }
	public float ClientCameraPressZ { get; init; }
	public float ClientCameraReleaseX { get; init; }
	public float ClientCameraReleaseY { get; init; }
	public float ClientCameraReleaseZ { get; init; }

	public Vector3 ViewForwardOnPress { get; init; }
	/// <summary>Camera look direction at attack release (trace / validation).</summary>
	public Vector3 ViewForwardOnRelease { get; init; }
	public Vector3 ClientPlayerWorldPosition { get; init; }
	public Rotation ClientPlayerWorldRotation { get; init; }
	public ushort IntentSequence { get; init; }

	/// <summary>
	/// Horizontal swing-from on world XZ (Y-up): <see cref="SwingFromX"/> is world +X, <see cref="SwingFromY"/> is world +Z.
	/// Client sends a unit vector (normalized); server may renormalize if slightly off.
	/// </summary>
	public float SwingFromX { get; init; }
	public float SwingFromY { get; init; }

	/// <summary>
	/// Vertical swing intent in world up (Y): -1 down, 0 neutral, +1 up. Independent of <see cref="SwingFromY"/> (which is horizontal world Z).
	/// </summary>
	public float SwingVerticalHint { get; init; }

	/// <summary>Discrete swing-from: <see cref="SwingDirs.Left"/>, <see cref="SwingDirs.Right"/>, or <see cref="SwingDirs.Up"/> only.</summary>
	public byte SwingDir { get; init; }

	/// <summary>Legacy prepaid cap (0 = host debits stamina once on release from hold duration via <see cref="PlayerCombat.GetPrimaryAttackStaminaCostForHoldDuration"/>).</summary>
	public float StaminaPrepaidMax { get; init; }

	public float PostSwingDragScreenX { get; init; }
	public float PostSwingDragScreenY { get; init; }
}

/// <summary>Only three melee swing directions (no forward).</summary>
public static class SwingDirs
{
	public const byte Left = 0;
	public const byte Right = 1;
	public const byte Up = 2;

	public static string Letter( byte c )
	{
		if ( c == Left ) return "L";
		if ( c == Right ) return "R";
		if ( c == Up ) return "U";
		return "?";
	}
}

/// <summary>Server→owner authoritative outcome (do not trust client damage).</summary>
public readonly struct AttackReleaseResult
{
	public bool Accepted { get; init; }
	public bool Hit { get; init; }
	public float DamageDealt { get; init; }
	public Guid TargetGameObjectId { get; init; }
	public int DebugCode { get; init; }
	public string DebugDetail { get; init; }
}

public static class AttackReleaseDebugCode
{
	public const int OkMiss = 10;
	public const int OkHit = 11;
	public const int RejectNotHost = 1;
	public const int RejectOwnerMismatch = 2;
	public const int RejectRateLimit = 3;
	public const int RejectCameraVsPlayer = 4;
	public const int RejectPlayerVsServerRoot = 5;
	public const int RejectDirection = 6;
	public const int RejectNoCombatAuthority = 12;
	public const int RejectAttackerMissingPlayerCombat = 13;
	public const int RejectInsufficientStamina = 14;
}

public interface IDamageable
{
	/// <summary>Apply damage; returns amount actually removed from health when <see cref="PlayerVitals"/> is present.</summary>
	float TakeDamage( float amount, Component attacker );
}

[Title( "Damage Receiver" )]
public sealed class DamageReceiver : Component, IDamageable
{
	[Property] public float DamageMultiplier { get; set; } = 1f;

	public float TakeDamage( float amount, Component attacker )
	{
		var scaled = amount * DamageMultiplier;
		var vitals = Components.Get<PlayerVitals>() ?? FindVitalsInParents( GameObject.Parent );
		if ( vitals is not null )
			return vitals.ApplyDamageAfterArmor( scaled, attacker );

		Log.Info( $"{GameObject.Name}: TakeDamage {scaled:0.#} from {attacker?.GameObject.Name} (no PlayerVitals)" );
		return scaled;
	}

	static PlayerVitals FindVitalsInParents( GameObject start )
	{
		if ( start is null || !start.IsValid() )
			return null;

		for ( var p = start; p.IsValid(); p = p.Parent )
		{
			var v = p.Components.Get<PlayerVitals>();
			if ( v is not null )
				return v;
		}

		return null;
	}
}

public static class AttackCombatConstants
{
	public const float DefaultMeleeRange = 80f;

	/// <summary>Fallback if <see cref="PlayerCombat.MeleeWeaponBaseDamage"/> wasn’t replicated in older assets.</summary>
	public const float DefaultMeleeWeaponDamage = 10f;
}

/// <summary>Server-only: melee damage multiplier from post-release mouse drag vs locked swing dir.</summary>
public static class MeleeCameraSwingDamage
{
	/// <summary>
	/// Unit screen-space drag that scores as “good” follow-through for the locked swing (+x right, +y down).
	/// Left = pull mouse left (−x), Right = pull right (+x), Up overhead = pull down (+y).
	/// </summary>
	public static Vector2 GoodDragUnitScreen( byte swingDir )
	{
		if ( swingDir == SwingDirs.Left )
			return new Vector2( -1f, 0f );
		if ( swingDir == SwingDirs.Right )
			return new Vector2( 1f, 0f );
		return new Vector2( 0f, 1f );
	}

	/// <summary>Opposite “bad” drag direction for the same swing.</summary>
	public static Vector2 BadDragUnitScreen( byte swingDir )
	{
		if ( swingDir == SwingDirs.Left )
			return new Vector2( 1f, 0f );
		if ( swingDir == SwingDirs.Right )
			return new Vector2( -1f, 0f );
		return new Vector2( 0f, -1f );
	}

	/// <summary>
	/// <paramref name="dragScreen"/> = sum of mouse deltas during the swing window.
	/// Below <paramref name="clearDragPixels"/> on both good and bad axes → <paramref name="neutralMul"/>.
	/// Clear motion on the bad axis and it dominates good → <paramref name="badMul"/>.
	/// Clear motion on the good axis and good ≥ bad → <paramref name="goodMul"/>.
	/// </summary>
	public static float ComputePostDragMultiplier( byte lockedSwingDir, Vector2 dragScreen, float clearDragPixels,
		float neutralMul, float goodMul, float badMul )
	{
		clearDragPixels = Math.Max( 1f, clearDragPixels );

		var gU = GoodDragUnitScreen( lockedSwingDir );
		var bU = BadDragUnitScreen( lockedSwingDir );
		var good = MathF.Max( 0f, Vector2.Dot( dragScreen, gU ) );
		var bad = MathF.Max( 0f, Vector2.Dot( dragScreen, bU ) );

		if ( good < clearDragPixels && bad < clearDragPixels )
			return neutralMul;

		if ( bad >= clearDragPixels && bad > good )
			return badMul;

		if ( good >= clearDragPixels && good >= bad )
			return goodMul;

		return neutralMul;
	}
}
