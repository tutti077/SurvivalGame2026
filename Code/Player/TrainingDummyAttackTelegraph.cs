using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-only training dummy: telegraphs L/R/U on a fixed placed rotation, then runs a real <see cref="PlayerCombat"/> sweep.
/// Rotate the dummy in the scene to aim attacks — orientation does not track players.
/// </summary>
[Title( "Training Dummy Attack Telegraph" )]
public sealed class TrainingDummyAttackTelegraph : Component
{
	[Property] public PlayerCombat Combat { get; set; }

	[Property, Group( "Timing" )] public float TelegraphSeconds { get; set; } = 0.85f;
	[Property, Group( "Timing" )] public float CooldownSeconds { get; set; } = 0.45f;
	[Property, Group( "Timing" )] public float HoldSeconds { get; set; } = 0.12f;
	[Property, Group( "Range" ), Title( "Attack activation range" ), Description( "Dummy only telegraphs and attacks while a player pawn is within this distance." )]
	public float AttackActivationRange { get; set; } = 256f;
	[Property, Group( "Timing" ), Title( "Attack path vertical offset" )] public float AttackPathVerticalOffset { get; set; } = -24f;
	[Property, Group( "Debug" )] public bool ShowTelegraphDebug { get; set; } = true;
	[Property, Group( "Debug" )] public float TelegraphLineLength { get; set; } = 92f;
	[Property, Group( "Debug" )] public float TelegraphEyeHeight { get; set; } = 64f;

	readonly Random _rng = new();
	Rotation _lockedAttackRotation;
	float _baseMeleeAttackZaxisStart;
	float _baseForwardPivotUpFromEye;
	byte _nextSwingDir = SwingDirs.Up;
	bool _hasQueuedAttack;
	bool _telegraphActive;
	double _phaseEndsAt;
	ushort _intentSequence;

	protected override void OnStart()
	{
		Combat ??= Components.Get<PlayerCombat>();
		_lockedAttackRotation = GameObject.WorldRotation;
		if ( Combat is not null )
		{
			_baseMeleeAttackZaxisStart = Combat.MeleeAttackZaxisStart;
			_baseForwardPivotUpFromEye = Combat.MeleeAttackForwardPivotUpFromEye;
		}
	}

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid() || GameObject.IsProxy )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		Combat ??= Components.Get<PlayerCombat>();
		if ( Combat is null || !Combat.Enabled )
			return;

		GameObject.WorldRotation = _lockedAttackRotation;
		ApplyAttackPathVerticalOffset();

		if ( !IsPlayerInAttackRange() )
		{
			ResetAttackCycle();
			return;
		}

		if ( !_hasQueuedAttack )
			QueueNextAttack();

		if ( ShowTelegraphDebug )
			DrawTelegraphDebug();

		if ( Time.NowDouble < _phaseEndsAt )
			return;

		if ( _telegraphActive )
		{
			TryExecuteQueuedAttack();
			return;
		}

		QueueNextAttack();
	}

	void ResetAttackCycle()
	{
		_hasQueuedAttack = false;
		_telegraphActive = false;
		_phaseEndsAt = 0;
	}

	bool IsPlayerInAttackRange()
	{
		if ( AttackActivationRange <= 0f )
			return true;

		return TryGetNearestPlayerDistance( out var distance ) && distance <= AttackActivationRange;
	}

	bool TryGetNearestPlayerDistance( out float distance )
	{
		distance = float.MaxValue;
		var scene = Scene;
		if ( !scene.IsValid() )
			return false;

		var origin = GameObject.WorldPosition;
		var found = false;

		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is null || !vitals.Enabled || vitals.GameObject is null || !vitals.GameObject.IsValid() )
				continue;

			if ( vitals.GameObject == GameObject || SharesHierarchy( vitals.GameObject, GameObject ) )
				continue;

			if ( vitals.Components.Get<PlayerController>() is null )
				continue;

			var d = Vector3.DistanceBetween( origin, vitals.GameObject.WorldPosition );
			if ( d >= distance )
				continue;

			distance = d;
			found = true;
		}

		return found;
	}

	static bool SharesHierarchy( GameObject a, GameObject b )
	{
		if ( !a.IsValid() || !b.IsValid() )
			return false;

		for ( var go = a; go.IsValid(); go = go.Parent )
		{
			if ( go == b )
				return true;
		}

		for ( var go = b; go.IsValid(); go = go.Parent )
		{
			if ( go == a )
				return true;
		}

		return false;
	}

	void QueueNextAttack()
	{
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
			$"dummy telegraph {SwingDirs.Letter( _nextSwingDir )}" );

		_telegraphActive = false;
		_phaseEndsAt = Time.NowDouble + Math.Max( 0.05f, CooldownSeconds );
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
			f = new Vector2( _lockedAttackRotation.Forward.x, _lockedAttackRotation.Forward.z );
		f = f.LengthSquared < 1e-8f ? new Vector2( 0f, 1f ) : f.Normal;
		var right = new Vector2( f.y, -f.x );

		if ( swingDir == SwingDirs.Left )
			return -right;
		if ( swingDir == SwingDirs.Right )
			return right;
		return f;
	}

	byte RollSwingDir()
	{
		return _rng.Next( 0, 3 ) switch
		{
			0 => SwingDirs.Left,
			1 => SwingDirs.Right,
			_ => SwingDirs.Up
		};
	}

	void DrawTelegraphDebug()
	{
		if ( !_telegraphActive || !_hasQueuedAttack )
			return;

		var start = GameObject.WorldPosition + Vector3.Up * TelegraphEyeHeight;
		var viewForward = _lockedAttackRotation.Forward.Normal;
		var right = Vector3.Cross( viewForward, Vector3.Up ).Normal;
		var color = ResolveTelegraphColor();

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

	Color ResolveTelegraphColor()
	{
		// Black flash = player light windup window (mirrors MeleeWindupDuration).
		var flashWindow = Combat is not null ? Combat.GetMeleeWindupDuration( isHeavy: false ) : 0f;
		if ( flashWindow > 1e-4f && _telegraphActive && Time.NowDouble >= _phaseEndsAt - flashWindow )
			return new Color( 0.02f, 0.02f, 0.02f, 0.98f );

		return _nextSwingDir == SwingDirs.Left
			? new Color( 0.50f, 0.78f, 1f, 0.95f )
			: _nextSwingDir == SwingDirs.Right
				? new Color( 1f, 0.85f, 0.20f, 0.95f )
				: new Color( 1f, 0.45f, 0.35f, 0.95f );
	}
}
