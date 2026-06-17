using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-only enemy attacks: random L/R/U telegraph (same as training dummy), aimed at the current target.
/// One attack per approach — after swing + recovery the brain must chase back into range.
/// </summary>
[Title( "Entity Combat" )]
public sealed class EntityCombat : Component
{
	[Property] public PlayerCombat Combat { get; set; }

	[Property, Group( "Timing" )] public float TelegraphSeconds { get; set; } = 0.85f;

	[Property, Group( "Timing" ), Title( "Post-attack recovery (no move / no swing)" )]
	public float RecoverySeconds { get; set; } = 1f;

	[Property, Group( "Timing" )] public float HoldSeconds { get; set; } = 0.12f;

	[Property, Group( "Telegraph" )] public float AttackPathVerticalOffset { get; set; } = -24f;
	[Property, Group( "Telegraph" )] public float TelegraphEyeHeight { get; set; } = 64f;
	[Property, Group( "Telegraph" )] public float TelegraphLineLength { get; set; } = 92f;

	[Property, Group( "Debug" )] public bool ShowTelegraphDebug { get; set; } = true;

	readonly Random _rng = new();
	GameObject _attackTarget;
	Rotation _attackRotation;
	float _baseMeleeAttackZaxisStart;
	float _baseForwardPivotUpFromEye;
	byte _nextSwingDir = SwingDirs.Up;
	bool _engaged;
	bool _hasQueuedAttack;
	bool _telegraphActive;
	bool _rotationLocked;
	bool _inRecovery;
	bool _waitingForSwingEnd;
	Rotation _lockedAttackRotation;
	double _phaseEndsAt;
	ushort _intentSequence;

	public bool IsEngaged => _engaged;

	public event Action AttackCycleFinished;

	/// <summary>Telegraph, swing, or post-attack recovery — brain holds position.</summary>
	public bool IsMovementLocked =>
		IsAttackCommitted || _waitingForSwingEnd || _inRecovery;

	/// <summary>Telegraph or active swing in progress — brain must not cancel mid-swing.</summary>
	public bool IsAttackCommitted =>
		_rotationLocked || _telegraphActive || (Combat?.ServerHasActiveMeleeAttackAction ?? false);

	public void SetEngaged( bool engaged )
	{
		if ( _engaged == engaged )
			return;

		if ( !engaged && IsMovementLocked )
			return;

		_engaged = engaged;
		if ( !engaged && !_inRecovery )
			ResetCycle();
	}

	public void SetAttackTarget( GameObject target ) => _attackTarget = target;

	public void TickCombat( GameObject target )
	{
		SetAttackTarget( target );
		SetEngaged( true );
	}

	public void ResetCycle()
	{
		_hasQueuedAttack = false;
		_telegraphActive = false;
		_rotationLocked = false;
		_inRecovery = false;
		_waitingForSwingEnd = false;
		_phaseEndsAt = 0;
	}

	protected override void OnStart()
	{
		Combat ??= Components.Get<PlayerCombat>();
		if ( Combat is not null )
		{
			_baseMeleeAttackZaxisStart = Combat.MeleeAttackZaxisStart;
			_baseForwardPivotUpFromEye = Combat.MeleeAttackForwardPivotUpFromEye;
		}
	}

	protected override void OnUpdate()
	{
		if ( !Active || GameObject.IsProxy || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		Combat ??= Components.Get<PlayerCombat>();

		if ( _inRecovery )
		{
			TickRecovery();
			return;
		}

		if ( _waitingForSwingEnd )
		{
			if ( Combat is not null && Combat.ServerHasActiveMeleeAttackAction )
				return;

			_waitingForSwingEnd = false;
			BeginRecovery();
			return;
		}

		if ( Combat is null || !Combat.Enabled || !_engaged )
			return;

		if ( _attackTarget is null || !_attackTarget.IsValid() )
		{
			if ( !IsAttackCommitted )
			{
				ResetCycle();
				return;
			}
		}

		if ( _rotationLocked )
			_attackRotation = _lockedAttackRotation;
		else if ( _attackTarget is { IsValid: true } )
			UpdateAttackRotation();

		GameObject.WorldRotation = _attackRotation;
		ApplyAttackPathVerticalOffset();

		if ( ShowTelegraphDebug && _telegraphActive )
			DrawTelegraphDebug();

		if ( Time.NowDouble < _phaseEndsAt )
			return;

		if ( _telegraphActive )
		{
			TryExecuteQueuedAttack();
			return;
		}

		if ( !_rotationLocked && _engaged )
			QueueNextAttack();
	}

	void TickRecovery()
	{
		if ( Time.NowDouble < _phaseEndsAt )
			return;

		_inRecovery = false;
		_engaged = false;
		ResetCycle();
		AttackCycleFinished?.Invoke();
	}

	void BeginRecovery()
	{
		_inRecovery = true;
		_engaged = false;
		_telegraphActive = false;
		_hasQueuedAttack = false;
		_rotationLocked = false;
		_phaseEndsAt = Time.NowDouble + Math.Max( 0.05f, RecoverySeconds );
	}

	void UpdateAttackRotation()
	{
		var toTarget = _attackTarget.WorldPosition - GameObject.WorldPosition;
		toTarget = toTarget.WithZ( 0f );
		if ( toTarget.LengthSquared > 1f )
			_attackRotation = Rotation.LookAt( toTarget.Normal, Vector3.Up );
	}

	void QueueNextAttack()
	{
		UpdateAttackRotation();
		_lockedAttackRotation = _attackRotation;
		_rotationLocked = true;

		_nextSwingDir = RollSwingDir();
		_hasQueuedAttack = true;
		_telegraphActive = true;
		_phaseEndsAt = Time.NowDouble + Math.Max( 0.05f, TelegraphSeconds );
	}

	void TryExecuteQueuedAttack()
	{
		if ( !_hasQueuedAttack || Combat is null )
			return;

		if ( !Combat.ServerCanBeginMeleeAttackAction() )
		{
			_phaseEndsAt = Time.NowDouble + 0.05;
			return;
		}

		GameObject.WorldRotation = _lockedAttackRotation;

		_intentSequence++;
		var basisRot = _lockedAttackRotation;
		var view = basisRot.Forward.Normal;
		var basisAngles = basisRot.Angles();
		var camera = GameObject.WorldPosition + Vector3.Up * TelegraphEyeHeight;
		var swingFrom = GetSwingFromXz( _nextSwingDir, view );
		var swingVert = _nextSwingDir == SwingDirs.Up ? 1f : 0f;
		var hold = Math.Max( 0f, HoldSeconds );
		var heavy = Combat.IsHeavyAttackForHoldDuration( hold );

		var intent = new AttackReleaseIntent
		{
			PressedGlobalSeconds = RealTime.GlobalNow - hold,
			ReleasedGlobalSeconds = RealTime.GlobalNow,
			ClientCameraPressX = camera.x,
			ClientCameraPressY = camera.y,
			ClientCameraPressZ = camera.z,
			ClientCameraReleaseX = camera.x,
			ClientCameraReleaseY = camera.y,
			ClientCameraReleaseZ = camera.z,
			ViewForwardOnPress = view,
			ViewForwardOnRelease = view,
			ClientPlayerWorldPosition = GameObject.WorldPosition,
			ClientPlayerWorldRotation = basisRot,
			IntentSequence = _intentSequence,
			SwingFromX = swingFrom.x,
			SwingFromY = swingFrom.y,
			SwingVerticalHint = swingVert,
			SwingDir = _nextSwingDir,
			AttackType = Combat.ResolveAttackTypeFromCursorDir( _nextSwingDir ),
			StaminaPrepaidMax = 0f,
			CombatBasisYawDegrees = basisAngles.yaw,
			CombatBasisPitchDegrees = basisAngles.pitch
		};

		Combat.ServerStartMeleeAttackAction( intent, hold, heavy,
			$"entity {SwingDirs.Letter( _nextSwingDir )}" );

		_telegraphActive = false;
		_hasQueuedAttack = false;
		_rotationLocked = false;
		_engaged = false;
		_waitingForSwingEnd = true;
	}

	void ApplyAttackPathVerticalOffset()
	{
		if ( Combat is null )
			return;

		Combat.MeleeAttackZaxisStart = _baseMeleeAttackZaxisStart + AttackPathVerticalOffset;
		Combat.MeleeAttackForwardPivotUpFromEye = _baseForwardPivotUpFromEye + AttackPathVerticalOffset;
	}

	Vector2 GetSwingFromXz( byte swingDir, Vector3 viewForward )
	{
		var f = new Vector2( viewForward.x, viewForward.z );
		if ( f.LengthSquared < 1e-8f )
			f = new Vector2( _attackRotation.Forward.x, _attackRotation.Forward.z );
		f = f.LengthSquared < 1e-8f ? new Vector2( 0f, 1f ) : f.Normal;
		var right = new Vector2( f.y, -f.x );

		if ( swingDir == SwingDirs.Left )
			return -right;
		if ( swingDir == SwingDirs.Right )
			return right;
		return f;
	}

	byte RollSwingDir() => _rng.Next( 0, 3 ) switch
	{
		0 => SwingDirs.Left,
		1 => SwingDirs.Right,
		_ => SwingDirs.Up
	};

	void DrawTelegraphDebug()
	{
		var start = GameObject.WorldPosition + Vector3.Up * TelegraphEyeHeight;
		var viewForward = _attackRotation.Forward.Normal;
		var right = Vector3.Cross( viewForward, Vector3.Up ).Normal;
		var color = _nextSwingDir == SwingDirs.Left
			? new Color( 0.50f, 0.78f, 1f, 0.95f )
			: _nextSwingDir == SwingDirs.Right
				? new Color( 1f, 0.85f, 0.20f, 0.95f )
				: new Color( 1f, 0.45f, 0.35f, 0.95f );

		Vector3 dir;
		if ( _nextSwingDir == SwingDirs.Left )
			dir = -right;
		else if ( _nextSwingDir == SwingDirs.Right )
			dir = right;
		else
			dir = (viewForward + Vector3.Up * 0.33f).Normal;

		var end = start + dir * Math.Max( 18f, TelegraphLineLength );
		var drawFor = MathF.Max( 0.03f, Time.Delta * 1.5f );
		DebugOverlay.Line( start, end, color, drawFor );
		DebugOverlay.Sphere( new Sphere( end, 3f ), color.WithAlpha( 0.75f ), drawFor );
	}
}
