using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Server-side thickened sphere substeps along blade motion during damaging attack states.
/// </summary>
public static class MeleeAttackSweep
{
	public static bool SphereSweepBladeSegment(
		Scene scene,
		GameObject attackerRoot,
		PlayerCombat attackerCombat,
		float radius,
		float maxSubstepLength,
		Vector3 tipA,
		Vector3 tipB,
		Vector3 heelA,
		Vector3 heelB,
		HashSet<Guid> hitVictimRootIds,
		int maxTargetsHit,
		bool allowMultipleHits,
		float damage,
		float stagger,
		byte attackState,
		byte attackType,
		bool isHeavy,
		ushort attackInstanceId,
		string swingLogNote,
		bool logHits,
		ref int targetsHitCount,
		Action<MeleeHitResult> onHitReported )
	{
		if ( !allowMultipleHits && targetsHitCount >= 1 )
			return false;

		if ( targetsHitCount >= maxTargetsHit )
			return false;

		var hitCapReached = false;
		hitCapReached |= SubSweep( scene, attackerRoot, attackerCombat, radius, maxSubstepLength, tipA, tipB, hitVictimRootIds,
			maxTargetsHit, allowMultipleHits, damage, stagger, attackState, attackType, isHeavy, attackInstanceId, swingLogNote, logHits,
			ref targetsHitCount, ref hitCapReached, onHitReported );
		hitCapReached |= SubSweep( scene, attackerRoot, attackerCombat, radius, maxSubstepLength, heelA, heelB, hitVictimRootIds,
			maxTargetsHit, allowMultipleHits, damage, stagger, attackState, attackType, isHeavy, attackInstanceId, swingLogNote, logHits,
			ref targetsHitCount, ref hitCapReached, onHitReported );
		return hitCapReached;
	}

	static bool SubSweep(
		Scene scene,
		GameObject attackerRoot,
		PlayerCombat attackerCombat,
		float radius,
		float maxSubstepLength,
		Vector3 a,
		Vector3 b,
		HashSet<Guid> hitVictimRootIds,
		int maxTargetsHit,
		bool allowMultipleHits,
		float damage,
		float stagger,
		byte attackState,
		byte attackType,
		bool isHeavy,
		ushort attackInstanceId,
		string swingLogNote,
		bool logHits,
		ref int targetsHitCount,
		ref bool hitCapReached,
		Action<MeleeHitResult> onHitReported )
	{
		var delta = b - a;
		var len = delta.Length;
		if ( len < 1e-4f )
			return hitCapReached;

		var dir = delta / len;
		var step = Math.Max( 4f, maxSubstepLength );
		var steps = Math.Max( 1, (int)Math.Ceiling( len / step ) );

		for ( var i = 0; i < steps; i++ )
		{
			if ( !allowMultipleHits && targetsHitCount >= 1 )
			{
				hitCapReached = true;
				return true;
			}

			if ( targetsHitCount >= maxTargetsHit )
			{
				hitCapReached = true;
				return true;
			}

			var t0 = a + dir * (len * (i / (float)steps));
			var t1 = a + dir * (len * ((i + 1) / (float)steps));
			if ( TryConsumeSphereHit( scene, attackerRoot, attackerCombat, radius, t0, t1, hitVictimRootIds, damage, stagger,
				    attackState, attackType, isHeavy, attackInstanceId, swingLogNote, logHits, ref targetsHitCount, onHitReported ) )
			{
				if ( !allowMultipleHits || targetsHitCount >= maxTargetsHit )
				{
					hitCapReached = true;
					return true;
				}
			}
		}

		return hitCapReached;
	}

	static bool TryConsumeSphereHit(
		Scene scene,
		GameObject attackerRoot,
		PlayerCombat attackerCombat,
		float radius,
		Vector3 segA,
		Vector3 segB,
		HashSet<Guid> hitVictimRootIds,
		float damage,
		float stagger,
		byte attackState,
		byte attackType,
		bool isHeavy,
		ushort attackInstanceId,
		string swingLogNote,
		bool logHits,
		ref int targetsHitCount,
		Action<MeleeHitResult> onHitReported )
	{
		var tr = scene.Trace.Sphere( radius, segA, segB )
			.IgnoreGameObjectHierarchy( attackerRoot )
			.UseHitboxes()
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return false;

		if ( !CombatAuthority.TryFindDamageable( tr.GameObject, out var recv ) || recv is not DamageReceiver dmg )
			return false;

		if ( CombatAuthority.IsGameObjectUnderHierarchy( attackerRoot, tr.GameObject ) )
			return false;

		if ( !CombatAuthority.MayApplyMeleeDamageFromAttackerToReceiver( attackerRoot, dmg ) )
			return false;

		var vitals = CombatAuthority.ResolvePlayerVitalsForDamageReceiver( dmg );
		if ( vitals is not null && vitals.CurrentHealth <= 0.001f )
			return false;

		var dedupId = CombatAuthority.ResolveMeleeVictimDedupId( dmg );
		var alreadyHit = !hitVictimRootIds.Add( dedupId );
		if ( alreadyHit )
			return false;

		var dmgAmount = damage;
		var stMul = 1f;
		if ( MeleeAttackResolution.TryGetBlockDamageMultiplier( attackerRoot, dmg, attackType, isHeavy, out var blockMul, out var blockStMul, out _ ) )
		{
			dmgAmount *= blockMul;
			stMul *= blockStMul;
		}

		var dealt = dmg.TakeDamage( dmgAmount, attackerCombat );
		var staggerApplied = stagger * stMul;
		attackerCombat.ApplyMeleeStaggerToVictim( vitals, staggerApplied );

		targetsHitCount++;

		onHitReported?.Invoke( new MeleeHitResult
		{
			AttackerId = attackerRoot.Id,
			TargetId = dedupId,
			AttackInstanceId = attackInstanceId,
			AttackType = attackType,
			IsHeavy = isHeavy,
			AttackState = attackState,
			HitPosition = tr.HitPosition,
			DamageApplied = dealt,
			StaggerApplied = staggerApplied,
			TargetsHitCount = targetsHitCount,
			TargetWasAlreadyHit = false
		} );

		if ( logHits )
		{
			Log.Info(
				$"[PlayerCombat/MeleeSweep] {MeleeAttackTypes.Label( attackType )} {MeleeAttackStates.Label( attackState )} heavy={isHeavy} hit {dmg.GameObject.Name} dmg={dealt:0.#} stagger={staggerApplied:0.#} targets={targetsHitCount}{swingLogNote}" );
		}

		return true;
	}
}
