using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Server-side training dummy attacker: telegraphs next L/R/U attack, then executes a real PlayerCombat melee action.
/// Attach to a dummy GameObject that already has <see cref="PlayerCombat"/> and optional <see cref="DamageReceiver"/>.
/// </summary>
[Title( "Training Dummy Attack Telegraph" )]
public sealed class TrainingDummyAttackTelegraph : Component
{
	[Property] public PlayerCombat Combat { get; set; }
	[Property, Group( "Targeting" )] public PlayerCombat TargetCombatOverride { get; set; }
	[Property, Group( "Targeting" )] public float TargetSearchRadius { get; set; } = 280f;
	[Property, Group( "Targeting" ), Title( "Continuously face target" )] public bool AutoFaceTarget { get; set; }
	[Property, Group( "Targeting" ), Title( "Face target on attack start" )] public bool FaceTargetOnAttackStart { get; set; } = true;
	[Property, Group( "Targeting" ), Title( "Yaw offset when facing (deg)" )] public float FacingYawOffsetDegrees { get; set; } = 180f;
	[Property, Group( "Timing" )] public float TelegraphSeconds { get; set; } = 0.85f;
	[Property, Group( "Timing" )] public float CooldownSeconds { get; set; } = 0.45f;
	[Property, Group( "Timing" )] public float HoldSeconds { get; set; } = 0.12f;
	[Property, Group( "Timing" ), Title( "Attack path vertical offset" )] public float AttackPathVerticalOffset { get; set; } = -24f;
	[Property, Group( "Debug" )] public bool ShowTelegraphDebug { get; set; } = true;
	[Property, Group( "Debug" )] public float TelegraphLineLength { get; set; } = 92f;
	[Property, Group( "Debug" )] public float TelegraphEyeHeight { get; set; } = 64f;

	readonly Random _rng = new();
	PlayerCombat _targetCombat;
	GameObject _selfRoot;
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
		_selfRoot = GetTopMostRoot( GameObject );
		if ( Combat is not null )
		{
			_baseMeleeAttackZaxisStart = Combat.MeleeAttackZaxisStart;
			_baseForwardPivotUpFromEye = Combat.MeleeAttackForwardPivotUpFromEye;
		}
	}

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid() || GameObject.IsProxy || !Networking.IsHost )
			return;

		Combat ??= Components.Get<PlayerCombat>();
		if ( Combat is null || !Combat.Enabled )
			return;
		ApplyAttackPathVerticalOffset();

		_targetCombat = FindNearestPlayerCombat();
		if ( _targetCombat is null || !_targetCombat.GameObject.IsValid() )
		{
			_hasQueuedAttack = false;
			_telegraphActive = false;
			return;
		}

		if ( AutoFaceTarget )
			FaceTargetYaw( _targetCombat.GameObject.WorldPosition );

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
			// Attack still in progress/recovery; retry shortly without dropping the telegraphed direction.
			_phaseEndsAt = Time.NowDouble + 0.05;
			return;
		}

		_intentSequence++;
		if ( FaceTargetOnAttackStart )
			FaceTargetYaw( _targetCombat.GameObject.WorldPosition );

		var view = GetViewForward();
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
			ClientPlayerWorldRotation = GameObject.WorldRotation,
			IntentSequence = _intentSequence,
			SwingFromX = swingFrom.x,
			SwingFromY = swingFrom.y,
			SwingVerticalHint = swingVert,
			SwingDir = _nextSwingDir,
			AttackType = Combat.ResolveAttackTypeFromCursorDir( _nextSwingDir ),
			StaminaPrepaidMax = 0f,
			PostSwingDragScreenX = 0f,
			PostSwingDragScreenY = 0f
		};

		Combat.ServerStartMeleeAttackAction( intent, hold, heavy,
			$"dummy telegraph {SwingDirs.Letter( _nextSwingDir )} target={_targetCombat.GameObject.Name}" );

		_telegraphActive = false;
		_phaseEndsAt = Time.NowDouble + Math.Max( 0.05f, CooldownSeconds );
	}

	void FaceTargetYaw( Vector3 targetPos )
	{
		var to = targetPos - GameObject.WorldPosition;
		to.y = 0f;
		if ( to.LengthSquared < 1e-6f )
			return;
		var yaw = Rotation.LookAt( to.Normal, Vector3.Up ).Angles().yaw + FacingYawOffsetDegrees;
		GameObject.WorldRotation = new Angles( 0f, yaw, 0f ).ToRotation();
	}

	PlayerCombat FindNearestPlayerCombat()
	{
		if ( TargetCombatOverride is not null && TargetCombatOverride.Enabled && TargetCombatOverride.GameObject.IsValid() && TargetCombatOverride != Combat )
			return TargetCombatOverride;

		var scene = GameObject.Scene;
		if ( !scene.IsValid() )
			return null;

		var maxDistSq = TargetSearchRadius > 0f ? TargetSearchRadius * TargetSearchRadius : float.MaxValue;
		var bestDistSq = maxDistSq;
		PlayerCombat best = null;

		foreach ( var pc in scene.GetAllComponents<PlayerCombat>() )
		{
			if ( pc is null || !pc.Enabled || !pc.GameObject.IsValid() || pc == Combat )
				continue;

			// Never target the same entity/root as the dummy itself.
			if ( GetTopMostRoot( pc.GameObject ) == _selfRoot )
				continue;

			var controller = pc.GameObject.Components.Get<PlayerController>();
			if ( controller is null || !controller.Enabled )
				continue;

			var d = (pc.GameObject.WorldPosition - GameObject.WorldPosition).LengthSquared;
			if ( d >= bestDistSq )
				continue;

			bestDistSq = d;
			best = pc;
		}

		return best;
	}

	static GameObject GetTopMostRoot( GameObject go )
	{
		if ( !go.IsValid() )
			return null;

		var root = go;
		for ( var p = go.Parent; p.IsValid(); p = p.Parent )
			root = p;

		return root;
	}

	Vector3 GetViewForward()
	{
		var toTarget = _targetCombat is not null
			? _targetCombat.GameObject.WorldPosition - GameObject.WorldPosition
			: GameObject.WorldRotation.Forward;
		if ( toTarget.LengthSquared < 1e-6f )
			return GameObject.WorldRotation.Forward.Normal;
		return toTarget.Normal;
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
			f = new Vector2( GameObject.WorldRotation.Forward.x, GameObject.WorldRotation.Forward.z );
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
		var v = _rng.Next( 0, 3 );
		return v switch
		{
			0 => SwingDirs.Left,
			1 => SwingDirs.Right,
			_ => SwingDirs.Up
		};
	}

	void DrawTelegraphDebug()
	{
		var start = GameObject.WorldPosition + Vector3.Up * TelegraphEyeHeight;
		var viewForward = GetViewForward();
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
