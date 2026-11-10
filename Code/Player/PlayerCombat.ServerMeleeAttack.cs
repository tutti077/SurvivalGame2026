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
/// Host-side phased primary melee (remaining windup → active sweep → outcome recovery). Lives on <see cref="PlayerCombat"/> per Commandment #1.
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

		// Cannot start a swing while the guard is still up or recovery locks actions.
		if ( IsAuthoritativeMeleeBlocking || IsCombatActionLocked )
			return false;

		// Broken weapons cannot swing — repair at a workbench first.
		if ( IsActiveMainHandBroken() )
			return false;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		return scene.IsValid();
	}

	public byte ResolveAttackTypeFromIntent( in AttackReleaseIntent intent ) =>
		intent.IsSpecial ? MeleeAttackTypes.Stab : ResolveAttackTypeFromCursorDir( intent.SwingDir );

	/// <summary>Resolves attack type from cursor cardinal using <see cref="SouthpawSwing"/> (call once when attack starts).</summary>
	public byte ResolveAttackTypeFromCursorDir( byte cursorDir )
	{
		var type = MeleeAttackTypes.FromCursorDir( cursorDir, SouthpawSwing );
		// Spear: up is a forward thrust, not an overhead chop (weapon class forwardAttackIsThrust).
		if ( type == MeleeAttackTypes.Forward && GetMeleeWeaponTimings().ForwardAttackIsThrust )
			return MeleeAttackTypes.Stab;
		return type;
	}

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
	/// Damage of one swing: base weapon damage × the combat multiplier (× the class heavy multiplier for a heavy)
	/// × <paramref name="scale"/> (the static combo overhead bonus / the Q special multiplier), rounded.
	/// </summary>
	public float GetMeleeDamage( bool isHeavy = false, float scale = 1f )
	{
		var raw = GetMeleeWeaponBaseDamage() * ComputeMeleeCombatDamageMultiplier() * Math.Max( 0f, scale );
		if ( isHeavy )
			raw *= GetMeleeWeaponTimings().HeavyDamageMultiplier;
		return MathF.Round( raw, MidpointRounding.AwayFromZero );
	}

	/// <summary>Host: charge the heavy's extra stamina once, when the swing turns heavy. False = cannot pay → it stays light.</summary>
	internal bool HostTrySpendHeavyStamina()
	{
		var extra = GetHeavyExtraStaminaCost();
		if ( extra <= 1e-4f )
			return true;

		var pv = Components.Get<PlayerVitals>();
		if ( pv is null )
			return false;

		if ( VitalsAuthority.Instance is { } vAuth )
			return vAuth.TryApplyDeltas( GameObject, 0f, -extra, pv );

		return pv.HasStaminaFor( extra ) && pv.RequestVitalsDelta( 0f, -extra );
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

	/// <summary>Host: a blocked heavy breaks the defender's guard — no block, no attack for the attacker's class time.</summary>
	void ServerBreakGuard( Guid defenderId )
	{
		var seconds = GetMeleeWeaponTimings().HeavyGuardBreakSeconds;
		if ( seconds <= 1e-4f || defenderId == Guid.Empty )
			return;

		var scene = GameObject.Scene;
		var target = scene.IsValid() ? scene.Directory.FindByGuid( defenderId ) : null;
		var defender = target?.Root?.Components.Get<PlayerCombat>( FindMode.EverythingInSelfAndDescendants );
		if ( defender is null || defender == this )
			return;

		defender.ServerBeginCombatRecovery( seconds, CombatRecoveryAnim.Rpg2hAttackMoving );
		LogMeleePhasePulse( "Guard break", $"{defender.GameObject.Name} locked {seconds:0.###}s by a blocked heavy" );
	}

	/// <summary>Global combat damage multiplier.</summary>
	public float ComputeMeleeCombatDamageMultiplier() => Math.Max( 0f, MeleeBaseCombatDamageMultiplier );

	/// <summary>Victim flail seconds for a landed swing (<see cref="MeleeHitReactionSeconds"/>).</summary>
	public float GetMeleeStagger() => Math.Max( 0f, MeleeHitReactionSeconds );

	public Color GetMeleeDebugColorForState( byte attackState )
	{
		if ( attackState == MeleeAttackStates.Active )
			return new Color( 1f, 0.85f, 0.1f, 0.92f );
		if ( attackState == MeleeAttackStates.Windup )
			return new Color( 0.55f, 0.55f, 0.62f, 0.45f );
		return new Color( 0.7f, 0.7f, 0.7f, 0.5f );
	}

	public void ApplyMeleeStaggerToVictim( PlayerVitals vitals, float stagger )
	{
		if ( stagger <= 1e-4f )
			return;

		if ( vitals is null || !vitals.GameObject.IsValid() )
			return;

		// Resolve the victim's animation, not its combat — being hit is not equipment-dependent and
		// PlayerAnimation owns the reaction window.
		var victimAnimation = vitals.Components.Get<PlayerAnimation>()
		                      ?? vitals.GameObject.Root?.Components.Get<PlayerAnimation>( FindMode.EverythingInSelfAndDescendants );

		if ( victimAnimation is null || victimAnimation.GameObject == GameObject )
			return;

		victimAnimation.ServerBeginHitReaction( stagger );
	}

	/// <summary>
	/// Host: start one phased swing — windup (class windup minus <paramref name="telegraphedSeconds"/>, the time an
	/// entity already showed its cocked pose before committing; players click-commit, so 0) then the damage swoop.
	/// </summary>
	public void ServerStartMeleeAttackAction( in AttackReleaseIntent intent, float telegraphedSeconds, string swingLogNote, bool canTurnHeavy = false )
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

		SetMeleeAttackCommitmentLock( true );
		// Drop soft pistol / prior recovery so it cannot re-apply over the new swing anim next frame.
		ServerClearCombatRecoveryForNewAttack();
		var animation = Components.Get<PlayerAnimation>();
		// Press/release already snapped the clip — don't abort before the strike is scheduled.
		if ( animation is null || !animation.HasActiveMeleeSwingPresentation )
			animation?.AbortMeleeAttackAnimClip( "new swing" );
		var damageScale = HostResolveSwingDamageScale( intent );
		_serverMeleeAttack = new ServerMeleeAttackRuntime( this, intent, telegraphedSeconds, swingLogNote, canTurnHeavy: canTurnHeavy,
			damageScale: damageScale );

		var attackType = ResolveAttackTypeFromIntent( intent );
		LogMeleePhasePulse( "Swing start",
			$"type={MeleeAttackTypes.Label( attackType )} combo={intent.ComboStep} special={intent.IsSpecial} dmg×{damageScale:0.##} telegraphed={telegraphedSeconds:0.###}s windRemaining={_serverMeleeAttack.WindupRemainingSeconds:0.###}s active={MeleeAttackPath.GetActiveDurationSeconds( this ):0.###}s seq={intent.IntentSequence}" );
		LogMeleePhaseEnter( "windup start",
			$"remaining={_serverMeleeAttack.WindupRemainingSeconds:0.###}s (class windup {GetMeleeWindupSeconds():0.###}s)" );

		EmitSwingNoiseIfPlayer();
		// The pawn this machine drives strikes on its own (predicted) clock; everyone else holds the cocked pose until
		// the host's strike cue (ServerOnStrikeStarted), which is what makes a light that turns heavy read correctly.
		animation?.PlayMeleeSwingAttack( attackType, broadcastFromHost: true,
			IsLocalCombatDriver() ? _serverMeleeAttack.WindupRemainingSeconds : PlayerAnimation.StrikeOnCue );

		// Don't Broadcast inside Rpc.Host handling of the attacker pawn — nested HostOnly broadcasts on a
		// host-owned object often never reach joining clients (host still runs it locally → host sees clients).
		if ( GameObject.Network is { Active: true } && Networking.IsHost && ClientMeleeSwingTraceDebug )
		{
			_deferredSwingVisualIntent = intent;
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
			if ( !pc.ClientMeleeSwingTraceDebug || !pc.MeleeDebugDrawEnabled )
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
			pc._hasPendingSwingVisualIntent = false;
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
		SetMeleeAttackCommitmentLock( false );
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
			SetMeleeAttackCommitmentLock( false );
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

		if ( _serverMeleeAttack is null )
			return;

		if ( !_serverMeleeAttack.Tick( scene ) )
		{
			_serverMeleeAttack = null;
			HostNoteSwingEnded();
			SetMeleeAttackCommitmentLock( false );
			// Don't wait on Rpc.Owner to clear the spam-click gate (listen-server host can stall otherwise).
			// Remote owners are cleared by RpcOwnerMeleeSwingComplete inside Tick; if that path
			// never ran, failsafe unlock so the client is not stuck forever.
			if ( IsLocalCombatDriver() )
				ClearOwnerMeleeBusyExpect( "local sweep end" );
		}
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

		var telegraphed = Math.Max( 0f, (float)( intent.ReleasedGlobalSeconds - intent.PressedGlobalSeconds ) );
		_clientSwingTracePlayback = new ServerMeleeAttackRuntime( this, intent, telegraphed, "client-trace", visualOnly: true );
	}

	/// <summary>Local owner ticks remote swing overlays — proxy pawns may not receive OnUpdate.</summary>
	internal void TickClientSwingTracePlaybackOnly( Scene scene )
	{
		if ( _clientSwingTracePlayback is null )
			return;

		if ( !_clientSwingTracePlayback.Tick( scene ) )
			_clientSwingTracePlayback = null;
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
				pc.ServerTickMeleeBlockTimers();
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

		// Stab has no arc fan — its overlay is spheres marching outward along the thrust.
		if ( attackType == MeleeAttackTypes.Stab )
		{
			AdvanceStabAttackPathOverlay( activeProgress01, attackState, drawOverlay, overlayDuration, ref scratch );
			return;
		}

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
	/// Stab overlay: spheres created outward along the thrust line as the active window advances —
	/// the sphere row IS the damage volume (radius = <see cref="MeleeHitVolumeThickness"/> like the sweep).
	/// </summary>
	void AdvanceStabAttackPathOverlay(
		float activeProgress01,
		byte attackState,
		bool drawOverlay,
		float overlayDuration,
		ref MeleeAttackDebugDrawScratch scratch )
	{
		if ( !drawOverlay || !MeleeAttackStates.DealsDamage( attackState ) || activeProgress01 <= 1e-5f )
			return;

		SampleServerMeleeBladeWorld( MeleeAttackTypes.Stab, activeProgress01, out var liveTip, out _ );
		var color = GetMeleeDebugColorForState( attackState ).WithAlpha( 0.5f );
		var radius = Math.Max( 2f, MeleeHitVolumeThickness );

		if ( scratch.HasLastLeadTip )
		{
			var delta = liveTip - scratch.LastLeadTipWorld;
			var spacing = Math.Max( radius * 1.5f, 6f );
			var steps = Math.Max( 1, (int)MathF.Ceiling( delta.Length / spacing ) );
			for ( var i = 1; i <= steps; i++ )
				DebugOverlay.Sphere( new Sphere( scratch.LastLeadTipWorld + delta * (i / (float)steps), radius ), color, overlayDuration );
		}
		else
			DebugOverlay.Sphere( new Sphere( liveTip, radius ), color, overlayDuration );

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
		readonly AttackReleaseIntent _intent;
		readonly byte _attackType;
		readonly ushort _sequence;
		readonly ushort _instanceId;
		float _windup;
		readonly float _active;
		readonly float _radius;
		readonly float _substep;
		readonly string _swingNote;
		readonly bool _logHits;
		readonly bool _visualOnly;
		/// <summary>Player swings only: still held when the light windup ends → heavy (entities never turn heavy).</summary>
		readonly bool _canTurnHeavy;
		readonly float _heavyWindup;
		bool _heavyDecided;
		bool _isHeavy;
		/// <summary>Heavy, button still down on the host's view: the cocked pose hangs until the release (or the max hold).</summary>
		bool _heavyHolding;
		readonly float _heavyMaxHold;
		/// <summary>Host-decided at the start: static combo overhead bonus or the Q special multiplier (1 otherwise).</summary>
		readonly float _damageScale;
		bool _strikeCueSent;
		bool _heavyReadySent;
		readonly bool _allowMultiple;
		readonly int _maxTargets;
		bool _loggedAttackPhase;

		readonly HashSet<Guid> _hitVictims = new();

		double _startedAtSandbox;
		Vector3 _prevTip;
		Vector3 _prevHeel;
		bool _havePrevSample;
		float _totalDamageDealt;
		Guid _firstHitTargetId;
		bool _anyHit;
		bool _wasBlocked;
		bool _wasParried;
		bool _completionSent;
		int _targetsHitCount;
		bool _stopHitValidation;
		MeleeAttackDebugDrawScratch _debugDrawScratch;

		internal ServerMeleeAttackRuntime(
			PlayerCombat pc,
			in AttackReleaseIntent intent,
			float telegraphedSeconds,
			string swingNote,
			bool visualOnly = false,
			bool canTurnHeavy = false,
			float damageScale = 1f )
		{
			_pc = pc;
			_intent = intent;
			_attackType = pc.ResolveAttackTypeFromIntent( intent );
			_sequence = intent.IntentSequence;
			_instanceId = _nextMeleeAttackInstanceId++;
			_visualOnly = visualOnly;
			// Click → damage is the class windup, always (no charge, no initiative). Entities that already
			// showed their cocked pose before committing play only the rest of it.
			// The Q special (sword / club stab) has its own, longer class windup.
			var classWindup = intent.IsSpecial ? pc.GetMeleeWeaponTimings().SpecialWindupSeconds : pc.GetMeleeWindupSeconds();
			_windup = Math.Max( 0f, classWindup - Math.Max( 0f, telegraphedSeconds ) );
			_damageScale = damageScale;
			_active = MeleeAttackPath.GetActiveDurationSeconds( pc );
			_canTurnHeavy = canTurnHeavy && !visualOnly;
			_heavyWindup = Math.Max( _windup, pc.GetMeleeWeaponTimings().HeavyWindupSeconds - Math.Max( 0f, telegraphedSeconds ) );
			_heavyMaxHold = Math.Max( _heavyWindup, pc.HeavyMaxHoldSeconds );
			_radius = Math.Max( 2f, pc.MeleeHitVolumeThickness );
			_substep = Math.Max( 4f, pc.MeleeSweepSubstepLength );
			_swingNote = swingNote;
			_logHits = pc.LogMeleeSweepHitsToConsole;
			_allowMultiple = pc.MeleeAllowMultipleHitsPerAttack;
			_maxTargets = _allowMultiple ? Math.Max( 1, pc.MeleeMaxTargetsHit ) : 1;
			_startedAtSandbox = Time.NowDouble;
			_debugDrawScratch.Reset();
			_pc.PushMeleeAttackBasisFromIntent( intent, _attackType );
		}

		internal byte AttackType => _attackType;

		internal ushort IntentSequence => _intent.IntentSequence;

		/// <summary>Windup left to play before the damage swoop opens.</summary>
		internal float WindupRemainingSeconds => _windup;

		internal bool IsInWindupPhase
		{
			get
			{
				// A heavy-capable swing is still winding up until the heavy decision is made at the light windup's end.
				var elapsed = (float)( Time.NowDouble - _startedAtSandbox );
				return elapsed < _windup || (_canTurnHeavy && !_heavyDecided) || _heavyHolding;
			}
		}

		internal bool IsHeavy => _isHeavy;


		internal float GetActiveArcProgress01()
		{
			var elapsed = (float)( Time.NowDouble - _startedAtSandbox );
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

			var elapsed = (float)( Time.NowDouble - _startedAtSandbox );

			// End of the light windup: button still held (no release reached the host) and the heavy's extra stamina
			// paid → heavy: the cocked pose keeps hanging to the heavy windup, then the same swoop hits harder.
			if ( !_heavyDecided && elapsed >= _windup )
			{
				_heavyDecided = true;
				if ( _canTurnHeavy && !_pc.HostWasPrimaryAttackReleased( _sequence ) && _pc.HostTrySpendHeavyStamina() )
				{
					_isHeavy = true;
					_heavyHolding = true;
					_pc.HostNoteComboBrokenByHeavy();
					_pc.LogMeleePhasePulse( "Heavy", $"held past the windup — strikes on release (≥ {_heavyWindup:0.###}s), x{_pc.GetMeleeWeaponTimings().HeavyDamageMultiplier:0.##} damage" );
				}
			}

			// Heavy: hang in the cocked pose while the button is down; the release (or the max hold) is the strike,
			// never earlier than the heavy windup.
			if ( _heavyHolding )
			{
				if ( _pc.HostWasPrimaryAttackReleased( _sequence ) || elapsed >= _heavyMaxHold )
				{
					_heavyHolding = false;
					_windup = Math.Max( _heavyWindup, elapsed );
				}
				else
					_windup = Math.Max( _heavyWindup, elapsed + 0.05f );
			}

			// Heavy-ready glint for every other peer, at the heavy windup on the host's clock.
			if ( _isHeavy && !_heavyReadySent && elapsed >= _heavyWindup )
			{
				_heavyReadySent = true;
				_pc.ServerBroadcastHeavyReady();
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
				if ( !_strikeCueSent )
				{
					_strikeCueSent = true;
					_pc.ServerOnStrikeStarted( _attackType );
				}
				_pc.LogMeleePhaseEnter( "attack phase",
					$"duration={_active:0.###}s type={MeleeAttackTypes.Label( _attackType )}" );
			}

			if ( elapsed >= activeEnd )
			{
				// The last frame of the window usually lands short of 1: close the ribbon to the end of the arc.
				_pc.FeedSwingVfxFromDamageArc( _attackType, 1f );
				_debugDrawScratch.Reset();
				TrySendCompletion();
				return false;
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
				var damage = _pc.GetMeleeDamage( _isHeavy, _damageScale );
				var stagger = _pc.GetMeleeStagger();
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

				// A blocked / parried swing bounces off: it ends on contact and the attacker's lockout starts now.
				// A landed hit keeps sweeping (no more hit checks) to the end of the damage swoop, so hitting
				// never makes the next attack come sooner (Mark 2026-11-04: "attack speed does not change").
				if ( _stopHitValidation && (_wasBlocked || _wasParried) )
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
			if ( hit.WasParried )
				_wasParried = true;
			if ( _firstHitTargetId == Guid.Empty && hit.TargetId != Guid.Empty )
				_firstHitTargetId = hit.TargetId;
			// A heavy that is blocked (not parried) breaks the guard.
			if ( _isHeavy && !_visualOnly && hit.WasBlocked && !hit.WasParried )
				_pc.ServerBreakGuard( hit.TargetId );
		}

		void TrySendCompletion()
		{
			if ( _completionSent )
				return;
			_completionSent = true;

			if ( _pc.IsValid() )
				_pc.ClearMeleeAttackBasisFromIntent();

			if ( !_pc.IsValid() || !_pc.GameObject.IsValid() )
				return;

			// Weapon durability: 1 tick per swing that connected with anything (hit or blocked).
			if ( !_visualOnly && _anyHit )
				_pc.HostAddWearToActiveMainHand();

			if ( !_visualOnly )
			{
				_pc.NotifyServerMeleeAttackFinished( new MeleeAttackFinishOutcome
				{
					AnyHit = _anyHit,
					WasBlocked = _wasBlocked,
					WasParried = _wasParried,
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
