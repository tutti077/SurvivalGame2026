using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Navigation;

namespace Survival;

/// <summary>
/// Structure breaching. When the chased player is unreachable because of build pieces — walled
/// in, standing on a floor with no way up, or the entity is wedged against a thin wall the nav
/// mesh leaked through — the entity walks up to a piece and hits it until it is dead (wood dies in
/// three scav hits — material health, not a swing counter), then re-asks where the player is; each
/// kill also cascades through structural integrity. Piece choice, in order: the piece on the line to
/// the player (what they are behind), the piece we are physically pushing against, then the
/// nearest reachable piece biased toward load-bearing ones (higher support = closer to what is
/// holding the weight). Every check here is event-driven or on a ≥1 s cadence — never per frame.
/// </summary>
public sealed partial class EntityBrain
{
	[Property, Group( "Breach" ), Title( "No-route confirmation before breaching (seconds)" ), Description( "Per Mark: breach the moment a fresh mesh has no complete route to the player. This only absorbs a single bad path query." )]
	public float BreachAfterBlockedSeconds { get; set; } = 0.3f;

	[Property, Group( "Breach" ), Title( "Only pieces this close to the player count (units)" ), Description( "Per Mark: hit what stands between us, what is near the player, or what holds the player up — never a structure the player is not in." )]
	public float BreachSearchRadius { get; set; } = 400f;

	[Property, Group( "Breach" ), Title( "Swing reach to a piece surface (units)" ), Description( "Inside the shortest melee class reach (unarmed 1.3 m ≈ 52 u) so every swing connects." )]
	public float StructureAttackRange { get; set; } = 60f;

	[Property, Group( "Breach" ), Title( "Give up walking to a piece after (seconds)" )]
	public float BreachStandTimeoutSeconds { get; set; } = 8f;

	/// <summary>Body-center distance from the piece surface a stand point is placed at (units).</summary>
	const float BreachStandOffset = 40f;
	/// <summary>A piece hit this recently is skipped while any other candidate is reachable.</summary>
	const float BreachRevisitSeconds = 20f;
	/// <summary>How often a breaching entity asks whether the player became reachable.</summary>
	const float BreachRecheckSeconds = 1f;
	/// <summary>A physics clip against a piece counts as "pushing on it" for this long.</summary>
	const float ClipPieceMemorySeconds = 3f;
	/// <summary>
	/// With a COMPLETE path to the player, only a truly wedged agent (not moving at all for this
	/// long) may escalate to breaching — a maze route is walked, however long it takes (per Mark).
	/// </summary>
	const float ChaseWedgedSeconds = 8f;
	/// <summary>Stand-point projection retries per side (nav sampling is random within its radius).</summary>
	const int StandProjectAttempts = 3;
	const int MaxBreachCandidatesEvaluated = 8;
	/// <summary>Band above the feet the swing actually sweeps — a piece must intersect it to be hittable.</summary>
	const float SwingBandBottom = 12f;
	const float SwingBandTop = 90f;
	/// <summary>Melee cannot reach a target more than this far above / below the feet.</summary>
	const float MeleeVerticalReach = 64f;
	/// <summary>A player counts as standing on nav when the projection lands within this of their feet.</summary>
	const float StandingNavTolerance = 48f;
	/// <summary>Body-center height used for reach tests against a piece.</summary>
	const float BodyCenterHeight = 40f;
	/// <summary>How far ahead of the body the in-the-way probe looks (two chase thinks at run speed).</summary>
	const float InWayProbeLength = 72f;
	const float InWayProbeRadius = 12f;
	/// <summary>Cadence of the in-the-way probe while walking to a breach stand.</summary>
	const float InWayRecheckSeconds = 0.3f;
	/// <summary>After our piece goes down, longest we hold for its nav rebake before picking the next piece (a regenerate is ~0.7–6 s).</summary>
	const float BreachNavWaitSeconds = 8f;
	/// <summary>A bake-complete arriving sooner than this after the kill belongs to an earlier change, not to the kill.</summary>
	const float BreachRebakeMinLatency = 0.25f;
	/// <summary>Body-radius arrival distance for an on-foot walk to a breach stand while the mesh is stale.</summary>
	const float ManualStandArrive = 24f;

	static readonly Vector3[] StandAxes =
	{
		new( 1f, 0f, 0f ), new( -1f, 0f, 0f ), new( 0f, 1f, 0f ), new( 0f, -1f, 0f )
	};

	BuildPiece _breachPiece;
	Vector3 _breachStand;
	int _breachSwings;
	double _breachStandIssuedAt;
	double _nextBreachRecheckAt;
	double _chaseBlockedSince;
	/// <summary>Last spot the body actually displaced from — the wedged test for complete paths.</summary>
	Vector3 _chaseProgressPos;
	double _chaseProgressPosAt;
	/// <summary>Set by GetLiveChasePoint: the goal is where the player stands (not the ground under an unreachable player).</summary>
	bool _chaseGoalReachesTarget = true;
	double _nextBreachLogAt;
	double _nextResumeLogAt;
	bool _breachEngageLogged;
	double _nextInWayCheckAt;
	/// <summary>Claim slot on the current piece (0..2) — picks the stand along the face so claimants spread out.</summary>
	int _breachSlot;
	/// <summary>Since when the chase has had no route and no structure to work on (→ return home).</summary>
	double _noStructureSince;
	/// <summary>No route to the player and nothing to breach for this long → walk back to where we started.</summary>
	const float ReturnHomeAfterSeconds = 3f;
	/// <summary>Lateral stand offsets along the piece face per claim slot (units).</summary>
	static readonly float[] StandSlotOffsets = { 0f, 38f, -38f };
	/// <summary>Non-zero while holding after a kill for the nav rebake (then a reachability recheck).</summary>
	double _breachAwaitNavUntil;
	/// <summary>When our breach piece went down — the rebake that matters is the one that starts after this.</summary>
	double _breachPieceDownAt;
	double _manualChaseSince = -1d;
	double _nextTraceAt;
	Vector3 _tracePrevPos;
	double _tracePrevAt;
	BuildPiece _lastClipPiece;
	double _lastClipPieceAt;
	readonly Dictionary<BuildPiece, double> _breachRecentlyHit = new();
	readonly List<(BuildPiece piece, float score)> _breachCandidates = new();
	readonly List<(BuildPiece piece, float score)> _breachRevisitCandidates = new();

	public BuildPiece BreachTarget => _state == EnemyAiState.Breaching && Alive( _breachPiece ) ? _breachPiece : null;

	static bool Alive( BuildPiece piece ) => piece is not null && piece.IsValid();

	void OnAttackCycleFinished()
	{
		if ( _state == EnemyAiState.Breaching )
			_breachSwings++;
	}

	bool IsWithinMeleeVertical( GameObject target ) =>
		target.IsValid() && MathF.Abs( target.WorldPosition.z - GameObject.WorldPosition.z ) <= MeleeVerticalReach;

	void ResetChaseProgress()
	{
		_chaseBlockedSince = 0d;
		_chaseProgressPos = GameObject.WorldPosition;
		_chaseProgressPosAt = Time.NowDouble;
	}

	void LogBreach( string message ) => Log.Info( $"[Breach] {GameObject.Name}: {message}" );

	void ClearBreachState()
	{
		BreachClaims.Release( this );
		_breachPiece = null;
		_noStructureSince = 0d;
		_breachSwings = 0;
		_breachAwaitNavUntil = 0d;
		_breachRecentlyHit.Clear();
		ResetChaseProgress();
	}

	/// <summary>Nav rebaked while breaching: recheck the player now; a pending post-kill hold ends here.</summary>
	void OnBreachNavRebaked()
	{
		_nextBreachRecheckAt = 0d;
		// The mesh now reflects the kill — but only a bake that STARTED after it does. An earlier
		// change's bake finishing right after the kill must not end the hold (per Mark: it must see
		// the wall is gone, then recheck where the player is — not hit the next wall off a stale mesh).
		if ( _breachAwaitNavUntil > 0d && Time.NowDouble - _breachPieceDownAt >= BreachRebakeMinLatency )
			_breachAwaitNavUntil = Time.NowDouble;
	}

	/// <summary>
	/// Hand the body back to the agent where it actually is, and forget the "no progress" clocked
	/// up while the mesh was stale — otherwise the first fresh think reads a stall and breaches a
	/// wall the moment the bake lands. Safe to call when no manual tracking is active.
	/// </summary>
	void EndManualChase()
	{
		if ( _manualChaseSince < 0d )
			return;

		_manualChaseSince = -1d;
		if ( Agent is not null && Agent.IsValid() )
		{
			Agent.SetAgentPosition( GameObject.WorldPosition );
			Agent.UpdatePosition = true;
		}

		ResetChaseProgress();
		_needsImmediatePathCheck = true;
	}

	/// <summary>
	/// Nav is rebuilding (a piece went in or came down): keep running at the player on foot for up to
	/// a second — physics clips still stop us at walls — instead of sprinting in place on a frozen
	/// agent. After that, stand and wait for the mesh. Returns true while it is driving movement.
	/// </summary>
	bool TickManualChaseStep() =>
		_target.IsValid() && TickManualStepToward( _target.WorldPosition, AttackRange * 0.8f );

	/// <summary>
	/// While the mesh is stale, walk straight at <paramref name="goal"/> on foot (physics clips still
	/// stop us at walls). No time cap: spam-placing pieces keeps the mesh stale for as long as the
	/// player likes, and the old 8 s cap left every entity frozen after that (per Mark: "spam place
	/// and eventually they're locked in place"). Returns true while it owns movement.
	/// </summary>
	bool TickManualStepToward( Vector3 goal, float stopWithin )
	{
		// Stale covers the debounce window too: a path through a wall placed a moment ago is not
		// a leak to detour around — that was the "runs off somewhere, turns round, comes back".
		if ( !Scene.IsValid() || !BuildNavMeshSync.IsNavStale( Scene ) )
		{
			EndManualChase();
			return false;
		}

		var now = Time.NowDouble;
		if ( _manualChaseSince < 0d )
		{
			_manualChaseSince = now;
			// The agent keeps writing its own (frozen) position onto the body every frame — with
			// UpdatePosition on, our steps were mostly undone and the scav crept. Take the body.
			if ( Agent is not null && Agent.IsValid() )
			{
				Agent.Stop();
				Agent.UpdatePosition = false;
			}
		}

		var pos = GameObject.WorldPosition;
		var to = (goal - pos).WithZ( 0f );
		var flat = to.Length;
		if ( flat <= stopWithin )
			return true;

		var dir = to / flat;
		var dt = Math.Max( Time.Delta, 1e-4f );
		var next = pos + dir * (Math.Max( 160f, ChaseMoveSpeed ) * dt);

		// The locomotion clip measures movement between fixed updates from an origin the feet-glue
		// resets every frame; this step lands before the glue, so the clip never saw it and the
		// entity walked straight through walls. Sweep the body ourselves: a wall (not stairs / roof,
		// not a pawn) stops the step, sliding along its face.
		var from = pos + Vector3.Up * BodyCenterHeight;
		var trace = Scene.Trace.Ray( from, next + Vector3.Up * BodyCenterHeight )
			.Radius( 14f )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "player" )
			.Run();
		if ( trace.Hit && trace.GameObject.IsValid()
		     && !BuildPieceNavPolicy.IsWalkablePathObject( trace.GameObject )
		     && trace.Normal.z < 0.55f )
		{
			var wall = trace.Normal.WithZ( 0f );
			if ( wall.LengthSquared > 1e-4f )
			{
				wall = wall.Normal;
				var step = next - pos;
				var into = Vector3.Dot( step, wall );
				next = into < 0f ? pos + (step - wall * into) : pos;
				// Recheck the slide so a corner cannot let the tangent through.
				var slide = Scene.Trace.Ray( from, next + Vector3.Up * BodyCenterHeight )
					.Radius( 14f )
					.UsePhysicsWorld()
					.IgnoreGameObjectHierarchy( GameObject )
					.WithoutTags( "player" )
					.Run();
				if ( slide.Hit && slide.GameObject.IsValid() && !BuildPieceNavPolicy.IsWalkablePathObject( slide.GameObject ) && slide.Normal.z < 0.55f )
					next = pos;
			}
			else
			{
				next = pos;
			}

			var blocker = BuildPlacementUtility.FindBuildPieceOnHierarchy( trace.GameObject );
			if ( blocker is not null && blocker.IsValid() )
			{
				_lastClipPiece = blocker;
				_lastClipPieceAt = Time.NowDouble;
			}
		}

		GameObject.WorldPosition = next;
		GameObject.WorldRotation = Rotation.LookAt( dir, Vector3.Up );
		return true;
	}

	/// <summary>
	/// Chasing (think cadence): are build pieces what keeps us from the player? Blocked = the nav
	/// path is missing or partial, or we have stopped moving while still chasing. Once that holds
	/// for <see cref="BreachAfterBlockedSeconds"/> and a piece is reachable, go breach it.
	/// </summary>
	void TickChaseBlockedDetection( float dist )
	{
		var now = Time.NowDouble;
		// Only a fresh mesh may decide this (stale windows are handled by TickManualChaseStep).
		if ( BuildNavMeshSync.IsNavStale( Scene ) )
		{
			_chaseBlockedSince = 0d;
			return;
		}

		// No route = the goal is not where the player stands (off nav), the path is missing /
		// partial, or it runs through a solid. StartNotFound is our own footing, not a wall.
		var noRoute = !_chaseGoalReachesTarget
			|| LastPathCrossesSolid
			|| LastPathStatus is NavMeshPathStatus.PathNotFound
				or NavMeshPathStatus.TargetNotFound
				or NavMeshPathStatus.Partial;

		// Wedged fallback: a complete route but the body has not moved for a while (physically stuck).
		var pos = GameObject.WorldPosition;
		if ( Vector3.DistanceBetween( pos, _chaseProgressPos ) > 30f )
		{
			_chaseProgressPos = pos;
			_chaseProgressPosAt = now;
		}

		var wedged = !noRoute && dist > AttackRange && now - _chaseProgressPosAt >= ChaseWedgedSeconds;
		if ( !noRoute && !wedged )
		{
			_chaseBlockedSince = 0d;
			_noStructureSince = 0d;
			return;
		}

		if ( _chaseBlockedSince <= 0d )
		{
			_chaseBlockedSince = now;
			return;
		}

		if ( now - _chaseBlockedSince < BreachAfterBlockedSeconds )
			return;

		// Retry window either way — no candidate now may become one after the next repath.
		_chaseBlockedSince = now;

		var reason = $"noRoute={noRoute} wedged={wedged} goalAtPlayer={_chaseGoalReachesTarget} status={LastPathStatus} crossesSolid={LastPathCrossesSolid} dist={dist:0}";
		if ( !TrySelectBreachPiece( out var piece, out var stand ) )
		{
			if ( now >= _nextBreachLogAt )
			{
				_nextBreachLogAt = now + 3d;
				LogBreach( $"blocked ({reason}) but no piece relates to the player — candidates={_breachCandidates.Count}" );
			}

			// Nothing structural relates to the player at all (a tree, a rock they climbed) — not
			// merely "every piece already has three claimants". Per Mark: go back where we started.
			var nothingToBreach = _breachCandidates.Count == 0 && _breachRevisitCandidates.Count == 0;
			if ( !nothingToBreach )
			{
				_noStructureSince = 0d;
				return;
			}

			if ( _noStructureSince <= 0d )
				_noStructureSince = now;
			else if ( now - _noStructureSince >= ReturnHomeAfterSeconds )
				ReturnHome( "no route to the player and no structure to breach" );

			return;
		}

		_noStructureSince = 0d;
		LogBreach( $"blocked ({reason}) → breaching {piece.GameObject.Name} stand {Vector3.DistanceBetween( GameObject.WorldPosition, stand ):0}u away" );
		BeginBreachPiece( piece, stand );
		EnterState( EnemyAiState.Breaching, _target );
	}

	void TickBreaching()
	{
		if ( !_target.IsValid() || !IsValidPlayerTarget( _target ) )
			_target = FindNearestPlayer( ChaseAbandonRange );

		var locked = EntityCombat is { IsMovementLocked: true };
		if ( !_target.IsValid() )
		{
			if ( locked )
			{
				Agent?.Stop();
				return;
			}

			DropChaseToIdle();
			return;
		}

		var now = Time.NowDouble;
		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _target.WorldPosition );
		if ( dist > ChaseAbandonRange && !locked )
		{
			DropChaseToIdle();
			return;
		}

		// Breaching IS the hunt continuing behind cover — the unseen clock must not end it.
		_chaseLastSeenAt = now;
		_alertMeter = _perception.AlertThreshold;
		_alertLocked = true;
		ApplyAgentSpeed( run: true );

		// Our piece went down (any time, even mid-recovery): start the nav-rebake hold now, so the
		// bake that follows the kill ends it, instead of the hold starting a second late and the
		// immediate recheck racing a mesh that has already moved on.
		if ( !IsBreachable( _breachPiece ) && _breachAwaitNavUntil <= 0d )
		{
			_breachAwaitNavUntil = now + BreachNavWaitSeconds;
			_breachPieceDownAt = now;
			LogBreach( "piece down — holding for nav rebake" );
		}

		// Telegraph / swing / recovery: hold feet and yaw, keep the piece as the aim.
		if ( locked )
		{
			Agent?.Stop();
			if ( Alive( _breachPiece ) )
				EntityCombat.TickCombat( _breachPiece.GameObject );
			return;
		}

		if ( now >= _nextBreachRecheckAt )
		{
			_nextBreachRecheckAt = now + BreachRecheckSeconds;
			if ( TryResumeChaseFromBreach( dist ) )
				return;

			// The player moved on (jumped to another structure): a piece is only worth hitting while
			// it still stands between us, is near them, or holds them up. Re-pick straight away —
			// not after finishing this wall (per Mark).
			if ( Alive( _breachPiece ) && !IsPieceStillRelevant( _breachPiece ) )
			{
				LogBreach( $"{_breachPiece.GameObject.Name} no longer relates to the player — re-picking" );
				if ( TrySelectBreachPiece( out var next, out var nextStand ) )
				{
					BeginBreachPiece( next, nextStand );
					return;
				}

				EnterState( EnemyAiState.Chasing, _target );
				return;
			}
		}

		if ( !IsBreachable( _breachPiece ) )
		{
			// Our piece is down. The route it opened only exists after the nav rebake (urgent, ~0.2 s)
			// — hold until OnNavBakeComplete has rechecked, else the next piece gets picked off the
			// stale mesh and the chase resumes a whole lap later.
			// Hold while the kill's rebake is pending or still running — never pick the next piece off
			// the stale mesh. The recheck above runs the moment the bake lands.
			if ( now < _breachAwaitNavUntil || BuildNavMeshSync.IsNavGenerating( Scene ) )
			{
				Agent?.Stop();
				EntityCombat.SetEngaged( false );
				return;
			}

			_breachAwaitNavUntil = 0d;
			if ( !AdvanceToNextBreachPiece() )
				return;
		}

		var body = GameObject.WorldPosition + Vector3.Up * BodyCenterHeight;
		if ( BuildPieceGeometry.DistanceToSurface( _breachPiece, body ) <= StructureAttackRange )
		{
			if ( !_breachEngageLogged )
			{
				_breachEngageLogged = true;
				LogBreach( $"in reach of {_breachPiece.GameObject.Name} (hp {_breachPiece.Health:0}/{_breachPiece.MaxHealth:0}) — swinging" );
			}

			Agent?.Stop();
			Locomotion?.SetLookTarget( null );
			Locomotion?.ClearTravelHint();
			EntityCombat.TickCombat( _breachPiece.GameObject );
			return;
		}

		EntityCombat.SetEngaged( false );
		if ( now - _breachStandIssuedAt > BreachStandTimeoutSeconds )
		{
			// Never got there — treat it like a hit piece and pick another instead of pacing forever.
			LogBreach( $"could not reach {_breachPiece.GameObject.Name} in {BreachStandTimeoutSeconds:0}s (nav={LastNavBlockReason} status={LastPathStatus}) — next piece" );
			if ( !AdvanceToNextBreachPiece() )
				return;
		}

		// Something else blocks the walk to the stand — that piece is the one in our way now.
		if ( now >= _nextInWayCheckAt )
		{
			_nextInWayCheckAt = now + InWayRecheckSeconds;
			if ( TryFindPieceInWay( out var blocker, out var blockerStand ) && blocker != _breachPiece )
			{
				LogBreach( $"{blocker.GameObject.Name} blocks the walk to {_breachPiece.GameObject.Name} — hitting it instead" );
				BeginBreachPiece( blocker, blockerStand );
				return;
			}
		}

		// Mesh stale (a piece just went in / came down): walk to the stand on foot instead of
		// standing still until the bake lands.
		if ( TickManualStepToward( _breachStand, ManualStandArrive ) )
			return;

		Locomotion?.SetLookTarget( null );
		Locomotion?.SetTravelHint( _breachStand );
		if ( ShouldRunPathCheck() )
			TryIssueNavMove( _breachStand, 0f, ClosePathCheckInterval );
	}

	/// <summary>Player reachable again (a piece fell, they came out, they are in reach) — resume the hunt proper.</summary>
	bool TryResumeChaseFromBreach( float dist )
	{
		if ( HasGeometricLos( _target ) && dist <= AttackRange && IsWithinMeleeVertical( _target ) )
		{
			LogBreach( "player in reach — attacking" );
			EnterState( EnemyAiState.Attacking, _target );
			return true;
		}

		if ( !Scene.IsValid() || BuildNavMeshSync.IsNavGenerating( Scene ) )
			return false;

		var origin = GetNavOrigin();
		var goal = GetLiveChasePoint( _target );
		// The ground under a player on a floor we cannot reach is not "reachable" — keep breaching.
		if ( !_chaseGoalReachesTarget )
		{
			LogResumeFailure( $"player is not standing on nav (feet {_target.WorldPosition}, on {DescribeGroundUnder( _target )})" );
			return false;
		}

		var query = EntityChaseRouting.QueryPath( Scene, origin, goal, Agent, NavProjectTier.Fast );
		if ( !query.HasPath || query.Status != NavMeshPathStatus.Complete )
		{
			LogResumeFailure( $"no complete path to player: status={query.Status} points={query.Points?.Count ?? 0} goal={goal}" );
			return false;
		}

		// Physics is the judge, not path length: the route through a hole we just made is short and
		// still occluded from here, which the "shortcut" heuristic wrongly rejected for a whole lap.
		if ( PathCrossesSolid( query ) )
		{
			LogResumeFailure( $"path ({query.Length:0}u) crosses a solid" );
			return false;
		}

		LogBreach( $"player reachable again (path {query.Length:0}u) — chasing" );
		EnterState( EnemyAiState.Chasing, _target );
		return true;
	}

	/// <summary>What a pawn stands on, for the breach log: object, piece category, surface normal.</summary>
	string DescribeGroundUnder( GameObject pawn )
	{
		var feet = pawn.WorldPosition;
		var trace = Scene.Trace.Ray( feet + Vector3.Up * 8f, feet - Vector3.Up * 128f )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( pawn )
			.Run();
		if ( !trace.Hit || !trace.GameObject.IsValid() )
			return "nothing";

		var piece = BuildPlacementUtility.FindBuildPieceOnHierarchy( trace.GameObject );
		var note = piece is not null ? $" {BuildPieceNavPolicy.GetCategory( piece.PieceId )}" : string.Empty;
		return $"{trace.GameObject.Name}{note} normal.z={trace.Normal.z:0.00}";
	}

	void LogResumeFailure( string why )
	{
		if ( Time.NowDouble < _nextResumeLogAt )
			return;

		_nextResumeLogAt = Time.NowDouble + 2d;
		LogBreach( $"still breaching — {why}" );
	}

	/// <summary>Current piece is done with (3 swings / destroyed / unreachable) — pick the next, or fall back to chasing.</summary>
	bool AdvanceToNextBreachPiece()
	{
		var previous = Alive( _breachPiece ) ? _breachPiece.GameObject.Name : "(destroyed)";
		if ( Alive( _breachPiece ) )
			_breachRecentlyHit[_breachPiece] = Time.NowDouble;

		if ( TrySelectBreachPiece( out var piece, out var stand ) )
		{
			LogBreach( $"done with {previous} after {_breachSwings} swing(s) → next {piece.GameObject.Name} stand {Vector3.DistanceBetween( GameObject.WorldPosition, stand ):0}u away" );
			BeginBreachPiece( piece, stand );
			return true;
		}

		// Nothing hittable from here — chase again; blocked detection brings us back if needed.
		LogBreach( $"done with {previous} after {_breachSwings} swing(s), no reachable piece left — chasing" );
		EnterState( EnemyAiState.Chasing, _target );
		return false;
	}

	void BeginBreachPiece( BuildPiece piece, Vector3 stand )
	{
		BreachClaims.Release( this );
		_breachPiece = piece;
		BreachClaims.TryClaim( piece, this, out _breachSlot );
		_breachStand = stand;
		_breachSwings = 0;
		_breachEngageLogged = false;
		_breachAwaitNavUntil = 0d;
		_breachStandIssuedAt = Time.NowDouble;
		_needsImmediatePathCheck = true;
		_issuedNavGoal = default;
		EntityCombat?.SetEngaged( false );
		if ( EntityCombat is not { IsMovementLocked: true } )
			EntityCombat?.ResetCycle();
	}

	// ── Trace ──────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// `nav_trace 1`: one line per entity every 0.5 s with everything that decides movement —
	/// state, real speed vs wish vs MaxSpeed, agent flags, mesh staleness, manual tracking,
	/// last path status / block reason, distance to the target. For reading the log after a run.
	/// </summary>
	void TickMovementTrace()
	{
		if ( !NavDebugCommands.TraceEnabled )
			return;

		var now = Time.NowDouble;
		if ( now < _nextTraceAt )
			return;

		var pos = GameObject.WorldPosition;
		var dt = _tracePrevAt > 0d ? now - _tracePrevAt : 0d;
		var realSpeed = dt > 1e-3 ? Vector3.DistanceBetween( pos.WithZ( 0f ), _tracePrevPos.WithZ( 0f ) ) / (float)dt : 0f;
		_tracePrevPos = pos;
		_tracePrevAt = now;
		_nextTraceAt = now + 0.5d;

		var agentOk = Agent is not null && Agent.IsValid();
		var wish = agentOk ? Agent.WishVelocity.WithZ( 0f ).Length : 0f;
		var maxSpeed = agentOk ? Agent.MaxSpeed : 0f;
		var navigating = agentOk && Agent.IsNavigating;
		var updatePos = agentOk && Agent.UpdatePosition;
		var dist = _target.IsValid() ? Vector3.DistanceBetween( pos, _target.WorldPosition ) : -1f;
		var stale = Scene.IsValid() && BuildNavMeshSync.IsNavStale( Scene );
		var generating = Scene.IsValid() && BuildNavMeshSync.IsNavGenerating( Scene );
		var locked = EntityCombat is { IsMovementLocked: true };
		var piece = Alive( _breachPiece ) ? _breachPiece.GameObject.Name : "-";
		var agentOff = agentOk ? Vector3.DistanceBetween( Agent.AgentPosition, pos ) : -1f;
		var targetOff = agentOk && Agent.TargetPosition.HasValue ? Vector3.DistanceBetween( Agent.TargetPosition.Value, _issuedNavGoal ) : -1f;
		var bodyOnNav = Scene.IsValid() && EntityNavMeshUtility.TryFindNavAtFeet( Scene, pos, out _, horizontal: 24f, vertical: 48f );

		Log.Info( $"[Trace] {GameObject.Name} {_state} pos={pos:0} dist={dist:0} real={realSpeed:0} wish={wish:0} max={maxSpeed:0} nav={navigating} updPos={updatePos} agentOff={agentOff:0.#} targetOff={targetOff:0} bodyOnNav={bodyOnNav} manual={_manualChaseSince >= 0d} stale={stale} gen={generating} locked={locked} path={LastPathStatus} solid={LastPathCrossesSolid} goalAtPlayer={_chaseGoalReachesTarget} block={LastNavBlockReason} piece={piece}" );
	}

	// ── In the way ─────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Chasing (think cadence): a build piece directly ahead along the way we are moving is
	/// remembered as the preferred breach target. It never breaches by itself — only "no complete
	/// route to the player" does (per Mark).
	/// </summary>
	void RememberPieceInWay()
	{
		if ( !TryFindPieceInWay( out var piece, out _ ) )
			return;

		_lastClipPiece = piece;
		_lastClipPieceAt = Time.NowDouble;
	}

	/// <summary>
	/// Short body-height sphere probe along the movement direction (nav wish, else facing) for a
	/// destructible piece within swing height. A doorway is a gap, so a path through it never
	/// trips this; a wall the path leaks through does.
	/// </summary>
	bool TryFindPieceInWay( out BuildPiece piece, out Vector3 stand )
	{
		piece = null;
		stand = GameObject.WorldPosition;
		if ( !Scene.IsValid() )
			return false;

		var dir = Agent is not null && Agent.IsValid() ? Agent.WishVelocity.WithZ( 0f ) : Vector3.Zero;
		if ( dir.LengthSquared < 64f )
			dir = GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( dir.LengthSquared < 1e-4f )
			return false;

		dir = dir.Normal;
		var start = GameObject.WorldPosition + Vector3.Up * BodyCenterHeight;
		var trace = Scene.Trace.Ray( start, start + dir * InWayProbeLength )
			.Radius( InWayProbeRadius )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !trace.Hit || !trace.GameObject.IsValid() )
			return false;

		var hit = BuildPlacementUtility.FindBuildPieceOnHierarchy( trace.GameObject );
		if ( !IsBreachable( hit ) || !IsInSwingBand( hit, GameObject.WorldPosition.z ) )
			return false;

		// Stairs / roofs are walked up, never breached from the probe (a riser or a roof edge reads as
		// a wall here). They can still be picked by stall-based selection when there is no way up.
		if ( BuildPieceNavPolicy.GetCategory( hit.PieceId ) == BuildNavCategory.WalkablePath )
			return false;

		// Floors / ramps we are walking onto are not "in the way".
		if ( !trace.StartedSolid && trace.Normal.z > 0.55f )
			return false;

		piece = hit;
		// Stand a body-width short of the face — already inside swing range, never inside the wall.
		stand = GameObject.WorldPosition + dir * MathF.Max( 0f, trace.Distance - BreachStandOffset );
		return true;
	}

	/// <summary>Same test the selector applies: on the line to the player, or within the player radius.</summary>
	bool IsPieceStillRelevant( BuildPiece piece )
	{
		if ( !_target.IsValid() )
			return false;

		if ( FindPieceBetween( GameObject.WorldPosition, _target ) == piece )
			return true;

		var standingPiece = FindPieceUnder( _target );
		if ( standingPiece is not null )
		{
			var connected = BuildStructuralIntegrity.GetConnectedPieces( standingPiece );
			if ( connected.Count > 0 )
				return connected.Contains( piece );
		}

		var flatFromPlayer = Vector3.DistanceBetween( _target.WorldPosition.WithZ( 0f ), piece.GameObject.WorldPosition.WithZ( 0f ) );
		return flatFromPlayer <= BreachSearchRadius;
	}

	// ── Piece selection ────────────────────────────────────────────────────────────────────

	static bool IsBreachable( BuildPiece piece ) =>
		piece is not null && piece.IsValid() && piece.Enabled && piece.GameObject.IsValid()
		&& !piece.IsPreviewGhost && piece.IsDestructible && !piece.IsBroken;

	bool TrySelectBreachPiece( out BuildPiece piece, out Vector3 stand )
	{
		piece = null;
		stand = default;
		if ( !Scene.IsValid() || !_target.IsValid() || Agent is null || !Agent.IsValid() )
			return false;

		PruneRecentlyHit();

		var self = GameObject.WorldPosition;
		var playerPos = _target.WorldPosition;
		var selfToPlayer = Vector3.DistanceBetween( self.WithZ( 0f ), playerPos.WithZ( 0f ) );
		var playerAbove = playerPos.z - self.z > MeleeVerticalReach;
		var losPiece = FindPieceBetween( self, _target );
		var clipPiece = Alive( _lastClipPiece ) && Time.NowDouble - _lastClipPieceAt <= ClipPieceMemorySeconds
			? _lastClipPiece
			: null;

		// The structure the player is standing on: its connected pieces are the ones holding them
		// up. A separate structure a few metres away — even inside the player radius — is not.
		var standingPiece = FindPieceUnder( _target );
		var connected = standingPiece is not null
			? BuildStructuralIntegrity.GetConnectedPieces( standingPiece )
			: null;

		_breachCandidates.Clear();
		_breachRevisitCandidates.Clear();
		foreach ( var candidate in Scene.GetAllComponents<BuildPiece>() )
		{
			if ( !IsBreachable( candidate ) )
				continue;

			var center = candidate.GameObject.WorldPosition;
			var flatFromPlayer = Vector3.DistanceBetween( playerPos.WithZ( 0f ), center.WithZ( 0f ) );

			// Per Mark: only pieces that relate to the PLAYER — on the line between us, the piece
			// we are being held by on the way to them, part of the structure they stand on, or near
			// them when they are on the ground. A piece that is merely near the entity (the other
			// half of a split base, the house next to the tree the player grappled into) is never a
			// target, and neither is an unconnected structure beside the one they are on.
			var between = candidate == losPiece
				|| (candidate == clipPiece && flatFromPlayer < selfToPlayer);
			if ( !between )
			{
				if ( connected is { Count: > 0 } )
				{
					if ( !connected.Contains( candidate ) )
						continue;
				}
				else if ( flatFromPlayer > BreachSearchRadius )
				{
					continue;
				}
			}

			if ( !IsInSwingBand( candidate, self.z ) )
				continue;

			// Closest to the player first, pulled toward load-bearing pieces (100 support ≈ 7.5 m).
			var score = flatFromPlayer - candidate.Support * 3f;
			if ( candidate == losPiece )
				score -= 1_000_000f;
			else if ( candidate == clipPiece && between )
				score -= 500_000f;

			// Player up on a structure: what is under their feet and holding it up comes first.
			if ( playerAbove && center.z < playerPos.z && flatFromPlayer <= 150f )
				score -= 250_000f;

			// Stairs / roofs are routes (per Mark) — only ever hit when nothing else is reachable.
			if ( BuildPieceNavPolicy.GetCategory( candidate.PieceId ) == BuildNavCategory.WalkablePath )
				score += 100_000f;

			if ( _breachRecentlyHit.ContainsKey( candidate ) )
				_breachRevisitCandidates.Add( (candidate, score) );
			else
				_breachCandidates.Add( (candidate, score) );
		}

		if ( TryPickReachable( _breachCandidates, out piece, out stand ) )
			return true;

		// Everything nearby has had its three swings — start the next lap.
		return TryPickReachable( _breachRevisitCandidates, out piece, out stand );
	}

	bool TryPickReachable( List<(BuildPiece piece, float score)> list, out BuildPiece piece, out Vector3 stand )
	{
		piece = null;
		stand = default;
		if ( list.Count == 0 )
			return false;

		list.Sort( ( a, b ) => a.score.CompareTo( b.score ) );
		var evaluated = 0;
		foreach ( var entry in list )
		{
			if ( evaluated++ >= MaxBreachCandidatesEvaluated )
				break;

			// At most three entities on one piece — the rest take another piece of the structure.
			if ( !BreachClaims.CanClaim( entry.piece, this ) )
				continue;

			if ( !TryFindBreachStand( entry.piece, out stand ) )
				continue;

			piece = entry.piece;
			return true;
		}

		return false;
	}

	void PruneRecentlyHit()
	{
		if ( _breachRecentlyHit.Count == 0 )
			return;

		var now = Time.NowDouble;
		List<BuildPiece> stale = null;
		foreach ( var entry in _breachRecentlyHit )
		{
			if ( entry.Key.IsValid() && now - entry.Value < BreachRevisitSeconds )
				continue;

			stale ??= new List<BuildPiece>();
			stale.Add( entry.Key );
		}

		if ( stale is null )
			return;

		foreach ( var piece in stale )
			_breachRecentlyHit.Remove( piece );
	}

	bool IsInSwingBand( BuildPiece piece, float feetZ )
	{
		var bounds = BuildPieceGeometry.WorldBounds( piece );
		return bounds.Maxs.z >= feetZ + SwingBandBottom && bounds.Mins.z <= feetZ + SwingBandTop;
	}

	/// <summary>Registry view of the piece this entity is on — independent of state so a claim made just before EnterState(Breaching) is not pruned.</summary>
	internal BuildPiece ClaimedPiece => Alive( _breachPiece ) ? _breachPiece : null;

	/// <summary>Slot this entity would get on the piece (its existing one, else the next free).</summary>
	int PeekSlot( BuildPiece piece ) => piece == _breachPiece ? _breachSlot : BreachClaims.NextSlot( piece, this );

	/// <summary>Per Mark: no route and nothing to breach → walk back to where this entity started, then idle.</summary>
	void ReturnHome( string why )
	{
		LogBreach( $"{why} — returning home {HomePosition}" );
		_alertMeter = 0f;
		_alertLocked = false;
		_hasSearchGoal = false;
		_chaseLastSeenAt = 0d;
		_target = null;
		ClearBreachState();
		EnterState( EnemyAiState.Wander );
		_wanderGoal = HomePosition;
		_wanderStuckSince = 0d;
		_needsImmediatePathCheck = true;
		TryIssueNavMove( _wanderGoal, 0f );
		if ( IsNavAgentReady() )
			Agent.MoveTo( _wanderGoal );
	}

	/// <summary>The build piece directly under a pawn's feet, if they stand on one.</summary>
	BuildPiece FindPieceUnder( GameObject pawn )
	{
		var feet = pawn.WorldPosition;
		var trace = Scene.Trace.Ray( feet + Vector3.Up * 8f, feet - Vector3.Up * 96f )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( pawn )
			.Run();
		if ( !trace.Hit || !trace.GameObject.IsValid() )
			return null;

		var piece = BuildPlacementUtility.FindBuildPieceOnHierarchy( trace.GameObject );
		return piece is not null && piece.IsValid() && !piece.IsPreviewGhost ? piece : null;
	}

	/// <summary>The first build piece on the straight line from our eyes to the player — what they are behind.</summary>
	BuildPiece FindPieceBetween( Vector3 self, GameObject target )
	{
		var eye = self + Vector3.Up * _perception.EyeHeight;
		var bounds = target.GetBounds();
		var aim = bounds.Size.LengthSquared > 1f ? bounds.Center : target.WorldPosition + Vector3.Up * BodyCenterHeight;
		var trace = Scene.Trace.Ray( eye, aim )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !trace.Hit || !trace.GameObject.IsValid() )
			return null;

		var piece = BuildPlacementUtility.FindBuildPieceOnHierarchy( trace.GameObject );
		return IsBreachable( piece ) ? piece : null;
	}

	/// <summary>
	/// A nav point beside the piece we can path to and swing from: one probe per horizontal face of
	/// the piece's oriented solid, projected to nav, kept only when it is still inside swing range
	/// of the surface and the path there is complete. Shortest path wins.
	/// </summary>
	bool TryFindBreachStand( BuildPiece piece, out Vector3 stand )
	{
		stand = default;
		var origin = GetNavOrigin();
		var body = GameObject.WorldPosition + Vector3.Up * BodyCenterHeight;
		if ( BuildPieceGeometry.DistanceToSurface( piece, body ) <= StructureAttackRange * 0.9f )
		{
			stand = origin;
			return true;
		}

		var rotation = BuildColliderSnap.GetSnapWorldRotation( piece.GameObject, piece.PieceId );
		var half = BuildColliderSnap.GetColliderHalfForPiece( piece.PieceId );
		var center = piece.GameObject.WorldPosition;
		var floorZ = BuildPieceGeometry.WorldBounds( piece ).Mins.z + 4f;
		var bestLength = float.MaxValue;
		var found = false;

		foreach ( var axis in StandAxes )
		{
			var extent = MathF.Abs( axis.x ) > 0.5f ? half.x : half.y;
			var dir = rotation * axis;
			// Pitched piece: this face points up/down — nothing stands "beside" it.
			if ( MathF.Abs( dir.z ) > 0.7f )
				continue;

			// Claimants spread along the face by slot so three entities do not stack on one spot
			// and shove each other through the wall.
			var tangent = Vector3.Cross( dir, Vector3.Up ).Normal;
			var lateral = StandSlotOffsets[Math.Clamp( PeekSlot( piece ), 0, StandSlotOffsets.Length - 1 )];
			var probe = (center + dir * (extent + BreachStandOffset) + tangent * lateral).WithZ( floorZ );
			for ( var attempt = 0; attempt < StandProjectAttempts; attempt++ )
			{
				if ( !EntityNavMeshUtility.TryProjectToNavMesh( Scene, probe, out var onNav, NavProjectTier.Fast, maxRadius: 96f ) )
					break;

				if ( BuildPieceGeometry.DistanceToSurface( piece, onNav + Vector3.Up * BodyCenterHeight ) > StructureAttackRange )
					continue;

				var query = EntityChaseRouting.QueryPath( Scene, origin, onNav, Agent, NavProjectTier.Fast );
				if ( !query.HasPath || query.Status != NavMeshPathStatus.Complete )
					break;

				// A "path" that cuts through the piece itself (nav not yet carved) is the far side — skip it.
				if ( PathCrossesSolid( query ) )
					break;

				if ( query.Length < bestLength )
				{
					bestLength = query.Length;
					stand = onNav;
					found = true;
				}

				break;
			}
		}

		return found;
	}
}
