using System;
using Sandbox;

namespace Survival;

public partial class PlayerCombat
{
	/// <summary>
	/// Host: this pawn stopped a blade. A chamber costs nothing (the defender's own swing carries on). A block costs the
	/// JSON row's stamina (a heavy costs more; a guard that cannot pay drains to empty), and a blocked melee blow
	/// <b>breaks the guard</b> - it drops and needs a fresh press (Mark 2026-11-16: "When you block a hit the guard
	/// breaks"). An arrow (<paramref name="breaksGuard"/> false) leaves the guard up. Holding the guard costs nothing - only
	/// a block does (Mark 2026-11-16: "Holding block shouldn't eat stamina btw, that costs nothing unless you actually
	/// block"; the 5/s long-guard drain is gone). Then
	/// the exchange windows follow: blocking a light opens the counter window, blocking a heavy staggers the blocker
	/// (<see cref="HostOnBlockedSwing"/>).
	/// </summary>
	internal void ServerApplyBlockOutcome( in MeleeBlockOutcome outcome, Component attacker, Vector3 hitWorld, bool breaksGuard = true )
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		// Where the blades met: the point on the attacker's weapon nearest the guarding weapon, and how far the guard has
		// to reach out to be there (Mark 2026-11-23: "my sword should be pushed further out to the right to meet his
		// sword"). The guard is still up here - it breaks below. Arrows and chambers keep the plain contact point.
		var meetPush = Vector3.Zero;
		if ( !outcome.WasChambered && attacker is PlayerCombat swinger && swinger != this && swinger.GameObject.IsValid()
		     && swinger.TryGetServerSwingInfo( out _, out var swingType ) )
		{
			var meet = MeleeGuardGeometry.TryMeetGuard( this, swinger, swingType, Time.NowDouble );
			if ( meet.Distance < float.MaxValue )
			{
				hitWorld = meet.WeaponPoint;
				var reach = meet.WeaponPoint - meet.GuardPoint;
				var maxPush = Math.Max( 0f, GuardMeetPushMeters ) * GetPawnUnitsPerMeter();
				meetPush = reach.Length > maxPush ? reach.Normal * maxPush : reach;
			}
		}

		ServerBroadcastBlockSparks( hitWorld, meetPush );
		// Through the attacker's combat when it has one: every exchange cue plays on the attacker.
		var voice = attacker as PlayerCombat;
		(voice.IsValid() ? voice : this).ServerBroadcastCombatCue( outcome.WasChambered ? CombatAudioCue.Chamber
			: outcome.OutcomeId == MeleeBlockStaggerCatalog.BlockHeavyId ? CombatAudioCue.BlockedHeavy
			: CombatAudioCue.Blocked, hitWorld );
		PlaytestLog.HostBlocked( this, attacker, in outcome );

		if ( outcome.WasChambered )
			return;

		var vitals = Components.Get<PlayerVitals>();
		if ( vitals is not null )
		{
			if ( outcome.StaminaCost > 1e-4f && !HostTrySpendCombatStamina( outcome.StaminaCost, afterBlock: true ) )
			{
				// Not enough stamina left to absorb the blow: the guard still stops it, empties the bar and drops.
				HostTrySpendCombatStamina( Math.Max( 0f, vitals.CurrentStamina ), afterBlock: true );
				ConsumeAuthoritativeMeleeBlock();
			}

			if ( outcome.HealthDamage > 1e-4f )
				vitals.ApplyDamageAfterArmor( outcome.HealthDamage, attacker );
		}
		else if ( HostEntityVitals is { } entity && outcome.StaminaCost > 1e-4f
		          && !HostTrySpendCombatStamina( outcome.StaminaCost, afterBlock: true ) )
		{
			// A scav's guard that cannot pay: it still stops the blow, empties the bar and drops (the player rule).
			entity.HostEmptyStamina( CombatRegenDelaySeconds( afterBlock: true ) );
			ConsumeAuthoritativeMeleeBlock();
		}

		if ( breaksGuard )
			HostBreakGuard();

		var attackerCombat = attacker as PlayerCombat ?? attacker?.Components.Get<PlayerCombat>();
		var attackerTimings = attackerCombat?.GetMeleeWeaponTimings() ?? GetMeleeWeaponTimings();
		HostOnBlockedSwing( outcome.OutcomeId == MeleeBlockStaggerCatalog.BlockHeavyId, attackerTimings );

		Log.Info(
			$"[MeleeBlock] outcome {GameObject.Name}: {outcome.OutcomeId} hp={outcome.HealthDamage:0.#} stam={outcome.StaminaCost:0.#}" );
	}

	/// <summary>Host: this pawn's swing just chambered <paramref name="attacker"/>'s — the Parry Recharge augment pays out here.</summary>
	internal void HostOnChambered( PlayerCombat attacker )
	{
		_ = attacker;
		if ( Components.Get<PlayerVitals>() is { } vitals
		     && Components.Get<PlayerAugments>() is { } augments
		     && augments.TryGetActiveDefinition( AugmentAbility.ParryRecharge, out var parryDef ) )
			vitals.HostBeginStaminaRegenBoost( parryDef.EffectSeconds, parryDef.EffectScale );
	}

	/// <summary>Host: the release start of this pawn's swing in flight (for the chamber check); false when none is due.</summary>
	internal bool TryGetServerReleaseStart( out double releaseStartSandbox, out byte attackType )
	{
		releaseStartSandbox = 0;
		attackType = 0;
		if ( _serverMeleeAttack is not { } runtime || runtime.ReleaseStartSandbox is not { } start )
			return false;

		releaseStartSandbox = start;
		attackType = runtime.AttackType;
		return true;
	}

	[Property, Group( "Combat — Block" ), Title( "Show block guard + ground arc (debug)" )]
	public bool ShowBlockVisualization { get; set; }

	[Property, Group( "Combat — Block" ), Title( "Show block footprint volume (debug wedge fill)" )]
	public bool ShowBlockFootprintVolume { get; set; }

	[Property, Group( "Combat — Block" ), Title( "Footprint volume fill alpha" )]
	public float BlockFootprintFillAlpha { get; set; } = 0.09f;

	[Property, Group( "Combat — Block" ), Title( "Block sample sphere radius" )]
	public float BlockSampleSphereRadius { get; set; } = 2f;

	[Property, Group( "Combat — Block" ), Title( "Block guard sample count" )]
	public float BlockGuardSampleCount { get; set; } = 24f;

	[Property, Group( "Combat — Block" ), Title( "Front block arc (° total)" ), Range( 90f, 360f ), Step( 5f ), Description( "Facing coverage while blocking. 270° = ±135° from look yaw. Teardrop does not change this." )]
	public float MeleeBlockFrontArcDegrees { get; set; } = 270f;

	[Property, Group( "Combat — Block" ), Title( "Flank block angle (°)" ), Range( 0f, 90f ), Step( 5f ), Description( "An attacker further than this to the left / right of the look yaw is blocked with that side's guard, whatever the swing. Inside it the swing decides (overhead = up, left / right by where it starts). 0 = off." )]
	public float MeleeBlockFlankDegrees { get; set; } = 15f;

	[Property, Group( "Combat — Block" ), Title( "Block body radius" ), Range( 4f, 48f ), Step( 1f ), Description( "Horizontal radius of the block shell around the pawn (body coverage, not an extended guard arc)." )]
	public float MeleeBlockBodyRadius { get; set; } = 18f;

	[Property, Group( "Combat — Block" ), Title( "Block shell height padding" ), Range( 0f, 24f ), Step( 1f ), Description( "Extra height above eye height for the block shell." )]
	public float BlockShellHeightPadding { get; set; } = 8f;

	[Property, Group( "Combat — Block" ), Title( "Ground arc height offset" )]
	public float BlockGroundArcHeightOffset { get; set; } = 2f;

	/// <summary>Guard up → it stops hits (Mark 2026-11-14 spec: 100 ms). The shield / sword block pose finish raising in the same time.</summary>
	[Property, Group( "Combat — Block" ), Title( "Block activation (s)" ), Range( 0f, 0.3f ), Step( 0.01f ), Description( "The guard rises → it actually stops hits. Hits landing earlier go through." )]
	public float BlockActivationSeconds { get; set; } = 0.15f;

	[Property, Group( "Combat — Block" ), Title( "Block direction switch (s)" ), Range( 0f, 0.3f ), Step( 0.01f ), Description( "A new guard direction takes this long; the old one keeps covering until then." )]
	public float BlockSwitchSeconds { get; set; } = 0.15f;

	[Property, Group( "Combat — Block" ), Title( "Block minimum hold (s)" ), Range( 0f, 0.5f ), Step( 0.01f ), Description( "A guard stays up at least this long even if the button is let go at once." )]
	public float BlockMinHoldSeconds { get; set; } = 0.1f;

	// --- The guard bar: where the raised weapon physically is (MeleeGuardGeometry). Meters, turned with the body.
	// A value of 0 (a prefab / scene saved before the property existed) reads as the default.

	[Property, Group( "Combat — Block (guard bar)" ), Title( "Physical guard" ), Description( "A guard also stops any swing that physically runs into the guarding weapon - raised, still rising, or crossing to another side. Off = only the side the red marker calls blocks, once the guard is up." )]
	public bool MeleePhysicalGuardEnabled { get; set; } = true;

	[Property, Group( "Combat — Block (guard bar)" ), Title( "Overhead bar height (m)" ), Range( 1f, 2.5f ), Step( 0.01f ), Description( "Height of the level blade of the overhead guard (the BLOCK_UP pose holds it at 1.86 m)." )]
	public float GuardBarOverheadHeightMeters { get; set; } = 1.86f;

	[Property, Group( "Combat — Block (guard bar)" ), Title( "Overhead bar forward of the body (m)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float GuardBarOverheadForwardMeters { get; set; } = 0.2f;

	[Property, Group( "Combat — Block (guard bar)" ), Title( "Overhead bar half width (m)" ), Range( 0.1f, 1f ), Step( 0.05f ), Description( "The overhead guard's blade reaches this far left and right of the body's middle." )]
	public float GuardBarOverheadHalfWidthMeters { get; set; } = 0.45f;

	[Property, Group( "Combat — Block (guard bar)" ), Title( "Guard meet push (m)" ), Range( 0f, 0.8f ), Step( 0.05f ), Description( "On a block the guarding weapon is pushed at most this far toward the point the blades met. 0 = off." )]
	public float GuardMeetPushMeters { get; set; } = 0.4f;

	[Property, Group( "Combat — Block (guard bar)" ), Title( "Bar forward of the body (m)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float GuardBarForwardMeters { get; set; } = 0.35f;

	[Property, Group( "Combat — Block (guard bar)" ), Title( "Side bar offset left / right (m)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float GuardBarSideMeters { get; set; } = 0.3f;

	[Property, Group( "Combat — Block (guard bar)" ), Title( "Side bar bottom height (m)" ), Range( 0f, 2f ), Step( 0.05f )]
	public float GuardBarBottomMeters { get; set; } = 0.9f;

	[Property, Group( "Combat — Block (guard bar)" ), Title( "Side bar top height (m)" ), Range( 0.5f, 2.5f ), Step( 0.05f ), Description( "Tip height of the side guards." )]
	public float GuardBarTopMeters { get; set; } = 1.95f;

	[Property, Group( "Combat — Block (guard bar)" ), Title( "Catch radius (m)" ), Range( 0.05f, 0.6f ), Step( 0.01f ), Description( "How close the attacking weapon must pass to the guarding weapon to be stopped - the guard's thickness." )]
	public float GuardCatchRadiusMeters { get; set; } = 0.28f;

	static float OrDefault( float value, float fallback ) => value > 1e-4f ? value : fallback;

	internal float GuardBarForwardMetersOrDefault => OrDefault( GuardBarForwardMeters, 0.35f );
	internal float GuardBarSideMetersOrDefault => OrDefault( GuardBarSideMeters, 0.3f );
	internal float GuardBarBottomMetersOrDefault => OrDefault( GuardBarBottomMeters, 0.9f );
	internal float GuardBarTopMetersOrDefault => OrDefault( GuardBarTopMeters, 1.95f );
	internal float GuardCatchRadiusMetersOrDefault => OrDefault( GuardCatchRadiusMeters, 0.28f );
	internal float GuardBarOverheadHeightMetersOrDefault => OrDefault( GuardBarOverheadHeightMeters, 1.86f );
	internal float GuardBarOverheadForwardMetersOrDefault => OrDefault( GuardBarOverheadForwardMeters, 0.2f );
	internal float GuardBarOverheadHalfWidthMetersOrDefault => OrDefault( GuardBarOverheadHalfWidthMeters, 0.45f );

	/// <summary>Host: how far the guard has risen at <paramref name="atSandbox"/> (0 = just pressed, 1 = up; <see cref="BlockActivationSeconds"/>).</summary>
	internal float GetGuardRise01( double atSandbox )
	{
		var seconds = Math.Max( 0f, BlockActivationSeconds );
		return seconds <= 1e-4f ? 1f : (float)Math.Clamp( (atSandbox - _serverBlockStartedAtSandbox) / seconds, 0.0, 1.0 );
	}

	/// <summary>Host: how far a guard direction change has travelled at <paramref name="atSandbox"/> (1 = arrived / no change running) and the side it left.</summary>
	internal float GetGuardSwitch01( double atSandbox, out byte fromDirection )
	{
		fromDirection = NormalizeCardinalBlockDirection( _blockDirectionBeforeSwitch );
		var seconds = Math.Max( 0f, BlockSwitchSeconds );
		if ( seconds <= 1e-4f || double.IsNegativeInfinity( _serverBlockLastDirectionChangeAtSandbox ) )
			return 1f;

		return (float)Math.Clamp( (atSandbox - _serverBlockLastDirectionChangeAtSandbox) / seconds, 0.0, 1.0 );
	}

	[Property, Group( "Combat — Block" ), Title( "Log rejected blocks to console" )]
	public bool LogMeleeBlockRejectionsToConsole { get; set; }

	[Property, Group( "Combat — Block" ), Title( "Block debug color" )]
	public Color BlockDebugColor { get; set; } = new( 0.22f, 0.92f, 0.38f, 0.92f );


	bool _authoritativeMeleeBlockActive;
	byte _authoritativeMeleeBlockDirection = SwingDirs.Up;

	bool _hostReportedBlockActive;
	/// <summary>Host, remote owner: the guard was used up (block landed / broken) — ignore "held" reports until the owner reports a release.</summary>
	bool _hostBlockAwaitingRelease;
	byte _hostReportedBlockDirection = SwingDirs.Up;
	float _hostReportedBlockBasisYaw;

	float _remoteBlockBasisYaw;
	bool _remoteBlockBasisYawValid;

	bool _lastSentBlockActive;
	byte _lastSentBlockDirection = byte.MaxValue;
	bool _lastSentBlockYawValid;
	float _lastSentBlockBasisYaw;

	bool _lastBroadcastBlockActive;
	byte _lastBroadcastBlockDirection = byte.MaxValue;
	bool _lastBroadcastBlockYawValid;
	float _lastBroadcastBlockBasisYaw;

	bool _meleeBlockConsumedAwaitingRelease;

	/// <summary>Horizontal look yaw for block guard on remote pawns (view yaw ≠ body yaw).</summary>
	public Rotation GetBlockCombatBasisRotation()
	{
		if ( !IsLocalCombatDriver() && _remoteBlockBasisYawValid && IsAuthoritativeMeleeBlocking )
			return new Angles( 0f, _remoteBlockBasisYaw, 0f ).ToRotation();

		return GetCameraYawRotation();
	}

	public float GetBlockCombatBasisYaw() => GetBlockCombatBasisRotation().Angles().yaw;

	/// <summary>Committed block pose (L/R/U); morphs only when teardrop cardinal changes, not on look rotation.</summary>
	byte _heldBlockGuardDir = SwingDirs.Up;

	float _postAttackRecoveryRemaining;

	double _serverBlockStartedAtSandbox;
	double _serverBlockLastDirectionChangeAtSandbox;
	/// <summary>The guard direction that kept covering while a switch to <see cref="_authoritativeMeleeBlockDirection"/> runs.</summary>
	byte _blockDirectionBeforeSwitch = SwingDirs.Up;
	/// <summary>Owner: when this machine's own guard last rose (the minimum hold counts from here); 0 = down.</summary>
	double _localGuardUpSince;
	double _localBlockVizStartedAt;
	bool _localBlockInputWasActive;

	bool _blockDirectionChangedThisFrame;

	public CombatState CombatState { get; private set; } = CombatState.Idle;

	public bool IsAuthoritativeMeleeBlocking => _authoritativeMeleeBlockActive;

	public byte AuthoritativeMeleeBlockDirection => _authoritativeMeleeBlockDirection;

	public double ServerBlockStartedAtSandbox => _serverBlockStartedAtSandbox;

	/// <summary>Host time the guard actually blocks from: block start + <see cref="BlockActivationSeconds"/>.</summary>
	public double ServerBlockActiveFromSandbox => _serverBlockStartedAtSandbox + Math.Max( 0f, BlockActivationSeconds );

	/// <summary>
	/// The guard direction that covers at <paramref name="atSandbox"/>: a direction change takes <see cref="BlockSwitchSeconds"/>,
	/// and until then the previous one keeps covering.
	/// </summary>
	public byte GetEffectiveGuardDirection( double atSandbox )
	{
		var current = NormalizeCardinalBlockDirection( _authoritativeMeleeBlockDirection );
		if ( atSandbox - _serverBlockLastDirectionChangeAtSandbox < Math.Max( 0f, BlockSwitchSeconds ) )
			return NormalizeCardinalBlockDirection( _blockDirectionBeforeSwitch );
		return current;
	}

	public byte GetActiveBlockDirection() => GetBlockGuardDirection();

	/// <summary>
	/// The side a guard raised right now would start on: the owner's live teardrop side, elsewhere the last guard's.
	/// <see cref="PlayerAnimation"/> keeps the guard-direction pose selector on it while the guard is down, so the
	/// guard rises straight into its side instead of cross-fading over from the previous guard's.
	/// </summary>
	public byte GetPendingBlockDirection() =>
		NormalizeCardinalBlockDirection( IsLocalCombatDriver() ? _blockLiveSwingDir : _authoritativeMeleeBlockDirection );

	public bool BlockDirectionChangedThisFrame => _blockDirectionChangedThisFrame;

	internal int GetBlockGuardSampleCount() =>
		(int)Math.Clamp( BlockGuardSampleCount, 4f, 48f );

	/// <summary>
	/// Host: a blocked melee blow or a hit taken breaks a raised guard - it drops and comes back only on a fresh press
	/// (Mark 2026-11-16: "your character is no longer blocking and need to release block and click again for it to come
	/// up"). The same path as a guard that ran out of stamina (<see cref="ConsumeAuthoritativeMeleeBlock"/>).
	/// </summary>
	internal void HostBreakGuard()
	{
		if ( _authoritativeMeleeBlockActive )
			ConsumeAuthoritativeMeleeBlock();
	}

	/// <summary>Host: the guard was broken / used up and the pawn has not let go of block yet (scavs re-press on this).</summary>
	internal bool HostGuardAwaitingRelease => _hostBlockAwaitingRelease;

	internal void OnBlockPressCommitGuardDirection()
	{
		_heldBlockGuardDir = NormalizeCardinalBlockDirection( _blockLiveSwingDir );
		_blockGuardPrevYaw = GetBlockCombatBasisYaw();
		_blockGuardYawTracking = true;
	}

	void TickCombatStateMachine()
	{
		if ( !IsLocalCombatDriver() )
			return;

		var blockHeld = LocalBlockInputActive();
		var attacking = OwnerSwingPhaseNow() != MeleeSwingPhase.None || ServerHasActiveMeleeAttackAction;

		if ( blockHeld )
		{
			CombatState = CombatState.Blocking;
			_postAttackRecoveryRemaining = 0f;
		}
		else if ( attacking )
		{
			CombatState = CombatState.Attacking;
		}
		else if ( _postAttackRecoveryRemaining > 0f )
		{
			_postAttackRecoveryRemaining = MathF.Max( 0f, _postAttackRecoveryRemaining - Time.Delta );
			CombatState = CombatState.PostAttack;
		}
		else
		{
			CombatState = CombatState.Idle;
		}
	}

	void TickAuthoritativeMeleeBlockState()
	{
		if ( GameObject.IsProxy && !Networking.IsHost )
			return;

		_blockDirectionChangedThisFrame = false;

		if ( IsServerSideForMeleeAuthority() && !IsLocalCombatDriver() )
		{
			// Same gates as the owner's own input (CanStartBlock / LocalBlockInputActive): a used-up guard stays down
			// until released, a lockout (blocked recovery, stagger) or an exhausted bar allows no guard, and a swing in
			// flight keeps the arm until it ends (the owner feinted it already when it was early enough).
			var reported = _hostReportedBlockActive
			               || (_authoritativeMeleeBlockActive && Time.NowDouble - _serverBlockStartedAtSandbox < Math.Max( 0f, BlockMinHoldSeconds ));
			var hostActive = reported && !_hostBlockAwaitingRelease && !IsCombatActionLocked && !IsStaminaExhausted
			                 && _serverMeleeAttack is null;
			SetAuthoritativeMeleeBlockState( hostActive, _hostReportedBlockDirection, _hostReportedBlockBasisYaw );
			return;
		}

		if ( !IsLocalCombatDriver() )
			return;

		if ( Input.Released( BlockAction ) )
			_meleeBlockConsumedAwaitingRelease = false;

		var active = LocalBlockInputActive();
		var dir = GetBlockGuardDirection();
		var prevDir = _authoritativeMeleeBlockDirection;

		// Playtest log: every press / release of the block button as this machine saw it, raised guard or not.
		var buttonDown = Input.Down( BlockAction );
		if ( buttonDown != _playtestBlockButtonWasDown && PlaytestLog.ClientReporting )
			PlaytestReportBlockButton( buttonDown, active, dir );
		_playtestBlockButtonWasDown = buttonDown;

		if ( active && !_localBlockInputWasActive )
		{
			_localBlockVizStartedAt = _block.Snapshot.PressedSandboxTimeNowDouble ?? Time.NowDouble;
			_localGuardUpSince = Time.NowDouble;
		}
		else if ( !active )
			_localGuardUpSince = 0;
		_localBlockInputWasActive = active;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			MaybeSendMeleeBlockStateRpc( active, dir );
		else if ( IsServerSideForMeleeAuthority() )
			SetAuthoritativeMeleeBlockState( active, dir );

		if ( active && dir != prevDir )
			_blockDirectionChangedThisFrame = true;
	}

	void SetAuthoritativeMeleeBlockState( bool active, byte direction, float? remoteBasisYaw = null )
	{
		var now = Time.NowDouble;

		// Guard up after the punch lock: drop what is left of the punch pose so the guard shows.
		if ( active && !_authoritativeMeleeBlockActive && IsServerSideForMeleeAuthority()
		     && _combatRecoveryAnim == CombatRecoveryAnim.MeleePunchLeft && !IsInCombatRecovery )
			ClearCombatRecoveryPresentation();

		if ( active && !_authoritativeMeleeBlockActive )
		{
			// A fresh guard rises in its pressed direction (the activation time covers it) — no switch delay on top.
			_serverBlockStartedAtSandbox = now;
			_serverBlockLastDirectionChangeAtSandbox = double.NegativeInfinity;
			HostResetExchange();
		}
		else if ( active && direction is (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up)
		          && direction != _authoritativeMeleeBlockDirection
		          && now >= ServerBlockActiveFromSandbox )
		{
			// A guard that is still rising simply rises on the new side: a mouse flick landing just after the press is
			// part of that press, not a switch on top of it (Mark 2026-11-16: "when we are moving our mouse and
			// clicking ... it takes longer than if we just stand still and click it").
			_blockDirectionBeforeSwitch = GetEffectiveGuardDirection( now );
			_serverBlockLastDirectionChangeAtSandbox = now;
		}

		_authoritativeMeleeBlockActive = active;
		if ( direction is SwingDirs.Left or SwingDirs.Right or SwingDirs.Up )
			_authoritativeMeleeBlockDirection = direction;

		if ( !active )
			_remoteBlockBasisYawValid = false;
		else if ( remoteBasisYaw is { } yaw )
		{
			_remoteBlockBasisYaw = yaw;
			_remoteBlockBasisYawValid = true;
		}

		if ( GameObject.Network is { Active: true } && Networking.IsHost )
		{
			var vizYaw = remoteBasisYaw ?? GetBlockCombatBasisYaw();
			BroadcastMeleeBlockVisualizationIfHost( active, _authoritativeMeleeBlockDirection, vizYaw );
		}
	}

	void BroadcastMeleeBlockVisualizationIfHost( bool active, byte direction, float basisYaw )
	{
		var activeChanged = active != _lastBroadcastBlockActive;
		var dirChanged = direction != _lastBroadcastBlockDirection;
		var yawChanged = !_lastBroadcastBlockYawValid || MathF.Abs( basisYaw - _lastBroadcastBlockBasisYaw ) >= 0.35f;

		if ( !activeChanged && !dirChanged && !( active && yawChanged ) )
			return;

		_lastBroadcastBlockActive = active;
		_lastBroadcastBlockDirection = direction;
		_lastBroadcastBlockBasisYaw = basisYaw;
		_lastBroadcastBlockYawValid = true;

		RpcBroadcastMeleeBlockVisualization( active, direction, basisYaw );
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	void RpcBroadcastMeleeBlockVisualization( bool active, byte direction, float basisYaw )
	{
		if ( Networking.IsHost )
			return;

		if ( active )
		{
			if ( !_authoritativeMeleeBlockActive )
				_serverBlockStartedAtSandbox = Time.NowDouble;
			_remoteBlockBasisYaw = basisYaw;
			_remoteBlockBasisYawValid = true;
		}
		else
			_remoteBlockBasisYawValid = false;

		_authoritativeMeleeBlockActive = active;
		if ( direction is SwingDirs.Left or SwingDirs.Right or SwingDirs.Up )
			_authoritativeMeleeBlockDirection = direction;
	}

	bool _playtestBlockButtonWasDown;

	/// <summary>Owner, playtest log on: one <c>block_input</c> line on the host for this press / release - when it happened on
	/// this machine's clock, whether it raised the guard and, if not, what held it down.</summary>
	void PlaytestReportBlockButton( bool down, bool guardUp, byte direction )
	{
		var why = string.Empty;
		if ( down && !guardUp )
		{
			if ( IsInCombatRecovery ) why += "recovery ";
			if ( IsHitReactionActive ) why += "stagger ";
			if ( _meleeBlockConsumedAwaitingRelease ) why += "consumed ";
			if ( IsStaminaExhausted ) why += "exhausted ";
			if ( IsOwnerSwingHoldingGuard() ) why += "swinging ";
		}

		var incoming = TryGetIncomingAttackSide( out var incomingSide );
		PlaytestLog.RpcClientBlockInput( down, guardUp, why.TrimEnd(), Time.NowDouble, direction, incoming, incomingSide );
	}

	/// <summary>Host: the guard as the host holds it, for the playtest log's <c>guard_miss</c> line.</summary>
	internal readonly struct GuardTrace
	{
		public bool GuardUp { get; init; }
		public bool Held { get; init; }
		public bool AwaitingRelease { get; init; }
		public bool Locked { get; init; }
		public bool Exhausted { get; init; }
		public bool Swinging { get; init; }
		public float GuardAgeSeconds { get; init; }
		public float ActivationSeconds { get; init; }
		public byte Direction { get; init; }
		/// <summary>Seconds since the host last heard the block button change (-1 = never).</summary>
		public float SinceInputSeconds { get; init; }
		public bool LastInputDown { get; init; }
		/// <summary>The round trip the host gave this guard back (0 for the host's own pawn).</summary>
		public float LatencyCreditSeconds { get; init; }
	}

	double _hostBlockReportAtSandbox = -1;

	internal GuardTrace HostGuardTrace( double atSandbox )
	{
		var local = IsLocalCombatDriver();
		var inputAt = local ? (_localBlockInputWasActive ? _localBlockVizStartedAt : -1) : _hostBlockReportAtSandbox;
		return new GuardTrace
		{
			GuardUp = _authoritativeMeleeBlockActive,
			Held = local ? _localBlockInputWasActive : _hostReportedBlockActive,
			AwaitingRelease = local ? _meleeBlockConsumedAwaitingRelease : _hostBlockAwaitingRelease,
			Locked = IsCombatActionLocked,
			Exhausted = IsStaminaExhausted,
			Swinging = _serverMeleeAttack is not null,
			GuardAgeSeconds = _authoritativeMeleeBlockActive ? (float)(atSandbox - _serverBlockStartedAtSandbox) : -1f,
			ActivationSeconds = Math.Max( 0f, BlockActivationSeconds ),
			Direction = GetEffectiveGuardDirection( atSandbox ),
			SinceInputSeconds = inputAt > 0 ? (float)(atSandbox - inputAt) : -1f,
			LastInputDown = local ? _localBlockInputWasActive : _hostReportedBlockActive,
			LatencyCreditSeconds = HostBlockLatencyCreditSeconds(),
		};
	}

	void MaybeSendMeleeBlockStateRpc( bool active, byte direction )
	{
		var basisYaw = GetBlockCombatBasisYaw();
		if ( active == _lastSentBlockActive && direction == _lastSentBlockDirection
		     && _lastSentBlockYawValid && MathF.Abs( basisYaw - _lastSentBlockBasisYaw ) < 0.4f )
			return;

		_lastSentBlockActive = active;
		_lastSentBlockDirection = direction;
		_lastSentBlockBasisYaw = basisYaw;
		_lastSentBlockYawValid = true;
		RpcSubmitMeleeBlockState( active, direction, basisYaw );
	}

	[Rpc.Host]
	void RpcSubmitMeleeBlockState( bool active, byte direction, float basisYaw )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		HostAcceptBlockState( active, direction, basisYaw );
	}

	/// <summary>Host: the round trip of the client that owns this pawn (twice <see cref="HostOwnerLatencySeconds"/>, so capped
	/// by twice Owner latency give-back); 0 for the host's own pawn and scavs.</summary>
	internal float HostBlockLatencyCreditSeconds() => HostOwnerLatencySeconds() * 2f;

	/// <summary>Host intake of a remote owner's guard state (the body of <see cref="RpcSubmitMeleeBlockState"/>).</summary>
	void HostAcceptBlockState( bool active, byte direction, float basisYaw )
	{
		_hostReportedBlockActive = active;
		_hostBlockReportAtSandbox = Time.NowDouble;
		if ( !active )
			_hostBlockAwaitingRelease = false;
		if ( direction is SwingDirs.Left or SwingDirs.Right or SwingDirs.Up )
			_hostReportedBlockDirection = direction;
		_hostReportedBlockBasisYaw = basisYaw;

		// The guard button on a swing early in its windup is a feint (the owner already showed it); later the guard waits.
		if ( active && _serverMeleeAttack is { } runtime )
			HostTryFeint( runtime.IntentSequence );

		if ( !IsLocalCombatDriver() )
		{
			var guardAllowed = active && !_hostBlockAwaitingRelease && !IsCombatActionLocked && !IsStaminaExhausted
			                   && _serverMeleeAttack is null;
			var wasUp = _authoritativeMeleeBlockActive;
			var switchedAt = _serverBlockLastDirectionChangeAtSandbox;
			SetAuthoritativeMeleeBlockState( guardAllowed, direction, basisYaw );

			// A remote owner pressed one trip before this arrived and sees every blow one trip late, so its guard (and a
			// change of side) counts from a full round trip earlier - the host's measured ping, never a time the client
			// claims (Mark 2026-11-24: "see if theres a delay for clients ... remove it at all costs"). The client's own
			// press timestamp used to be written here and was overwritten by SetAuthoritativeMeleeBlockState, so a
			// client's 0.15 s rise started when the message arrived: later than the host player's by its whole ping.
			var credit = HostBlockLatencyCreditSeconds();
			if ( credit > 0f )
			{
				if ( _authoritativeMeleeBlockActive && !wasUp )
					_serverBlockStartedAtSandbox -= credit;
				else if ( _serverBlockLastDirectionChangeAtSandbox != switchedAt )
					_serverBlockLastDirectionChangeAtSandbox -= credit;
			}
		}
	}

	/// <summary>anim_test: the owner's guard button held on <paramref name="direction"/> (the test pawn reads no input; the
	/// host intake below is ignored for the pawn this machine drives, so the old test never raised a guard).</summary>
	internal void TestHoldGuard( bool held, byte direction = SwingDirs.Up )
	{
		_testGuardHeld = held;
		if ( held )
			_heldBlockGuardDir = NormalizeCardinalBlockDirection( direction );
	}

	bool _testGuardHeld;

	/// <summary>Automated combat test (<c>combat_test</c>): feeds a guard through the same host intake a remote owner's block RPC uses.</summary>
	internal void TestSubmitBlockIntent( bool active, float basisYaw, byte direction = SwingDirs.Up ) =>
		HostAcceptBlockState( active, direction, basisYaw );

	/// <summary>
	/// Dodge roll started: the roll's i-frames + reposition replace the guard, so drop it the same
	/// way a consumed block does — the held Block button stops guarding and re-guarding needs an
	/// actual re-press. Owner-side input semantics only; the regular block sync tick reports the
	/// drop to the host, and <see cref="TickCombatStateMachine"/> leaves Blocking on its own.
	/// </summary>
	internal void OnDodgeRollStarted()
	{
		if ( !IsLocalCombatDriver() )
			return;

		// Only when the button is actually held — the flag is cleared by Input.Released, so setting
		// it with the button up would silently eat the player's next block press.
		if ( _block.Down )
			_meleeBlockConsumedAwaitingRelease = true;
	}

	internal void NotifyAuthoritativeMeleeBlockIntercepted()
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;
	}

	/// <summary>Host: the guard drops and needs a fresh press (empty stamina, dodge roll).</summary>
	internal void ConsumeAuthoritativeMeleeBlock()
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		if ( !IsLocalCombatDriver() )
			_hostBlockAwaitingRelease = true;

		SetAuthoritativeMeleeBlockState( false, _authoritativeMeleeBlockDirection );

		if ( GameObject.Network is { Active: true } )
			RpcOwnerMeleeBlockConsumed();
		else
			_meleeBlockConsumedAwaitingRelease = true;

		if ( IsLocalCombatDriver() )
			CombatState = CombatState.PostBlocking;
	}

	[Rpc.Owner]
	void RpcOwnerMeleeBlockConsumed()
	{
		_meleeBlockConsumedAwaitingRelease = true;
		_lastSentBlockActive = false;
		CombatState = CombatState.PostBlocking;
	}

	byte GetBlockGuardDirection()
	{
		if ( IsLocalCombatDriver() && LocalBlockInputActive() )
			return NormalizeCardinalBlockDirection( _heldBlockGuardDir );

		return NormalizeCardinalBlockDirection( _authoritativeMeleeBlockDirection );
	}

	internal bool TryServerResolveBlock(
		in MeleeBlockContact contact,
		bool logRejections,
		out MeleeBlockOutcome outcome,
		out MeleeBlockRejectReason rejectReason ) =>
		MeleeBlockResolution.TryResolve( this, in contact, logRejections, out outcome, out rejectReason );

	internal bool TryServerResolveBlock(
		in MeleeBlockContact contact,
		bool logRejections,
		out MeleeBlockOutcome outcome,
		out MeleeBlockRejectReason rejectReason,
		out MeleeBlockValidationTrace trace ) =>
		MeleeBlockResolution.TryResolve( this, in contact, logRejections, out outcome, out rejectReason, out trace );

	internal void DrawRemoteBlockVisualizationIfNeeded()
	{
		if ( ShouldDrawMeleeBlockVisualization() )
			DrawMeleeBlockGuardVisualization();
	}

	void DrawMeleeBlockGuardVisualization()
	{
		if ( !ShouldDrawMeleeBlockVisualization() || !GameObject.IsValid() )
			return;

		var drawDuration = MathF.Max( 0.016f, Time.Delta * 1.5f );
		var lineColor = GetBlockVisualizationColor();

		var sampleCount = GetBlockGuardSampleCount();
		Span<Vector3> samples = stackalloc Vector3[48];
		var count = MeleeBlockPath.BuildGuardSamples( this, GetBlockGuardDirection(), sampleCount, samples );

		if ( count >= 2 )
		{
			for ( var i = 1; i < count; i++ )
				DebugOverlay.Line( samples[i - 1], samples[i], lineColor, drawDuration );

			var hitRadius = Math.Max( 0.5f, BlockSampleSphereRadius );
			var sphereColor = lineColor.WithAlpha( 0.55f );
			for ( var i = 0; i < count; i++ )
				DebugOverlay.Sphere( new Sphere( samples[i], hitRadius ), sphereColor, drawDuration );
		}

		MeleeBlockPath.EnumerateGroundArcSegments( this, GetBlockGuardDirection(), 32, ( p0, p1 ) =>
			DebugOverlay.Line( p0, p1, lineColor.WithAlpha( 0.85f ), drawDuration ) );

		if ( ShowBlockFootprintVolume )
			DrawBlockFootprintSolidFill( GetBlockGuardDirection() );
	}

	Color GetBlockVisualizationColor() => BlockDebugColor;

	void DrawBlockFootprintSolidFill( byte blockDir )
	{
		Gizmo.Draw.Color = GetBlockVisualizationColor().WithAlpha( Math.Clamp( BlockFootprintFillAlpha, 0.02f, 0.35f ) );
		MeleeBlockPath.EnumerateFootprintSolidTriangles( this, blockDir,
			( a, b, c ) => Gizmo.Draw.SolidTriangle( a, b, c ) );
	}

	protected override void DrawGizmos()
	{
		if ( !ShowBlockVisualization || !ShowBlockFootprintVolume || !GameObject.IsValid() )
			return;

		if ( !ShouldDrawMeleeBlockVisualization() )
			return;

		DrawBlockFootprintSolidFill( GetBlockGuardDirection() );
	}

	bool ShouldDrawMeleeBlockVisualization()
	{
		if ( !ShowBlockVisualization )
			return false;

		if ( IsLocalCombatDriver() )
			return LocalBlockInputActive();

		return IsAuthoritativeMeleeBlocking;
	}
}
