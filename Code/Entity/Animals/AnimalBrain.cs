using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-only animal AI. Every species runs the same master state machine
/// (<see cref="TickStateMachine"/>); the species' <see cref="AnimalBehaviorProfile"/>
/// (from <c>data/animal_behaviors.json</c>) picks which transitions exist:
/// prey flee on any stimulus, harassers (fox/coyote) track → bite → flee → repeat,
/// predators (lynx/wolf) track → attack until dead and flee only at low health.
/// Threats are plain GameObjects so other entities can scare animals too.
///
/// Movement: the NavMeshAgent drives (paths around water/cliffs/builds); repaths are
/// throttled and compared against RAW target positions so projection jitter can't starve
/// the async path. If the agent produces no motion for a few seconds it is benched and
/// the animal walks the terrain heightfield directly until nav placement succeeds again —
/// a broken path can never freeze an animal.
/// </summary>
[Title( "Animal Brain" )]
public sealed class AnimalBrain : Component
{
	[Property] public AnimalSpecies Species { get; set; } = AnimalSpecies.Whitetail;
	[Property] public EntityVitals Vitals { get; set; }
	[Property] public EntityLocomotion Locomotion { get; set; }
	[Property] public NavMeshAgent Agent { get; set; }

	[Property, Group( "Debug" ), Title( "Log AI state changes" )]
	public bool LogStateDebug { get; set; }

	AnimalBehaviorProfile _behavior = AnimalBehaviorProfile.CreateFallback();

	AnimalAiState _state = AnimalAiState.Idle;
	GameObject _threat;
	Vector3 _threatLastPos;
	double _threatLastSensedAt;

	Vector3 _homePosition;
	Vector3 _moveGoal;
	double _stateEndsAt;
	double _nextPerceptionAt;
	double _attackReadyAt;
	/// <summary>&gt; 0 while a bite windup is running; the bite lands at this time.</summary>
	double _biteLandsAt;
	int _attacksDone;
	double _stareStartedAt;
	bool _fleeingLowHealth;
	/// <summary>Short stare-spook hop (turn around and re-track after one leg) vs a full flee.</summary>
	bool _fleeIsStareHop;
	float _fleeLegDistance;
	float _fleeYawNudge;
	double _lowHealthFleeSuppressedUntil;
	Vector3 _progressPos;
	double _progressAt;
	double _nextMoveDebugAt;
	bool _aiStarted;

	// Agent bookkeeping
	bool _agentOnNav;
	Vector3 _issuedTargetRaw;
	double _nextRepathAt;
	double _agentIdleSince;
	double _navRetryAt;

	const float ReachDistance = 96f;
	/// <summary>Minimum real walk for a wander leg — shorter projected goals count as "no usable nav here".</summary>
	const float MinWanderLegDistance = 200f;
	const float PerceptionInterval = 0.25f;
	/// <summary>No fresh sight/sound for this long while tracking → lose interest.</summary>
	const float TrackGiveUpSeconds = 15f;
	/// <summary>Arrived at the last-known spot with nothing sensed this long → trail is cold, back to wander/graze.</summary>
	const float ColdTrailSeconds = 2.5f;
	/// <summary>Predator that turns to fight ignores the low-health flee gate this long.</summary>
	const float ReengageFightSeconds = 6f;
	/// <summary>No rotating on a dime — animation-driven look pauses come later with real models.</summary>
	const float TurnDegreesPerSecond = 200f;
	/// <summary>Player aim within this half-angle of the animal counts as staring at it (remote-player fallback).</summary>
	const float StareConeHalfAngleDegrees = 15f;
	/// <summary>Player eye height for stare/LOS checks (matches combat's ServerEyeHeight).</summary>
	const float PlayerEyeHeight = 64f;
	/// <summary>Agent claims to navigate but produces no wish this long → bench it, walk manually.</summary>
	const float AgentWedgedSeconds = 3f;
	/// <summary>After benching the agent, walk manually this long before trying nav again.</summary>
	const float ManualHoldSeconds = 10f;
	/// <summary>Manual mode: wanting to move but barely displaced this long = blocked by a solid.</summary>
	const float StallSeconds = 2.5f;
	const float StallDistance = 12f;
	/// <summary>Manual mode: only move once roughly facing the travel direction (turn-then-go).</summary>
	const float MoveAlignMinDot = 0.35f;
	/// <summary>Re-issue the agent path only when the raw target moved this far (projection jitter is smaller).</summary>
	const float RepathTargetMoveThreshold = 120f;
	/// <summary>
	/// The NavMeshAgent produces no motion below roughly this MaxSpeed (empirical: dead at 48,
	/// healthy at 120). Slower configured speeds are floored to this while the agent drives.
	/// </summary>
	const float AgentMinDriveSpeed = 120f;

	public AnimalAiState CurrentState => _state;
	public GameObject CurrentThreat => _threat.IsValid() ? _threat : null;
	public AnimalThreatResponse ThreatResponse => _behavior.ThreatResponse;

	protected override void OnStart()
	{
		Vitals ??= Components.Get<EntityVitals>();
		Locomotion ??= Components.Get<EntityLocomotion>();
		Agent ??= Components.Get<NavMeshAgent>();

		if ( _homePosition == default )
			_homePosition = GameObject.WorldPosition;

		if ( Vitals is not null )
		{
			Vitals.OnDied += OnDeath;
			Vitals.OnDamaged += OnDamaged;
		}

		// Spawner calls BeginAiNow — don't auto-start here.
	}

	protected override void OnDestroy()
	{
		if ( Vitals is not null )
		{
			Vitals.OnDied -= OnDeath;
			Vitals.OnDamaged -= OnDamaged;
		}
	}

	public void ApplyBehavior( AnimalBehaviorProfile behavior ) => _behavior = behavior;

	public void SetHomePosition( Vector3 home ) => _homePosition = home;

	/// <summary>Nav placement succeeded at spawn — the agent may drive immediately.</summary>
	public void MarkAgentOnNav()
	{
		_agentOnNav = true;
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is not null && Agent.IsValid() )
			Agent.UpdatePosition = true;
	}

	public void BeginAiNow()
	{
		_aiStarted = true;
		_threat = null;
		EnterState( AnimalAiState.Wander );
	}

	/// <summary>Host: noises from the noise bus. Any audible player action registers the source as a threat.</summary>
	public void TryHearNoise( Vector3 noiseWorldPos, EntityNoiseKind kind, GameObject source )
	{
		if ( !CanRunHostLogic() || IsDead() )
			return;

		var range = kind == EntityNoiseKind.SneakFootstep
			? _behavior.HearRange * _behavior.SneakHearFraction
			: _behavior.HearRange;
		if ( range <= 1e-3f )
			return;

		if ( Vector3.DistanceBetween( GameObject.WorldPosition, noiseWorldPos ) > range )
			return;

		SenseThreat( source, noiseWorldPos );
	}

	/// <summary>Any system (other entities included) can scare / provoke this animal.</summary>
	public void NotifyThreat( GameObject threat, Vector3 threatWorldPos )
	{
		if ( !CanRunHostLogic() || IsDead() )
			return;

		SenseThreat( threat, threatWorldPos );
	}

	protected override void OnUpdate()
	{
		if ( !CanRunHostLogic() || IsDead() || !_aiStarted )
			return;

		Locomotion ??= Components.Get<EntityLocomotion>();
		Agent ??= Components.Get<NavMeshAgent>();

		if ( Locomotion is not null && Locomotion.IsAirborne )
		{
			Agent?.Stop();
			return;
		}

		TickNavRecovery();
		TickPerception();
		TickLowHealthFlee();
		TickStateMachine();
	}

	/// <summary>The one master method — every species runs this same switch.</summary>
	void TickStateMachine()
	{
		switch ( _state )
		{
			case AnimalAiState.Idle: TickIdle(); break;
			case AnimalAiState.Wander: TickWander(); break;
			case AnimalAiState.Graze: TickGraze(); break;
			case AnimalAiState.Alerted: TickAlerted(); break;
			case AnimalAiState.Tracking: TickTracking(); break;
			case AnimalAiState.Attacking: TickAttacking(); break;
			case AnimalAiState.Fleeing: TickFleeing(); break;
		}
	}

	// ------------------------------------------------------------------
	// Perception
	// ------------------------------------------------------------------

	/// <summary>Interval sight scan over players. Hearing arrives via <see cref="TryHearNoise"/>.</summary>
	void TickPerception()
	{
		if ( !Scene.IsValid() || Time.NowDouble < _nextPerceptionAt )
			return;

		_nextPerceptionAt = Time.NowDouble + PerceptionInterval;

		var origin = GameObject.WorldPosition;
		var facing = GameObject.WorldRotation.Forward;
		var eye = origin + Vector3.Up * _behavior.EyeHeight;

		foreach ( var pv in Scene.GetAllComponents<PlayerVitals>() )
		{
			var go = pv.GameObject;
			if ( !IsValidThreat( go ) )
				continue;

			var dist = Vector3.DistanceBetween( origin, go.WorldPosition );
			if ( dist > _behavior.SightRange )
				continue;

			if ( !EntitySight.IsInFov( origin, facing, go.WorldPosition, _behavior.SightFovDegrees ) )
				continue;

			if ( !EntitySight.HasClearLos( Scene, eye, GameObject, go, _behavior.EyeHeight ) )
				continue;

			SenseThreat( go, go.WorldPosition );
			return;
		}
	}

	void SenseThreat( GameObject threat, Vector3 worldPos )
	{
		if ( threat.IsValid() && threat != GameObject )
			_threat = threat;

		_threatLastPos = worldPos;
		_threatLastSensedAt = Time.NowDouble;

		// Calm states decide how to react; alert/combat/flee states read the refreshed position in their tick.
		if ( _state is not (AnimalAiState.Idle or AnimalAiState.Wander or AnimalAiState.Graze) )
			return;

		switch ( _behavior.ThreatResponse )
		{
			case AnimalThreatResponse.Flee:
				EnterState( AnimalAiState.Fleeing );
				break;

			case AnimalThreatResponse.Harass:
			case AnimalThreatResponse.Predator:
				// A player noticed beyond the start range is remembered but not stalked yet.
				if ( Vector3.DistanceBetween( GameObject.WorldPosition, worldPos ) <= _behavior.TrackStartRange )
					EnterState( AnimalAiState.Alerted );
				break;
		}
	}

	void OnDamaged( Component attacker )
	{
		var source = attacker is { } c && c.GameObject.IsValid() && c.GameObject != GameObject
			? c.GameObject
			: null;
		if ( source is null )
			return;

		_threat = source;
		_threatLastPos = source.WorldPosition;
		_threatLastSensedAt = Time.NowDouble;

		if ( _behavior.ThreatResponse == AnimalThreatResponse.Predator )
		{
			// Already running from low health — a hit is not an invitation to turn around.
			if ( _state == AnimalAiState.Fleeing && _fleeingLowHealth )
				return;

			// Fight back unless the low-health gate flips it to flee next tick.
			if ( _state != AnimalAiState.Attacking )
				EnterState( AnimalAiState.Attacking );
			return;
		}

		// Prey and harassers that take a hit always break off and run.
		if ( _state != AnimalAiState.Fleeing )
			EnterState( AnimalAiState.Fleeing );
	}

	/// <summary>Predator gate: engaged and health at/below the flee fraction → run (unless it just turned to fight).</summary>
	void TickLowHealthFlee()
	{
		if ( _behavior.ThreatResponse != AnimalThreatResponse.Predator || _behavior.FleeHealthFraction <= 0f )
			return;

		if ( _state is not (AnimalAiState.Alerted or AnimalAiState.Tracking or AnimalAiState.Attacking) )
			return;

		if ( Time.NowDouble < _lowHealthFleeSuppressedUntil )
			return;

		Vitals ??= Components.Get<EntityVitals>();
		if ( Vitals is null || Vitals.HealthFraction > _behavior.FleeHealthFraction )
			return;

		EnterState( AnimalAiState.Fleeing, lowHealth: true );
	}

	// ------------------------------------------------------------------
	// Calm loop: Idle ⇄ Wander ⇄ Graze
	// ------------------------------------------------------------------

	void TickIdle()
	{
		if ( Time.NowDouble >= _stateEndsAt )
			EnterState( AnimalAiState.Wander );
	}

	void TickGraze()
	{
		if ( Time.NowDouble >= _stateEndsAt )
			EnterState( AnimalAiState.Wander );
	}

	void TickWander()
	{
		if ( _moveGoal == default )
		{
			EnterState( AnimalAiState.Idle );
			return;
		}

		Locomotion?.SetTravelHint( _moveGoal );
		TickMoveDebug();

		if ( FlatDistanceTo( _moveGoal ) <= ReachDistance )
		{
			EnterRestAfterWander();
			return;
		}

		// Manual mode walked into something solid — pick a fresh direction instead of grinding the wall.
		if ( !IsAgentDriving() && HasStalled() )
		{
			PickWanderGoal();
			ResetStall();
			return;
		}

		MoveTowardTarget( _moveGoal, _moveGoal, _behavior.WalkSpeed, 1.0f, ReachDistance );
	}

	void EnterRestAfterWander()
	{
		Locomotion?.ClearTravelHint();
		var graze = Sandbox.Game.Random.Float( 0f, 1f ) < _behavior.GrazeChance;
		EnterState( graze ? AnimalAiState.Graze : AnimalAiState.Idle );
	}

	void PickWanderGoal()
	{
		var origin = GameObject.WorldPosition;
		var facing = GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( facing.LengthSquared < 1e-6f )
			facing = Vector3.Forward;
		else
			facing = facing.Normal;

		var radius = Math.Max( MinWanderLegDistance * 2f, _behavior.WanderRadius );
		Vector3 ideal = default;

		// A goal that projects back onto (or right next to) us is "reached" instantly and turns
		// wander into a stand-still graze loop — demand a real walk when using nav.
		for ( var attempt = 0; attempt < 5; attempt++ )
		{
			var dir = Rotation.FromYaw( Sandbox.Game.Random.Float( -150f, 150f ) ) * facing;
			ideal = origin + dir * radius;

			if ( !IsAgentDriving() )
			{
				_moveGoal = ideal;
				return;
			}

			if ( !EntityNavMeshUtility.TryProjectToNavMesh( Scene, ideal, out var onNav, NavProjectTier.Full ) )
				continue;

			if ( Vector3.DistanceBetween( origin.WithZ( 0f ), onNav.WithZ( 0f ) ) < MinWanderLegDistance )
				continue;

			_moveGoal = onNav;
			return;
		}

		// Nav has no real walk to offer here — bench the agent and walk the heightfield.
		BenchAgent( "no usable nav for wander goal" );
		_moveGoal = ideal == default ? origin + facing * radius : ideal;
	}

	// ------------------------------------------------------------------
	// Alerted / Tracking / Attacking (harass + predator)
	// ------------------------------------------------------------------

	void TickAlerted()
	{
		Agent?.Stop();
		Locomotion?.SmoothFaceTowardWorld( _threatLastPos, TurnDegreesPerSecond );

		if ( TickStareSpook( ThreatDistance() ) )
			return;

		if ( Time.NowDouble < _stateEndsAt )
			return;

		if ( _threat.IsValid() && ThreatDistance() <= _behavior.TrackRange )
			EnterState( AnimalAiState.Tracking );
		else
			EnterState( AnimalAiState.Wander );
	}

	void TickTracking()
	{
		if ( !IsValidThreat( _threat ) )
		{
			EnterState( AnimalAiState.Wander );
			return;
		}

		var dist = ThreatDistance();
		if ( dist <= _behavior.LungeRange )
		{
			// Committed — from here it rushes and no stare stops it.
			EnterState( AnimalAiState.Attacking );
			return;
		}

		if ( TickStareSpook( dist ) )
			return;

		// Wandered off, or gone quiet + unseen for too long — lose interest.
		if ( dist > _behavior.TrackRange * 1.2f
		     || Time.NowDouble - _threatLastSensedAt >= TrackGiveUpSeconds )
		{
			EnterState( AnimalAiState.Wander );
			return;
		}

		// Cold trail: standing on the last seen/heard spot with nothing new — back to the wander/graze loop.
		if ( Time.NowDouble - _threatLastSensedAt >= ColdTrailSeconds
		     && FlatDistanceTo( _threatLastPos ) <= ReachDistance )
		{
			EnterState( AnimalAiState.Wander );
			return;
		}

		// Manual mode blocked mid-stalk (wall between us) — give up rather than grind in place.
		if ( !IsAgentDriving() && HasStalled() )
		{
			EnterState( AnimalAiState.Wander );
			return;
		}

		Locomotion?.SetTravelHint( _threatLastPos );
		TickMoveDebug();
		MoveTowardTarget( _threatLastPos, GetNavThreatPoint(), _behavior.SneakSpeed, 1.0f, ReachDistance );
	}

	void TickAttacking()
	{
		if ( !IsValidThreat( _threat ) )
		{
			// Target died or vanished mid-bite — stand down.
			_biteLandsAt = 0d;
			EnterState( AnimalAiState.Wander );
			return;
		}

		_threatLastPos = _threat.WorldPosition;
		_threatLastSensedAt = Time.NowDouble;

		var dist = ThreatDistance();

		// Bite windup running — hold position and land it.
		if ( _biteLandsAt > 0d )
		{
			Agent?.Stop();
			Locomotion?.SmoothFaceTowardWorld( _threatLastPos, TurnDegreesPerSecond );
			if ( Time.NowDouble >= _biteLandsAt )
				LandBite( dist );
			return;
		}

		// They pulled away — harassers drop back to the stalk, predators keep running them down.
		if ( _behavior.ThreatResponse == AnimalThreatResponse.Harass && dist > _behavior.LungeRange * 1.6f )
		{
			EnterState( AnimalAiState.Tracking );
			return;
		}

		if ( _behavior.ThreatResponse == AnimalThreatResponse.Predator && dist > _behavior.TrackRange * 1.2f )
		{
			EnterState( AnimalAiState.Wander );
			return;
		}

		if ( dist <= _behavior.AttackRange && Time.NowDouble >= _attackReadyAt )
		{
			Agent?.Stop();
			_biteLandsAt = Time.NowDouble + _behavior.AttackWindupSeconds;
			return;
		}

		Locomotion?.SetTravelHint( _threatLastPos );
		TickMoveDebug();
		// Manual close-in stops inside bite range, not the generic reach stop.
		MoveTowardTarget( _threatLastPos, GetNavThreatPoint(), _behavior.RunSpeed, 0.6f, _behavior.AttackRange * 0.8f );
	}

	/// <summary>
	/// Spookable stalker beyond commit (lunge) range: the player holding their aim on it
	/// for <c>stareSpookSeconds</c> sends it fleeing. True when it fled.
	/// </summary>
	bool TickStareSpook( float dist )
	{
		if ( !_behavior.SpookedByStare || dist <= _behavior.LungeRange )
		{
			_stareStartedAt = 0d;
			return false;
		}

		if ( !IsThreatStaringAtMe() )
		{
			_stareStartedAt = 0d;
			return false;
		}

		if ( _stareStartedAt <= 0d )
		{
			_stareStartedAt = Time.NowDouble;
			return false;
		}

		if ( Time.NowDouble - _stareStartedAt < _behavior.StareSpookSeconds )
			return false;

		// Caught staring — short retreat hop, then it turns around and stalks again.
		EnterState( AnimalAiState.Fleeing, stareHop: true );
		return true;
	}

	bool IsThreatStaringAtMe()
	{
		if ( !_threat.IsValid() )
			return false;

		var controller = _threat.Components.Get<PlayerController>();
		if ( controller is null )
			return false;

		var myEye = GameObject.WorldPosition + Vector3.Up * _behavior.EyeHeight;

		// Local player: screen test — staring means we sit in the middle third of their screen.
		if ( !_threat.IsProxy && TryCheckScreenCenter( myEye, out var centered ) )
		{
			if ( !centered )
				return false;
		}
		else
		{
			// Remote player (host has no camera for them): eye-aim cone fallback.
			var playerEye = _threat.WorldPosition + Vector3.Up * PlayerEyeHeight;
			var toMe = myEye - playerEye;
			if ( toMe.LengthSquared >= 1f
			     && Vector3.Dot( controller.EyeAngles.ToRotation().Forward, toMe.Normal )
			        < MathF.Cos( StareConeHalfAngleDegrees * (MathF.PI / 180f) ) )
				return false;
		}

		// Staring through a wall spooks nothing.
		return EntitySight.HasClearLos( Scene, myEye, GameObject, _threat, PlayerEyeHeight );
	}

	/// <summary>False when no usable local camera. Centered = our eye projects into the middle third of the screen (both axes).</summary>
	bool TryCheckScreenCenter( Vector3 worldPos, out bool centered )
	{
		centered = false;
		var cam = Scene.Camera;
		if ( cam is null || !cam.IsValid() )
			return false;

		var size = Screen.Size;
		if ( size.x < 1f || size.y < 1f )
			return false;

		var px = cam.BBoxToScreenPixels( BBox.FromPositionAndSize( worldPos, 4f ), out var onScreen );
		if ( !onScreen && px.Width < 0.5f && px.Height < 0.5f )
			return true; // projection valid, we're simply not on their screen

		var cx = px.Left + px.Width * 0.5f;
		var cy = px.Top + px.Height * 0.5f;
		centered = cx >= size.x / 3f && cx <= size.x * (2f / 3f)
		           && cy >= size.y / 3f && cy <= size.y * (2f / 3f);
		return true;
	}

	void LandBite( float dist )
	{
		_biteLandsAt = 0d;
		_attackReadyAt = Time.NowDouble + _behavior.AttackCooldownSeconds;

		// Generous land window — the windup already telegraphed it.
		if ( dist <= _behavior.AttackRange * 1.4f )
		{
			DealBiteDamage( _threat, _behavior.AttackDamage );
			_attacksDone++;
		}

		if ( _behavior.ThreatResponse == AnimalThreatResponse.Harass
		     && _behavior.AttacksBeforeFlee > 0
		     && _attacksDone >= _behavior.AttacksBeforeFlee )
		{
			EnterState( AnimalAiState.Fleeing, postBite: true );
		}
	}

	void DealBiteDamage( GameObject target, float damage )
	{
		if ( !target.IsValid() || damage <= 0f )
			return;

		var receiver = target.Components.Get<DamageReceiver>( FindMode.EnabledInSelfAndDescendants );
		if ( receiver is not null )
		{
			receiver.TakeDamage( damage, this );
			return;
		}

		target.Components.Get<PlayerVitals>()?.ApplyDamageAfterArmor( damage, this );
	}

	// ------------------------------------------------------------------
	// Fleeing — leg-based: run one full leg away from where the threat was,
	// then re-check the pursuit. Escape points are sampled EQS-style: candidates
	// on a ring away from the threat, filtered by nav reachability, best first.
	// ------------------------------------------------------------------

	void TickFleeing()
	{
		// Live pursuit keeps the flee-from point fresh.
		if ( _threat.IsValid() )
			_threatLastPos = _threat.WorldPosition;

		var threatDist = ThreatDistance();

		// Predator low-health flee: the player pressing in flips it back to a fight.
		if ( _behavior.ThreatResponse == AnimalThreatResponse.Predator
		     && _fleeingLowHealth
		     && IsValidThreat( _threat )
		     && threatDist <= _behavior.ReengageRange )
		{
			_lowHealthFleeSuppressedUntil = Time.NowDouble + ReengageFightSeconds;
			EnterState( AnimalAiState.Attacking );
			return;
		}

		if ( _moveGoal == default && !TryPickFleePoint( _fleeLegDistance, out _moveGoal ) )
		{
			// Cornered with nowhere reachable — fighters turn and fight, prey freezes briefly.
			if ( _behavior.ThreatResponse != AnimalThreatResponse.Flee && IsValidThreat( _threat ) )
			{
				_lowHealthFleeSuppressedUntil = Time.NowDouble + ReengageFightSeconds;
				EnterState( AnimalAiState.Attacking );
			}
			else
			{
				EnterState( AnimalAiState.Idle );
			}
			return;
		}

		// Leg complete — decide whether the pursuit is over.
		if ( FlatDistanceTo( _moveGoal ) <= ReachDistance )
		{
			OnFleeLegFinished( threatDist );
			return;
		}

		// Blocked mid-leg (manual mode) — re-pick with a sideways nudge.
		if ( !IsAgentDriving() && HasStalled() )
		{
			_fleeYawNudge = Sandbox.Game.Random.Float( 60f, 120f ) * (Sandbox.Game.Random.Float( -1f, 1f ) < 0f ? -1f : 1f);
			_moveGoal = default;
			ResetStall();
			return;
		}

		Locomotion?.SetTravelHint( _moveGoal );
		TickMoveDebug();
		MoveTowardTarget( _moveGoal, _moveGoal, _behavior.RunSpeed, 0.6f, 8f );
	}

	void OnFleeLegFinished( float threatDist )
	{
		// Stare hop: one short leg, then turn around and get back to stalking.
		if ( _fleeIsStareHop )
		{
			if ( IsValidThreat( _threat ) && threatDist <= _behavior.TrackRange )
				EnterState( AnimalAiState.Tracking );
			else
				EnterState( AnimalAiState.Wander );
			return;
		}

		// Still being chased — run another full leg from wherever the pursuer is now.
		if ( IsValidThreat( _threat ) && threatDist < _behavior.CalmRange )
		{
			_moveGoal = default;
			ResetStall();
			return;
		}

		OnFleeCalmed();
	}

	/// <summary>
	/// EQS-style escape point: candidates on a ring one leg away from us, straight-away first
	/// then increasing slants; agent mode requires nav projection, real distance gained on the
	/// threat, and a valid path — first candidate that passes wins.
	/// </summary>
	bool TryPickFleePoint( float legDistance, out Vector3 goal )
	{
		goal = default;
		var origin = GameObject.WorldPosition;
		var away = (origin - _threatLastPos).WithZ( 0f );
		if ( away.LengthSquared < 1e-4f )
			away = -GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( away.LengthSquared < 1e-4f )
			away = Vector3.Forward;
		away = away.Normal;

		if ( MathF.Abs( _fleeYawNudge ) > 1f )
		{
			away = Rotation.FromYaw( _fleeYawNudge ) * away;
			_fleeYawNudge = 0f;
		}

		if ( !IsAgentDriving() )
		{
			// Manual: straight-line leg; the stall handler nudges sideways when blocked.
			goal = origin + away * legDistance;
			return true;
		}

		var threatFlatDist = Vector3.DistanceBetween( origin.WithZ( 0f ), _threatLastPos.WithZ( 0f ) );
		float[] yaws = { 0f, 35f, -35f, 70f, -70f, 110f, -110f, 150f, -150f };
		foreach ( var yaw in yaws )
		{
			var dir = Rotation.FromYaw( yaw ) * away;
			var ideal = origin + dir * legDistance;

			if ( !EntityNavMeshUtility.TryProjectToNavMesh( Scene, ideal, out var onNav, NavProjectTier.Full ) )
				continue;

			// Must genuinely gain ground on the threat, not curl back toward it.
			var gained = Vector3.DistanceBetween( onNav.WithZ( 0f ), _threatLastPos.WithZ( 0f ) ) - threatFlatDist;
			if ( gained < legDistance * 0.35f )
				continue;

			if ( !EntityChaseRouting.QueryPath( Scene, origin, onNav, Agent ).HasPath )
				continue;

			goal = onNav;
			return true;
		}

		return false;
	}

	void OnFleeCalmed()
	{
		switch ( _behavior.ThreatResponse )
		{
			// Fox/coyote: player backed off — slink back in and keep nipping.
			case AnimalThreatResponse.Harass
				when IsValidThreat( _threat ) && ThreatDistance() <= _behavior.TrackRange:
				EnterState( AnimalAiState.Tracking );
				return;

			default:
				_threat = null;
				EnterState( AnimalAiState.Wander );
				return;
		}
	}

	// ------------------------------------------------------------------
	// State entry
	// ------------------------------------------------------------------

	void EnterState( AnimalAiState next, bool lowHealth = false, bool stareHop = false, bool postBite = false )
	{
		if ( LogStateDebug && next != _state )
			Log.Info( $"[AnimalBrain] {GameObject.Name} {_state} -> {next}" );

		_state = next;
		_biteLandsAt = 0d;
		_stareStartedAt = 0d;
		_agentIdleSince = 0d;
		_nextRepathAt = 0d;
		ResetStall();

		switch ( next )
		{
			case AnimalAiState.Idle:
				Agent?.Stop();
				Locomotion?.ClearTravelHint();
				_stateEndsAt = Time.NowDouble + Sandbox.Game.Random.Float( _behavior.IdleMinSeconds, _behavior.IdleMaxSeconds );
				break;

			case AnimalAiState.Graze:
				Agent?.Stop();
				Locomotion?.ClearTravelHint();
				_stateEndsAt = Time.NowDouble + Sandbox.Game.Random.Float( _behavior.GrazeMinSeconds, _behavior.GrazeMaxSeconds );
				break;

			case AnimalAiState.Wander:
				PickWanderGoal();
				break;

			case AnimalAiState.Alerted:
				Agent?.Stop();
				Locomotion?.ClearTravelHint();
				_stateEndsAt = Time.NowDouble + _behavior.AlertSeconds;
				break;

			case AnimalAiState.Tracking:
				_attacksDone = 0;
				break;

			case AnimalAiState.Attacking:
				break;

			case AnimalAiState.Fleeing:
				_fleeingLowHealth = lowHealth;
				_fleeIsStareHop = stareHop;
				// After landing a bite the break-off is half a normal flee leg — nip and hop out, not a full rout.
				_fleeLegDistance = stareHop
					? _behavior.StareRetreatDistance
					: (postBite ? _behavior.FleeDistance * 0.5f : _behavior.FleeDistance);
				_fleeYawNudge = 0f;
				_moveGoal = default;
				break;
		}
	}

	// ------------------------------------------------------------------
	// Movement — agent-driven with manual heightfield fallback
	// ------------------------------------------------------------------

	/// <summary>
	/// One movement entry for every state. Agent mode issues throttled MoveTo repaths keyed on the
	/// RAW target (projection jitter can't starve the async path) and watches for a wedged agent;
	/// manual mode walks the heightfield turn-then-go.
	/// </summary>
	void MoveTowardTarget( Vector3 rawTarget, Vector3 navGoal, float speed, float repathInterval, float stopDistance )
	{
		if ( IsAgentDriving() )
		{
			// The agent is dead below ~120 — floor it; the visual "slow stalk" comes from
			// animation later, not from starving the agent (pulsing read as stutter).
			ApplySpeed( Math.Max( speed, AgentMinDriveSpeed ) );
			IssueAgentMove( rawTarget, navGoal, repathInterval );
			TickAgentWatchdog();
			return;
		}

		TickManualStep( rawTarget, speed, stopDistance );
	}

	bool IsAgentDriving() =>
		Agent is not null && Agent.IsValid() && Agent.Enabled && _agentOnNav;

	void IssueAgentMove( Vector3 rawTarget, Vector3 navGoal, float interval )
	{
		if ( Time.NowDouble < _nextRepathAt )
			return;

		// Cruising toward (roughly) the same real target — leave the async path alone.
		if ( Agent.IsNavigating
		     && Vector3.DistanceBetween( rawTarget.WithZ( 0f ), _issuedTargetRaw.WithZ( 0f ) ) < RepathTargetMoveThreshold )
		{
			_nextRepathAt = Time.NowDouble + interval;
			return;
		}

		_nextRepathAt = Time.NowDouble + Math.Max( 0.5f, interval );
		Agent.MoveTo( navGoal );
		_issuedTargetRaw = rawTarget;
	}

	/// <summary>Agent claims to navigate but produces no motion — bench it and walk manually for a while.</summary>
	void TickAgentWatchdog()
	{
		var wish = Agent.WishVelocity.WithZ( 0f ).Length;
		if ( Agent.IsNavigating && wish >= 8f )
		{
			_agentIdleSince = 0d;
			return;
		}

		if ( _agentIdleSince <= 0d )
		{
			_agentIdleSince = Time.NowDouble;
			return;
		}

		if ( Time.NowDouble - _agentIdleSince < AgentWedgedSeconds )
			return;

		_agentIdleSince = 0d;
		BenchAgent( "agent produced no motion" );
	}

	void BenchAgent( string reason )
	{
		if ( LogStateDebug )
			Log.Info( $"[AnimalBrain] {GameObject.Name} benching nav agent ({reason}) — manual walking for {ManualHoldSeconds}s" );

		_agentOnNav = false;
		_navRetryAt = Time.NowDouble + ManualHoldSeconds;
		if ( Agent is not null && Agent.IsValid() )
		{
			Agent.Stop();
			Agent.UpdatePosition = false;
		}
	}

	/// <summary>Benched or never placed — periodically try to win the nav agent back.</summary>
	void TickNavRecovery()
	{
		if ( _agentOnNav || Time.NowDouble < _navRetryAt )
			return;

		_navRetryAt = Time.NowDouble + 1.5d;

		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() || !Scene.IsValid() )
			return;

		BuildNavMeshSync.EnsureNavAroundPoint( Scene, GameObject.WorldPosition );
		if ( !EntityNavMeshUtility.EnsureAgentOnNavMesh( Scene, Agent, GameObject.WorldPosition ) )
			return;

		_agentOnNav = true;
		Agent.UpdatePosition = true;
		_nextRepathAt = 0d;
		_issuedTargetRaw = default;

		// Re-pick calm-loop goals so the agent starts with a nav-valid target.
		if ( _state is AnimalAiState.Idle or AnimalAiState.Wander or AnimalAiState.Graze )
			EnterState( AnimalAiState.Wander );
	}

	/// <summary>
	/// Nav goal for stalking/rushing the threat. The raw player position sits off the nav polys
	/// (feet above mesh, props, grapple) and MoveTo to it never paths — project it first,
	/// same as the scav's live-chase point.
	/// </summary>
	Vector3 GetNavThreatPoint()
	{
		var anchor = _threatLastPos;
		var selfZ = GameObject.WorldPosition.z;
		// Airborne / grappling threat: chase the ground under their XY, not the sky.
		if ( anchor.z - selfZ > 96f )
			anchor = anchor.WithZ( selfZ );

		return EntityLocomotion.GetNavChasePoint( Scene, anchor );
	}

	void ApplySpeed( float speed )
	{
		Locomotion ??= Components.Get<EntityLocomotion>();
		if ( Locomotion is not null )
			Locomotion.SetIntendedMaxSpeed( speed );
		else if ( Agent is not null && Agent.IsValid() )
			Agent.MaxSpeed = speed;
	}

	/// <summary>Manual mode: turn toward the goal at a fixed rate; walk once roughly facing it. Heightfield handles Z.</summary>
	void TickManualStep( Vector3 goal, float speed, float stopDistance )
	{
		var pos = GameObject.WorldPosition;
		var to = (goal - pos).WithZ( 0f );
		var flat = to.Length;
		if ( flat <= stopDistance )
			return;

		var dir = to / flat;

		Locomotion ??= Components.Get<EntityLocomotion>();
		if ( Locomotion is not null )
			Locomotion.SmoothFaceTowardWorld( goal, TurnDegreesPerSecond );
		else
			GameObject.WorldRotation = Rotation.LookAt( dir, Vector3.Up );

		var forward = GameObject.WorldRotation.Forward.WithZ( 0f );
		var align = forward.LengthSquared > 1e-6f ? Vector3.Dot( forward.Normal, dir ) : 1f;
		if ( align < MoveAlignMinDot )
			return; // still turning in place

		// Ease in as the body lines up so corners are arcs, not pivots.
		var ramp = Math.Clamp( (align - MoveAlignMinDot) / (1f - MoveAlignMinDot), 0.3f, 1f );
		var dt = Math.Max( Time.Delta, 1e-4f );
		var next = pos + dir * (Math.Max( 24f, speed ) * ramp * dt);

		if ( Locomotion is not null && Locomotion.TrySampleTerrainGroundZ( next, out var groundZ ) )
		{
			var climb = Math.Clamp( groundZ - next.z, -140f * dt, 72f * dt );
			next.z += climb;
		}

		GameObject.WorldPosition = next;
	}

	/// <summary>True when this animal has wanted to move but barely displaced for a while (wall, water edge).</summary>
	bool HasStalled()
	{
		var pos = GameObject.WorldPosition;
		if ( Vector3.DistanceBetween( pos.WithZ( 0f ), _progressPos.WithZ( 0f ) ) > StallDistance )
		{
			_progressPos = pos;
			_progressAt = Time.NowDouble;
			return false;
		}

		return Time.NowDouble - _progressAt >= StallSeconds;
	}

	void ResetStall()
	{
		_progressPos = GameObject.WorldPosition;
		_progressAt = Time.NowDouble;
	}

	/// <summary>LogStateDebug only: periodic movement snapshot so a stationary animal explains itself.</summary>
	void TickMoveDebug()
	{
		if ( !LogStateDebug || Time.NowDouble < _nextMoveDebugAt )
			return;

		_nextMoveDebugAt = Time.NowDouble + 1.5;
		var agentDriving = IsAgentDriving();
		var navigating = Agent is not null && Agent.IsValid() && Agent.IsNavigating;
		var maxSpeed = Agent is not null && Agent.IsValid() ? Agent.MaxSpeed : -1f;
		var wish = Agent is not null && Agent.IsValid() ? Agent.WishVelocity.WithZ( 0f ).Length : -1f;
		Log.Info( $"[AnimalBrain] {GameObject.Name} {_state} dbg: mode={(agentDriving ? "agent" : "manual")} " +
		          $"navigating={navigating} maxSpd={maxSpeed:0} wish={wish:0} distGoal={FlatDistanceTo( _moveGoal ):0}" );
	}

	// ------------------------------------------------------------------
	// Misc
	// ------------------------------------------------------------------

	float ThreatDistance()
	{
		var reference = _threat.IsValid() ? _threat.WorldPosition : _threatLastPos;
		return Vector3.DistanceBetween( GameObject.WorldPosition, reference );
	}

	float FlatDistanceTo( Vector3 goal ) =>
		Vector3.DistanceBetween( GameObject.WorldPosition.WithZ( 0f ), goal.WithZ( 0f ) );

	bool IsValidThreat( GameObject go )
	{
		if ( go is null || !go.IsValid() || go == GameObject )
			return false;

		// Players must be alive to matter; anything else just has to exist.
		if ( go.Components.Get<PlayerVitals>() is { } pv )
			return pv.CurrentHealth > 0.001f;

		return true;
	}

	bool IsDead() => Vitals is not null && Vitals.IsDead;

	bool CanRunHostLogic()
	{
		if ( !Active || !GameObject.IsValid() || GameObject.IsProxy )
			return false;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return false;

		return true;
	}

	void OnDeath()
	{
		_threat = null;
		Agent?.Stop();
		Enabled = false;

		if ( GameObject.IsProxy )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		Components.Get<BiomePopulationSlot>()?.HandleOwnerDied();
		GameObject.Destroy();
	}
}
