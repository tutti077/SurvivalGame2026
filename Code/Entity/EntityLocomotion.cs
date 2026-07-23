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
	const float EntityStandingHeight = 72f;
	const float FallDamageHeightMultiplier = 5f;
	const float FeetTraceLift = 8f;
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
	TerrainWorldManager _cachedTerrain;
	float _smoothedGroundZ;
	bool _hasSmoothedGroundZ;
	Vector3 _airVelocity;
	Vector3 _frozenAimWorld;
	GameObject _lookTarget;
	bool _isFalling;
	bool _hasFrozenAim;
	bool _preferAimOverVelocity;
	bool _brainOwnsFacing;
	bool _hasTravelHint;
	Vector3 _travelHintWorld;
	float _fallSpeed;
	float _fallStartZ;
	float _intendedMaxSpeed = 220f;
	double _nextAimReassessAt;
	double _nextClipNotifyAt;

	public bool IsAirborne => _isFalling;
	public bool IsSpawnSettling => false;

	public event Action Landed;

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
	}

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid() || GameObject.IsProxy )
			return;

		Agent ??= Components.Get<NavMeshAgent>();
		EnsureAnimHelper();
		if ( AnimHelper is null )
			return;

		var dt = Math.Max( Time.Delta, 1e-4f );
		var position = GameObject.WorldPosition;

		// Stick feet before measuring velocity so Z corrections don't pulse the walk anim.
		if ( !_isFalling )
			GlueFeetToTerrain( force: false );

		position = GameObject.WorldPosition;
		var velocity = (position - _lastPosition) / dt;
		_lastPosition = position;

		if ( !_isFalling && Agent is not null && Agent.IsValid() && Agent.IsNavigating )
			velocity = Agent.WishVelocity.WithZ( 0f );
		else if ( _isFalling )
			velocity = _airVelocity + new Vector3( 0f, 0f, -_fallSpeed );

		var animWish = !_isFalling && Agent is not null && Agent.IsValid()
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
			// If the path wish drives into a wall, face along the wall toward the look target instead.
			if ( TrySteerAlongWall( desire, out var steered ) )
			{
				desire = steered;
				return true;
			}

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

	float StepGroundZ( float currentZ, float targetZ, float dt, float followRateScale )
	{
		var deltaZ = targetZ - currentZ;
		var t = 1f - MathF.Exp( -GroundFollowRate * followRateScale * dt );
		var desiredStep = deltaZ * t;
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
				horizontalPoint + Vector3.Up * FeetTraceLift,
				horizontalPoint - Vector3.Up * SupportTraceDepth );
			// Props / build pieces sit above the heightfield — use physics when clearly higher.
			if ( trace.Hit && trace.HitPosition.z > heightfieldZ + 8f )
				groundZ = trace.HitPosition.z;
			return true;
		}

		var physics = TraceGround(
			horizontalPoint + Vector3.Up * FeetTraceLift,
			horizontalPoint - Vector3.Up * SupportTraceDepth );
		if ( !physics.Hit )
			return false;

		groundZ = physics.HitPosition.z;
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
			if ( Scene.IsValid()
			     && EntityNavMeshUtility.EnsureAgentOnNavMesh( Scene, Agent, feet ) )
				Agent.UpdatePosition = true;
			else
				Agent.UpdatePosition = false;
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

		// Heightfield first on streamed terrain — physics can miss for a tick and trigger fall/land stutter.
		if ( TrySampleTerrainGroundZ( feet, out groundZ ) )
			return GameObject.WorldPosition.z - groundZ <= MaxStandGap + 24f;

		var trace = TraceGround( feet + Vector3.Up * FeetTraceLift, feet - Vector3.Up * traceDepth );
		if ( !trace.Hit )
			return false;

		groundZ = trace.HitPosition.z;
		return GameObject.WorldPosition.z - groundZ <= MaxStandGap;
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

		if ( delta.z > 1f )
		{
			_clipFromPosition = current;
			return;
		}

		var horizontalDelta = delta.WithZ( 0 );
		if ( horizontalDelta.LengthSquared < 0.25f )
		{
			_clipFromPosition = current;
			return;
		}

		var from = _clipFromPosition + Vector3.Up * BodyTraceHeight;
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

		// Walkable slopes / floors — allow.
		if ( trace.Normal.z > 0.45f )
		{
			_clipFromPosition = current;
			return;
		}

		var wallNormal = trace.Normal.WithZ( 0 );
		if ( wallNormal.LengthSquared < 1e-4f )
		{
			_clipFromPosition = current;
			return;
		}

		wallNormal = wallNormal.Normal;
		if ( Vector3.Dot( horizontalDelta.Normal, wallNormal ) >= -0.05f )
		{
			_clipFromPosition = current;
			return;
		}

		// Soft slide off the wall — keep horizontal motion tangential; don't Agent.Stop
		// (Stop + flank repath was the “jerk away / back up 5m” feel).
		var intoWall = Vector3.Dot( horizontalDelta, wallNormal );
		// intoWall is negative when driving into the wall — peel that component off + small gap.
		var slid = current - wallNormal * (intoWall - 1.5f);
		slid.z = current.z;
		GameObject.WorldPosition = slid;
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is not null && Agent.IsValid() )
			Agent.SetAgentPosition( slid );

		_clipFromPosition = slid;
		_lastPosition = slid;

		// Hit a wall while pathing — brief creep, not a permanent MaxSpeed=0 latch.
		if ( Agent is not null && Agent.IsValid() && Agent.IsNavigating && ForwardOnlyNavigation )
			Agent.MaxSpeed = Math.Max( 12f, _intendedMaxSpeed * 0.15f );

		if ( Time.NowDouble < _nextClipNotifyAt )
			return;

		_nextClipNotifyAt = Time.NowDouble + 0.4;
		var brain = Components.Get<EntityBrain>();
		brain?.NotifyChasePhysicsBlocked( trace.GameObject );
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
