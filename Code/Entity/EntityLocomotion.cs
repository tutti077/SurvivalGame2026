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
	const float LandFeetOffset = 2f;
	const float Gravity = 800f;
	const float MaxFallSpeed = 1200f;
	const float BodyTurnRate = 10f;

	[Property] public NavMeshAgent Agent { get; set; }
	[Property] public EntityVitals Vitals { get; set; }
	[Property] public SkinnedModelRenderer Body { get; set; }
	[Property] public CitizenAnimationHelper AnimHelper { get; set; }

	[Property, Group( "Fall" ), Title( "Fall damage starts above (units)" )]
	public float FallDamageMinHeight { get; set; } = EntityStandingHeight * FallDamageHeightMultiplier;

	Vector3 _lastPosition;
	Vector3 _clipFromPosition;
	Vector3 _airVelocity;
	GameObject _lookTarget;
	bool _isFalling;
	float _fallSpeed;
	float _fallStartZ;

	public bool IsAirborne => _isFalling;
	public bool IsSpawnSettling => false;

	public event Action Landed;

	public void SetLookTarget( GameObject target ) => _lookTarget = target;

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

		if ( Agent is not null && Agent.IsValid() )
			Agent.UpdatePosition = true;

		_lastPosition = GameObject.WorldPosition;
		_clipFromPosition = _lastPosition;
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
			TickGroundSupport();
			ClipMovementAgainstBuildPieces();
			SyncPositionToGround();
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
		var velocity = (position - _lastPosition) / dt;
		_lastPosition = position;

		if ( !_isFalling && Agent is not null && Agent.IsValid() && Agent.IsNavigating )
			velocity = Agent.WishVelocity;
		else if ( _isFalling )
			velocity = _airVelocity + new Vector3( 0f, 0f, -_fallSpeed );

		AnimHelper.WithVelocity( velocity );
		AnimHelper.WithWishVelocity( !_isFalling && Agent is not null && Agent.IsValid() ? Agent.WishVelocity : velocity );
		AnimHelper.IsGrounded = !_isFalling;

		if ( _lookTarget.IsValid() )
			AnimHelper.LookAt = _lookTarget;

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

		AnimHelper.LookAtEnabled = true;
		AnimHelper.Enabled = true;
	}

	void UpdateBodyFacing( Vector3 velocity )
	{
		var flatVelocity = velocity.WithZ( 0 );
		if ( flatVelocity.Length > 24f )
		{
			SmoothFaceBodyToward( flatVelocity.Normal );
			return;
		}

		if ( _lookTarget.IsValid() )
			SmoothFaceBodyToward( (_lookTarget.WorldPosition - GameObject.WorldPosition).WithZ( 0 ).Normal );
	}

	void SyncPositionToGround()
	{
		if ( _isFalling )
			return;

		Agent ??= Components.Get<NavMeshAgent>();

		// NavMeshAgent.UpdatePosition drives the transform while pathing; don't fight it.
		if ( Agent is not null && Agent.IsValid() && Agent.UpdatePosition && Agent.IsNavigating )
		{
			_clipFromPosition = GameObject.WorldPosition;
			return;
		}

		// NavMeshAgent.UpdatePosition drives the transform; we only snap feet to ground when idle.
		if ( Agent is not null && Agent.IsValid() && Agent.UpdatePosition )
		{
			if ( !Agent.IsNavigating && Components.Get<EntityBrain>() is not null )
			{
				_clipFromPosition = GameObject.WorldPosition;
				return;
			}

			var sample = GameObject.WorldPosition;
			if ( TryGetSupportHeightAt( sample, out var groundZ ) )
			{
				var feet = new Vector3( sample.x, sample.y, groundZ + LandFeetOffset );
				if ( (feet - sample).Length > 0.05f )
					ApplyRootPosition( feet, syncAgent: true );
				else
					_clipFromPosition = sample;
			}

			return;
		}

		var manualSample = GameObject.WorldPosition;
		if ( Agent is not null && Agent.IsValid() && Agent.IsNavigating )
			manualSample = Agent.AgentPosition;

		if ( TryResolveFeetPosition( manualSample, out var manualFeet ) )
		{
			ApplyRootPosition( manualFeet, syncAgent: Agent is not null && Agent.IsValid() && (Agent.AgentPosition - manualFeet).Length > 0.05f );
			return;
		}

		// Nav agent can advance before a foot trace succeeds — still follow horizontal motion.
		if ( Agent is not null && Agent.IsValid() && Agent.IsNavigating )
		{
			var horizontal = manualSample.WithZ( GameObject.WorldPosition.z );
			if ( TryGetSupportHeightAt( horizontal, out var groundZ ) )
				ApplyRootPosition( new Vector3( horizontal.x, horizontal.y, groundZ + LandFeetOffset ) );
			else
				ApplyRootPosition( manualSample );
		}
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
		var trace = TraceGround(
			horizontalPoint + Vector3.Up * FeetTraceLift,
			horizontalPoint - Vector3.Up * SupportTraceDepth );

		if ( !trace.Hit )
			return false;

		groundZ = trace.HitPosition.z;
		return true;
	}

	void TickGroundSupport()
	{
		if ( HasGroundSupport( SupportTraceDepth, out _ ) )
			return;

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

		Agent ??= Components.Get<NavMeshAgent>();
		if ( Agent is not null && Agent.IsValid() )
		{
			Agent.SetAgentPosition( feet );
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
		if ( !trace.Hit )
			return false;

		if ( targetPosition.z > trace.HitPosition.z + MaxStandGap )
			return false;

		landedPosition = new Vector3( targetPosition.x, targetPosition.y, trace.HitPosition.z + LandFeetOffset );
		return true;
	}

	SceneTraceResult TraceGround( Vector3 from, Vector3 to ) =>
		Scene.Trace.Ray( from, to )
			.Radius( BodyTraceRadius )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

	void ClipMovementAgainstBuildPieces()
	{
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
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !trace.Hit || !IsBlockingBuildPiece( trace.GameObject ) )
		{
			_clipFromPosition = current;
			return;
		}

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

		var blocked = trace.HitPosition + trace.Normal * BodyTraceRadius;
		blocked.z = current.z;
		GameObject.WorldPosition = blocked;
		Agent?.SetAgentPosition( blocked );
		_clipFromPosition = blocked;
		_lastPosition = blocked;
	}

	static bool IsBlockingBuildPiece( GameObject hit )
	{
		var current = hit;
		while ( current.IsValid() )
		{
			var piece = current.Components.Get<BuildPiece>();
			if ( piece is not null && piece.Enabled && !piece.IsPreviewGhost && !piece.IsBlueprint )
				return true;

			current = current.Parent;
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

	void SmoothFaceBodyToward( Vector3 flatDirection )
	{
		if ( flatDirection.LengthSquared < 1e-4f )
			return;

		var targetRotation = Rotation.LookAt( flatDirection.Normal, Vector3.Up );
		var blend = Math.Min( 1f, BodyTurnRate * Time.Delta );
		GameObject.WorldRotation = Rotation.Slerp( GameObject.WorldRotation, targetRotation, blend );
	}
}
