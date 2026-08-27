using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Outcome-based combat recovery: locks attack/block/shove and plays a named citizen sequence
/// when the host sweep finishes.
/// Soft pistol = remaining swing-clip return frames + short pistol pose (no mid-clip interrupt).
/// </summary>
public partial class PlayerCombat
{
	// Attacker outcome recoveries (miss/hit/blocked/parried) live in melee_weapon_classes.json per
	// weapon class — the properties below cover the pawn-level reactions that have no weapon class:
	// shove and the defender side of blocks.

	[Property, Group( "Combat — Timings" ), Title( "Defender recovery after heavy block (s)" )]
	public float RecoveryDefenderHeavyBlockSeconds { get; set; } = 0.4f;

	[Property, Group( "Combat — Timings" ), Title( "Defender recovery after shove-vs-block (s)" )]
	public float RecoveryDefenderShoveBlockSeconds { get; set; } = 0.5f;

	[Property, Group( "Combat — Timings" ), Title( "Defender recovery after shove (unblocked) (s)" )]
	public float RecoveryDefenderShoveSeconds { get; set; } = 1.2f;

	[Property, Group( "Combat — Timings" ), Title( "Attacker shove combat lock (s)" ), Description( "After shove: lock sword/block/shove until this elapses AND the punch clip finishes. Attack1 during the kick is ignored (no buffered sword swing)." ), Range( 0.1f, 2f ), Step( 0.05f )]
	public float RecoveryShoveCombatLockSeconds { get; set; } = 0.8f;

	float _combatRecoveryRemaining;
	CombatRecoveryAnim _combatRecoveryAnim = CombatRecoveryAnim.None;
	/// <summary>Soft pistol pose waiting for the melee attack clip to finish return frames.</summary>
	bool _deferSoftCombatRecoveryPose;
	float _softPistolPoseSeconds;
	/// <summary>Sandbox time until sword/block/shove are locked (host + owner prediction).</summary>
	double _combatActionLockUntilSandbox;
	/// <summary>After a local clear, ignore stale FromHost Punch Sync so we don't replay the clip.</summary>
	double _ignoreNetworkedCombatRecoveryUntilSandbox;

	[Sync( SyncFlags.FromHost )]
	public byte NetworkedCombatRecoveryAnim { get; private set; }

	[Sync( SyncFlags.FromHost )]
	public float NetworkedCombatRecoveryRemaining { get; private set; }

	public bool IsInCombatRecovery => _combatRecoveryRemaining > 1e-4f
	                                  || NetworkedCombatRecoveryRemaining > 1e-4f
	                                  || Time.NowDouble < _combatActionLockUntilSandbox;

	/// <summary>
	/// Locks attack / block / shove / parry for the recovery pose duration.
	/// Soft recoveries include swing return frames + short pistol.
	/// </summary>
	public bool IsCombatActionLocked => IsInCombatRecovery || IsHitReactionActive;

	internal void ServerTickCombatRecovery()
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		if ( _combatRecoveryRemaining <= 0f )
		{
			if ( NetworkedCombatRecoveryRemaining > 0f || NetworkedCombatRecoveryAnim != 0 )
				ClearCombatRecoveryPresentation();
			return;
		}

		_combatRecoveryRemaining = MathF.Max( 0f, _combatRecoveryRemaining - Time.Delta );
		NetworkedCombatRecoveryRemaining = _combatRecoveryRemaining;
		if ( _combatRecoveryRemaining > 1e-4f )
			return;

		ClearCombatRecoveryPresentation();
	}

	internal void TickLocalCombatRecoveryPresentation()
	{
		// Owning clients: count down Rpc.Owner-predicted remaining (host Sync alone is unreliable on owner pawns).
		if ( !IsServerSideForMeleeAuthority() && _combatRecoveryRemaining > 1e-4f )
		{
			_combatRecoveryRemaining = MathF.Max( 0f, _combatRecoveryRemaining - Time.Delta );
			if ( _combatRecoveryRemaining <= 1e-4f )
			{
				var wasPunch = _combatRecoveryAnim == CombatRecoveryAnim.MeleePunchLeft || _shovePunchPlayedThisRecovery;
				_combatRecoveryAnim = CombatRecoveryAnim.None;
				_combatActionLockUntilSandbox = Math.Min( _combatActionLockUntilSandbox, Time.NowDouble );
				_ignoreNetworkedCombatRecoveryUntilSandbox = Time.NowDouble + 1.0f;
				_ownerSuppressNetworkedRecovery = true;
				_hardRecoveryPoseConsumed = false;
				_combatRecoveryPresentationActive = false;
				if ( wasPunch )
					LogShoveAnim( "owner local CLEAR punch (flag stays; no end replay)" );
				else
					LogShoveAnim( "owner local CLEAR recovery pose" );
				Components.Get<PlayerAnimation>()?.ExitCombatSequenceToLocomotion();
			}
		}

		if ( IsServerSideForMeleeAuthority() )
		{
			ApplyCombatRecoveryPresentation(
				_combatRecoveryAnim,
				_combatRecoveryRemaining > 1e-4f );
			return;
		}

		// Owning client: FromHost Sync for remaining/anim is unreliable on the owned pawn.
		// Presentation is Rpc.Owner + local countdown only — never re-apply a stale Networked pose.
		if ( IsOwningClientCombatPresentation() )
		{
			ApplyCombatRecoveryPresentation(
				_combatRecoveryAnim,
				_combatRecoveryRemaining > 1e-4f );
			return;
		}

		// Remote observer: host Sync is the source of truth for other pawns.
		var ignoreNet = _ownerSuppressNetworkedRecovery
		                || Time.NowDouble < _ignoreNetworkedCombatRecoveryUntilSandbox;
		var anim = ignoreNet
			? CombatRecoveryAnim.None
			: (CombatRecoveryAnim)NetworkedCombatRecoveryAnim;
		var active = !ignoreNet && NetworkedCombatRecoveryRemaining > 1e-4f;
		ApplyCombatRecoveryPresentation( anim, active );
	}

	internal void ServerBeginCombatRecovery( float durationSeconds, CombatRecoveryAnim anim, bool playPresentation = true )
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		durationSeconds = Math.Max( 0f, durationSeconds );
		if ( durationSeconds <= 1e-4f || anim == CombatRecoveryAnim.None )
		{
			ClearCombatRecoveryPresentation();
			return;
		}

		_softPistolPoseSeconds = durationSeconds;
		_deferSoftCombatRecoveryPose = false;

		// Soft recover: lock for remaining swing presentation + short buffer. Never cut mid-clip.
		if ( IsSoftPistolRecovery( anim ) )
		{
			var animLeft = Components.Get<PlayerAnimation>()?.GetMeleeAttackAnimBusyRemainingSeconds() ?? 0f;
			_deferSoftCombatRecoveryPose = animLeft > 0.05f;
			// Keep at least the configured soft seconds after the presentation window ends.
			durationSeconds = animLeft + Math.Max( 0.05f, _softPistolPoseSeconds );
		}

		if ( durationSeconds + 1e-4f < _combatRecoveryRemaining
		     && anim == _combatRecoveryAnim )
			return;

		if ( anim == CombatRecoveryAnim.MeleePunchLeft )
			_shovePunchPlayedThisRecovery = false;

		if ( anim is CombatRecoveryAnim.Rpg2hAttackMoving )
			_hardRecoveryPoseConsumed = false;

		ApplyCombatActionLock( durationSeconds );
		_combatRecoveryRemaining = durationSeconds;
		_combatRecoveryAnim = anim;
		NetworkedCombatRecoveryRemaining = durationSeconds;
		NetworkedCombatRecoveryAnim = (byte)anim;

		var seq = CombatRecoveryAnims.SequenceName( anim );
		LogMeleePhaseEnter( "recover phase",
			$"duration={durationSeconds:0.###}s anim={anim} seq={seq ?? "—"} deferSoft={_deferSoftCombatRecoveryPose} pistol={_softPistolPoseSeconds:0.###}s playPres={playPresentation}" );

		if ( playPresentation )
			ApplyCombatRecoveryPresentation( anim, true );

		// Owning clients often miss FromHost Sync on their own pawn — push lock via Rpc.Owner.
		if ( GameObject.Network is { Active: true } && Networking.IsHost )
			RpcOwnerApplyCombatRecoveryLock( durationSeconds, (byte)anim, playPresentation );
	}

	/// <summary>Owner prediction / host: lock combat until sandbox time.</summary>
	internal void ApplyCombatActionLock( float durationSeconds )
	{
		durationSeconds = Math.Max( 0f, durationSeconds );
		if ( durationSeconds <= 1e-4f )
			return;

		var until = Time.NowDouble + durationSeconds;
		if ( until > _combatActionLockUntilSandbox )
			_combatActionLockUntilSandbox = until;
	}

	/// <summary>Host: lengthen an in-flight recovery lock (e.g. shove punch clip longer than configured lock).</summary>
	void ExtendCombatActionRecovery( float durationSeconds )
	{
		durationSeconds = Math.Max( 0f, durationSeconds );
		if ( durationSeconds <= 1e-4f )
			return;

		ApplyCombatActionLock( durationSeconds );
		if ( durationSeconds > _combatRecoveryRemaining )
		{
			_combatRecoveryRemaining = durationSeconds;
			NetworkedCombatRecoveryRemaining = durationSeconds;
		}
	}

	[Rpc.Owner]
	void RpcOwnerApplyCombatRecoveryLock( float durationSeconds, byte animByte, bool playPresentation )
	{
		if ( !GameObject.IsValid() )
			return;

		// Host already applied locally in ServerBeginCombatRecovery.
		if ( Networking.IsHost )
			return;

		ApplyCombatActionLock( durationSeconds );
		_combatRecoveryRemaining = Math.Max( _combatRecoveryRemaining, durationSeconds );
		_combatRecoveryAnim = (CombatRecoveryAnim)animByte;
		_ownerSuppressNetworkedRecovery = false;
		_ignoreNetworkedCombatRecoveryUntilSandbox = 0;

		if ( _combatRecoveryAnim is CombatRecoveryAnim.Rpg2hAttackMoving )
			_hardRecoveryPoseConsumed = false;

		if ( _combatRecoveryAnim == CombatRecoveryAnim.MeleePunchLeft )
		{
			// Punch clip is Rpc.Broadcast from the host — this Owner RPC only owns the combat lock.
			var clip = Components.Get<PlayerAnimation>()?.GetActiveCombatSequenceDurationSeconds() ?? 0f;
			if ( clip > durationSeconds + 1e-4f )
				ExtendCombatActionRecovery( clip );
			CancelAttackIntentsForShoveRecovery();
			return;
		}

		if ( playPresentation )
			ApplyCombatRecoveryPresentation( _combatRecoveryAnim, true );
	}

	/// <param name="pushOwnerClear">
	/// False when the caller already runs on every peer (hit reaction broadcast) — no Rpc.Owner needed,
	/// and nesting one inside a Broadcast handler is a delivery hazard.
	/// </param>
	void ClearCombatRecoveryPresentation( bool pushOwnerClear = true )
	{
		var wasRecovering = _combatRecoveryRemaining > 1e-4f || _combatRecoveryAnim != CombatRecoveryAnim.None
		                    || Time.NowDouble < _combatActionLockUntilSandbox;
		var wasShovePunch = _combatRecoveryAnim == CombatRecoveryAnim.MeleePunchLeft || _shovePunchPlayedThisRecovery;
		_combatRecoveryRemaining = 0f;
		_combatRecoveryAnim = CombatRecoveryAnim.None;
		NetworkedCombatRecoveryRemaining = 0f;
		NetworkedCombatRecoveryAnim = 0;
		_combatActionLockUntilSandbox = 0;
		_deferSoftCombatRecoveryPose = false;
		_softPistolPoseSeconds = 0f;
		_ignoreNetworkedCombatRecoveryUntilSandbox = Time.NowDouble + 0.5f;
		_ownerSuppressNetworkedRecovery = true;
		if ( wasShovePunch )
			LogShoveAnim( "CLEAR punch / end recovery (flag stays until next shove begin)" );
		// Keep _shovePunchPlayedThisRecovery true so stale NetworkedCombatRecovery* cannot restart punch.
		// Reset only in ServerBeginCombatRecovery when starting a new MeleePunchLeft.
		_hardRecoveryPoseConsumed = false;
		_combatRecoveryPresentationActive = false;
		Components.Get<PlayerAnimation>()?.ExitCombatSequenceToLocomotion();
		if ( wasRecovering && LogMeleeAttackPhaseDebug
		     && string.Equals( _meleePhaseDebugCurrent, "recover phase", StringComparison.Ordinal ) )
			LogMeleePhaseExitIfAny( "recovery cleared" );

		// Owning clients often miss FromHost Sync zeroes — push an explicit clear so punch cannot stick.
		if ( pushOwnerClear && wasRecovering && GameObject.Network is { Active: true } && Networking.IsHost )
			RpcOwnerClearCombatRecovery();
	}

	[Rpc.Owner]
	void RpcOwnerClearCombatRecovery()
	{
		if ( !GameObject.IsValid() )
			return;

		if ( Networking.IsHost )
			return;

		_combatRecoveryRemaining = 0f;
		_combatRecoveryAnim = CombatRecoveryAnim.None;
		_combatActionLockUntilSandbox = 0;
		_deferSoftCombatRecoveryPose = false;
		_softPistolPoseSeconds = 0f;
		_hardRecoveryPoseConsumed = false;
		_combatRecoveryPresentationActive = false;
		_ignoreNetworkedCombatRecoveryUntilSandbox = Time.NowDouble + 1.0f;
		_ownerSuppressNetworkedRecovery = true;
		Components.Get<PlayerAnimation>()?.ExitCombatSequenceToLocomotion();
		LogShoveAnim( "RpcOwner CLEAR recovery pose (restore animgraph)" );
	}

	/// <summary>True on the machine that owns this pawn (not the listen-server host copy).</summary>
	bool IsOwningClientCombatPresentation()
	{
		if ( Networking.IsHost )
			return false;

		if ( GameObject.Network is { Active: true } n )
			return n.IsOwner;

		return !GameObject.IsProxy;
	}

	/// <summary>After a local/Rpc clear, ignore stale FromHost recovery until the host Sync also reads idle.</summary>
	bool _ownerSuppressNetworkedRecovery;

	/// <summary>Hard recovery already handled for this window (blocks a mid-lock restart).</summary>
	bool _hardRecoveryPoseConsumed;

	/// <summary>True while ApplyCombatRecoveryPresentation last drove an active recovery pose (edge-trigger exit).</summary>
	bool _combatRecoveryPresentationActive;

	/// <summary>Host: drop soft recovery so a new swing animgraph is not overwritten next frame.
	/// Hard kick (shove punch) lock is never cleared here — attack must wait for it.</summary>
	internal void ServerClearCombatRecoveryForNewAttack()
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		if ( _combatRecoveryAnim == CombatRecoveryAnim.MeleePunchLeft && _combatRecoveryRemaining > 1e-4f )
		{
			LogShoveAnim( "KEEP kick lock — refused clear for new sword attack" );
			return;
		}

		ClearCombatRecoveryPresentation();
	}

	static bool IsSoftPistolRecovery( CombatRecoveryAnim anim ) =>
		anim == CombatRecoveryAnim.Pistol2hStandingIdle;

	void ApplyCombatRecoveryPresentation( CombatRecoveryAnim anim, bool active )
	{
		var animation = Components.Get<PlayerAnimation>();
		if ( animation is null )
			return;

		if ( !active || anim == CombatRecoveryAnim.None )
		{
			_deferSoftCombatRecoveryPose = false;
			_hardRecoveryPoseConsumed = false;
			// Edge-trigger only — calling Exit every idle frame zeros move params and fights locomotion.
			if ( _combatRecoveryPresentationActive )
			{
				_combatRecoveryPresentationActive = false;
				animation.ExitCombatSequenceToLocomotion();
			}
			return;
		}

		_combatRecoveryPresentationActive = true;

		// Shove punch: play once, release pose when the clip ends, keep combat lock via remaining timer.
		if ( anim == CombatRecoveryAnim.MeleePunchLeft )
		{
			var seq = CombatRecoveryAnims.MeleePunchLeft;
			if ( animation.IsPlayingCombatSequence( seq ) )
			{
				if ( animation.IsCombatSequenceFinished() )
				{
					LogShoveAnim( "punch clip finished — release pose (combat lock continues)" );
					animation.ExitCombatSequenceToLocomotion();
					return;
				}

				animation.MaintainCombatSequencePose( seq, keepMeleeSwordVisible: true );
				return;
			}

			if ( _shovePunchPlayedThisRecovery )
				return;

			PlayShovePunchAnimationOnce( "ApplyCombatRecoveryPresentation fallback" );
			return;
		}

		// Attacker after being blocked/parried: animgraph only, no UseAnimGraph=false sequence
		// (those froze owning clients on the clip's last frame). Clears any leftover sequence.
		if ( anim == CombatRecoveryAnim.Rpg2hAttackMoving )
		{
			if ( !_hardRecoveryPoseConsumed )
			{
				_hardRecoveryPoseConsumed = true;
				animation.ExitCombatSequenceToLocomotion();
				LogShoveAnim( $"{anim} — animgraph only (no sequence pose)" );
			}

			return;
		}

		// Soft miss/hit: wait for swing presentation window — do NOT swap to a pistol sequence
		// (UseAnimGraph=false aborts return frames and makes subsequent swings look cut short).
		if ( IsSoftPistolRecovery( anim ) )
		{
			var busy = animation.GetMeleeAttackAnimBusyRemainingSeconds();
			if ( busy > 0.05f )
			{
				_deferSoftCombatRecoveryPose = true;
				return;
			}

			_deferSoftCombatRecoveryPose = false;
			return;
		}

		var name = CombatRecoveryAnims.SequenceName( anim );
		animation.MaintainCombatSequencePose( name, keepMeleeSwordVisible: false );
	}

	internal void NotifyServerMeleeAttackFinished() =>
		NotifyServerMeleeAttackFinished( new MeleeAttackFinishOutcome() );

	internal void NotifyServerMeleeAttackFinished( in MeleeAttackFinishOutcome outcome )
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		if ( !outcome.IsShove )
			ApplyAttackerRecoveryFromFinish( in outcome );

		// Sweep is done on the host — free the owner spam-click gate immediately (don't wait on Rpc.Owner).
		if ( IsLocalCombatDriver() )
			ClearOwnerMeleeBusyExpect( "local sweep finish" );

		if ( IsLocalCombatDriver() && !Input.Down( BlockAction ) )
			CombatState = CombatState.PostAttack;
	}

	/// <summary>Attacker outcome recovery per the class combat timings: miss / hit / blocked / parried.</summary>
	void ApplyAttackerRecoveryFromFinish( in MeleeAttackFinishOutcome outcome )
	{
		if ( outcome.IsShove )
			return;

		var t = GetMeleeWeaponTimings();

		if ( outcome.WasParried )
		{
			ServerClearInitiative();
			ServerBeginCombatRecovery( t.RecoveryParriedSeconds, CombatRecoveryAnim.Rpg2hAttackMoving );
			return;
		}

		if ( outcome.WasBlocked )
		{
			ServerClearInitiative();
			ServerBeginCombatRecovery( t.RecoveryBlockedSeconds, CombatRecoveryAnim.Rpg2hAttackMoving );
			return;
		}

		if ( outcome.AnyHit )
		{
			// Initiative arms on a clean hit — its reward is the class hit recovery (shorter than
			// miss) plus initiativeWindupSeconds on the follow-up attack.
			ServerArmInitiative();
			ServerBeginCombatRecovery( t.RecoveryHitSeconds, CombatRecoveryAnim.Pistol2hStandingIdle );
			return;
		}

		ServerBeginCombatRecovery( t.RecoveryMissSeconds, CombatRecoveryAnim.Pistol2hStandingIdle );
	}

	/// <summary>Sandbox time until the initiative windup bonus stays armed (set by a clean hit).</summary>
	double _initiativeArmedUntilSandbox;

	/// <summary>Host: initiative currently armed — the next attack winds up with the class `initiativeWindupSeconds`.</summary>
	internal bool ServerIsInitiativeArmed => Time.NowDouble < _initiativeArmedUntilSandbox;

	/// <summary>Host: arm initiative for <see cref="InitiativeWindowSeconds"/> after a clean hit.</summary>
	internal void ServerArmInitiative()
	{
		if ( InitiativeWindowSeconds <= 1e-4f )
			return;

		_initiativeArmedUntilSandbox = Time.NowDouble + Math.Max( 0f, InitiativeWindowSeconds );
	}

	internal void ServerClearInitiative() => _initiativeArmedUntilSandbox = 0;

	/// <summary>Host: consume the armed initiative for an attack start — only one follow-up benefits per hit.</summary>
	internal bool ServerConsumeInitiativeArmed()
	{
		if ( !ServerIsInitiativeArmed )
			return false;

		_initiativeArmedUntilSandbox = 0;
		return true;
	}
}
