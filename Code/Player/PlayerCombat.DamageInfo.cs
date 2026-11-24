using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Practice readout (Mark 2026-11-18: "a 'show damage information' toggle in game settings that tells us how much damage
/// we did for the last hit, and the aceleration or decel buff/debuff, just so we can see if were doing it right").
/// The host tells the swing's owner what each contact of its swing did - the damage, the turn damage scale that hit
/// used (<see cref="TurnDamageScale"/>), heavy / blocked / chambered - and the crosshair shows it for a few seconds when
/// <see cref="CombatControlSettings.ShowDamageInfo"/> is on. One small owner RPC per contact; nothing per frame.
/// </summary>
public partial class PlayerCombat
{
	/// <summary>How long the line stays under the crosshair after a hit.</summary>
	const float DamageInfoShowSeconds = 4f;

	float _damageInfoAt = float.NegativeInfinity;
	string _damageInfoText = "";

	/// <summary>Host: one contact of this pawn's swing (a hit, a block or a chamber) - report it to the pawn's owner.</summary>
	internal void HostReportDamageInfo( float damage, float turnScale, bool heavy, bool blocked, bool chambered )
	{
		// Scavs and other ownerless fighters have nobody to tell.
		if ( GameObject.Network is { Active: true, Owner: not null } )
			RpcOwnerDamageInfo( damage, turnScale, heavy, blocked, chambered );
		else if ( GameObject.Network is not { Active: true } )
			ApplyDamageInfo( damage, turnScale, heavy, blocked, chambered );
	}

	[Rpc.Owner]
	void RpcOwnerDamageInfo( float damage, float turnScale, bool heavy, bool blocked, bool chambered ) =>
		ApplyDamageInfo( damage, turnScale, heavy, blocked, chambered );

	void ApplyDamageInfo( float damage, float turnScale, bool heavy, bool blocked, bool chambered )
	{
		_damageInfoAt = Time.Now;

		string turn;
		var percent = MathF.Round( (turnScale - 1f) * 100f );
		if ( percent > 0f )
			turn = $"turn +{percent:0}%";
		else if ( percent < 0f )
			turn = $"turn {percent:0}%";
		else
			turn = "turn +0%";

		var kind = heavy ? "heavy" : "light";
		if ( chambered )
			_damageInfoText = $"Chambered · {kind}";
		else if ( blocked )
			_damageInfoText = $"Blocked · {kind} · {turn}";
		else
			_damageInfoText = $"{damage:0.#} damage · {kind} · {turn}";
	}

	/// <summary>Owner HUD: the last hit's line while it is fresh and the setting is on.</summary>
	public bool TryGetDamageInfoText( out string text )
	{
		text = _damageInfoText;
		var age = Time.Now - _damageInfoAt;
		return CombatControlSettings.ShowDamageInfo && age >= 0f && age < DamageInfoShowSeconds && !string.IsNullOrEmpty( text );
	}
}
