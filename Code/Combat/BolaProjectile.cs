using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-simulated thrown bola (Mark 2026-11-02, prompt 15). Flies a forward-and-down arc under gravity,
/// spinning round its flight axis, drawn <see cref="FlightScale"/>x so it reads in the air. If it reaches the
/// LEGS (lower <see cref="LegHeightFraction"/> of the body) of a <see cref="TrapSize.Small"/> creature
/// (a player, a fox, a scav - the same size band the small foot trap takes, resolved through
/// <see cref="BearTrap.TryResolveVictimRoot"/>) the victim is held where it stands for
/// <see cref="RootSeconds"/> (<see cref="EntityLocomotion.HostSetTrapped"/> / <see cref="PlayerMovement.HostSetTrapped"/>
/// plus the <c>trapped</c> status on players); the wrapped bola sits at the feet meanwhile and drops as a
/// <c>bola_basic</c> pickup when the hold ends. Anything else it lands on just drops the pickup.
/// Clients see the networked transform; only the host decides.
/// </summary>
[Title( "Bola Projectile" )]
public sealed class BolaProjectile : Component
{
	public const string BolaResourceId = "bola_basic";
	const string BolaModelPath = "models/weapons/bola_basic.vmdl";
	/// <summary>A hit in this lower fraction of the victim's height is on the legs; higher bounces off.</summary>
	const float LegHeightFraction = 0.45f;
	/// <summary>Legs reach at least this high on a squat body (units; 0.6 m).</summary>
	const float MinLegHeightUnits = 24f;
	/// <summary>Model scale while flying (Mark 2026-11-02: "a bit bigger as they fly so players can see them").</summary>
	const float FlightScale = 2f;

	[Property] public float LifetimeSeconds { get; set; } = 8f;
	[Property] public float Radius { get; set; } = 6f;
	[Property] public float SpinDegreesPerSecond { get; set; } = 900f;

	GameObject _attackerRoot;
	Vector3 _velocity;
	float _rootSeconds = 5f;
	float _age;
	bool _spent;
	bool _authority;
	float _spinAngle;

	// Hold bookkeeping (host)
	GameObject _victimRoot;
	PlayerMovement _victimPlayer;
	PlayerVitals _victimVitals;
	EntityLocomotion _victimEntity;
	double _releaseAt;

	public void Configure( GameObject attackerRoot, Vector3 velocity, float rootSeconds, bool hostAuthority )
	{
		_attackerRoot = attackerRoot;
		_velocity = velocity;
		_rootSeconds = Math.Max( 0.5f, rootSeconds );
		_authority = hostAuthority;
		_age = 0f;
		_spent = false;
		if ( velocity.LengthSquared > 1e-6f )
			WorldRotation = Rotation.LookAt( velocity.Normal, Vector3.Up );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( _spent || !GameObject.IsValid() )
			return;

		// Spin round the flight axis - every peer, it is only looks.
		_spinAngle = (_spinAngle + SpinDegreesPerSecond * Time.Delta) % 360f;
		var axis = _velocity.LengthSquared > 1e-6f ? _velocity.Normal : WorldRotation.Forward;
		WorldRotation = Rotation.LookAt( axis, Vector3.Up ) * Rotation.FromRoll( _spinAngle );
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();
		if ( !GameObject.IsValid() )
			return;

		if ( !_authority && GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( _spent )
		{
			TickHold();
			return;
		}

		var dt = Math.Max( 0f, Time.Delta );
		if ( dt <= 1e-6f )
			return;

		_age += dt;
		if ( _age >= LifetimeSeconds )
		{
			DropPickupAndDestroy( WorldPosition );
			return;
		}

		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
		{
			GameObject.Destroy();
			return;
		}

		var gravity = scene.PhysicsWorld?.Gravity ?? new Vector3( 0f, 0f, -800f );
		_velocity += gravity * dt;

		var start = WorldPosition;
		var end = start + _velocity * dt;
		var trace = scene.Trace.Ray( start, end )
			.Radius( Math.Max( 0.5f, Radius ) )
			.IgnoreGameObjectHierarchy( GameObject );
		if ( _attackerRoot is not null && _attackerRoot.IsValid() )
			trace = trace.IgnoreGameObjectHierarchy( _attackerRoot );

		var tr = trace.Run();
		if ( !tr.Hit )
		{
			WorldPosition = end;
			return;
		}

		if ( tr.GameObject.IsValid()
		     && BearTrap.TryResolveVictimRoot( tr.GameObject, out var root, out var isPlayer, out var size )
		     && size == TrapSize.Small
		     && tr.HitPosition.z - root.WorldPosition.z <= LegHeightUnits( root ) )
		{
			HostWrapFeet( root, isPlayer, tr.HitPosition );
			return;
		}

		DropPickupAndDestroy( tr.HitPosition + tr.Normal * 2f );
	}

	/// <summary>Leg band of the victim, from its bounds once at the hit (not per frame).</summary>
	static float LegHeightUnits( GameObject root )
	{
		var height = root.GetBounds().Size.z;
		return Math.Max( MinLegHeightUnits, height * LegHeightFraction );
	}

	void HostWrapFeet( GameObject root, bool isPlayer, Vector3 hitPos )
	{
		_spent = true;
		_victimRoot = root;
		_releaseAt = Time.NowDouble + _rootSeconds;

		if ( isPlayer )
		{
			_victimPlayer = root.Components.Get<PlayerMovement>();
			_victimVitals = root.Components.Get<PlayerVitals>();
			_victimPlayer?.HostSetTrapped( true, root.WorldPosition );
			_victimVitals?.HostSetStatusEffectExpiry( BearTrap.TrappedStatusId, _releaseAt, extendOnly: false );
		}
		else
		{
			_victimEntity = root.Components.Get<EntityLocomotion>();
			_victimEntity?.HostSetTrapped( true, root.WorldPosition );
		}

		// The wrapped bola sits round the ankles and rides the victim.
		_velocity = Vector3.Zero;
		GameObject.SetParent( root, true );
		WorldScale = Vector3.One;
		WorldPosition = root.WorldPosition + Vector3.Up * 6f;
		WorldRotation = Rotation.FromPitch( 90f );
	}

	void TickHold()
	{
		var gone = _victimRoot is null || !_victimRoot.IsValid();
		if ( !gone && Time.NowDouble < _releaseAt )
			return;

		var dropAt = gone ? WorldPosition : _victimRoot.WorldPosition + Vector3.Up * 4f;
		if ( !gone )
		{
			_victimPlayer?.HostSetTrapped( false );
			_victimVitals?.HostRemoveStatusEffect( BearTrap.TrappedStatusId );
			_victimEntity?.HostSetTrapped( false );
		}

		GameObject.SetParent( null, true );
		DropPickupAndDestroy( dropAt );
	}

	void DropPickupAndDestroy( Vector3 at )
	{
		_spent = true;
		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( scene is not null && scene.IsValid() && (_authority || GameObject.Network is not { Active: true } || Networking.IsHost) )
			HostSpawnPickup( scene, at );
		GameObject.Destroy();
	}

	static void HostSpawnPickup( Scene scene, Vector3 at )
	{
		var go = new GameObject( true, "drop_" + BolaResourceId );
		go.NetworkMode = NetworkMode.Object;
		go.Parent = scene;
		go.WorldPosition = at;
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( BolaModelPath );
		var drop = go.Components.Create<WorldDroppedResource>();
		drop.Configure( BolaResourceId, 1 );
		drop.SetDespawnAfterSeconds( 120f );
		var sphere = go.Components.Create<SphereCollider>();
		sphere.Static = true;
		sphere.Radius = 6f;
		go.Tags.Add( "worlddrop" );
		HostNetworkSpawn.TrySpawn( go );
	}

	public static GameObject HostSpawn( Scene scene, Vector3 origin, Vector3 velocity, GameObject attackerRoot, float rootSeconds )
	{
		if ( scene is null || !scene.IsValid() )
			return null;

		var go = new GameObject( true, "bola_projectile" );
		go.NetworkMode = NetworkMode.Object;
		go.Parent = scene;
		go.WorldPosition = origin;
		go.WorldScale = FlightScale;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( BolaModelPath );

		var bola = go.Components.Create<BolaProjectile>();
		bola.Configure( attackerRoot, velocity, rootSeconds, hostAuthority: true );
		go.Enabled = true;
		HostNetworkSpawn.TrySpawn( go );
		return go;
	}
}
