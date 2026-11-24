using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Locomotion hooks combat hands to <see cref="PlayerMovement"/>. Melee swings, the guard and staggers do not cap the pawn
/// here any more: they scale ground speed by the combat footwork shares (<see cref="CurrentFootworkState"/> →
/// <see cref="PlayerMovement.GetCombatFootworkScale"/>, Mark 2026-11-14 spec), and the camera stays free — the swing basis
/// follows the look at the phase turn rate instead (<see cref="GetSwingTurnDegrees"/>). What is left here: an aimed bow
/// walks (no sprint) and the bola charge is walk-only at <see cref="MeleeAttackMoveSpeedScale"/>.
/// </summary>
public partial class PlayerCombat : Component, PlayerController.IEvents
{
	/// <summary>Multiplies Walk/Run wish speed while the bola charges. Sprint is suppressed.</summary>
	[Property, Group( "Combat — Attack commitment" ), Title( "Move speed scale while the bola charges" ), Range( 0.05f, 1f ), Step( 0.05f )]
	public float MeleeAttackMoveSpeedScale { get; set; } = 0.1f;

	/// <summary>Ground move is slowed + sprint suppressed while the bola charges. Airborne velocity is left alone.</summary>
	public bool IsMeleeAttackWalkOnlyActive => _bolaCharging;

	/// <summary>Regular walking speed, no sprint, while the bow is aimed (ADS). Read by <see cref="PlayerMovement"/> on the pawn's driver.</summary>
	public bool IsAttackWalkCapActive => _bowAdsActive;
}
