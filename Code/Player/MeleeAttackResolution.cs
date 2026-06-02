using Sandbox;

namespace Survival;

/// <summary>
/// Server-side hit resolution after a melee sweep hits a <see cref="DamageReceiver"/>.
/// Block validation will be wired through <see cref="PlayerCombat"/> when block combat is re-enabled.
/// </summary>
public static class MeleeAttackResolution
{
	public static bool TryGetBlockDamageMultiplier(
		GameObject attackerRoot,
		DamageReceiver victimReceiver,
		byte attackType,
		bool attackWasHeavy,
		out float damageMultiplier,
		out float victimStaminaDrainMultiplier,
		out PlayerCombat blockingCombat )
	{
		damageMultiplier = 1f;
		victimStaminaDrainMultiplier = 1f;
		blockingCombat = null;

		_ = attackerRoot;
		_ = victimReceiver;
		_ = attackType;
		_ = attackWasHeavy;
		return false;
	}
}
