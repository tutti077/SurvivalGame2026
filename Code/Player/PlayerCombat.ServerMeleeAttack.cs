using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

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
		if ( !GameObject.IsValid() || GameObject.IsProxy )
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

	public float GetMeleeDamageForState( byte attackState, bool isHeavy, byte swingDir, Vector2 postDragScreen ) =>
		GetMeleeWeaponBaseDamage() * ComputeMeleeCombatDamageMultiplier( swingDir, postDragScreen, isHeavy, attackState );

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
			MeleeLateActiveDamagePenalty,
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
		if ( !GameObject.IsValid() || GameObject.IsProxy || !IsServerSideForMeleeAuthority() )
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

		if ( GameObject.Network is { Active: true } && Networking.IsHost && ClientMeleeSwingTraceDebug )
			RpcBroadcastMeleeSwingTraceDebug( intent );
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

		if ( GameObject.IsProxy )
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
		if ( _clientSwingTracePlayback is not null || _serverMeleeAttack is not null )
			return;

		var hold = Math.Max( 0f, (float)( intent.ReleasedGlobalSeconds - intent.PressedGlobalSeconds ) );
		var heavy = IsHeavyAttackForHoldDuration( hold );
		_clientSwingTracePlayback = new ServerMeleeAttackRuntime( this, intent, hold, heavy, "client-trace", visualOnly: true );
	}

	void DrawAccumulatedAttackPath( IReadOnlyList<MeleeAttackPathPoint> path )
	{
		if ( !MeleeDebugDrawEnabled || !GameObject.IsValid() || path is null || path.Count == 0 )
			return;

		if ( !IsServerSideForMeleeAuthority() && _clientSwingTracePlayback is null )
			return;

		var drawDuration = Math.Max( 0f, MeleeDebugOverlayDuration );
		var hitRadius = Math.Max( 2f, MeleeHitVolumeThickness );

		for ( var i = 1; i < path.Count; i++ )
		{
			var a = path[i - 1];
			var b = path[i];
			var color = GetMeleeDebugColorForState( b.AttackState );
			DebugOverlay.Line( a.TipWorld, b.TipWorld, color, drawDuration );
		}

		if ( !MeleeDebugDrawSamplePointsEnabled )
			return;

		foreach ( var sample in path )
		{
			var color = GetMeleeDebugColorForState( sample.AttackState ).WithAlpha( 0.42f );
			DebugOverlay.Sphere( new Sphere( sample.TipWorld, hitRadius ), color, drawDuration );
		}
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

		readonly List<MeleeAttackPathPoint> _pathSamples = new();
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
		float _lastPathSampleActiveElapsed = -1f;
		bool _appendedFinalActiveSample;
		bool _stopHitValidation;

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
			_pc.CaptureForwardMeleeStartPitch( _attackType );
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

			if ( elapsed < windEnd )
			{
				_pc.DrawAccumulatedAttackPath( _pathSamples );
				return true;
			}

			if ( elapsed >= activeEnd )
			{
				if ( !_appendedFinalActiveSample )
				{
					_appendedFinalActiveSample = true;
					var finalState = MeleeAttackPath.ClassifyActiveState( _pc, _attackType, _active );
					_pc.SampleServerMeleeBladeWorld( _attackType, 1f, out var finalTip, out var finalHeel );
					AppendPathSample( 1f, _active, finalState, finalTip, finalHeel, forceAppend: true );
				}

				_pc.DrawAccumulatedAttackPath( _pathSamples );
				if ( elapsed < totalEnd )
					return true;

				TrySendCompletion();
				return false;
			}

			var activeLen = Math.Max( 1e-4f, _active );
			var activeElapsed = elapsed - windEnd;
			var activeT01 = Math.Clamp( activeElapsed / activeLen, 0f, 1f );
			var segState = MeleeAttackPath.ClassifyActiveState( _pc, _attackType, activeElapsed );

			if ( _lastPathSampleActiveElapsed >= 0f )
				AppendPhaseBoundarySamples( _prevActiveElapsed, activeElapsed );

			_pc.SampleServerMeleeBladeWorld( _attackType, activeT01, out var tip, out var heel );
			AppendPathSample( activeT01, activeElapsed, segState, tip, heel );
			_pc.DrawAccumulatedAttackPath( _pathSamples );

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

				_stopHitValidation = MeleeAttackSweep.SphereSweepBladeSegment(
					scene,
					attacker,
					_pc,
					_radius,
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
					_isHeavy,
					_instanceId,
					_swingNote,
					_logHits,
					ref _targetsHitCount,
					OnHit );
			}

			_prevActiveT01 = activeT01;
			_prevActiveElapsed = activeElapsed;
			_prevTip = tip;
			_prevHeel = heel;
			return true;
		}

		void AppendPhaseBoundarySamples( float prevElapsed, float curElapsed )
		{
			MeleeAttackPath.GetPhaseBoundaryElapsedSeconds( _pc, _attackType, out var activeStart, out var lateStart );

			TryAppendBoundarySample( prevElapsed, curElapsed, activeStart );
			TryAppendBoundarySample( prevElapsed, curElapsed, lateStart );
		}

		void TryAppendBoundarySample( float prevElapsed, float curElapsed, float boundaryElapsed )
		{
			if ( prevElapsed >= boundaryElapsed - 1e-5f || curElapsed < boundaryElapsed - 1e-5f )
				return;

			var t01 = MeleeAttackPath.ActiveProgressFromElapsed( _pc, _attackType, boundaryElapsed );
			var state = MeleeAttackPath.ClassifyActiveState( _pc, _attackType, boundaryElapsed );
			_pc.SampleServerMeleeBladeWorld( _attackType, t01, out var tip, out var heel );
			AppendPathSample( t01, boundaryElapsed, state, tip, heel, forceAppend: true );
		}

		void AppendPathSample( float activeT01, float activeElapsed, byte attackState, Vector3 tip, Vector3 heel,
			bool forceAppend = false )
		{
			var spacing = Math.Max( 2f, _radius * 0.9f );

			if ( _pathSamples.Count > 0 )
			{
				var last = _pathSamples[^1];
				var dist = (tip - last.TipWorld).Length;
				var lastElapsed = _lastPathSampleActiveElapsed >= 0f ? _lastPathSampleActiveElapsed : activeElapsed;
				var stateChanged = last.AttackState != attackState;
				var minTime = Math.Max( 0.008f, _active / 72f );
				var timeDue = activeElapsed - lastElapsed >= minTime - 1e-5f;

				if ( dist > spacing )
				{
					var steps = Math.Max( 1, (int)MathF.Ceiling( dist / spacing ) );
					for ( var i = 1; i <= steps; i++ )
					{
						var f = i / (float)steps;
						var it = last.ActiveProgress01 + (activeT01 - last.ActiveProgress01) * f;
						var iElapsed = lastElapsed + (activeElapsed - lastElapsed) * f;
						var istate = MeleeAttackPath.ClassifyActiveState( _pc, _attackType, iElapsed );
						PushPathPoint(
							Vector3.Lerp( last.TipWorld, tip, f ),
							Vector3.Lerp( last.HeelWorld, heel, f ),
							it,
							istate,
							iElapsed );
					}

					return;
				}

				if ( !forceAppend && !stateChanged && !timeDue && dist * dist < spacing * spacing * 0.04f )
					return;
			}

			PushPathPoint( tip, heel, activeT01, attackState, activeElapsed );
		}

		void PushPathPoint( Vector3 tip, Vector3 heel, float activeT01, byte attackState, float activeElapsed )
		{
			_pathSamples.Add( new MeleeAttackPathPoint
			{
				TipWorld = tip,
				HeelWorld = heel,
				ActiveProgress01 = activeT01,
				AttackState = attackState
			} );
			_lastPathSampleActiveElapsed = activeElapsed;
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

			if ( _attackType == MeleeAttackTypes.Forward && _pc.IsValid() )
				_pc.ClearForwardMeleeStartPitch();

			if ( !_pc.IsValid() || !_pc.GameObject.IsValid() )
				return;

			if ( !_visualOnly && _pc.GameObject.Network is { Active: true } )
				_pc.RpcOwnerMeleeSwingComplete( _sequence, _anyHit, _totalDamageDealt, _firstHitTargetId );
			else if ( !_visualOnly )
				_pc.ApplyAuthoritativeMeleeSweepSummary( _sequence, _anyHit, _totalDamageDealt, _firstHitTargetId );
		}
	}
}
