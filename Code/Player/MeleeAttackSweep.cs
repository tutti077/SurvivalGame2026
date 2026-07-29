using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Server-side thickened sphere substeps along blade motion during damaging attack states.
/// </summary>
public static class MeleeAttackSweep
{
	public static bool RaySweepFromOrigin(
		Scene scene,
		GameObject attackerRoot,
		PlayerCombat attackerCombat,
		Vector3 origin,
		Vector3 tip,
		HashSet<Guid> hitVictimRootIds,
		int maxTargetsHit,
		bool allowMultipleHits,
		float damage,
		float stagger,
		byte attackState,
		byte attackType,
		byte attackSwingDir,
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
		if ( !scene.IsValid() || !attackerRoot.IsValid() || attackerCombat is null )
			return false;

		var delta = tip - origin;
		var lineLen = delta.Length;
		if ( lineLen < 1e-4f )
			return false;

		var dir = delta / lineLen;
		var bodyHitDist = float.MaxValue;
		DamageReceiver bodyReceiver = null;
		Vector3 bodyHitPos = tip;

		var tr = scene.Trace.Ray( origin, tip )
			.IgnoreGameObjectHierarchy( attackerRoot )
			.UseHitboxes()
			.Run();
		if ( !tr.Hit || !tr.GameObject.IsValid() )
		{
			tr = scene.Trace.Ray( origin, tip )
				.IgnoreGameObjectHierarchy( attackerRoot )
				.Run();
		}
		if ( tr.Hit && tr.GameObject.IsValid()
		     && CombatAuthority.TryFindDamageable( tr.GameObject, out var recv )
		     && recv is DamageReceiver dmg
		     && !CombatAuthority.IsGameObjectUnderHierarchy( attackerRoot, tr.GameObject )
		     && CombatAuthority.MayApplyMeleeDamageFromAttackerToReceiver( attackerRoot, dmg ) )
		{
			bodyHitDist = Math.Clamp( tr.Distance, 0f, lineLen );
			bodyReceiver = dmg;
			bodyHitPos = tr.HitPosition;
		}

		var rayThickness = Math.Max( 2f, attackerCombat.MeleeHitVolumeThickness );
		PlayerCombat blockFirstDefender = null;
		var blockFirstDist = float.MaxValue;
		var blockFirstPos = tip;
		var blockFirstOutcome = default( MeleeBlockOutcome );
		foreach ( var defender in scene.GetAllComponents<PlayerCombat>() )
		{
			if ( defender is null || defender == attackerCombat || !defender.Enabled || !defender.GameObject.IsValid() )
				continue;
			if ( !defender.IsAuthoritativeMeleeBlocking )
				continue;
			if ( !MeleeBlockPath.TryRaycastBlockGuardLine( defender, origin, tip, bodyHitDist + 1e-4f, rayThickness,
				     out var guardDist, out var guardPos ) )
				continue;
			if ( guardDist >= blockFirstDist )
				continue;

			var contact = new MeleeBlockContact
			{
				AttackerRoot = attackerRoot,
				AttackerPosition = attackerRoot.WorldPosition,
				DefenderRoot = defender.GameObject,
				DefenderCombat = defender,
				HitPosition = guardPos,
				AttackType = attackType,
				AttackSwingDir = attackSwingDir,
				AttackWasHeavy = isHeavy,
				HitSandboxTime = Time.NowDouble,
				AttackRayGeometryValidated = true
			};
			if ( !defender.TryServerResolveBlock( in contact, defender.LogMeleeBlockRejectionsToConsole,
				     out var outcome, out _, out _ ) )
				continue;

			blockFirstDefender = defender;
			blockFirstDist = guardDist;
			blockFirstPos = guardPos;
			blockFirstOutcome = outcome;
		}

		// Body-shell hit before body => block intercept (JSON HP / stamina / stagger).
		if ( blockFirstDefender is not null && blockFirstDist <= bodyHitDist + 1e-4f )
		{
			var dedupId = blockFirstDefender.GameObject.Id;
			if ( !hitVictimRootIds.Add( dedupId ) )
				return false;

			blockFirstDefender.NotifyAuthoritativeMeleeBlockIntercepted();
			blockFirstDefender.ServerApplyBlockOutcome( in blockFirstOutcome, attackerCombat );
			targetsHitCount++;

			onHitReported?.Invoke( new MeleeHitResult
			{
				AttackerId = attackerRoot.Id,
				TargetId = dedupId,
				AttackInstanceId = attackInstanceId,
				AttackType = attackType,
				IsHeavy = isHeavy,
				AttackState = attackState,
				HitPosition = blockFirstPos,
				DamageApplied = blockFirstOutcome.HealthDamage,
				StaggerApplied = blockFirstOutcome.DurationSeconds,
				TargetsHitCount = targetsHitCount,
				TargetWasAlreadyHit = false,
				WasBlocked = true,
				IncomingAngleDegrees = MeleeBlockResolution.ComputeIncomingAngleFromAttacker(
					blockFirstDefender, attackerRoot.WorldPosition )
			} );

			if ( logHits )
			{
				Log.Info(
					$"[PlayerCombat/MeleeSweepRay] {MeleeAttackTypes.Label( attackType )} {MeleeAttackStates.Label( attackState )} heavy={isHeavy} BLOCKED by {blockFirstDefender.GameObject.Name} outcome={blockFirstOutcome.OutcomeId} hp={blockFirstOutcome.HealthDamage:0.#} stam={blockFirstOutcome.StaminaCost:0.#} stagger={blockFirstOutcome.DurationSeconds:0.###}s targets={targetsHitCount}{swingLogNote}" );
			}

			return true;
		}

		if ( bodyReceiver is null )
			return false;

		if ( !CombatAuthority.IsDamageVictimAlive( bodyReceiver ) )
			return false;

		var dedupBodyId = CombatAuthority.ResolveMeleeVictimDedupId( bodyReceiver );
		if ( !hitVictimRootIds.Add( dedupBodyId ) )
			return false;

		var bodyDmgAmount = damage;
		var bodyStaggerApplied = stagger;
		var wasBlocked = false;
		var incomingAngle = 0f;
		PlayerCombat blockingCombat = null;
		if ( MeleeAttackResolution.TryGetBlockOutcome( attackerRoot, bodyReceiver, bodyHitPos, attackType,
			     attackSwingDir, isHeavy, origin, tip, rayThickness, out var blockOutcome, out blockingCombat,
			     out var blockTrace ) )
		{
			wasBlocked = true;
			incomingAngle = blockTrace.IncomingAngleDegrees;
			blockingCombat?.NotifyAuthoritativeMeleeBlockIntercepted();
			blockingCombat?.ServerApplyBlockOutcome( in blockOutcome, attackerCombat );
			bodyDmgAmount = 0f;
			bodyStaggerApplied = blockOutcome.DurationSeconds;

			onHitReported?.Invoke( new MeleeHitResult
			{
				AttackerId = attackerRoot.Id,
				TargetId = dedupBodyId,
				AttackInstanceId = attackInstanceId,
				AttackType = attackType,
				IsHeavy = isHeavy,
				AttackState = attackState,
				HitPosition = bodyHitPos,
				DamageApplied = blockOutcome.HealthDamage,
				StaggerApplied = blockOutcome.DurationSeconds,
				TargetsHitCount = ++targetsHitCount,
				TargetWasAlreadyHit = false,
				WasBlocked = true,
				IncomingAngleDegrees = incomingAngle
			} );

			if ( logHits )
			{
				Log.Info(
					$"[PlayerCombat/MeleeSweepRay] {MeleeAttackTypes.Label( attackType )} {MeleeAttackStates.Label( attackState )} heavy={isHeavy} BLOCKED body-path {bodyReceiver.GameObject.Name} outcome={blockOutcome.OutcomeId} hp={blockOutcome.HealthDamage:0.#} stagger={blockOutcome.DurationSeconds:0.###}s targets={targetsHitCount}{swingLogNote}" );
			}

			return true;
		}

		var bodyDealt = bodyReceiver.TakeDamage( bodyDmgAmount, attackerCombat );
		var bodyVitals = CombatAuthority.ResolvePlayerVitalsForDamageReceiver( bodyReceiver );
		attackerCombat.ApplyMeleeStaggerToVictim( bodyVitals, bodyStaggerApplied );
		targetsHitCount++;

		onHitReported?.Invoke( new MeleeHitResult
		{
			AttackerId = attackerRoot.Id,
			TargetId = dedupBodyId,
			AttackInstanceId = attackInstanceId,
			AttackType = attackType,
			IsHeavy = isHeavy,
			AttackState = attackState,
			HitPosition = bodyHitPos,
			DamageApplied = bodyDealt,
			StaggerApplied = bodyStaggerApplied,
			TargetsHitCount = targetsHitCount,
			TargetWasAlreadyHit = false,
			WasBlocked = wasBlocked,
			IncomingAngleDegrees = incomingAngle
		} );

		if ( logHits )
		{
			Log.Info(
				$"[PlayerCombat/MeleeSweepRay] {MeleeAttackTypes.Label( attackType )} {MeleeAttackStates.Label( attackState )} heavy={isHeavy} hit {bodyReceiver.GameObject.Name} dmg={bodyDealt:0.#} stagger={bodyStaggerApplied:0.#} angle={incomingAngle:0.#}° targets={targetsHitCount}{swingLogNote}" );
		}

		return true;
	}

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
		// Prefer hitboxes (pawns/entities); fall back to solid so world meshes (trees) still register.
		var tr = scene.Trace.Sphere( radius, segA, segB )
			.IgnoreGameObjectHierarchy( attackerRoot )
			.UseHitboxes()
			.Run();
		if ( !tr.Hit || !tr.GameObject.IsValid() )
		{
			tr = scene.Trace.Sphere( radius, segA, segB )
				.IgnoreGameObjectHierarchy( attackerRoot )
				.Run();
		}

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return false;

		if ( !CombatAuthority.TryFindDamageable( tr.GameObject, out var recv ) || recv is not DamageReceiver dmg )
			return false;

		if ( CombatAuthority.IsGameObjectUnderHierarchy( attackerRoot, tr.GameObject ) )
			return false;

		if ( !CombatAuthority.MayApplyMeleeDamageFromAttackerToReceiver( attackerRoot, dmg ) )
			return false;

		if ( !CombatAuthority.IsDamageVictimAlive( dmg ) )
			return false;

		var dedupId = CombatAuthority.ResolveMeleeVictimDedupId( dmg );
		var alreadyHit = !hitVictimRootIds.Add( dedupId );
		if ( alreadyHit )
			return false;

		var dmgAmount = damage;
		var staggerAppliedAmount = stagger;
		var wasBlocked = false;
		var incomingAngle = 0f;
		PlayerCombat blockingCombat = null;

		if ( MeleeAttackResolution.TryGetBlockOutcome( attackerRoot, dmg, tr.HitPosition, attackType,
			     attackSwingDir: 0, isHeavy, segA, segB, Math.Max( 2f, attackerCombat.MeleeHitVolumeThickness ),
			     out var blockOutcome, out blockingCombat, out var blockTrace ) )
		{
			wasBlocked = true;
			incomingAngle = blockTrace.IncomingAngleDegrees;
			blockingCombat?.NotifyAuthoritativeMeleeBlockIntercepted();
			blockingCombat?.ServerApplyBlockOutcome( in blockOutcome, attackerCombat );
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
				DamageApplied = blockOutcome.HealthDamage,
				StaggerApplied = blockOutcome.DurationSeconds,
				TargetsHitCount = targetsHitCount,
				TargetWasAlreadyHit = false,
				WasBlocked = true,
				IncomingAngleDegrees = incomingAngle
			} );

			if ( logHits )
			{
				Log.Info(
					$"[PlayerCombat/MeleeSweep] {MeleeAttackTypes.Label( attackType )} {MeleeAttackStates.Label( attackState )} heavy={isHeavy} BLOCKED {dmg.GameObject.Name} outcome={blockOutcome.OutcomeId} hp={blockOutcome.HealthDamage:0.#} stagger={blockOutcome.DurationSeconds:0.###}s targets={targetsHitCount}{swingLogNote}" );
			}

			return true;
		}

		var dealt = dmg.TakeDamage( dmgAmount, attackerCombat );
		var vitals = CombatAuthority.ResolvePlayerVitalsForDamageReceiver( dmg );
		attackerCombat.ApplyMeleeStaggerToVictim( vitals, staggerAppliedAmount );

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
			StaggerApplied = staggerAppliedAmount,
			TargetsHitCount = targetsHitCount,
			TargetWasAlreadyHit = false,
			WasBlocked = wasBlocked,
			IncomingAngleDegrees = incomingAngle
		} );

		if ( logHits )
		{
			Log.Info(
				$"[PlayerCombat/MeleeSweep] {MeleeAttackTypes.Label( attackType )} {MeleeAttackStates.Label( attackState )} heavy={isHeavy} hit {dmg.GameObject.Name} dmg={dealt:0.#} stagger={staggerAppliedAmount:0.#} angle={incomingAngle:0.#}° targets={targetsHitCount}{swingLogNote}" );
		}

		return true;
	}

}
