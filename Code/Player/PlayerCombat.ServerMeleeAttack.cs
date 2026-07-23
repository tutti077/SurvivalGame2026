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
/// Host-side phased primary melee (windup → EarlyActive/Active/LateActive sweeps → recovery). Lives on <see cref="PlayerCombat"/> per Commandment #1.
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

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		return scene.IsValid();
	}

	public byte ResolveAttackTypeFromIntent( in AttackReleaseIntent intent ) =>
		ResolveAttackTypeFromCursorDir( intent.SwingDir );

	/// <summary>Resolves attack type from cursor cardinal using <see cref="SouthpawSwing"/> (call once when attack starts).</summary>
	public byte ResolveAttackTypeFromCursorDir( byte cursorDir ) =>
		MeleeAttackTypes.FromCursorDir( cursorDir, SouthpawSwing );

	public bool IsHeavyAttackForHoldDuration( float holdSeconds ) =>
		holdSeconds + 1e-5f >= Math.Max( 0f, MeleeHeavyAttackHoldThreshold );

	public float GetMeleeWeaponBaseDamage() =>
		MeleeWeaponBaseDamage > 0f ? MeleeWeaponBaseDamage : AttackCombatConstants.DefaultMeleeWeaponDamage;

	public float GetMeleeDamageForState( byte attackState, bool isHeavy, byte swingDir, Vector2 postDragScreen )
	{
		var raw = GetMeleeWeaponBaseDamage()
		          * ComputeMeleeCombatDamageMultiplier( swingDir, postDragScreen, isHeavy, attackState );
		return MathF.Round( raw, MidpointRounding.AwayFromZero );
	}

	public float GetMeleeDamageForState( byte attackState, bool isHeavy, in AttackReleaseIntent intent ) =>
		GetMeleeDamageForState( attackState, isHeavy, intent.SwingDir,
			new Vector2( intent.PostSwingDragScreenX, intent.PostSwingDragScreenY ) );

	/// <summary>Global combat damage multiplier (1.0 + drag + phase penalties + heavy bonus).</summary>
	public float ComputeMeleeCombatDamageMultiplier( byte swingDir, Vector2 postDragScreen, bool isHeavy, byte attackState ) =>
		MeleeCombatDamageMultiplier.Compute(
			swingDir,
			postDragScreen,
			SwingDragGoodPixels,
			MeleeSwingDragGoodBonus,
			MeleeSwingDragBadPenalty,
			isHeavy,
			MeleeHeavyAttackDamageBonus,
			attackState,
			MeleeEarlyActiveDamagePenalty,
			MeleeLateActiveDamageBonus,
			MeleeBaseCombatDamageMultiplier );

	public float ComputeMeleeCombatDamageMultiplier( in AttackReleaseIntent intent, bool isHeavy, byte attackState ) =>
		ComputeMeleeCombatDamageMultiplier( intent.SwingDir,
			new Vector2( intent.PostSwingDragScreenX, intent.PostSwingDragScreenY ), isHeavy, attackState );

	public float ComputeMeleeCombatDamageMultiplier( byte swingDir, Vector2 postDragScreen, bool isHeavy ) =>
		ComputeMeleeCombatDamageMultiplier( swingDir, postDragScreen, isHeavy, MeleeAttackStates.Active );

	public float ComputeMeleeCombatDamageMultiplier( in AttackReleaseIntent intent, bool isHeavy ) =>
		ComputeMeleeCombatDamageMultiplier( intent, isHeavy, MeleeAttackStates.Active );

	public float GetMeleeStaggerMultiplierForState( byte attackState )
	{
		if ( attackState == MeleeAttackStates.EarlyActive )
			return Math.Max( 0f, MeleeEarlyActiveStaggerMultiplier );
		if ( attackState == MeleeAttackStates.Active )
			return Math.Max( 0f, MeleeActiveStaggerMultiplier );
		if ( attackState == MeleeAttackStates.LateActive )
			return Math.Max( 0f, MeleeLateActiveStaggerMultiplier );
		return 0f;
	}

	public float GetMeleeStaggerForState( byte attackState ) =>
		Math.Max( 0f, MeleeBaseStagger ) * GetMeleeStaggerMultiplierForState( attackState );

	public Color GetMeleeDebugColorForState( byte attackState )
	{
		if ( attackState == MeleeAttackStates.EarlyActive )
			return new Color( 0.55f, 0.78f, 1f, 0.92f );
		if ( attackState == MeleeAttackStates.Active )
			return new Color( 1f, 0.85f, 0.1f, 0.92f );
		if ( attackState == MeleeAttackStates.LateActive )
			return new Color( 1f, 0.35f, 0.25f, 0.92f );
		if ( attackState == MeleeAttackStates.Windup )
			return new Color( 0.55f, 0.55f, 0.62f, 0.45f );
		return new Color( 0.7f, 0.7f, 0.7f, 0.5f );
	}

	public void ApplyMeleeStaggerToVictim( PlayerVitals vitals, float stagger )
	{
		if ( vitals is null || stagger <= 1e-4f )
			return;
		vitals.ApplyMeleeStagger( stagger );
	}

	public void ServerStartMeleeAttackAction( in AttackReleaseIntent intent, float holdSeconds, bool isHeavy, string swingLogNote )
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

		_serverMeleeAttack = new ServerMeleeAttackRuntime( this, intent, holdSeconds, isHeavy, swingLogNote );

		EmitSwingNoiseIfPlayer();

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

	/// <summary>Host→all peers: start local DebugOverlay replay for the named attacker.</summary>
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

			if ( !pc.MeleeDebugDrawEnabled || !pc.ClientMeleeSwingTraceDebug )
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
		ClearForwardMeleeStartPitch();
		_serverMeleeAttack = null;
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

		if ( _clientSwingTracePlayback is not null && !_clientSwingTracePlayback.Tick( scene ) )
			_clientSwingTracePlayback = null;

		// Client proxies never run host sweeps; listen-server host still simulates client-owned pawns.
		if ( GameObject.IsProxy && !Networking.IsHost )
			return;

		if ( !IsServerSideForMeleeAuthority() )
			return;

		if ( _serverMeleeAttack is null )
			return;

		if ( !_serverMeleeAttack.Tick( scene ) )
			_serverMeleeAttack = null;
	}

	void StartClientMeleeSwingTracePlayback( in AttackReleaseIntent intent )
	{
		// Allow a visual companion alongside host authority — DebugOverlay is local-only, so the
		// host also needs this playback to see remote (and own) attack lines when testing MP.
		if ( _clientSwingTracePlayback is not null )
			return;

		var hold = Math.Max( 0f, (float)( intent.ReleasedGlobalSeconds - intent.PressedGlobalSeconds ) );
		var heavy = IsHeavyAttackForHoldDuration( hold );
		_clientSwingTracePlayback = new ServerMeleeAttackRuntime( this, intent, hold, heavy, "client-trace", visualOnly: true );
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

			// Listen-server: proxy pawns may skip OnUpdate — CombatAuthority drives sweep + draw companion once.
			if ( driveHostProxyAuthority && isHostProxy )
			{
				pc.MaybeTickServerMeleeAttackAction();
			}
			else if ( !pc.IsLocalCombatDriver() && !isHostProxy )
			{
				pc.TickClientSwingTracePlaybackOnly( scene );
			}

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

		var drawOverlay = allowDebugOverlay && MeleeDebugDrawEnabled;
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
			var yawBucket = MeleeAttackPath.QuantizeYawDegrees( currentBasisYaw, degreeStep );
			var currentSampleIndex = Math.Clamp( (int)MathF.Round( activeProgress01 * (sampleCount - 1) ), 0, sampleCount - 1 );
			var key = MeleeAttackPath.PackArcYawDebugKey( currentSampleIndex, yawBucket ) ^ ((long)attackStateForDraw << 56);
			if ( drawnArcYawKeys.Add( key ) )
			{
				var arcProgress = MeleeAttackPath.ArcSampleIndexToProgress01( sampleCount, currentSampleIndex );
				EmitMeleeDebugPathRay( attackType, currentBasisYaw, arcProgress, attackStateForDraw, drawOverlay,
					overlayDuration, hitRadius, drawSpheres, ref scratch );
			}
		}

		if ( !MeleeDebugDrawRotationSpokes )
		{
			scratch.LastArcProgress01 = activeProgress01;
			return;
		}

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
				overlayDuration, hitRadius, drawSpheres, ref scratch );
		}

		scratch.LastDrawBasisYaw = currentBasisYaw;
		scratch.HasLastDrawBasisYaw = true;
		scratch.LastArcProgress01 = activeProgress01;
	}

	internal void DrawWindupTelegraphIfNeeded()
	{
		if ( !ShowMeleeAttackWindupTelegraph || !_windupTelegraphActive )
			return;

		DrawMeleeAttackWindupTelegraph( _windupTelegraphAttackType, _windupTelegraphBasisYaw, _windupTelegraphHeavy );
	}

	void TickWindupTelegraphNetworkState()
	{
		if ( !ShowMeleeAttackWindupTelegraph )
		{
			if ( _windupTelegraphActive )
				PublishWindupTelegraphState( false, 0, 0f, false );
			return;
		}

		if ( !TryComputeLocalWindupTelegraph( out var attackType, out var basisYaw, out var isHeavy ) )
		{
			if ( _windupTelegraphActive )
				PublishWindupTelegraphState( false, 0, 0f, false );
			return;
		}

		PublishWindupTelegraphState( true, attackType, basisYaw, isHeavy );
	}

	bool TryComputeLocalWindupTelegraph( out byte attackType, out float basisYaw, out bool isHeavy )
	{
		attackType = 0;
		basisYaw = 0f;
		isHeavy = false;

		if ( _clientSwingTracePlayback?.IsInWindupPhase == true )
		{
			attackType = _clientSwingTracePlayback.AttackType;
			basisYaw = GetMeleeCombatBasisYaw( attackType );
			isHeavy = _clientSwingTracePlayback.IsHeavy;
			return true;
		}

		if ( ServerHasActiveMeleeAttackInWindup( out attackType, out basisYaw, out isHeavy ) )
			return true;

		if ( !_hasLockedPrimaryAttackDir )
			return false;

		if ( !Input.Down( PrimaryAttackAction ) && !_primarySwingPhaseActive )
			return false;

		attackType = ResolveAttackTypeFromCursorDir( _lockedPrimaryAttackSwingDir );
		basisYaw = GetMeleeCombatBasisYaw( attackType );
		isHeavy = IsHeavyAttackForHoldDuration( _primary.Snapshot.HoldDurationSeconds );
		return true;
	}

	bool ServerHasActiveMeleeAttackInWindup( out byte attackType, out float basisYaw, out bool isHeavy )
	{
		attackType = 0;
		basisYaw = 0f;
		isHeavy = false;
		if ( _serverMeleeAttack?.IsInWindupPhase != true )
			return false;

		attackType = _serverMeleeAttack.AttackType;
		basisYaw = GetMeleeCombatBasisYaw( attackType );
		isHeavy = _serverMeleeAttack.IsHeavy;
		return true;
	}

	void PublishWindupTelegraphState( bool active, byte attackType, float basisYaw, bool isHeavy )
	{
		if ( active == _lastSentWindupTelegraphActive && _lastSentWindupTelegraphValid
		     && attackType == _lastSentWindupTelegraphAttackType
		     && isHeavy == _lastSentWindupTelegraphHeavy
		     && ( !active || MathF.Abs( basisYaw - _lastSentWindupTelegraphBasisYaw ) < 0.4f ) )
			return;

		_lastSentWindupTelegraphActive = active;
		_lastSentWindupTelegraphAttackType = attackType;
		_lastSentWindupTelegraphBasisYaw = basisYaw;
		_lastSentWindupTelegraphHeavy = isHeavy;
		_lastSentWindupTelegraphValid = true;
		ApplyWindupTelegraphState( active, attackType, basisYaw, isHeavy );

		if ( GameObject.Network is not { Active: true } )
			return;

		if ( Networking.IsHost )
			BroadcastWindupTelegraphIfHost( active, attackType, basisYaw, isHeavy );
		else
			RpcSubmitWindupTelegraph( active, attackType, basisYaw, isHeavy );
	}

	void ApplyWindupTelegraphState( bool active, byte attackType, float basisYaw, bool isHeavy )
	{
		_windupTelegraphActive = active;
		_windupTelegraphAttackType = attackType;
		_windupTelegraphBasisYaw = basisYaw;
		_windupTelegraphHeavy = isHeavy;
	}

	void BroadcastWindupTelegraphIfHost( bool active, byte attackType, float basisYaw, bool isHeavy )
	{
		var changed = active != _lastBroadcastWindupTelegraphActive
		              || attackType != _lastBroadcastWindupTelegraphAttackType
		              || isHeavy != _lastBroadcastWindupTelegraphHeavy
		              || !_lastBroadcastWindupTelegraphValid
		              || ( active && MathF.Abs( basisYaw - _lastBroadcastWindupTelegraphBasisYaw ) >= 0.4f );

		if ( !changed )
			return;

		_lastBroadcastWindupTelegraphActive = active;
		_lastBroadcastWindupTelegraphAttackType = attackType;
		_lastBroadcastWindupTelegraphBasisYaw = basisYaw;
		_lastBroadcastWindupTelegraphHeavy = isHeavy;
		_lastBroadcastWindupTelegraphValid = true;
		RpcBroadcastWindupTelegraph( active, attackType, basisYaw, isHeavy );
	}

	[Rpc.Host]
	void RpcSubmitWindupTelegraph( bool active, byte attackType, float basisYaw, bool isHeavy )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		ApplyWindupTelegraphState( active, attackType, basisYaw, isHeavy );
		BroadcastWindupTelegraphIfHost( active, attackType, basisYaw, isHeavy );
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	void RpcBroadcastWindupTelegraph( bool active, byte attackType, float basisYaw, bool isHeavy )
	{
		if ( Networking.IsHost )
			return;

		ApplyWindupTelegraphState( active, attackType, basisYaw, isHeavy );
	}

	/// <summary>Thick line at the first attack-path sample — black (light) or white (heavy) until colored sweep rays begin.</summary>
	void DrawMeleeAttackWindupTelegraph( byte attackType, float basisYaw, bool isHeavy )
	{
		if ( !GameObject.IsValid() )
			return;

		var basis = GetMeleeCombatBasisRotationForYaw( attackType, basisYaw );
		var origin = MeleeAttackPath.GetSwingPivotWorld( GameObject, this, attackType, basis );
		MeleeAttackPath.EvaluateWorldBlade( GameObject, this, attackType, 0f, basis, out var tip, out _ );
		var drawFor = MathF.Max( 0.03f, Time.Delta * 1.5f );
		var color = isHeavy ? Color.White.WithAlpha( 0.92f ) : Color.Black.WithAlpha( 0.92f );
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
		ref MeleeAttackDebugDrawScratch scratch )
	{
		if ( !drawOverlay )
			return;

		var basis = GetMeleeCombatBasisRotationForYaw( attackType, basisYaw );
		var origin = MeleeAttackPath.GetSwingPivotWorld( GameObject, this, attackType, basis );
		var spokeColor = GetMeleeDebugColorForState( attackStateForDraw ).WithAlpha( 0.42f );
		MeleeAttackPath.EvaluateWorldBlade( GameObject, this, attackType, arcProgress01, basis, out var tip, out _ );
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
				: tip - basis.Forward * Math.Max( 8f, MeleeAttackRangeForward * MeleeBladeHeelFraction );
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
		readonly bool _isHeavy;
		readonly float _windup;
		readonly float _active;
		readonly float _recovery;
		readonly float _radius;
		readonly float _substep;
		readonly string _swingNote;
		readonly bool _logHits;
		readonly bool _visualOnly;
		readonly bool _allowMultiple;
		readonly int _maxTargets;

		readonly HashSet<Guid> _hitVictims = new();

		double _startedAtSandbox;
		Vector3 _prevTip;
		Vector3 _prevHeel;
		bool _havePrevSample;
		float _totalDamageDealt;
		Guid _firstHitTargetId;
		bool _anyHit;
		bool _completionSent;
		int _targetsHitCount;
		float _prevActiveT01;
		float _prevActiveElapsed;
		bool _stopHitValidation;
		MeleeAttackDebugDrawScratch _debugDrawScratch;

		internal ServerMeleeAttackRuntime(
			PlayerCombat pc,
			in AttackReleaseIntent intent,
			float holdSeconds,
			bool isHeavy,
			string swingNote,
			bool visualOnly = false )
		{
			_pc = pc;
			_intent = intent;
			_attackType = pc.ResolveAttackTypeFromIntent( intent );
			_sequence = intent.IntentSequence;
			_instanceId = _nextMeleeAttackInstanceId++;
			_isHeavy = isHeavy;
			_visualOnly = visualOnly;
			_windup = Math.Max( 0f, pc.MeleeWindupDuration );
			_active = MeleeAttackPath.GetActiveDurationSeconds( pc, _attackType );
			_recovery = Math.Max( 0f, pc.MeleeRecoveryDuration );
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

		internal bool IsInWindupPhase
		{
			get
			{
				var elapsed = (float)( Time.NowDouble - _startedAtSandbox );
				return elapsed < _windup;
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
			var activeElapsed = Math.Min( elapsed - windEnd, MeleeAttackPath.GetLatePhaseEndElapsedSeconds( _pc, _attackType ) );
			return Math.Clamp( activeElapsed / activeLen, 0f, 1f );
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
			var windEnd = _windup;
			var activeEnd = windEnd + _active;
			var totalEnd = activeEnd + _recovery;

			// When clients replicate path overlay is on, only the visual-only runtime draws DebugOverlay
			// (avoids double lines on the listen-server host that also has the authoritative sweep).
			var allowOverlay = _visualOnly
			                   || !_pc.ClientMeleeSwingTraceDebug
			                   || _pc.GameObject.Network is not { Active: true };

			if ( elapsed < windEnd )
			{
				_pc.AdvanceAttackPath( _attackType, 0f, MeleeAttackStates.Windup,
					_pc.GetMeleeCombatBasisYaw( _attackType ), ref _debugDrawScratch, allowOverlay );
				return true;
			}

			if ( elapsed >= activeEnd )
			{
				_debugDrawScratch.Reset();
				if ( elapsed < totalEnd )
					return true;

				TrySendCompletion();
				return false;
			}

			var activeLen = Math.Max( 1e-4f, _active );
			var activeElapsed = elapsed - windEnd;
			var latePhaseEnd = MeleeAttackPath.GetLatePhaseEndElapsedSeconds( _pc, _attackType );
			activeElapsed = Math.Min( activeElapsed, latePhaseEnd );
			var activeT01 = Math.Clamp( activeElapsed / activeLen, 0f, 1f );
			var segState = MeleeAttackPath.ClassifyActiveState( _pc, _attackType, activeElapsed );

			_pc.SampleServerMeleeBladeWorld( _attackType, activeT01, out var tip, out var heel );
			if ( MeleeAttackStates.DealsDamage( segState ) )
			{
				_pc.AdvanceAttackPath( _attackType, activeT01, segState, _pc.GetMeleeCombatBasisYaw( _attackType ),
					ref _debugDrawScratch, allowOverlay );
			}

			if ( !_havePrevSample )
			{
				_prevTip = tip;
				_prevHeel = heel;
				_havePrevSample = true;
				_prevActiveT01 = activeT01;
				return true;
			}

			if ( !_visualOnly && !_stopHitValidation && MeleeAttackStates.DealsDamage( segState ) )
			{
				var midElapsed = (_prevActiveElapsed + activeElapsed) * 0.5f;
				var hitState = MeleeAttackPath.ClassifyActiveState( _pc, _attackType, midElapsed );
				var damage = _pc.GetMeleeDamageForState( hitState, _isHeavy, _intent );
				var stagger = _pc.GetMeleeStaggerForState( hitState );
				var basis = _pc.GetMeleeCombatBasisRotation( _attackType );
				var rayOrigin = MeleeAttackPath.GetSwingPivotWorld( attacker, _pc, _attackType, basis );

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
					_isHeavy,
					_instanceId,
					_swingNote,
					_logHits,
					ref _targetsHitCount,
					OnHit );

				// End this attack action immediately on first relevant contact (hit or block), per training flow.
				if ( _stopHitValidation )
				{
					_debugDrawScratch.Reset();
					TrySendCompletion();
					return false;
				}
			}

			_prevActiveT01 = activeT01;
			_prevActiveElapsed = activeElapsed;
			_prevTip = tip;
			_prevHeel = heel;
			return true;
		}

		void OnHit( MeleeHitResult hit )
		{
			_anyHit = true;
			_totalDamageDealt += hit.DamageApplied;
			_pc.LastMeleeHitResult = hit;
			if ( _firstHitTargetId == Guid.Empty && hit.TargetId != Guid.Empty )
				_firstHitTargetId = hit.TargetId;
		}

		void TrySendCompletion()
		{
			if ( _completionSent )
				return;
			_completionSent = true;

			if ( _pc.IsValid() )
			{
				_pc.ClearMeleeAttackBasisFromIntent();
				if ( _attackType == MeleeAttackTypes.Forward )
					_pc.ClearForwardMeleeStartPitch();
			}

			if ( !_pc.IsValid() || !_pc.GameObject.IsValid() )
				return;

			if ( !_visualOnly )
				_pc.NotifyServerMeleeAttackFinished();

			if ( !_visualOnly && _pc.GameObject.Network is { Active: true } )
				_pc.RpcOwnerMeleeSwingComplete( _sequence, _anyHit, _totalDamageDealt, _firstHitTargetId );
			else if ( !_visualOnly )
				_pc.ApplyAuthoritativeMeleeSweepSummary( _sequence, _anyHit, _totalDamageDealt, _firstHitTargetId );
		}
	}
}
