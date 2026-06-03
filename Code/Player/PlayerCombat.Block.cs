using System;
using Sandbox;

namespace Survival;

public partial class PlayerCombat
{
	[Property, Group( "Combat — Block" ), Title( "Show block guard + ground arc" )]
	public bool ShowBlockVisualization { get; set; } = true;

	[Property, Group( "Combat — Block" ), Title( "Show block footprint volume (wedge fill)" )]
	public bool ShowBlockFootprintVolume { get; set; } = true;

	[Property, Group( "Combat — Block" ), Title( "Footprint volume fill alpha" )]
	public float BlockFootprintFillAlpha { get; set; } = 0.09f;

	[Property, Group( "Combat — Block" ), Title( "Block sample sphere radius" )]
	public float BlockSampleSphereRadius { get; set; } = 2f;

	[Property, Group( "Combat — Block" ), Title( "Block line length" )]
	public float BlockLineLength { get; set; } = 62f;

	[Property, Group( "Combat — Block" ), Title( "Guard reach (edge of arc → line center)" )]
	public float BlockGuardReach { get; set; } = 20f;

	[Property, Group( "Combat — Block" ), Title( "Block guard sample count" )]
	public float BlockGuardSampleCount { get; set; } = 24f;

	[Property, Group( "Combat — Block" ), Title( "Lateral block arc (° total, half each side)" )]
	public float MeleeBlockLateralArcDegrees { get; set; } = 150f;

	[Property, Group( "Combat — Block" ), Title( "Overhead block arc (° total, ±half from center)" )]
	public float MeleeBlockOverheadArcDegrees { get; set; } = 50f;

	[Property, Group( "Combat — Block" ), Title( "Ground arc radius at feet" )]
	public float BlockGroundArcRadius { get; set; } = 48f;

	[Property, Group( "Combat — Block" ), Title( "Ground arc height offset" )]
	public float BlockGroundArcHeightOffset { get; set; } = 2f;

	[Property, Group( "Combat — Block" ), Title( "Log rejected blocks to console" )]
	public bool LogMeleeBlockRejectionsToConsole { get; set; } = true;

	[Property, Group( "Combat — Block" ), Title( "Overhead block up (above chest)" )]
	public float BlockOverheadUpOffset { get; set; } = 12f;

	[Property, Group( "Combat — Block" ), Title( "Overhead block forward offset" )]
	public float BlockOverheadForwardOffset { get; set; } = 14f;

	[Property, Group( "Combat — Block" ), Title( "Block debug color" )]
	public Color BlockDebugColor { get; set; } = new( 0.22f, 0.92f, 0.38f, 0.92f );

	[Property, Group( "Combat — Block" ), Title( "Light attack block stamina cost" )]
	public float LightAttackBlockStaminaCost { get; set; } = 10f;

	[Property, Group( "Combat — Block" ), Title( "Heavy attack block stamina cost" )]
	public float HeavyAttackBlockStaminaCost { get; set; } = 15f;

	[Property, Group( "Combat — Block" ), Title( "Blocked damage multiplier" )]
	public float MeleeBlockedDamageMultiplier { get; set; } = 0f;

	[Property, Group( "Combat — Block" ), Title( "Blocked victim stagger multiplier" )]
	public float MeleeBlockedStaggerMultiplier { get; set; } = 0.35f;

	[Property, Group( "Combat — Block" ), Title( "Post-block recovery (s)" )]
	public float PostBlockRecoveryDuration { get; set; } = 0.35f;

	public float MeleeBlockLateralHalfArcDegrees => Math.Max( 1f, MeleeBlockLateralArcDegrees ) * 0.5f;

	public float MeleeBlockOverheadHalfArcDegrees => Math.Max( 1f, MeleeBlockOverheadArcDegrees ) * 0.5f;

	bool _authoritativeMeleeBlockActive;
	byte _authoritativeMeleeBlockDirection = SwingDirs.Up;

	bool _hostReportedBlockActive;
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

	float _postBlockRecoveryRemaining;
	float _postAttackRecoveryRemaining;

	double _serverBlockStartedAtSandbox;
	double _serverBlockLastDirectionChangeAtSandbox;

	bool _blockDirectionChangedThisFrame;

	public CombatState CombatState { get; private set; } = CombatState.Idle;

	public bool IsAuthoritativeMeleeBlocking => _authoritativeMeleeBlockActive;

	public byte AuthoritativeMeleeBlockDirection => _authoritativeMeleeBlockDirection;

	public double ServerBlockStartedAtSandbox => _serverBlockStartedAtSandbox;

	public byte GetActiveBlockDirection() => GetBlockGuardDirection();

	public bool BlockDirectionChangedThisFrame => _blockDirectionChangedThisFrame;

	public float GetMeleeBlockStaminaCost( bool attackWasHeavy ) =>
		Math.Max( 0f, attackWasHeavy ? HeavyAttackBlockStaminaCost : LightAttackBlockStaminaCost );

	internal int GetBlockGuardSampleCount() =>
		(int)Math.Clamp( BlockGuardSampleCount, 4f, 48f );

	internal bool ServerIsInPostBlockRecovery() => _postBlockRecoveryRemaining > 0.001f;

	internal void ServerTickMeleeBlockTimers()
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		if ( _postBlockRecoveryRemaining > 0f )
			_postBlockRecoveryRemaining = MathF.Max( 0f, _postBlockRecoveryRemaining - Time.Delta );
	}

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
		var attacking = _primary.Down || _primarySwingPhaseActive || ServerHasActiveMeleeAttackAction;

		if ( blockHeld )
		{
			CombatState = CombatState.Blocking;
			_postAttackRecoveryRemaining = 0f;
		}
		else if ( _postBlockRecoveryRemaining > 0f )
		{
			_postBlockRecoveryRemaining = MathF.Max( 0f, _postBlockRecoveryRemaining - Time.Delta );
			CombatState = CombatState.PostBlocking;
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

	void CancelAllAttackActivity()
	{
		CancelPrimarySwingPhase();
		ServerCancelMeleeAttack();
		_postAttackRecoveryRemaining = 0f;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost && IsLocalCombatDriver() )
			RpcHostCancelMeleeAttack();
	}

	[Rpc.Host]
	void RpcHostCancelMeleeAttack()
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		ServerCancelMeleeAttack();
	}

	internal void NotifyServerMeleeAttackFinished()
	{
		if ( !IsServerSideForMeleeAuthority() && !IsLocalCombatDriver() )
			return;

		if ( CombatState == CombatState.Blocking || CombatState == CombatState.PostBlocking )
			return;

		_postAttackRecoveryRemaining = Math.Max( 0f, MeleeRecoveryDuration );
		if ( IsLocalCombatDriver() && !Input.Down( BlockAction ) )
			CombatState = CombatState.PostAttack;
	}

	void TickAuthoritativeMeleeBlockState()
	{
		if ( GameObject.IsProxy && !Networking.IsHost )
			return;

		_blockDirectionChangedThisFrame = false;

		if ( IsServerSideForMeleeAuthority() && !IsLocalCombatDriver() )
		{
			SetAuthoritativeMeleeBlockState( _hostReportedBlockActive, _hostReportedBlockDirection, _hostReportedBlockBasisYaw );
			return;
		}

		if ( !IsLocalCombatDriver() )
			return;

		if ( Input.Released( BlockAction ) )
			_meleeBlockConsumedAwaitingRelease = false;

		var active = LocalBlockInputActive();
		var dir = GetBlockGuardDirection();
		var prevDir = _authoritativeMeleeBlockDirection;

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

		if ( active && !_authoritativeMeleeBlockActive )
			_serverBlockStartedAtSandbox = now;

		if ( active && direction is (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up)
		     && direction != _authoritativeMeleeBlockDirection )
			_serverBlockLastDirectionChangeAtSandbox = now;

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

		_authoritativeMeleeBlockActive = active;
		if ( direction is SwingDirs.Left or SwingDirs.Right or SwingDirs.Up )
			_authoritativeMeleeBlockDirection = direction;

		if ( active )
		{
			_remoteBlockBasisYaw = basisYaw;
			_remoteBlockBasisYawValid = true;
		}
		else
			_remoteBlockBasisYawValid = false;
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
		var pressedSandbox = _block.Snapshot.PressedSandboxTimeNowDouble ?? Time.NowDouble;
		RpcSubmitMeleeBlockState( active, direction, pressedSandbox, basisYaw );
	}

	[Rpc.Host]
	void RpcSubmitMeleeBlockState( bool active, byte direction, double blockPressedSandboxTime, float basisYaw )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		_hostReportedBlockActive = active;
		if ( direction is SwingDirs.Left or SwingDirs.Right or SwingDirs.Up )
			_hostReportedBlockDirection = direction;
		_hostReportedBlockBasisYaw = basisYaw;

		if ( active && ServerHasActiveMeleeAttackAction )
			ServerCancelMeleeAttack();

		if ( !IsLocalCombatDriver() )
		{
			if ( active && !_authoritativeMeleeBlockActive && blockPressedSandboxTime > 1e-6 )
				_serverBlockStartedAtSandbox = blockPressedSandboxTime;

			SetAuthoritativeMeleeBlockState( active, direction, basisYaw );
		}
	}

	internal void NotifyAuthoritativeMeleeBlockIntercepted()
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;
	}

	internal void ConsumeAuthoritativeMeleeBlock( bool attackWasHeavy )
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		var cost = GetMeleeBlockStaminaCost( attackWasHeavy );
		var vitals = Components.Get<PlayerVitals>();
		if ( vitals is not null && cost > 0f )
			vitals.TrySpendStamina( cost );

		SetAuthoritativeMeleeBlockState( false, _authoritativeMeleeBlockDirection );
		_postBlockRecoveryRemaining = Math.Max( 0f, PostBlockRecoveryDuration );

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
		_postBlockRecoveryRemaining = Math.Max( _postBlockRecoveryRemaining, PostBlockRecoveryDuration );
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
		out float damageMultiplier,
		out float staggerMultiplier,
		out MeleeBlockRejectReason rejectReason ) =>
		MeleeBlockResolution.TryResolve( this, in contact, logRejections, out damageMultiplier, out staggerMultiplier,
			out rejectReason );

	internal bool TryServerResolveBlock(
		in MeleeBlockContact contact,
		bool logRejections,
		out float damageMultiplier,
		out float staggerMultiplier,
		out MeleeBlockRejectReason rejectReason,
		out MeleeBlockValidationTrace trace ) =>
		MeleeBlockResolution.TryResolve( this, in contact, logRejections, out damageMultiplier, out staggerMultiplier,
			out rejectReason, out trace );

	internal void DrawRemoteBlockVisualizationIfNeeded()
	{
		if ( ShouldDrawMeleeBlockVisualization() )
			DrawMeleeBlockGuardVisualization();
	}

	void DrawMeleeBlockGuardVisualization()
	{
		if ( !ShouldDrawMeleeBlockVisualization() || !GameObject.IsValid() )
			return;

		var dir = GetBlockGuardDirection();
		if ( dir is not (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up) )
			dir = SwingDirs.Up;

		var drawDuration = MathF.Max( 0.016f, Time.Delta * 1.5f );
		var lineColor = BlockDebugColor;

		var sampleCount = GetBlockGuardSampleCount();
		Span<Vector3> samples = stackalloc Vector3[48];
		var count = MeleeBlockPath.BuildGuardSamples( this, dir, sampleCount, samples );

		if ( count >= 2 )
		{
			for ( var i = 1; i < count; i++ )
				DebugOverlay.Line( samples[i - 1], samples[i], lineColor, drawDuration );

			var hitRadius = Math.Max( 0.5f, BlockSampleSphereRadius );
			var sphereColor = lineColor.WithAlpha( 0.55f );
			for ( var i = 0; i < count; i++ )
				DebugOverlay.Sphere( new Sphere( samples[i], hitRadius ), sphereColor, drawDuration );
		}

		MeleeBlockPath.EnumerateGroundArcSegments( this, dir, 32, ( p0, p1 ) =>
			DebugOverlay.Line( p0, p1, lineColor.WithAlpha( 0.85f ), drawDuration ) );

		if ( ShowBlockFootprintVolume )
			DrawBlockFootprintSolidFill( dir );
	}

	void DrawBlockFootprintSolidFill( byte blockDir )
	{
		if ( blockDir is not (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up) )
			return;

		Gizmo.Draw.Color = BlockDebugColor.WithAlpha( Math.Clamp( BlockFootprintFillAlpha, 0.02f, 0.35f ) );
		MeleeBlockPath.EnumerateFootprintSolidTriangles( this, blockDir,
			( a, b, c ) => Gizmo.Draw.SolidTriangle( a, b, c ) );
	}

	protected override void DrawGizmos()
	{
		if ( !ShowBlockVisualization || !ShowBlockFootprintVolume || !GameObject.IsValid() )
			return;

		if ( !ShouldDrawMeleeBlockVisualization() )
			return;

		var dir = GetBlockGuardDirection();
		if ( dir is not (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up) )
			dir = SwingDirs.Up;

		DrawBlockFootprintSolidFill( dir );
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
