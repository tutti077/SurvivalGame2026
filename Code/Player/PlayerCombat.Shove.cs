using System;
using Sandbox;

namespace Survival;

/// <summary>
/// F-key punch (shield bash with a shield out): whacks whoever stands within <see cref="ShoveRangeMeters"/> in front —
/// no lunge (Mark 2026-11-05), shorter reach than the sword, so you have to be close. It breaks a guard but never
/// cancels an attack already started (Mark 2026-11-05: hits never break attacks). Post-punch combat lock uses the same recovery gate as the sword (<see cref="IsCombatActionLocked"/>).
/// </summary>
public partial class PlayerCombat
{
	[Property, Group( "Combat — Shove" ), Title( "Shove input action" )]
	public string ShoveAction { get; set; } = "Shove";

	/// <summary>Reach of the punch from the body, in meters (shorter than the sword's ~1.9 m — get close).</summary>
	[Property, Group( "Combat — Shove" ), Title( "Punch reach (m)" ), Range( 0.3f, 3f ), Step( 0.05f )]
	public float ShoveRangeMeters { get; set; } = 1.2f;

	[Property, Group( "Combat — Shove" ), Title( "Shove damage (unblocked)" )]
	public float ShoveDamage { get; set; } = 3f;

	/// <summary>Thickness of the punch trace, in meters.</summary>
	[Property, Group( "Combat — Shove" ), Title( "Punch radius (m)" ), Range( 0.05f, 1f ), Step( 0.05f )]
	public float ShoveHitRadiusMeters { get; set; } = 0.3f;

	[Property, Group( "Combat — Shove" ), Title( "Shove stamina cost" )]
	public float ShoveStaminaCost { get; set; } = 10f;

	void TickOwnerShoveInput()
	{
		if ( !IsLocalCombatDriver() )
			return;

		if ( string.IsNullOrWhiteSpace( ShoveAction ) || !Input.Pressed( ShoveAction ) )
			return;

		OwnerTryShove();
	}

	/// <summary>Owner: fire the shove now if the grounded / chain / stamina gates allow it.</summary>
	public bool OwnerTryShove()
	{
		if ( !IsLocalCombatDriver() )
			return false;

		if ( !IsLocalPlayerGroundedForShove() )
			return false;

		// Same gate as sword: recovery / hit reaction / in-flight melee / swing window.
		if ( IsMeleeAttackChainBusy() )
			return false;

		// Punch cooldown (the host enforces the same).
		if ( Time.NowDouble - _ownerLastPunchAt < Math.Max( 0f, PunchCooldownSeconds ) )
			return false;

		// F belongs to the hoe while it is out (its mode swap). Otherwise F always throws the punch, at someone or
		// at nothing (Mark 2026-11-05: "add the punch back in, should be F").
		var equipment = Components.Get<PlayerEquipment>();
		if ( equipment is not null && equipment.MainHandHasAction( EquippedItemActions.Till ) )
			return false;

		var cost = Math.Max( 0f, ShoveStaminaCost );
		if ( cost > 1e-4f )
		{
			var vitals = Components.Get<PlayerVitals>();
			if ( vitals is null || !vitals.CanAffordStamina( cost ) )
				return false;
		}

		BeginShoveFacingLock();
		_ownerLastPunchAt = Time.NowDouble;
		OwnerRequestShove();
		return true;
	}

	bool IsLocalPlayerGroundedForShove()
	{
		var controller = Components.Get<PlayerController>();
		return controller is not null && controller.IsOnGround;
	}

	bool IsServerPlayerGroundedForShove()
	{
		var controller = Components.Get<PlayerController>();
		return controller is not null && controller.IsOnGround;
	}

	void OwnerRequestShove()
	{
		if ( HasHostAuthorityForCombat() )
		{
			ServerTryShove();
			return;
		}

		// Predict lock so spam F / Attack1 can't slip through before Rpc.Owner recovery arrives.
		ApplyCombatActionLock( Math.Max( 0.05f, Math.Max( 0f, PunchWindupSeconds ) + RecoveryShoveCombatLockSeconds ) );
		BeginPunchActionLock();
		CancelAttackIntentsForShoveRecovery();
		RpcHostRequestShove();
	}

	bool HasHostAuthorityForCombat() =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	[Rpc.Host]
	void RpcHostRequestShove()
	{
		if ( !Networking.IsHost )
			return;

		if ( Rpc.Caller is { } caller
		     && GameObject.Network is { Active: true, Owner: { } owner }
		     && caller.Id != owner.Id )
			return;

		ServerTryShove();
	}

	/// <summary>Click → the punch lands (readable, still quicker than the 0.6 s one-handed windup). The lockout runs this plus <see cref="RecoveryShoveCombatLockSeconds"/>.</summary>
	[Property, Group( "Combat — Shove" ), Title( "Punch windup (s)" ), Range( 0f, 1f ), Step( 0.01f )]
	public float PunchWindupSeconds { get; set; } = 0.3f;

	/// <summary>
	/// No second punch for this long after one (Mark 2026-11-05: punches could stagger a bot forever). The exchange it
	/// protects: punch lands → puncher free after <see cref="RecoveryShoveCombatLockSeconds"/> (0.15) and its light swing
	/// hits 0.4 s later (0.55) → the punched pawn is free after <see cref="RecoveryDefenderShoveSeconds"/> (0.4) and a held
	/// guard is up by 0.45 — the punch buys initiative, not a free hit, and never a chain.
	/// </summary>
	[Property, Group( "Combat — Shove" ), Title( "Punch cooldown (s)" ), Range( 0f, 5f ), Step( 0.05f )]
	public float PunchCooldownSeconds { get; set; } = 1.5f;

	double _hostLastPunchAt = double.NegativeInfinity;
	double _ownerLastPunchAt = double.NegativeInfinity;

	/// <summary>Seconds until this pawn may punch again (host view; the owner predicts the same from its own clock).</summary>
	public float PunchCooldownRemaining =>
		Math.Max( 0f, (float)(_hostLastPunchAt + Math.Max( 0f, PunchCooldownSeconds ) - Time.NowDouble) );

	/// <summary>Host: a punch thrown and not landed yet (lands at <see cref="_pendingPunchAt"/>).</summary>
	bool _pendingPunch;
	double _pendingPunchAt;
	Vector3 _pendingPunchForward;

	/// <summary>Host: start a punch — stamina, the punch pose and the lockout now, the hit <see cref="PunchWindupSeconds"/> later.</summary>
	void ServerTryShove() => ServerStartPunch( requireGrounded: true );

	/// <summary>Automated combat test (<c>combat_test</c>): the host punch path without the on-the-ground rule (the test pawns float).</summary>
	internal bool TestServerPunch() => ServerStartPunch( requireGrounded: false );

	bool ServerStartPunch( bool requireGrounded )
	{
		if ( !IsServerSideForMeleeAuthority() )
			return false;

		if ( requireGrounded && !IsServerPlayerGroundedForShove() )
			return false;

		if ( _pendingPunch || IsCombatActionLocked || ServerHasActiveMeleeAttackAction || PunchCooldownRemaining > 0f )
			return false;

		var cost = Math.Max( 0f, ShoveStaminaCost );
		if ( cost > 1e-4f )
		{
			var vitals = Components.Get<PlayerVitals>();
			if ( vitals is null || !vitals.TrySpendStamina( cost ) )
				return false;
		}

		// A flat punch at chest height along the facing at the click.
		var forward = GetViewDirectionForIntent().WithZ( 0f );
		if ( forward.LengthSquared < 1e-4f )
			forward = GameObject.WorldRotation.Forward.WithZ( 0f );

		_pendingPunchForward = forward.Normal;
		_pendingPunchAt = Time.NowDouble + Math.Max( 0f, PunchWindupSeconds );
		_pendingPunch = true;
		_hostLastPunchAt = Time.NowDouble;
		ServerBeginShoveAttackerRecovery();
		ServerTickPendingPunch();
		return true;
	}

	/// <summary>Host, every tick (with the swing runtime): land the pending punch when its windup has run.</summary>
	internal void ServerTickPendingPunch()
	{
		if ( !_pendingPunch || Time.NowDouble < _pendingPunchAt )
			return;

		_pendingPunch = false;
		ServerLandPunch( _pendingPunchForward );
	}

	/// <summary>
	/// Host: the punch connects — whoever stands within <see cref="ShoveRangeMeters"/> (reach and radius in meters, pawn
	/// units via BodyHeight/1.8) takes it. It breaks a guard; an attack they already started plays on.
	/// </summary>
	void ServerLandPunch( Vector3 forward )
	{
		var unitsPerMeter = GetPawnUnitsPerMeter();
		var range = Math.Max( 0.3f, ShoveRangeMeters ) * unitsPerMeter;
		var radius = Math.Max( 0.05f, ShoveHitRadiusMeters ) * unitsPerMeter;
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return;

		var eye = GameObject.WorldPosition + Vector3.Up * Math.Max( 32f, ServerEyeHeight * 0.5f );
		var tr = scene.Trace.Ray( eye, eye + forward * range )
			.Radius( radius )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
			return;

		// Find the victim by animation: being punched is not equipment-dependent, and PlayerAnimation owns the
		// reaction window. Enemies come through PlayerCombat instead (no PlayerAnimation).
		var victimRoot = tr.GameObject.Root ?? tr.GameObject;
		var defenderAnimation = victimRoot.Components.Get<PlayerAnimation>( FindMode.EverythingInSelfAndDescendants );
		var defenderCombat = victimRoot.Components.Get<PlayerCombat>( FindMode.EverythingInSelfAndDescendants );
		if ( victimRoot == GameObject.Root || (defenderAnimation is null && defenderCombat is null) )
			return;

		if ( defenderCombat is { IsAuthoritativeMeleeBlocking: true } )
		{
			defenderCombat.ServerApplyShoveVsBlock( this );
			return;
		}

		var receiver = victimRoot.Components.Get<DamageReceiver>( FindMode.EverythingInSelfAndDescendants );
		if ( receiver is not null )
		{
			using ( PlaytestLog.Kind( "punch" ) )
				receiver.TakeDamage( Math.Max( 0f, ShoveDamage ), this );
		}

		defenderAnimation?.ServerBeginHitReaction( RecoveryDefenderShoveSeconds );
	}

	/// <summary>
	/// Punch pose + combat lock. The lock (no sword, block or punch) is <see cref="RecoveryShoveCombatLockSeconds"/>
	/// (0.15 s, Mark 2026-11-05); the recovery itself runs as long as the punch clip so the pose plays out, and a
	/// swing or a raised guard after the lock clears what is left of it.
	/// </summary>
	void ServerBeginShoveAttackerRecovery()
	{
		var hold = Math.Max( 0.05f, Math.Max( 0f, PunchWindupSeconds ) + RecoveryShoveCombatLockSeconds );
		ServerBeginCombatRecovery( hold, CombatRecoveryAnim.MeleePunchLeft, playPresentation: false );
		BeginPunchActionLock();
		PlayShovePunchAnimationOnce( "host ServerBeginShoveAttackerRecovery" );

		// The pose runs the whole punch clip (~0.83 s); IsInCombatRecovery only locks for the punch lock above.
		var clip = Components.Get<PlayerAnimation>()?.GetActiveCombatSequenceDurationSeconds() ?? 0f;
		if ( clip > hold + 1e-4f )
		{
			LogShoveAnim( $"punch pose runs the clip {clip:0.###}s (actions locked {hold:0.###}s)" );
			ExtendCombatActionRecovery( clip );
		}

		CancelAttackIntentsForShoveRecovery();

		// Remotes never tick Sync-driven recovery on the host pawn — broadcast the punch like hit reaction / swing.
		if ( GameObject.Network is { Active: true } && Networking.IsHost )
			RpcBroadcastShovePunch();
	}

	/// <summary>Host→all peers: play the shove punch on every machine that isn't the host (host already played locally).</summary>
	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable | NetFlags.SendImmediate )]
	void RpcBroadcastShovePunch()
	{
		if ( !GameObject.IsValid() || Networking.IsHost )
			return;

		_shovePunchPlayedThisRecovery = false;
		PlayShovePunchAnimationOnce( "RpcBroadcastShovePunch" );
	}

	/// <summary>Owner + host: drop in-flight sword intents so Attack1 during kick does not fire at unlock.</summary>
	void CancelAttackIntentsForShoveRecovery()
	{
		ClearQueuedAttackPress();
		Components.Get<PlayerAnimation>()?.CancelMeleeSwingSnap();
		LogShoveAnim( "cancel in-flight attack intents (kick recovery owns the window)" );
	}

	bool _shovePunchPlayedThisRecovery;

	/// <summary>One-shot shove punch for this recovery — forceRestart so spam F always gets a fresh clip.</summary>
	void PlayShovePunchAnimationOnce( string reason )
	{
		if ( _shovePunchPlayedThisRecovery )
		{
			LogShoveAnim( $"SKIP punch (already played) reason={reason}" );
			return;
		}

		_shovePunchPlayedThisRecovery = true;
		var animation = Components.Get<PlayerAnimation>();

		// Shield out (as this peer sees it): F is a shield bash — the shield drives forward instead of the left-hand punch.
		if ( animation is { HasShieldShown: true } )
		{
			LogShoveAnim( $"PLAY shield bash reason={reason}" );
			animation.PlayShieldBash();
			return;
		}

		LogShoveAnim( $"PLAY punch once reason={reason}" );
		animation?.PlayCombatSequencePose( CombatRecoveryAnims.MeleePunchLeft, keepMeleeSwordVisible: true, forceRestart: true );
	}

	void LogShoveAnim( string detail )
	{
		if ( !LogMeleeAttackPhaseDebug )
			return;

		Log.Info( $"[ShoveAnim] t={Time.Now:0.000} {detail}" );
	}

	internal void LogShoveAnimIfPunch( string sequenceName, string detail )
	{
		if ( !string.Equals( sequenceName, CombatRecoveryAnims.MeleePunchLeft, StringComparison.OrdinalIgnoreCase ) )
			return;

		LogShoveAnim( detail );
	}

	/// <summary>Host: shove connected against an active guard.</summary>
	internal void ServerApplyShoveVsBlock( Component attacker )
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		ConsumeAuthoritativeMeleeBlock( wasPerfectParry: false );
		ServerBeginHitReaction( RecoveryDefenderShoveBlockSeconds );
		Log.Info( $"[MeleeBlock] shove-vs-block {GameObject.Name} from {attacker?.GameObject?.Name}" );
	}
}
