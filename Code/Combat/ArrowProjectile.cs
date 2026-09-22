using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-simulated arrow. Clients see the networked transform; only the host applies damage.
/// On impact the host destroys the flyer and spawns a stuck brown-cylinder pickup
/// (<see cref="WorldDroppedResource"/>) that can be magnet / E recovered. Hits on anything
/// damageable (entity, player, tree, build piece) parent the pickup to the hit object via
/// <see cref="StuckArrow"/>, so it rides along and drops with physics when the victim dies.
/// </summary>
[Title( "Arrow Projectile" )]
public sealed class ArrowProjectile : Component
{
	const int TrailPointCount = 12;
	const string CylinderModelPath = "models/cylinder.vmdl";
	const string FallbackBoxModelPath = "models/dev/box.vmdl";

	[Property] public float LifetimeSeconds { get; set; } = 12f;
	[Property] public float Radius { get; set; } = 2.5f;

	GameObject _attackerRoot;
	Component _attackerCombat;
	Vector3 _velocity;
	float _damage;
	float _age;
	bool _spent;
	bool _authority;
	string _ammoResourceId = "arrow_wood";
	float _shaftLength;

	readonly Vector3[] _trail = new Vector3[TrailPointCount];
	int _trailCount;
	int _trailWrite;
	Vector3 _lastTrailSample;
	bool _hasTrailSample;

	public void Configure(
		GameObject attackerRoot,
		Component attackerCombat,
		Vector3 velocity,
		float damage,
		bool hostAuthority,
		string ammoResourceId,
		float shaftLength )
	{
		_attackerRoot = attackerRoot;
		_attackerCombat = attackerCombat;
		_velocity = velocity;
		_damage = Math.Max( 0f, damage );
		_authority = hostAuthority;
		_ammoResourceId = string.IsNullOrWhiteSpace( ammoResourceId )
			? "arrow_wood"
			: ResourceCatalog.NormalizeResourceId( ammoResourceId );
		_shaftLength = Math.Max( 8f, shaftLength );
		_age = 0f;
		_spent = false;
		_trailCount = 0;
		_trailWrite = 0;
		_hasTrailSample = false;

		if ( velocity.LengthSquared > 1e-6f )
			WorldRotation = Rotation.LookAt( velocity.Normal, Vector3.Up );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( _spent || !GameObject.IsValid() )
			return;

		SampleAndDrawTrail();
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();

		if ( _spent || !GameObject.IsValid() )
			return;

		// Only the host (or offline) advances physics + hits. Proxies just display synced transform.
		if ( !_authority && GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		var dt = Math.Max( 0f, Time.Delta );
		if ( dt <= 1e-6f )
			return;

		_age += dt;
		if ( _age >= LifetimeSeconds )
		{
			// Lost in flight — no recovery.
			DestroySelf();
			return;
		}

		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
		{
			DestroySelf();
			return;
		}

		var gravity = scene.PhysicsWorld?.Gravity ?? new Vector3( 0f, 0f, -800f );
		_velocity += gravity * dt;

		var start = WorldPosition;
		var delta = _velocity * dt;
		var end = start + delta;

		var trace = scene.Trace.Ray( start, end )
			.Radius( Math.Max( 0.5f, Radius ) )
			.IgnoreGameObjectHierarchy( GameObject );

		if ( _attackerRoot is not null && _attackerRoot.IsValid() )
			trace = trace.IgnoreGameObjectHierarchy( _attackerRoot );

		var tr = trace.Run();
		if ( tr.Hit )
		{
			TryApplyHit( tr );
			HostEmbedAsPickup( scene, tr );
			DestroySelf();
			return;
		}

		WorldPosition = end;
		if ( _velocity.LengthSquared > 1e-6f )
			WorldRotation = Rotation.LookAt( _velocity.Normal, Vector3.Up );
	}

	void HostEmbedAsPickup( Scene scene, SceneTraceResult tr )
	{
		if ( !_authority && GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		var flightDir = _velocity.LengthSquared > 1e-6f
			? _velocity.Normal
			: WorldRotation.Forward;
		if ( flightDir.LengthSquared < 1e-6f && tr.Normal.LengthSquared > 1e-6f )
			flightDir = -tr.Normal;
		if ( flightDir.LengthSquared < 1e-6f )
			flightDir = Vector3.Forward;
		flightDir = flightDir.Normal;

		// Embed ~35% of the shaft into the surface so it reads as stuck.
		var pos = tr.HitPosition - flightDir * ( _shaftLength * 0.35f );
		var rot = Rotation.LookAt( flightDir, Vector3.Up );

		// Damageable things move or die — ride the hit object. Static world stays scene-rooted.
		GameObject hitObject = null;
		DamageReceiver receiver = null;
		var victimDead = false;
		if ( tr.GameObject.IsValid()
		     && CombatAuthority.TryFindDamageable( tr.GameObject, out var found )
		     && found is DamageReceiver dmg )
		{
			// The killing arrow lands on something that just died — its death event already
			// fired, so never attach; drop it loose right away instead.
			victimDead = !CombatAuthority.IsDamageVictimAlive( dmg );
			if ( !victimDead )
			{
				hitObject = tr.GameObject;
				receiver = dmg;
			}
		}

		var stuck = HostSpawnStuckPickup( scene, pos, rot, _ammoResourceId, _shaftLength, hitObject, receiver );
		if ( victimDead && stuck.IsValid() )
			ApplyLoosePhysics( stuck, _shaftLength, Math.Max( 1.5f, _shaftLength * 0.05f ) );
	}

	/// <summary>
	/// Fresh networked pickup — created before NetworkSpawn so clients get the full setup.
	/// Pass <paramref name="hitObject"/> + <paramref name="receiver"/> to embed in a living thing.
	/// </summary>
	public static GameObject HostSpawnStuckPickup(
		Scene scene,
		Vector3 position,
		Rotation rotation,
		string ammoResourceId,
		float shaftLength,
		GameObject hitObject,
		DamageReceiver receiver )
	{
		if ( scene is null || !scene.IsValid() )
			return null;

		ammoResourceId = string.IsNullOrWhiteSpace( ammoResourceId )
			? "arrow_wood"
			: ResourceCatalog.NormalizeResourceId( ammoResourceId );
		shaftLength = Math.Max( 8f, shaftLength );
		var thickness = Math.Max( 1.5f, shaftLength * 0.05f );

		var go = new GameObject( true, $"stuck_{ammoResourceId}" );
		go.NetworkMode = NetworkMode.Object;
		go.Parent = scene;
		go.WorldPosition = position;
		go.WorldRotation = rotation;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = LoadArrowModel();
		renderer.Tint = new Color( 0.45f, 0.28f, 0.14f );
		ApplyShaftScale( go, renderer.Model?.Bounds.Size ?? new Vector3( 1f ), shaftLength, thickness );

		var drop = go.Components.Create<WorldDroppedResource>();
		drop.Configure( ammoResourceId, 1 );
		drop.PreventMerge = true;
		drop.SetDespawnAfterSeconds( 60f );

		go.Tags.Add( "worlddrop" );

		if ( hitObject.IsValid() && receiver is not null )
		{
			// No collider while riding a victim: a child collider would fold into the entity's
			// rigidbody and clutter its traces. Pickup is registry-driven, not collider-driven.
			var stuck = go.Components.Create<StuckArrow>();
			stuck.HostAttach( hitObject, receiver, shaftLength, thickness );
		}
		else
		{
			var sphere = go.Components.Create<SphereCollider>();
			sphere.Static = true;
			sphere.Radius = Math.Max( 3f, shaftLength * 0.22f );
			sphere.Friction = 1f;
		}

		go.Enabled = true;
		HostNetworkSpawn.TrySpawn( go );
		return go;
	}

	/// <summary>
	/// Host: turn a stuck pickup into a loose one — shaft-sized dynamic box collider + rigidbody
	/// so it tumbles to the ground. Used when the thing it was stuck in dies.
	/// </summary>
	public static void ApplyLoosePhysics( GameObject go, float shaftLength, float thickness )
	{
		if ( !go.IsValid() )
			return;

		shaftLength = Math.Max( 8f, shaftLength );
		thickness = Math.Max( 1.5f, thickness );

		foreach ( var col in go.Components.GetAll<Collider>( FindMode.EverythingInSelf ) )
			col.Destroy();

		// Box in unscaled model space — GameObject.LocalScale already stretches it to the shaft.
		var modelSize = go.Components.Get<ModelRenderer>()?.Model?.Bounds.Size ?? new Vector3( 1f );
		var box = go.Components.Create<BoxCollider>();
		box.Static = false;
		box.Scale = new Vector3(
			Math.Max( 0.01f, modelSize.x ),
			Math.Max( 0.01f, modelSize.y ),
			Math.Max( 0.01f, modelSize.z ) );
		box.Friction = 0.9f;
		box.Elasticity = 0.05f;

		var body = go.Components.Get<Rigidbody>() ?? go.Components.Create<Rigidbody>();
		body.MotionEnabled = true;
		body.Gravity = true;
		body.GravityScale = 1f;
		body.EnableImpactDamage = false;
		body.MassOverride = 0.3f;
		body.LinearDamping = 0.3f;
		body.AngularDamping = 2f;
		body.SleepThreshold = 12f;
		body.StartAsleep = false;
		body.ResetInertiaTensor();

		// Small outward pop so it visibly comes free instead of sliding straight down.
		body.Velocity = go.WorldRotation.Backward * 20f + Vector3.Up * 25f;
		body.AngularVelocity = Vector3.Random * 3f;
	}

	void SampleAndDrawTrail()
	{
		var pos = WorldPosition;
		const float minSampleDist = 4f;
		if ( !_hasTrailSample || (pos - _lastTrailSample).Length >= minSampleDist )
		{
			_trail[_trailWrite] = pos;
			_trailWrite = (_trailWrite + 1) % TrailPointCount;
			if ( _trailCount < TrailPointCount )
				_trailCount++;
			_lastTrailSample = pos;
			_hasTrailSample = true;
		}

		if ( _trailCount < 2 )
			return;

		for ( var i = 1; i < _trailCount; i++ )
		{
			var aIndex = (_trailWrite - _trailCount + i - 1 + TrailPointCount * 2) % TrailPointCount;
			var bIndex = (_trailWrite - _trailCount + i + TrailPointCount * 2) % TrailPointCount;
			var t = i / (float)_trailCount;
			var alpha = MathX.Lerp( 0.12f, 0.55f, t );
			var grey = MathX.Lerp( 0.55f, 0.95f, t );
			var color = new Color( grey, grey, grey, alpha );
			DebugOverlay.Line( _trail[aIndex], _trail[bIndex], color, 0f );
		}
	}

	void TryApplyHit( SceneTraceResult tr )
	{
		if ( !_authority && GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( _damage <= 0f || !tr.GameObject.IsValid() )
			return;

		if ( !CombatAuthority.TryFindDamageable( tr.GameObject, out var recv ) || recv is not DamageReceiver dmg )
			return;

		if ( _attackerRoot is not null && _attackerRoot.IsValid()
		     && !CombatAuthority.MayApplyMeleeDamageFromAttackerToReceiver( _attackerRoot, dmg ) )
			return;

		if ( !CombatAuthority.IsDamageVictimAlive( dmg ) )
			return;

		dmg.TakeDamage( _damage, _attackerCombat );
	}

	void DestroySelf()
	{
		_spent = true;
		if ( GameObject.IsValid() )
			GameObject.Destroy();
	}

	/// <summary>Host/offline: spawn a networked arrow and start simulation.</summary>
	public static GameObject HostSpawn(
		Scene scene,
		Vector3 origin,
		Vector3 velocity,
		float damage,
		GameObject attackerRoot,
		Component attackerCombat,
		string ammoResourceId )
	{
		if ( scene is null || !scene.IsValid() )
			return null;

		var go = new GameObject( true, "arrow_projectile" );
		go.NetworkMode = NetworkMode.Object;
		go.Parent = scene;
		go.WorldPosition = origin;
		if ( velocity.LengthSquared > 1e-6f )
			go.WorldRotation = Rotation.LookAt( velocity.Normal, Vector3.Up );

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = LoadArrowModel();
		renderer.Tint = new Color( 0.45f, 0.28f, 0.14f );

		var unitsPerMeter = 40f;
		var controller = attackerRoot?.Components.Get<PlayerController>();
		if ( controller is not null && controller.IsValid() )
			unitsPerMeter = Math.Max( 24f, controller.BodyHeight ) / 1.8f;

		var length = 0.95f * unitsPerMeter;
		var thickness = 0.045f * unitsPerMeter;
		ApplyShaftScale( go, renderer.Model?.Bounds.Size ?? new Vector3( 1f ), length, thickness );

		var proj = go.Components.Create<ArrowProjectile>();
		var isHost = !Networking.IsActive || Networking.IsHost;
		proj.Configure(
			attackerRoot,
			attackerCombat,
			velocity,
			damage,
			hostAuthority: isHost,
			ammoResourceId: ammoResourceId,
			shaftLength: length );
		proj.Radius = Math.Max( 1.2f, thickness * 0.75f );

		go.Enabled = true;
		HostNetworkSpawn.TrySpawn( go );
		return go;
	}

	static Model LoadArrowModel()
	{
		var model = Model.Load( CylinderModelPath );
		if ( model is not null )
			return model;

		return Model.Load( FallbackBoxModelPath );
	}

	static void ApplyShaftScale( GameObject go, Vector3 modelSize, float length, float thickness )
	{
		var modelX = Math.Max( 0.01f, modelSize.x );
		var modelY = Math.Max( 0.01f, modelSize.y );
		var modelZ = Math.Max( 0.01f, modelSize.z );

		// LookAt aims local +X along flight — length on X, thickness on Y/Z.
		go.LocalScale = new Vector3( length / modelX, thickness / modelY, thickness / modelZ );
	}
}
