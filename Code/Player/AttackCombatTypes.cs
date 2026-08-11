using System;

namespace Survival;

/// <summary>Owner→host intent for a primary attack release (timing + pose for server checks).</summary>
/// <remarks>
/// Camera uses explicit floats so RPC/codegen reliably carries world positions (some builds are picky about <see cref="Vector3"/> on structs).
/// Swing-from uses two floats for horizontal XZ for the same RPC reliability; vertical arc intent is a separate float so it is not confused with <see cref="SwingFromY"/> (world +Z on XZ).
/// <see cref="SwingDirs"/> is authoritative for discrete **L / R / U only** (no forward); floats mirror that for traces.
/// Post-release <see cref="PostSwingDragScreenX"/>/<see cref="PostSwingDragScreenY"/> feed <see cref="MeleeCombatDamageMultiplier"/> on the host.
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

	/// <summary>Locked attack pattern from cursor via <see cref="PlayerCombat.ResolveAttackTypeFromCursorDir"/> (informational; host re-resolves from <see cref="SwingDir"/>).</summary>
	public byte AttackType { get; init; }

	/// <summary>Legacy prepaid cap (0 = host debits stamina once on release from hold duration via <see cref="PlayerCombat.GetPrimaryAttackStaminaCostForHoldDuration"/>).</summary>
	public float StaminaPrepaidMax { get; init; }

	public float PostSwingDragScreenX { get; init; }
	public float PostSwingDragScreenY { get; init; }

	/// <summary>Combat-path yaw (degrees) when submitted; unset (NaN) = derive from view forward.</summary>
	public float CombatBasisYawDegrees { get; init; }

	/// <summary>View pitch (degrees) when submitted; unset (NaN) = derive from view forward.</summary>
	public float CombatBasisPitchDegrees { get; init; }
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

	/// <summary>Swap L/R; overhead unchanged. Attack teardrop uses mirror of stored combat dir for HUD placement.</summary>
	public static byte MirrorLateral( byte dir ) =>
		dir == Left ? Right : dir == Right ? Left : dir;
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

/// <summary>Authoritative summary after a phased melee swing completes on the host (may arrive after <see cref="AttackReleaseResult"/>).</summary>
public readonly record struct MeleeSweepOutcomeSummary
{
	public ushort IntentSequence { get; init; }
	public bool AnyHit { get; init; }
	public float TotalDamageDealt { get; init; }
	public Guid FirstHitTargetId { get; init; }
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
	public const int OkMeleeSweepStarted = 15;
	public const int RejectMeleeBusy = 16;
	public const int RejectNoMeleeItemEquipped = 17;
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

		var chop = Components.Get<ChopableTree>() ?? FindChopableTreeInParents( GameObject.Parent );
		if ( chop is not null && chop.Enabled )
			return chop.ApplyChopDamage( scaled, attacker );

		var vitals = Components.Get<PlayerVitals>() ?? FindVitalsInParents( GameObject.Parent );
		if ( vitals is not null && vitals.Enabled )
			return vitals.ApplyDamageAfterArmor( scaled, attacker );

		var entity = Components.Get<EntityVitals>() ?? FindEntityVitalsInParents( GameObject.Parent );
		if ( entity is not null && entity.Enabled )
			return entity.ApplyDamage( scaled, attacker );

		return scaled;
	}

	static ChopableTree FindChopableTreeInParents( GameObject start )
	{
		if ( start is null || !start.IsValid() )
			return null;

		for ( var p = start; p.IsValid(); p = p.Parent )
		{
			var t = p.Components.Get<ChopableTree>();
			if ( t is not null )
				return t;
		}

		return null;
	}

	static EntityVitals FindEntityVitalsInParents( GameObject start )
	{
		if ( start is null || !start.IsValid() )
			return null;

		for ( var p = start; p.IsValid(); p = p.Parent )
		{
			var v = p.Components.Get<EntityVitals>();
			if ( v is not null )
				return v;
		}

		return null;
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
	public const float DefaultMeleeWeaponDamage = 8f;
}

/// <summary>
/// Global melee combat damage multiplier: starts at <see cref="Standard"/> (1.0), then bonuses/penalties are added.
/// Follow-through drag, heavy attack, etc. — see <see cref="Compute"/> on <see cref="PlayerCombat"/>.
/// </summary>
public static class MeleeCombatDamageMultiplier
{
	public const float Standard = 1f;

	/// <summary>
	/// Unit screen-space drag that scores as good follow-through for the locked swing (+x right, +y down).
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
	/// Builds the combat multiplier: base + drag + phase adjustments + heavy bonus (all additive).
	/// </summary>
	public static float Compute(
		byte lockedSwingDir,
		Vector2 dragScreen,
		float clearDragPixels,
		float goodBonus,
		float badPenalty,
		bool isHeavy,
		float heavyBonus,
		byte attackState,
		float earlyActivePenalty,
		float lateActiveBonus,
		float baseMultiplier = Standard )
	{
		var total = baseMultiplier
		            + EvaluateDragBonus( lockedSwingDir, dragScreen, clearDragPixels, goodBonus, badPenalty )
		            + EvaluatePhaseAdjustment( attackState, earlyActivePenalty, lateActiveBonus );
		if ( isHeavy )
			total += heavyBonus;
		return Math.Max( 0f, total );
	}

	static float EvaluatePhaseAdjustment( byte attackState, float earlyActivePenalty, float lateActiveBonus )
	{
		if ( attackState == MeleeAttackStates.EarlyActive )
			return -Math.Max( 0f, earlyActivePenalty );
		if ( attackState == MeleeAttackStates.LateActive )
			return Math.Max( 0f, lateActiveBonus );
		return 0f;
	}

	static float EvaluateDragBonus(
		byte lockedSwingDir,
		Vector2 dragScreen,
		float clearDragPixels,
		float goodBonus,
		float badPenalty )
	{
		clearDragPixels = Math.Max( 1f, clearDragPixels );
		goodBonus = Math.Max( 0f, goodBonus );
		badPenalty = Math.Max( 0f, badPenalty );

		var gU = GoodDragUnitScreen( lockedSwingDir );
		var bU = BadDragUnitScreen( lockedSwingDir );
		var good = MathF.Max( 0f, Vector2.Dot( dragScreen, gU ) );
		var bad = MathF.Max( 0f, Vector2.Dot( dragScreen, bU ) );

		if ( good < clearDragPixels && bad < clearDragPixels )
			return 0f;

		if ( bad >= clearDragPixels && bad > good )
			return -badPenalty;

		if ( good >= clearDragPixels && good >= bad )
			return goodBonus;

		return 0f;
	}
}
