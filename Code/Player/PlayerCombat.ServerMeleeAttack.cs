using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Per-swing scratch for attack debug overlay (lead trail + optional rotation spokes).</summary>
struct MeleeAttackDebugDrawScratch
{
	internal Vector3 LastLeadTipWorld;
	internal bool HasLastLeadTip;
	internal float LastArcProgress01;
	/// <summary>Basis yaw/pitch and pawn position when the last arc slice was painted — new slices interpolate from here.</summary>
	internal float LastEmitBasisYaw;
	internal float LastEmitBasisPitch;
	internal Vector3 LastEmitPawnPos;
	internal bool HasLastEmitBasisYaw;
	internal HashSet<long> DrawnArcYawKeys;
	internal HashSet<int> DrawnRelativeYawStepIndices;
	internal float SwingBasisYaw;
	internal bool HasSwingBasisYaw;
	internal float YawRingProgress01;
	internal bool HasYawRingProgress;
	internal int YawRingArcSampleEnd;
	internal float AbsYawDegreesTurned;
	internal float YawTurnSign;
	internal float LastDrawBasisYaw;
	internal bool HasLastDrawBasisYaw;

	internal void Reset()
	{
		HasLastLeadTip = false;
		LastArcProgress01 = -1f;
		HasLastEmitBasisYaw = false;
		DrawnArcYawKeys?.Clear();
		DrawnRelativeYawStepIndices?.Clear();
		HasSwingBasisYaw = false;
		HasYawRingProgress = false;
		YawRingArcSampleEnd = 0;
		AbsYawDegreesTurned = 0f;
		YawTurnSign = 1f;
		HasLastDrawBasisYaw = false;
	}
}

/// <summary>
/// Host-side phased primary melee: windup → release (the damage sweep) → recovery, with the chained follow-up taking
/// over at the end of the release (PlayerCombat.Exchange.cs). Lives on <see cref="PlayerCombat"/> per Commandment #1.
/// </summary>
public partial class PlayerCombat
{
	static ushort _nextMeleeAttackInstanceId = 1;

	ServerMeleeAttackRuntime _serverMeleeAttack;
	ServerMeleeAttackRuntime _clientSwingTracePlayback;

	public MeleeHitResult LastMeleeHitResult { get; private set; }

	bool IsServerSideForMeleeAuthority() =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	public bool ServerHasActiveMeleeAttackAction => _serverMeleeAttack is not null;

	public bool ServerCanBeginMeleeAttackAction()
	{
		if ( !GameObject.IsValid() )
			return false;

		// Host simulates client-owned pawns even when they are proxies on the listen server.
		if ( GameObject.IsProxy && !Networking.IsHost )
			return false;

		if ( !IsServerSideForMeleeAuthority() )
			return false;

		if ( _serverMeleeAttack is not null )
			return false;

		// Cannot start a swing while the guard is still up, recovery / stagger / feint locks attacks, or stamina is exhausted.
		if ( IsAuthoritativeMeleeBlocking || IsAttackLocked )
			return false;

		// Broken weapons cannot swing — repair at a workbench first.
		if ( IsActiveMainHandBroken() )
			return false;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		return scene.IsValid();
	}

	public byte ResolveAttackTypeFromIntent( in AttackReleaseIntent intent ) =>
		ResolveAttackTypeFromCursorDir( intent.SwingDir );

	/// <summary>Resolves attack type from cursor cardinal using <see cref="SouthpawSwing"/> (call once when attack starts).</summary>
	public byte ResolveAttackTypeFromCursorDir( byte cursorDir ) =>
		MeleeAttackTypes.FromCursorDir( cursorDir, SouthpawSwing );

	/// <summary>Playtest logger (<see cref="PlaytestLog"/>): the host swing in flight — heavy flag and attack type; false between swings.</summary>
	internal bool TryGetServerSwingInfo( out bool heavy, out byte attackType )
	{
		var runtime = _serverMeleeAttack;
		if ( runtime is null )
		{
			heavy = false;
			attackType = 0;
			return false;
		}

		heavy = runtime.IsHeavy;
		attackType = runtime.AttackType;
		return true;
	}

	/// <summary>Base swing damage, grown by the held weapon's workbench upgrade tier (<see cref="EquipmentUpgrade"/>).</summary>
	public float GetMeleeWeaponBaseDamage() =>
		EquipmentUpgrade.ScaleStat(
			MeleeWeaponBaseDamage > 0f ? MeleeWeaponBaseDamage : AttackCombatConstants.DefaultMeleeWeaponDamage,
			GetActiveWeaponTier() );

	/// <summary>
	/// Damage of one swing: base weapon damage × the combat multiplier (× the class heavy multiplier for a heavy) × the
	/// turn damage scale (<see cref="TurnDamageScale"/>), rounded.
	/// </summary>
	public float GetMeleeDamage( bool isHeavy = false, float turnScale = 1f )
	{
		var raw = GetMeleeWeaponBaseDamage() * ComputeMeleeCombatDamageMultiplier() * Math.Max( 0f, turnScale );
		if ( isHeavy )
			raw *= GetMeleeWeaponTimings().HeavyDamageMultiplier;
		return MathF.Round( raw, MidpointRounding.AwayFromZero );
	}

	/// <summary>Host: charge the heavy's extra stamina once, when the swing turns heavy. False = cannot pay → it stays light.</summary>
	internal bool HostTrySpendHeavyStamina()
	{
		var extra = GetHeavyExtraStaminaCost();
		return extra <= 1e-4f || HostTrySpendCombatStamina( extra, afterBlock: false );
	}

	/// <summary>
	/// Host: the damage swoop just opened. Remote peers (and the host's own view of pawns it does not drive) hold
	/// the cocked pose until this cue, so an opponent never sees a light strike that then turns heavy.
	/// </summary>
	void ServerOnStrikeStarted( byte attackType )
	{
		Components.Get<PlayerAnimation>()?.PlayMeleeSwingAttack( attackType, broadcastFromHost: true,
			windupRemainingSeconds: 0f, applyLocally: !IsLocalCombatDriver() );
	}

	/// <summary>Global combat damage multiplier.</summary>
	public float ComputeMeleeCombatDamageMultiplier() => Math.Max( 0f, MeleeBaseCombatDamageMultiplier );

	public Color GetMeleeDebugColorForState( byte attackState )
	{
		if ( attackState == MeleeAttackStates.Active )
			return new Color( 1f, 0.85f, 0.1f, 0.92f );
		if ( attackState == MeleeAttackStates.Windup )
			return new Color( 0.55f, 0.55f, 0.62f, 0.45f );
		return new Color( 0.7f, 0.7f, 0.7f, 0.5f );
	}

	/// <summary>
	/// Host: a landed hit staggers the victim. A pawn's combat decides first (hard-stagger protection, its swing is cut),
	/// then its animation plays the reaction; a scav runs the same rules on its own <see cref="PlayerCombat"/> and
	/// <see cref="EntityCombat"/> shows it (guard down, sword at rest, no attack until it ends).
	/// </summary>
	public void ApplyMeleeStaggerToVictim( DamageReceiver receiver, float stagger )
	{
		if ( stagger <= 1e-4f || receiver is null || !receiver.GameObject.IsValid() )
			return;

		var vitals = CombatAuthority.ResolvePlayerVitalsForDamageReceiver( receiver );
		if ( vitals is null )
		{
			var entity = receiver.GameObject.Root?.Components.Get<EntityCombat>( FindMode.EverythingInSelfAndDescendants );
			if ( entity?.Combat is { } entityCombat && entityCombat != this
			     && entityCombat.HostTryStagger( stagger, _serverMeleeAttack is { IsHeavy: true } ) )
				entity.HostOnStaggered( stagger );
			return;
		}

		if ( !vitals.GameObject.IsValid() )
			return;

		// Resolve the victim's animation, not its combat — being hit is not equipment-dependent and
		// PlayerAnimation owns the reaction window.
		var victimAnimation = vitals.Components.Get<PlayerAnimation>()
		                      ?? vitals.GameObject.Root?.Components.Get<PlayerAnimation>( FindMode.EverythingInSelfAndDescendants );

		if ( victimAnimation is null || victimAnimation.GameObject == GameObject )
			return;

		// The victim's combat decides first: a hard stagger (heavy) inside its protection window does not stagger, and
		// a stagger interrupts the victim's own swing (PlayerCombat.Exchange.cs).
		var hard = _serverMeleeAttack is { IsHeavy: true };
		var victimCombat = victimAnimation.Components.Get<PlayerCombat>();
		if ( victimCombat is not null && victimCombat != this && !victimCombat.HostTryStagger( stagger, hard ) )
			return;

		victimAnimation.ServerBeginHitReaction( stagger );
	}

	/// <summary>
	/// Host: start one phased swing — windup (<paramref name="windupOverride"/>, the host's chain / counter / class
	/// choice, or the class windup; minus <paramref name="telegraphedSeconds"/>, the time an entity already showed its
	/// cocked pose before committing; for a remote owner's click, the trip it took to reach the host,
	/// <see cref="HostOwnerLatencySeconds"/>; 0 for the host's own pawn), then the release and the recovery.
	/// </summary>
	public void ServerStartMeleeAttackAction( in AttackReleaseIntent intent, float telegraphedSeconds, string swingLogNote,
		bool canTurnHeavy = false, float windupOverride = -1f, int comboStep = 1 )
	{
		if ( !GameObject.IsValid() || !IsServerSideForMeleeAuthority() )
			return;

		if ( GameObject.IsProxy && !Networking.IsHost )
			return;

		if ( _serverMeleeAttack is not null )
		{
			Log.Warning( $"[PlayerCombat] {GameObject.Name}: ServerStartMeleeAttackAction ignored — attack already active." );
			return;
		}

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return;

		// Drop soft pistol / prior recovery so it cannot re-apply over the new swing anim next frame.
		ServerClearCombatRecoveryForNewAttack();
		var animation = Components.Get<PlayerAnimation>();
		// Press/release already snapped the clip — don't abort before the strike is scheduled.
		if ( animation is null || !animation.HasActiveMeleeSwingPresentation )
			animation?.AbortMeleeAttackAnimClip( "new swing" );
		_serverMeleeAttack = new ServerMeleeAttackRuntime( this, intent, telegraphedSeconds, swingLogNote, canTurnHeavy: canTurnHeavy,
			windupOverride: windupOverride, comboStep: comboStep );

		var attackType = ResolveAttackTypeFromIntent( intent );
		LogMeleePhasePulse( "Swing start",
			$"type={MeleeAttackTypes.Label( attackType )} step={comboStep} telegraphed={telegraphedSeconds:0.###}s windRemaining={_serverMeleeAttack.WindupRemainingSeconds:0.###}s active={MeleeAttackPath.GetActiveDurationSeconds( this ):0.###}s seq={intent.IntentSequence}" );
		LogMeleePhaseEnter( "windup start",
			$"remaining={_serverMeleeAttack.WindupRemainingSeconds:0.###}s (class windup {GetMeleeWindupSeconds():0.###}s)" );

		EmitSwingNoiseIfPlayer();
		// The pawn this machine drives strikes on its own (predicted) clock; everyone else holds the cocked pose until
		// the host's strike cue (ServerOnStrikeStarted), which is what makes a light that turns heavy read correctly.
		animation?.PlayMeleeSwingAttack( attackType, broadcastFromHost: true,
			IsLocalCombatDriver() ? _serverMeleeAttack.WindupRemainingSeconds
				: PlayerAnimation.StrikeOnCueAfter( _serverMeleeAttack.WindupRemainingSeconds ) );

		// Don't Broadcast inside Rpc.Host handling of the attacker pawn — nested HostOnly broadcasts on a
		// host-owned object often never reach joining clients (host still runs it locally → host sees clients).
		if ( GameObject.Network is { Active: true } && Networking.IsHost && ClientMeleeSwingTraceDebug )
		{
			// Peers replay the swing on the host's windup (a chained / counter windup differs from the class one).
			_deferredSwingVisualIntent = intent with { HostWindupSeconds = _serverMeleeAttack.WindupRemainingSeconds };
			_deferSwingVisualBroadcast = true;
		}
	}

	void EmitSwingNoiseIfPlayer()
	{
		if ( Components.Get<EntityBrain>() is not null )
			return;

		if ( Components.Get<PlayerController>() is null )
			return;

		EntityNoiseBus.Emit( GameObject.Scene, GameObject.WorldPosition, EntityNoiseKind.Swing, GameObject );
	}

	AttackReleaseIntent _deferredSwingVisualIntent;
	bool _deferSwingVisualBroadcast;

	/// <summary>
	/// Host: flush deferred path-overlay broadcasts after the Rpc.Host call stack unwinds.
	/// Uses a static Broadcast so delivery isn't tied to the attacker object's ownership.
	/// </summary>
	public static void FlushDeferredSwingVisualBroadcasts( Scene scene )
	{
		if ( !Networking.IsHost || scene is null || !scene.IsValid() )
			return;

		foreach ( var pc in scene.GetAllComponents<PlayerCombat>() )
		{
			if ( pc is null || !pc.GameObject.IsValid() || !pc._deferSwingVisualBroadcast )
				continue;

			pc._deferSwingVisualBroadcast = false;
			// The replay is the slash ribbon on every other peer, debug overlay or not (the overlay flag used to gate
			// this send: no swoosh on clients, playtest 2026-11-18).
			if ( !pc.ClientMeleeSwingTraceDebug )
				continue;

			RpcStaticBroadcastMeleeSwingTraceDebug( pc.GameObject.Id, pc._deferredSwingVisualIntent );
		}
	}

	/// <summary>Host→all peers: start the local swing replay (slash ribbon, optional debug overlay) for the named attacker.</summary>
	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable | NetFlags.SendImmediate )]
	public static void RpcStaticBroadcastMeleeSwingTraceDebug( Guid attackerRootId, AttackReleaseIntent intent )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		foreach ( var pc in scene.GetAllComponents<PlayerCombat>() )
		{
			if ( pc is null || !pc.GameObject.IsValid() )
				continue;
			if ( pc.GameObject.Id != attackerRootId )
				continue;

			// The replay drives the slash ribbon on this peer, debug overlay or not.
			if ( !pc.ClientMeleeSwingTraceDebug )
				return;

			pc.StartClientMeleeSwingTracePlayback( intent );
			return;
		}
	}

	public void ServerCancelMeleeAttack()
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		if ( GameObject.IsProxy && !Networking.IsHost )
			return;

		ClearMeleeAttackBasisFromIntent();
		_serverMeleeAttack = null;
		NotifyOwnerMeleeBusyCleared( "host cancel" );
	}

	/// <summary>
	/// Unlock Attack1 on the owning client after host cancel / early teardown.
	/// Local drivers clear immediately; remote owners get <see cref="RpcOwnerMeleeBusyCleared"/>.
	/// </summary>
	void NotifyOwnerMeleeBusyCleared( string reason )
	{
		if ( IsLocalCombatDriver() )
		{
			ClearOwnerMeleeBusyExpect( reason );
			return;
		}

		if ( !Networking.IsHost || GameObject.Network is not { Active: true } )
			return;

		RpcOwnerMeleeBusyCleared( reason ?? "host cancel" );
	}

	void MaybeTickServerMeleeAttackAction()
	{
		if ( !GameObject.IsValid() )
			return;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
		{
			_serverMeleeAttack = null;
			_clientSwingTracePlayback = null;
			return;
		}

		// Visual arc replay is ticked only from TickSceneCombatVisualizations (once per frame).
		// Ticking it here too double-advanced the companion and clients saw sparse arcs.

		// Client proxies never run host sweeps; listen-server host still simulates client-owned pawns.
		if ( GameObject.IsProxy && !Networking.IsHost )
			return;

		if ( !IsServerSideForMeleeAuthority() )
			return;

		ServerTickPendingPunch();

		if ( _serverMeleeAttack is not null && !_serverMeleeAttack.Tick( scene ) )
		{
			_serverMeleeAttack = null;
			ClearMeleeAttackBasisFromIntent();
			if ( IsLocalCombatDriver() )
				ClearOwnerMeleeBusyExpect( "local swing end" );
		}

		HostPublishHeavyHold();

		// A chained follow-up takes over the moment the runtime hands over; a stored press fires once the pawn is free.
		HostTickPendingSwing();
	}

	/// <summary>
	/// True when this pawn's swing clip is actually playing on this machine. The arc overlay is a
	/// presentation of that clip, so it must never draw on its own (spam clicks used to show arcs
	/// with no animation at all).
	/// </summary>
	bool IsSwingAnimPlayingForArcOverlay()
	{
		var anim = Components.Get<PlayerAnimation>();
		if ( anim is null || !anim.IsValid() )
			return true;

		return anim.HasActiveMeleeSwingPresentation;
	}

	void StartClientMeleeSwingTracePlayback( in AttackReleaseIntent intent )
	{
		// One companion per swing: a duplicate intent (broadcast + owner backup) must not restart the fan.
		if ( _clientSwingTracePlayback is { } active && active.IntentSequence == intent.IntentSequence )
			return;

		// Listen-server host: the authority runtime already draws this swing on this machine. The HostOnly
		// broadcast also runs locally, and letting it spawn a second visual companion (starting a frame
		// late on its own clock) mid-swing painted the fan twice with an offset — visible seams.
		if ( _serverMeleeAttack is { } authority && authority.IntentSequence == intent.IntentSequence )
			return;

		var windup = intent.HostWindupSeconds > 0f ? intent.HostWindupSeconds : -1f;
		_clientSwingTracePlayback = new ServerMeleeAttackRuntime( this, intent, 0f, "client-trace", visualOnly: true, windupOverride: windup );
	}

	/// <summary>Local owner ticks remote swing overlays — proxy pawns may not receive OnUpdate.</summary>
	internal void TickClientSwingTracePlaybackOnly( Scene scene )
	{
		if ( _clientSwingTracePlayback is null )
			return;

		if ( _clientSwingTracePlayback.Tick( scene ) )
			return;

		_clientSwingTracePlayback = null;
		// The replay pinned the facing basis for its windup, release and recovery; the host's own runtime keeps its own.
		if ( _serverMeleeAttack is null )
			ClearMeleeAttackBasisFromIntent();
	}

	/// <summary>
	/// Advances client-only swing path overlays (and remote block/windup draws) for every pawn.
	/// Called from <see cref="CombatAuthority"/> so proxy pawns still animate when their OnUpdate is skipped.
	/// Host also ticks authoritative melee on remote-owned (proxy) pawns when <paramref name="driveHostProxyAuthority"/> is true.
	/// </summary>
	public static void TickSceneCombatVisualizations( Scene scene, bool driveHostProxyAuthority = false )
	{
		if ( scene is null || !scene.IsValid() )
			return;

		foreach ( var pc in scene.GetAllComponents<PlayerCombat>() )
		{
			if ( pc is null || !pc.GameObject.IsValid() )
				continue;

			var isHostProxy = Networking.IsHost && pc.GameObject.IsProxy;

			// Listen-server: client-owned pawns are proxies — OnUpdate skips authority timers here.
			// Without these ticks, NetworkedCombatRecovery* never counts down → stuck UseAnimGraph=false poses.
			if ( driveHostProxyAuthority && isHostProxy )
			{
				pc.MaybeTickServerMeleeAttackAction();
				pc.ServerTickCombatRecovery();
				pc.TickLocalCombatRecoveryPresentation();
			}

			// Every peer ticks visual arc replay once per pawn. Host proxies already ran MaybeTick
			// above (authority only — playback is not ticked there anymore).
			pc.TickClientSwingTracePlaybackOnly( scene );
			pc.TickSwingVfx();

			if ( pc.IsLocalCombatDriver() )
				continue;

			pc.DrawRemoteBlockVisualizationIfNeeded();
			pc.DrawWindupTelegraphIfNeeded();
		}
	}

	void TickAllRemoteCombatVisualizationsInScene()
	{
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		TickSceneCombatVisualizations( scene );
	}

	/// <summary>Core attack-path sampling every frame; optional overlay when <see cref="MeleeDebugDrawEnabled"/>.</summary>
	void AdvanceAttackPath(
		byte attackType,
		float activeProgress01,
		byte attackState,
		float currentBasisYaw,
		ref MeleeAttackDebugDrawScratch scratch,
		bool allowDebugOverlay = true )
	{
		if ( !GameObject.IsValid() )
			return;

		if ( !ServerHasActiveMeleeAttackAction && _clientSwingTracePlayback is null )
			return;

		if ( !IsServerSideForMeleeAuthority() && _clientSwingTracePlayback is null )
			return;

		activeProgress01 = Math.Clamp( activeProgress01, 0f, 1f );
		if ( attackState == MeleeAttackStates.Recovery )
			return;

		// Slash ribbon: exactly where this frame's damage sweep went (every pawn, every peer running the swing).
		if ( MeleeAttackStates.DealsDamage( attackState ) && activeProgress01 > 1e-5f )
			FeedSwingVfxFromDamageArc( attackType, activeProgress01 );

		// Arcs are bound to the swing animation on this peer: no clip playing here means no arc drawn here.
		// (Damage sweeps below still run on the host — authority never depends on presentation.)
		var drawOverlay = allowDebugOverlay && MeleeDebugDrawEnabled && IsSwingAnimPlayingForArcOverlay();
		var overlayDuration = GetMeleeDebugOverlayDrawDuration();
		var degreeStep = GetMeleeAttackArcDegreeStep();

		if ( activeProgress01 > 1e-5f && MeleeAttackStates.DealsDamage( attackState ) )
		{
			UpdateAttackPathSamples( attackType, currentBasisYaw, activeProgress01, attackState, degreeStep,
				drawOverlay, overlayDuration, ref scratch );
		}

		if ( !drawOverlay )
			return;

		var livePhaseColor = GetMeleeDebugColorForState( attackState );
		SampleServerMeleeBladeWorld( attackType, activeProgress01, out var liveTip, out _ );
		var weaponRange = MeleeAttackPath.GetAttackRange( this, attackType );
		var maxTrailLen = Math.Max( weaponRange * 1.35f, 24f );

		if ( scratch.HasLastLeadTip )
		{
			var trailDelta = liveTip - scratch.LastLeadTipWorld;
			var trailLen = trailDelta.Length;
			if ( trailLen >= 4f && trailLen <= maxTrailLen )
			{
				var trailColor = livePhaseColor.WithAlpha( 0.22f );
				DebugOverlay.Line( scratch.LastLeadTipWorld, liveTip, trailColor, overlayDuration );
			}
			else if ( trailLen > maxTrailLen )
				scratch.HasLastLeadTip = false;
		}

		scratch.LastLeadTipWorld = liveTip;
		scratch.HasLastLeadTip = true;
	}

	/// <summary>
	/// Time-phase debug rays: while in Early/Active/Late windows, emit the current swing sample at the current yaw.
	/// Turning or moving during a phase paints that phase's color coverage over time (no per-tick mini-fans).
	/// </summary>
	void UpdateAttackPathSamples(
		byte attackType,
		float currentBasisYaw,
		float activeProgress01,
		byte attackStateForDraw,
		float degreeStep,
		bool drawOverlay,
		float overlayDuration,
		ref MeleeAttackDebugDrawScratch scratch )
	{
		scratch.DrawnArcYawKeys ??= new HashSet<long>();
		var drawnArcYawKeys = scratch.DrawnArcYawKeys;
		var hitRadius = Math.Max( 2f, MeleeHitVolumeThickness );
		var drawSpheres = MeleeDebugDrawSamplePointsEnabled;
		var sampleCount = MeleeAttackPath.GetArcPathSampleCount( this, attackType, degreeStep );
		var maxRotationSpokes = MeleeAttackPath.GetRotationDebugSpokeCount( degreeStep );

		if ( drawOverlay )
		{
			var currentBasisPitch = GetMeleeSwingPitchDegrees( attackType );
			var currentPawnPos = GameObject.WorldPosition;
			var hadPrev = scratch.LastArcProgress01 >= 0f && scratch.HasLastEmitBasisYaw;
			var lastProgress = Math.Max( 0f, scratch.LastArcProgress01 );
			var prevYaw = hadPrev ? scratch.LastEmitBasisYaw : currentBasisYaw;
			var prevPitch = hadPrev ? scratch.LastEmitBasisPitch : currentBasisPitch;
			var prevPawnPos = hadPrev ? scratch.LastEmitPawnPos : currentPawnPos;
			var yawSpan = hadPrev ? NormalizeDegreesDelta( currentBasisYaw - prevYaw ) : 0f;
			var pitchSpan = hadPrev ? currentBasisPitch - prevPitch : 0f;
			var pawnTravel = (currentPawnPos - prevPawnPos).Length;
			var progressSpan = Math.Max( 0f, activeProgress01 - lastProgress );

			// The active window is only a few frames long, so each frame reveals one large contiguous
			// slice of the fan. On the local pawn the swing pose is LIVE — camera yaw/pitch move
			// mid-drag and the pawn itself translates (jumping / falling / strafing) — so painting a
			// slice at a single frame's pose left seams wherever the pose jumped between frames.
			// Subdivide the slice by whichever moved most (arc progress, yaw, pitch, or pawn travel)
			// and interpolate the full pose along it — the same between-frames interpolation the
			// damage sweep already does — so the painted fan cannot seam.
			var arcSteps = (int)MathF.Ceiling( progressSpan * Math.Max( 1, sampleCount - 1 ) );
			var basisSteps = (int)MathF.Ceiling( MathF.Max( MathF.Abs( yawSpan ), MathF.Abs( pitchSpan ) ) / degreeStep );
			var travelSteps = (int)MathF.Ceiling( pawnTravel / 4f );
			var stepCount = Math.Max( arcSteps, Math.Max( basisSteps, travelSteps ) );

			if ( stepCount > 0 || !hadPrev )
			{
				stepCount = Math.Max( 1, stepCount );
				for ( var step = hadPrev ? 1 : 0; step <= stepCount; step++ )
				{
					var f = step / (float)stepCount;
					var arcProgress = lastProgress + progressSpan * f;
					var sampleYaw = prevYaw + yawSpan * f;
					var samplePitch = prevPitch + pitchSpan * f;
					var samplePos = Vector3.Lerp( prevPawnPos, currentPawnPos, f );
					var key = (long)HashCode.Combine(
						(int)MathF.Round( arcProgress * Math.Max( 1, sampleCount - 1 ) ),
						MeleeAttackPath.QuantizeYawDegrees( sampleYaw, degreeStep ),
						(int)MathF.Floor( samplePos.x / 8f ),
						(int)MathF.Floor( samplePos.y / 8f ),
						(int)MathF.Floor( samplePos.z / 8f ) );
					if ( !drawnArcYawKeys.Add( key ) )
						continue;

					EmitMeleeDebugPathRay( attackType, sampleYaw, arcProgress, MeleeAttackStates.Active, drawOverlay,
						overlayDuration, hitRadius, drawSpheres, samplePitch, samplePos - currentPawnPos );
				}
			}

			// Bookkeeping advances only when a slice was actually painted — a frame where the overlay
			// gate flickers off no longer swallows its span forever (that painted permanent slits);
			// the next painting frame catches the whole span up instead.
			scratch.LastEmitBasisYaw = currentBasisYaw;
			scratch.LastEmitBasisPitch = currentBasisPitch;
			scratch.LastEmitPawnPos = currentPawnPos;
			scratch.HasLastEmitBasisYaw = true;
			scratch.LastArcProgress01 = activeProgress01;
		}

		if ( !MeleeDebugDrawRotationSpokes )
			return;

		scratch.DrawnRelativeYawStepIndices ??= new HashSet<int>();
		var drawnYawSteps = scratch.DrawnRelativeYawStepIndices;

		if ( !scratch.HasSwingBasisYaw )
		{
			scratch.SwingBasisYaw = currentBasisYaw;
			scratch.HasSwingBasisYaw = true;
		}

		if ( !scratch.HasYawRingProgress )
		{
			scratch.YawRingProgress01 = activeProgress01;
			scratch.HasYawRingProgress = true;
		}

		var sampleCountForRing = sampleCount;
		var currentArcEndForRing = MeleeAttackPath.RevealedArcSampleExclusiveEnd( activeProgress01, sampleCountForRing );
		if ( currentArcEndForRing > scratch.YawRingArcSampleEnd )
		{
			scratch.YawRingArcSampleEnd = currentArcEndForRing;
			scratch.YawRingProgress01 = MeleeAttackPath.ArcSampleIndexToProgress01( sampleCountForRing,
				Math.Max( 0, currentArcEndForRing - 1 ) );
		}

		if ( scratch.HasLastDrawBasisYaw )
		{
			var yawDelta = Angles.NormalizeAngle( currentBasisYaw - scratch.LastDrawBasisYaw );
			scratch.AbsYawDegreesTurned += MathF.Abs( yawDelta );
			if ( MathF.Abs( yawDelta ) > 1e-4f )
				scratch.YawTurnSign = MathF.Sign( yawDelta );
		}

		var rotationSpokesOwed = Math.Min( maxRotationSpokes,
			(int)MathF.Floor( scratch.AbsYawDegreesTurned / degreeStep ) );

		for ( var stepIndex = 0; stepIndex < rotationSpokesOwed; stepIndex++ )
		{
			if ( !drawnYawSteps.Add( stepIndex ) )
				continue;

			var spokeYaw = Angles.NormalizeAngle( scratch.SwingBasisYaw + scratch.YawTurnSign * stepIndex * degreeStep );
			EmitMeleeDebugPathRay( attackType, spokeYaw, scratch.YawRingProgress01, attackStateForDraw, drawOverlay,
				overlayDuration, hitRadius, drawSpheres );
		}

		scratch.LastDrawBasisYaw = currentBasisYaw;
		scratch.HasLastDrawBasisYaw = true;
	}

	internal void DrawWindupTelegraphIfNeeded()
	{
		if ( !ShowMeleeAttackWindupTelegraph || !_windupTelegraphActive )
			return;

		DrawMeleeAttackWindupTelegraph( _windupTelegraphAttackType, _windupTelegraphBasisYaw );
	}

	void TickWindupTelegraphNetworkState()
	{
		if ( !ShowMeleeAttackWindupTelegraph )
		{
			if ( _windupTelegraphActive )
				PublishWindupTelegraphState( false, 0, 0f );
			return;
		}

		if ( !TryComputeLocalWindupTelegraph( out var attackType, out var basisYaw ) )
		{
			if ( _windupTelegraphActive )
				PublishWindupTelegraphState( false, 0, 0f );
			return;
		}

		PublishWindupTelegraphState( true, attackType, basisYaw );
	}

	/// <summary>The swing is in its windup (cocked, hanging) on this machine: the client replay or the host runtime.</summary>
	bool TryComputeLocalWindupTelegraph( out byte attackType, out float basisYaw )
	{
		attackType = 0;
		basisYaw = 0f;

		if ( _clientSwingTracePlayback?.IsInWindupPhase == true )
		{
			attackType = _clientSwingTracePlayback.AttackType;
			basisYaw = GetMeleeCombatBasisYaw( attackType );
			return true;
		}

		if ( _serverMeleeAttack?.IsInWindupPhase != true )
			return false;

		attackType = _serverMeleeAttack.AttackType;
		basisYaw = GetMeleeCombatBasisYaw( attackType );
		return true;
	}

	void PublishWindupTelegraphState( bool active, byte attackType, float basisYaw )
	{
		if ( active == _lastSentWindupTelegraphActive && _lastSentWindupTelegraphValid
		     && attackType == _lastSentWindupTelegraphAttackType
		     && ( !active || MathF.Abs( basisYaw - _lastSentWindupTelegraphBasisYaw ) < 0.4f ) )
			return;

		_lastSentWindupTelegraphActive = active;
		_lastSentWindupTelegraphAttackType = attackType;
		_lastSentWindupTelegraphBasisYaw = basisYaw;
		_lastSentWindupTelegraphValid = true;
		ApplyWindupTelegraphState( active, attackType, basisYaw );

		if ( GameObject.Network is not { Active: true } )
			return;

		if ( Networking.IsHost )
			BroadcastWindupTelegraphIfHost( active, attackType, basisYaw );
		else
			RpcSubmitWindupTelegraph( active, attackType, basisYaw );
	}

	void ApplyWindupTelegraphState( bool active, byte attackType, float basisYaw )
	{
		_windupTelegraphActive = active;
		_windupTelegraphAttackType = attackType;
		_windupTelegraphBasisYaw = basisYaw;
	}

	void BroadcastWindupTelegraphIfHost( bool active, byte attackType, float basisYaw )
	{
		var changed = active != _lastBroadcastWindupTelegraphActive
		              || attackType != _lastBroadcastWindupTelegraphAttackType
		              || !_lastBroadcastWindupTelegraphValid
		              || ( active && MathF.Abs( basisYaw - _lastBroadcastWindupTelegraphBasisYaw ) >= 0.4f );

		if ( !changed )
			return;

		_lastBroadcastWindupTelegraphActive = active;
		_lastBroadcastWindupTelegraphAttackType = attackType;
		_lastBroadcastWindupTelegraphBasisYaw = basisYaw;
		_lastBroadcastWindupTelegraphValid = true;
		RpcBroadcastWindupTelegraph( active, attackType, basisYaw );
	}

	[Rpc.Host]
	void RpcSubmitWindupTelegraph( bool active, byte attackType, float basisYaw )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		ApplyWindupTelegraphState( active, attackType, basisYaw );
		BroadcastWindupTelegraphIfHost( active, attackType, basisYaw );
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	void RpcBroadcastWindupTelegraph( bool active, byte attackType, float basisYaw )
	{
		if ( Networking.IsHost )
			return;

		ApplyWindupTelegraphState( active, attackType, basisYaw );
	}

	/// <summary>Thick black line at the first attack-path sample until the coloured sweep rays begin.</summary>
	void DrawMeleeAttackWindupTelegraph( byte attackType, float basisYaw )
	{
		if ( !GameObject.IsValid() )
			return;

		var basis = GetMeleeCombatBasisRotationForYaw( attackType, basisYaw );
		var origin = MeleeAttackPath.GetSwingPivotWorld( GameObject, this, attackType, basis );
		MeleeAttackPath.EvaluateWorldBlade( GameObject, this, attackType, 0f, basis, out var tip, out _ );
		var drawFor = MathF.Max( 0.03f, Time.Delta * 1.5f );
		var color = Color.Black.WithAlpha( 0.92f );
		var thickness = Math.Max( 2f, MeleeWindupTelegraphThickness );
		DrawThickDebugLine( origin, tip, color, drawFor, thickness );
		DrawThickDebugLineSphere( tip, thickness * 0.45f, color.WithAlpha( 0.85f ), drawFor );
	}

	void DrawThickDebugLine( Vector3 start, Vector3 end, Color color, float duration, float thickness )
	{
		DebugOverlay.Line( start, end, color, duration );

		var delta = end - start;
		if ( delta.LengthSquared < 1e-6f )
			return;

		var dir = delta.Normal;
		var side = Vector3.Cross( dir, Vector3.Up );
		if ( side.LengthSquared < 1e-6f )
			side = Vector3.Cross( dir, Vector3.Right );
		side = side.Normal;

		var half = thickness * 0.5f;
		DebugOverlay.Line( start + side * half, end + side * half, color, duration );
		DebugOverlay.Line( start - side * half, end - side * half, color, duration );
	}

	void DrawThickDebugLineSphere( Vector3 center, float radius, Color color, float duration ) =>
		DebugOverlay.Sphere( new Sphere( center, Math.Max( 1f, radius ) ), color, duration );

	void EmitMeleeDebugPathRay(
		byte attackType,
		float basisYaw,
		float arcProgress01,
		byte attackStateForDraw,
		bool drawOverlay,
		float overlayDuration,
		float hitRadius,
		bool drawSpheres,
		float? basisPitchDegrees = null,
		Vector3 worldOffset = default )
	{
		if ( !drawOverlay )
			return;

		// Interpolated slice painting passes its own pitch — reading the live cursor pitch here made
		// consecutive slices sit on differently tilted planes (visible seams on the local pawn).
		var basis = basisPitchDegrees is { } pitch
			? new Angles( ClampMeleeSwingPitchDegrees( pitch ), basisYaw, 0f ).ToRotation()
			: GetMeleeCombatBasisRotationForYaw( attackType, basisYaw );
		var origin = MeleeAttackPath.GetSwingPivotWorld( GameObject, this, attackType, basis );
		var spokeColor = GetMeleeDebugColorForState( attackStateForDraw ).WithAlpha( 0.42f );
		MeleeAttackPath.EvaluateWorldBlade( GameObject, this, attackType, arcProgress01, basis, out var tip, out _ );

		// The blade transform anchors at the pawn's CURRENT position; interpolated slice painting shifts
		// the ray back toward where the pawn was mid-frame (jumping / strafing between frames).
		origin += worldOffset;
		tip += worldOffset;
		var drawTip = tip;
		if ( TryGetDebugGuardLineClipPoint( origin, tip, out var guardClipPoint ) )
			drawTip = guardClipPoint;

		DebugOverlay.Line( origin, drawTip, spokeColor, overlayDuration );
		if ( drawSpheres )
			DebugOverlay.Sphere( new Sphere( drawTip, hitRadius ), spokeColor.WithAlpha( 0.35f ), overlayDuration );
	}

	/// <summary>
	/// Debug only: clip this swing sample at the guard polyline (footprint is combat-only, not used here).
	/// </summary>
	bool TryGetDebugGuardLineClipPoint( Vector3 origin, Vector3 tip, out Vector3 hitPoint )
	{
		hitPoint = tip;

		if ( !GameObject.IsValid() )
			return false;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		var thickness = Math.Max( 2f, MeleeHitVolumeThickness );
		var bestDist = float.MaxValue;
		var found = false;

		foreach ( var defender in scene.GetAllComponents<PlayerCombat>() )
		{
			if ( defender is null || defender == this || !defender.Enabled || !defender.GameObject.IsValid() )
				continue;
			if ( !defender.IsAuthoritativeMeleeBlocking )
				continue;
			if ( !MeleeBlockPath.TryRaycastGuardLine( defender, origin, tip, float.MaxValue, thickness,
				     out var guardDist, out var guardPos ) )
				continue;
			if ( guardDist >= bestDist )
				continue;

			bestDist = guardDist;
			hitPoint = guardPos;
			found = true;
		}

		return found;
	}

	void SampleServerMeleeBladeWorld( byte attackType, float arcProgress01, out Vector3 tip, out Vector3 heel )
	{
		if ( MeleeBladeTip.IsValid() )
		{
			tip = MeleeBladeTip.WorldPosition;
			var basis = GetMeleeCombatBasisRotation( attackType );
			heel = MeleeBladeHeel.IsValid()
				? MeleeBladeHeel.WorldPosition
				: tip - basis.Forward * Math.Max( 8f, GetMeleeAttackRangeUnits( attackType ) * MeleeBladeHeelFraction );
			return;
		}

		MeleeAttackPath.EvaluateWorldBlade( GameObject, this, attackType, arcProgress01, out tip, out heel );
	}

	sealed class ServerMeleeAttackRuntime
	{
		readonly PlayerCombat _pc;
		/// <summary>Mutable for the heavy morph only (<see cref="MorphTo"/>): the side the swing comes from.</summary>
		AttackReleaseIntent _intent;
		byte _attackType;
		readonly ushort _sequence;
		readonly ushort _instanceId;
		float _windup;
		/// <summary>Light windup before any heavy hold (the feint cutoff and the chamber timing measure against it).</summary>
		readonly float _lightWindup;
		float _active;
		readonly float _radius;
		readonly float _substep;
		readonly string _swingNote;
		readonly bool _logHits;
		readonly bool _visualOnly;
		/// <summary>Clicked swings (players, scavs): still held when the light windup ends → heavy (the training dummy never turns heavy).</summary>
		readonly bool _canTurnHeavy;
		readonly float _heavyWindup;
		bool _heavyDecided;
		bool _isHeavy;
		/// <summary>Heavy, button still down on the host's view: the cocked pose hangs until the release (never by itself).</summary>
		bool _heavyHolding;
		/// <summary>Heavy morph: elapsed time of the last side change, how long the weapon takes to settle there, and how long until it is a heavy again.</summary>
		float _morphedElapsed = float.NegativeInfinity;
		float _morphSettle;
		float _morphRearm;
		bool HasMorphed => !float.IsNegativeInfinity( _morphedElapsed );
		/// <summary>Elapsed time from which a held heavy is (again) a heavy: the heavy windup, or the re-arm after the last morph.</summary>
		float HeavyArmedElapsed => HasMorphed ? _morphedElapsed + _morphRearm : _heavyWindup;
		/// <summary>Combo step of this swing (1 from neutral; the last chainable step recovers for the combo end).</summary>
		readonly int _comboStep;
		bool _strikeCueSent;
		bool _heavyReadySent;
		/// <summary>Turn damage: the body's unwrapped yaw and the elapsed time when the release opened (NaN: no body tracker).</summary>
		float _releaseBodyYaw0 = float.NaN;
		float _releaseStartElapsed;
		readonly bool _allowMultiple;
		readonly int _maxTargets;
		bool _loggedAttackPhase;

		readonly HashSet<Guid> _hitVictims = new();
		/// <summary>Guard side per defender (GameObject id), latched at the windup start (<see cref="LatchIncomingSides"/>).</summary>
		readonly Dictionary<Guid, byte> _latchedSides = new();
		/// <summary>Pawns this close when the windup starts get a latched side (the red marker's own range).</summary>
		const float IncomingLatchRangeMeters = 4.5f;

		double _startedAtSandbox;
		Vector3 _prevTip;
		Vector3 _prevHeel;
		bool _havePrevSample;
		float _totalDamageDealt;
		Guid _firstHitTargetId;
		bool _anyHit;
		bool _wasBlocked;
		bool _wasChambered;
		bool _completionSent;
		int _targetsHitCount;
		bool _stopHitValidation;
		/// <summary>Elapsed time the release ended (NaN while it has not); recovery runs from there.</summary>
		float _releaseEndedElapsed = float.NaN;
		float _recoverySeconds;
		MeleeAttackDebugDrawScratch _debugDrawScratch;

		internal ServerMeleeAttackRuntime(
			PlayerCombat pc,
			in AttackReleaseIntent intent,
			float telegraphedSeconds,
			string swingNote,
			bool visualOnly = false,
			bool canTurnHeavy = false,
			float windupOverride = -1f,
			int comboStep = 1 )
		{
			_pc = pc;
			_intent = intent;
			_attackType = pc.ResolveAttackTypeFromIntent( intent );
			_sequence = intent.IntentSequence;
			_instanceId = _nextMeleeAttackInstanceId++;
			_visualOnly = visualOnly;
			var t = pc.GetMeleeWeaponTimings();
			// Windup: the host's choice (chain / counter / class) when given; entities that already showed their cocked
			// pose before committing play only the rest of it.
			var classWindup = windupOverride >= 0f ? windupOverride : t.WindupSeconds;
			_windup = Math.Max( 0f, classWindup - Math.Max( 0f, telegraphedSeconds ) );
			_lightWindup = _windup;
			_active = MeleeAttackPath.GetActiveDurationSeconds( pc );
			_canTurnHeavy = canTurnHeavy && !visualOnly;
			_heavyWindup = Math.Max( _windup, t.HeavyWindupSeconds - Math.Max( 0f, telegraphedSeconds ) );
			_comboStep = Math.Max( 1, comboStep );
			_radius = Math.Max( 2f, pc.MeleeHitVolumeThickness );
			_substep = Math.Max( 4f, pc.MeleeSweepSubstepLength );
			_swingNote = swingNote;
			_logHits = pc.LogMeleeSweepHitsToConsole;
			_allowMultiple = pc.MeleeAllowMultipleHitsPerAttack;
			_maxTargets = _allowMultiple ? Math.Max( 1, pc.MeleeMaxTargetsHit ) : 1;
			_startedAtSandbox = Time.NowDouble;
			_debugDrawScratch.Reset();
			_awaitStrikeShown = visualOnly;
			_pc.PushMeleeAttackBasisFromIntent( intent, _attackType );
			LatchIncomingSides();
		}

		/// <summary>
		/// The guard side each pawn in range must take against this swing, decided as its windup starts (and again on a
		/// heavy morph) and never re-read for this swing (Mark 2026-11-17: the red arrow "frequently flips to the other side
		/// last second" - the defender's mouse flick to pick a guard crossed the 15° flank line; he chose "lock side per
		/// swing"). The host's block check, the scav AI and the red marker all read it (<see cref="PlayerCombat.TryGetLatchedIncomingSide"/>).
		/// Once per swing over the pawns within the marker range - no per-frame work.
		/// </summary>
		void LatchIncomingSides()
		{
			_latchedSides.Clear();
			// A client's replay never answers with a guess of its own: the host's side is sent to each defender.
			var attacker = _pc.GameObject;
			if ( _visualOnly || !attacker.IsValid() || attacker.Scene is not { } scene )
				return;

			var range = IncomingLatchRangeMeters * _pc.GetPawnUnitsPerMeter();
			var rangeSq = range * range;
			foreach ( var defender in scene.GetAllComponents<PlayerCombat>() )
			{
				if ( defender == _pc || !defender.IsValid() || !defender.GameObject.IsValid() )
					continue;

				if ( (defender.GameObject.WorldPosition - attacker.WorldPosition).WithZ( 0f ).LengthSquared > rangeSq )
					continue;

				var side = MeleeBlockResolution.ResolveIncomingSide( defender, attacker, _attackType );
				_latchedSides[defender.GameObject.Id] = side;
				defender.HostSendIncomingSide( attacker.Id, _attackType, side );
			}
		}

		internal bool TryGetLatchedSide( PlayerCombat defender, out byte side )
		{
			side = SwingDirs.Up;
			return defender is not null && defender.GameObject.IsValid() && _latchedSides.TryGetValue( defender.GameObject.Id, out side );
		}

		internal byte AttackType => _attackType;

		/// <summary>
		/// Replay only: the release opens when this machine shows the strike (<see cref="StrikeShown"/>), not on a clock
		/// of its own - the ribbon belongs to the swing clip, and a held heavy, a morph or a chained windup all strike
		/// at a time only the clip's cue knows.
		/// </summary>
		bool _awaitStrikeShown;

		/// <summary>This machine's swing clip just fired its strike: the replay's release (the slash ribbon) starts now.</summary>
		internal void StrikeShown( byte attackType )
		{
			if ( !_visualOnly || !_awaitStrikeShown )
				return;

			_awaitStrikeShown = false;
			_attackType = attackType;
			_windup = Elapsed;
		}

		/// <summary>
		/// Heavy morph (Mark 2026-11-16): a held heavy moves to another side - the damage arc, the incoming side the
		/// defender must guard and the strike clip all follow; the strike waits <paramref name="settleSeconds"/> after the
		/// change so it always starts from the new cocked pose, and it is a heavy again only <paramref name="rearmSeconds"/>
		/// after the change (the glint plays then; let go sooner and it strikes as a light - Mark 2026-11-17).
		/// </summary>
		internal void MorphTo( byte swingDir, byte attackType, float settleSeconds, float rearmSeconds )
		{
			_attackType = attackType;
			_intent = _intent with { SwingDir = swingDir, AttackType = attackType };
			_morphedElapsed = Elapsed;
			_morphSettle = Math.Max( 0f, settleSeconds );
			_morphRearm = Math.Max( 0f, rearmSeconds );
			_heavyReadySent = false;
			// A morph is a new side decision: the guard side is read again, once, for the new swing side.
			LatchIncomingSides();
		}

		internal ushort IntentSequence => _intent.IntentSequence;

		internal double StartedAtSandbox => _startedAtSandbox;

		internal int ComboStep => _comboStep;

		/// <summary>Host time the recovery ends; NaN until the release has ended.</summary>
		internal double RecoveryEndSandbox =>
			float.IsNaN( _releaseEndedElapsed ) ? double.NaN : _startedAtSandbox + _releaseEndedElapsed + _recoverySeconds;

		/// <summary>Windup left to play before the release opens (as scheduled at the start).</summary>
		internal float WindupRemainingSeconds => _windup;

		float Elapsed => (float)( Time.NowDouble - _startedAtSandbox );

		internal bool IsInWindupPhase
		{
			get
			{
				// A heavy-capable swing is still winding up until the heavy decision is made at the light windup's end.
				var elapsed = Elapsed;
				return elapsed < _windup || (_canTurnHeavy && !_heavyDecided) || _heavyHolding;
			}
		}

		internal bool IsHeavy => _isHeavy;

		/// <summary>Still winding up with the attack button down on the host's view (a clicked swing nobody let go of yet).</summary>
		internal bool IsWindupHeld =>
			!_visualOnly && _canTurnHeavy && IsInWindupPhase && !_pc.HostWasPrimaryAttackReleased( _sequence );

		internal bool IsHeavyHolding => _heavyHolding;

		/// <summary>The heavy morph may move this swing: a held heavy, or a clicked swing not yet past its heavy decision.</summary>
		internal bool CanMorph => !_visualOnly && _canTurnHeavy && (_heavyHolding || !_heavyDecided);

		internal MeleeSwingPhase Phase
		{
			get
			{
				if ( IsInWindupPhase )
					return MeleeSwingPhase.Windup;
				return float.IsNaN( _releaseEndedElapsed ) ? MeleeSwingPhase.Release : MeleeSwingPhase.Recovery;
			}
		}

		/// <summary>
		/// Host time the release starts / started; null while a heavy is still held (its strike waits for the release) or
		/// the release is already over. The chamber compares it with an incoming blade's contact time.
		/// </summary>
		internal double? ReleaseStartSandbox
		{
			get
			{
				if ( _heavyHolding || !float.IsNaN( _releaseEndedElapsed ) )
					return null;
				return _startedAtSandbox + _windup;
			}
		}

		/// <summary>
		/// The guard can feint this swing: before <paramref name="cutoffFraction"/> of the light windup (+ the round-trip
		/// slack), or at any point before it turns heavy while the attack button is still <paramref name="held"/> (Mark
		/// 2026-11-16: holding a light that has yet to go heavy, the guard ditches the attack and goes into block; the slack
		/// covers a guard press the owner made just before its heavy decision). A heavy cannot be feinted - the guard
		/// button starts the morph instead.
		/// </summary>
		internal bool CanFeint( float cutoffFraction, float slackSeconds, bool held )
		{
			if ( _visualOnly || !IsInWindupPhase )
				return false;

			var limit = held && _canTurnHeavy ? _lightWindup : cutoffFraction * _lightWindup;
			return Elapsed < limit + slackSeconds;
		}

		/// <summary>
		/// Turn damage scale for a hit now (<see cref="PlayerCombat.TurnDamageScale"/>): the body's average turn speed
		/// over the release so far (its turn this frame in the release's first frame).
		/// </summary>
		float TurnScaleSoFar( float elapsed )
		{
			if ( float.IsNaN( _releaseBodyYaw0 ) )
				return 1f;

			var span = elapsed - _releaseStartElapsed;
			float rate;
			if ( span > 0.02f )
				rate = (_pc.SwingBodyYawUnwrapped() - _releaseBodyYaw0) / span;
			else
				rate = _pc.Components.Get<PlayerMovement>( FindMode.EverythingInSelf )?.BodyTurnRateDegreesPerSecond ?? 0f;

			_pc.LastTurnDamageScale = _pc.TurnDamageScale( _attackType, rate );
			return _pc.LastTurnDamageScale;
		}

		internal float GetActiveArcProgress01()
		{
			var elapsed = Elapsed;
			var windEnd = _windup;
			var activeEnd = windEnd + _active;
			if ( elapsed < windEnd )
				return 0f;
			if ( elapsed >= activeEnd )
				return 1f;
			var activeLen = Math.Max( 1e-4f, _active );
			return Math.Clamp( (elapsed - windEnd) / activeLen, 0f, 1f );
		}

		internal bool Tick( Scene scene )
		{
			if ( !_pc.IsValid() )
			{
				TrySendCompletion();
				return false;
			}

			var attacker = _pc.GameObject;
			if ( !attacker.IsValid() )
			{
				TrySendCompletion();
				return false;
			}

			var elapsed = Elapsed;
			var t = _pc.GetMeleeWeaponTimings();

			// End of the light windup: button still held (no release reached the host) and the heavy's extra stamina
			// paid → heavy: the cocked pose keeps hanging to the heavy windup, then the heavy release hits harder.
			if ( !_heavyDecided && elapsed >= _windup )
			{
				_heavyDecided = true;
				if ( _canTurnHeavy && !_pc.HostWasPrimaryAttackReleased( _sequence ) && _pc.HostTrySpendHeavyStamina() )
				{
					_isHeavy = true;
					_heavyHolding = true;
					_active = Math.Max( 0.04f, t.HeavyActiveSeconds );
					_pc.LogMeleePhasePulse( "Heavy", $"held past the windup — strikes on release (≥ {_heavyWindup:0.###}s), x{t.HeavyDamageMultiplier:0.##} damage" );
				}
			}

			// Heavy: hang in the cocked pose while the button is down - never released by itself (Mark 2026-11-16); the
			// release is the strike, never earlier than the heavy windup nor before a morphed weapon settled on its new
			// side. Held past the drain point the charge costs stamina; an empty bar lets go.
			if ( _heavyHolding )
			{
				var drainAfter = Math.Max( 0f, _pc.HeavyHoldDrainAfterSeconds );
				var outOfStamina = !_visualOnly && elapsed > drainAfter && _pc.HeavyHoldDrainPerSecond > 1e-4f
				                   && !_pc.HostTrySpendCombatStamina( _pc.HeavyHoldDrainPerSecond * Time.Delta, afterBlock: false );

				if ( _pc.HostWasPrimaryAttackReleased( _sequence ) || outOfStamina )
				{
					_heavyHolding = false;
					if ( HasMorphed )
					{
						// Morphed: strikes once the weapon settled on its new side - a heavy only if it re-armed.
						if ( elapsed < HeavyArmedElapsed )
						{
							_isHeavy = false;
							_active = MeleeAttackPath.GetActiveDurationSeconds( _pc );
							_pc.LogMeleePhasePulse( "Heavy morph", $"let go {elapsed - _morphedElapsed:0.###}s after the side change (re-arm {_morphRearm:0.###}s) — strikes as a light" );
						}
						_windup = Math.Max( elapsed, _morphedElapsed + _morphSettle );
					}
					else
						_windup = Math.Max( _heavyWindup, elapsed );
				}
				else
					_windup = Math.Max( _heavyWindup, elapsed + 0.05f );
			}

			// Replay: stay cocked until the strike shows here; a swing whose clip is gone (feint, stagger, cut) ends.
			if ( _awaitStrikeShown )
			{
				if ( elapsed > 30f || (elapsed > 0.25f && !_pc.IsSwingAnimPlayingForArcOverlay()) )
					return false;
				_windup = Math.Max( _windup, elapsed + 0.05f );
			}

			// Heavy-ready glint for every other peer, at the heavy windup on the host's clock - and again after each morph,
			// once it re-armed while still held.
			if ( _isHeavy && !_heavyReadySent && elapsed >= HeavyArmedElapsed && (_heavyHolding || !HasMorphed) )
			{
				_heavyReadySent = true;
				_pc.ServerBroadcastHeavyReady();
			}

			// Recovery (the follow-through): the swing still owns the pawn (no guard, footwork + turn rate). When it runs out
			// a chained click takes over (Last Oasis: every chained swing plays its follow-through first).
			if ( !float.IsNaN( _releaseEndedElapsed ) )
			{
				var recoveryEnd = _releaseEndedElapsed + _recoverySeconds;
				if ( elapsed < recoveryEnd )
					return true;

				if ( !_visualOnly )
					_pc.HostTryTakeChain( _startedAtSandbox + _releaseEndedElapsed, _startedAtSandbox + recoveryEnd );
				return false;
			}

			var windEnd = _windup;
			var activeEnd = windEnd + _active;

			// Path overlay: visual-only replay draws for clients. Authority also draws unless a
			// visual companion is already running (avoids double lines on listen-server host).
			var allowOverlay = _visualOnly
			                   || _pc._clientSwingTracePlayback is null
			                   || !_pc.ClientMeleeSwingTraceDebug;

			if ( elapsed < windEnd )
			{
				// Windup: black/white telegraph only — no red/yellow/blue path until Active phases.
				_pc.AdvanceAttackPath( _attackType, 0f, MeleeAttackStates.Windup,
					_pc.GetMeleeCombatBasisYaw( _attackType ), ref _debugDrawScratch, allowDebugOverlay: false );
				return true;
			}

			if ( !_visualOnly && !_loggedAttackPhase )
			{
				_loggedAttackPhase = true;
				_releaseBodyYaw0 = _pc.SwingBodyYawUnwrapped();
				_releaseStartElapsed = elapsed;
				if ( !_strikeCueSent )
				{
					_strikeCueSent = true;
					_pc.ServerOnStrikeStarted( _attackType );
				}
				_pc.LogMeleePhaseEnter( "release",
					$"duration={_active:0.###}s type={MeleeAttackTypes.Label( _attackType )} step={_comboStep} heavy={_isHeavy}" );
			}

			if ( elapsed >= activeEnd )
			{
				// The last frame of the window usually lands short of 1: close the ribbon to the end of the arc.
				_pc.FeedSwingVfxFromDamageArc( _attackType, 1f );
				_debugDrawScratch.Reset();
				_releaseEndedElapsed = activeEnd;
				TrySendCompletion();

				_recoverySeconds = _pc.RecoveryFor( t, _isHeavy, _comboStep );
				_pc.LogMeleePhaseEnter( "recovery", $"duration={_recoverySeconds:0.###}s step={_comboStep}" );
				return elapsed < _releaseEndedElapsed + _recoverySeconds;
			}

			var activeLen = Math.Max( 1e-4f, _active );
			var activeElapsed = Math.Min( elapsed - windEnd, activeLen );
			var activeT01 = Math.Clamp( activeElapsed / activeLen, 0f, 1f );
			const byte segState = MeleeAttackStates.Active;

			_pc.SampleServerMeleeBladeWorld( _attackType, activeT01, out var tip, out var heel );
			_pc.AdvanceAttackPath( _attackType, activeT01, segState, _pc.GetMeleeCombatBasisYaw( _attackType ),
				ref _debugDrawScratch, allowOverlay );

			if ( !_havePrevSample )
			{
				_prevTip = tip;
				_prevHeel = heel;
				_havePrevSample = true;
				return true;
			}

			if ( !_visualOnly && !_stopHitValidation )
			{
				const byte hitState = MeleeAttackStates.Active;
				var damage = _pc.GetMeleeDamage( _isHeavy, TurnScaleSoFar( elapsed ) );
				var stagger = _isHeavy ? t.HeavyHitStaggerSeconds : t.HitStaggerSeconds;
				var hitRadius = Math.Max( 2f, _pc.MeleeHitVolumeThickness );
				var basis = _pc.GetMeleeCombatBasisRotation( _attackType );
				var rayOrigin = MeleeAttackPath.GetSwingPivotWorld( attacker, _pc, _attackType, basis );

				// Primary: sweep tip+heel motion between frames (catches late-sweep tunneling that
				// a single pivot→tip spoke misses when the blade has already swung through the body).
				_stopHitValidation = MeleeAttackSweep.SphereSweepBladeSegment(
					scene,
					attacker,
					_pc,
					hitRadius,
					_substep,
					_prevTip,
					tip,
					_prevHeel,
					heel,
					_hitVictims,
					_maxTargets,
					_allowMultiple,
					damage,
					stagger,
					hitState,
					_attackType,
					_instanceId,
					_swingNote,
					_logHits,
					ref _targetsHitCount,
					OnHit );

				// Secondary: spoke ray for tip currently inside a body with little inter-frame motion.
				if ( !_stopHitValidation )
				{
					_stopHitValidation = MeleeAttackSweep.RaySweepFromOrigin(
						scene,
						attacker,
						_pc,
						rayOrigin,
						tip,
						_hitVictims,
						_maxTargets,
						_allowMultiple,
						damage,
						stagger,
						hitState,
						_attackType,
						_intent.SwingDir,
						_instanceId,
						_swingNote,
						_logHits,
						ref _targetsHitCount,
						OnHit );
				}

				// A blocked / chambered swing bounces off: it ends on contact and its blocked recovery starts now
				// (PlayerCombat.CombatRecovery). A landed hit keeps sweeping (no more hit checks) to the end of the release.
				if ( _stopHitValidation && (_wasBlocked || _wasChambered) )
				{
					_debugDrawScratch.Reset();
					TrySendCompletion();
					return false;
				}
			}

			_prevTip = tip;
			_prevHeel = heel;
			return true;
		}

		void OnHit( MeleeHitResult hit )
		{
			_anyHit = true;
			_totalDamageDealt += hit.DamageApplied;
			_pc.LastMeleeHitResult = hit;
			if ( hit.WasBlocked )
				_wasBlocked = true;
			if ( hit.WasChambered )
				_wasChambered = true;
			if ( _firstHitTargetId == Guid.Empty && hit.TargetId != Guid.Empty )
				_firstHitTargetId = hit.TargetId;

			// Practice readout for the swing's owner (settings: show damage information).
			if ( !_visualOnly )
				_pc.HostReportDamageInfo( hit.DamageApplied, _pc.LastTurnDamageScale, _isHeavy, hit.WasBlocked, hit.WasChambered );

			// A blade landing (blocks / chambers sound from ServerApplyBlockOutcome): a fleshy thud on another player, the
			// duller one on anything else (Mark 2026-11-16).
			if ( !_visualOnly && !hit.WasBlocked && !hit.WasChambered )
				_pc.ServerBroadcastCombatCue( IsPlayerTarget( hit.TargetId ) ? CombatAudioCue.HitPlayer : CombatAudioCue.HitOther,
					hit.HitPosition );
		}

		/// <summary>A player pawn (scavs and animals have their own vitals, not <see cref="PlayerVitals"/>).</summary>
		bool IsPlayerTarget( Guid id )
		{
			var go = id == Guid.Empty ? null : _pc.Scene.Directory.FindByGuid( id );
			return go.IsValid() && go.Components.Get<PlayerVitals>( FindMode.EverythingInSelfAndDescendants ) is not null;
		}

		void TrySendCompletion()
		{
			if ( _completionSent )
				return;
			_completionSent = true;

			if ( !_pc.IsValid() || !_pc.GameObject.IsValid() )
				return;

			// Weapon durability: 1 tick per swing that connected with anything (hit, blocked or chambered).
			if ( !_visualOnly && _anyHit )
				_pc.HostAddWearToActiveMainHand();

			if ( !_visualOnly )
			{
				_pc.NotifyServerMeleeAttackFinished( new MeleeAttackFinishOutcome
				{
					AnyHit = _anyHit,
					WasBlocked = _wasBlocked,
					WasChambered = _wasChambered,
					IsHeavy = _isHeavy,
					IsShove = false
				} );
			}

			if ( !_visualOnly && _pc.GameObject.Network is { Active: true } )
				_pc.RpcOwnerMeleeSwingComplete( _sequence, _anyHit, _totalDamageDealt, _firstHitTargetId );
			else if ( !_visualOnly )
				_pc.ApplyAuthoritativeMeleeSweepSummary( _sequence, _anyHit, _totalDamageDealt, _firstHitTargetId );
		}
	}
}
