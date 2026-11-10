using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Navigation;

namespace Survival;

/// <summary>
/// Host-only enemy AI: Idle → Wander; a rising alert meter first stops it to look at the stimulus, a full one chases what it
/// sees or searches where it heard something. Chasing hunts the live player while it sees them (and for the perception's
/// <c>chaseCommitLosSeconds</c> after — it saw which way they went), then the last place it saw them led by how they were
/// moving, then searches around there; <c>chaseLostLosSeconds</c> unseen ends the hunt. Close in, attackers take bearings on
/// a ring around the player and at most two hold attack tokens (<see cref="EntityAttackSlots"/>). Retreating at low HP,
/// committed until it got away or is cornered. Breaching (see <c>EntityBrain.Breach.cs</c>) when build pieces are what keeps
/// the player out of reach. Raiding (see <c>EntityBrain.Raid.cs</c>) for entities spawned by a base raid.
/// (Scav AI cleanup, Mark 2026-11-04: "The player tracking and entity states need work I think. Seems very artificial".)
/// </summary>
[Title( "Entity Brain" )]
public sealed partial class EntityBrain : Component
{
	[Property] public EntityCombat EntityCombat { get; set; }
	[Property] public EntityVitals Vitals { get; set; }
	[Property] public EntityLocomotion Locomotion { get; set; }
	[Property] public NavMeshAgent Agent { get; set; }

	[Property, Group( "Home" )] public Vector3 HomePosition { get; set; }
	/// <summary>Camp leash: distances measured from <see cref="LeashCenter"/> (the camp origin, shared by every guard). Zero = unbounded (free roam).</summary>
	[Property, Group( "Home" ), Title( "Leash center (world, camp origin)" )] public Vector3 LeashCenter { get; set; }
	[Property, Group( "Home" ), Title( "Wander distance from leash center (units, 0 = unbounded)" )] public float LeashWanderDistance { get; set; }
	[Property, Group( "Home" ), Title( "Max travel distance from leash center (units, 0 = unbounded)" )] public float LeashMaxTravelDistance { get; set; }
	public bool HasLeash => LeashWanderDistance > 0f;

	/// <summary>
	/// Root-to-root distance a swing connects at: the weapon class's lateral reach (meters in melee_weapon_classes.json,
	/// converted once by <see cref="PlayerCombat.GetMeleeAttackRangeUnits"/>) plus the target's body radius. It used to be a
	/// hard-coded 110 u against a 76 u blade, so scavs planted 2.75 m out and swung at the air.
	/// </summary>
	public float AttackRange => SwingReachUnits() + TargetBodyRadiusUnits;

	[Property, Group( "Ranges" ), Title( "Breaching: give up beyond (units)" )]
	public float ChaseAbandonRange { get; set; } = 2400f;

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
	double _lastMoveIssuedAt;
	/// <summary>A MoveTo this recent is still being computed — the agent reads "not navigating" meanwhile.</summary>
	const double MoveIssueSettleSeconds = 0.6;
	Vector3 _wanderGoal;
	Vector3 _lastKnownPlayerPos;
	Vector3 _stimulusPos;
	Vector3 _searchGoal;
	Vector3 _searchNoisePos;
	Vector3 _retreatGoal;
	Vector3 _retreatStart;
	Vector3 _returnGoal;
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
	/// <summary>Consecutive nav-settle attempts that found no nav; drives the settle back-off.</summary>
	int _navSettleFailures;
	const int NavSettleBakeRequestLimit = 3;
	/// <summary>Last time geometric LOS on the chase target was true (starts when chase begins).</summary>
	double _chaseLastSeenAt;
	EnemyAiState _loggedState = (EnemyAiState)(-1);

	const float NavVerifyIntervalSeconds = 4f;
	const float PathTargetMoveThreshold = 64f;
	const float LiveChaseRepathMoveThreshold = 96f;
	/// <summary>A search lasts this long (random in the range) before the entity gives up and idles.</summary>
	const float SearchSecondsMin = 8f, SearchSecondsMax = 14f;
	/// <summary>Minimum flat distance for a search leg so contact alerts still cause a real walk.</summary>
	const float MinSearchLegDistance = 180f;
	/// <summary>Search: extra spots checked around where the trail went cold (meters from it).</summary>
	const float SearchSpotMinMeters = 3f, SearchSpotMaxMeters = 7f;
	const float AlertTurnDegreesPerSecond = 303.75f;

	/// <summary>Player capsule radius added to the blade reach (root-to-root swing distance).</summary>
	const float TargetBodyRadiusUnits = 12f;
	/// <summary>Attacking hands back to Chasing past this many swing ranges (hysteresis; stepping in closes the gap first).</summary>
	const float LeaveAttackRangeFactor = 1.6f;
	/// <summary>Attack-token holders close to this fraction of the blade reach; waiters hold just outside it.</summary>
	const float StrikeRingFraction = 0.7f, WaitRingFraction = 1.35f;
	/// <summary>Inside this the chase goal is the attacker's ring slot, not the player's feet (meters).</summary>
	const float SlotEngageMeters = 6f;
	/// <summary>Past this the ring bearing is dropped, so a re-approach takes the side it comes from (meters).</summary>
	const float SlotReleaseMeters = 9f;
	/// <summary>A slot more than this off the side it stands on is re-claimed (no walking round the player to reach it).</summary>
	const float SlotReclaimDegrees = 100f;
	/// <summary>Step-in MoveTo re-issue interval while attacking.</summary>
	const float StepInRepathSeconds = 0.25f;

	/// <summary>Within this an entity senses a player outside its sight cone (LOS still needed), meters.</summary>
	const float ProximitySenseMeters = 3f;
	/// <summary>Sight / noise fill outside the cone (proximity sense) runs at this fraction of the in-cone rate.</summary>
	const float ProximitySenseFillFraction = 0.6f;
	/// <summary>Alert at or above this fraction (below full): stop and look at the stimulus — the "huh?" beat.</summary>
	const float NoticeAlertFraction = 0.3f;

	/// <summary>Once hunting, a player is still "seen" out to this many perception sight ranges (with line of sight).</summary>
	const float ChaseSightRangeFactor = 2f;
	/// <summary>Lost from sight: the trail goal is the last-seen spot plus the player's velocity for this long, capped (m).</summary>
	const float TrailLeadSeconds = 1.2f, TrailLeadMaxMeters = 6f;

	/// <summary>Low-HP retreat: re-engages only when a seen player is this close (cornered), meters.</summary>
	const float RetreatCorneredMeters = 3f;
	/// <summary>After a finished retreat it fights rather than flees again for this long.</summary>
	const float RetreatCooldownSeconds = 15f;
	/// <summary>A retreat ends after this long even if the flee distance was not covered.</summary>
	const float RetreatMaxSeconds = 12f;

	float _swingReachUnits;
	double _searchEndsAt;
	double _searchPauseUntil;
	Vector3 _searchLookDir;
	Vector3 _trailGoal;
	bool _trailGoalSet;
	Vector3 _lastSeenVelocity;
	Vector3 _velocitySamplePos;
	double _velocitySampleAt;
	double _nextStepInAt;
	bool _noticing;
	Vector3 _idleLookDir;

	public EnemyAiState CurrentState => _state;
	public GameObject ChaseTarget => _target.IsValid() ? _target : null;
	public float AlertMeter01 =>
		_perception.AlertThreshold > 1e-3f ? Math.Clamp( _alertMeter / _perception.AlertThreshold, 0f, 1f ) : 0f;
	public NavMeshPathStatus LastPathStatus { get; private set; }
	/// <summary>
	/// The last path physically runs through a wall / tree / rock. Nav can leak through thin
	/// solids and still report Complete — that is not a route, it is a wall to breach.
	/// </summary>
	public bool LastPathCrossesSolid { get; private set; }
	public string LastNavBlockReason { get; private set; } = "init";
	/// <summary>Diagnostics: seconds since the last MoveTo this brain issued (−1 = never).</summary>
	public double SecondsSinceMoveIssued => _lastMoveIssuedAt > 0d ? Time.NowDouble - _lastMoveIssuedAt : -1d;
	/// <summary>Diagnostics: seconds since this brain last entered a state (−1 = never).</summary>
	public double SecondsSinceStateChange => _stateEnteredAt > 0d ? Time.NowDouble - _stateEnteredAt : -1d;
	double _stateEnteredAt;
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

		if ( EntityCombat is not null )
			EntityCombat.AttackCycleFinished += OnAttackCycleFinished;

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

		if ( EntityCombat is not null )
			EntityCombat.AttackCycleFinished -= OnAttackCycleFinished;

		BreachClaims.Release( this );
		EntityAttackSlots.Release( this );
	}

	public void SetHomePosition( Vector3 home ) => HomePosition = home;

	/// <summary>
	/// Leash the entity to a camp: wander legs stay within <paramref name="wanderDistance"/> of
	/// <paramref name="center"/>; an alerted entity pulled past <paramref name="maxTravelDistance"/>
	/// drops its target and runs back to within wander distance (so players cannot lure a camp away).
	/// </summary>
	public void SetLeash( Vector3 center, float wanderDistance, float maxTravelDistance )
	{
		LeashCenter = center;
		LeashWanderDistance = Math.Max( 0f, wanderDistance );
		LeashMaxTravelDistance = Math.Max( LeashWanderDistance, maxTravelDistance );
	}

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

		// A hit, provoke or noise already put it on a target while the nav was still settling: keep that. Resetting here
		// wiped the chase and sent the scav off on a wander leg past the player (audit 2026-11-04).
		if ( _state is EnemyAiState.Chasing or EnemyAiState.Attacking or EnemyAiState.Searching or EnemyAiState.Breaching
		     or EnemyAiState.Retreating or EnemyAiState.Returning or EnemyAiState.Raiding )
			return;

		_alertMeter = 0f;
		_alertLocked = false;
		_hasSearchGoal = false;
		EnterState( IsRaider ? EnemyAiState.Raiding : EnemyAiState.Wander );
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

		// Raiders are after the beds — only a player inside the aggro range (or a hit) pulls them off.
		if ( _state == EnemyAiState.Raiding )
			return;

		// Already hunting: noise does not fill the meter, but it DOES move the investigate / last-known goal.
		if ( _state is EnemyAiState.Searching or EnemyAiState.Chasing or EnemyAiState.Breaching )
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

		if ( _state is EnemyAiState.Chasing or EnemyAiState.Attacking or EnemyAiState.Breaching )
		{
			if ( _target.IsValid() )
				RememberLastKnown( _target.WorldPosition );
			return;
		}

		if ( _state == EnemyAiState.Searching && _target.IsValid() )
			EnterState( EnemyAiState.Chasing, _target );
	}

	/// <summary>Host: War Cry — this player becomes the target now, unless the entity is already fighting.</summary>
	public void HostProvoke( GameObject player )
	{
		if ( !player.IsValid() || !IsValidPlayerTarget( player ) )
			return;

		RememberStimulus( player.WorldPosition, player );
		_target = player;
		RememberLastKnown( player.WorldPosition );

		if ( _state is EnemyAiState.Chasing or EnemyAiState.Attacking or EnemyAiState.Breaching )
			return;

		EnterState( EnemyAiState.Chasing, _target );
	}

	public void OnNavBakeComplete()
	{
		// A bake just landed: drop the settle back-off so an off-nav entity retries this frame.
		_navSettleFailures = 0;
		_nextNavSettleAt = 0d;
		// Every entity gets this on the same frame; twenty raiders all re-pathing at once (each a query
		// plus the solid sweep) was a spike right after every rebake. Spread the re-paths over the
		// next half second — a breacher waiting on this exact rebake still rechecks at once below.
		_needsImmediatePathCheck = false;
		_nextPathCheckAt = Time.NowDouble + Sandbox.Game.Random.Float( 0.05f, 0.55f );
		_validatedPathAt = -1d;
		// Breaching: the nav now reflects the piece we (or a collapse) took out — ask at once whether
		// the player is reachable, before committing to the next piece.
		if ( _state == EnemyAiState.Breaching )
			OnBreachNavRebaked();

		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() || !Scene.IsValid() )
			return;

		// Already walking on nav: leave the body where it is. Every rebake (each wall a raid knocks
		// down, ~1/s) used to snap EVERY entity in the scene to the nearest nav point — up to 120 u —
		// which read as scavs jumping to a new spot every second. An agent that really lost its footing
		// is re-placed lazily by the next path check (RunPathCheckTo: "agentReplaced").
		if ( _agentOnNav && _aiStarted && !_awaitingNavToStartAi && IsAgentDriving() )
			return;

		if ( !EntityNavMeshUtility.EnsureAgentOnNavMesh( Scene, Agent, GameObject.WorldPosition ) )
		{
			// Already driving before this bake: keep driving from where we stand; the agent clamps.
			if ( _agentOnNav )
				Agent.UpdatePosition = true;
			return;
		}

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
		// Same stagger as OnNavBakeComplete — one piece change must not re-path every entity this frame.
		if ( _state is EnemyAiState.Chasing or EnemyAiState.Searching or EnemyAiState.Retreating or EnemyAiState.Breaching
		     or EnemyAiState.Raiding )
			_nextPathCheckAt = Math.Min( _nextPathCheckAt, Time.NowDouble + Sandbox.Game.Random.Float( 0.05f, 0.55f ) );

		// A piece fell (ours or theirs) — ask right away whether the player is reachable now.
		if ( _state == EnemyAiState.Breaching )
			_nextBreachRecheckAt = 0d;
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
		{
			if ( _agentOnNav )
				Agent.UpdatePosition = true;
			return;
		}

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

		if ( IsRaider && RaiderIgnoresHitFrom( attacker.GameObject ) )
			return;

		// Leashed home: while still past the max travel distance hits do not pull them back out (no luring a camp away);
		// once back inside it a hit turns them round again.
		if ( _state == EnemyAiState.Returning && FlatDistanceFromLeash() > LeashMaxTravelDistance )
			return;

		var player = attacker.GameObject;
		_target = player;
		RememberLastKnown( player.WorldPosition );
		RememberStimulus( player.WorldPosition, player );
		_alertMeter = _perception.AlertThreshold;
		_alertLocked = true;
		// Taking hits while low HP interrupts flee so they can fight back briefly.
		_retreatBlockedUntil = Time.NowDouble + 6d;

		// Shot from behind / above the structure: keep tearing in; the next recheck (now) decides
		// whether they have actually become reachable.
		if ( _state == EnemyAiState.Breaching )
		{
			_nextBreachRecheckAt = 0d;
			return;
		}

		EnterState( EnemyAiState.Chasing, player );
	}

	// ── Stun (Medusa Eye, Sonic Burst): the brain stops entirely — no state ticks, no swings ──

	double _stunnedUntil;
	bool _petrifyTintApplied;
	readonly Dictionary<ModelRenderer, Color> _petrifyOriginalTints = new();

	/// <summary>Host clock: true while a stun holds. Feet are pinned separately by the caller (<see cref="EntityLocomotion.HostSetTrapped"/>).</summary>
	public bool IsStunned => Time.NowDouble < _stunnedUntil;

	/// <summary>Mirrored to every peer for the grey "petrified" tint.</summary>
	[Sync] public bool IsPetrified { get; private set; }

	/// <summary>Host: freeze the AI for <paramref name="seconds"/> — cancels any attack in flight, drops engagement, stops the agent.</summary>
	public void HostStun( float seconds )
	{
		if ( !CanRunHostLogic() || seconds <= 0f )
			return;

		_stunnedUntil = Math.Max( _stunnedUntil, Time.NowDouble + seconds );
		IsPetrified = true;

		EntityCombat ??= Components.Get<EntityCombat>();
		EntityCombat?.ResetCycle();
		EntityCombat?.SetEngaged( false );
		Agent ??= Components.Get<NavMeshAgent>();
		Agent?.Stop();
	}

	/// <summary>Every peer: swap the model tint while petrified, restore the authored tint after.</summary>
	void TickPetrifyVisual()
	{
		if ( IsPetrified == _petrifyTintApplied )
			return;

		_petrifyTintApplied = IsPetrified;
		if ( IsPetrified )
		{
			_petrifyOriginalTints.Clear();
			foreach ( var renderer in GameObject.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
			{
				if ( renderer is null || !renderer.IsValid() )
					continue;

				_petrifyOriginalTints[renderer] = renderer.Tint;
				renderer.Tint = new Color( 0.5f, 0.55f, 0.65f );
			}

			return;
		}

		foreach ( var (renderer, tint) in _petrifyOriginalTints )
		{
			if ( renderer is not null && renderer.IsValid() )
				renderer.Tint = tint;
		}

		_petrifyOriginalTints.Clear();
	}

	protected override void OnUpdate()
	{
		TickPetrifyVisual();

		if ( !CanRunHostLogic() )
			return;

		// No viewer within the shared entity range: gone to memory or asleep, nothing below runs (EntityBrain.Dormancy.cs).
		if ( TickDormancy() )
			return;

		if ( Vitals is not null && Vitals.IsDead )
			return;

		TickAwareness();

		EntityCombat ??= Components.Get<EntityCombat>();
		Locomotion ??= Components.Get<EntityLocomotion>();
		Agent ??= Components.Get<NavMeshAgent>();
		if ( EntityCombat is null )
			return;

		TickNavSettle();

		// Stunned: stand there. Nothing below runs — no alert fill, no chase, no attack tick.
		if ( IsStunned )
		{
			Agent?.Stop();
			return;
		}

		if ( IsPetrified )
			IsPetrified = false;

		if ( _state is EnemyAiState.Idle or EnemyAiState.Wander or EnemyAiState.Searching
		     or EnemyAiState.Chasing or EnemyAiState.Retreating or EnemyAiState.Breaching )
			TickAmbientAlert( Time.Delta );

		if ( CanStartRetreat() )
		{
			EnterState( EnemyAiState.Retreating );
			return;
		}

		if ( _state == EnemyAiState.Chasing && TryEnterAttackFromChase() )
			return;

		// Airborne, settling, or caught in a trap: feet stay put, but a target inside reach still gets swung at.
		if ( Locomotion is not null && (Locomotion.IsAirborne || Locomotion.IsSpawnSettling || Locomotion.IsTrapped) )
		{
			Agent?.Stop();
			if ( _target.IsValid()
			     && HasGeometricLos( _target )
			     && Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition ) <= AttackRange )
				EntityCombat.TickCombat( _target );
			return;
		}

		if ( TickLeash() )
			return;

		if ( TickRaidState() )
			return;

		if ( TickStragglerChase() )
			return;

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
			case EnemyAiState.Breaching:
				TickBreaching();
				break;
			case EnemyAiState.Returning:
				TickReturning();
				break;
			case EnemyAiState.Raiding:
				TickRaiding();
				break;
		}

		TickPerceptionDebug();
		TickMovementTrace();
	}

	/// <summary>
	/// Host → everyone: 0 unaware, 1 suspicious (noticing something, searching), 2 alerted (hunting / fighting). Drives the
	/// awareness mark on the overhead nameplate (<see cref="OverheadNameplateHud"/>); written only when it changes.
	/// </summary>
	[Sync( SyncFlags.FromHost )] public byte Awareness { get; private set; }

	void TickAwareness()
	{
		byte level = _state switch
		{
			EnemyAiState.Chasing or EnemyAiState.Attacking or EnemyAiState.Breaching or EnemyAiState.Raiding => 2,
			EnemyAiState.Searching => 1,
			_ => _noticing ? (byte)1 : (byte)0
		};
		if ( Awareness != level )
			Awareness = level;
	}

	void TickAmbientAlert( float dt )
	{
		if ( dt <= 0f || !Scene.IsValid() )
			return;

		// Alerted: meter stays locked, but walk/sprint noise still retargets investigate / last-known.
		if ( _state is EnemyAiState.Searching or EnemyAiState.Chasing or EnemyAiState.Breaching )
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

			// Close FOV + LOS fills ~1s at sightClose; slower toward max sight range. Right next to it a player outside the
			// cone is still sensed (slower) — a hard 150° cone made someone at 1 m behind it invisible.
			var inFov = EntitySight.IsInFov( origin, facing, go.WorldPosition, _perception.SightFovDegrees );
			var sensed = !inFov && dist <= TerrainWorldUnits.MetersToEngine( ProximitySenseMeters );
			if ( dist <= _perception.SightRange
			     && (inFov || sensed)
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
				if ( sensed )
					rate *= ProximitySenseFillFraction;
				AddAlertMeter( rate * dt, sensed ? "sense" : "sight" );
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
			{
				EnterState( EnemyAiState.Chasing, go );
				return;
			}

			// Chasing out of sight: footsteps tell it where they are now — the trail moves there and the hunt does not
			// time out while they stay audible (it still has to see them to hunt the live position).
			if ( _state == EnemyAiState.Chasing && go == _target )
			{
				_trailGoal = ProjectSearchPointToNav( go.WorldPosition );
				_trailGoalSet = true;
				RememberLastKnown( go.WorldPosition );
				var heardFloor = Time.NowDouble - _perception.ChaseCommitLosSeconds - 0.05;
				if ( _chaseLastSeenAt < heardFloor )
					_chaseLastSeenAt = heardFloor;
			}
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
		_noticing = false;

		// Chase only what it can actually see (or sense right beside it); a noise it cannot place by sight is searched.
		// The old fallback was the nearest player within 60 m, line of sight or not.
		GameObject player = null;
		if ( _target.IsValid() && IsValidPlayerTarget( _target ) && HasSightOn( _target ) )
			player = _target;
		else if ( TryFindVisiblePlayer( out var seen ) )
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

		var pos = GameObject.WorldPosition;
		// Ask for a bake only for the first few attempts: after that the area has been baked and there
		// is simply no nav here (outside the nav bounds, or unwalkable). Keep trying to seat the agent
		// at the backed-off rate, but stop costing everyone a rebake.
		if ( _navSettleFailures < NavSettleBakeRequestLimit )
			BuildNavMeshSync.EnsureNavAroundPoint( Scene, pos );
		if ( !EntityNavMeshUtility.EnsureAgentOnNavMesh( Scene, Agent, pos ) )
		{
			// Still no nav under the feet after asking for a bake: back off (0.45 s doubling to 10 s)
			// instead of asking every 0.45 s forever. One off-nav scav kept terrainTest in a bake loop —
			// a synchronous GenerateTiles plus a scene-wide collider pass about once a second (Mark's
			// log: steady "[BuildNav] rebake tiles" lines, 30–40 ms spikes, avg frame stepping
			// 7.7 → 11.1 ms). OnNavBakeComplete resets the back-off so a real bake is tried at once.
			_navSettleFailures++;
			_nextNavSettleAt = Time.NowDouble + Math.Min( 10d, 0.45d * Math.Pow( 2d, _navSettleFailures ) );
			return;
		}

		_navSettleFailures = 0;
		_nextNavSettleAt = Time.NowDouble + 0.45;
		_agentOnNav = true;
		Agent.UpdatePosition = true;
		if ( _awaitingNavToStartAi || !_aiStarted )
			BeginAiNow();
		else if ( _state is EnemyAiState.Idle or EnemyAiState.Wander )
			EnterState( EnemyAiState.Wander );
	}

	/// <summary>Agent is placed on nav and allowed to drive the transform.</summary>
	bool IsNavAgentReady() =>
		Agent is not null && Agent.IsValid() && Agent.Enabled && _agentOnNav && IsAgentDriving();

	/// <summary>
	/// The body is (or is about to be) following the agent: locomotion turns every UpdatePosition = true
	/// into an attach on its next fixed update, so a freshly set flag counts too.
	/// </summary>
	bool IsAgentDriving() =>
		(Agent is not null && Agent.IsValid() && Agent.UpdatePosition) || (Locomotion is { AgentDrivesBody: true });

	void TickIdle()
	{
		Locomotion?.SetLookTarget( null );
		Locomotion?.ClearTravelHint();
		StopAndIdle();

		if ( TickNotice() )
			return;

		// Glance somewhere while standing (picked on entering Idle) instead of staring down the last wander heading.
		if ( _idleLookDir.LengthSquared > 0.5f )
			Locomotion?.SmoothFaceTowardWorld( GameObject.WorldPosition + _idleLookDir * 64f, AlertTurnDegreesPerSecond * 0.4f );

		if ( Time.NowDouble >= _stateEndsAt )
			EnterState( EnemyAiState.Wander );
	}

	/// <summary>
	/// Idle / wander with the alert meter part-filled: stop and turn to look at what it noticed. The sight fill keeps
	/// running while it looks, so a player who stays in view gets chased, one who ducks out lets it decay and walk on.
	/// </summary>
	bool TickNotice()
	{
		var noticing = !_alertLocked && _hasStimulus && AlertMeter01 >= NoticeAlertFraction;
		if ( !noticing )
		{
			if ( _noticing )
			{
				// Lost interest: carry on with the wander leg it was on.
				_noticing = false;
				_needsImmediatePathCheck = true;
				if ( _state == EnemyAiState.Wander && _wanderGoal != default && IsNavAgentReady() )
					Agent.MoveTo( _wanderGoal );
			}

			return false;
		}

		_noticing = true;
		Agent?.Stop();
		Locomotion?.SetLookTarget( null );
		Locomotion?.SmoothFaceTowardWorld( _stimulusPos, AlertTurnDegreesPerSecond );
		return true;
	}

	void TickWander()
	{
		Locomotion?.SetLookTarget( null );
		if ( TickNotice() )
			return;
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
		var flat = (_wanderGoal - GameObject.WorldPosition).WithZ( 0f ).Length;
		if ( flat <= WanderReachDistance )
		{
			EnterState( EnemyAiState.Idle );
			return;
		}

		ManualStepToward( _wanderGoal, Math.Max( 48f, WanderMoveSpeed ) );
	}

	/// <summary>No nav agent drive yet — walk toward a goal on the heightfield so entities are not statues.</summary>
	void ManualStepToward( Vector3 goal, float speed )
	{
		// Locomotion is carrying the body through a rebake — do not also step it.
		if ( Locomotion is { IsCoasting: true } )
			return;

		var pos = GameObject.WorldPosition;
		var to = (goal - pos).WithZ( 0f );
		var flat = to.Length;
		if ( flat < 1e-3f )
			return;

		var dir = to / flat;
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
		// Found them: chase only on sight. "Any player within 60 m" used to end every search at once, so the investigate
		// walk almost never played.
		if ( TryFindVisiblePlayer( out var seen ) )
		{
			EnterState( EnemyAiState.Chasing, seen );
			return;
		}

		if ( !_hasSearchGoal )
			FreezeSearchGoalFromStimulus();

		ApplyAgentSpeed( run: false );

		var now = Time.NowDouble;
		if ( now >= _searchEndsAt )
		{
			DropChaseToIdle();
			return;
		}

		// At the spot: stop, look around for a moment, then try another spot near where the trail went cold.
		var anchor = Locomotion?.GetNavAnchorWorld() ?? GameObject.WorldPosition;
		if ( Vector3.DistanceBetween( anchor.WithZ( 0f ), _searchGoal.WithZ( 0f ) ) <= WanderReachDistance )
		{
			if ( _searchPauseUntil <= 0d )
			{
				_searchPauseUntil = now + Sandbox.Game.Random.Float( 1f, 2.5f );
				_searchLookDir = Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) ).Forward;
				Agent?.Stop();
			}

			if ( now < _searchPauseUntil )
			{
				Locomotion?.SetLookTarget( null );
				Locomotion?.SmoothFaceTowardWorld( anchor + _searchLookDir * 64f, AlertTurnDegreesPerSecond * 0.5f );
				return;
			}

			_searchPauseUntil = 0d;
			_searchGoal = PickSearchSpotNear( _searchNoisePos );
			_needsImmediatePathCheck = true;
		}

		FaceSearchNoiseDirection();
		if ( ShouldRunPathCheck() )
			TryIssueNavMove( _searchGoal, 0f );
	}

	Vector3 PickSearchSpotNear( Vector3 center )
	{
		var dir = Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) ).Forward;
		var meters = Sandbox.Game.Random.Float( SearchSpotMinMeters, SearchSpotMaxMeters );
		return ProjectSearchPointToNav( center + dir * TerrainWorldUnits.MetersToEngine( meters ) );
	}

	void TickChasing()
	{
		if ( !_target.IsValid() || !IsValidPlayerTarget( _target ) )
		{
			// Target gone (died, left): whoever it can see, else search where the hunt was.
			if ( TryFindVisiblePlayer( out var seen ) )
				_target = seen;
			else
			{
				LoseTrailToSearch();
				return;
			}
		}

		_alertMeter = _perception.AlertThreshold;
		_alertLocked = true;

		// Mesh rebuilding after a placed / destroyed piece: track on foot for a second, then wait.
		if ( TickManualChaseStep() )
			return;
		ApplyAgentSpeed( run: true );

		var now = Time.NowDouble;
		var hasLos = HasChaseSight( _target );
		if ( hasLos )
			NoteSighting( _target );

		var unseen = now - _chaseLastSeenAt;
		if ( unseen >= _perception.ChaseLostLosSeconds )
		{
			LoseTrailToSearch();
			return;
		}

		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );
		// A player a floor above is "in range" by distance but out of reach — never attack the air under them.
		if ( dist <= AttackRange && hasLos && IsWithinMeleeVertical( _target ) )
		{
			EnterState( EnemyAiState.Attacking, _target );
			return;
		}

		// Sees them, or lost them a moment ago (saw which way they went): hunt the live position. Past that it goes where it
		// last saw them, led by how they were moving — no more pathing to an unseen player's live feet for 30 s.
		var tracking = unseen <= _perception.ChaseCommitLosSeconds;

		Locomotion?.SetPreferAimOverVelocity( false );
		Locomotion?.SetLookTarget( tracking ? _target : null );
		if ( Locomotion is not null )
			Locomotion.TurnDegreesPerSecond = AlertTurnDegreesPerSecond;

		if ( now < _nextChaseThinkAt )
			return;

		_nextChaseThinkAt = now + ChaseThinkInterval;

		if ( tracking )
		{
			// Plain nav only (per Mark): the mesh is correct, so a door or stairs is found by pathing.
			// A piece directly ahead is remembered as the preferred breach target, nothing more.
			RememberPieceInWay();

			if ( ShouldRunPathCheck( dist ) )
			{
				TryIssueNavMove(
					GetMeleeChasePoint( _target, dist, StrikeRingFraction ),
					0f,
					ClosePathCheckInterval,
					LiveChaseRepathMoveThreshold );
			}

			// No complete route to where the player stands → tear a way through, now (per Mark).
			TickChaseBlockedDetection( dist );
			return;
		}

		EntityAttackSlots.Release( this );
		if ( !_trailGoalSet )
		{
			_trailGoal = BuildTrailGoal();
			_trailGoalSet = true;
			_needsImmediatePathCheck = true;
		}

		// Got to where they went and they are not here: search around it.
		var anchor = Locomotion?.GetNavAnchorWorld() ?? GameObject.WorldPosition;
		if ( Vector3.DistanceBetween( anchor.WithZ( 0f ), _trailGoal.WithZ( 0f ) ) <= WanderReachDistance )
		{
			LoseTrailToSearch();
			return;
		}

		if ( ShouldRunPathCheck( dist ) )
			TryIssueNavMove( _trailGoal, 0f, ClosePathCheckInterval, LiveChaseRepathMoveThreshold );
	}

	/// <summary>Seen this frame: refresh the clock, the last-known spot and a coarse velocity for the trail lead.</summary>
	void NoteSighting( GameObject target )
	{
		var now = Time.NowDouble;
		_chaseLastSeenAt = now;
		_trailGoalSet = false;
		var pos = target.WorldPosition;
		if ( _velocitySampleAt <= 0d || now - _velocitySampleAt > 1.0 )
		{
			_velocitySamplePos = pos;
			_velocitySampleAt = now;
			_lastSeenVelocity = Vector3.Zero;
		}
		else if ( now - _velocitySampleAt >= 0.25 )
		{
			_lastSeenVelocity = ((pos - _velocitySamplePos) / (float)(now - _velocitySampleAt)).WithZ( 0f );
			_velocitySamplePos = pos;
			_velocitySampleAt = now;
		}

		_lastKnownPlayerPos = pos;
		_hasLastKnown = true;
	}

	/// <summary>Where the player was last seen, plus where they were heading, on the nav.</summary>
	Vector3 BuildTrailGoal()
	{
		var lead = _lastSeenVelocity * TrailLeadSeconds;
		var maxLead = TerrainWorldUnits.MetersToEngine( TrailLeadMaxMeters );
		if ( lead.Length > maxLead )
			lead = lead.Normal * maxLead;
		return ProjectSearchPointToNav( _lastKnownPlayerPos + lead );
	}

	/// <summary>The trail went cold: search around the last place the player was seen (or heard).</summary>
	void LoseTrailToSearch()
	{
		var around = _trailGoalSet ? _trailGoal : _lastKnownPlayerPos;
		if ( !_hasLastKnown && !_trailGoalSet )
		{
			DropChaseToIdle();
			return;
		}

		_stimulusPos = around;
		_hasStimulus = true;
		_hasSearchGoal = false;
		_trailGoalSet = false;
		ClearBreachState();
		EnterState( EnemyAiState.Searching );
	}

	/// <summary>
	/// Close in (<see cref="SlotEngageMeters"/>) the goal is this attacker's bearing on a ring of
	/// <paramref name="ringFraction"/> × blade reach around the player, so a group fans out round them instead of stacking
	/// on their feet; further out (or when the player is off nav) it is the live chase point.
	/// </summary>
	Vector3 GetMeleeChasePoint( GameObject target, float dist, float ringFraction )
	{
		var live = GetLiveChasePoint( target );
		if ( dist > TerrainWorldUnits.MetersToEngine( SlotReleaseMeters ) )
			EntityAttackSlots.Release( this );
		if ( !_chaseGoalReachesTarget || dist > TerrainWorldUnits.MetersToEngine( SlotEngageMeters ) )
			return live;

		var from = (GameObject.WorldPosition - target.WorldPosition).WithZ( 0f );
		var side = from.LengthSquared > 1f ? MathF.Atan2( from.y, from.x ) * 180f / MathF.PI : 0f;
		var bearing = EntityAttackSlots.ClaimBearing( target, this, side );
		if ( MathF.Abs( Angles.NormalizeAngle( bearing - side ) ) > SlotReclaimDegrees )
		{
			EntityAttackSlots.Release( this );
			bearing = EntityAttackSlots.ClaimBearing( target, this, side );
		}

		var rad = bearing * MathF.PI / 180f;
		var slot = live + new Vector3( MathF.Cos( rad ), MathF.Sin( rad ), 0f ) * (SwingReachUnits() * ringFraction);
		if ( Scene.IsValid()
		     && EntityNavMeshUtility.TryProjectToNavMesh( Scene, slot, out var onNav, NavProjectTier.Fast )
		     && MathF.Abs( onNav.z - live.z ) <= 48f )
			return onNav;

		return live;
	}

	/// <summary>Blade reach of this entity's weapon class (engine units), read once from its <see cref="PlayerCombat"/>.</summary>
	float SwingReachUnits()
	{
		if ( _swingReachUnits > 1f )
			return _swingReachUnits;

		var pc = EntityCombat?.Combat ?? Components.Get<PlayerCombat>();
		if ( pc is null || !pc.IsValid() )
			return 76f;

		_swingReachUnits = pc.GetMeleeAttackRangeUnits( MeleeAttackTypes.Right );
		return _swingReachUnits;
	}

	/// <summary>Locomotion clipped a wall — request a fresh live repath; remember a build piece so breaching can prefer it.</summary>
	public void NotifyChasePhysicsBlocked( GameObject hit )
	{
		if ( _state is not (EnemyAiState.Chasing or EnemyAiState.Breaching) )
			return;

		var piece = BuildPlacementUtility.FindBuildPieceOnHierarchy( hit );
		if ( piece is not null && piece.IsValid() )
		{
			_lastClipPiece = piece;
			_lastClipPieceAt = Time.NowDouble;
		}

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
		ClearBreachState();
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
		_searchEndsAt = _searchStartedAt + Sandbox.Game.Random.Float( SearchSecondsMin, SearchSecondsMax );
		_searchPauseUntil = 0d;
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
		if ( dist > AttackRange || !IsWithinMeleeVertical( _target ) )
			return false;

		EnterState( EnemyAiState.Attacking, _target );
		return true;
	}

	void TickAttacking()
	{
		if ( !_target.IsValid() || !IsValidPlayerTarget( _target ) )
		{
			// Never abort mid-swing — finish it first.
			if ( EntityCombat is { IsMovementLocked: true } )
			{
				Agent?.Stop();
				return;
			}

			if ( TryFindVisiblePlayer( out var seen ) )
			{
				EnterState( EnemyAiState.Chasing, seen );
				return;
			}

			LoseTrailToSearch();
			return;
		}

		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );
		var hasLos = HasChaseSight( _target );
		if ( hasLos )
			NoteSighting( _target );

		// Released swing: feet planted, yaw locked by the combat cycle.
		if ( EntityCombat.IsSwingInFlight )
		{
			Agent?.Stop();
			EntityCombat.TickCombat( _target );
			return;
		}

		// Telegraph: keep stepping in at a walk so the swing lands where the player is now, not where they stood.
		if ( EntityCombat.IsTelegraphing )
		{
			StepInToRing( dist, StrikeRingFraction, run: false );
			EntityCombat.TickCombat( _target );
			return;
		}

		if ( Time.NowDouble - _chaseLastSeenAt >= _perception.ChaseLostLosSeconds )
		{
			LoseTrailToSearch();
			return;
		}

		if ( dist > AttackRange * LeaveAttackRangeFactor || !hasLos || !IsWithinMeleeVertical( _target ) )
		{
			EnterState( EnemyAiState.Chasing, _target );
			return;
		}

		// Two attack tokens per player: a holder closes in and swings, the rest hold their ring slot just out of reach and
		// face the player until one frees up — no more three scavs on one spot swinging on their own clocks.
		var ready = EntityCombat.IsReadyToSwing;
		var hasToken = ready && EntityAttackSlots.TryTakeToken( _target, this );
		EntityCombat.AttackPermitted = hasToken;
		StepInToRing( dist, hasToken ? StrikeRingFraction : WaitRingFraction, run: true );
		EntityCombat.TickCombat( _target );
	}

	/// <summary>Attacking: walk to this attacker's ring slot (<paramref name="ringFraction"/> × reach) and plant there.</summary>
	void StepInToRing( float dist, float ringFraction, bool run )
	{
		ApplyAgentSpeed( run );
		var ring = SwingReachUnits() * ringFraction;
		if ( dist <= ring + TargetBodyRadiusUnits )
		{
			if ( Agent is { IsNavigating: true } )
				Agent.Stop();
			return;
		}

		if ( Time.NowDouble < _nextStepInAt || !IsNavAgentReady() )
			return;

		_nextStepInAt = Time.NowDouble + StepInRepathSeconds;
		Agent.MoveTo( GetMeleeChasePoint( _target, dist, ringFraction ) );
	}

	void TickRetreating()
	{
		// Committed to the flee: it turns and fights only when a player it can see is right on it (cornered), and then
		// for a while. "Any player within 30 m → chase" flipped it back every frame — the next frame's low-HP check sent
		// it fleeing again, and every flip reset the combat cycle, so it neither fled nor fought.
		if ( TryFindVisiblePlayer( out var seen )
		     && Vector3.DistanceBetween( GameObject.WorldPosition, seen.WorldPosition ) <= TerrainWorldUnits.MetersToEngine( RetreatCorneredMeters ) )
		{
			_retreatBlockedUntil = Time.NowDouble + 6d;
			EnterState( EnemyAiState.Chasing, seen );
			return;
		}

		var anchor = Locomotion?.GetNavAnchorWorld() ?? GameObject.WorldPosition;
		var fled = Vector3.DistanceBetween( _retreatStart.WithZ( 0f ), anchor.WithZ( 0f ) );
		if ( fled >= _perception.RetreatDistance * 0.92f
		     || Time.NowDouble - _stateEnteredAt >= RetreatMaxSeconds
		     || (_retreatGoal != default
		         && Vector3.DistanceBetween( anchor.WithZ( 0f ), _retreatGoal.WithZ( 0f ) ) <= WanderReachDistance) )
		{
			// Got away: calm down (the meter can fill again — it stayed locked before, so it never re-noticed anyone)
			// and fight rather than flee next time for a while.
			_alertMeter = 0f;
			_alertLocked = false;
			_target = null;
			_retreatBlockedUntil = Time.NowDouble + RetreatCooldownSeconds;
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

	/// <summary>
	/// Camp leash: an alerted entity pulled past the max travel distance from the leash center
	/// drops its target and runs back. One flat distance check per frame while alerted — nothing else.
	/// </summary>
	bool TickLeash()
	{
		if ( !HasLeash || LeashMaxTravelDistance <= 0f )
			return false;

		if ( _state is not (EnemyAiState.Chasing or EnemyAiState.Attacking or EnemyAiState.Searching or EnemyAiState.Breaching) )
			return false;

		if ( FlatDistanceFromLeash() <= LeashMaxTravelDistance )
			return false;

		BeginReturnHome();
		return true;
	}

	float FlatDistanceFromLeash() =>
		Vector3.DistanceBetween( GameObject.WorldPosition.WithZ( 0f ), LeashCenter.WithZ( 0f ) );

	void BeginReturnHome()
	{
		_alertMeter = 0f;
		_alertLocked = false;
		_hasSearchGoal = false;
		_hasLastKnown = false;
		_hasStimulus = false;
		_chaseLastSeenAt = 0d;
		_target = null;
		ClearBreachState();
		EnterState( EnemyAiState.Returning );
	}

	void TickReturning()
	{
		if ( !HasLeash )
		{
			EnterState( EnemyAiState.Idle );
			return;
		}

		// Back within wander distance of the camp — resume normal life; alert can build again from here.
		if ( FlatDistanceFromLeash() <= LeashWanderDistance * 0.85f )
		{
			_alertMeter = 0f;
			EnterState( EnemyAiState.Idle );
			return;
		}

		ApplyAgentSpeed( run: true );
		Locomotion?.SetLookTarget( null );
		Locomotion?.SetTravelHint( _returnGoal );

		Agent ??= Components.Get<NavMeshAgent>();
		if ( !IsNavAgentReady() )
		{
			ManualStepToward( _returnGoal, Math.Max( 160f, ChaseMoveSpeed ) );
			return;
		}

		if ( !ShouldRunPathCheck() )
			return;

		var issued = TryIssueNavMove( _returnGoal, 0f );
		// Streamed terrain often fails QueryPath even when tiles exist — still ask the agent to go.
		if ( issued is null || !issued.Value.HasPath )
			Agent.MoveTo( _returnGoal );
	}

	Vector3 BuildReturnGoal()
	{
		if ( EntityNavMeshUtility.TryProjectToNavMesh( Scene, LeashCenter, out var onNav, NavProjectTier.Full ) )
			return onNav;

		return LeashCenter.WithZ( GameObject.WorldPosition.z );
	}

	bool CanStartRetreat()
	{
		if ( _state == EnemyAiState.Retreating )
			return false;

		// Raiders fight to the death.
		if ( IsRaider )
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
		// Manual tracking (stale mesh) took the body from the agent; any state change hands it back.
		EndManualChase();

		// A piece claim only means something while breaching.
		if ( next != EnemyAiState.Breaching )
			BreachClaims.Release( this );

		// Melee ring: a token only while attacking, a bearing only while chasing / attacking.
		if ( next != EnemyAiState.Attacking )
		{
			EntityAttackSlots.ReturnToken( this );
			if ( EntityCombat is not null )
				EntityCombat.AttackPermitted = true;
		}
		if ( next is not (EnemyAiState.Chasing or EnemyAiState.Attacking) )
			EntityAttackSlots.Release( this );
		Locomotion?.SetCombatStrafe( next == EnemyAiState.Attacking );

		var prev = _state;
		_noticing = false;
		_state = next;
		_stateEnteredAt = Time.NowDouble;
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
				// One idle in four is a longer pause; half of them glance somewhere new.
				if ( !forceIdleSeconds.HasValue && Sandbox.Game.Random.Float( 0f, 1f ) < 0.25f )
					_stateEndsAt += (_stateEndsAt - Time.NowDouble) * 1.5;
				_idleLookDir = Sandbox.Game.Random.Float( 0f, 1f ) < 0.5f
					? Rotation.FromYaw( GameObject.WorldRotation.Angles().yaw + Sandbox.Game.Random.Float( -110f, 110f ) ).Forward
					: Vector3.Zero;
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
				ResetChaseProgress();
				// A fresh hunt (from idle, a search, a retreat) starts its unseen clock now; Attack↔Chase keeps it.
				if ( prev is not (EnemyAiState.Attacking or EnemyAiState.Chasing or EnemyAiState.Breaching) || _chaseLastSeenAt <= 0d )
				{
					_chaseLastSeenAt = Time.NowDouble;
					_trailGoalSet = false;
				}
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
			case EnemyAiState.Breaching:
				// Piece + stand were chosen by BeginBreachPiece before entering.
				EntityCombat?.SetEngaged( false );
				if ( EntityCombat is not { IsMovementLocked: true } )
					EntityCombat?.ResetCycle();
				ApplyAgentSpeed( run: true );
				_alertMeter = _perception.AlertThreshold;
				_alertLocked = true;
				_nextBreachRecheckAt = Time.NowDouble + BreachRecheckSeconds;
				Locomotion?.SetLookTarget( null );
				Locomotion?.SetPreferAimOverVelocity( false );
				break;
			case EnemyAiState.Returning:
				EntityCombat?.SetEngaged( false );
				EntityCombat?.ResetCycle();
				ApplyAgentSpeed( run: true );
				_returnGoal = BuildReturnGoal();
				Locomotion?.SetLookTarget( null );
				Locomotion?.SetPreferAimOverVelocity( false );
				_needsImmediatePathCheck = true;
				TryIssueNavMove( _returnGoal, 0f );
				Agent ??= Components.Get<NavMeshAgent>();
				if ( IsNavAgentReady() )
					Agent.MoveTo( _returnGoal );
				break;
			case EnemyAiState.Raiding:
				EnterRaidingState();
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

		// Legs of varied length (35–100 % of the wander distance) on a wide arc; without a leash, once it has drifted more
		// than two wander distances from home the leg bends back toward home (it used to walk 15 m legs at ±70° forever).
		var radius = Math.Max( 160f, _perception.WanderDistance * Sandbox.Game.Random.Float( 0.35f, 1f ) );
		// Raid stragglers came off the same chase facing the same way — a narrow leg sent the whole
		// group off together as a pack. They pick any direction.
		var yaw = _isRaidStraggler ? Sandbox.Game.Random.Float( 0f, 360f ) : Sandbox.Game.Random.Float( -100f, 100f );
		var dir = Rotation.FromYaw( yaw ) * facing;
		var toHome = (HomePosition - origin).WithZ( 0f );
		if ( !HasLeash && !_isRaidStraggler && HomePosition != default && toHome.Length > _perception.WanderDistance * 2f )
			dir = Rotation.FromYaw( Sandbox.Game.Random.Float( -35f, 35f ) ) * toHome.Normal;
		var ideal = origin + dir * radius;
		if ( HasLeash )
			ideal = ClampWanderGoalToLeash( origin, ideal, radius );

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

	/// <summary>Keep wander legs within wander distance of the leash center; from further out, the leg heads back toward the camp.</summary>
	Vector3 ClampWanderGoalToLeash( Vector3 origin, Vector3 ideal, float legLength )
	{
		var fromCenter = (origin - LeashCenter).WithZ( 0f );
		if ( fromCenter.Length > LeashWanderDistance )
		{
			// Too far out (after a retreat or a lost chase): walk one leg toward the camp.
			var back = (LeashCenter - origin).WithZ( 0f ).Normal;
			return origin + back * Math.Min( legLength, fromCenter.Length );
		}

		var offset = (ideal - LeashCenter).WithZ( 0f );
		var limit = LeashWanderDistance * 0.9f;
		if ( offset.Length <= limit )
			return ideal;

		return (LeashCenter + offset.Normal * limit).WithZ( ideal.z );
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

	/// <summary>Spacing of the re-grounded samples along a path segment when checking for solids.</summary>
	const float PathProbeStepUnits = 96f;
	/// <summary>How much of the route (from the start) the solid sweep covers — the rest is swept as the entity advances.</summary>
	const float PathProbeMaxUnits = 640f;
	/// <summary>A path with the same endpoints and length as the last validated one reuses its verdict for this long.</summary>
	const double PathValidationReuseSeconds = 1.5;
	Vector3 _validatedPathStart;
	Vector3 _validatedPathEnd;
	float _validatedPathLength;
	double _validatedPathAt = -1d;
	bool _validatedPathCrosses;

	/// <summary>
	/// Does the nav path physically pass through something solid (a thin wall the mesh leaked
	/// through)? Each segment is re-grounded every <see cref="PathProbeStepUnits"/> with a down
	/// probe before the horizontal sweep. A string-pulled path has no point where a stair meets a
	/// floor, so the raw segment from the stair foot to the floor sliced through the slab from
	/// underneath and read as a wall — every route UP a structure was "blocked" and the entity
	/// breached instead of climbing (per Mark: chase them up their own creation), and the route
	/// through a hole it had just opened was vetoed the same way.
	/// </summary>
	bool PathCrossesSolid( EntityChaseRouting.NavPathQuery path )
	{
		if ( path.Points is null || path.Points.Count < 2 || !Scene.IsValid() )
			return false;

		// Only the near part of the route is swept: a leak far along a 3000 u path is caught when the
		// entity gets there, and sweeping the whole thing cost ~60 traces per query per entity.
		var sweptUnits = 0f;
		var prev = GroundPathPoint( path.Points[0] );
		for ( var i = 0; i < path.Points.Count - 1 && sweptUnits < PathProbeMaxUnits; i++ )
		{
			var a = path.Points[i];
			var b = path.Points[i + 1];
			sweptUnits += (b - a).WithZ( 0f ).Length;
			var flat = (b - a).WithZ( 0f ).Length;
			var steps = Math.Max( 1, (int)MathF.Ceiling( flat / PathProbeStepUnits ) );
			for ( var step = 1; step <= steps; step++ )
			{
				var sample = GroundPathPoint( Vector3.Lerp( a, b, step / (float)steps ) );
				if ( SegmentHitsWall( prev, sample ) )
					return true;

				prev = sample;
			}
		}

		return false;
	}

	/// <summary>
	/// The sweep costs ~2 traces per 96 u of path; with a dozen entities re-pathing every 0.3 s that
	/// was the second half of the "renav lag". A path whose endpoints and length match the last
	/// validated one (the player has not moved, the mesh has not changed) reuses the verdict.
	/// </summary>
	bool PathCrossesSolidCached( EntityChaseRouting.NavPathQuery path )
	{
		var start = path.Points[0];
		var end = path.Points[^1];
		var now = Time.NowDouble;
		var same = _validatedPathAt >= 0d
		           && now - _validatedPathAt < PathValidationReuseSeconds
		           && Vector3.DistanceBetween( start, _validatedPathStart ) < 48f
		           && Vector3.DistanceBetween( end, _validatedPathEnd ) < 48f
		           && MathF.Abs( path.Length - _validatedPathLength ) < 64f;
		if ( same )
			return _validatedPathCrosses;

		_validatedPathCrosses = PathCrossesSolid( path );
		_validatedPathStart = start;
		_validatedPathEnd = end;
		_validatedPathLength = path.Length;
		_validatedPathAt = now;
		return _validatedPathCrosses;
	}

	/// <summary>The walkable surface under a point along the path — a stair tread, a floor top, the ground.</summary>
	Vector3 GroundPathPoint( Vector3 point )
	{
		var tr = Scene.Trace.Ray( point + Vector3.Up * 64f, point - Vector3.Up * 160f )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "player", "enemy" )
			.Run();

		return tr.Hit ? tr.HitPosition : point;
	}

	/// <summary>Body-height sweep between two grounded samples: a vertical-ish face that is not stairs / roof is a wall.</summary>
	bool SegmentHitsWall( Vector3 from, Vector3 to )
	{
		var tr = Scene.Trace.Ray( from + Vector3.Up * 36f, to + Vector3.Up * 36f )
			.Radius( 8f )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "player", "enemy" )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return false;

		// Stairs / roofs are the route, not an obstacle — a riser face would fail the normal test.
		if ( BuildPieceNavPolicy.IsWalkablePathObject( tr.GameObject ) )
			return false;

		// Floor hits are fine; vertical-ish walls are not.
		return tr.Normal.z < 0.55f;
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

	/// <summary>
	/// Hunting sight: no cone (it is looking for them) but a range — <see cref="ChaseSightRangeFactor"/> × the perception
	/// sight range. Plain line of sight at any distance kept a scav "seeing" a player 120 m across open grass.
	/// </summary>
	bool HasChaseSight( GameObject target ) =>
		target.IsValid()
		&& (target.WorldPosition - GameObject.WorldPosition).Length <= _perception.SightRange * ChaseSightRangeFactor
		&& HasGeometricLos( target );

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

		if ( !EntitySight.IsInFov( origin, facing, target.WorldPosition, _perception.SightFovDegrees )
		     && (target.WorldPosition - origin).Length > TerrainWorldUnits.MetersToEngine( ProximitySenseMeters ) )
			return false;

		return EntitySight.HasClearLos( Scene, eye, GameObject, target, _perception.EyeHeight, out _lastLosDetail );
	}

	bool ShouldRunPathCheck( float distToTarget = float.MaxValue )
	{
		// Attack telegraphs lock feet; chase/break abort must still be able to repath.
		if ( _state is (EnemyAiState.Attacking or EnemyAiState.Breaching) && EntityCombat is { IsMovementLocked: true } )
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

		// Scene-wide budget: raiders spawned in one frame keep their check timers in lock-step, so
		// twenty path queries (each with a solid sweep) landed in the same frame about once a second —
		// the "everyone stutters on the second". Over budget → try again next frame; the interval is
		// jittered so the timers drift apart for good.
		if ( !TryTakePathQuerySlot() )
		{
			LastNavBlockReason = "budget";
			_nextPathCheckAt = Time.NowDouble + 0.03f;
			return null;
		}

		_needsImmediatePathCheck = false;
		_nextPathCheckAt = Time.NowDouble + Math.Max( 0.12f, pathInterval ) * Sandbox.Game.Random.Float( 0.85f, 1.35f );

		origin = GetNavOrigin();
		navGoal = standOff > 1f
			? EntityChaseRouting.OffsetChaseGoal( goal, origin, standOff )
			: goal;

		var pathQuery = EntityChaseRouting.QueryPath( Scene, origin, navGoal, Agent, NavProjectTier.Fast, startOnNavHint );

		LastNavGoal = navGoal;
		LastPathStatus = pathQuery.Status;
		// Physics is the judge of a "complete" path: a leak through a wall must never read as a route.
		LastPathCrossesSolid = pathQuery.HasPath && PathCrossesSolidCached( pathQuery );

		_lastPathPoints.Clear();
		if ( pathQuery.Points is not null )
			_lastPathPoints.AddRange( pathQuery.Points );

		if ( !pathQuery.HasPath )
		{
			LastNavBlockReason = $"noPath({pathQuery.Status})";
			return pathQuery;
		}

		// IsNavigating reads false for the frames after a MoveTo while the path is computed; treating
		// that as "not navigating" re-issued MoveTo on every think, the agent never settled, and the
		// body — turned toward the look target in the gaps — crept along at the forward-only gate's
		// 17 % (trace: real=37 wish=220 max=220, nav flipping).
		var recentlyIssued = Time.NowDouble - _lastMoveIssuedAt < MoveIssueSettleSeconds;
		if ( (Agent.IsNavigating || recentlyIssued) && (_issuedNavGoal - navGoal).Length < 96f )
		{
			LastNavBlockReason = "alreadyNavigating";
			return pathQuery;
		}

		// The last MoveTo had its settle time and the agent still is not navigating: it was issued
		// from an agent position that is not on the mesh (a clip / landing nudged it off), and a
		// MoveTo from off-mesh silently does nothing — the trace showed 20 s of "moveIssued" with
		// real=0 on a floor tile and on the stairs. Put the agent back on nav beside the body first.
		if ( _lastMoveIssuedAt > 0d && !recentlyIssued && !Agent.IsNavigating )
		{
			if ( EntityNavMeshUtility.EnsureAgentOnNavMesh( Scene, Agent, GameObject.WorldPosition ) )
				LastNavBlockReason = "agentReplaced";
		}

		// An idle agent may have been re-seated by a tile rebuild while the body stayed put (locomotion
		// only follows a walking agent): start the path from where the body actually is.
		if ( !Agent.IsNavigating && Vector3.DistanceBetween( Agent.AgentPosition.WithZ( 0f ), GameObject.WorldPosition.WithZ( 0f ) ) > 8f )
			Agent.SetAgentPosition( GameObject.WorldPosition );

		// Retarget in place — avoid SyncAgentFromRoot every issue (yanks root sideways).
		Agent.MoveTo( navGoal );
		_issuedNavGoal = navGoal;
		_lastMoveIssuedAt = Time.NowDouble;
		Locomotion?.SetTravelHint( navGoal );
		LastNavBlockReason = "moveIssued";

		return pathQuery;
	}


	/// <summary>At most this many full path checks (query + solid sweep) per frame across every entity.</summary>
	const int MaxPathQueriesPerFrame = 4;
	static float _pathQueryFrameTime = -1f;
	static int _pathQueriesThisFrame;

	static bool TryTakePathQuerySlot()
	{
		if ( Time.Now != _pathQueryFrameTime )
		{
			_pathQueryFrameTime = Time.Now;
			_pathQueriesThisFrame = 0;
		}

		if ( _pathQueriesThisFrame >= MaxPathQueriesPerFrame )
			return false;

		_pathQueriesThisFrame++;
		return true;
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

		// The projection is only the path query's start hint — never a reason to move the body. The
		// old glue (WorldPosition = projection, up to 96 u) ran on every immediate path check, and every
		// nav rebake requests one, so each wall a raid knocked down hopped every entity sideways.
		if ( EntityNavMeshUtility.TryProjectToNavMesh( Scene, origin, out onNav, NavProjectTier.Full ) )
		{
			// Only trust a local projection — random far samples were launching entities.
			if ( (onNav - origin).Length > 96f )
				onNav = origin;

			return true;
		}

		if ( EntityNavMeshUtility.TryProjectToNavMesh( Scene, GameObject.WorldPosition, out onNav, NavProjectTier.Full )
		     && (onNav - GameObject.WorldPosition).Length <= 96f )
			return true;

		return false;
	}

	/// <summary>Last chase goal that was genuinely under the player's feet, for the off-nav grace.</summary>
	Vector3 _lastOnNavChasePoint;
	double _lastOnNavChaseAt = -1000d;
	/// <summary>Feet test misses this long (jump arcs, poly seams, roof edge trim) before the goal may degrade.</summary>
	const float OffNavGraceSeconds = 2.5f;
	/// <summary>Grace only holds while the remembered spot is still near the player (not a stale teleport).</summary>
	const float OffNavGraceMaxDrift = 320f;

	Vector3 GetLiveChasePoint( GameObject target )
	{
		if ( target is null || !target.IsValid() || !Scene.IsValid() )
			return Vector3.Zero;

		var playerAnchor = EntityLocomotion.GetNavAnchorWorld( target );
		var selfZ = GameObject.WorldPosition.z;

		// Standing on nav — terrain or a build floor at any height — chase them THERE. Clamping to
		// our own Z is what parked entities on the ground under a player on an upper floor; when
		// no walkable way up exists the path comes back partial and breaching takes over.
		// Tight box sample, not a radius projection: a random sample within 64 u of a roof edge
		// lands on the ground below as often as on the roof, which read as "player off nav".
		if ( EntityNavMeshUtility.TryFindNavAtFeet( Scene, playerAnchor, out var onFloor, vertical: StandingNavTolerance ) )
		{
			_chaseGoalReachesTarget = true;
			_lastOnNavChasePoint = onFloor;
			_lastOnNavChaseAt = Time.NowDouble;
			return onFloor;
		}

		// Briefly off nav — a jump mid-run, a sprint across a poly seam, the trimmed strip at a roof
		// edge. Keep hunting the last spot they truly stood on instead of instantly re-targeting the
		// terrain under their XY: that flicker is what sent a roof chaser diving off the structure to
		// the ground the moment the player hopped away after a missed swing.
		if ( Time.NowDouble - _lastOnNavChaseAt <= OffNavGraceSeconds
		     && Vector3.DistanceBetween( _lastOnNavChasePoint, playerAnchor ) <= OffNavGraceMaxDrift )
		{
			_chaseGoalReachesTarget = true;
			return _lastOnNavChasePoint;
		}

		// Grappling / airborne / off-mesh: don't path to sky — chase the ground under their XY.
		// That goal is NOT the player — breach logic treats it as blocked.
		_chaseGoalReachesTarget = false;
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
		BreachClaims.Release( this );
		EntityAttackSlots.Release( this );
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

	public void RequestImmediateRepath()
	{
		_needsImmediatePathCheck = true;
	}
}
