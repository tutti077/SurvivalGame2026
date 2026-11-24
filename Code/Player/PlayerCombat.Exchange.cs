using System;
using Sandbox;

namespace Survival;

/// <summary>What the pawn's legs are doing for <see cref="PlayerMovement.GetCombatFootworkScale"/>.</summary>
public enum CombatFootworkState : byte
{
	None = 0,
	LightWindup = 1,
	LightRelease = 2,
	Recovery = 3,
	HeavyWindup = 4,
	HeavyRelease = 5,
	Blocking = 6,
	Staggered = 7,
}

/// <summary>Swing phase as one machine sees it (the host runtime, the client replay or the owner's prediction).</summary>
public enum MeleeSwingPhase : byte
{
	None = 0,
	Windup = 1,
	Release = 2,
	Recovery = 3,
}

/// <summary>
/// Directional exchange rules that do not depend on the weapon (Mark 2026-11-14, Last Oasis spec v0.1). Weapon timings
/// (windup / combo windup / release / recovery, heavy, staggers, counter windup) are weapon class rows
/// (<see cref="MeleeWeaponClassCatalog"/>); this partial owns the rest:
/// <list type="bullet">
/// <item><b>Chaining</b> — a click from the last <see cref="ChainInputWindowSeconds"/> of a release through its recovery
/// queues the next swing, which winds up the moment the recovery (the follow-through) ends, in the combo windup (25 %
/// shorter, any direction - Mark 2026-11-14: "Attack 2 and 3 in a combo have faster windup by 25%"); a chain plays every
/// follow-through (Last Oasis footage, ~1.07 s strike to strike; the old chain skipped the recovery and ran 0.33 s). At most <see cref="ComboMaxHits"/> swings chain; the last one
/// recovers for the class combo-end recovery. Any other click is kept for <see cref="AttackInputBufferSeconds"/> (one
/// stored press, the newest wins).</item>
/// <item><b>Feint</b> — the guard button during the first <see cref="FeintCutoffFraction"/> of a windup - or at any point
/// before the swing turns heavy while the attack button is still held (Mark 2026-11-16) - cancels the swing for
/// <see cref="FeintStaminaCost"/> and locks attacks for <see cref="FeintRecoverySeconds"/>; a held guard button then raises
/// the guard. Later in a tapped windup, in the release and in the recovery the guard waits for the swing to end.</item>
/// <item><b>Heavy</b> — still held when the light windup ends, a swing turns heavy and hangs until the button comes up,
/// never by itself (Mark 2026-11-16); past <see cref="HeavyHoldDrainAfterSeconds"/> the hold drains
/// <see cref="HeavyHoldDrainPerSecond"/> and an empty bar lets it go. <b>Morph</b>: the guard button on a held heavy starts
/// the morph - the cocked weapon follows the attack cursor to any side and the release strikes from there
/// (PlayerCombat.HeavyMorph.cs).</item>
/// <item><b>Priority</b> — blocking a light opens <see cref="CounterWindowSeconds"/> in which the blocker's first swing
/// winds up in the class counter windup (after a <see cref="LightBlockReactionSeconds"/> block reaction). Blocking a
/// heavy staggers the blocker instead.</item>
/// <item><b>Chamber</b> — a swing whose release starts within ±<see cref="ChamberToleranceSeconds"/> of an incoming blade
/// reaching the pawn, mirroring that attack (overhead vs overhead, left vs right), intercepts it: no damage, no stagger, the incoming
/// swing stops as if blocked and the chambering swing carries on (MeleeBlockResolution).</item>
/// <item><b>Stagger</b> — a landed hit staggers and interrupts the victim's swing; hard staggers (heavy hit, blocked heavy)
/// do not repeat within <see cref="HardStaggerProtectionSeconds"/> (the damage still lands).</item>
/// <item><b>Turning</b> — the swing basis turns with the body, which follows the look at most
/// <see cref="PlayerMovement.BodyTurnDegreesPerSecond"/> (the camera itself is free); a lateral blade turned with the body
/// hits harder, against it softer (<see cref="TurnDamageScale"/>).</item>
/// </list>
/// The owner predicts its own swing timeline (instant feedback); the host re-runs the same rules on its own clock and
/// is the authority (a click the host cannot honour is rejected and the owner drops the predicted swing).
/// </summary>
public partial class PlayerCombat
{
	[Property, Group( "Combat — Exchange" ), Title( "Combo max hits" ), Range( 1, 6 )]
	public int ComboMaxHits { get; set; } = 3;

	[Property, Group( "Combat — Exchange" ), Title( "Chain input window (s)" ), Description( "A click from this long before the release ends, through the recovery, chains the next swing (it winds up when the recovery ends)." ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float ChainInputWindowSeconds { get; set; } = 0.15f;

	[Property, Group( "Combat — Exchange" ), Title( "Attack input buffer (s)" ), Description( "A click that cannot fire yet is kept this long (one stored press, the newest wins)." ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float AttackInputBufferSeconds { get; set; } = 0.15f;

	[Property, Group( "Combat — Exchange" ), Title( "Heavy hold drain after (s)" ), Range( 0f, 2f ), Step( 0.05f )]
	public float HeavyHoldDrainAfterSeconds { get; set; } = 1f;

	[Property, Group( "Combat — Exchange" ), Title( "Heavy hold drain (/s)" ), Range( 0f, 30f ), Step( 0.5f )]
	public float HeavyHoldDrainPerSecond { get; set; } = 5f;

	[Property, Group( "Combat — Exchange" ), Title( "Feint cutoff (share of windup)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float FeintCutoffFraction { get; set; } = 0.75f;

	[Property, Group( "Combat — Exchange" ), Title( "Feint recovery (s)" ), Range( 0f, 1f ), Step( 0.01f )]
	public float FeintRecoverySeconds { get; set; } = 0.15f;

	[Property, Group( "Combat — Exchange" ), Title( "Feint stamina" ), Range( 0f, 50f ), Step( 1f )]
	public float FeintStaminaCost { get; set; } = 5f;

	[Property, Group( "Combat — Exchange" ), Title( "Counter window (s)" ), Description( "After blocking a light, the first swing started in this window winds up in the class counter windup." ), Range( 0f, 2f ), Step( 0.05f )]
	public float CounterWindowSeconds { get; set; } = 0.5f;

	[Property, Group( "Combat — Exchange" ), Title( "Light block reaction (s)" ), Description( "Blocking a light locks the blocker's attacks this long (the guard stays up)." ), Range( 0f, 1f ), Step( 0.01f )]
	public float LightBlockReactionSeconds { get; set; } = 0.1f;

	[Property, Group( "Combat — Exchange" ), Title( "Chamber timing tolerance (±s)" ), Range( 0f, 0.3f ), Step( 0.005f )]
	public float ChamberToleranceSeconds { get; set; } = 0.075f;

	[Property, Group( "Combat — Exchange" ), Title( "Hard stagger protection (s)" ), Description( "A second hard stagger (heavy hit / blocked heavy) inside this window does not stagger; damage still lands." ), Range( 0f, 2f ), Step( 0.05f )]
	public float HardStaggerProtectionSeconds { get; set; } = 0.5f;

	/// <summary>
	/// Turn damage (Mark 2026-11-17: "if you rotate while hitting the sword is going faster than if you were standing
	/// still so it should add some damage buff. Maybe 1.25 is the max buff? and .75 is the max debuff?"). A lateral
	/// blade's speed is its arc plus the body's turn: turning the way the blade travels adds to it, turning against it
	/// takes away. The scale is linear in the body's average turn speed over the release so far, as a share of the body
	/// turn cap (<see cref="PlayerMovement.BodyTurnDegreesPerSecond"/>): the full cap with the blade = the max, the full cap
	/// against it = the min, 40 % of the cap with it = 1.10. An overhead chop travels down, not sideways - no change.
	/// </summary>
	[Property, Group( "Combat — Exchange" ), Title( "Turn damage: max (x)" ), Description( "Damage of a lateral swing whose body turns the way the blade travels at the full body turn rate." ), Range( 1f, 2f ), Step( 0.05f )]
	public float TurnDamageMaxScale { get; set; } = 1.25f;

	[Property, Group( "Combat — Exchange" ), Title( "Turn damage: min (x)" ), Description( "Damage of a lateral swing whose body turns against the blade at the full body turn rate." ), Range( 0.25f, 1f ), Step( 0.05f )]
	public float TurnDamageMinScale { get; set; } = 0.75f;

	/// <summary>Host only: extra time allowed for the round trip when matching an owner's chained / buffered click and feint to its own clock.</summary>
	[Property, Group( "Combat — Networking" ), Title( "Host latency slack (s)" ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float HostLatencySlackSeconds { get; set; } = 0.15f;

	/// <summary>
	/// Host only: a remote owner's swing or shot reaches the host one trip late. Up to this much of that trip (half the
	/// connection's measured ping, never a client's own claim) is given back: the swing's windup is that much shorter on
	/// the host, the arrow flies that much first. Without it the host's own swing always landed first and interrupted
	/// the client's (Mark 2026-11-17: "when we attack each other only i can land the hit"). 0 = off.
	/// </summary>
	[Property, Group( "Combat — Networking" ), Title( "Owner latency give-back, max (s)" ), Range( 0f, 0.3f ), Step( 0.01f )]
	public float OwnerLatencyCompensationMaxSeconds { get; set; } = 0.12f;

	/// <summary>Host: one-way latency of the client that owns this pawn, capped; 0 for the host's own pawn, scavs and offline.</summary>
	internal float HostOwnerLatencySeconds()
	{
		if ( !Networking.IsActive || !Networking.IsHost || IsLocalCombatDriver() )
			return 0f;
		if ( GameObject.Network is not { Active: true, Owner: { } owner } || owner == Connection.Local )
			return 0f;

		return Math.Clamp( owner.Ping * 0.0005f, 0f, Math.Max( 0f, OwnerLatencyCompensationMaxSeconds ) );
	}

	// ------------------------------------------------------------------ shared state (host + predicting owner)

	/// <summary>Attacks (not the guard) are locked until then: feint recovery, the light block reaction, a blocked heavy's stagger.</summary>
	double _attackLockUntilSandbox;

	/// <summary>The legs read "staggered" until then (a blocked heavy keeps the guard but staggers the footwork).</summary>
	double _staggerFootworkUntilSandbox;

	/// <summary>Blocked a light: the first swing started before this winds up in the counter windup.</summary>
	double _counterWindowUntilSandbox;

	/// <summary>Host: the last hard stagger this pawn took (protection window).</summary>
	double _lastHardStaggerAtSandbox = double.NegativeInfinity;

	/// <summary>Attacks locked: recovery / hit reaction, the attack-only lock, or an exhausted stamina bar.</summary>
	public bool IsAttackLocked =>
		IsCombatActionLocked || Time.NowDouble < _attackLockUntilSandbox || IsStaminaExhausted;

	bool IsStaminaExhausted =>
		Components.Get<PlayerVitals>() is { IsExhausted: true } || HostEntityVitals is { IsStaminaExhausted: true };

	internal void ApplyAttackLock( float seconds )
	{
		if ( seconds <= 1e-4f )
			return;
		_attackLockUntilSandbox = Math.Max( _attackLockUntilSandbox, Time.NowDouble + seconds );
	}

	// ------------------------------------------------------------------ owner prediction

	// Owner: the swing this client fired — windup, combo step and direction, and when its release starts (heavy: on the
	// button release). Drives the chain / buffer / feint decisions, footwork and the guard gate without a round trip.
	float _ownerSwingWindup;
	byte _ownerSwingStep;
	double _ownerStrikeAt;
	bool _queuedIsChain;

	MeleeWeaponTimings OwnerTimings => GetMeleeWeaponTimings();

	double OwnerReleaseEndAt => _ownerStrikeAt + OwnerTimings.ActiveFor( _ownerSwingHeavy );

	double OwnerRecoveryEndAt => OwnerReleaseEndAt + RecoveryFor( OwnerTimings, _ownerSwingHeavy, _ownerSwingStep );

	/// <summary>Recovery after an unchained swing: the combo end when it was the last chainable step, else light / heavy.</summary>
	internal float RecoveryFor( in MeleeWeaponTimings t, bool heavy, int comboStep )
	{
		var normal = heavy ? t.HeavyRecoverySeconds : t.RecoverySeconds;
		return comboStep >= Math.Max( 1, ComboMaxHits ) ? Math.Max( normal, t.ComboEndRecoverySeconds ) : normal;
	}

	/// <summary>Where the owner's predicted swing is now (None when nothing is running).</summary>
	internal MeleeSwingPhase OwnerSwingPhaseNow()
	{
		if ( !_ownerSwingTracking )
			return MeleeSwingPhase.None;

		var now = Time.NowDouble;
		if ( _ownerSwingHolding && _ownerSwingHeavy )
			return MeleeSwingPhase.Windup;
		if ( now < _ownerStrikeAt )
			return MeleeSwingPhase.Windup;
		if ( now < OwnerReleaseEndAt )
			return MeleeSwingPhase.Release;
		if ( now < OwnerRecoveryEndAt )
			return MeleeSwingPhase.Recovery;

		_ownerSwingTracking = false;
		return MeleeSwingPhase.None;
	}

	/// <summary>Owner: the windup the next swing gets — chained (the combo windup), counter, or the class windup.</summary>
	float OwnerWindupFor( bool chained )
	{
		var t = OwnerTimings;
		if ( chained )
			return t.ComboWindupSeconds;
		return Time.NowDouble < _counterWindowUntilSandbox ? t.CounterWindupSeconds : t.WindupSeconds;
	}

	/// <summary>Owner, a click: fire now when nothing runs; else chain (end of the release or the recovery) or keep it in the buffer.</summary>
	void OwnerHandleAttackClick( byte dir )
	{
		var phase = OwnerSwingPhaseNow();
		if ( phase == MeleeSwingPhase.None && !IsAttackLocked )
		{
			TryFirePrimaryAttack( dir, "click", chained: false );
			return;
		}

		var now = Time.NowDouble;
		var chain = phase is MeleeSwingPhase.Windup or MeleeSwingPhase.Release or MeleeSwingPhase.Recovery
		            && !_ownerSwingHolding
		            && _ownerSwingStep < Math.Max( 1, ComboMaxHits )
		            && now >= OwnerReleaseEndAt - Math.Max( 0f, ChainInputWindowSeconds );
		QueueAttackPress( dir, chain );
		LogCombatDiag( "CLIENT / OWNER", $"Attack click {(chain ? "chained" : "buffered")} — {phase}" );
	}

	/// <summary>Owner, every frame: fire the stored click when its moment comes (chain at the recovery end, else when free).</summary>
	void MaybeFireQueuedAttackPress()
	{
		if ( !_hasQueuedAttackPress )
			return;

		var now = Time.NowDouble;
		var phase = OwnerSwingPhaseNow();

		if ( _queuedIsChain )
		{
			if ( phase != MeleeSwingPhase.None )
				return;

			// The recovery just ran out: chain into the next windup, unless the host cut the swing or put us in a lockout
			// (blocked / chambered / staggered) — then it is an ordinary buffered press.
			var sinceRecoveryEnd = now - OwnerRecoveryEndAt;
			if ( !IsAttackLocked && sinceRecoveryEnd >= -0.02 && sinceRecoveryEnd <= 0.1 )
			{
				_hasQueuedAttackPress = false;
				TryFirePrimaryAttack( _queuedAttackSwingDir, "chained click", chained: true );
				return;
			}

			_queuedIsChain = false;
		}

		if ( now - _queuedAttackPressedAtSandbox > Math.Max( 0f, AttackInputBufferSeconds ) )
		{
			_hasQueuedAttackPress = false;
			return;
		}

		if ( phase != MeleeSwingPhase.None || IsAttackLocked || IsBlockPreventingAttack() )
			return;

		_hasQueuedAttackPress = false;
		TryFirePrimaryAttack( _queuedAttackSwingDir, "buffered click", chained: false );
	}

	void QueueAttackPress( byte swingDir, bool chain )
	{
		_hasQueuedAttackPress = true;
		_queuedAttackPressedAtSandbox = Time.NowDouble;
		_queuedAttackSwingDir = swingDir;
		_queuedIsChain = chain;
	}

	void ClearQueuedAttackPress() => _hasQueuedAttackPress = false;

	/// <summary>Owner: start the predicted timeline for a swing that just went out.</summary>
	void BeginOwnerSwingTimeline( ushort seq, float windup, bool chained )
	{
		_ownerSwingStep = (byte)(chained ? Math.Min( 250, _ownerSwingStep + 1 ) : 1);
		_ownerSwingWindup = windup;
		_ownerSwingTracking = true;
		_ownerSwingSeq = seq;
		_ownerSwingClickAt = Time.NowDouble;
		_ownerStrikeAt = _ownerSwingClickAt + windup;
		_ownerSwingHeavy = false;
		_ownerHeavyFlashPlayed = false;
		if ( !chained )
			_counterWindowUntilSandbox = 0;
	}

	/// <summary>Owner: drop the predicted swing (host reject / cancel, stagger, feint).</summary>
	void ClearOwnerSwingTracking()
	{
		_ownerSwingTracking = false;
		_ownerSwingHolding = false;
	}

	/// <summary>Owner: the guard button went down while a swing runs — a feint in the early windup, else the guard waits.</summary>
	bool OwnerTryFeint()
	{
		// A heavy is never feinted: the guard button starts its morph (OwnerTryBeginHeavyMorph runs first).
		if ( OwnerSwingPhaseNow() != MeleeSwingPhase.Windup || _ownerSwingHeavy )
			return false;

		// Still holding the attack button before it turned heavy: the guard drops the swing at any point (Mark 2026-11-16).
		if ( !_ownerSwingHolding
		     && Time.NowDouble - _ownerSwingClickAt >= Math.Clamp( FeintCutoffFraction, 0f, 1f ) * _ownerSwingWindup )
			return false;

		var vitals = Components.Get<PlayerVitals>();
		if ( vitals is not null && !vitals.CanAffordStamina( FeintStaminaCost ) )
			return false;

		var seq = _ownerSwingSeq;
		ClearOwnerSwingTracking();
		ClearQueuedAttackPress();
		ApplyAttackLock( FeintRecoverySeconds );
		var animation = Components.Get<PlayerAnimation>();
		animation?.CancelMeleeSwingSnap();

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			RpcHostFeint( seq );
		else
			HostTryFeint( seq );

		// The button counts as let go for this swing from here on: should the host refuse the feint (its clock already
		// past the cutoff), the swing still strikes instead of hanging as a heavy nobody holds.
		SubmitPrimaryAttackReleased( seq );
		LogCombatDiag( "CLIENT / OWNER", $"feint seq={seq}" );
		return true;
	}

	/// <summary>Owner: the predicted swing (any phase) still owns the arm — no guard until it ends (a feint aside).</summary>
	internal bool IsOwnerSwingHoldingGuard() => IsLocalCombatDriver() && OwnerSwingPhaseNow() != MeleeSwingPhase.None;

	// ------------------------------------------------------------------ host

	/// <summary>Host: the combo step of the swing in flight / just finished (0 = none), and its direction.</summary>
	byte _hostComboStep;

	/// <summary>Host: a player click that arrived while it could not fire yet (one stored press, the newest wins).</summary>
	bool _hostHasPending;
	AttackReleaseIntent _hostPendingIntent;
	double _hostPendingArrivedAt;
	string _hostPendingNote;

	/// <summary>Host: the chained swing to start the moment the current runtime hands over.</summary>
	bool _hostChainNext;

	/// <summary>Host: when the last swing's recovery ended (a chained click delayed by the round trip may still take it).</summary>
	double _hostLastRecoveryEndAt = double.NegativeInfinity;

	/// <summary>
	/// Host, a player's validated click (<see cref="CombatAuthority.ValidateAndApplyPrimaryMelee"/>): start it when the pawn
	/// is free, otherwise keep it as the single pending press (a chain claimed by the owner, or latency on a buffered one).
	/// </summary>
	internal AttackReleaseResult HostSubmitPlayerSwing( in AttackReleaseIntent intent, string swingNote )
	{
		if ( IsAuthoritativeMeleeBlocking )
			return HostReject( AttackReleaseDebugCode.RejectMeleeBusy, "guard is up" );

		// Started the moment it arrives: the click itself is one trip old (a buffered or chained click starts on the
		// host's own clock, which the first swing already lined up with the owner's).
		if ( _serverMeleeAttack is null && !IsAttackLocked )
			return HostTryStartPlayerSwing( intent, swingNote, chained: false, lateSeconds: HostOwnerLatencySeconds() );

		_hostHasPending = true;
		_hostPendingIntent = intent;
		_hostPendingArrivedAt = Time.NowDouble;
		_hostPendingNote = swingNote;
		LogMeleePhasePulse( "Swing buffered", $"seq={intent.IntentSequence} claimsChain={intent.ComboStep > 1} busy={(_serverMeleeAttack is not null ? "swing" : "locked")}" );
		return new AttackReleaseResult
		{
			Accepted = true,
			DebugCode = AttackReleaseDebugCode.OkMeleeSweepStarted,
			DebugDetail = $"buffered seq={intent.IntentSequence}"
		};
	}

	static AttackReleaseResult HostReject( int code, string detail ) =>
		new() { Accepted = false, DebugCode = code, DebugDetail = detail };

	/// <summary>Host: pay the light swing, pick the windup (chain / counter / class) and start the runtime.</summary>
	AttackReleaseResult HostTryStartPlayerSwing( in AttackReleaseIntent intent, string swingNote, bool chained, float lateSeconds = 0f )
	{
		if ( IsActiveMainHandBroken() )
			return HostReject( AttackReleaseDebugCode.RejectMeleeBusy, "weapon broken" );

		var t = GetMeleeWeaponTimings();
		if ( !HostTrySpendCombatStamina( t.StaminaCost, afterBlock: false ) )
			return HostReject( AttackReleaseDebugCode.RejectInsufficientStamina, $"need {t.StaminaCost:0.#} stamina" );

		float windup;
		if ( chained )
		{
			windup = t.ComboWindupSeconds;
			_hostComboStep = (byte)Math.Min( 250, _hostComboStep + 1 );
		}
		else
		{
			var counter = Time.NowDouble < _counterWindowUntilSandbox;
			windup = counter ? t.CounterWindupSeconds : t.WindupSeconds;
			_hostComboStep = 1;
		}

		_counterWindowUntilSandbox = 0;
		ServerStartMeleeAttackAction( intent, Math.Max( 0f, lateSeconds ), swingNote, canTurnHeavy: true, windupOverride: windup, comboStep: _hostComboStep );

		return new AttackReleaseResult
		{
			Accepted = true,
			DebugCode = AttackReleaseDebugCode.OkMeleeSweepStarted,
			DebugDetail = $"host swing seq={intent.IntentSequence} step={_hostComboStep} wind={windup:0.###}s chained={chained}"
		};
	}

	/// <summary>
	/// Host, when a swing's recovery runs out: the pending press chains when the owner fired it as a chain, the combo has
	/// room and it arrived after the chain window opened (the last <see cref="ChainInputWindowSeconds"/> of the release,
	/// less the round-trip slack). A chained press arriving up to the slack later still chains (<see cref="HostTickPendingSwing"/>).
	/// </summary>
	internal bool HostTryTakeChain( double releaseEndedAt, double recoveryEndedAt )
	{
		_hostLastRecoveryEndAt = recoveryEndedAt;
		if ( !_hostHasPending || _hostPendingIntent.ComboStep <= 1 )
			return false;
		if ( _hostComboStep >= Math.Max( 1, ComboMaxHits ) )
			return false;

		var slack = Math.Max( 0f, HostLatencySlackSeconds );
		if ( _hostPendingArrivedAt < releaseEndedAt - Math.Max( 0f, ChainInputWindowSeconds ) - slack )
			return false;

		_hostChainNext = true;
		return true;
	}

	/// <summary>Host, every tick with no swing running: start the chained follow-up, or a pending press once the pawn is free.</summary>
	void HostTickPendingSwing()
	{
		if ( _serverMeleeAttack is not null || !_hostHasPending )
			return;

		// A chained press the round trip delayed past the recovery end still chains within the slack.
		var lateChain = !_hostChainNext && _hostPendingIntent.ComboStep > 1
		                && _hostComboStep > 0 && _hostComboStep < Math.Max( 1, ComboMaxHits )
		                && Time.NowDouble - _hostLastRecoveryEndAt <= Math.Max( 0f, HostLatencySlackSeconds )
		                && !IsAttackLocked && !IsAuthoritativeMeleeBlocking;
		if ( _hostChainNext || lateChain )
		{
			_hostChainNext = false;
			_hostHasPending = false;
			HostRunPending( chained: true );
			return;
		}

		var age = Time.NowDouble - _hostPendingArrivedAt;
		if ( age > Math.Max( 0f, AttackInputBufferSeconds ) + Math.Max( 0f, HostLatencySlackSeconds ) )
		{
			_hostHasPending = false;
			HostNotifyOwnerSwingDropped( _hostPendingIntent.IntentSequence, "buffered click expired" );
			return;
		}

		if ( IsAttackLocked || IsAuthoritativeMeleeBlocking )
			return;

		_hostHasPending = false;
		HostRunPending( chained: false );
	}

	void HostRunPending( bool chained )
	{
		var result = HostTryStartPlayerSwing( _hostPendingIntent, _hostPendingNote, chained );
		if ( !result.Accepted )
			HostNotifyOwnerSwingDropped( _hostPendingIntent.IntentSequence, result.DebugDetail );
	}

	/// <summary>Host: a press the owner already shows was dropped — tell it to let go of the predicted swing.</summary>
	void HostNotifyOwnerSwingDropped( ushort seq, string reason )
	{
		LogMeleePhasePulse( "Swing dropped", $"seq={seq} {reason}" );
		var result = HostReject( AttackReleaseDebugCode.RejectMeleeBusy, reason ?? "dropped" );
		if ( GameObject.Network is { Active: true } && !IsLocalCombatDriver() )
			RpcReceiveAttackReleaseResult( result );
		else
			OwnerOnSwingRejected( result );
	}

	/// <summary>Host: forget the combo and any stored press (guard up, stagger, blocked swing).</summary>
	void HostResetExchange()
	{
		_hostComboStep = 0;
		_hostHasPending = false;
		_hostChainNext = false;
	}

	[Rpc.Host]
	void RpcHostFeint( ushort seq )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		HostTryFeint( seq );
	}

	/// <summary>Host: feint the swing in flight when it is still early in its windup (the round trip gets the slack).</summary>
	internal bool HostTryFeint( ushort seq )
	{
		var runtime = _serverMeleeAttack;
		if ( runtime is null || runtime.IntentSequence != seq )
			return false;

		if ( !runtime.CanFeint( Math.Clamp( FeintCutoffFraction, 0f, 1f ), Math.Max( 0f, HostLatencySlackSeconds ),
			     held: !HostWasPrimaryAttackReleased( seq ) ) )
		{
			LogMeleePhasePulse( "Feint refused", $"seq={seq} past the cutoff" );
			return false;
		}

		HostTrySpendCombatStamina( FeintStaminaCost, afterBlock: false );
		ServerCancelMeleeAttack();
		HostResetExchange();
		ApplyAttackLock( FeintRecoverySeconds );
		HostBroadcastSwingCancelled();
		LogMeleePhasePulse( "Feint", $"seq={seq} lock {FeintRecoverySeconds:0.###}s" );
		return true;
	}

	/// <summary>
	/// Host: a landed melee hit staggers this pawn (the victim). Hard staggers inside the protection window do nothing
	/// (the damage still landed). A stagger interrupts the victim's own swing and drops its stored press.
	/// </summary>
	internal bool HostTryStagger( float seconds, bool hard )
	{
		// A landed hit breaks a raised guard (another side, the back arc): it needs a fresh press.
		HostBreakGuard();
		if ( seconds <= 1e-4f )
			return false;

		var now = Time.NowDouble;
		if ( hard )
		{
			if ( now - _lastHardStaggerAtSandbox < Math.Max( 0f, HardStaggerProtectionSeconds ) )
			{
				LogMeleePhasePulse( "Stagger protected", $"{GameObject.Name} hard stagger ignored ({now - _lastHardStaggerAtSandbox:0.###}s since the last)" );
				return false;
			}

			_lastHardStaggerAtSandbox = now;
		}

		if ( _serverMeleeAttack is not null )
		{
			ServerCancelMeleeAttack();
			HostBroadcastSwingCancelled();
		}

		HostResetExchange();
		return true;
	}

	/// <summary>
	/// Host, this pawn just blocked a swing: a light gives the light block reaction then the counter window, a heavy
	/// staggers the blocker (attacks locked, the guard stays) unless hard-stagger protection holds.
	/// </summary>
	internal void HostOnBlockedSwing( bool attackerHeavy, in MeleeWeaponTimings attackerTimings )
	{
		if ( attackerHeavy )
		{
			var stagger = attackerTimings.HeavyBlockStaggerSeconds;
			var now = Time.NowDouble;
			if ( stagger <= 1e-4f || now - _lastHardStaggerAtSandbox < Math.Max( 0f, HardStaggerProtectionSeconds ) )
				return;

			_lastHardStaggerAtSandbox = now;
			HostApplyDefenseWindows( stagger, 0f, stagger );
			return;
		}

		HostApplyDefenseWindows( LightBlockReactionSeconds, LightBlockReactionSeconds + CounterWindowSeconds, 0f );
	}

	/// <summary>Host → owner: attack lock, counter window and footwork stagger after a block (the owner predicts none of these).</summary>
	void HostApplyDefenseWindows( float attackLockSeconds, float counterWindowSeconds, float staggerFootworkSeconds )
	{
		ApplyDefenseWindowsLocal( attackLockSeconds, counterWindowSeconds, staggerFootworkSeconds );
		if ( GameObject.Network is { Active: true } && Networking.IsHost && !IsLocalCombatDriver() )
			RpcOwnerDefenseWindows( attackLockSeconds, counterWindowSeconds, staggerFootworkSeconds );
	}

	void ApplyDefenseWindowsLocal( float attackLockSeconds, float counterWindowSeconds, float staggerFootworkSeconds )
	{
		var now = Time.NowDouble;
		ApplyAttackLock( attackLockSeconds );
		if ( counterWindowSeconds > 1e-4f )
			_counterWindowUntilSandbox = now + counterWindowSeconds;
		if ( staggerFootworkSeconds > 1e-4f )
			_staggerFootworkUntilSandbox = Math.Max( _staggerFootworkUntilSandbox, now + staggerFootworkSeconds );
	}

	[Rpc.Owner]
	void RpcOwnerDefenseWindows( float attackLockSeconds, float counterWindowSeconds, float staggerFootworkSeconds )
	{
		if ( Networking.IsHost )
			return;
		ApplyDefenseWindowsLocal( attackLockSeconds, counterWindowSeconds, staggerFootworkSeconds );
	}

	/// <summary>Host → every peer: the swing was cut (feint, stagger) — drop the cocked pose / strike, and the owner's prediction.</summary>
	void HostBroadcastSwingCancelled()
	{
		if ( GameObject.Network is { Active: true } && Networking.IsHost )
			RpcBroadcastSwingCancelled();
		else
			ApplySwingCancelledLocal();
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable | NetFlags.SendImmediate )]
	void RpcBroadcastSwingCancelled() => ApplySwingCancelledLocal();

	void ApplySwingCancelledLocal()
	{
		var animation = Components.Get<PlayerAnimation>();
		animation?.CancelMeleeSwingSnap();
		animation?.AbortMeleeAttackAnimClip( "swing cancelled" );
		_clientSwingTracePlayback = null;
		ClearMeleeAttackBasisFromIntent();
		if ( IsLocalCombatDriver() )
		{
			ClearOwnerSwingTracking();
			ClearQueuedAttackPress();
		}
	}

	/// <summary>Owner: the host refused / dropped a swing this machine already shows — let go of it.</summary>
	void OwnerOnSwingRejected( in AttackReleaseResult result )
	{
		ClearOwnerMeleeBusyExpect( $"reject {result.DebugCode}" );
		if ( !_ownerSwingTracking )
			return;

		ClearOwnerSwingTracking();
		ClearQueuedAttackPress();
		Components.Get<PlayerAnimation>()?.CancelMeleeSwingSnap();
	}

	// ------------------------------------------------------------------ combat_test hooks

	/// <summary>combat_test: the windup the host gave the swing in flight (-1 when none).</summary>
	internal float TestSwingWindupSeconds => _serverMeleeAttack?.WindupRemainingSeconds ?? -1f;

	/// <summary>combat_test: the host's combo step of the swing in flight / last finished.</summary>
	internal int TestComboStep => _hostComboStep;

	/// <summary>combat_test: the host's swing in flight is a heavy still held.</summary>
	internal bool TestSwingHeavyHolding => _serverMeleeAttack?.IsHeavyHolding == true;

	/// <summary>combat_test: the host's swing in flight is a heavy (false once a morphed heavy let go early went light).</summary>
	internal bool TestSwingIsHeavy => _serverMeleeAttack?.IsHeavy == true;

	/// <summary>combat_test: <see cref="MeleeAttackTypes"/> of the host's swing in flight (255 when none).</summary>
	internal byte TestSwingAttackType => _serverMeleeAttack?.AttackType ?? 255;

	/// <summary>combat_test: the host phase of the swing in flight.</summary>
	internal MeleeSwingPhase TestSwingPhase => _serverMeleeAttack?.Phase ?? MeleeSwingPhase.None;

	/// <summary>combat_test: forget every exchange window between rounds (attack lock, counter window, stagger protection, stored press).</summary>
	internal void TestResetExchange()
	{
		HostResetExchange();
		_attackLockUntilSandbox = 0;
		_counterWindowUntilSandbox = 0;
		_staggerFootworkUntilSandbox = 0;
		_lastHardStaggerAtSandbox = double.NegativeInfinity;
	}

	// ------------------------------------------------------------------ stamina

	/// <summary>
	/// Host: spend combat stamina with the combat regen delay (after an attack / after a block). False when the pawn
	/// cannot pay. Infinite stamina (dev setting) always pays.
	/// </summary>
	internal bool HostTrySpendCombatStamina( float cost, bool afterBlock )
	{
		if ( cost <= 1e-4f )
			return true;

		var pv = Components.Get<PlayerVitals>();
		if ( pv is null )
		{
			// A scav: its own stamina bar, the player's regen delays.
			return HostEntityVitals is not { } entity || entity.HostTrySpendStamina( cost, CombatRegenDelaySeconds( afterBlock ) );
		}

		if ( VitalsAuthority.Instance is { } vAuth )
		{
			var delay = afterBlock ? vAuth.StaminaRegenDelayAfterBlockSeconds : vAuth.StaminaRegenDelayAfterAttackSeconds;
			return vAuth.TryApplyDeltas( GameObject, 0f, -cost, pv, regenDelaySeconds: delay );
		}

		return pv.HasStaminaFor( cost ) && pv.RequestVitalsDelta( 0f, -cost );
	}

	// ------------------------------------------------------------------ footwork + turning

	/// <summary>The melee state the legs follow on this pawn's driver (<see cref="PlayerMovement.GetCombatFootworkScale"/>).</summary>
	public CombatFootworkState CurrentFootworkState
	{
		get
		{
			if ( IsHitReactionActive || Time.NowDouble < _staggerFootworkUntilSandbox )
				return CombatFootworkState.Staggered;

			if ( IsGuardShown )
				return CombatFootworkState.Blocking;

			if ( _serverMeleeAttack is { } runtime )
				return FootworkFor( runtime.Phase, runtime.IsHeavy || runtime.IsHeavyHolding );

			var phase = IsLocalCombatDriver() ? OwnerSwingPhaseNow() : MeleeSwingPhase.None;
			if ( phase != MeleeSwingPhase.None )
				return FootworkFor( phase, _ownerSwingHeavy );

			return IsInCombatRecovery && !IsPunchRecovery ? CombatFootworkState.Recovery : CombatFootworkState.None;
		}
	}

	static CombatFootworkState FootworkFor( MeleeSwingPhase phase, bool heavy ) => phase switch
	{
		MeleeSwingPhase.Windup => heavy ? CombatFootworkState.HeavyWindup : CombatFootworkState.LightWindup,
		MeleeSwingPhase.Release => heavy ? CombatFootworkState.HeavyRelease : CombatFootworkState.LightRelease,
		MeleeSwingPhase.Recovery => CombatFootworkState.Recovery,
		_ => CombatFootworkState.None
	};

	/// <summary>
	/// Damage scale of a swing of <paramref name="attackType"/> whose body turns at <paramref name="bodyTurnDegreesPerSecond"/>
	/// (signed, + = left). A right swing travels right → left, i.e. toward +yaw; a left swing toward -yaw; an overhead
	/// does not travel sideways (1). Pawns without a body tracker (scavs) never scale.
	/// </summary>
	internal float TurnDamageScale( byte attackType, float bodyTurnDegreesPerSecond )
	{
		var travel = attackType == MeleeAttackTypes.Right ? 1f : attackType == MeleeAttackTypes.Left ? -1f : 0f;
		if ( travel == 0f || Components.Get<PlayerMovement>( FindMode.EverythingInSelf ) is not { } movement )
			return 1f;

		var share = Math.Clamp( travel * bodyTurnDegreesPerSecond / Math.Max( 1f, movement.BodyTurnDegreesPerSecond ), -1f, 1f );
		return share >= 0f
			? 1f + share * (Math.Max( 1f, TurnDamageMaxScale ) - 1f)
			: 1f + share * (1f - Math.Clamp( TurnDamageMinScale, 0f, 1f ));
	}

	/// <summary>Host: the turn damage scale the last hit tick of this pawn's swing used (combat_test).</summary>
	internal float LastTurnDamageScale { get; set; } = 1f;

	/// <summary>Unwrapped body yaw of this pawn (its swing basis turns with it); NaN for pawns without one (scavs).</summary>
	internal float SwingBodyYawUnwrapped() =>
		Components.Get<PlayerMovement>( FindMode.EverythingInSelf ) is { } movement ? movement.BodyYawUnwrapped : float.NaN;
}
