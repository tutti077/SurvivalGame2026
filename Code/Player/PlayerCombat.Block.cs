using System;
using Sandbox;

namespace Survival;

public partial class PlayerCombat
{
	/// <summary>Host: apply JSON block costs, then the blocker's reaction (parry = none, heavy block = hit reaction).</summary>
	internal void ServerApplyBlockOutcome( in MeleeBlockOutcome outcome, Component attacker )
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		var vitals = Components.Get<PlayerVitals>();
		if ( vitals is not null )
		{
			if ( outcome.StaminaCost > 1e-4f )
				vitals.TrySpendStamina( outcome.StaminaCost );

			if ( outcome.HealthDamage > 1e-4f )
				vitals.ApplyDamageAfterArmor( outcome.HealthDamage, attacker );
		}

		var wasHeavy = outcome.OutcomeId.StartsWith( "heavy", StringComparison.OrdinalIgnoreCase );
		var wasParry = outcome.WasPerfectParry;

		ConsumeAuthoritativeMeleeBlock( attackWasHeavy: wasHeavy, wasPerfectParry: wasParry );

		if ( !wasParry && wasHeavy )
			ServerBeginHitReaction( RecoveryDefenderHeavyBlockSeconds );

		Log.Info(
			$"[MeleeBlock] outcome {GameObject.Name}: {outcome.OutcomeId} tier={outcome.Tier} "
			+ $"duration={outcome.DurationSeconds:0.###}s hp={outcome.HealthDamage:0.#} stam={outcome.StaminaCost:0.#} parry={outcome.WasPerfectParry}" );
	}

	[Property, Group( "Combat — Block" ), Title( "Show block guard + ground arc" )]
	public bool ShowBlockVisualization { get; set; } = true;

	[Property, Group( "Combat — Block" ), Title( "Show block footprint volume (wedge fill)" )]
	public bool ShowBlockFootprintVolume { get; set; } = true;

	[Property, Group( "Combat — Block" ), Title( "Footprint volume fill alpha" )]
	public float BlockFootprintFillAlpha { get; set; } = 0.09f;

	[Property, Group( "Combat — Block" ), Title( "Block sample sphere radius" )]
	public float BlockSampleSphereRadius { get; set; } = 2f;

	[Property, Group( "Combat — Block" ), Title( "Block guard sample count" )]
	public float BlockGuardSampleCount { get; set; } = 24f;

	[Property, Group( "Combat — Block" ), Title( "Front block arc (° total)" ), Range( 90f, 360f ), Step( 5f ), Description( "Facing coverage while blocking. 270° = ±135° from look yaw. Teardrop does not change this." )]
	public float MeleeBlockFrontArcDegrees { get; set; } = 270f;

	[Property, Group( "Combat — Block" ), Title( "Block body radius" ), Range( 4f, 48f ), Step( 1f ), Description( "Horizontal radius of the block shell around the pawn (body coverage, not an extended guard arc)." )]
	public float MeleeBlockBodyRadius { get; set; } = 18f;

	[Property, Group( "Combat — Block" ), Title( "Block shell height padding" ), Range( 0f, 24f ), Step( 1f ), Description( "Extra height above eye height for the block shell." )]
	public float BlockShellHeightPadding { get; set; } = 8f;

	[Property, Group( "Combat — Block" ), Title( "Ground arc height offset" )]
	public float BlockGroundArcHeightOffset { get; set; } = 2f;

	[Property, Group( "Combat — Block" ), Title( "Parry window (s)" ), Range( 0f, 0.5f ), Step( 0.01f ), Description( "Block started within this many seconds of the hit = perfect parry. Block viz lines are white during this window." )]
	public float MeleeBlockParryWindowSeconds { get; set; } = 0.2f;

	[Property, Group( "Combat — Block" ), Title( "Standard block free hold (s)" ), Range( 0f, 10f ), Step( 0.25f ), Description( "From block start (parry window included): holding guard costs nothing for this long. Past it the long block phase drains stamina." )]
	public float StandardBlockFreeSeconds { get; set; } = 2f;

	[Property, Group( "Combat — Block" ), Title( "Long block stamina drain (/s)" ), Range( 0f, 30f ), Step( 0.5f ), Description( "Stamina per second while the guard is held past the standard phase. Empty stamina breaks the block (forces re-press). 0 = never drains." )]
	public float LongBlockStaminaPerSecond { get; set; } = 5f;

	[Property, Group( "Combat — Block" ), Title( "Log rejected blocks to console" )]
	public bool LogMeleeBlockRejectionsToConsole { get; set; }

	[Property, Group( "Combat — Block" ), Title( "Block debug color" )]
	public Color BlockDebugColor { get; set; } = new( 0.22f, 0.92f, 0.38f, 0.92f );

	[Property, Group( "Combat — Block" ), Title( "Parry window color" ), Description( "Block guard lines while still inside the perfect-parry timing window." )]
	public Color BlockParryWindowColor { get; set; } = Color.White;


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

	float _postAttackRecoveryRemaining;

	double _serverBlockStartedAtSandbox;
	double _serverBlockLastDirectionChangeAtSandbox;
	double _localBlockVizStartedAt;
	bool _localBlockInputWasActive;

	bool _blockDirectionChangedThisFrame;

	public CombatState CombatState { get; private set; } = CombatState.Idle;

	public bool IsAuthoritativeMeleeBlocking => _authoritativeMeleeBlockActive;

	public byte AuthoritativeMeleeBlockDirection => _authoritativeMeleeBlockDirection;

	public double ServerBlockStartedAtSandbox => _serverBlockStartedAtSandbox;

	public byte GetActiveBlockDirection() => GetBlockGuardDirection();

	public bool BlockDirectionChangedThisFrame => _blockDirectionChangedThisFrame;

	public float GetMeleeBlockStaminaCost( bool attackWasHeavy )
	{
		var outcome = MeleeBlockStaggerCatalog.Resolve( attackWasHeavy, perfectParry: false );
		return Math.Max( 0f, outcome.StaminaCost );
	}

	internal int GetBlockGuardSampleCount() =>
		(int)Math.Clamp( BlockGuardSampleCount, 4f, 48f );

	internal void ServerTickMeleeBlockTimers()
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		ServerTickLongBlockStaminaDrain();
	}

	/// <summary>Host: long block phase — guard held past the free window drains stamina; empty stamina breaks the block.</summary>
	void ServerTickLongBlockStaminaDrain()
	{
		if ( !_authoritativeMeleeBlockActive || LongBlockStaminaPerSecond <= 1e-4f )
			return;

		if ( _serverBlockStartedAtSandbox <= 0d
		     || Time.NowDouble - _serverBlockStartedAtSandbox < Math.Max( 0f, StandardBlockFreeSeconds ) )
			return;

		var vitals = Components.Get<PlayerVitals>();
		if ( vitals is null )
			return;

		var drain = LongBlockStaminaPerSecond * Time.Delta;
		if ( drain <= 0f )
			return;

		if ( !vitals.TrySpendStamina( drain ) )
			ConsumeAuthoritativeMeleeBlock( attackWasHeavy: false );
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
		var attacking = _primary.Down || ServerHasActiveMeleeAttackAction;

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

	void CancelAllAttackActivity()
	{
		ClearQueuedAttackPress();
		// Raising the guard also cancels a charging special before it can auto-fire.
		ClearSpecialAttackChargeState();
		Components.Get<PlayerAnimation>()?.CancelMeleeAttackWindupHold();
		ServerCancelMeleeAttack();
		// Pure clients never run ServerCancelMeleeAttack locally — still unlock Attack1 immediately.
		ClearOwnerMeleeBusyExpect( "cancel all attack" );
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

		if ( active && !_localBlockInputWasActive )
			_localBlockVizStartedAt = _block.Snapshot.PressedSandboxTimeNowDouble ?? Time.NowDouble;
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

	internal void ConsumeAuthoritativeMeleeBlock( bool attackWasHeavy, bool wasPerfectParry = false )
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		_ = attackWasHeavy;
		SetAuthoritativeMeleeBlockState( false, _authoritativeMeleeBlockDirection );

		if ( GameObject.Network is { Active: true } )
			RpcOwnerMeleeBlockConsumed( wasPerfectParry );
		else
			_meleeBlockConsumedAwaitingRelease = true;

		if ( IsLocalCombatDriver() )
			CombatState = wasPerfectParry ? CombatState.Idle : CombatState.PostBlocking;
	}

	[Rpc.Owner]
	void RpcOwnerMeleeBlockConsumed( bool wasPerfectParry )
	{
		_meleeBlockConsumedAwaitingRelease = true;
		_lastSentBlockActive = false;
		CombatState = wasPerfectParry ? CombatState.Idle : CombatState.PostBlocking;
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

	Color GetBlockVisualizationColor()
	{
		if ( IsInsidePerfectParryWindow() )
			return BlockParryWindowColor;

		return BlockDebugColor;
	}

	/// <summary>True while block age is still within <see cref="MeleeBlockParryWindowSeconds"/>.</summary>
	bool IsInsidePerfectParryWindow()
	{
		var window = Math.Max( 0f, MeleeBlockParryWindowSeconds );
		if ( window <= 0f )
			return false;

		if ( !IsAuthoritativeMeleeBlocking && !LocalBlockInputActive() )
			return false;

		var started = _serverBlockStartedAtSandbox;
		if ( IsLocalCombatDriver() && LocalBlockInputActive() && _localBlockVizStartedAt > 0d )
			started = _localBlockVizStartedAt;

		if ( started <= 0d )
			return false;

		var age = Time.NowDouble - started;
		return age >= 0d && age <= window;
	}

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
