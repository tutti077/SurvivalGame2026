using System;
using Sandbox;
using Sandbox.Citizen;

namespace Survival;

/// <summary>Drives citizen walk/run anims, build wall clips, and gravity falls when support is lost.</summary>
[Title( "Entity Locomotion" )]
public sealed class EntityLocomotion : Component
{
	const float BodyTraceRadius = 14f;
	const float BodyTraceHeight = 48f;
	/// <summary>Clearance kept between the body sphere and a wall after a clip — the next trace must start outside the solid.</summary>
	const float ClipSurfaceGap = 1f;
	const float EntityStandingHeight = 72f;
	const float FallDamageHeightMultiplier = 5f;
	const float FeetTraceLift = 8f;
	/// <summary>
	/// Support probes start this far above the feet so the ray begins ABOVE the next stair riser.
	/// The old feet+8 start was inside any step taller than 8 u (StartedSolid), which read as
	/// "never climb that" — entities could not walk up build stairs at all.
	/// </summary>
	const float StepProbeLift = 48f;
	/// <summary>Tallest single step the feet glue may climb in one probe (mirrors nav AgentStepSize 40).</summary>
	const float MaxStepUpUnits = 44f;
	const float SupportTraceDepth = 128f;
	const float FallTraceDepth = 512f;
	const float MaxStandGap = 32f;
	const float LandFeetOffset = 0f;
	/// <summary>Soft chase toward heightfield (low = smooth slopes, high = snappy).</summary>
	const float GroundFollowRate = 4.5f;
	/// <summary>Settle flush once this close — larger values left feet hovering.</summary>
	const float GroundStickDeadzone = 0.35f;
	/// <summary>Max upward stick speed (units/sec) — stops “shoot up” on ridges.</summary>
	const float MaxGroundClimbSpeed = 55f;
	/// <summary>Max downward stick speed (units/sec) — a bit faster so they don’t hover.</summary>
	const float MaxGroundDropSpeed = 110f;
	/// <summary>Hard agent resync only after teleports / load (SetAgentPosition hitches steps).</summary>
	const float AgentZResyncUnits = 96f;
	const float Gravity = 800f;
	const float MaxFallSpeed = 1200f;
	const float DefaultBodyTurnDegreesPerSecond = 303.75f;
	const float AimReassessSeconds = 2.75f;
	/// <summary>~40° — below this, speed blends down so corners can be taken without wall-rams.</summary>
	const float DefaultMoveAlignMinDot = 0.76f;
	/// <summary>Below this alignment, stop and turn (no reverse/side strafe).</summary>
	const float MoveAlignStopDot = 0.25f;

	[Property] public NavMeshAgent Agent { get; set; }
	[Property] public EntityVitals Vitals { get; set; }
	[Property] public SkinnedModelRenderer Body { get; set; }
	[Property] public CitizenAnimationHelper AnimHelper { get; set; }

	[Property, Group( "Fall" ), Title( "Fall damage starts above (units)" )]
	public float FallDamageMinHeight { get; set; } = EntityStandingHeight * FallDamageHeightMultiplier;

	[Property, Group( "Aim" ), Title( "Body turn speed (degrees / second)" )]
	public float TurnDegreesPerSecond { get; set; } = DefaultBodyTurnDegreesPerSecond;

	[Property, Group( "Aim" ), Title( "Reassess look aim (seconds)" )]
	public float AimReassessInterval { get; set; } = AimReassessSeconds;

	[Property, Group( "Move" ), Title( "Forward-only run (turn then go)" ), Description( "Stop and rotate to face the path, then run straight. No sideways strafe." )]
	public bool ForwardOnlyNavigation { get; set; } = true;

	[Property, Group( "Move" ), Title( "Run align min (dot)" ), Range( 0.5f, 0.99f ), Step( 0.01f ), Description( "Full run speed above this facing-vs-path dot. Below it, creep while turning so corners work." )]
	public float MoveAlignMinDot { get; set; } = DefaultMoveAlignMinDot;

	Vector3 _lastPosition;
	Vector3 _clipFromPosition;

	// ── Visual hop smoothing ──────────────────────────────────────────────────────────────
	/// <summary>A root move this much larger than a frame of running is a hop, not a step (units). 4 u: a scav runs ~4 u a frame at 60 fps, so a 10 u shift is still caught (Mark: still "jittering" with only ≥20 u hops smoothed).</summary>
	const float VisualHopSlackUnits = 4f;
	/// <summary>Fastest the root legitimately moves (units/s) — run speed with margin (chase 220).</summary>
	const float VisualHopMaxSpeed = 300f;
	/// <summary>Hops longer than this are real teleports (spawn placement, respawn) and stay instant.</summary>
	const float VisualHopMaxUnits = 320f;
	/// <summary>How fast the model eases onto the root after a hop (1/s — ~0.2 s to settle).</summary>
	const float VisualHopDecayRate = 12f;
	Vector3 _visualOffsetWorld;
	Vector3 _visualPrevRoot;
	bool _hasVisualPrevRoot;
	Vector3 _bodyBaseLocal;
	bool _bodyBaseCaptured;
	double _nextHopLogAt;
	TerrainWorldManager _cachedTerrain;
	float _smoothedGroundZ;
	bool _hasSmoothedGroundZ;
	/// <summary>Feet are on a build piece (stairs / roof / floor) — follow its height at full speed, not the terrain ease.</summary>
	bool _supportOnBuildPiece;
	/// <summary>Climb / drop rate while on build pieces: a 45° roof at run speed needs ~220 u/s vertical.</summary>
	const float PieceGroundFollowSpeed = 600f;
	Vector3 _airVelocity;
	Vector3 _pendingAirCarry;
	double _pendingAirCarryUntil;
	Vector3 _frozenAimWorld;
	GameObject _lookTarget;
	bool _isFalling;
	bool _hasFrozenAim;
	bool _preferAimOverVelocity;
	bool _brainOwnsFacing;
	bool _hasTravelHint;
	Vector3 _travelHintWorld;
	Vector3 _lastWishDir;
	double _lastWishAt;
	float _lastWishSpeed;

	// ── Coast through a nav rebake ────────────────────────────────────────────────────────
	/// <summary>Longest a coast may carry the body before it simply holds (the rebake is taking too long).</summary>
	const float CoastMaxSeconds = 1.5f;
	/// <summary>A wish this recent counts as "was moving" when the mesh goes stale.</summary>
	const float CoastRecentWishSeconds = 0.3f;
	bool _coasting;
	bool _coastResumeIssued;

	// ── Body follows the agent (never the engine writing the transform) ───────────────────
	/// <summary>
	/// Per Mark (2026-09-23, "definitely related to the walls and the renav"): every tile rebuild
	/// re-seats each agent on the new polygons and, with <see cref="NavMeshAgent.UpdatePosition"/>
	/// on, the engine wrote that seat straight onto the body — 10–16 u, every scav in the area, on
	/// the frame the tile came back. There is no incremental nav update on this engine, so instead
	/// the engine never drives the transform: any <c>UpdatePosition = true</c> (spawn placement,
	/// bake-complete, landing, manual-chase handoff) is taken as "attach" and turned off, and each
	/// fixed update the body walks toward the agent at a capped speed — a normal path is followed
	/// exactly as before, and a re-seat becomes a short slide instead of a snap.
	/// </summary>
	bool _agentAttached;
	/// <summary>Headroom over the intended run speed for catching a re-seated agent (units/s).</summary>
	const float FollowCatchUpSpeed = 120f;
	/// <summary>
	/// Agent this far ahead of the body (horizontal, units) = the body is held by something the mesh
	/// does not know about (a wall nav leaks through): pull the agent back and re-path, instead of the
	/// body running in place against the wall while the agent walks off (Mark: "running against the wall").
	/// </summary>
	const float FollowBlockedLagUnits = 64f;

	/// <summary>The body is following its nav agent (the engine's UpdatePosition is always off).</summary>
	public bool AgentDrivesBody => _agentAttached;

	/// <summary>Brain takes the body (manual stepping, benching): stop following the agent.</summary>
	public void DetachAgent()
	{
		_agentAttached = false;
		if ( Agent is not null && Agent.IsValid() )
			Agent.UpdatePosition = false;
	}

	/// <summary>Any external UpdatePosition = true becomes an attach; the engine flag itself stays off.</summary>
	void SyncAgentAttachment()
	{
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() || !Agent.UpdatePosition )
			return;

		Agent.UpdatePosition = false;
		_agentAttached = true;
	}

	/// <summary>
	/// Walk the body toward the agent's position (XY; Z is the ground glue's) at a capped speed —
	/// only while the agent is actually walking a path. An idle agent (standing, mid-swing) gets
	/// re-seated 12–18 u by every tile rebuild; following it then slid every standing scav across the
	/// base on each rebake (Mark's log: "STALL 244→0 … locked=True" after every tile request). The
	/// brain puts the agent back at the body before its next MoveTo instead.
	/// </summary>
	void FollowAgent()
	{
		if ( !_agentAttached || Agent is null || !Agent.IsValid() || !Agent.Enabled || !Agent.IsNavigating )
			return;

		var body = GameObject.WorldPosition;
		var delta = (Agent.AgentPosition - body).WithZ( 0f );
		var dist = delta.Length;
		if ( dist < 0.01f )
			return;

		if ( dist > FollowBlockedLagUnits )
		{
			Agent.SetAgentPosition( body );
			Components.Get<EntityBrain>()?.RequestImmediateRepath();
			return;
		}

		var dt = Math.Max( Time.Delta, 1e-4f );
		var maxStep = (Math.Max( _intendedMaxSpeed, 160f ) + FollowCatchUpSpeed) * dt;
		var step = Math.Min( dist, maxStep );
		GameObject.WorldPosition = body + delta * (step / dist);
	}
	double _coastSince;
	Vector3 _coastVelocity;

	/// <summary>
	/// The mesh is being rebuilt and locomotion is carrying the body on its last velocity — the
	/// brain must not step the body itself meanwhile (see <see cref="EntityBrain"/> manual stepping).
	/// </summary>
	public bool IsCoasting => _coasting;

	/// <summary>
	/// Coasting while the mesh is still stale: the brain has nothing to path on yet. Once the mesh is
	/// back the brain must path again (the coast ends when the agent is walking) — blocking path
	/// checks for the whole coast left walkers running blind for the 2.5 s cap, then stopping dead.
	/// </summary>
	public bool IsCoastingOnStaleNav => _coasting && Scene.IsValid() && BuildNavMeshSync.IsNavStale( Scene );
	float _fallSpeed;
	float _fallStartZ;
	float _intendedMaxSpeed = 220f;
	double _nextAimReassessAt;
	double _nextClipNotifyAt;

	public bool IsAirborne => _isFalling;
	public bool IsSpawnSettling => false;

	/// <summary>Held by a <see cref="BearTrap"/> — brains stop driving and the feet are pinned where they were caught.</summary>
	public bool IsTrapped { get; private set; }

	Vector3 _trappedAnchor;

	public event Action Landed;

	/// <summary>
	/// Host: pin / free the body. The trap owns the timer; the brains read <see cref="IsTrapped"/>.
	/// <paramref name="anchorWorld"/> is the trap centre — the feet are pulled there (XY) and held.
	/// </summary>
	public void HostSetTrapped( bool trapped, Vector3 anchorWorld = default )
	{
		if ( IsTrapped == trapped )
			return;

		IsTrapped = trapped;
		if ( !trapped )
			return;

		var pos = GameObject.WorldPosition;
		_trappedAnchor = new Vector3( anchorWorld.x, anchorWorld.y, pos.z );
		GameObject.WorldPosition = _trappedAnchor;

		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is not null && Agent.IsValid() )
		{
			Agent.Stop();
			// The agent must agree on where the body is, or UpdatePosition drags it back off the plate.
			Agent.SetAgentPosition( _trappedAnchor );
		}
	}

	public void SetLookTarget( GameObject target )
	{
		_lookTarget = target;
		if ( !target.IsValid() )
		{
			_hasFrozenAim = false;
			return;
		}

		// First acquire stores aim point; body turns toward it at TurnDegreesPerSecond.
		if ( !_hasFrozenAim )
		{
			_frozenAimWorld = target.WorldPosition;
			_hasFrozenAim = true;
			_nextAimReassessAt = Time.NowDouble + Math.Max( 0.5f, AimReassessInterval );
		}
	}

	/// <summary>
	/// When true, body keeps facing the frozen/search aim even while pathing (so a noise behind
	/// still causes an about-face instead of only facing walk velocity).
	/// </summary>
	public void SetPreferAimOverVelocity( bool prefer ) => _preferAimOverVelocity = prefer;

	/// <summary>When true, locomotion does not write WorldRotation — brain turns the body.</summary>
	public void SetBrainOwnsFacing( bool owns ) => _brainOwnsFacing = owns;

	public void SetFrozenAimWorld( Vector3 worldPos )
	{
		_frozenAimWorld = worldPos;
		_hasFrozenAim = true;
		_nextAimReassessAt = Time.NowDouble + Math.Max( 0.5f, AimReassessInterval );
	}

	/// <summary>Wander/chase goal so forward-only can face something when WishVelocity is dead.</summary>
	public void SetTravelHint( Vector3 worldPos )
	{
		_travelHintWorld = worldPos;
		_hasTravelHint = true;
	}

	public void ClearTravelHint() => _hasTravelHint = false;

	/// <summary>
	/// Seed the horizontal carry for the next fall. When the brain walks the root off an edge on
	/// purpose (drop-in attacks) the agent is stopped, so <see cref="BeginFall"/> would read a dead
	/// wish and drop the body straight down at the lip instead of out over the target.
	/// </summary>
	public void SetPendingAirCarry( Vector3 velocity )
	{
		_pendingAirCarry = velocity.WithZ( 0f );
		_pendingAirCarryUntil = Time.NowDouble + 0.5;
	}

	/// <summary>Brain sets this when applying walk/run speed — forward-only gate restores to it when aligned.</summary>
	public void SetIntendedMaxSpeed( float speed )
	{
		_intendedMaxSpeed = Math.Max( 0f, speed );
		ApplyForwardOnlySpeedNow();
	}

	/// <summary>Re-apply gated MaxSpeed immediately so brain ApplyAgentSpeed cannot stomp a turn-in-place stop.</summary>
	public void ApplyForwardOnlySpeedNow()
	{
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() )
			return;

		if ( !ForwardOnlyNavigation || !Agent.IsNavigating
		     || Components.Get<EntityCombat>() is { IsMovementLocked: true } )
		{
			Agent.MaxSpeed = _intendedMaxSpeed;
			return;
		}

		if ( !TryGetTravelDirection( out var desire ) )
		{
			Agent.MaxSpeed = _intendedMaxSpeed;
			return;
		}

		var facing = GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( facing.LengthSquared < 1e-6f )
		{
			Agent.MaxSpeed = 0f;
			return;
		}

		var align = Vector3.Dot( facing.Normal, desire );
		if ( align < MoveAlignStopDot )
		{
			Agent.MaxSpeed = 0f;
			return;
		}

		var fullDot = Math.Clamp( MoveAlignMinDot, MoveAlignStopDot + 0.05f, 0.99f );
		if ( align >= fullDot )
		{
			Agent.MaxSpeed = _intendedMaxSpeed;
			return;
		}

		// Creep while turning into the path — lets them arc around cubes instead of locking into a wall.
		var t = (align - MoveAlignStopDot) / (fullDot - MoveAlignStopDot);
		Agent.MaxSpeed = _intendedMaxSpeed * Math.Clamp( 0.12f + (0.55f * t), 0.12f, 0.7f );
	}

	public static Vector3 GetNavAnchorWorld( GameObject go ) =>
		go is { IsValid: true } ? go.WorldPosition : Vector3.Zero;

	public Vector3 GetNavAnchorWorld() => GameObject.WorldPosition;

	public void SyncAgentFromRoot()
	{
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() )
			return;

		Agent.SetAgentPosition( GameObject.WorldPosition );
	}

	public static Vector3 GetNavChasePoint( Scene scene, Vector3 worldPosition )
	{
		if ( EntityNavMeshUtility.TryProjectToNavMesh( scene, worldPosition, out var onNav, NavProjectTier.Fast ) )
			return onNav;

		return worldPosition;
	}

	protected override void OnStart()
	{
		Agent ??= Components.Get<NavMeshAgent>();
		Vitals ??= Components.Get<EntityVitals>();
		Body ??= FindBodyRenderer();
		EnsureAnimHelper();

		// Do not force UpdatePosition — EntityEnemySetup leaves it false until nav exists.

		_lastPosition = GameObject.WorldPosition;
		_clipFromPosition = _lastPosition;
		_smoothedGroundZ = _lastPosition.z;
		_hasSmoothedGroundZ = true;
		_fallStartZ = _lastPosition.z;
	}

	protected override void OnFixedUpdate()
	{
		if ( !Active || !GameObject.IsValid() || GameObject.IsProxy )
			return;

		if ( _isFalling )
			TickFall();
		else
		{
			TickForwardOnlyMoveGate();
			TickGroundSupport();
			ClipMovementAgainstSolids();
			// Ground Z is softened in OnUpdate (render rate) so elevation eases visually.
		}

		// Trapped: nothing (agent drift, shoves, the rigidbody) moves the feet off the plate.
		if ( IsTrapped && !_isFalling )
		{
			var pos = GameObject.WorldPosition;
			GameObject.WorldPosition = new Vector3( _trappedAnchor.x, _trappedAnchor.y, pos.z );
		}
	}

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid() || GameObject.IsProxy )
			return;

		Agent ??= Components.Get<NavMeshAgent>();

		// Body movement runs at render rate (it used to sit in the fixed tick, which stepped the body
		// at 50 Hz under a 144 Hz frame — visible judder). Follow / coast first, then the wall clip on
		// that very step, then the feet glue: the glue resets the clip origin every frame, so a step
		// taken here and clipped only in the next fixed tick was never seen — scavs walked clean
		// through walls (Mark, 2026-09-23). Same trap TickManualStepToward documents.
		if ( !_isFalling )
		{
			SyncAgentAttachment();
			TickRebakeCoast();
			if ( !_coasting )
				FollowAgent();
			ClipMovementAgainstSolids();
		}

		// Stick feet before measuring velocity so Z corrections don't pulse the walk anim.
		// Runs even without a skinned body — placeholder animals still need terrain Z.
		if ( !_isFalling )
			GlueFeetToTerrain( force: false );

		EnsureAnimHelper();
		if ( AnimHelper is null )
			return;

		var dt = Math.Max( Time.Delta, 1e-4f );
		var position = GameObject.WorldPosition;
		var velocity = (position - _lastPosition) / dt;
		_lastPosition = position;

		if ( _coasting )
			velocity = _coastVelocity;
		else if ( !_isFalling && Agent is not null && Agent.IsValid() && Agent.IsNavigating )
			velocity = Agent.WishVelocity.WithZ( 0f );
		else if ( _isFalling )
			velocity = _airVelocity + new Vector3( 0f, 0f, -_fallSpeed );

		// Coasting: the agent's wish is zero (its tile is gone) — feed the anim the carried velocity, or
		// the run cycle snaps to idle and back on every rebake.
		var animWish = _coasting
			? _coastVelocity
			: !_isFalling && Agent is not null && Agent.IsValid()
				? Agent.WishVelocity.WithZ( 0f )
				: velocity.WithZ( 0f );
		var facingTravel = IsFacingTravelDirection( out _ );
		// Turn-in-place: don't feed sideways wish into the citizen anim.
		if ( ForwardOnlyNavigation && !facingTravel )
		{
			animWish = Vector3.Zero;
			velocity = Vector3.Zero;
		}

		AnimHelper.WithVelocity( velocity );
		AnimHelper.WithWishVelocity( animWish );
		AnimHelper.IsGrounded = !_isFalling;
		AnimHelper.LookAtEnabled = false;

		UpdateBodyFacing( velocity );
	}

	void EnsureAnimHelper()
	{
		Body ??= FindBodyRenderer();
		if ( Body is null )
			return;

		if ( AnimHelper is null )
		{
			AnimHelper = Components.Get<CitizenAnimationHelper>();
			if ( AnimHelper is null )
				AnimHelper = Components.Create<CitizenAnimationHelper>();
		}

		if ( AnimHelper.Target is null )
			AnimHelper.Target = Body;

		AnimHelper.LookAtEnabled = false;
		AnimHelper.Enabled = true;
	}

	void UpdateBodyFacing( Vector3 velocity )
	{
		// Attack cycle owns yaw — locomotion must not fight it.
		if ( Components.Get<EntityCombat>() is { IsMovementLocked: true } )
			return;

		// Forward-only gate already rotates toward the path each FixedUpdate.
		if ( ForwardOnlyNavigation && Agent is not null && Agent.IsValid() && Agent.IsNavigating )
			return;

		// Between MoveTo re-issues the agent reports "not navigating" for a few frames. Facing the
		// look target there swung the body ~70° off the path; when the path resumed the forward-only
		// gate saw the misalignment and crept at ~17 % — the chase-only crawl. Keep the path heading.
		if ( ForwardOnlyNavigation && Time.NowDouble - _lastWishAt < 0.6 && _lastWishDir.LengthSquared > 0.5f )
		{
			SmoothFaceBodyToward( _lastWishDir, TurnDegreesPerSecond );
			return;
		}

		if ( (_brainOwnsFacing || _preferAimOverVelocity) && _hasFrozenAim )
		{
			var toAim = (_frozenAimWorld - GameObject.WorldPosition).WithZ( 0 );
			if ( toAim.LengthSquared > 1e-4f )
				SmoothFaceBodyToward( toAim.Normal, TurnDegreesPerSecond );
			return;
		}

		var flatVelocity = velocity.WithZ( 0 );
		if ( flatVelocity.Length > 24f )
		{
			SmoothFaceBodyToward( flatVelocity.Normal, TurnDegreesPerSecond );
			return;
		}

		if ( !_lookTarget.IsValid() )
		{
			_hasFrozenAim = false;
			return;
		}

		if ( !_hasFrozenAim || Time.NowDouble >= _nextAimReassessAt )
		{
			_frozenAimWorld = _lookTarget.WorldPosition;
			_hasFrozenAim = true;
			_nextAimReassessAt = Time.NowDouble + Math.Max( 0.5f, AimReassessInterval );
		}

		var toLiveAim = (_frozenAimWorld - GameObject.WorldPosition).WithZ( 0 );
		if ( toLiveAim.LengthSquared > 1e-4f )
			SmoothFaceBodyToward( toLiveAim.Normal, TurnDegreesPerSecond );
	}

	/// <summary>
	/// Stop until facing the nav wish, then run straight. Prevents run-then-turn / strafe look.
	/// </summary>
	void TickForwardOnlyMoveGate()
	{
		if ( !ForwardOnlyNavigation )
			return;

		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() || !Agent.IsNavigating )
			return;

		if ( Components.Get<EntityCombat>() is { IsMovementLocked: true } )
			return;

		if ( !TryGetTravelDirection( out var desire ) )
		{
			// MaxSpeed=0 kills WishVelocity, which clears desire, which left MaxSpeed at 0 forever.
			Agent.MaxSpeed = _intendedMaxSpeed;
			return;
		}

		SmoothFaceBodyToward( desire, TurnDegreesPerSecond );
		ApplyForwardOnlySpeedNow();
	}

	bool IsFacingTravelDirection( out Vector3 desire )
	{
		desire = default;
		if ( !ForwardOnlyNavigation )
			return true;

		if ( Agent is null || !Agent.IsValid() || !Agent.IsNavigating )
			return true;

		if ( !TryGetTravelDirection( out desire ) )
			return true;

		var facing = GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( facing.LengthSquared < 1e-6f )
			return false;

		return Vector3.Dot( facing.Normal, desire ) >= Math.Clamp( MoveAlignMinDot, 0.5f, 0.99f );
	}

	bool TryGetTravelDirection( out Vector3 desire )
	{
		desire = default;
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() )
			return false;

		var wish = Agent.WishVelocity.WithZ( 0f );
		if ( wish.LengthSquared > 16f )
		{
			desire = wish.Normal;
			_lastWishDir = desire;
			_lastWishAt = Time.NowDouble;
			_lastWishSpeed = wish.Length;
			// If the path wish drives into a wall, face along the wall toward the look target instead.
			if ( TrySteerAlongWall( desire, out var steered ) )
			{
				desire = steered;
				return true;
			}

			return true;
		}

		// Wish died because the turn gate zeroed MaxSpeed — keep turning toward the path direction
		// we were on. Falling back to look/hint here oscillates when the path disagrees with the
		// straight line to the target (speed toggles 0↔full every frame, net movement zero).
		if ( Agent.IsNavigating
		     && Time.NowDouble - _lastWishAt < 0.6
		     && _lastWishDir.LengthSquared > 0.5f )
		{
			desire = _lastWishDir;
			return true;
		}

		// Agent still "navigating" but wish is tiny (turning) — face look / travel hint.
		if ( _lookTarget.IsValid() )
		{
			var to = (_lookTarget.WorldPosition - GameObject.WorldPosition).WithZ( 0f );
			if ( to.LengthSquared > 1f )
			{
				desire = to.Normal;
				return true;
			}
		}

		if ( _hasTravelHint )
		{
			var toHint = (_travelHintWorld - GameObject.WorldPosition).WithZ( 0f );
			if ( toHint.LengthSquared > 1f )
			{
				desire = toHint.Normal;
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// When forward wish hits a vertical solid, return a tangent that still progresses toward the look target.
	/// </summary>
	bool TrySteerAlongWall( Vector3 forwardWish, out Vector3 steered )
	{
		steered = default;
		if ( !Scene.IsValid() )
			return false;

		var origin = GameObject.WorldPosition + Vector3.Up * BodyTraceHeight;
		var probe = origin + forwardWish * 28f;
		var tr = Scene.Trace.Ray( origin, probe )
			.Radius( BodyTraceRadius )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return false;

		if ( IsPlayerHierarchy( tr.GameObject ) )
			return false;

		// A stair riser ahead is the way up, not a wall to slide along.
		if ( BuildPieceNavPolicy.IsWalkablePathObject( tr.GameObject ) )
			return false;

		if ( tr.Normal.z > 0.45f )
			return false;

		var wall = tr.Normal.WithZ( 0f );
		if ( wall.LengthSquared < 1e-4f )
			return false;

		wall = wall.Normal;
		// Prefer the tangent that points more toward the player / look target.
		var alongA = Vector3.Cross( Vector3.Up, wall ).Normal;
		var alongB = -alongA;
		var prefer = forwardWish;
		if ( _lookTarget.IsValid() )
		{
			var to = (_lookTarget.WorldPosition - GameObject.WorldPosition).WithZ( 0f );
			if ( to.LengthSquared > 1f )
				prefer = to.Normal;
		}

		steered = Vector3.Dot( alongA, prefer ) >= Vector3.Dot( alongB, prefer ) ? alongA : alongB;
		return steered.LengthSquared > 1e-4f;
	}

	/// <summary>
	/// Soft-stick Z to the heightfield. Never rewrite XY — that cancels manual wander and
	/// fights the nav agent.
	/// </summary>
	public void GlueFeetToTerrain( bool force )
	{
		Agent ??= Components.Get<NavMeshAgent>();

		var pos = GameObject.WorldPosition;

		if ( !TryResolveFeetPosition( pos, out var feet ) )
		{
			_clipFromPosition = pos;
			return;
		}

		var targetZ = feet.z;
		var dt = Math.Max( Time.Delta, 1e-4f );

		if ( !_hasSmoothedGroundZ )
		{
			_smoothedGroundZ = pos.z;
			_hasSmoothedGroundZ = true;
		}

		var followScale = force ? 2.5f : 1f;
		var deltaToTarget = targetZ - _smoothedGroundZ;
		if ( MathF.Abs( deltaToTarget ) <= GroundStickDeadzone )
			_smoothedGroundZ = targetZ;
		else
			_smoothedGroundZ = StepGroundZ( _smoothedGroundZ, targetZ, dt, followScale );

		if ( MathF.Abs( _smoothedGroundZ - pos.z ) < 0.02f )
		{
			_clipFromPosition = pos;
			return;
		}

		var glued = pos.WithZ( _smoothedGroundZ );
		GameObject.WorldPosition = glued;
		_clipFromPosition = glued;

		// Rare resync only — frequent SetAgentPosition cancels agent motion.
		if ( force && Agent is not null && Agent.IsValid()
		     && MathF.Abs( Agent.AgentPosition.z - _smoothedGroundZ ) > AgentZResyncUnits )
		{
			var ap = Agent.AgentPosition;
			Agent.SetAgentPosition( ap.WithZ( _smoothedGroundZ ) );
		}
	}

	/// <summary>
	/// Per Mark ("all of them teleport every second on the nose", only the scavs, nothing else
	/// stutters): a raid knocks a wall down about once a second and every rebake unloads the tiles
	/// under the raiders — the engine agent standing on one loses its polygon, its wish drops to
	/// zero and the body freezes for the frames until the tile is back, then re-seats. Every scav in
	/// the base stalled in sync. While the mesh is stale a body that was moving is carried on its
	/// last velocity with the agent detached (UpdatePosition off — otherwise the frozen agent writes
	/// its old position back over ours), the solid clip still stops it at walls, and once the mesh is
	/// back the agent is seated at the body and takes over when it is navigating again. A standing
	/// body (no recent wish, mid-swing) is detached too and simply holds: the tile UNLOAD itself
	/// re-seated a standing agent and dragged the body ~12 u (Mark's log: every hop on the exact
	/// frame of a "tile request (unload …)", most of them locked mid-swing).
	/// </summary>
	void TickRebakeCoast()
	{
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is null || !Agent.IsValid() || !Scene.IsValid() )
			return;

		var now = Time.NowDouble;
		var stale = BuildNavMeshSync.IsNavStale( Scene );

		if ( _coasting )
		{
			if ( !stale && !_coastResumeIssued )
			{
				// Mesh is back: seat the agent where the body is; the brain's (staggered) path check
				// re-issues MoveTo from here and the agent takes over the moment it is navigating.
				_coastResumeIssued = true;
				Agent.SetAgentPosition( GameObject.WorldPosition );
			}

			// Back on the mesh: a walker hands over once the agent is navigating again; a holder (zero
			// velocity — standing, swinging) hands over at once, there is no path to wait for.
			if ( (_coastResumeIssued && (Agent.IsNavigating || _coastVelocity == Vector3.Zero)) || now - _coastSince > CoastMaxSeconds + 1d )
			{
				EndCoast();
				return;
			}

			if ( now - _coastSince > CoastMaxSeconds )
				return; // hold — the rebake is taking too long to keep walking blind

			GameObject.WorldPosition += _coastVelocity * Math.Max( Time.Delta, 1e-4f );
			return;
		}

		if ( !stale || !_agentAttached || IsTrapped )
			return;

		// Moving with a recent wish → carry it; standing / mid-swing → hold (velocity zero), but detach
		// either way so the unload cannot drag the body.
		var wasMoving = Components.Get<EntityCombat>() is not { IsMovementLocked: true }
		                && now - _lastWishAt <= CoastRecentWishSeconds && _lastWishSpeed >= 8f && _lastWishDir.LengthSquared >= 0.5f;

		_coasting = true;
		_coastResumeIssued = false;
		_coastSince = now;
		_coastVelocity = wasMoving ? _lastWishDir * Math.Min( _lastWishSpeed, Math.Max( 8f, _intendedMaxSpeed ) ) : Vector3.Zero;
		_agentAttached = false;
	}

	void EndCoast()
	{
		_coasting = false;
		_coastResumeIssued = false;
		if ( Agent is not null && Agent.IsValid() )
		{
			Agent.SetAgentPosition( GameObject.WorldPosition );
			_agentAttached = true;
		}
	}

	float StepGroundZ( float currentZ, float targetZ, float dt, float followRateScale )
	{
		var deltaZ = targetZ - currentZ;
		var t = 1f - MathF.Exp( -GroundFollowRate * followRateScale * dt );
		var desiredStep = deltaZ * t;
		// Terrain eases (ridge pops); build pieces are followed at once so stairs and roofs can be run
		// up — the exponential ease alone lags ~50 u behind a 45° roof at run speed (body inside slab).
		if ( _supportOnBuildPiece )
		{
			var pieceStep = PieceGroundFollowSpeed * dt;
			return currentZ + Math.Clamp( deltaZ, -pieceStep, pieceStep );
		}

		var maxUp = MaxGroundClimbSpeed * dt;
		var maxDown = MaxGroundDropSpeed * dt;
		var step = Math.Clamp( desiredStep, -maxDown, maxUp );
		var newZ = currentZ + step;
		if ( MathF.Abs( targetZ - newZ ) < 0.15f && MathF.Abs( deltaZ ) < maxUp )
			return targetZ;
		return newZ;
	}

	void ApplyRootPosition( Vector3 feet, bool syncAgent = false )
	{
		if ( (feet - GameObject.WorldPosition).Length < 0.05f )
			return;

		GameObject.WorldPosition = feet;
		_clipFromPosition = feet;
		_lastPosition = feet;

		if ( syncAgent && Agent is not null && Agent.IsValid() )
			Agent.SetAgentPosition( feet );
	}

	bool TryResolveFeetPosition( Vector3 sample, out Vector3 feet )
	{
		feet = sample;
		if ( !TryGetSupportHeightAt( sample, out var groundZ ) )
			return false;

		feet = new Vector3( sample.x, sample.y, groundZ + LandFeetOffset );
		return true;
	}

	bool TryGetSupportHeightAt( Vector3 horizontalPoint, out float groundZ )
	{
		groundZ = 0f;

		// On streamed terrain, heightfield Sample() matches the mesh builder — prefer it over
		// nav/physics which can be flatter and cause clip-then-snap.
		if ( TrySampleTerrainGroundZ( horizontalPoint, out var heightfieldZ ) )
		{
			groundZ = heightfieldZ;
			var trace = TraceGround(
				horizontalPoint + Vector3.Up * StepProbeLift,
				horizontalPoint - Vector3.Up * SupportTraceDepth );
			// Props / build pieces sit above the heightfield — use physics when clearly higher, but only
			// within step reach of the current feet (never yank up onto a slab we cannot step onto). A
			// probe that starts inside a solid (feet pushed into a wall) reports its own start — never
			// climb that.
			_supportOnBuildPiece = false;
			if ( trace.Hit && !trace.StartedSolid
			     && trace.HitPosition.z > heightfieldZ + 8f
			     && trace.HitPosition.z <= horizontalPoint.z + MaxStepUpUnits )
			{
				groundZ = trace.HitPosition.z;
				_supportOnBuildPiece = trace.GameObject.IsValid() && BuildPlacementUtility.FindBuildPieceOnHierarchy( trace.GameObject ) is not null;
			}

			return true;
		}

		var physics = TraceGround(
			horizontalPoint + Vector3.Up * StepProbeLift,
			horizontalPoint - Vector3.Up * SupportTraceDepth );
		if ( !physics.Hit || physics.StartedSolid )
			return false;

		if ( physics.HitPosition.z > horizontalPoint.z + MaxStepUpUnits )
			return false;

		groundZ = physics.HitPosition.z;
		_supportOnBuildPiece = physics.GameObject.IsValid() && BuildPlacementUtility.FindBuildPieceOnHierarchy( physics.GameObject ) is not null;
		return true;
	}

	void TickGroundSupport()
	{
		if ( HasGroundSupport( SupportTraceDepth, out _ ) )
			return;

		// Heightfield still under us — restick instead of starting a fall (nav Z dips into mesh).
		if ( TrySampleTerrainGroundZ( GameObject.WorldPosition, out var groundZ )
		     && GameObject.WorldPosition.z - groundZ <= MaxStandGap + 48f )
		{
			GlueFeetToTerrain( force: false );
			return;
		}

		BeginFall();
	}

	void BeginFall()
	{
		if ( _isFalling )
			return;

		_fallStartZ = GameObject.WorldPosition.z;
		_isFalling = true;
		_fallSpeed = 0f;
		_airVelocity = Vector3.Zero;

		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is not null && Agent.IsValid() )
		{
			var wish = Agent.WishVelocity.WithZ( 0 );
			if ( wish.Length > 8f )
				_airVelocity = wish;

			Agent.Stop();
			Agent.UpdatePosition = false;
			Agent.SetAgentPosition( GameObject.WorldPosition );
		}

		_agentAttached = false;

		// Scripted walk-off (drop-in): the agent is already stopped, carry comes from the brain.
		if ( _airVelocity.Length < 8f && Time.NowDouble < _pendingAirCarryUntil )
			_airVelocity = _pendingAirCarry;
	}

	void TickFall()
	{
		var dt = Math.Max( Time.Delta, 1e-4f );
		_fallSpeed = Math.Min( MaxFallSpeed, _fallSpeed + Gravity * dt );

		var position = GameObject.WorldPosition;
		var next = position;
		next += _airVelocity * dt;
		next.z -= _fallSpeed * dt;

		if ( TryFindLanding( next, out var landedPosition ) )
		{
			FinishFall( landedPosition );
			return;
		}

		GameObject.WorldPosition = next;
		_clipFromPosition = next;
		_lastPosition = next;
	}

	void FinishFall( Vector3 landedPosition )
	{
		_isFalling = false;
		_fallSpeed = 0f;
		_airVelocity = Vector3.Zero;
		_pendingAirCarryUntil = 0d;

		if ( !TryResolveFeetPosition( landedPosition, out var feet ) )
			feet = landedPosition;

		GameObject.WorldPosition = feet;
		_clipFromPosition = feet;
		_lastPosition = feet;
		_smoothedGroundZ = feet.z;
		_hasSmoothedGroundZ = true;

		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is not null && Agent.IsValid() )
		{
			Agent.SetAgentPosition( feet );
			// Drive again either way: the agent clamps itself to the nearest poly, and the brain
			// re-paths on Landed. Leaving UpdatePosition off after a failed snap froze the body
			// while paths kept being issued.
			if ( Scene.IsValid() )
				EntityNavMeshUtility.EnsureAgentOnNavMesh( Scene, Agent, feet );
			Agent.UpdatePosition = true;
		}

		if ( AnimHelper is not null )
			AnimHelper.IsGrounded = true;

		ApplyFallDamage( feet.z );
		Landed?.Invoke();
	}

	void ApplyFallDamage( float landedZ )
	{
		if ( FallDamageMinHeight <= 0f )
			return;

		Vitals ??= Components.Get<EntityVitals>();
		if ( Vitals is null || Vitals.IsDead )
			return;

		var fallDistance = Math.Max( 0f, _fallStartZ - landedZ );
		if ( fallDistance <= FallDamageMinHeight )
			return;

		Vitals.ApplyDamage( fallDistance - FallDamageMinHeight, this );
	}

	bool HasGroundSupport( float traceDepth, out float groundZ )
	{
		groundZ = 0f;
		var feet = GameObject.WorldPosition;

		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is not null && Agent.IsValid() && Agent.IsNavigating )
			feet = Agent.AgentPosition.WithZ( GameObject.WorldPosition.z );

		// Physics first: a build piece under the feet IS support, however high above the terrain
		// heightfield it sits. The old heightfield-first verdict declared "no support" as soon as an
		// entity climbed ~56 u up stairs on streamed terrain — BeginFall stopped the agent and dropped
		// it back down every tick, which is why entities never made it up ramps or roofs.
		var trace = TraceGround( feet + Vector3.Up * StepProbeLift, feet - Vector3.Up * traceDepth );
		if ( trace.Hit && !trace.StartedSolid )
		{
			groundZ = trace.HitPosition.z;
			if ( GameObject.WorldPosition.z - groundZ <= MaxStandGap )
				return true;
		}

		// Heightfield fallback on streamed terrain — physics can miss for a tick (fall/land stutter).
		if ( TrySampleTerrainGroundZ( feet, out var heightfieldZ ) )
		{
			groundZ = heightfieldZ;
			return GameObject.WorldPosition.z - heightfieldZ <= MaxStandGap + 24f;
		}

		// Embedded probe with no heightfield: never start a fall from inside geometry.
		return trace.Hit && trace.StartedSolid;
	}

	bool TryFindLanding( Vector3 targetPosition, out Vector3 landedPosition )
	{
		landedPosition = targetPosition;
		var traceStart = targetPosition + Vector3.Up * Math.Max( 64f, _fallSpeed * Time.Delta + 24f );
		var trace = TraceGround( traceStart, targetPosition - Vector3.Up * FallTraceDepth );
		if ( trace.Hit )
		{
			if ( targetPosition.z > trace.HitPosition.z + MaxStandGap )
				return false;

			landedPosition = new Vector3( targetPosition.x, targetPosition.y, trace.HitPosition.z + LandFeetOffset );
			return true;
		}

		if ( !TrySampleTerrainGroundZ( targetPosition, out var groundZ ) )
			return false;

		if ( targetPosition.z > groundZ + MaxStandGap )
			return false;

		landedPosition = new Vector3( targetPosition.x, targetPosition.y, groundZ + LandFeetOffset );
		return true;
	}

	/// <summary>Heightfield ground sample for scavs when physics/nav are not ready.</summary>
	public bool TrySampleTerrainGroundZ( Vector3 worldPos, out float groundZEngine )
	{
		groundZEngine = 0f;
		if ( !Scene.IsValid() )
			return false;

		if ( _cachedTerrain is null || !_cachedTerrain.IsValid() || !_cachedTerrain.Enabled )
		{
			_cachedTerrain = null;
			foreach ( var m in Scene.GetAllComponents<TerrainWorldManager>() )
			{
				if ( m is not null && m.IsValid() && m.Enabled )
				{
					_cachedTerrain = m;
					break;
				}
			}
		}

		if ( _cachedTerrain is null )
			return false;

		var meters = TerrainWorldUnits.EngineToMeters( worldPos );
		if ( !_cachedTerrain.TrySampleGroundMeters( meters.x, meters.y, out var groundZMeters ) )
			return false;

		groundZEngine = TerrainWorldUnits.MetersToEngine( groundZMeters );
		return true;
	}

	SceneTraceResult TraceGround( Vector3 from, Vector3 to )
	{
		// Thin probe for standing height — BodyTraceRadius (14) lifts HitPosition and makes feet hover.
		const float GroundProbeRadius = 2f;
		var physics = Scene.Trace.Ray( from, to )
			.Radius( GroundProbeRadius )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();
		if ( physics.Hit )
			return physics;

		return Scene.Trace.Ray( from, to )
			.Radius( GroundProbeRadius )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();
	}

	void ClipMovementAgainstSolids()
	{
		Agent ??= Components.Get<NavMeshAgent>();
		var current = GameObject.WorldPosition;
		var delta = current - _clipFromPosition;
		if ( delta.LengthSquared < 0.25f )
		{
			_clipFromPosition = current;
			return;
		}

		// Horizontal motion only. The old "delta.z > 1 = climbing, skip" gate fired on every nav
		// height nudge (quantized tile heights vs the smoothed feet), which disabled the clip
		// almost permanently — that, plus the flush-start sweep, is how scavs walked through walls.
		// Ramps and stairs are still allowed below via the hit normal.
		var horizontalDelta = delta.WithZ( 0 );
		if ( horizontalDelta.LengthSquared < 0.25f )
		{
			_clipFromPosition = current;
			return;
		}

		var from = _clipFromPosition.WithZ( current.z ) + Vector3.Up * BodyTraceHeight;
		var to = current + Vector3.Up * BodyTraceHeight;
		var trace = Scene.Trace.Ray( from, to )
			.Radius( BodyTraceRadius )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();
		if ( !trace.Hit )
		{
			trace = Scene.Trace.Ray( from, to )
				.Radius( BodyTraceRadius )
				.IgnoreGameObjectHierarchy( GameObject )
				.Run();
		}

		if ( !trace.Hit || !trace.GameObject.IsValid() )
		{
			_clipFromPosition = current;
			return;
		}

		// Terrain mesh is walkable via heightfield stick — treating it as a wall zeros MaxSpeed each step.
		if ( IsTerrainChunkHierarchy( trace.GameObject ) )
		{
			_clipFromPosition = current;
			return;
		}

		// Never shove off players — that caused mutual scoot / launches.
		if ( IsPlayerHierarchy( trace.GameObject ) )
		{
			_clipFromPosition = current;
			return;
		}

		// Stairs / roofs are walked up, never clipped against: a stair riser is a vertical face and
		// read as a wall here, which pinned scavs at the first step.
		if ( BuildPieceNavPolicy.IsWalkablePathObject( trace.GameObject ) )
		{
			_clipFromPosition = current;
			return;
		}

		// The body sphere already overlaps the solid (the nav agent dragged us in, or an earlier clip
		// left us flush). A started-solid sweep carries no usable normal — that was the tunnel: the
		// zero normal read as "nothing to slide on" and the entity walked straight through walls.
		// A thin ray from the last good spot still finds the face; failing that, hold position.
		var embedded = trace.StartedSolid;
		if ( embedded )
		{
			var thin = Scene.Trace.Ray( from, to )
				.UsePhysicsWorld()
				.IgnoreGameObjectHierarchy( GameObject )
				.Run();
			if ( thin.Hit && !thin.StartedSolid && thin.Normal.LengthSquared > 1e-4f
			     && thin.GameObject.IsValid() && !IsPlayerHierarchy( thin.GameObject ) )
			{
				trace = thin;
			}
			else
			{
				HoldAtClipOrigin( current, trace.GameObject );
				return;
			}
		}

		// Walkable slopes / floors — allow.
		if ( trace.Normal.z > 0.45f )
		{
			_clipFromPosition = current;
			return;
		}

		var wallNormal = trace.Normal.WithZ( 0 );
		if ( wallNormal.LengthSquared < 1e-4f )
		{
			HoldAtClipOrigin( current, trace.GameObject );
			return;
		}

		wallNormal = wallNormal.Normal;
		if ( Vector3.Dot( horizontalDelta.Normal, wallNormal ) >= -0.05f )
		{
			_clipFromPosition = current;
			return;
		}

		// Stop where the sphere first touched, a hair off the face — never closer than the body
		// radius, or the next sweep starts inside the wall. Keep horizontal motion tangential; don't
		// Agent.Stop (Stop + flank repath was the "jerk away / back up 5m" feel).
		var stop = embedded ? _clipFromPosition.WithZ( current.z ) : trace.EndPosition - Vector3.Up * BodyTraceHeight;
		var slid = (stop + wallNormal * ClipSurfaceGap).WithZ( current.z );
		ApplyClippedPosition( slid, trace.GameObject );
	}

	/// <summary>Embedded with no face to slide on — stay at the last good spot rather than tunnel.</summary>
	void HoldAtClipOrigin( Vector3 current, GameObject hit ) =>
		ApplyClippedPosition( _clipFromPosition.WithZ( current.z ), hit );

	void ApplyClippedPosition( Vector3 position, GameObject hit )
	{
		GameObject.WorldPosition = position;
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is not null && Agent.IsValid() )
			Agent.SetAgentPosition( position );

		_clipFromPosition = position;
		_lastPosition = position;

		// Hit a wall while pathing — brief creep, not a permanent MaxSpeed=0 latch.
		if ( Agent is not null && Agent.IsValid() && Agent.IsNavigating && ForwardOnlyNavigation )
			Agent.MaxSpeed = Math.Max( 12f, _intendedMaxSpeed * 0.15f );

		if ( Time.NowDouble < _nextClipNotifyAt )
			return;

		_nextClipNotifyAt = Time.NowDouble + 0.4;
		var brain = Components.Get<EntityBrain>();
		brain?.NotifyChasePhysicsBlocked( hit );
	}

	static bool IsTerrainChunkHierarchy( GameObject hit )
	{
		for ( var current = hit; current.IsValid(); current = current.Parent )
		{
			if ( current.Name.StartsWith( "TerrainChunk", StringComparison.Ordinal ) )
				return true;
			if ( current.Components.Get<TerrainWorldManager>() is not null )
				return true;
		}

		return false;
	}

	static bool IsPlayerHierarchy( GameObject hit )
	{
		for ( var current = hit; current.IsValid(); current = current.Parent )
		{
			if ( current.Components.Get<PlayerController>() is not null )
				return true;
			if ( current.Components.Get<PlayerVitals>() is not null )
				return true;
		}

		return false;
	}

	/// <summary>
	/// Last thing before the frame renders: the engine nav agent writes the root after our OnUpdate,
	/// so smoothing from OnUpdate let the hop render for one frame, then yanked the model back and
	/// eased it forward — jump, back, jump (Mark: "the same, if not even more frequent").
	/// </summary>
	protected override void OnPreRender()
	{
		if ( !Active || !GameObject.IsValid() || GameObject.IsProxy )
			return;

		SmoothVisualHops();
		DetectTwitch();
	}

	// ── Twitch detector (diagnostics) ─────────────────────────────────────────────────────
	/// <summary>A one-frame speed collapse from above this (u/s) …</summary>
	const float TwitchFastSpeed = 90f;
	/// <summary>… to below this counts as a stall.</summary>
	const float TwitchSlowSpeed = 25f;
	/// <summary>A one-frame yaw change above this counts as a facing snap.</summary>
	const float TwitchYawDegrees = 25f;
	Vector3 _twitchPrevPos;
	float _twitchPrevSpeed;
	float _twitchPrevYaw;
	bool _hasTwitchPrev;
	double _nextTwitchLogAt;

	/// <summary>
	/// Per Mark ("they twitch every second on the second, even before a wall breaks"): name the
	/// twitch. A stall (speed collapsing in one frame) or a facing snap logs once per entity per second
	/// with everything that could have caused it — state and how long ago it changed, seconds since the
	/// brain last issued MoveTo, the agent's status, combat lock, coast / attach — so the cause can be
	/// read off the console instead of guessed.
	/// </summary>
	void DetectTwitch()
	{
		var pos = GameObject.WorldPosition;
		var yaw = GameObject.WorldRotation.Angles().yaw;
		if ( !_hasTwitchPrev )
		{
			_twitchPrevPos = pos;
			_twitchPrevYaw = yaw;
			_hasTwitchPrev = true;
			return;
		}

		var dt = Math.Max( Time.Delta, 1e-4f );
		var speed = (pos - _twitchPrevPos).WithZ( 0f ).Length / dt;
		var yawDelta = MathF.Abs( Angles.NormalizeAngle( yaw - _twitchPrevYaw ) );
		var prevSpeed = _twitchPrevSpeed;
		var stall = prevSpeed > TwitchFastSpeed && speed < TwitchSlowSpeed;
		var snap = yawDelta > TwitchYawDegrees;
		_twitchPrevPos = pos;
		_twitchPrevSpeed = speed;
		_twitchPrevYaw = yaw;

		if ( (!stall && !snap) || _isFalling || !NavDebugCommands.TraceEnabled || Time.NowDouble < _nextTwitchLogAt )
			return;

		_nextTwitchLogAt = Time.NowDouble + 1d;
		var brain = Components.Get<EntityBrain>();
		var combat = Components.Get<EntityCombat>();
		var agentOk = Agent is not null && Agent.IsValid();
		Log.Info( $"[Twitch] {GameObject.Name} {(stall ? $"STALL {prevSpeed:0}→{speed:0} u/s" : "")}{(stall && snap ? " + " : "")}{(snap ? $"YAW SNAP {yawDelta:0}°" : "")} | state={brain?.CurrentState} stateAge={brain?.SecondsSinceStateChange ?? -1:0.00}s moveIssued={brain?.SecondsSinceMoveIssued ?? -1:0.00}s ago block={brain?.LastNavBlockReason} path={brain?.LastPathStatus} navigating={(agentOk && Agent.IsNavigating)} maxSpeed={(agentOk ? Agent.MaxSpeed : 0):0} wish={(agentOk ? Agent.WishVelocity.WithZ( 0f ).Length : 0):0} locked={combat is { IsMovementLocked: true }} attached={_agentAttached} coasting={_coasting} navStale={(Scene.IsValid() && BuildNavMeshSync.IsNavStale( Scene ))}" );
	}

	/// <summary>
	/// Per Mark ("SO choppy — they teleport around"): the root can hop when the engine nav agent
	/// re-clamps onto a rebuilt tile (every wall a raid knocks down rebakes the tiles under the
	/// raiders) and UpdatePosition carries the body with it. Gameplay keeps the new root position;
	/// the model child is held where it was and eases onto the root over ~0.2 s, so a hop reads as
	/// a quick step instead of a teleport. Hops are logged (throttled) with the nav state so the
	/// remaining causes can be found and removed at the source. Host only — clients interpolate.
	/// </summary>
	void SmoothVisualHops()
	{
		var bodyGo = Body?.GameObject;
		if ( bodyGo is null || !bodyGo.IsValid() || bodyGo == GameObject )
			return;

		if ( !_bodyBaseCaptured )
		{
			_bodyBaseLocal = bodyGo.LocalPosition;
			_bodyBaseCaptured = true;
		}

		var dt = Math.Max( Time.Delta, 1e-4f );
		var root = GameObject.WorldPosition;
		if ( _hasVisualPrevRoot && !_isFalling && !IsTrapped )
		{
			var hop = (root - _visualPrevRoot).WithZ( 0f );
			var length = hop.Length;
			var frameLimit = VisualHopMaxSpeed * dt + VisualHopSlackUnits;
			if ( length > frameLimit && length <= VisualHopMaxUnits )
			{
				// Keep the model where the eye last saw it; the offset decays to zero below. Measured at
		// pre-render, after the agent has written the root, so the hop itself never reaches a frame.
				_visualOffsetWorld -= hop;
				if ( NavDebugCommands.TraceEnabled && Time.NowDouble >= _nextHopLogAt )
				{
					_nextHopLogAt = Time.NowDouble + 2d;
					var agentOk = Agent is not null && Agent.IsValid();
					var stale = Scene.IsValid() && BuildNavMeshSync.IsNavStale( Scene );
					var generating = Scene.IsValid() && BuildNavMeshSync.IsNavGenerating( Scene );
					var state = Components.Get<EntityBrain>()?.CurrentState.ToString() ?? "-";
					var agentOff = agentOk ? Vector3.DistanceBetween( Agent.AgentPosition, root ) : -1f;
					Log.Info( $"[Loco] {GameObject.Name} root hopped {length:0}u in one frame ({state}, attached={_agentAttached}, navigating={(agentOk && Agent.IsNavigating)}, agentOff={agentOff:0}, locked={Components.Get<EntityCombat>() is { IsMovementLocked: true }}, navStale={stale}, navGenerating={generating}) — smoothing the model" );
				}
			}
		}

		_visualPrevRoot = root;
		_hasVisualPrevRoot = true;

		if ( _visualOffsetWorld.LengthSquared < 0.01f )
		{
			if ( _visualOffsetWorld != Vector3.Zero )
			{
				_visualOffsetWorld = Vector3.Zero;
				bodyGo.LocalPosition = _bodyBaseLocal;
			}

			return;
		}

		_visualOffsetWorld *= MathF.Exp( -VisualHopDecayRate * dt );
		if ( _visualOffsetWorld.Length > VisualHopMaxUnits )
			_visualOffsetWorld = _visualOffsetWorld.Normal * VisualHopMaxUnits;

		bodyGo.LocalPosition = _bodyBaseLocal + GameObject.WorldRotation.Inverse * _visualOffsetWorld;
	}

	SkinnedModelRenderer FindBodyRenderer()
	{
		foreach ( var renderer in Components.GetAll<SkinnedModelRenderer>( FindMode.EverythingInSelfAndChildren ) )
		{
			if ( renderer is not null && renderer.Enabled )
				return renderer;
		}

		return null;
	}

	/// <summary>Turn body toward a world point at <paramref name="degreesPerSecond"/> (host AI).</summary>
	public void SmoothFaceTowardWorld( Vector3 worldPos, float degreesPerSecond )
	{
		var flat = (worldPos - GameObject.WorldPosition).WithZ( 0f );
		if ( flat.LengthSquared < 1e-4f )
			return;

		SmoothFaceBodyToward( flat.Normal, degreesPerSecond );
	}

	void SmoothFaceBodyToward( Vector3 flatDirection ) =>
		SmoothFaceBodyToward( flatDirection, TurnDegreesPerSecond );

	void SmoothFaceBodyToward( Vector3 flatDirection, float degreesPerSecond )
	{
		if ( flatDirection.LengthSquared < 1e-4f )
			return;

		var desire = flatDirection.Normal;
		var currentYaw = GameObject.WorldRotation.Angles().yaw;
		var targetYaw = Rotation.LookAt( desire, Vector3.Up ).Angles().yaw;
		var delta = Angles.NormalizeAngle( targetYaw - currentYaw );
		// Hitch safety only (cap Δt) — old 2.5°/frame cap starved corner turns.
		var frameBudget = Math.Max( 1f, degreesPerSecond ) * Math.Min( Math.Max( Time.Delta, 1e-4f ), 0.05f );
		var step = Math.Clamp( delta, -frameBudget, frameBudget );
		if ( MathF.Abs( step ) < 0.01f )
			return;

		GameObject.WorldRotation = Rotation.FromYaw( currentYaw + step );

		// Citizen body is a child renderer — keep it locked to root so anim wish can't visual-snap.
		if ( Body is not null && Body.GameObject.IsValid() && Body.GameObject != GameObject )
			Body.GameObject.LocalRotation = Rotation.Identity;
	}
}
