using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Static three-hit melee combo (Settings → Dev tools, <see cref="DevToolsSettings.StaticMeleeCombo"/>; Mark 2026-11-06).
/// The mouse no longer picks the attack: quick clicks go right → left → overhead (crosshair arrows left, right, up),
/// two quick clicks are right → left, one is always right. "Quick" = the click lands while the previous swing is
/// still going or within <see cref="ComboChainWindowSeconds"/> after it ended, hit or miss. A long hold turns that
/// step heavy and ends the combo (light, light, heavy = a heavy overhead). The overhead and any heavy are followed by
/// <see cref="ComboEndPauseSeconds"/> before the next swing comes out (a click in that pause waits in the queue).
/// Q on the ground is the weapon class special (sword / club: a stab that winds up <c>specialWindupSeconds</c> and hits
/// for <c>specialDamageMultiplier</c> × light); it never turns heavy and starts the next click at the right attack.
/// The owner picks the direction (as in free aim); the host recounts the combo on its own clock for the overhead's
/// <see cref="ComboFinisherDamageMultiplier"/> and checks the special once (class has one, pawn on the ground).
/// </summary>
public partial class PlayerCombat
{
	/// <summary>A click within this long after the previous swing ended (or during it) continues the combo; later starts over at the right attack.</summary>
	[Property, Group( "Combat — Static combo" ), Title( "Combo chain window (s)" ), Range( 0.1f, 2f ), Step( 0.05f )]
	public float ComboChainWindowSeconds { get; set; } = 0.6f;

	/// <summary>The combo's third swing (the overhead) hits for this × its damage — × the heavy multiplier too when it is held.</summary>
	[Property, Group( "Combat — Static combo" ), Title( "Combo overhead damage ×" ), Range( 1f, 3f ), Step( 0.05f )]
	public float ComboFinisherDamageMultiplier { get; set; } = 1.25f;

	/// <summary>After the overhead or a heavy, the next swing waits this long once the chain is free.</summary>
	[Property, Group( "Combat — Static combo" ), Title( "Pause after combo / heavy (s)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float ComboEndPauseSeconds { get; set; } = 0.2f;

	/// <summary>The weapon special (Q, on the ground, static combo mode only).</summary>
	[Property, Group( "Combat — Static combo" ), Title( "Special attack input action" )]
	public string SpecialAttackAction { get; set; } = "SpecialAttack";

	/// <summary>Host only: extra time allowed on top of the chain window for the round trip (client measures from its own swing end).</summary>
	const float HostComboLatencySlackSeconds = 0.3f;

	/// <summary>The local player's static combo mode is on (dev toggle); combat_test pawns never read it.</summary>
	public bool StaticComboActive => !InputSuppressedForTests && DevToolsSettings.StaticMeleeCombo;

	// Owner: last combo step fired (0 none / reset, 1 right, 2 left; the overhead resets to 0), the last frame the chain
	// was busy, and the post-combo pause.
	byte _comboStep;
	double _comboLastBusyAt = double.NegativeInfinity;
	bool _comboEndPending;
	double _comboPauseUntil;
	bool _ownerSwingSpecial;

	// Queued follow-up carries the combo step / special it was clicked as.
	byte _queuedComboStep;
	bool _queuedSpecial;

	// Host: combo step it last accepted and when that player's previous swing ended.
	byte _hostComboStep;
	double _hostComboLastEndAt = double.NegativeInfinity;

	/// <summary>Combo step → swing direction: 1 right (arrow left), 2 left (arrow right), 3 overhead.</summary>
	public static byte StaticComboSwingDir( byte step ) =>
		step switch { 2 => SwingDirs.Left, 3 => SwingDirs.Up, _ => SwingDirs.Right };

	/// <summary>Owner, static mode: the next swing waits for the chain, then for the post-combo pause.</summary>
	bool IsStaticComboChainBusy() =>
		IsMeleeAttackChainBusy() || _comboEndPending || Time.NowDouble < _comboPauseUntil;

	/// <summary>Owner gate for firing a click / queued click in the current mode.</summary>
	bool IsOwnerAttackGateBusy() => StaticComboActive ? IsStaticComboChainBusy() : IsMeleeAttackChainBusy();

	/// <summary>Owner, static mode: replaces the mouse-direction click — combo step from click timing, Q special.</summary>
	void TickOwnerStaticCombo( bool clicked )
	{
		var now = Time.NowDouble;
		var chainBusy = IsMeleeAttackChainBusy();
		if ( chainBusy )
			_comboLastBusyAt = now;
		else if ( _comboEndPending )
		{
			_comboEndPending = false;
			_comboPauseUntil = now + Math.Max( 0f, ComboEndPauseSeconds );
		}

		if ( IsBlockPreventingAttack() )
			return;

		if ( clicked )
		{
			var chained = _comboStep is 1 or 2 && (chainBusy || now - _comboLastBusyAt <= ComboChainWindowSeconds);
			var step = (byte)(chained ? _comboStep + 1 : 1);
			var dir = StaticComboSwingDir( step );
			if ( IsStaticComboChainBusy() )
			{
				QueueAttackPress( dir, step, special: false );
				LogCombatDiag( "CLIENT / OWNER", $"Combo click {step} queued — {FormatMeleePhaseBusyReason()}" );
			}
			else
				TryFirePrimaryAttack( dir, "combo click", step );
		}

		if ( !string.IsNullOrWhiteSpace( SpecialAttackAction ) && Input.Pressed( SpecialAttackAction ) && OwnerCanStartSpecial() )
		{
			if ( IsStaticComboChainBusy() )
				QueueAttackPress( SwingDirs.Down, 0, special: true );
			else
				TryFirePrimaryAttack( SwingDirs.Down, "special", 0, special: true );
		}
	}

	/// <summary>Owner: Q works with a class special, standing on the ground (not grappling / swimming / seated).</summary>
	bool OwnerCanStartSpecial()
	{
		if ( !GetMeleeWeaponTimings().HasStabSpecial )
			return false;

		var controller = Components.Get<PlayerController>();
		if ( controller is null || !controller.IsOnGround )
			return false;

		return Components.Get<PlayerMovement>() is not { GrappleAttached: true };
	}

	/// <summary>Owner: a swing just went out — advance / reset the combo.</summary>
	void OnOwnerSwingFired( byte comboStep, bool special )
	{
		_ownerSwingSpecial = special;
		if ( !StaticComboActive )
			return;

		if ( special || comboStep >= 3 )
		{
			_comboStep = 0;
			_comboEndPending = comboStep >= 3;
		}
		else
			_comboStep = comboStep;
	}

	/// <summary>Owner: the held swing turned heavy — it ends the combo and the pause follows it.</summary>
	void OnOwnerSwingTurnedHeavy()
	{
		if ( !StaticComboActive )
			return;

		_comboStep = 0;
		_comboEndPending = true;
	}

	/// <summary>Owner: guard up / swing cancelled — the combo starts over, no pause.</summary>
	void ResetOwnerStaticCombo()
	{
		_comboStep = 0;
		_comboEndPending = false;
	}

	/// <summary>
	/// Host, once when a player's swing is accepted: the damage scale for this swing — the class special multiplier for
	/// a special, the finisher bonus when the owner calls it the third combo swing and the host also counted two quick
	/// swings before it (a client claiming step 3 out of nowhere gets plain damage). Entities send step 0.
	/// </summary>
	internal float HostResolveSwingDamageScale( in AttackReleaseIntent intent )
	{
		if ( intent.IsSpecial )
		{
			_hostComboStep = 0;
			return GetMeleeWeaponTimings().SpecialDamageMultiplier;
		}

		if ( intent.ComboStep == 0 )
		{
			_hostComboStep = 0;
			return 1f;
		}

		// The owner's step 1 always starts a new combo; a later step continues only what the host counted itself.
		var chained = intent.ComboStep > 1 && _hostComboStep is 1 or 2
		              && Time.NowDouble - _hostComboLastEndAt <= ComboChainWindowSeconds + HostComboLatencySlackSeconds;
		var step = chained ? _hostComboStep + 1 : 1;
		_hostComboStep = (byte)(step >= 3 ? 0 : step);
		return intent.ComboStep == 3 && step == 3 ? Math.Max( 0f, ComboFinisherDamageMultiplier ) : 1f;
	}

	/// <summary>Host: the swing turned heavy — the combo is over.</summary>
	internal void HostNoteComboBrokenByHeavy() => _hostComboStep = 0;

	/// <summary>Host: a player's swing finished (chain window starts now).</summary>
	void HostNoteSwingEnded() => _hostComboLastEndAt = Time.NowDouble;

	/// <summary>
	/// Host: Q special allowed — the class has one and the pawn stands on the ground (lenient: the host's view of a
	/// remote pawn can flicker IsOnGround off, so a small vertical speed still counts as standing).
	/// </summary>
	internal bool HostMaySpecialAttack( out string reason )
	{
		if ( !GetMeleeWeaponTimings().HasStabSpecial )
		{
			reason = $"class {GetMeleeWeaponTimings().ClassId} has no special attack";
			return false;
		}

		if ( Components.Get<PlayerMovement>() is { } movement && !movement.HostSeesStandingOnGround() )
		{
			reason = "special attack needs the ground";
			return false;
		}

		reason = null;
		return true;
	}
}
