using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Transition-only console logging for melee phase + animation start/stop (spam-click / timing debug).
/// </summary>
public partial class PlayerCombat
{
	[Property, Group( "Combat — Debug" ), Title( "Log melee phase / anim transitions" ),
	 Description( "Console on enter/exit only: Swing start, windup, attack, recover, ready + animation names." )]
	public bool LogMeleeAttackPhaseDebug { get; set; } = true;

	string _meleePhaseDebugCurrent;
	double _meleePhaseDebugEnteredAt;
	string _meleeAnimDebugCurrent;
	double _meleeAnimDebugEnteredAt;
	bool _meleePhaseDebugWasChainBusy;
	bool _meleePhaseDebugSawBusy;
	string _meleePhaseDebugLastBusyReason = "—";

	internal void LogMeleePhasePulse( string phase, string detail = null )
	{
		if ( !LogMeleeAttackPhaseDebug )
			return;

		var extra = string.IsNullOrWhiteSpace( detail ) ? "" : $" | {detail}";
		Log.Info( $"[MeleePhase] t={Time.Now:0.000} EVENT \"{phase}\"{extra}" );
	}

	internal void LogMeleePhaseEnter( string phase, string detail = null )
	{
		if ( !LogMeleeAttackPhaseDebug )
			return;

		if ( string.Equals( _meleePhaseDebugCurrent, phase, StringComparison.Ordinal ) )
			return;

		LogMeleePhaseExitIfAny();
		_meleePhaseDebugCurrent = phase;
		_meleePhaseDebugEnteredAt = Time.NowDouble;
		var extra = string.IsNullOrWhiteSpace( detail ) ? "" : $" | {detail}";
		Log.Info( $"[MeleePhase] t={Time.Now:0.000} START \"{phase}\"{extra}" );
	}

	internal void LogMeleePhaseExitIfAny( string reason = null )
	{
		if ( !LogMeleeAttackPhaseDebug || string.IsNullOrEmpty( _meleePhaseDebugCurrent ) )
			return;

		var dt = Math.Max( 0.0, Time.NowDouble - _meleePhaseDebugEnteredAt );
		var extra = string.IsNullOrWhiteSpace( reason ) ? "" : $" | {reason}";
		Log.Info( $"[MeleePhase] t={Time.Now:0.000} STOP  \"{_meleePhaseDebugCurrent}\" (dt={dt:0.000}s){extra}" );
		_meleePhaseDebugCurrent = null;
	}

	internal void LogMeleeAnimStart( string animLabel, string detail = null )
	{
		if ( !LogMeleeAttackPhaseDebug )
			return;

		if ( string.Equals( _meleeAnimDebugCurrent, animLabel, StringComparison.Ordinal ) )
			return;

		LogMeleeAnimStopIfAny( "replaced" );
		_meleeAnimDebugCurrent = animLabel;
		_meleeAnimDebugEnteredAt = Time.NowDouble;
		var extra = string.IsNullOrWhiteSpace( detail ) ? "" : $" | {detail}";
		Log.Info( $"[MeleeAnim]  t={Time.Now:0.000} START \"{animLabel}\"{extra}" );
	}

	internal void LogMeleeAnimStopIfAny( string reason = null )
	{
		if ( !LogMeleeAttackPhaseDebug || string.IsNullOrEmpty( _meleeAnimDebugCurrent ) )
			return;

		var dt = Math.Max( 0.0, Time.NowDouble - _meleeAnimDebugEnteredAt );
		var extra = string.IsNullOrWhiteSpace( reason ) ? "" : $" | {reason}";
		Log.Info( $"[MeleeAnim]  t={Time.Now:0.000} STOP  \"{_meleeAnimDebugCurrent}\" (dt={dt:0.000}s){extra}" );
		_meleeAnimDebugCurrent = null;
	}

	/// <summary>Owner: emit Ready when chain-busy falls (recovery + sweep done).</summary>
	internal void TickMeleePhaseReadyDebug()
	{
		if ( !LogMeleeAttackPhaseDebug || !IsLocalCombatDriver() )
			return;

		var busy = IsMeleeAttackChainBusy();
		if ( busy )
		{
			_meleePhaseDebugSawBusy = true;
			_meleePhaseDebugLastBusyReason = FormatMeleePhaseBusyReason();
		}

		if ( _meleePhaseDebugSawBusy && _meleePhaseDebugWasChainBusy && !busy )
			LogMeleePhaseEnter( "ready to attack again", $"wasBusy={_meleePhaseDebugLastBusyReason}" );

		_meleePhaseDebugWasChainBusy = busy;
	}

	internal string FormatMeleePhaseBusyReason()
	{
		if ( IsCombatActionLocked )
		{
			var rem = Math.Max( _combatRecoveryRemaining, NetworkedCombatRecoveryRemaining );
			if ( Time.NowDouble < _combatActionLockUntilSandbox )
				rem = Math.Max( rem, (float)( _combatActionLockUntilSandbox - Time.NowDouble ) );
			return $"recovery/hit-reaction rem={rem:0.000}s";
		}
		if ( ServerHasActiveMeleeAttackAction )
			return "server melee active";
		if ( _primarySwingPhaseActive )
			return "client swing drag window";
		if ( Components.Get<PlayerAnimation>() is { IsMeleeSwingAnimBusy: true } anim )
			return $"swing anim rem={anim.GetMeleeAttackAnimBusyRemainingSeconds():0.000}s";
		if ( GameObject.Network is { Active: true } && !Networking.IsHost && _ownerExpectsHostMeleeBusy )
			return "awaiting host sweep complete";
		return "not busy";
	}
}
