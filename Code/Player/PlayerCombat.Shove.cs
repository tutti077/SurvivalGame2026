using System;
using Sandbox;

namespace Survival;

/// <summary>
/// F-key shove: grounded dash + punch sequence with sword still held.
/// Post-shove combat lock uses the same recovery gate as sword (<see cref="IsCombatActionLocked"/>).
/// </summary>
public partial class PlayerCombat
{
	[Property, Group( "Combat — Shove" ), Title( "Shove input action" )]
	public string ShoveAction { get; set; } = "Shove";

	[Property, Group( "Combat — Shove" ), Title( "Shove range (m)" )]
	public float ShoveRangeMeters { get; set; } = 70f;

	[Property, Group( "Combat — Shove" ), Title( "Shove damage (unblocked)" )]
	public float ShoveDamage { get; set; } = 3f;

	[Property, Group( "Combat — Shove" ), Title( "Shove radius (m)" )]
	public float ShoveHitRadiusMeters { get; set; } = 28f;

	[Property, Group( "Combat — Shove" ), Title( "Shove dash distance (m)" ), Description( "Designer meters; converted to pawn units via BodyHeight/1.8 (citizen ~40u per meter)." )]
	public float ShoveDashMeters { get; set; } = 1f;

	[Property, Group( "Combat — Shove" ), Title( "Shove stamina cost" )]
	public float ShoveStaminaCost { get; set; } = 10f;

	void TickOwnerShoveInput()
	{
		if ( !IsLocalCombatDriver() )
			return;

		if ( string.IsNullOrWhiteSpace( ShoveAction ) || !Input.Pressed( ShoveAction ) )
			return;

		if ( !IsLocalPlayerGroundedForShove() )
			return;

		// Same gate as sword: recovery / hit reaction / in-flight melee / swing window.
		if ( IsMeleeAttackChainBusy() )
			return;

		var cost = Math.Max( 0f, ShoveStaminaCost );
		if ( cost > 1e-4f )
		{
			var vitals = Components.Get<PlayerVitals>();
			if ( vitals is null || !vitals.CanAffordStamina( cost ) )
				return;
		}

		OwnerRequestShove();
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
		ApplyCombatActionLock( Math.Max( 0.05f, RecoveryShoveCombatLockSeconds ) );
		CancelAttackIntentsForShoveRecovery();
		// Local lunge — host still applies authoritative dash; without this the client never sees movement.
		PredictShoveDashLocal();
		RpcHostRequestShove();
	}

	void PredictShoveDashLocal()
	{
		var meters = Math.Max( 0f, ShoveDashMeters );
		if ( meters <= 1e-4f )
			return;

		var forward = GetViewDirectionForIntent().WithZ( 0f );
		if ( forward.LengthSquared < 1e-6f )
			forward = GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( forward.LengthSquared < 1e-6f )
			return;

		Components.Get<PlayerMovement>()?.PredictFlatDashMeters( forward.Normal, meters );
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

	void ServerTryShove()
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		if ( !IsServerPlayerGroundedForShove() )
			return;

		if ( IsCombatActionLocked || ServerHasActiveMeleeAttackAction )
			return;

		var cost = Math.Max( 0f, ShoveStaminaCost );
		if ( cost > 1e-4f )
		{
			var vitals = Components.Get<PlayerVitals>();
			if ( vitals is null || !vitals.TrySpendStamina( cost ) )
				return;
		}

		// Dash + shove together (dash first so the hit traces from the new spot).
		ServerApplyShoveDash();

		var eye = GetCameraPositionForIntent();
		var forward = GetViewDirectionForIntent();
		if ( forward.LengthSquared < 1e-4f )
			forward = GameObject.WorldRotation.Forward;
		forward = forward.Normal;

		var range = Math.Max( 10f, ShoveRangeMeters );
		var radius = Math.Max( 4f, ShoveHitRadiusMeters );
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
		{
			ServerBeginShoveAttackerRecovery();
			return;
		}

		// Trace from after the dash.
		eye = GameObject.WorldPosition + Vector3.Up * Math.Max( 32f, ServerEyeHeight * 0.5f );
		var tr = scene.Trace.Ray( eye, eye + forward * range )
			.Radius( radius )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
		{
			ServerBeginShoveAttackerRecovery();
			return;
		}

		// Find the victim by animation: being shoved is not equipment-dependent, and PlayerAnimation
		// owns the reaction window.
		var victimRoot = tr.GameObject.Root ?? tr.GameObject;
		var defenderAnimation = victimRoot.Components.Get<PlayerAnimation>( FindMode.EverythingInSelfAndDescendants );
		// Enemies come through this one instead — they carry PlayerCombat but no PlayerAnimation.
		var defenderCombat = victimRoot.Components.Get<PlayerCombat>( FindMode.EverythingInSelfAndDescendants );
		if ( victimRoot == GameObject.Root || (defenderAnimation is null && defenderCombat is null) )
		{
			ServerBeginShoveAttackerRecovery();
			return;
		}

		if ( defenderCombat is { IsAuthoritativeMeleeBlocking: true } )
		{
			defenderCombat.ServerApplyShoveVsBlock( this );
			ServerBeginShoveAttackerRecovery();
			return;
		}

		var receiver = victimRoot.Components.Get<DamageReceiver>( FindMode.EverythingInSelfAndDescendants );
		if ( receiver is not null )
			receiver.TakeDamage( Math.Max( 0f, ShoveDamage ), this );

		defenderAnimation?.ServerBeginHitReaction( RecoveryShoveVictimSeconds );
		ServerBeginShoveAttackerRecovery();
	}

	/// <summary>
	/// Punch pose + combat lock — same <see cref="IsCombatActionLocked"/> gate as post-sword recovery
	/// (blocks sword, block, and another shove until it ends).
	/// Lock lasts at least the punch clip so attack cannot start mid-kick.
	/// </summary>
	void ServerBeginShoveAttackerRecovery()
	{
		var hold = Math.Max( 0.05f, RecoveryShoveCombatLockSeconds );
		ServerBeginCombatRecovery( hold, CombatRecoveryAnim.MeleePunchLeft, playPresentation: false );
		PlayShovePunchAnimationOnce( "host ServerBeginShoveAttackerRecovery" );

		// Punch clip (~0.83s) can outlast the configured lock (0.8s) — extend so attack waits for kick end.
		var clip = Components.Get<PlayerAnimation>()?.GetActiveCombatSequenceDurationSeconds() ?? 0f;
		if ( clip > hold + 1e-4f )
		{
			LogShoveAnim( $"extend lock to punch clip {clip:0.###}s (was {hold:0.###}s)" );
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
		CancelPrimarySwingPhase();
		Components.Get<PlayerAnimation>()?.CancelMeleeAttackWindupHold();
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
		LogShoveAnim( $"PLAY punch once reason={reason}" );
		Components.Get<PlayerAnimation>()
			?.PlayCombatSequencePose( CombatRecoveryAnims.MeleePunchLeft, keepMeleeSwordVisible: true, forceRestart: true );
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

	/// <summary>Host: move the shover ~<see cref="ShoveDashMeters"/> flat-forward (collision-clamped).</summary>
	void ServerApplyShoveDash()
	{
		var meters = Math.Max( 0f, ShoveDashMeters );
		if ( meters <= 1e-4f )
			return;

		var forward = GetViewDirectionForIntent().WithZ( 0f );
		if ( forward.LengthSquared < 1e-6f )
			forward = GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( forward.LengthSquared < 1e-6f )
			return;

		forward = forward.Normal;
		var movement = Components.Get<PlayerMovement>();
		if ( movement is not null )
		{
			movement.ServerApplyFlatDashMeters( forward, meters );
			return;
		}

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		var origin = GameObject.WorldPosition + Vector3.Up * 36f;
		var tr = scene.Trace.Ray( origin, origin + forward * meters )
			.Radius( 16f )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();
		var delta = tr.Hit
			? forward * Math.Max( 0f, (tr.HitPosition - origin).WithZ( 0f ).Length - 8f )
			: forward * meters;
		GameObject.WorldPosition += delta.WithZ( 0f );
	}

	/// <summary>Host: shove connected against an active guard.</summary>
	internal void ServerApplyShoveVsBlock( Component attacker )
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		ConsumeAuthoritativeMeleeBlock( attackWasHeavy: false, wasPerfectParry: false );
		ServerBeginHitReaction( RecoveryBlockerShoveBlockSeconds );
		Log.Info( $"[MeleeBlock] shove-vs-block {GameObject.Name} from {attacker?.GameObject?.Name}" );
	}
}
