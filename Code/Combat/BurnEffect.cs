using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-only "On Fire!" from a fire arrow (<c>burnDamage</c> / <c>burnSeconds</c> on the ammo recipe):
/// deals the total burn damage in one-second ticks through the victim's <see cref="DamageReceiver"/>,
/// so animals, raiders and players all take the same number. A fresh hit restarts the burn (no
/// stacking). Players also get the <c>onFire</c> status for the HUD icon - that status itself deals
/// nothing, the ticks here do. Lives on the victim's object only while burning.
/// </summary>
public sealed class BurnEffect : Component
{
	public const string StatusId = "onFire";

	float _remainingDamage;
	float _remainingSeconds;
	float _perSecond;
	float _tickAccum;
	Component _attacker;
	DamageReceiver _receiver;

	public static void HostApply( DamageReceiver receiver, float totalDamage, float seconds, Component attacker )
	{
		if ( receiver is null || !receiver.IsValid() || totalDamage <= 0f )
			return;

		var go = receiver.GameObject;
		if ( go is null || !go.IsValid() )
			return;

		var burn = go.Components.Get<BurnEffect>() ?? go.Components.Create<BurnEffect>();
		burn._receiver = receiver;
		burn._attacker = attacker;
		burn._remainingDamage = totalDamage;
		burn._remainingSeconds = Math.Max( 0.5f, seconds );
		burn._perSecond = totalDamage / burn._remainingSeconds;
		burn._tickAccum = 0f;

		CombatAuthority.ResolvePlayerVitalsForDamageReceiver( receiver )?.HostApplyStatusEffect( StatusId, burn._remainingSeconds );
	}

	protected override void OnFixedUpdate()
	{
		if ( _receiver is null || !_receiver.IsValid() || !CombatAuthority.IsDamageVictimAlive( _receiver ) )
		{
			Destroy();
			return;
		}

		var dt = Time.Delta;
		_remainingSeconds -= dt;
		_tickAccum += dt;

		var last = _remainingSeconds <= 0f;
		if ( _tickAccum >= 1f || last )
		{
			var dmg = last ? _remainingDamage : Math.Min( _remainingDamage, _perSecond * _tickAccum );
			_tickAccum = 0f;
			if ( dmg > 0f )
			{
				_remainingDamage -= dmg;
				using ( PlaytestLog.Kind( "burn" ) )
					_receiver.TakeDamage( dmg, _attacker );
			}
		}

		if ( last || _remainingDamage <= 0.01f )
			Destroy();
	}
}
