using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Server-side hit resolution after a melee sweep hits a <see cref="DamageReceiver"/> (block cone, parry stub).
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

		if ( victimReceiver is null || !victimReceiver.GameObject.IsValid() || !attackerRoot.IsValid() )
			return false;

		blockingCombat = ResolvePlayerCombat( victimReceiver.GameObject );
		if ( blockingCombat is null )
			return false;

		blockingCombat = null;
		return TryLegacyBlockDefender( attackerRoot, victimReceiver, out damageMultiplier, out victimStaminaDrainMultiplier );
	}

	static PlayerCombat ResolvePlayerCombat( GameObject victimGo )
	{
		var combat = victimGo.Components.Get<PlayerCombat>();
		if ( combat is not null )
			return combat;

		for ( var p = victimGo.Parent; p.IsValid(); p = p.Parent )
		{
			combat = p.Components.Get<PlayerCombat>();
			if ( combat is not null )
				return combat;
		}

		return null;
	}

	static bool TryLegacyBlockDefender(
		GameObject attackerRoot,
		DamageReceiver victimReceiver,
		out float damageMultiplier,
		out float victimStaminaDrainMultiplier )
	{
		damageMultiplier = 1f;
		victimStaminaDrainMultiplier = 1f;

		MeleeBlockDefender block = victimReceiver.Components.Get<MeleeBlockDefender>();
		if ( block is null )
		{
			for ( var p = victimReceiver.GameObject; p.IsValid(); p = p.Parent )
			{
				block = p.Components.Get<MeleeBlockDefender>();
				if ( block is not null )
					break;
			}
		}

		if ( block is null || !block.IsBlockingForServer )
			return false;

		var victimGo = victimReceiver.GameObject;
		var toAttacker = attackerRoot.WorldPosition - victimGo.WorldPosition;
		toAttacker = new Vector3( toAttacker.x, 0f, toAttacker.z );
		if ( toAttacker.LengthSquared < 1e-4f )
			return false;
		toAttacker = toAttacker.Normal;

		var facing = block.BlockFacingWorld;
		facing = new Vector3( facing.x, 0f, facing.z );
		if ( facing.LengthSquared < 1e-4f )
			return false;
		facing = facing.Normal;

		var dot = Math.Clamp( Vector3.Dot( facing, toAttacker ), -1f, 1f );
		var deg = MathF.Acos( dot ) * (180f / MathF.PI);
		if ( deg > Math.Max( 5f, block.BlockArcHalfAngleDegrees ) )
			return false;

		damageMultiplier = Math.Clamp( block.BlockedDamageMultiplier, 0f, 1f );
		victimStaminaDrainMultiplier = Math.Clamp( block.BlockedVictimStaminaDrainMultiplier, 0f, 1f );
		return true;
	}

	public static bool TryGetParryDamageMultiplier( DamageReceiver victimReceiver, out float damageMultiplier )
	{
		damageMultiplier = 1f;
		_ = victimReceiver;
		return false;
	}
}
