using Sandbox;

namespace Survival;

/// <summary>
/// Server-side hit resolution after a melee sweep hits a <see cref="DamageReceiver"/>.
/// Block validation runs through <see cref="MeleeBlockResolution"/> on the defender's <see cref="PlayerCombat"/>.
/// </summary>
public static class MeleeAttackResolution
{
	public static bool TryGetBlockDamageMultiplier(
		GameObject attackerRoot,
		DamageReceiver victimReceiver,
		Vector3 hitPosition,
		byte attackType,
		bool attackWasHeavy,
		out float damageMultiplier,
		out float victimStaminaDrainMultiplier,
		out PlayerCombat blockingCombat,
		out MeleeBlockValidationTrace blockTrace ) =>
		TryGetBlockDamageMultiplier( attackerRoot, victimReceiver, hitPosition, attackType, attackSwingDir: 0,
			attackWasHeavy, blockRayOrigin: null, blockRayEnd: null, extraRayThickness: 0f, out damageMultiplier,
			out victimStaminaDrainMultiplier, out blockingCombat, out blockTrace );

	public static bool TryGetBlockDamageMultiplier(
		GameObject attackerRoot,
		DamageReceiver victimReceiver,
		Vector3 hitPosition,
		byte attackType,
		byte attackSwingDir,
		bool attackWasHeavy,
		Vector3? blockRayOrigin,
		Vector3? blockRayEnd,
		float extraRayThickness,
		out float damageMultiplier,
		out float victimStaminaDrainMultiplier,
		out PlayerCombat blockingCombat,
		out MeleeBlockValidationTrace blockTrace )
	{
		damageMultiplier = 1f;
		victimStaminaDrainMultiplier = 1f;
		blockingCombat = null;
		blockTrace = default;

		if ( !attackerRoot.IsValid() || victimReceiver is null || !victimReceiver.GameObject.IsValid() )
			return false;

		var defender = MeleeBlockResolution.FindDefenderCombat( victimReceiver );
		if ( defender is null || !defender.IsValid() )
			return false;

		blockingCombat = defender;

		var resolveHitPos = hitPosition;
		var attackRayGeometryValidated = false;
		if ( blockRayOrigin.HasValue && blockRayEnd.HasValue )
		{
			var rayDelta = blockRayEnd.Value - blockRayOrigin.Value;
			if ( rayDelta.Length >= 1e-4f )
			{
				var rayDir = rayDelta / rayDelta.Length;
				var bodyDist = MeleeBlockPath.ProjectDistanceAlongRay( blockRayOrigin.Value, rayDir, hitPosition );
				if ( MeleeBlockPath.TryRaycastBlockGuardLine( defender, blockRayOrigin.Value, blockRayEnd.Value,
					     bodyDist + Math.Max( 2f, extraRayThickness ), extraRayThickness, out _, out var guardHitPos ) )
				{
					resolveHitPos = guardHitPos;
					attackRayGeometryValidated = true;
				}
			}
		}

		var contact = new MeleeBlockContact
		{
			AttackerRoot = attackerRoot,
			AttackerPosition = attackerRoot.WorldPosition,
			DefenderRoot = defender.GameObject,
			DefenderCombat = defender,
			HitPosition = resolveHitPos,
			AttackType = attackType,
			AttackSwingDir = attackSwingDir,
			AttackWasHeavy = attackWasHeavy,
			HitSandboxTime = Time.NowDouble,
			AttackRayGeometryValidated = attackRayGeometryValidated
		};

		if ( !defender.TryServerResolveBlock( in contact, defender.LogMeleeBlockRejectionsToConsole, out damageMultiplier,
			     out victimStaminaDrainMultiplier, out _, out blockTrace ) )
			return false;

		return damageMultiplier < 0.999f;
	}
}
