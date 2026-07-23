using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Navigation;

namespace Survival;

/// <summary>
/// Host-only enemy AI: Idle → Wander; Searching on noise;
/// once alerted → always nav to the live player and attack until geometric LOS has been lost for
/// <see cref="ChaseLosLostAbandonSeconds"/>; Retreating at low HP.
/// </summary>
[Title( "Entity Brain" )]
public sealed class EntityBrain : Component
{
	[Property] public EntityCombat EntityCombat { get; set; }
	[Property] public EntityVitals Vitals { get; set; }
	[Property] public EntityLocomotion Locomotion { get; set; }
	[Property] public NavMeshAgent Agent { get; set; }

	[Property, Group( "Home" )] public Vector3 HomePosition { get; set; }

	[Property, Group( "Ranges" )] public float AttackRange { get; set; } = 110f;
	[Property, Group( "Ranges" ), Title( "Find-player radius while chasing (units)" )]
	public float ChaseAbandonRange { get; set; } = 2400f;
	[Property, Group( "Ranges" ), Title( "Abandon chase after not seen (seconds)" ), Description( "Geometric LOS lost for this long ends the chase. Live player is tracked the whole time." )]
	public float ChaseLosLostAbandonSeconds { get; set; } = 30f;

	[Property, Group( "Timing" )] public float PathCheckInterval { get; set; } = 0.65f;
	[Property, Group( "Timing" )] public float ClosePathCheckInterval { get; set; } = 0.28f;
	[Property, Group( "Timing" )] public float ChaseThinkInterval { get; set; } = 0.15f;
	[Property, Group( "Timing" )] public float WanderStuckSeconds { get; set; } = 3f;

	[Property, Group( "Wander" )] public float WanderReachDistance { get; set; } = 96f;
	[Property, Group( "Wander" ), Title( "Walk speed while idle / wander / search" )]
	public float WanderMoveSpeed { get; set; } = 88f;
	[Property, Group( "Wander" ), Title( "Run speed while chasing / attacking / retreating" )]
	public float ChaseMoveSpeed { get; set; } = 220f;

	[Property, Group( "Debug" ), Title( "Log AI state changes" )]
	public bool LogPerceptionDebug { get; set; }

	EntityPerceptionProfile _perception = EntityPerceptionProfile.CreateFallback();

	EnemyAiState _state = EnemyAiState.Idle;
	GameObject _target;
	Vector3 _issuedNavGoal;
	Vector3 _wanderGoal;
	Vector3 _lastKnownPlayerPos;
	Vector3 _stimulusPos;
	Vector3 _searchGoal;
	Vector3 _searchNoisePos;
	Vector3 _retreatGoal;
	Vector3 _retreatStart;
	float _alertMeter;
	string _lastAlertFillReason = "";
	string _lastLosDetail = "";
	double _stateEndsAt;
	double _nextPathCheckAt;
	double _wanderStuckSince;
	double _nextChaseThinkAt;
	double _nextNavVerifyAt;
	Vector3 _lastPathTargetPos;
	double _nextDebugLogAt;
	double _alertDecayPausedUntil;
	double _retreatBlockedUntil;
	double _searchStartedAt;
	double _nextNoiseRetargetAt;
	bool _needsImmediatePathCheck;
	bool _hasLastKnown;
	bool _hasStimulus;
	bool _hasSearchGoal;
	bool _alertLocked;
	bool _aiStarted;
	bool _awaitingNavToStartAi;
	bool _agentOnNav;
	double _nextNavSettleAt;
	/// <summary>Last time geometric LOS on the chase target was true (starts when chase begins).</summary>
	double _chaseLastSeenAt;
	EnemyAiState _loggedState = (EnemyAiState)(-1);

	const float NavVerifyIntervalSeconds = 4f;
	const float PathTargetMoveThreshold = 64f;
	const float LiveChaseRepathMoveThreshold = 96f;
	const float MinSearchSeconds = 6f;
	/// <summary>Minimum flat distance for a search leg so contact alerts still cause a real walk.</summary>
	const float MinSearchLegDistance = 180f;
	const float AlertTurnDegreesPerSecond = 303.75f;

	public EnemyAiState CurrentState => _state;
	public GameObject ChaseTarget => _target.IsValid() ? _target : null;
	public float AlertMeter01 =>
		_perception.AlertThreshold > 1e-3f ? Math.Clamp( _alertMeter / _perception.AlertThreshold, 0f, 1f ) : 0f;
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
			ChaseMoveSpeed = Math.Max( ChaseMoveSpeed, Agent.MaxSpeed );

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

		// Population calls BeginAiNow (or WaitForNavThenStartAi). Don't auto-wander here.
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

	public void ApplyPerception( EntityPerceptionProfile profile )
	{
		_perception = profile;
		IdleMinSeconds = profile.IdleMinSeconds;
		IdleMaxSeconds = profile.IdleMaxSeconds;
		ChaseAbandonRange = Math.Max( ChaseAbandonRange, profile.SightRange * 3f );
	}

	// Mirrored onto properties so archetype / designers can still see idle range in inspector when not from JSON.
	[Property, Group( "Timing" )] public float IdleMinSeconds { get; set; } = 1f;
	[Property, Group( "Timing" )] public float IdleMaxSeconds { get; set; } = 3f;

	public void BeginAiNow()
	{
		_awaitingNavToStartAi = false;
		_aiStarted = true;
		_needsImmediatePathCheck = true;
		_alertMeter = 0f;
		_alertLocked = false;
		_hasSearchGoal = false;
		EnterState( EnemyAiState.Wander );
	}

	/// <summary>Spawn landed but nav tiles not ready — start wander once <see cref="OnNavBakeComplete"/> snaps the agent.</summary>
	public void WaitForNavThenStartAi()
	{
		_awaitingNavToStartAi = true;
		_aiStarted = false;
		StopAndIdle();
	}

	/// <summary>Nav mesh placement succeeded — enable agent-driven movement.</summary>
	public void MarkAgentOnNav()
	{
		_agentOnNav = true;
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is not null && Agent.IsValid() )
			Agent.UpdatePosition = true;
	}

	/// <summary>Host: player noises fill the alert meter (idle/wander) or retarget investigate/chase (alerted).</summary>
	public void TryHearNoise( Vector3 noiseWorldPos, EntityNoiseKind kind, GameObject source )
	{
		if ( !CanRunHostLogic() )
			return;

		if ( Vitals is not null && Vitals.IsDead )
			return;

		// Already hunting: noise does not fill the meter, but it DOES move the investigate / last-known goal.
		if ( _state is EnemyAiState.Searching or EnemyAiState.Chasing )
		{
			if ( TryAcceptNoiseForRetarget( noiseWorldPos, kind, out var heardAt ) )
				RetargetInvestigationFromNoise( heardAt, source );
			return;
		}

		if ( _state == EnemyAiState.Attacking )
			return;

		if ( kind is EntityNoiseKind.Run )
		{
			RememberStimulus( noiseWorldPos, source );
			return;
		}

		if ( kind == EntityNoiseKind.SneakFootstep )
		{
			var sneakRange = _perception.SneakAlertRange;
			if ( sneakRange <= 1e-3f )
				return;

			var sneakDist = Vector3.DistanceBetween( GameObject.WorldPosition, noiseWorldPos );
			if ( sneakDist > sneakRange )
				return;

			RememberStimulus( noiseWorldPos, source );
			var sneakProx = WalkLoudness01( sneakDist, sneakRange );
			AddAlertMeter( _perception.SneakFillPerSecond * sneakProx * 0.4f, "sneak:step" );
			TryEnterChaseFromMeter();
			return;
		}

		if ( kind == EntityNoiseKind.Footstep )
		{
			var walkRange = _perception.WalkAlertRange;
			var dist = Vector3.DistanceBetween( GameObject.WorldPosition, noiseWorldPos );
			if ( dist > walkRange )
				return;

			RememberStimulus( noiseWorldPos, source );
			var prox = WalkLoudness01( dist, walkRange );
			AddAlertMeter( _perception.WalkFillPerSecond * prox * 0.4f, "walk:step" );
			TryEnterChaseFromMeter();
			return;
		}

		var range = kind is EntityNoiseKind.ChopTree or EntityNoiseKind.Swing
			? _perception.ToolAlertRange
			: 0f;
		if ( range <= 1e-3f )
			return;

		var toolDist = Vector3.DistanceBetween( GameObject.WorldPosition, noiseWorldPos );
		if ( toolDist > range )
			return;

		RememberStimulus( noiseWorldPos, source );
		var toolProx = Math.Max( 0.25f, 1f - toolDist / range );
		AddAlertMeter( _perception.ToolFillPerHit * toolProx, $"tool:{kind}" );
		TryEnterChaseFromMeter();
	}

	bool TryAcceptNoiseForRetarget( Vector3 noiseWorldPos, EntityNoiseKind kind, out Vector3 heardAt )
	{
		heardAt = noiseWorldPos;
		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, noiseWorldPos );

		var maxRange = kind switch
		{
			EntityNoiseKind.Run => _perception.SprintIgnoreBeyond,
			EntityNoiseKind.Footstep => _perception.WalkAlertRange,
			EntityNoiseKind.SneakFootstep => _perception.SneakAlertRange,
			EntityNoiseKind.ChopTree or EntityNoiseKind.Swing => _perception.ToolAlertRange,
			_ => 0f
		};

		if ( maxRange <= 1e-3f || dist > maxRange )
			return false;

		return true;
	}

	/// <summary>
	/// Alerted hunt: lock onto the noisy player and chase them.
	/// Already chasing/attacking: only refresh target — do not re-enter state (that rebakes nav).
	/// </summary>
	void RetargetInvestigationFromNoise( Vector3 noiseWorldPos, GameObject source )
	{
		RememberStimulus( noiseWorldPos, source );
		_lastAlertFillReason = "hear:retarget";

		if ( source.IsValid() && IsValidPlayerTarget( source ) )
			_target = source;
		else if ( !_target.IsValid() )
			_target = FindNearestPlayer( ChaseAbandonRange );

		if ( _state is EnemyAiState.Chasing or EnemyAiState.Attacking )
		{
			if ( _target.IsValid() )
				RememberLastKnown( _target.WorldPosition );
			return;
		}

		if ( _state == EnemyAiState.Searching && _target.IsValid() )
			EnterState( EnemyAiState.Chasing, _target );
	}

	public void OnNavBakeComplete()
	{
		_needsImmediatePathCheck = true;
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() || !Scene.IsValid() )
			return;

		if ( !EntityNavMeshUtility.EnsureAgentOnNavMesh( Scene, Agent, GameObject.WorldPosition ) )
			return;

		_agentOnNav = true;
		Agent.UpdatePosition = true;

		if ( _awaitingNavToStartAi || !_aiStarted )
		{
			BeginAiNow();
			return;
		}

		// Nav just arrived — re-pick wander so Idle↔failed-Wander loops start walking.
		if ( _state is EnemyAiState.Idle or EnemyAiState.Wander )
			EnterState( EnemyAiState.Wander );
	}

	public void OnStructureBlockerChanged()
	{
		if ( _state is EnemyAiState.Chasing or EnemyAiState.Searching or EnemyAiState.Retreating )
			_needsImmediatePathCheck = true;
	}

	void OnLocomotionLanded()
	{
		Locomotion ??= Components.Get<EntityLocomotion>();
		Locomotion?.SyncAgentFromRoot();
		_needsImmediatePathCheck = true;

		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() || !Scene.IsValid() )
			return;

		if ( !EntityNavMeshUtility.EnsureAgentOnNavMesh( Scene, Agent, GameObject.WorldPosition ) )
			return;

		_agentOnNav = true;
		Agent.UpdatePosition = true;
		if ( _awaitingNavToStartAi || !_aiStarted )
			BeginAiNow();
		else if ( _state is EnemyAiState.Idle or EnemyAiState.Wander )
			EnterState( EnemyAiState.Wander );
	}

	void OnDamaged( Component attacker )
	{
		if ( attacker is null || !attacker.GameObject.IsValid() )
			return;

		if ( attacker.Components.Get<PlayerController>() is null )
			return;

		var player = attacker.GameObject;
		_target = player;
		RememberLastKnown( player.WorldPosition );
		RememberStimulus( player.WorldPosition, player );
		_alertMeter = _perception.AlertThreshold;
		_alertLocked = true;
		// Taking hits while low HP interrupts flee so they can fight back briefly.
		_retreatBlockedUntil = Time.NowDouble + 6d;
		EnterState( EnemyAiState.Chasing, player );
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

		TickNavSettle();

		if ( _state is EnemyAiState.Idle or EnemyAiState.Wander or EnemyAiState.Searching
		     or EnemyAiState.Chasing or EnemyAiState.Retreating )
			TickAmbientAlert( Time.Delta );

		if ( CanStartRetreat() )
		{
			EnterState( EnemyAiState.Retreating );
			return;
		}

		if ( _state == EnemyAiState.Chasing && TryEnterAttackFromChase() )
			return;

		if ( Locomotion is not null && (Locomotion.IsAirborne || Locomotion.IsSpawnSettling) )
		{
			Agent?.Stop();
			if ( _target.IsValid()
			     && HasGeometricLos( _target )
			     && Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition ) <= AttackRange )
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
			case EnemyAiState.Searching:
				TickSearching();
				break;
			case EnemyAiState.Chasing:
				TickChasing();
				break;
			case EnemyAiState.Attacking:
				TickAttacking();
				break;
			case EnemyAiState.Retreating:
				TickRetreating();
				break;
		}

		TickPerceptionDebug();
	}

	void TickAmbientAlert( float dt )
	{
		if ( dt <= 0f || !Scene.IsValid() )
			return;

		// Alerted: meter stays locked, but walk/sprint noise still retargets investigate / last-known.
		if ( _state is EnemyAiState.Searching or EnemyAiState.Chasing )
		{
			TickHuntNoiseRetarget();
			return;
		}

		if ( _state == EnemyAiState.Attacking || _alertLocked )
			return;

		var origin = GameObject.WorldPosition;
		var facing = GameObject.WorldRotation.Forward;
		var eye = origin + Vector3.Up * _perception.EyeHeight;
		var anyFill = false;

		foreach ( var pv in Scene.GetAllComponents<PlayerVitals>() )
		{
			var go = pv.GameObject;
			if ( !IsValidPlayerTarget( go ) )
				continue;

			var dist = Vector3.DistanceBetween( origin, go.WorldPosition );
			var move = go.Components.Get<PlayerMovement>();

			// Close FOV + LOS fills ~1s at sightClose; slower toward max sight range.
			if ( dist <= _perception.SightRange
			     && EntitySight.IsInFov( origin, facing, go.WorldPosition, _perception.SightFovDegrees )
			     && EntitySight.HasClearLos( Scene, eye, GameObject, go, _perception.EyeHeight, out _lastLosDetail ) )
			{
				RememberStimulus( go.WorldPosition, go );
				var t = _perception.SightRange > 1e-3f
					? Math.Clamp( dist / _perception.SightRange, 0f, 1f )
					: 1f;
				var rate = dist <= _perception.SightClose
					? _perception.SightFillPerSecondClose
					: (_perception.SightFillPerSecondClose
					   + (_perception.SightFillPerSecondFar - _perception.SightFillPerSecondClose) * t );
				AddAlertMeter( rate * dt, "sight" );
				anyFill = true;
			}

			var sprinting = move is not null && move.IsSprintingForNoise();
			var sneaking = move is not null && move.IsSneakingForNoise();
			var walking = move is not null && move.IsWalkingForNoise();

			if ( sprinting )
			{
				if ( dist > _perception.SprintIgnoreBeyond )
					continue;

				RememberStimulus( go.WorldPosition, go );

				if ( dist <= _perception.SprintContact )
				{
					AddAlertMeter( _perception.AlertThreshold, "sprint:contact" );
					anyFill = true;
					continue;
				}

				// Wider hear range + distance loudness: slow at ~12–14 m, clearer as they close.
				var loud = WalkLoudness01( dist, _perception.SprintIgnoreBeyond );
				float rate;
				string reason;
				if ( dist <= _perception.SprintMid )
				{
					rate = _perception.SprintFillPerSecondNear;
					reason = "sprint:near";
				}
				else if ( dist <= _perception.SprintMid + (_perception.SprintIgnoreBeyond - _perception.SprintMid) * 0.45f )
				{
					rate = _perception.SprintFillPerSecondMid;
					reason = "sprint:mid";
				}
				else
				{
					rate = _perception.SprintFillPerSecondFar;
					reason = "sprint:far";
				}

				AddAlertMeter( rate * loud * dt, reason );
				anyFill = true;
				continue;
			}

			if ( sneaking )
			{
				if ( _perception.SneakAlertRange <= 1e-3f || dist > _perception.SneakAlertRange )
					continue;

				RememberStimulus( go.WorldPosition, go );
				var sneakProx = WalkLoudness01( dist, _perception.SneakAlertRange );
				AddAlertMeter( _perception.SneakFillPerSecond * sneakProx * dt, "sneak" );
				anyFill = true;
				continue;
			}

			if ( walking && dist <= _perception.WalkAlertRange )
			{
				RememberStimulus( go.WorldPosition, go );
				var prox = WalkLoudness01( dist, _perception.WalkAlertRange );
				AddAlertMeter( _perception.WalkFillPerSecond * prox * dt, "walk" );
				anyFill = true;
			}
		}

		if ( !anyFill
		     && _perception.AlertDecayPerSecond > 0f
		     && Time.NowDouble >= _alertDecayPausedUntil
		     && _state is EnemyAiState.Idle or EnemyAiState.Wander )
		{
			_alertMeter = Math.Max( 0f, _alertMeter - _perception.AlertDecayPerSecond * dt );
		}

		TryEnterChaseFromMeter();
	}

	/// <summary>While Searching/Chasing: keep the live player target from audible movement.</summary>
	void TickHuntNoiseRetarget()
	{
		if ( !Scene.IsValid() || Time.NowDouble < _nextNoiseRetargetAt )
			return;

		_nextNoiseRetargetAt = Time.NowDouble + 0.35;

		foreach ( var pv in Scene.GetAllComponents<PlayerVitals>() )
		{
			var go = pv.GameObject;
			if ( !IsValidPlayerTarget( go ) )
				continue;

			var move = go.Components.Get<PlayerMovement>();
			if ( move is null )
				continue;

			var dist = Vector3.DistanceBetween( GameObject.WorldPosition, go.WorldPosition );
			var audible = false;
			if ( move.IsSprintingForNoise() && dist <= _perception.SprintIgnoreBeyond )
				audible = true;
			else if ( move.IsWalkingForNoise() && dist <= _perception.WalkAlertRange )
				audible = true;
			else if ( move.IsSneakingForNoise() && _perception.SneakAlertRange > 1e-3f && dist <= _perception.SneakAlertRange )
				audible = true;

			if ( !audible )
				continue;

			_target = go;
			RememberStimulus( go.WorldPosition, go );
			if ( _state == EnemyAiState.Searching )
				EnterState( EnemyAiState.Chasing, go );
			return;
		}
	}

	void AddAlertMeter( float amount, string reason )
	{
		if ( amount <= 1e-4f || _alertLocked )
			return;

		if ( _alertMeter >= _perception.AlertThreshold )
		{
			_alertMeter = _perception.AlertThreshold;
			return;
		}

		_alertMeter = Math.Min( _perception.AlertThreshold, _alertMeter + amount );
		_lastAlertFillReason = reason;
		_alertDecayPausedUntil = Time.NowDouble + 0.4;
	}

	/// <summary>
	/// Forest loudness 0–1: quiet at max range, much louder up close (squared falloff).
	/// At 5 m walk range: ~0.08 edge, ~0.32 at 3 m, ~1 at contact.
	/// </summary>
	static float WalkLoudness01( float dist, float range )
	{
		if ( range <= 1e-3f || dist >= range )
			return 0f;

		var linear = 1f - dist / range;
		var loud = linear * linear;
		// Barely audible at the outer rim so "5 m slowly" still ticks.
		return Math.Max( 0.08f, loud );
	}

	bool TryEnterChaseFromMeter()
	{
		if ( _alertMeter < _perception.AlertThreshold )
			return false;

		if ( _state is EnemyAiState.Chasing or EnemyAiState.Attacking )
			return false;

		_alertMeter = _perception.AlertThreshold;
		_alertLocked = true;

		var player = _target.IsValid() && IsValidPlayerTarget( _target )
			? _target
			: FindNearestPlayer( ChaseAbandonRange );

		if ( !player.IsValid() && TryFindVisiblePlayer( out var seen ) )
			player = seen;

		if ( player.IsValid() )
		{
			EnterState( EnemyAiState.Chasing, player );
			return true;
		}

		if ( _state != EnemyAiState.Searching )
			EnterState( EnemyAiState.Searching );
		return true;
	}

	void TickNavSettle()
	{
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() || !Scene.IsValid() )
			return;

		if ( !_awaitingNavToStartAi && _aiStarted && _agentOnNav )
			return;

		if ( Time.NowDouble < _nextNavSettleAt )
			return;

		_nextNavSettleAt = Time.NowDouble + 0.45;

		var pos = GameObject.WorldPosition;
		BuildNavMeshSync.EnsureNavAroundPoint( Scene, pos );
		if ( !EntityNavMeshUtility.EnsureAgentOnNavMesh( Scene, Agent, pos ) )
			return;

		_agentOnNav = true;
		Agent.UpdatePosition = true;
		if ( _awaitingNavToStartAi || !_aiStarted )
			BeginAiNow();
		else if ( _state is EnemyAiState.Idle or EnemyAiState.Wander )
			EnterState( EnemyAiState.Wander );
	}

	/// <summary>Agent is placed on nav and allowed to drive the transform.</summary>
	bool IsNavAgentReady() =>
		Agent is not null && Agent.IsValid() && Agent.Enabled && _agentOnNav && Agent.UpdatePosition;

	void TickIdle()
	{
		Locomotion?.SetLookTarget( null );
		Locomotion?.ClearTravelHint();
		StopAndIdle();

		if ( Time.NowDouble >= _stateEndsAt )
			EnterState( EnemyAiState.Wander );
	}

	void TickWander()
	{
		Locomotion?.SetLookTarget( null );
		// Chase re-applies speed every frame; wander must too or ForwardOnly can leave MaxSpeed at 0.
		ApplyAgentSpeed( run: false );

		if ( _wanderGoal == default )
		{
			Locomotion?.ClearTravelHint();
			EnterState( EnemyAiState.Idle );
			return;
		}

		Locomotion?.SetTravelHint( _wanderGoal );

		var anchor = Locomotion?.GetNavAnchorWorld() ?? GameObject.WorldPosition;
		var distToGoal = Vector3.DistanceBetween( anchor.WithZ( 0f ), _wanderGoal.WithZ( 0f ) );
		if ( distToGoal <= WanderReachDistance )
		{
			EnterState( EnemyAiState.Idle );
			return;
		}

		Agent ??= Components.Get<NavMeshAgent>();
		var agentCanDrive = IsNavAgentReady();

		if ( agentCanDrive )
		{
			var wishSpd = Agent.WishVelocity.WithZ( 0f ).Length;
			var speedStuck = Agent.IsNavigating && Agent.MaxSpeed < 1f && wishSpd < 8f;
			if ( !Agent.IsNavigating || speedStuck )
			{
				if ( _wanderStuckSince <= 0d )
					_wanderStuckSince = Time.NowDouble;
				else if ( Time.NowDouble - _wanderStuckSince >= (speedStuck ? 0.6 : WanderStuckSeconds) )
				{
					if ( speedStuck )
						Agent.Stop();

					if ( TryPickWanderGoal() )
					{
						_wanderStuckSince = 0d;
						_needsImmediatePathCheck = true;
						TryIssueNavMove( _wanderGoal, 0f );
						Agent.MoveTo( _wanderGoal );
					}
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
			{
				var issued = TryIssueNavMove( _wanderGoal, 0f );
				// Streamed terrain often fails QueryPath even when tiles exist — still ask the agent to go.
				if ( issued is null || !issued.Value.HasPath )
					Agent.MoveTo( _wanderGoal );
			}

			return;
		}

		// No nav agent drive yet — walk toward the goal on the heightfield so scavs aren't statues.
		TickManualWanderStep();
	}

	void TickManualWanderStep()
	{
		var pos = GameObject.WorldPosition;
		var to = (_wanderGoal - pos).WithZ( 0f );
		var flat = to.Length;
		if ( flat <= WanderReachDistance )
		{
			EnterState( EnemyAiState.Idle );
			return;
		}

		var dir = to / flat;
		var speed = Math.Max( 48f, WanderMoveSpeed );
		var dt = Math.Max( Time.Delta, 1e-4f );
		var next = pos + dir * (speed * dt);

		Locomotion ??= Components.Get<EntityLocomotion>();
		if ( Locomotion is not null && Locomotion.TrySampleTerrainGroundZ( next, out var groundZ ) )
		{
			// Soft approach — don't teleport Z to the heightfield each wander tick.
			var climb = Math.Clamp( groundZ - next.z, -140f * dt, 72f * dt );
			next.z += climb;
		}

		GameObject.WorldPosition = next;
		if ( dir.LengthSquared > 1e-6f )
			GameObject.WorldRotation = Rotation.LookAt( dir, Vector3.Up );
		// Don't SetAgentPosition every wander tick — that hitch is the step stutter.
	}

	void TickSearching()
	{
		// Prefer live chase as soon as any player is in abandon range.
		var player = FindNearestPlayer( ChaseAbandonRange );
		if ( player.IsValid() )
		{
			EnterState( EnemyAiState.Chasing, player );
			return;
		}

		if ( !_hasSearchGoal )
			FreezeSearchGoalFromStimulus();

		FaceSearchNoiseDirection();
		ApplyAgentSpeed( run: false );

		if ( Time.NowDouble - _searchStartedAt >= MinSearchSeconds )
		{
			DropChaseToIdle();
			return;
		}

		if ( ShouldRunPathCheck() )
			TryIssueNavMove( _searchGoal, 0f );
	}

	void TickChasing()
	{
		if ( !_target.IsValid() || !IsValidPlayerTarget( _target ) )
			_target = FindNearestPlayer( ChaseAbandonRange );

		if ( !_target.IsValid() )
		{
			DropChaseToIdle();
			return;
		}

		_alertMeter = _perception.AlertThreshold;
		_alertLocked = true;
		ApplyAgentSpeed( run: true );

		// Seen = geometric LOS. Always nav to the live player while alerted.
		var hasLos = HasGeometricLos( _target );
		if ( hasLos )
			_chaseLastSeenAt = Time.NowDouble;

		if ( ChaseLosLostAbandonSeconds > 0f
		     && Time.NowDouble - _chaseLastSeenAt >= ChaseLosLostAbandonSeconds )
		{
			DropChaseToIdle();
			return;
		}

		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );
		if ( dist <= AttackRange && hasLos )
		{
			EnterState( EnemyAiState.Attacking, _target );
			return;
		}

		Locomotion?.SetPreferAimOverVelocity( false );
		Locomotion?.SetLookTarget( _target );
		if ( Locomotion is not null )
			Locomotion.TurnDegreesPerSecond = AlertTurnDegreesPerSecond;

		if ( Time.NowDouble < _nextChaseThinkAt )
			return;

		_nextChaseThinkAt = Time.NowDouble + ChaseThinkInterval;

		if ( !ShouldRunPathCheck( dist ) )
			return;

		TryIssueNavMove(
			GetLiveChasePoint( _target ),
			0f,
			ClosePathCheckInterval,
			LiveChaseRepathMoveThreshold );
	}

	/// <summary>Locomotion clipped a wall — request a fresh live repath, nothing else.</summary>
	public void NotifyChasePhysicsBlocked( GameObject hit )
	{
		if ( _state != EnemyAiState.Chasing )
			return;

		_needsImmediatePathCheck = true;
		_nextChaseThinkAt = 0d;
		_issuedNavGoal = default;
		LastNavBlockReason = "physicsClip";
	}

	void DropChaseToIdle()
	{
		_alertMeter = 0f;
		_alertLocked = false;
		_hasSearchGoal = false;
		_chaseLastSeenAt = 0d;
		_target = null;
		EnterState( EnemyAiState.Idle, forceIdleSeconds: _perception.PostLostIdleSeconds );
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
			var go = playerVitals.GameObject;
			if ( !IsValidPlayerTarget( go ) )
				continue;

			var dist = Vector3.DistanceBetween( origin, go.WorldPosition );
			if ( dist > maxRange || dist >= bestDist )
				continue;

			bestDist = dist;
			best = go;
		}

		return best;
	}

	void FreezeSearchGoalFromStimulus()
	{
		Vector3 noise;
		if ( _hasStimulus )
			noise = _stimulusPos;
		else if ( _hasLastKnown )
			noise = _lastKnownPlayerPos;
		else
			noise = GameObject.WorldPosition + GameObject.WorldRotation.Forward * MinSearchLegDistance;

		noise = ProjectSearchPointToNav( noise );
		_searchNoisePos = noise;
		RememberLastKnown( noise );

		_searchStartedAt = Time.NowDouble;
		_wanderStuckSince = 0d;
		_hasSearchGoal = true;

		// Contact / on-top-of-noise: don't "arrive" instantly — walk a real investigate leg.
		_searchGoal = BuildInvestigateGoal( noise );
		_needsImmediatePathCheck = true;

		// Face the heard direction over time — no instant snap.
		Locomotion?.SetPreferAimOverVelocity( true );
		Locomotion?.SetFrozenAimWorld( _searchNoisePos );
	}

	/// <summary>Face the heard noise at alert turn rate (no snap).</summary>
	void FaceSearchNoiseDirection()
	{
		Locomotion?.SetLookTarget( null );

		var origin = GameObject.WorldPosition;
		var toNoise = (_searchNoisePos - origin).WithZ( 0f );
		if ( toNoise.LengthSquared < 1e-4f )
			toNoise = (_searchGoal - origin).WithZ( 0f );
		if ( toNoise.LengthSquared < 1e-4f )
			return;

		var desire = toNoise.Normal;
		var facing = GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( facing.LengthSquared < 1e-6f )
			facing = Vector3.Forward;
		else
			facing = facing.Normal;

		Locomotion?.SmoothFaceTowardWorld( origin + desire * 64f, AlertTurnDegreesPerSecond );
	}

	Vector3 BuildInvestigateGoal( Vector3 noise )
	{
		var origin = GameObject.WorldPosition;
		var flatDist = Vector3.DistanceBetween( origin.WithZ( 0f ), noise.WithZ( 0f ) );

		// Prefer pathing around cover toward the noise — never a straight line into a wall.
		if ( IsOccludedToStand( origin, noise ) && TryFindReachableApproachToPoint( noise, out var around ) )
			return around;

		if ( flatDist >= MinSearchLegDistance )
			return noise;

		if ( TryFindReachableApproachToPoint( noise, out var ring ) )
			return ring;

		var away = (noise - origin).WithZ( 0f );
		if ( away.LengthSquared < 1e-4f )
			away = GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( away.LengthSquared < 1e-4f )
			away = Vector3.Forward;

		var past = noise + away.Normal * MinSearchLegDistance;
		return ProjectSearchPointToNav( past );
	}

	Vector3 ProjectSearchPointToNav( Vector3 world )
	{
		if ( Scene.IsValid()
		     && EntityNavMeshUtility.TryProjectToNavMesh( Scene, world, out var onNav, NavProjectTier.Full ) )
			return onNav;

		return world.WithZ( GameObject.WorldPosition.z );
	}

	bool TryEnterAttackFromChase()
	{
		if ( _state != EnemyAiState.Chasing || !_target.IsValid() || EntityCombat is { IsMovementLocked: true } )
			return false;

		if ( !HasGeometricLos( _target ) )
			return false;

		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );
		if ( dist > AttackRange )
			return false;

		EnterState( EnemyAiState.Attacking, _target );
		return true;
	}

	void TickAttacking()
	{
		if ( !_target.IsValid() || !IsValidPlayerTarget( _target ) )
		{
			// Never abort mid-swing / recovery — finish the cycle first.
			if ( EntityCombat is { IsMovementLocked: true } )
			{
				Agent?.Stop();
				return;
			}

			_target = FindNearestPlayer( ChaseAbandonRange );
			if ( _target.IsValid() )
			{
				EnterState( EnemyAiState.Chasing, _target );
				return;
			}

			DropChaseToIdle();
			return;
		}

		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );
		var hasLos = HasGeometricLos( _target );

		if ( hasLos )
		{
			_chaseLastSeenAt = Time.NowDouble;
			RememberLastKnown( _target.WorldPosition );
		}

		Agent?.Stop();

		// Telegraph / swing / recovery: hold state + yaw; do not chase-cancel or re-aim.
		if ( EntityCombat is { IsMovementLocked: true } )
		{
			EntityCombat.TickCombat( _target );
			return;
		}

		if ( ChaseLosLostAbandonSeconds > 0f
		     && Time.NowDouble - _chaseLastSeenAt >= ChaseLosLostAbandonSeconds )
		{
			DropChaseToIdle();
			return;
		}

		// Only leave after the full attack cycle finished.
		if ( dist > AttackRange * 1.75f || !hasLos )
		{
			EnterState( EnemyAiState.Chasing, _target );
			return;
		}

		EntityCombat.TickCombat( _target );
	}

	void TickRetreating()
	{
		// Player still nearby while fleeing — re-engage live chase.
		var nearby = FindNearestPlayer( ChaseAbandonRange * 0.5f );
		if ( nearby.IsValid() )
		{
			EnterState( EnemyAiState.Chasing, nearby );
			return;
		}

		var anchor = Locomotion?.GetNavAnchorWorld() ?? GameObject.WorldPosition;
		var fled = Vector3.DistanceBetween( _retreatStart.WithZ( 0f ), anchor.WithZ( 0f ) );
		if ( fled >= _perception.RetreatDistance * 0.92f
		     || (_retreatGoal != default
		         && Vector3.DistanceBetween( anchor.WithZ( 0f ), _retreatGoal.WithZ( 0f ) ) <= WanderReachDistance) )
		{
			_alertMeter = 0f;
			EnterState( EnemyAiState.Idle );
			return;
		}

		ApplyAgentSpeed( run: true );
		Locomotion?.SetLookTarget( null );

		if ( !ShouldRunPathCheck() )
			return;

		var path = TryIssueNavMove( _retreatGoal, 0f );
		if ( path is null || path.Value.HasPath )
			return;

		// Wall / shoreline — slant flee direction along the obstacle.
		if ( TryPickSlantedRetreatGoal( out var slanted ) )
		{
			_retreatGoal = slanted;
			_needsImmediatePathCheck = true;
			TryIssueNavMove( _retreatGoal, 0f );
		}
	}

	bool CanStartRetreat()
	{
		if ( _state == EnemyAiState.Retreating )
			return false;

		if ( Time.NowDouble < _retreatBlockedUntil )
			return false;

		return ShouldRetreat();
	}

	bool ShouldRetreat()
	{
		if ( Vitals is null || Vitals.MaxHealth <= 1e-3f )
			return false;

		var max = Vitals.CurrentHealthMax > 1e-3f ? Vitals.CurrentHealthMax : Vitals.MaxHealth;
		return Vitals.CurrentHealth / max <= _perception.RetreatHealthFraction;
	}

	void EnterState( EnemyAiState next, GameObject target = null, float? forceIdleSeconds = null )
	{
		_state = next;
		_needsImmediatePathCheck = true;
		_wanderStuckSince = 0d;

		if ( next != EnemyAiState.Searching )
			Locomotion?.SetPreferAimOverVelocity( false );

		if ( next != EnemyAiState.Chasing )
			Locomotion?.SetBrainOwnsFacing( false );

		if ( target.IsValid() )
			_target = target;

		switch ( next )
		{
			case EnemyAiState.Idle:
			{
				if ( forceIdleSeconds.HasValue )
					_stateEndsAt = Time.NowDouble + forceIdleSeconds.Value;
				else
					_stateEndsAt = Time.NowDouble + Sandbox.Game.Random.Float(
						IdleMinSeconds, Math.Max( IdleMinSeconds, IdleMaxSeconds ) );
				StopAndIdle();
				EntityCombat?.SetEngaged( false );
				EntityCombat?.ResetCycle();
				ApplyAgentSpeed( run: false );
				break;
			}
			case EnemyAiState.Wander:
				EntityCombat?.SetEngaged( false );
				EntityCombat?.ResetCycle();
				ApplyAgentSpeed( run: false );
				if ( !TryPickWanderGoal() )
				{
					EnterState( EnemyAiState.Idle );
					return;
				}

				_needsImmediatePathCheck = true;
				TryIssueNavMove( _wanderGoal, 0f );
				Agent ??= Components.Get<NavMeshAgent>();
				if ( IsNavAgentReady() )
					Agent.MoveTo( _wanderGoal );
				break;
			case EnemyAiState.Searching:
				EntityCombat?.SetEngaged( false );
				EntityCombat?.ResetCycle();
				ApplyAgentSpeed( run: false );
				_alertMeter = _perception.AlertThreshold;
				_alertLocked = true;
				FreezeSearchGoalFromStimulus();
				// Do not Agent.Stop() — they must walk the investigate legs.
				break;
			case EnemyAiState.Chasing:
				EntityCombat?.SetEngaged( false );
				if ( EntityCombat is not { IsMovementLocked: true } )
					EntityCombat?.ResetCycle();
				ApplyAgentSpeed( run: true );
				_alertMeter = _perception.AlertThreshold;
				_alertLocked = true;
				_nextChaseThinkAt = Time.NowDouble;
				// Seed unseen clock only on a fresh chase — Attack↔Chase must not reset the 30s.
				if ( _chaseLastSeenAt <= 0d )
					_chaseLastSeenAt = Time.NowDouble;
				if ( target.IsValid() )
				{
					RememberLastKnown( target.WorldPosition );
					Locomotion?.SetLookTarget( target );
					Locomotion?.SetPreferAimOverVelocity( false );
					if ( Locomotion is not null )
						Locomotion.TurnDegreesPerSecond = AlertTurnDegreesPerSecond;
					if ( HasGeometricLos( target ) )
						_chaseLastSeenAt = Time.NowDouble;
					_needsImmediatePathCheck = true;
				}
				break;
			case EnemyAiState.Attacking:
				EntityCombat?.SetEngaged( true );
				ApplyAgentSpeed( run: true );
				Agent?.Stop();
				Locomotion?.SetLookTarget( null );
				Locomotion?.SetPreferAimOverVelocity( false );
				break;
			case EnemyAiState.Retreating:
				EntityCombat?.SetEngaged( false );
				EntityCombat?.ResetCycle();
				ApplyAgentSpeed( run: true );
				_retreatStart = GameObject.WorldPosition;
				_retreatGoal = BuildRetreatGoal();
				break;
		}
	}

	void ApplyAgentSpeed( bool run )
	{
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() )
			return;

		var speed = run ? Math.Max( 160f, ChaseMoveSpeed ) : WanderMoveSpeed;
		Locomotion ??= Components.Get<EntityLocomotion>();
		if ( Locomotion is not null )
			Locomotion.SetIntendedMaxSpeed( speed );
		else
			Agent.MaxSpeed = speed;
	}

	bool TryPickWanderGoal()
	{
		var origin = GameObject.WorldPosition;
		var facing = GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( facing.LengthSquared < 1e-6f )
			facing = Vector3.Forward;
		else
			facing = facing.Normal;

		var radius = Math.Max( 160f, _perception.WanderDistance );
		var yaw = Sandbox.Game.Random.Float( -70f, 70f );
		var dir = Rotation.FromYaw( yaw ) * facing;
		var ideal = origin + dir * radius;

		// Prefer a projected nav point — do not require a precomputed path (streamed terrain often fails that gate).
		if ( EntityNavMeshUtility.TryProjectToNavMesh( Scene, ideal, out var onNav, NavProjectTier.Full ) )
		{
			_wanderGoal = onNav;
			_wanderStuckSince = 0d;
			_needsImmediatePathCheck = true;
			return true;
		}

		if ( EntityPathfinding.TryFindWanderPoint( Scene, origin, radius, Agent, out var point ) )
		{
			_wanderGoal = point;
			_wanderStuckSince = 0d;
			_needsImmediatePathCheck = true;
			return true;
		}

		// Last resort: walk toward the ideal XY even without nav tiles (manual wander uses heightfield).
		_wanderGoal = ideal.WithZ( origin.z );
		_wanderStuckSince = 0d;
		_needsImmediatePathCheck = true;
		return true;
	}

	Vector3 BuildRetreatGoal()
	{
		var origin = GameObject.WorldPosition;
		var awayFrom = _hasLastKnown ? _lastKnownPlayerPos : (_hasStimulus ? _stimulusPos : origin + GameObject.WorldRotation.Forward);
		var flee = (origin - awayFrom).WithZ( 0f );
		if ( flee.LengthSquared < 1e-4f )
			flee = -GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( flee.LengthSquared < 1e-4f )
			flee = Vector3.Forward;
		flee = flee.Normal;

		var ideal = origin + flee * _perception.RetreatDistance;
		if ( EntityNavMeshUtility.TryProjectToNavMesh( Scene, ideal, out var onNav, NavProjectTier.Full ) )
			return onNav;

		return ideal;
	}

	bool TryPickSlantedRetreatGoal( out Vector3 goal )
	{
		goal = default;
		var origin = GameObject.WorldPosition;
		var baseDir = (_retreatGoal - origin).WithZ( 0f );
		if ( baseDir.LengthSquared < 1e-4f )
			baseDir = (origin - (_hasLastKnown ? _lastKnownPlayerPos : origin)).WithZ( 0f );
		if ( baseDir.LengthSquared < 1e-4f )
			return false;

		baseDir = baseDir.Normal;
		float[] angles = { 35f, -35f, 70f, -70f, 110f, -110f };
		var remain = Math.Max( _perception.WanderDistance, _perception.RetreatDistance * 0.35f );

		foreach ( var angle in angles )
		{
			var dir = Rotation.FromYaw( angle ) * baseDir;
			var probe = origin + dir * remain;
			if ( !EntityNavMeshUtility.TryProjectToNavMesh( Scene, probe, out var onNav, NavProjectTier.Full ) )
				continue;

			if ( !EntityChaseRouting.QueryPath( Scene, origin, onNav, Agent ).HasPath )
				continue;

			goal = onNav;
			return true;
		}

		return false;
	}

	/// <summary>
	/// True when physics says something solid is between us and the goal, but the nav path is
	/// nearly a straight line (mesh leaking through the wall). Those paths make agents face-plant.
	/// </summary>
	bool IsNavShortcutThroughWall( Vector3 origin, Vector3 goal, EntityChaseRouting.NavPathQuery path )
	{
		if ( !path.HasPath )
			return IsOccludedToStand( origin, goal );

		if ( !IsOccludedToStand( origin, goal ) && !PathCrossesSolid( path ) )
			return false;

		var straight = Vector3.DistanceBetween( origin.WithZ( 0f ), goal.WithZ( 0f ) );
		if ( straight < 48f )
			return false;

		// Real around-path should be meaningfully longer than the straight shot.
		return path.Length < straight * 1.45f + 96f;
	}

	bool PathCrossesSolid( EntityChaseRouting.NavPathQuery path )
	{
		if ( path.Points is null || path.Points.Count < 2 || !Scene.IsValid() )
			return false;

		for ( var i = 0; i < path.Points.Count - 1; i++ )
		{
			var a = path.Points[i] + Vector3.Up * 36f;
			var b = path.Points[i + 1] + Vector3.Up * 36f;
			var tr = Scene.Trace.Ray( a, b )
				.Radius( 8f )
				.UsePhysicsWorld()
				.IgnoreGameObjectHierarchy( GameObject )
				.Run();

			if ( !tr.Hit || !tr.GameObject.IsValid() )
				continue;

			// Floor hits are fine; vertical-ish walls are not.
			if ( tr.Normal.z < 0.55f )
				return true;
		}

		return false;
	}

	bool IsOccludedToStand( Vector3 origin, Vector3 stand )
	{
		if ( !Scene.IsValid() )
			return false;

		var eye = origin + Vector3.Up * _perception.EyeHeight;
		var target = stand + Vector3.Up * _perception.EyeHeight;
		var dist = Vector3.DistanceBetween( eye, target );
		if ( dist <= 8f )
			return false;

		var tr = Scene.Trace.Ray( eye, target )
			.Radius( 4f )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return false;

		if ( tr.Normal.z > 0.55f )
			return false;

		return tr.Distance < dist - 12f;
	}

	bool TryFindReachableApproachToPoint( Vector3 stand, out Vector3 approach )
	{
		approach = default;
		if ( !Scene.IsValid() || Agent is null || !Agent.IsValid() )
			return false;

		var origin = GameObject.WorldPosition;
		var toStand = (stand - origin).WithZ( 0f );
		if ( toStand.LengthSquared < 1e-4f )
			return false;

		var forward = toStand.Normal;
		var right = Vector3.Cross( Vector3.Up, forward ).Normal;
		var straight = toStand.Length;
		// Tight rings first — wide 400–560 probes caused the “back up 5m then path” feel.
		float[] radii = { 64f, 100f, 140f, 200f, 280f };
		float[] lateral = { 90f, -90f, 120f, -120f, 60f, -60f, 150f, -150f, 180f, -180f };

		foreach ( var radius in radii )
		{
			foreach ( var yaw in lateral )
			{
				var dir = Rotation.FromYaw( yaw ) * forward;
				var probe = stand + dir * radius + right * (MathF.Abs( yaw ) < 1f ? 0f : MathF.Sign( yaw ) * 48f);
				if ( !EntityNavMeshUtility.TryProjectToNavMesh( Scene, probe, out var onNav, NavProjectTier.Full ) )
					continue;

				// Never pick a stand behind the scav (away from the player) — that looks like a reverse.
				var toApproach = (onNav - origin).WithZ( 0f );
				if ( toApproach.LengthSquared > 1f
				     && Vector3.Dot( toApproach.Normal, forward ) < -0.15f
				     && toApproach.Length > 64f )
					continue;

				var q = EntityChaseRouting.QueryPath( Scene, origin, onNav, Agent, NavProjectTier.Fast );
				if ( !q.HasPath )
					continue;

				if ( IsNavShortcutThroughWall( origin, onNav, q ) )
					continue;

				if ( PathCrossesSolid( q ) )
					continue;

				// Prefer routes that are actually longer than the blocked straight shot.
				if ( q.Length < straight * 1.15f + 48f && IsOccludedToStand( origin, stand ) )
					continue;

				approach = onNav;
				return true;
			}
		}

		return false;
	}

	void RememberStimulus( Vector3 worldPos, GameObject source )
	{
		_stimulusPos = worldPos;
		_hasStimulus = true;
		if ( source.IsValid() && IsValidPlayerTarget( source ) )
			_target = source;
	}

	void RememberLastKnown( Vector3 worldPos )
	{
		_lastKnownPlayerPos = ProjectSearchPointToNav( worldPos );
		_hasLastKnown = true;
	}

	void TickPerceptionDebug()
	{
		EntityPerceptionDebug.Enabled = LogPerceptionDebug;
		if ( !LogPerceptionDebug )
			return;

		if ( _state == _loggedState && Time.NowDouble < _nextDebugLogAt )
			return;

		_loggedState = _state;
		_nextDebugLogAt = Time.NowDouble + 1.5;

		var alertPct = _perception.AlertThreshold > 1e-3f
			? (int)MathF.Round( 100f * _alertMeter / _perception.AlertThreshold )
			: 0;
		EntityPerceptionDebug.LogBrainState(
			GameObject.Name,
			_state.ToString(),
			$"alert={alertPct}% reason={_lastAlertFillReason}" );
	}

	bool HasGeometricLos( GameObject target )
	{
		if ( !target.IsValid() )
			return false;

		var eye = GameObject.WorldPosition + Vector3.Up * _perception.EyeHeight;
		return EntitySight.HasClearLos( Scene, eye, GameObject, target, _perception.EyeHeight, out _lastLosDetail );
	}

	bool TryFindVisiblePlayer( out GameObject player )
	{
		player = null;
		var scene = Scene;
		if ( !scene.IsValid() )
			return false;

		var origin = GameObject.WorldPosition;
		var facing = GameObject.WorldRotation.Forward;
		var eye = origin + Vector3.Up * _perception.EyeHeight;
		var maxRange = _perception.SightRange;
		var bestDist = float.MaxValue;

		foreach ( var playerVitals in scene.GetAllComponents<PlayerVitals>() )
		{
			var go = playerVitals.GameObject;
			if ( !IsValidPlayerTarget( go ) )
				continue;

			var dist = Vector3.DistanceBetween( origin, go.WorldPosition );
			if ( dist > maxRange || dist >= bestDist )
				continue;

			if ( !HasSightOn( go, origin, facing, eye ) )
				continue;

			bestDist = dist;
			player = go;
		}

		return player.IsValid();
	}

	bool HasSightOn( GameObject target ) =>
		HasSightOn( target, GameObject.WorldPosition, GameObject.WorldRotation.Forward,
			GameObject.WorldPosition + Vector3.Up * _perception.EyeHeight );

	bool HasSightOn( GameObject target, Vector3 origin, Vector3 facing, Vector3 eye )
	{
		if ( !target.IsValid() )
			return false;

		if ( !EntitySight.IsInFov( origin, facing, target.WorldPosition, _perception.SightFovDegrees ) )
			return false;

		return EntitySight.HasClearLos( Scene, eye, GameObject, target, _perception.EyeHeight, out _lastLosDetail );
	}

	bool ShouldRunPathCheck( float distToTarget = float.MaxValue )
	{
		// Attack telegraphs lock feet; chase/break abort must still be able to repath.
		if ( _state == EnemyAiState.Attacking && EntityCombat is { IsMovementLocked: true } )
			return false;

		if ( _needsImmediatePathCheck )
			return true;

		if ( distToTarget <= AttackRange * 1.75f )
			return Time.NowDouble >= _nextPathCheckAt;

		return Time.NowDouble >= _nextPathCheckAt;
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
		RunPathCheckTo( goal, standOff, pathInterval, PathTargetMoveThreshold );

	EntityChaseRouting.NavPathQuery? TryIssueNavMove( Vector3 goal, float standOff, float pathInterval, float cruiseMoveThreshold ) =>
		RunPathCheckTo( goal, standOff, pathInterval, cruiseMoveThreshold );

	EntityChaseRouting.NavPathQuery? TryIssueNavMove( Vector3 goal, float standOff ) =>
		RunPathCheckTo( goal, standOff, PathCheckInterval, PathTargetMoveThreshold );

	EntityChaseRouting.NavPathQuery? RunPathCheckTo( Vector3 goal, float standOff, float pathInterval, float cruiseMoveThreshold )
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
			var wishSpeed = Agent.WishVelocity.WithZ( 0f ).Length;
			// Agent.IsNavigating can stay true while wedged — don't "cruise" when not moving.
			if ( wishSpeed >= 12f )
			{
				var targetMoved = Vector3.DistanceBetween( goal.WithZ( 0f ), _lastPathTargetPos.WithZ( 0f ) );
				var goalDrift = Vector3.DistanceBetween( navGoal.WithZ( 0f ), _issuedNavGoal.WithZ( 0f ) );
				if ( targetMoved < cruiseMoveThreshold && goalDrift < Math.Max( 40f, cruiseMoveThreshold ) )
				{
					LastNavBlockReason = "cruising";
					_nextPathCheckAt = Time.NowDouble + pathInterval;
					return null;
				}
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

		var pathQuery = EntityChaseRouting.QueryPath( Scene, origin, navGoal, Agent, NavProjectTier.Fast, startOnNavHint );

		LastNavGoal = navGoal;
		LastPathStatus = pathQuery.Status;

		_lastPathPoints.Clear();
		if ( pathQuery.Points is not null )
			_lastPathPoints.AddRange( pathQuery.Points );

		if ( !pathQuery.HasPath )
		{
			LastNavBlockReason = $"noPath({pathQuery.Status})";
			return pathQuery;
		}

		if ( Agent.IsNavigating && (_issuedNavGoal - navGoal).Length < 96f )
		{
			LastNavBlockReason = "alreadyNavigating";
			return pathQuery;
		}

		// Retarget in place — avoid SyncAgentFromRoot every issue (yanks root sideways).
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
			// Only snap when projection is local — random far samples were launching entities.
			if ( (onNav - origin).Length <= 96f )
			{
				if ( (onNav - origin).Length > 8f )
					onNav = ApplyNavXyKeepTerrainZ( onNav );

				return true;
			}

			onNav = origin;
			return true;
		}

		if ( EntityNavMeshUtility.TryProjectToNavMesh( Scene, GameObject.WorldPosition, out onNav, NavProjectTier.Full )
		     && (onNav - GameObject.WorldPosition).Length <= 96f )
		{
			onNav = ApplyNavXyKeepTerrainZ( onNav );
			return true;
		}

		return false;
	}

	Vector3 ApplyNavXyKeepTerrainZ( Vector3 onNav )
	{
		// Keep current Z — locomotion soft-sticks to the heightfield. Snapping here caused ridge pops.
		var glued = onNav.WithZ( GameObject.WorldPosition.z );
		GameObject.WorldPosition = glued;
		Agent ??= Components.Get<NavMeshAgent>();
		Agent?.SetAgentPosition( glued );
		return glued;
	}

	Vector3 GetLiveChasePoint( GameObject target )
	{
		if ( target is null || !target.IsValid() || !Scene.IsValid() )
			return Vector3.Zero;

		var playerAnchor = EntityLocomotion.GetNavAnchorWorld( target );
		var selfZ = GameObject.WorldPosition.z;

		// Grappling / airborne: don't path to sky — chase the ground under their XY.
		if ( playerAnchor.z - selfZ > 96f )
			playerAnchor = playerAnchor.WithZ( selfZ );

		var navGoal = EntityLocomotion.GetNavChasePoint( Scene, playerAnchor );
		if ( MathF.Abs( navGoal.z - selfZ ) > 160f )
			navGoal = ProjectSearchPointToNav( playerAnchor.WithZ( selfZ ) );

		return navGoal;
	}

	bool CanRunHostLogic()
	{
		if ( !Active || !GameObject.IsValid() || GameObject.IsProxy )
			return false;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return false;

		return true;
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
		EntityCombat?.ResetCycle();
		Agent?.Stop();
		Enabled = false;

		if ( GameObject.IsProxy )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		Components.Get<BiomePopulationSlot>()?.HandleOwnerDied();
		GameObject.Destroy();
	}

	public bool ShouldSkipNavRepath() =>
		EntityCombat is { IsMovementLocked: true };

	public void RequestImmediateRepath()
	{
		_needsImmediatePathCheck = true;
	}
}
