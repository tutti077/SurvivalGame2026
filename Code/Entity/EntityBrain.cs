using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Navigation;

namespace Survival;

/// <summary>Host-only stateful enemy AI — idle/wander, alert, chase, attack, obstacle, search, return.</summary>
[Title( "Entity Brain" )]
public sealed class EntityBrain : Component
{
	[Property] public EntityCombat EntityCombat { get; set; }
	[Property] public EntityVitals Vitals { get; set; }
	[Property] public EntityLocomotion Locomotion { get; set; }
	[Property] public NavMeshAgent Agent { get; set; }

	[Property, Group( "Home" )] public Vector3 HomePosition { get; set; }

	[Property, Group( "Ranges" )] public float DetectionRange { get; set; } = 900f;
	[Property, Group( "Ranges" )] public float AcquireRange { get; set; } = 2000f;
	[Property, Group( "Ranges" )] public float AttackRange { get; set; } = 110f;
	[Property, Group( "Ranges" )] public float MaxHomeDistance { get; set; } = 2400f;
	[Property, Group( "Ranges" )] public float HomeArriveDistance { get; set; } = 96f;

	[Property, Group( "Ranges" ), Title( "Nav goal stand-off from target (flat, units)" )]
	public float ChaseStandOff { get; set; } = 48f;

	[Property, Group( "Timing" )] public float PathCheckInterval { get; set; } = 0.65f;
	[Property, Group( "Timing" )] public float ClosePathCheckInterval { get; set; } = 0.28f;
	[Property, Group( "Timing" ), Title( "Tracking AI think interval (seconds)" )]
	public float TrackingThinkInterval { get; set; } = 0.15f;
	[Property, Group( "Timing" )] public float AlertConfirmSeconds { get; set; } = 0.5f;
	[Property, Group( "Timing" )] public float AlertLostSeconds { get; set; } = 3f;
	[Property, Group( "Timing" )] public float SearchDurationSeconds { get; set; } = 8f;
	[Property, Group( "Timing" )] public float TrackingLostSeconds { get; set; } = 4f;
	[Property, Group( "Timing" )] public float ObstacleTimeoutSeconds { get; set; } = 12f;
	[Property, Group( "Timing" )] public float IdleMinSeconds { get; set; } = 2f;
	[Property, Group( "Timing" )] public float IdleMaxSeconds { get; set; } = 5f;
	[Property, Group( "Timing" )] public float WanderStuckSeconds { get; set; } = 3f;

	[Property, Group( "Wander" )] public float WanderRadius { get; set; } = 1100f;
	[Property, Group( "Wander" )] public float WanderReachDistance { get; set; } = 96f;
	[Property, Group( "Wander" ), Title( "Walk speed while wandering / idle roam" )]
	public float WanderMoveSpeed { get; set; } = 88f;

	const float NavVerifyIntervalSeconds = 4f;
	const float ChaseGoalCacheSeconds = 0.6f;
	const float ChaseGoalMoveThreshold = 96f;
	const float PathTargetMoveThreshold = 128f;

	EnemyAiState _state = EnemyAiState.Idle;
	GameObject _target;
	BuildPiece _obstacleTarget;
	Vector3 _issuedNavGoal;
	Vector3 _wanderGoal;
	Vector3 _lastKnownPlayerPos;
	double _stateEndsAt;
	double _nextPathCheckAt;
	double _playerLastSeenAt;
	double _wanderStuckSince;
	double _nextTargetRefreshAt;
	double _nextTrackingThinkAt;
	double _nextNavVerifyAt;
	double _cachedChaseNavGoalAt;
	Vector3 _cachedChaseNavGoal;
	Vector3 _cachedChasePlayerPos;
	Vector3 _lastPathTargetPos;
	float _chaseMoveSpeed = 220f;
	bool _needsImmediatePathCheck;

	public EnemyAiState CurrentState => _state;
	public GameObject ChaseTarget => _target.IsValid() ? _target : null;
	public BuildPiece ObstacleTarget => _obstacleTarget;
	public NavMeshPathStatus LastPathStatus { get; private set; }
	public string LastNavBlockReason { get; private set; } = "init";
	public Vector3 LastNavGoal { get; private set; }
	public IReadOnlyList<Vector3> LastPathPoints => _lastPathPoints;

	readonly List<Vector3> _lastPathPoints = new();

	protected override void OnStart()
	{
		EntityCombat ??= Components.Get<EntityCombat>();
		Vitals ??= Components.Get<EntityVitals>();
		Locomotion ??= Components.Get<EntityLocomotion>();
		Agent ??= Components.Get<NavMeshAgent>();

		if ( Agent is not null && Agent.IsValid() )
			_chaseMoveSpeed = Agent.MaxSpeed;

		if ( HomePosition == default )
			HomePosition = GameObject.WorldPosition;

		if ( Scene.IsValid() )
			BuildNavMeshSync.EnsureBuildTraversalSettings( Scene );

		if ( Vitals is not null )
		{
			Vitals.OnDied += OnDeath;
			Vitals.OnDamaged += OnDamaged;
		}

		if ( Locomotion is not null )
			Locomotion.Landed += OnLocomotionLanded;

		EnterState( EnemyAiState.Idle );
	}

	protected override void OnDestroy()
	{
		if ( Vitals is not null )
		{
			Vitals.OnDied -= OnDeath;
			Vitals.OnDamaged -= OnDamaged;
		}

		if ( Locomotion is not null )
			Locomotion.Landed -= OnLocomotionLanded;
	}

	public void SetHomePosition( Vector3 home ) => HomePosition = home;

	public void BeginChaseNow() => BeginAiNow();

	public void BeginAiNow()
	{
		_needsImmediatePathCheck = true;

		var player = FindNearestPlayer( DetectionRange );
		if ( !player.IsValid() )
			return;

		_target = player;
		_lastKnownPlayerPos = player.WorldPosition;
		_playerLastSeenAt = Time.NowDouble;
		EnterState( EnemyAiState.Tracking, player );
	}

	public void OnNavBakeComplete() => _needsImmediatePathCheck = true;

	public void OnStructureBlockerChanged()
	{
		if ( _state is EnemyAiState.Tracking or EnemyAiState.AttackObstacle or EnemyAiState.Search )
			_needsImmediatePathCheck = true;
	}

	void OnLocomotionLanded()
	{
		Locomotion ??= Components.Get<EntityLocomotion>();
		Locomotion?.SyncAgentFromRoot();
		_needsImmediatePathCheck = true;
	}

	void OnDamaged( Component attacker )
	{
		if ( attacker is null || !attacker.GameObject.IsValid() )
			return;

		if ( attacker.Components.Get<PlayerController>() is null )
			return;

		var player = attacker.GameObject;
		_target = player;
		_lastKnownPlayerPos = player.WorldPosition;
		_playerLastSeenAt = Time.NowDouble;
		EnterState( EnemyAiState.Tracking, player );
	}

	protected override void OnUpdate()
	{
		if ( !CanRunHostLogic() )
			return;

		if ( Vitals is not null && Vitals.IsDead )
			return;

		EntityCombat ??= Components.Get<EntityCombat>();
		Locomotion ??= Components.Get<EntityLocomotion>();
		Agent ??= Components.Get<NavMeshAgent>();
		if ( EntityCombat is null )
			return;

		if ( Scene.IsValid() )
			BuildNavMeshSync.TickPendingLocalBakes( Scene );

		if ( _state == EnemyAiState.Tracking && TryEnterAttackFromTracking() )
			return;

		if ( Locomotion is not null && (Locomotion.IsAirborne || Locomotion.IsSpawnSettling) )
		{
			Agent?.Stop();
			if ( _target.IsValid() && Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition ) <= AttackRange )
				EntityCombat.TickCombat( _target );
			return;
		}

		switch ( _state )
		{
			case EnemyAiState.Idle:
				TickIdle();
				break;
			case EnemyAiState.Wander:
				TickWander();
				break;
			case EnemyAiState.Alert:
				TickAlert();
				break;
			case EnemyAiState.Tracking:
				TickTracking();
				break;
			case EnemyAiState.Attacking:
				TickAttacking();
				break;
			case EnemyAiState.AttackObstacle:
				TickAttackObstacle();
				break;
			case EnemyAiState.Search:
				TickSearch();
				break;
			case EnemyAiState.ReturnHome:
				TickReturnHome();
				break;
		}
	}

	void TickIdle()
	{
		Locomotion?.SetLookTarget( null );
		StopAndIdle();

		var player = FindNearestPlayer( DetectionRange );
		if ( player.IsValid() )
		{
			EnterState( EnemyAiState.Alert, player );
			return;
		}

		if ( Time.NowDouble >= _stateEndsAt )
			EnterState( EnemyAiState.Wander );
	}

	void TickWander()
	{
		Locomotion?.SetLookTarget( null );

		var player = FindNearestPlayer( DetectionRange );
		if ( player.IsValid() )
		{
			EnterState( EnemyAiState.Alert, player );
			return;
		}

		if ( _wanderGoal == default )
		{
			EnterState( EnemyAiState.Idle );
			return;
		}

		var anchor = Locomotion?.GetNavAnchorWorld() ?? GameObject.WorldPosition;
		var distToGoal = Vector3.DistanceBetween( anchor.WithZ( 0f ), _wanderGoal.WithZ( 0f ) );

		if ( distToGoal <= WanderReachDistance )
		{
			EnterState( EnemyAiState.Idle );
			return;
		}

		if ( Agent is not null && !Agent.IsNavigating )
		{
			if ( _wanderStuckSince <= 0d )
				_wanderStuckSince = Time.NowDouble;
			else if ( Time.NowDouble - _wanderStuckSince >= WanderStuckSeconds )
			{
				if ( TryPickWanderGoal() )
					_wanderStuckSince = 0d;
				else
					EnterState( EnemyAiState.Idle );
				return;
			}
		}
		else
		{
			_wanderStuckSince = 0d;
		}

		if ( ShouldRunPathCheck() )
			TryIssueNavMove( _wanderGoal, 0f );
	}

	void TickAlert()
	{
		RefreshCombatTarget();

		if ( !_target.IsValid() )
		{
			if ( Time.NowDouble - _playerLastSeenAt >= AlertLostSeconds )
				EnterState( EnemyAiState.Idle );
			return;
		}

		Locomotion?.SetLookTarget( _target );
		StopAndIdle();
		UpdateLastKnownPlayer( _target );

		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );
		if ( dist > AcquireRange )
		{
			EnterState( EnemyAiState.Idle );
			return;
		}

		if ( dist <= DetectionRange && Time.NowDouble - _stateEndsAt >= 0d )
			EnterState( EnemyAiState.Tracking, _target );
	}

	void TickTracking()
	{
		if ( Time.NowDouble < _nextTrackingThinkAt )
			return;

		_nextTrackingThinkAt = Time.NowDouble + TrackingThinkInterval;

		RefreshCombatTarget();

		if ( !_target.IsValid() )
		{
			if ( Time.NowDouble - _playerLastSeenAt >= TrackingLostSeconds )
				EnterState( EnemyAiState.Search );
			else
				StopAndIdle();
			return;
		}

		UpdateLastKnownPlayer( _target );
		Locomotion?.SetLookTarget( _target );

		if ( IsTooFarFromHome() )
		{
			EnterState( EnemyAiState.ReturnHome );
			return;
		}

		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );

		if ( EntityCombat.IsMovementLocked )
		{
			Agent?.Stop();
			return;
		}

		if ( !ShouldRunPathCheck( dist ) )
			return;

		var standOff = GetChaseStandOffForDistance( dist );
		var navGoal = GetChasePoint( _target, standOff );
		var path = TryIssueNavMove( navGoal, standOff, GetPathCheckIntervalForDistance( dist ) );
		if ( path is null )
			return;

		if ( path.Value.HasPath && path.Value.Status == NavMeshPathStatus.Complete )
			return;

		if ( EntityPathfinding.IsRouteBlockedByStructure( Scene, path.Value, GetNavOrigin(), navGoal, GameObject ) )
		{
			var blocker = EntityPathfinding.TryFindBlockingStructure( Scene, GetNavOrigin(), navGoal, GameObject );
			if ( blocker is not null )
			{
				EnterState( EnemyAiState.AttackObstacle, blocker );
				return;
			}
		}

		if ( !path.Value.HasPath )
		{
			var blocker = EntityPathfinding.TryFindBlockingStructure( Scene, GetNavOrigin(), navGoal, GameObject );
			if ( blocker is not null )
				EnterState( EnemyAiState.AttackObstacle, blocker );
			else if ( Time.NowDouble - _playerLastSeenAt >= TrackingLostSeconds )
				EnterState( EnemyAiState.Search );
		}
	}

	bool TryEnterAttackFromTracking()
	{
		if ( _state != EnemyAiState.Tracking || !_target.IsValid() || EntityCombat is { IsMovementLocked: true } )
			return false;

		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );
		if ( dist > AttackRange + 20f )
			return false;

		EnterState( EnemyAiState.Attacking, _target );
		TickAttacking();
		return true;
	}

	void TickAttacking()
	{
		if ( !_target.IsValid() )
		{
			EnterState( EnemyAiState.Search );
			return;
		}

		Locomotion?.SetLookTarget( _target );
		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );

		if ( EntityCombat.IsMovementLocked )
		{
			Agent?.Stop();
			EntityCombat.TickCombat( _target );
			return;
		}

		if ( dist > AttackRange )
		{
			EnterState( EnemyAiState.Tracking, _target );
			return;
		}

		Agent?.Stop();
		EntityCombat.TickCombat( _target );
	}

	void TickAttackObstacle()
	{
		if ( _obstacleTarget is null || !_obstacleTarget.IsValid() || !_obstacleTarget.GameObject.IsValid() )
		{
			EnterState( _target.IsValid() ? EnemyAiState.Tracking : EnemyAiState.Search );
			return;
		}

		if ( Time.NowDouble >= _stateEndsAt )
		{
			EnterState( _target.IsValid() ? EnemyAiState.Search : EnemyAiState.ReturnHome );
			return;
		}

		RefreshCombatTarget();
		if ( _target.IsValid() )
			UpdateLastKnownPlayer( _target );

		var obstaclePos = _obstacleTarget.GameObject.WorldPosition;
		Locomotion?.SetLookTarget( _obstacleTarget.GameObject );

		if ( _target.IsValid() )
		{
			var chaseGoal = GetChasePoint( _target, ChaseStandOff );
			var path = EntityChaseRouting.QueryPath( Scene, GetNavOrigin(), chaseGoal, Agent );
			if ( path.HasPath && path.Status == NavMeshPathStatus.Complete
			     && !EntityPathfinding.IsRouteBlockedByStructure( Scene, path, GetNavOrigin(), chaseGoal, GameObject ) )
			{
				EnterState( EnemyAiState.Tracking, _target );
				return;
			}
		}

		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, obstaclePos );
		if ( dist > AttackRange )
		{
			if ( ShouldRunPathCheck() )
				TryIssueNavMove( obstaclePos, Math.Max( 24f, ChaseStandOff * 0.5f ) );
			EntityCombat.ResetCycle();
			return;
		}

		Agent?.Stop();
		EntityCombat.TickCombat( _obstacleTarget.GameObject );
	}

	void TickSearch()
	{
		Locomotion?.SetLookTarget( null );

		var player = FindNearestPlayer( DetectionRange );
		if ( player.IsValid() )
		{
			EnterState( EnemyAiState.Tracking, player );
			return;
		}

		if ( Time.NowDouble >= _stateEndsAt )
		{
			EnterState( EnemyAiState.ReturnHome );
			return;
		}

		if ( _lastKnownPlayerPos == default )
		{
			EnterState( EnemyAiState.ReturnHome );
			return;
		}

		EntityCombat.ResetCycle();

		if ( ShouldRunPathCheck() )
			TryIssueNavMove( _lastKnownPlayerPos, 32f );
	}

	void TickReturnHome()
	{
		Locomotion?.SetLookTarget( null );

		var player = FindNearestPlayer( DetectionRange );
		if ( player.IsValid() )
		{
			EnterState( EnemyAiState.Tracking, player );
			return;
		}

		var anchor = Locomotion?.GetNavAnchorWorld() ?? GameObject.WorldPosition;
		var distHome = Vector3.DistanceBetween( anchor.WithZ( 0f ), HomePosition.WithZ( 0f ) );
		if ( distHome <= HomeArriveDistance )
		{
			EnterState( EnemyAiState.Idle );
			return;
		}

		EntityCombat.ResetCycle();

		if ( ShouldRunPathCheck() )
			TryIssueNavMove( HomePosition, 24f );
	}

	void EnterState( EnemyAiState next, GameObject player = null )
	{
		if ( player.IsValid() )
			_target = player;

		_state = next;
		_needsImmediatePathCheck = true;
		_obstacleTarget = null;

		switch ( next )
		{
			case EnemyAiState.Idle:
				_stateEndsAt = Time.NowDouble + Sandbox.Game.Random.Float( IdleMinSeconds, IdleMaxSeconds );
				StopAndIdle();
				break;
			case EnemyAiState.Wander:
				if ( !TryPickWanderGoal() )
					EnterState( EnemyAiState.Idle );
				else
					TryIssueNavMove( _wanderGoal, 0f );
				break;
			case EnemyAiState.Alert:
				_stateEndsAt = Time.NowDouble + AlertConfirmSeconds;
				_playerLastSeenAt = Time.NowDouble;
				StopAndIdle();
				break;
			case EnemyAiState.Tracking:
				EntityCombat?.SetEngaged( true );
				_nextTrackingThinkAt = Time.NowDouble;
				break;
			case EnemyAiState.Attacking:
				EntityCombat?.SetEngaged( true );
				Agent?.Stop();
				if ( _target.IsValid() )
					EntityCombat?.TickCombat( _target );
				break;
			case EnemyAiState.AttackObstacle:
				_stateEndsAt = Time.NowDouble + ObstacleTimeoutSeconds;
				break;
			case EnemyAiState.Search:
				_stateEndsAt = Time.NowDouble + SearchDurationSeconds;
				break;
			case EnemyAiState.ReturnHome:
				_target = null;
				EntityCombat?.SetEngaged( false );
				break;
		}

		ApplyAgentSpeedForState( next );
	}

	void EnterState( EnemyAiState next, BuildPiece obstacle )
	{
		_obstacleTarget = obstacle;
		_state = next;
		_needsImmediatePathCheck = true;
		_stateEndsAt = Time.NowDouble + ObstacleTimeoutSeconds;
		EntityCombat?.SetEngaged( true );
		ApplyAgentSpeedForState( next );
	}

	void ApplyAgentSpeedForState( EnemyAiState state )
	{
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() )
			return;

		var walk = state is EnemyAiState.Idle or EnemyAiState.Wander or EnemyAiState.Alert
		           or EnemyAiState.Search or EnemyAiState.ReturnHome;
		Agent.MaxSpeed = walk ? WanderMoveSpeed : _chaseMoveSpeed;
	}

	bool TryPickWanderGoal()
	{
		var origin = HomePosition != default ? HomePosition : GameObject.WorldPosition;
		if ( !EntityPathfinding.TryFindWanderPoint( Scene, origin, WanderRadius, Agent, out var point ) )
			return false;

		_wanderGoal = point;
		_wanderStuckSince = 0d;
		_needsImmediatePathCheck = true;
		return true;
	}

	void RefreshCombatTarget()
	{
		if ( Time.NowDouble < _nextTargetRefreshAt && _target.IsValid() && IsValidPlayerTarget( _target ) )
		{
			var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );
			if ( dist <= AcquireRange )
				return;
		}

		_nextTargetRefreshAt = Time.NowDouble + 0.5;

		if ( _target.IsValid() && IsValidPlayerTarget( _target ) )
		{
			var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );
			if ( dist <= AcquireRange )
				return;
		}

		var found = FindNearestPlayer( AcquireRange );
		if ( found.IsValid() )
			_target = found;
		else if ( !_target.IsValid() || Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition ) > AcquireRange )
			_target = null;
	}

	void UpdateLastKnownPlayer( GameObject player )
	{
		if ( !player.IsValid() )
			return;

		_lastKnownPlayerPos = player.WorldPosition;
		_playerLastSeenAt = Time.NowDouble;
	}

	bool IsTooFarFromHome()
	{
		if ( HomePosition == default )
			return false;

		var anchor = Locomotion?.GetNavAnchorWorld() ?? GameObject.WorldPosition;
		return Vector3.DistanceBetween( anchor.WithZ( 0f ), HomePosition.WithZ( 0f ) ) > MaxHomeDistance;
	}

	bool ShouldRunPathCheck( float distToTarget = float.MaxValue )
	{
		if ( EntityCombat is { IsMovementLocked: true } )
			return false;

		if ( _needsImmediatePathCheck )
			return true;

		if ( distToTarget <= AttackRange * 1.75f )
			return Time.NowDouble >= _nextPathCheckAt;

		return Time.NowDouble >= _nextPathCheckAt;
	}

	float GetChaseStandOffForDistance( float distToTarget )
	{
		if ( distToTarget <= AttackRange + 24f )
			return 0f;

		if ( distToTarget <= AttackRange + ChaseStandOff )
			return Math.Max( 8f, ChaseStandOff * 0.35f );

		return ChaseStandOff;
	}

	float GetPathCheckIntervalForDistance( float distToTarget )
	{
		if ( distToTarget <= AttackRange * 1.75f )
			return ClosePathCheckInterval;

		if ( distToTarget <= 600f )
			return PathCheckInterval;

		return Math.Max( PathCheckInterval, 1.1f );
	}

	EntityChaseRouting.NavPathQuery? TryIssueNavMove( Vector3 goal, float standOff, float pathInterval ) =>
		RunPathCheckTo( goal, standOff, pathInterval );

	EntityChaseRouting.NavPathQuery? TryIssueNavMove( Vector3 goal, float standOff ) =>
		RunPathCheckTo( goal, standOff, PathCheckInterval );

	EntityChaseRouting.NavPathQuery? RunPathCheckTo( Vector3 goal, float standOff, float pathInterval )
	{
		if ( Agent is null || !Agent.IsValid() || !Scene.IsValid() )
		{
			LastNavBlockReason = "noAgent";
			return null;
		}

		if ( BuildNavMeshSync.IsNavGenerating( Scene ) )
		{
			LastNavBlockReason = "navGenerating";
			return null;
		}

		var origin = GetNavOrigin();
		var navGoal = standOff > 1f
			? EntityChaseRouting.OffsetChaseGoal( goal, origin, standOff )
			: goal;

		if ( !_needsImmediatePathCheck && Agent.IsNavigating )
		{
			var targetMoved = Vector3.DistanceBetween( goal.WithZ( 0f ), _lastPathTargetPos.WithZ( 0f ) );
			var goalDrift = Vector3.DistanceBetween( navGoal.WithZ( 0f ), _issuedNavGoal.WithZ( 0f ) );
			if ( targetMoved < PathTargetMoveThreshold && goalDrift < 72f )
			{
				LastNavBlockReason = "cruising";
				_nextPathCheckAt = Time.NowDouble + pathInterval;
				return null;
			}
		}

		_lastPathTargetPos = goal;

		Vector3? startOnNavHint = null;
		var verifyNav = _needsImmediatePathCheck || Time.NowDouble >= _nextNavVerifyAt || !Agent.IsNavigating;
		if ( verifyNav )
		{
			if ( !TryResolveNavOrigin( origin, out var onNav ) )
			{
				LastNavBlockReason = "startOffNav";
				LastPathStatus = NavMeshPathStatus.StartNotFound;
				return null;
			}

			startOnNavHint = onNav;
			_nextNavVerifyAt = Time.NowDouble + NavVerifyIntervalSeconds;
		}
		else
		{
			startOnNavHint = origin;
		}

		_needsImmediatePathCheck = false;
		_nextPathCheckAt = Time.NowDouble + Math.Max( 0.12f, pathInterval );

		origin = GetNavOrigin();
		navGoal = standOff > 1f
			? EntityChaseRouting.OffsetChaseGoal( goal, origin, standOff )
			: goal;

		LastNavGoal = navGoal;
		var pathQuery = EntityChaseRouting.QueryPath( Scene, origin, navGoal, Agent, NavProjectTier.Fast, startOnNavHint );
		LastPathStatus = pathQuery.Status;

		_lastPathPoints.Clear();
		if ( pathQuery.Points is not null )
			_lastPathPoints.AddRange( pathQuery.Points );

		if ( !pathQuery.HasPath )
		{
			LastNavBlockReason = $"noPath({pathQuery.Status})";
			return pathQuery;
		}

		if ( Agent.IsNavigating && (_issuedNavGoal - navGoal).Length < 48f )
		{
			LastNavBlockReason = "alreadyNavigating";
			return pathQuery;
		}

		if ( Agent.IsNavigating )
			Agent.Stop();

		Locomotion ??= Components.Get<EntityLocomotion>();
		Locomotion?.SyncAgentFromRoot();
		Agent.MoveTo( navGoal );
		_issuedNavGoal = navGoal;
		LastNavBlockReason = "moveIssued";

		return pathQuery;
	}

	Vector3 GetNavOrigin() =>
		Agent is not null && Agent.IsValid() ? Agent.AgentPosition : GameObject.WorldPosition;

	bool TryResolveNavOrigin( Vector3 origin, out Vector3 onNav )
	{
		onNav = default;
		if ( BuildNavMeshSync.IsNavGenerating( Scene ) )
		{
			LastNavBlockReason = "navGenerating";
			return false;
		}

		if ( EntityNavMeshUtility.TryProjectToNavMesh( Scene, origin, out onNav, NavProjectTier.Fast ) )
			return true;

		if ( EntityNavMeshUtility.TryProjectToNavMesh( Scene, origin, out onNav, NavProjectTier.Full ) )
		{
			if ( (onNav - origin).Length > 8f )
			{
				GameObject.WorldPosition = onNav;
				Agent.SetAgentPosition( onNav );
			}

			return true;
		}

		if ( EntityNavMeshUtility.TryProjectToNavMesh( Scene, GameObject.WorldPosition, out onNav, NavProjectTier.Full ) )
		{
			GameObject.WorldPosition = onNav;
			Agent.SetAgentPosition( onNav );
			return true;
		}

		return false;
	}

	Vector3 GetChasePoint( GameObject target, float standOff )
	{
		if ( target is null || !target.IsValid() || !Scene.IsValid() )
			return Vector3.Zero;

		var playerAnchor = EntityLocomotion.GetNavAnchorWorld( target );
		var now = Time.NowDouble;
		Vector3 navGoal;

		if ( now - _cachedChaseNavGoalAt < ChaseGoalCacheSeconds
		     && Vector3.DistanceBetween( playerAnchor.WithZ( 0f ), _cachedChasePlayerPos.WithZ( 0f ) ) < ChaseGoalMoveThreshold )
		{
			navGoal = _cachedChaseNavGoal;
		}
		else
		{
			navGoal = EntityLocomotion.GetNavChasePoint( Scene, playerAnchor );
			_cachedChaseNavGoal = navGoal;
			_cachedChasePlayerPos = playerAnchor;
			_cachedChaseNavGoalAt = now;
		}

		if ( standOff <= 1f )
			return navGoal;

		return EntityChaseRouting.OffsetChaseGoal( navGoal, GetNavOrigin(), standOff );
	}

	bool CanRunHostLogic()
	{
		if ( !Active || !GameObject.IsValid() || GameObject.IsProxy )
			return false;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return false;

		return true;
	}

	GameObject FindNearestPlayer( float maxRange )
	{
		var scene = Scene;
		if ( !scene.IsValid() )
			return null;

		GameObject best = null;
		var bestDist = float.MaxValue;
		var origin = GameObject.WorldPosition;

		foreach ( var playerVitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( !IsValidPlayerTarget( playerVitals.GameObject ) )
				continue;

			var dist = Vector3.DistanceBetween( origin, playerVitals.GameObject.WorldPosition );
			if ( dist >= bestDist || dist > maxRange )
				continue;

			bestDist = dist;
			best = playerVitals.GameObject;
		}

		return best;
	}

	bool IsValidPlayerTarget( GameObject go )
	{
		if ( go is null || !go.IsValid() || go == GameObject )
			return false;

		if ( go.Components.Get<PlayerController>() is null )
			return false;

		if ( go.Components.Get<EntityBrain>() is not null )
			return false;

		if ( go.Components.Get<PlayerVitals>() is { } pv && pv.CurrentHealth <= 0.001f )
			return false;

		return true;
	}

	void StopAndIdle()
	{
		EntityCombat?.ResetCycle();
		Agent?.Stop();
	}

	void OnDeath()
	{
		_target = null;
		_obstacleTarget = null;
		EntityCombat?.ResetCycle();
		Agent?.Stop();
		Enabled = false;

		if ( GameObject.IsProxy )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		GameObject.Destroy();
	}

	public bool ShouldSkipNavRepath() =>
		EntityCombat is { IsMovementLocked: true };

	public void RequestImmediateRepath()
	{
		if ( ShouldSkipNavRepath() )
			return;

		_needsImmediatePathCheck = true;
	}
}
