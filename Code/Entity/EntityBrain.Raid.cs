using System;
using Sandbox;
using Sandbox.Navigation;

namespace Survival;

/// <summary>
/// Base raids (<see cref="BaseRaidSession"/>). A raider has one job: destroy the beds inside the
/// raid ring. It runs at the nearest bed on plain nav; near the base it picks a piece to hit — the
/// bed itself when it can reach it, else the piece between it and the bed, else the nearest
/// reachable piece of the base — using the breach helpers (claims, stands, swing reach). At most
/// three raiders work one piece (<see cref="BreachClaims"/>); the rest take the next piece of the
/// base or mill around the bed until a slot frees up.
/// <para>
/// Only a player within the raid's aggro range (3 m) pulls a raider into the normal chase / attack /
/// breach hunt — hits from further out do not; once that player is more than the drop range (3 m)
/// away it goes back to the beds. Raiders never retreat and ignore noise while raiding.
/// Piece planning only runs near the base, staggered, on a ≥1.5 s cadence — never per frame.
/// </para>
/// </summary>
public sealed partial class EntityBrain
{
	/// <summary>How often a raider looks for a player inside the aggro range.</summary>
	const float RaidAggroCheckSeconds = 0.3f;
	/// <summary>
	/// Per Mark: a player must stay inside the aggro range this long before a raider turns on them,
	/// and outside the drop range this long before it goes back to the beds. Pull and drop are both
	/// 3 m, so without this a player on the line flipped raiders (and their paths) several times a second.
	/// </summary>
	const float RaidAggroDwellSeconds = 2f;
	/// <summary>Raiders with nothing to claim re-plan this rarely (a plan is the one costly nav step).</summary>
	const float RaidIdleReplanMinSeconds = 3f;
	const float RaidIdleReplanMaxSeconds = 5f;
	/// <summary>Stand candidates (each a few path queries) one raid plan may test.</summary>
	const int RaidPlanMaxCandidates = 3;
	/// <summary>Scene-wide: at most this many raid plans run in one frame — the rest wait a frame.</summary>
	const int MaxRaidPlansPerFrame = 2;
	/// <summary>Pieces this close (flat, units) to the bed count as "the base" when picking what to hit.</summary>
	const float RaidPieceSearchRadius = 480f;
	/// <summary>Piece planning starts inside this flat distance of the bed; further out it just runs at the bed.</summary>
	const float RaidPlanRange = 720f;
	/// <summary>Loiter points around a bed nobody can be spared for (units).</summary>
	const float RaidLoiterMinRadius = 80f;
	const float RaidLoiterMaxRadius = 220f;
	const float RaidLoiterRepickSeconds = 6f;

	BaseRaidSession _raid;
	double _nextRaidAggroCheckAt;
	GameObject _raidAggroCandidate;
	double _raidAggroSince = -1d;
	double _raidDropSince = -1d;
	static float _raidPlanFrameTime = -1f;
	static int _raidPlansThisFrame;
	double _nextRaidPlanAt;
	/// <summary>When our raid piece went down (−1 = not waiting on a rebake).</summary>
	double _raidPieceDownAt = -1d;
	BuildBed _raidApproachBed;
	Vector3 _raidApproachGoal;
	Vector3 _raidLoiterGoal;
	double _raidLoiterPickedAt = -1000d;

	/// <summary>
	/// Per Mark: a straggler (raid over) that chased a player who then got this far away just goes
	/// back to wandering where it stands — no long hunt, no walking back "home" as a pack (units, 25 m).
	/// </summary>
	const float StragglerDropChaseUnits = 1000f;
	/// <summary>Survived an ended raid: wanders in any direction and drops far players (see <see cref="TickStragglerChase"/>).</summary>
	bool _isRaidStraggler;

	/// <summary>
	/// Straggler hunting a player who is gone or more than <see cref="StragglerDropChaseUnits"/> away:
	/// forget them and wander from here. Returns true when it changed state this frame.
	/// </summary>
	bool TickStragglerChase()
	{
		if ( !_isRaidStraggler || _state is not (EnemyAiState.Chasing or EnemyAiState.Attacking or EnemyAiState.Breaching or EnemyAiState.Searching) )
			return false;

		if ( EntityCombat is { IsMovementLocked: true } )
			return false;

		if ( _target.IsValid() && IsValidPlayerTarget( _target )
		     && Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition ) <= StragglerDropChaseUnits )
			return false;

		WanderFromHere();
		return true;
	}

	/// <summary>Forget the hunt and wander around where it stands (home = here).</summary>
	void WanderFromHere()
	{
		_alertMeter = 0f;
		_alertLocked = false;
		_hasSearchGoal = false;
		_chaseLastSeenAt = 0d;
		_target = null;
		ClearBreachState();
		SetHomePosition( GameObject.WorldPosition );
		EnterState( EnemyAiState.Wander );
	}

	/// <summary>Spawned by a raid that is still running.</summary>
	public bool IsRaider => _raid is not null && _raid.IsValid() && (_raid.IsRaidActive || _raidFinishingPiece);

	/// <summary>Raid over, but this raider was mid-way through a piece: it finishes that piece, then wanders (per Mark).</summary>
	bool _raidFinishingPiece;

	/// <summary>Host: this entity is a raider of <paramref name="raid"/> — no leash, the beds are the goal.</summary>
	public void JoinRaid( BaseRaidSession raid )
	{
		_raid = raid;
		LeashWanderDistance = 0f;
		LeashMaxTravelDistance = 0f;
		// Stagger first plans so a wave arriving together does not path-query in one frame.
		_nextRaidPlanAt = Time.NowDouble + Sandbox.Game.Random.Float( 0f, 1.5f );
		// Raid straight away even while still waiting for nav: RaidMoveTo walks on foot until the
		// agent is placed. Waiting for BeginAiNow left off-mesh spawns idling / wandering at the spawn.
		EnterState( EnemyAiState.Raiding );
	}

	/// <summary>
	/// Host: the raid ended with this raider alive (per Mark: no running off — they settle into normal
	/// wander where they stand). Home becomes the current spot, so wander legs stay around here; the
	/// ordinary perception rules apply again. <see cref="BaseRaidSession"/> despawns it later, once
	/// it has been out a while and nobody is looking.
	/// </summary>
	public void EndRaid()
	{
		_isRaidStraggler = true;

		// Per Mark: a raider mid-way through a wall finishes that wall first (the raid state keeps
		// running for it with the finishing flag), then wanders like the rest.
		if ( _state == EnemyAiState.Raiding && Alive( _breachPiece ) && _breachPiece.Components.Get<BuildBed>() is null )
		{
			_raidFinishingPiece = true;
			LogRaid( $"raid over — finishing {_breachPiece.GameObject.Name} first" );
			return;
		}

		_raid = null;
		_alertMeter = 0f;
		_alertLocked = false;
		_hasSearchGoal = false;
		_chaseLastSeenAt = 0d;
		_target = null;
		ClearBreachState();
		SetHomePosition( GameObject.WorldPosition );
		// Stand a random moment before the first wander leg: twenty raiders picking wander goals in
		// the same frame (a nav projection + a path query each) was the hitch when the bed fell.
		EnterState( EnemyAiState.Idle, forceIdleSeconds: Sandbox.Game.Random.Float( 0.3f, 2.5f ) );
	}

	/// <summary>
	/// Keeps a raider on task (runs before the state switch): pulled-off raiders whose player got
	/// away, and any non-raid state (idle, wander, search, returning), go back to raiding.
	/// Returns true when it changed state this frame.
	/// </summary>
	bool TickRaidState()
	{
		if ( _raid is null )
			return false;

		if ( !IsRaider )
		{
			// Raid is over (won or called off): whoever is left goes back to normal life.
			_raid = null;
			if ( _state == EnemyAiState.Raiding )
			{
				ClearBreachState();
				EnterState( EnemyAiState.Idle );
				return true;
			}

			return false;
		}

		switch ( _state )
		{
			case EnemyAiState.Raiding:
				return false;
			case EnemyAiState.Chasing or EnemyAiState.Attacking or EnemyAiState.Breaching:
			{
				if ( EntityCombat is { IsMovementLocked: true } )
					return false;

				if ( _target.IsValid() && IsValidPlayerTarget( _target ) )
				{
					var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );
					if ( dist <= _raid.DropAggroRangeUnits )
					{
						_raidDropSince = -1d;
						return false;
					}

					// Out of range, but only for real once it has held for the dwell.
					var now = Time.NowDouble;
					if ( _raidDropSince < 0d )
						_raidDropSince = now;
					if ( now - _raidDropSince < RaidAggroDwellSeconds )
						return false;

					LogRaid( $"player {_target.Name} {dist:0}u away for {RaidAggroDwellSeconds:0}s — back to the beds" );
				}

				ResumeRaid();
				return true;
			}
			default:
				ResumeRaid();
				return true;
		}
	}

	void ResumeRaid()
	{
		_alertMeter = 0f;
		_alertLocked = false;
		_hasSearchGoal = false;
		_chaseLastSeenAt = 0d;
		_target = null;
		ClearBreachState();
		EnterState( EnemyAiState.Raiding );
	}

	/// <summary>Raiders only turn on a player who hits them from inside the aggro range — a shot from further out is ignored.</summary>
	bool RaiderIgnoresHitFrom( GameObject attacker ) =>
		Vector3.DistanceBetween( GameObject.WorldPosition, attacker.WorldPosition ) > _raid.AggroRangeUnits;

	void EnterRaidingState()
	{
		EntityCombat?.SetEngaged( false );
		if ( EntityCombat is not { IsMovementLocked: true } )
			EntityCombat?.ResetCycle();
		ApplyAgentSpeed( run: true );
		Locomotion?.SetLookTarget( null );
		Locomotion?.SetPreferAimOverVelocity( false );
		_breachPiece = null;
		_raidPieceDownAt = -1d;
		_raidApproachBed = null;
		_raidLoiterPickedAt = -1000d;
		_nextRaidAggroCheckAt = 0d;
		_raidAggroCandidate = null;
		_raidAggroSince = -1d;
		_raidDropSince = -1d;
		_needsImmediatePathCheck = true;
	}

	void TickRaiding()
	{
		if ( !IsRaider )
			return;

		var now = Time.NowDouble;

		// Finishing the last wall after the raid ended: once it is down (or lost), wander from here.
		if ( _raidFinishingPiece && !Alive( _breachPiece ) && EntityCombat is not { IsMovementLocked: true } )
		{
			_raidFinishingPiece = false;
			LogRaid( "last wall done — wandering" );
			_raid = null;
			WanderFromHere();
			return;
		}

		// Telegraph / swing / recovery: hold feet and yaw, keep the piece as the aim.
		if ( EntityCombat is { IsMovementLocked: true } )
		{
			Agent?.Stop();
			if ( Alive( _breachPiece ) )
				EntityCombat.TickCombat( _breachPiece.GameObject );
			return;
		}

		if ( now >= _nextRaidAggroCheckAt )
		{
			_nextRaidAggroCheckAt = now + RaidAggroCheckSeconds;
			// A finisher is done with the raid's rules — normal perception takes over once it wanders.
			var player = _raidFinishingPiece ? null : FindNearestPlayer( _raid.AggroRangeUnits );
			if ( !player.IsValid() || player != _raidAggroCandidate )
			{
				// Nobody close, or someone new: (re)start the dwell clock.
				_raidAggroCandidate = player.IsValid() ? player : null;
				_raidAggroSince = player.IsValid() ? now : -1d;
			}
			else if ( now - _raidAggroSince >= RaidAggroDwellSeconds )
			{
				LogRaid( $"player {player.Name} within {_raid.AggroRangeUnits:0}u for {RaidAggroDwellSeconds:0}s — turning on them" );
				_target = player;
				RememberLastKnown( player.WorldPosition );
				ClearBreachState();
				EnterState( EnemyAiState.Chasing, player );
				return;
			}
		}

		// Our piece came down: hold while the mesh rebakes — the hole may be the way to the bed.
		if ( _breachPiece is not null && !IsBreachable( _breachPiece ) )
		{
			if ( _raidPieceDownAt < 0d )
				_raidPieceDownAt = now;

			var rebaking = Scene.IsValid() && (BuildNavMeshSync.IsNavStale( Scene ) || BuildNavMeshSync.IsNavGenerating( Scene ));
			if ( rebaking && now - _raidPieceDownAt < BreachNavWaitSeconds )
			{
				Agent?.Stop();
				EntityCombat.SetEngaged( false );
				return;
			}

			BreachClaims.Release( this );
			_breachPiece = null;
			_raidPieceDownAt = -1d;
			_nextRaidPlanAt = 0d;
			_needsImmediatePathCheck = true;
		}

		if ( !Alive( _breachPiece ) )
		{
			var bed = _raid.FindNearestTargetBed( GameObject.WorldPosition );
			if ( bed is null )
			{
				// Every bed is down — the session ends the raid on its next check.
				Agent?.Stop();
				return;
			}

			var flatToBed = Vector3.DistanceBetween( GameObject.WorldPosition.WithZ( 0f ), bed.GameObject.WorldPosition.WithZ( 0f ) );
			// Far out: just run at the bed. A raider whose route ends short of the base (partial
			// path, no progress) plans anyway — the piece between it and the bed is what is in the way.
			if ( flatToBed > RaidPlanRange && !IsRaidApproachBlocked( bed, flatToBed ) )
			{
				RaidMoveTo( GetRaidApproachGoal( bed ), run: true );
				return;
			}

			if ( now >= _nextRaidPlanAt && TryTakeRaidPlanSlot() )
			{
				_nextRaidPlanAt = now + Sandbox.Game.Random.Float( RaidIdleReplanMinSeconds, RaidIdleReplanMaxSeconds );
				if ( TrySelectRaidPiece( bed, out var piece, out var stand ) )
				{
					LogRaid( piece == bed.Piece
						? $"going for the bed {bed.GameObject.Name}"
						: $"bed not reachable — hitting {piece.GameObject.Name} {Vector3.DistanceBetween( piece.GameObject.WorldPosition, bed.GameObject.WorldPosition ):0}u from it" );
					BeginBreachPiece( piece, stand );
				}
			}

			if ( !Alive( _breachPiece ) )
			{
				TickRaidLoiter( bed );
				return;
			}
		}

		var body = GameObject.WorldPosition + Vector3.Up * BodyCenterHeight;
		if ( StrikeDistance( _breachPiece, body ) <= StructureAttackRange )
		{
			Agent?.Stop();
			Locomotion?.SetLookTarget( null );
			Locomotion?.ClearTravelHint();
			EntityCombat.TickCombat( _breachPiece.GameObject );
			return;
		}

		EntityCombat.SetEngaged( false );
		if ( now - _breachStandIssuedAt > BreachStandTimeoutSeconds )
		{
			// Never got there — skip this piece for a while and re-plan.
			_breachRecentlyHit[_breachPiece] = now;
			BreachClaims.Release( this );
			_breachPiece = null;
			_nextRaidPlanAt = 0d;
			return;
		}

		// Something else blocks the walk to the stand — that piece is the one in our way now.
		if ( now >= _nextInWayCheckAt )
		{
			_nextInWayCheckAt = now + InWayRecheckSeconds;
			if ( TryFindPieceInWay( out var blocker, out var blockerStand ) && blocker != _breachPiece
			     && BreachClaims.CanClaim( blocker, this ) )
			{
				BeginBreachPiece( blocker, blockerStand );
				return;
			}
		}

		RaidMoveTo( _breachStand, run: true );
	}

	/// <summary>
	/// What to hit near <paramref name="bed"/>: the bed, the piece on the line to it, then the base's
	/// pieces nearest the bed. Reachability, claims and stands come from the breach selector.
	/// </summary>
	bool TrySelectRaidPiece( BuildBed bed, out BuildPiece piece, out Vector3 stand )
	{
		piece = null;
		stand = default;
		if ( !Scene.IsValid() || Agent is null || !Agent.IsValid() )
			return false;

		PruneRecentlyHit();

		var bedPiece = bed.Piece;
		var bedPos = bed.GameObject.WorldPosition;
		var losPiece = FindPieceBetween( GameObject.WorldPosition, bed.GameObject );

		_breachCandidates.Clear();
		foreach ( var candidate in Scene.GetAllComponents<BuildPiece>() )
		{
			if ( !IsBreachable( candidate ) || _breachRecentlyHit.ContainsKey( candidate ) )
				continue;

			var flatFromBed = Vector3.DistanceBetween( bedPos.WithZ( 0f ), candidate.GameObject.WorldPosition.WithZ( 0f ) );
			// The piece on the line to the bed is always a candidate — a raider held up outside the
			// base radius by an outer wall / fence has to go through it.
			if ( flatFromBed > RaidPieceSearchRadius && candidate != losPiece )
				continue;

			var score = flatFromBed;
			if ( candidate == bedPiece )
				score -= 1_000_000f;
			else if ( candidate == losPiece )
				score -= 500_000f;

			// Stairs / roofs are routes — only hit when nothing else is reachable.
			if ( BuildPieceNavPolicy.GetCategory( candidate.PieceId ) == BuildNavCategory.WalkablePath )
				score += 100_000f;

			_breachCandidates.Add( (candidate, score) );
		}

		// Claimed-out pieces are skipped for free inside TryPickReachable; the stand probes are what
		// cost path queries, so a raid plan tests only the best few.
		_breachCandidates.Sort( ( a, b ) => a.score.CompareTo( b.score ) );
		if ( _breachCandidates.Count > RaidPlanMaxCandidates )
		{
			var open = 0;
			for ( var i = 0; i < _breachCandidates.Count; i++ )
			{
				if ( !BreachClaims.CanClaim( _breachCandidates[i].piece, this ) )
					continue;

				if ( ++open > RaidPlanMaxCandidates )
				{
					_breachCandidates.RemoveRange( i, _breachCandidates.Count - i );
					break;
				}
			}
		}

		return TryPickReachable( _breachCandidates, out piece, out stand );
	}

	/// <summary>Scene-wide plan budget: false when this frame's raid plans are used up (try next frame).</summary>
	static bool TryTakeRaidPlanSlot()
	{
		// Time.Now is constant for the whole frame — a new value means a new frame.
		if ( Time.Now != _raidPlanFrameTime )
		{
			_raidPlanFrameTime = Time.Now;
			_raidPlansThisFrame = 0;
		}

		if ( _raidPlansThisFrame >= MaxRaidPlansPerFrame )
			return false;

		_raidPlansThisFrame++;
		return true;
	}

	/// <summary>Nothing to claim near the bed right now: hang around it (walking) until a slot frees up.</summary>
	void TickRaidLoiter( BuildBed bed )
	{
		var now = Time.NowDouble;
		var anchor = Locomotion?.GetNavAnchorWorld() ?? GameObject.WorldPosition;
		var arrived = Vector3.DistanceBetween( anchor.WithZ( 0f ), _raidLoiterGoal.WithZ( 0f ) ) <= WanderReachDistance;
		if ( now - _raidLoiterPickedAt > RaidLoiterRepickSeconds || (arrived && now - _raidLoiterPickedAt > 2d) )
		{
			_raidLoiterPickedAt = now;
			var dir = Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) ) * Vector3.Forward;
			var point = bed.GameObject.WorldPosition + dir * Sandbox.Game.Random.Float( RaidLoiterMinRadius, RaidLoiterMaxRadius );
			_raidLoiterGoal = ProjectSearchPointToNav( point );
			_needsImmediatePathCheck = true;
		}

		if ( arrived )
		{
			Agent?.Stop();
			Locomotion?.SmoothFaceTowardWorld( bed.GameObject.WorldPosition, AlertTurnDegreesPerSecond );
			return;
		}

		RaidMoveTo( _raidLoiterGoal, run: false );
	}

	/// <summary>Nav point beside the bed (the bed carves the mesh), cached per bed.</summary>
	Vector3 GetRaidApproachGoal( BuildBed bed )
	{
		if ( _raidApproachBed != bed )
		{
			_raidApproachBed = bed;
			_raidApproachGoal = ProjectSearchPointToNav( bed.GameObject.WorldPosition );
			_needsImmediatePathCheck = true;
		}

		return _raidApproachGoal;
	}

	/// <summary>
	/// How far a body point is from where a swing at <paramref name="piece"/> counts: the piece
	/// surface, or for a bed its larger enemy hit zone (<see cref="BuildBed.EnemyHitZoneMeters"/>).
	/// </summary>
	static float StrikeDistance( BuildPiece piece, Vector3 point ) =>
		piece.Components.Get<BuildBed>() is { } bed
			? bed.DistanceToEnemyHitZone( point )
			: BuildPieceGeometry.DistanceToSurface( piece, point );

	/// <summary>AttackInstanceId of the last hit result this brain has already looked at.</summary>
	ushort _bedSwingSeenInstance;

	/// <summary>
	/// End of every attack cycle (host): a swing at a bed from inside its enemy hit zone counts even
	/// when the sweep missed the small model — the bed takes the swing's damage here. A swing that
	/// did connect with the bed is left alone (no double damage).
	/// </summary>
	void LandMissedBedSwing()
	{
		var combat = EntityCombat?.Combat;
		if ( combat is null || !combat.IsValid() )
			return;

		var last = combat.LastMeleeHitResult;
		var freshHit = last.AttackInstanceId != _bedSwingSeenInstance;
		_bedSwingSeenInstance = last.AttackInstanceId;

		if ( _state is not (EnemyAiState.Raiding or EnemyAiState.Breaching) || !Alive( _breachPiece ) )
			return;

		if ( _breachPiece.Components.Get<BuildBed>() is not { } bed || !bed.IsStanding )
			return;

		if ( freshHit && last.TargetId == bed.GameObject.Id )
			return;

		var body = GameObject.WorldPosition + Vector3.Up * BodyCenterHeight;
		if ( bed.DistanceToEnemyHitZone( body ) > StructureAttackRange )
			return;

		var receiver = bed.Components.Get<DamageReceiver>();
		if ( receiver is null )
			return;

		var dealt = receiver.TakeDamage( combat.GetMeleeDamage( false ), combat );
		LogRaid( $"swing landed on the bed's hit zone ({dealt:0.#} dmg)" );
	}

	Vector3 _raidProgressPos;
	double _raidProgressAt = -1d;
	double _nextRaidStallLogAt;
	/// <summary>No progress for this long on a partial route to the bed = blocked; plan a piece.</summary>
	const float RaidApproachBlockedSeconds = 3f;

	/// <summary>
	/// On the approach: the route to the bed is partial / missing and the body has not closed 1 m in
	/// <see cref="RaidApproachBlockedSeconds"/>. Logs (at most every 6 s) what stands between it and the bed.
	/// </summary>
	bool IsRaidApproachBlocked( BuildBed bed, float flatToBed )
	{
		var now = Time.NowDouble;
		if ( _raidProgressAt < 0d || Vector3.DistanceBetween( GameObject.WorldPosition, _raidProgressPos ) > 40f )
		{
			_raidProgressPos = GameObject.WorldPosition;
			_raidProgressAt = now;
			return false;
		}

		var noRoute = LastPathStatus is NavMeshPathStatus.Partial or NavMeshPathStatus.PathNotFound or NavMeshPathStatus.TargetNotFound;
		if ( !noRoute || now - _raidProgressAt < RaidApproachBlockedSeconds )
			return false;

		if ( now >= _nextRaidStallLogAt )
		{
			_nextRaidStallLogAt = now + 6d;
			var between = FindPieceBetween( GameObject.WorldPosition, bed.GameObject );
			LogRaid( $"blocked {flatToBed:0}u from the bed (path={LastPathStatus}) — in the way: {(between is not null ? between.GameObject.Name : "no build piece (terrain / props?)")}" );
		}

		return true;
	}

	void LogRaid( string message )
	{
		if ( NavDebugCommands.TraceEnabled || _raid is { LogRaid: true } )
			Log.Info( $"[Raid] {GameObject.Name}: {message}" );
	}

	/// <summary>Plain nav walk (on foot while the mesh is stale or the agent is not on nav yet).</summary>
	void RaidMoveTo( Vector3 goal, bool run )
	{
		ApplyAgentSpeed( run );
		Locomotion?.SetLookTarget( null );
		Locomotion?.SetTravelHint( goal );

		// A wall coming down rebakes nav somewhere in the base. A raider whose agent is still walking a
		// path keeps walking it — taking every raider off the agent for the rebake and snapping them
		// back after was the "jerking". Only an agent that has actually stopped walks on foot.
		var agentWalking = IsNavAgentReady() && Agent.IsNavigating && _manualChaseSince < 0d;
		if ( Scene.IsValid() && BuildNavMeshSync.IsNavStale( Scene ) && (agentWalking || Locomotion is { IsCoasting: true }) )
			return;

		// Mesh is back but the coast has not handed over yet: skip the on-foot step, run the path check.
		if ( Locomotion is { IsCoasting: true } )
		{
			if ( ShouldRunPathCheck() )
				TryIssueNavMove( goal, 0f );
			return;
		}

		if ( TickManualStepToward( goal, ManualStandArrive ) )
			return;

		if ( !IsNavAgentReady() )
		{
			ManualStepToward( goal, run ? Math.Max( 160f, ChaseMoveSpeed ) : WanderMoveSpeed );
			return;
		}

		if ( !ShouldRunPathCheck() )
			return;

		var issued = TryIssueNavMove( goal, 0f );
		// Partial / failed query: still ask the agent to get as close as it can.
		if ( issued is { HasPath: false } )
			Agent.MoveTo( goal );
	}
}
