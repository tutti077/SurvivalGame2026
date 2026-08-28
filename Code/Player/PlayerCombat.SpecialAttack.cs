using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Q special attack: press once → the pawn charges in a frozen windup pose for the class
/// `specialWindupSeconds`, then the stab auto-fires (no hold, no release). The charge IS the windup —
/// the intent carries it as hold time, so the host plays zero remaining windup and thrusts immediately
/// (same "windup elapses while held" contract as Attack1). The forward lunge fires
/// <see cref="SpecialLungeLeadSeconds"/> before the thrust. Geometry is <see cref="MeleeAttackTypes.Stab"/>
/// in <see cref="MeleeAttackPath"/>. Unavailable while grappling (Q is the rope detract key) and while airborne.
/// </summary>
public partial class PlayerCombat
{
	[Property, Group( "Combat — Special attack" ), Title( "Special attack input action" )]
	public string SpecialAttackAction { get; set; } = "SpecialAttack";

	[Property, Group( "Combat — Special attack" ), Title( "Lunge distance (m)" ), Description( "Forward dash on the stab. Designer meters; converted via BodyHeight/1.8 (citizen ~40u per meter)." )]
	public float SpecialLungeMeters { get; set; } = 1f;

	[Property, Group( "Combat — Special attack" ), Title( "Lunge lead before thrust (s)" ), Description( "How long before the thrust the lunge fires (clamped to the charge time)." ), Range( 0f, 0.5f ), Step( 0.05f )]
	public float SpecialLungeLeadSeconds { get; set; } = 0.2f;

	/// <summary>A stalled charge (menu opened, item switched) aborts instead of firing this long past its due time.</summary>
	const float SpecialChargeStaleAbortSeconds = 0.3f;

	double _specialChargeStartedAtSandbox;
	double _specialChargeFireAtSandbox;
	double _specialChargeLungeAtSandbox;
	bool _specialChargeLungeDone;

	/// <summary>Owner: Q was pressed and the charge pose is counting down to the auto-fire.</summary>
	internal bool IsSpecialAttackCharging => _specialChargeFireAtSandbox > 0;

	void TickOwnerSpecialAttackInput()
	{
		TickOwnerSpecialAttackCharge();

		if ( IsSpecialAttackCharging )
			return;

		if ( string.IsNullOrWhiteSpace( SpecialAttackAction ) || !Input.Pressed( SpecialAttackAction ) )
			return;

		// Q lets rope out while grappling — never also a stab.
		if ( Components.Get<PlayerMovement>() is { GrappleAttached: true } )
			return;

		// Attack1 owns the pawn while held (aiming a swing); Q mid-hold is ignored.
		if ( Input.Down( PrimaryAttackAction ) || _primary.Down )
			return;

		if ( IsBlockPreventingAttack() || IsMeleeAttackChainBusy() )
			return;

		if ( !IsLocalPlayerGroundedForShove() )
			return;

		if ( IsActiveMainHandBroken() )
			return;

		if ( !CanAffordSpecialAttack() )
			return;

		BeginSpecialAttackCharge();
	}

	void BeginSpecialAttackCharge()
	{
		var charge = Math.Max( 0f, GetMeleeSpecialWindupSeconds() );
		var now = Time.NowDouble;
		_specialChargeStartedAtSandbox = now;
		_specialChargeFireAtSandbox = now + charge;
		_specialChargeLungeAtSandbox = now + Math.Max( 0f, charge - Math.Max( 0f, SpecialLungeLeadSeconds ) );
		_specialChargeLungeDone = false;

		// Frozen windup pose while charging — OnUpdate ticks the hold with IsSpecialAttackCharging.
		Components.Get<PlayerAnimation>()?.BeginMeleeAttackWindupHold( MeleeAttackTypes.Stab );

		LogCombatDiag( "CLIENT / OWNER", $"Special attack charge started — fires in {charge:0.###}s" );
	}

	void TickOwnerSpecialAttackCharge()
	{
		if ( !IsSpecialAttackCharging )
			return;

		var now = Time.NowDouble;

		// Getting hit (or any recovery lock) outranks the charge — abort immediately, don't fire.
		if ( IsCombatActionLocked )
		{
			AbortSpecialAttackCharge( "combat lock" );
			return;
		}

		// A stalled tick (menu opened, weapon switched away) must not fire a surprise stab later.
		if ( now > _specialChargeFireAtSandbox + SpecialChargeStaleAbortSeconds )
		{
			AbortSpecialAttackCharge( "stale" );
			return;
		}

		if ( !_specialChargeLungeDone && now >= _specialChargeLungeAtSandbox )
		{
			_specialChargeLungeDone = true;
			ApplyOwnerSpecialLunge();
		}

		if ( now < _specialChargeFireAtSandbox )
			return;

		var chargeHeld = (float)( now - _specialChargeStartedAtSandbox );
		ClearSpecialAttackChargeState();

		// Re-check the press gates — a hit reaction, guard, grapple attach, or broken weapon
		// during the charge cancels the stab instead of firing it.
		if ( Components.Get<PlayerMovement>() is { GrappleAttached: true }
		     || IsBlockPreventingAttack()
		     || IsMeleeAttackChainBusy()
		     || IsActiveMainHandBroken()
		     || !CanAffordSpecialAttack() )
		{
			Components.Get<PlayerAnimation>()?.CancelMeleeAttackWindupHold();
			LogCombatDiag( "CLIENT / OWNER", "Special attack charge aborted at fire — gates failed" );
			return;
		}

		if ( !TryBuildSpecialAttackIntent( chargeHeld, out var intent ) )
		{
			Components.Get<PlayerAnimation>()?.CancelMeleeAttackWindupHold();
			return;
		}

		// Resume the frozen charge pose into the thrust clip.
		Components.Get<PlayerAnimation>()?.ReleaseMeleeAttackWindupHold( MeleeAttackTypes.Stab, isHeavy: false );

		LogCombatDiag( "CLIENT / OWNER",
			$"Special attack (stab) seq={intent.IntentSequence} charged={chargeHeld:0.###}s cost={GetSpecialAttackStaminaCost():0.#} — dispatch now" );

		DispatchPrimaryAttackReleaseToAuthority( intent );
	}

	void AbortSpecialAttackCharge( string reason )
	{
		ClearSpecialAttackChargeState();
		Components.Get<PlayerAnimation>()?.CancelMeleeAttackWindupHold();
		LogCombatDiag( "CLIENT / OWNER", $"Special attack charge aborted ({reason})" );
	}

	void ClearSpecialAttackChargeState()
	{
		_specialChargeStartedAtSandbox = 0;
		_specialChargeFireAtSandbox = 0;
		_specialChargeLungeAtSandbox = 0;
		_specialChargeLungeDone = false;
	}

	bool CanAffordSpecialAttack()
	{
		var cost = GetSpecialAttackStaminaCost();
		if ( cost <= 1e-4f )
			return true;

		var vitals = Components.Get<PlayerVitals>();
		return vitals is not null && vitals.CanAffordStamina( cost );
	}

	/// <summary>
	/// Owner-driven lunge mid-charge: authoritative directly on host/offline, predicted on pure clients
	/// (the host re-applies it authoritatively from the attack runtime for remote-owned pawns).
	/// </summary>
	void ApplyOwnerSpecialLunge()
	{
		var meters = Math.Max( 0f, SpecialLungeMeters );
		if ( meters <= 1e-4f )
			return;

		var forward = GetViewDirectionForIntent().WithZ( 0f );
		if ( forward.LengthSquared < 1e-6f )
			forward = GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( forward.LengthSquared < 1e-6f )
			return;

		var movement = Components.Get<PlayerMovement>();
		if ( movement is null )
			return;

		if ( HasHostAuthorityForCombat() )
			movement.ServerApplyFlatDashMeters( forward.Normal, meters );
		else
			movement.PredictFlatDashMeters( forward.Normal, meters );
	}

	/// <summary>Authority lunge for pawns whose owner is NOT this machine (remote clients, entities) — the local owner path already lunged during its charge.</summary>
	internal void ServerApplySpecialLunge()
	{
		if ( IsLocalCombatDriver() )
			return;

		var meters = Math.Max( 0f, SpecialLungeMeters );
		if ( meters <= 1e-4f )
			return;

		var forward = new Angles( 0f, GetMeleeCombatBasisYaw( MeleeAttackTypes.Stab ), 0f ).ToRotation().Forward;
		Components.Get<PlayerMovement>()?.ServerApplyFlatDashMeters( forward, meters );
	}

	bool TryBuildSpecialAttackIntent( float chargeHeldSeconds, out AttackReleaseIntent intent )
	{
		intent = default;
		var view = GetViewDirectionForIntent();
		var cam = GetCameraPositionForIntent();
		if ( view.LengthSquared < 1e-8f )
			return false;

		SwingForwardWorldXzFromHorizontalView( view, out var swingXz );

		_attackIntentSequence++;
		var now = RealTime.GlobalNow;
		// The charge is the hold: the host subtracts it from the special windup (→ zero remaining) and
		// goes straight into the thrust, exactly like a held Attack1 release.
		intent = new AttackReleaseIntent
		{
			PressedGlobalSeconds = now - Math.Max( 0f, chargeHeldSeconds ),
			ReleasedGlobalSeconds = now,
			ClientCameraPressX = cam.x,
			ClientCameraPressY = cam.y,
			ClientCameraPressZ = cam.z,
			ClientCameraReleaseX = cam.x,
			ClientCameraReleaseY = cam.y,
			ClientCameraReleaseZ = cam.z,
			ViewForwardOnPress = view,
			ViewForwardOnRelease = view,
			ClientPlayerWorldPosition = WorldPosition,
			ClientPlayerWorldRotation = WorldRotation,
			IntentSequence = _attackIntentSequence,
			SwingFromX = swingXz.x,
			SwingFromY = swingXz.y,
			SwingVerticalHint = 0f,
			SwingDir = SwingDirs.Up,
			AttackType = MeleeAttackTypes.Stab,
			CombatBasisYawDegrees = GetMeleeCombatBasisYaw( MeleeAttackTypes.Stab ),
			CombatBasisPitchDegrees = GetMeleeCursorAlignedPitchDegrees( MeleeAttackTypes.Stab ),
			IsSpecial = true
		};
		return true;
	}
}
