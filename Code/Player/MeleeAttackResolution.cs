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
		var contact = new MeleeBlockContact
		{
			AttackerRoot = attackerRoot,
			AttackerPosition = attackerRoot.WorldPosition,
			DefenderRoot = defender.GameObject,
			DefenderCombat = defender,
			HitPosition = hitPosition,
			AttackType = attackType,
			AttackWasHeavy = attackWasHeavy,
			HitSandboxTime = Time.NowDouble
		};

		if ( !defender.TryServerResolveBlock( in contact, defender.LogMeleeBlockRejectionsToConsole, out damageMultiplier,
			     out victimStaminaDrainMultiplier, out _, out blockTrace ) )
			return false;

		return damageMultiplier < 0.999f;
	}
}
