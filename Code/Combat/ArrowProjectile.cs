using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-simulated arrow. Clients see the networked transform; only the host applies damage.
/// On impact the host spawns a stuck pickup drawn as the ammo's own item model (<see cref="AttachArrowVisual"/>,
/// a <see cref="WorldDroppedResource"/>) that can be magnet / E recovered. Hits on anything
/// damageable (entity, player, tree, build piece) parent the pickup to the hit object via
/// <see cref="StuckArrow"/>, so it rides along and drops with physics when the victim dies.
/// Every peer draws its flight streak and landing star with an <see cref="ArrowFlightTrail"/>: the host syncs the
/// shooter and the landing (<see cref="ImpactKind"/> / <see cref="ImpactPosition"/>; a creature or player hit also
/// gives the shooter's crosshair its hit tick) and keeps the spent, hidden arrow a moment so that state reaches the
/// clients before the object goes.
/// </summary>
[Title( "Arrow Projectile" )]
public sealed class ArrowProjectile : Component
{
	const string CylinderModelPath = "models/cylinder.vmdl";
	const string FallbackBoxModelPath = "models/dev/box.vmdl";

	/// <summary>Arrow shaft as drawn in flight, stuck and nocked on the bow (m).</summary>
	public const float ShaftLengthMeters = 0.95f;
	public const float ShaftThicknessMeters = 0.045f;
	static readonly Color ShaftTint = new( 0.45f, 0.28f, 0.14f );

	/// <summary>Host keeps a landed arrow (hidden) this long so the landing syncs before the object is gone.</summary>
	const float SpentLingerSeconds = 0.3f;

	public const byte ImpactNone = 0;
	public const byte ImpactMiss = 1;
	public const byte ImpactHitCreature = 2;

	/// <summary>Pawn that loosed it (its root object id) - the shooter's own arc lingers on their screen.</summary>
	[Sync( SyncFlags.FromHost )] public Guid ShooterId { get; set; }

	/// <summary>Host to all: how the flight ended (<see cref="ImpactMiss"/> / <see cref="ImpactHitCreature"/>), 0 in flight.</summary>
	[Sync( SyncFlags.FromHost )] public byte ImpactKind { get; set; }

	[Sync( SyncFlags.FromHost )] public Vector3 ImpactPosition { get; set; }

	[Property] public float LifetimeSeconds { get; set; } = 12f;
	[Property] public float Radius { get; set; } = 2.5f;

	GameObject _attackerRoot;
	Component _attackerCombat;
	Vector3 _velocity;
	float _damage;
	float _burnDamage;
	float _burnSeconds;
	float _age;
	bool _spent;
	bool _authority;
	string _ammoResourceId = "arrow_wood";
	float _shaftLength;
	float _spentAge;

	ArrowFlightTrail _trail;
	bool _trailOwnResolved;

	public void Configure(
		GameObject attackerRoot,
		Component attackerCombat,
		Vector3 velocity,
		float damage,
		bool hostAuthority,
		string ammoResourceId,
		float shaftLength,
		float burnDamage = 0f,
		float burnSeconds = 0f )
	{
		_attackerRoot = attackerRoot;
		_attackerCombat = attackerCombat;
		_velocity = velocity;
		_damage = Math.Max( 0f, damage );
		_burnDamage = Math.Max( 0f, burnDamage );
		_burnSeconds = Math.Max( 0f, burnSeconds );
		_authority = hostAuthority;
		_ammoResourceId = string.IsNullOrWhiteSpace( ammoResourceId )
			? "arrow_wood"
			: ResourceCatalog.NormalizeResourceId( ammoResourceId );
		_shaftLength = Math.Max( 8f, shaftLength );
		_age = 0f;
		_spent = false;

		if ( velocity.LengthSquared > 1e-6f )
			WorldRotation = Rotation.LookAt( velocity.Normal, Vector3.Up );
	}

	protected override void OnStart()
	{
		base.OnStart();
		_trail = ArrowFlightTrail.Create( Scene );
		_trail?.Push( WorldPosition );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !GameObject.IsValid() || _trail is null || !_trail.IsValid() || _trail.Ended )
			return;

		ResolveTrailOwner();

		if ( ImpactKind != ImpactNone )
		{
			// Landed (synced from the host): the star pops at the impact, hide the spent shaft here too.
			if ( Components.Get<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) is { Enabled: true } shaft )
				shaft.Enabled = false;
			_trail.End( ImpactPosition );
			if ( ImpactKind == ImpactHitCreature && _trail.IsOwn )
				ResolveShooterCombat()?.NotifyOwnArrowHit();
			return;
		}

		_trail.Push( WorldPosition );
	}

	protected override void OnDestroy()
	{
		// Lost in flight, or gone before the landing synced: the streak just fades, no star.
		if ( _trail is { IsValid: true } && !_trail.Ended )
			_trail.End( null );
		base.OnDestroy();
	}

	PlayerCombat ResolveShooterCombat()
	{
		if ( ShooterId == Guid.Empty || !Scene.IsValid() )
			return null;

		return Scene.Directory.FindByGuid( ShooterId )?.Components.Get<PlayerCombat>();
	}

	/// <summary>Until found: is the shooter the pawn this peer drives?</summary>
	void ResolveTrailOwner()
	{
		if ( _trailOwnResolved || ShooterId == Guid.Empty )
			return;

		var combat = ResolveShooterCombat();
		if ( combat is null )
			return;

		_trail.IsOwn = combat.IsLocalCombatDriver();
		_trailOwnResolved = true;
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();

		if ( !GameObject.IsValid() )
			return;

		// Only the host (or offline) advances physics + hits. Proxies just display synced transform.
		if ( !_authority && GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		var dt = Math.Max( 0f, Time.Delta );
		if ( dt <= 1e-6f )
			return;

		if ( _spent )
		{
			_spentAge += dt;
			if ( _spentAge >= SpentLingerSeconds )
				GameObject.Destroy();
			return;
		}

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
			var hitCreature = TryApplyHit( tr );
			HostEmbedAsPickup( scene, tr );
			HostLand( tr.HitPosition, hitCreature );
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

		AttachArrowVisual( go, ammoResourceId, shaftLength, thickness );

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

		// The pickup root is unscaled with the shaft along +X (the visual is a child): a shaft-sized box.
		var box = go.Components.Create<BoxCollider>();
		box.Static = false;
		box.Scale = new Vector3( shaftLength, thickness, thickness );
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

	/// <summary>Host: damage what it struck. True when that was a living creature or player (the shooter's hit tick).</summary>
	bool TryApplyHit( SceneTraceResult tr )
	{
		if ( !_authority && GameObject.Network is { Active: true } && !Networking.IsHost )
			return false;

		if ( _damage <= 0f || !tr.GameObject.IsValid() )
			return false;

		if ( !CombatAuthority.TryFindDamageable( tr.GameObject, out var recv ) || recv is not DamageReceiver dmg )
			return false;

		if ( _attackerRoot is not null && _attackerRoot.IsValid()
		     && !CombatAuthority.MayApplyMeleeDamageFromAttackerToReceiver( _attackerRoot, dmg ) )
			return false;

		if ( !CombatAuthority.IsDamageVictimAlive( dmg ) )
			return false;

		var creature = CombatAuthority.ResolveEntityVitalsForDamageReceiver( dmg ) is not null
		               || CombatAuthority.ResolvePlayerVitalsForDamageReceiver( dmg ) is not null;
		using ( PlaytestLog.Kind( "arrow" ) )
			dmg.TakeDamage( _damage, _attackerCombat );
		if ( creature && _burnDamage > 0f )
			BurnEffect.HostApply( dmg, _burnDamage, _burnSeconds, _attackerCombat );
		return creature;
	}

	/// <summary>Host: the flight is over - publish where and how it landed, hide the shaft, despawn shortly.</summary>
	void HostLand( Vector3 at, bool hitCreature )
	{
		_spent = true;
		_spentAge = 0f;
		WorldPosition = at;
		ImpactPosition = at;
		ImpactKind = hitCreature ? ImpactHitCreature : ImpactMiss;
		if ( Components.Get<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) is { } shaft )
			shaft.Enabled = false;
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

		var unitsPerMeter = 40f;
		var controller = attackerRoot?.Components.Get<PlayerController>();
		if ( controller is not null && controller.IsValid() )
			unitsPerMeter = Math.Max( 24f, controller.BodyHeight ) / 1.8f;

		var length = ShaftLengthMeters * unitsPerMeter;
		var thickness = ShaftThicknessMeters * unitsPerMeter;
		AttachArrowVisual( go, ammoResourceId, length, thickness );

		var proj = go.Components.Create<ArrowProjectile>();
		proj.ShooterId = attackerRoot.IsValid() ? attackerRoot.Id : Guid.Empty;
		var isHost = !Networking.IsActive || Networking.IsHost;
		AmmoCatalog.TryGetAmmoBurn( ammoResourceId, out var burnDamage, out var burnSeconds );
		proj.Configure(
			attackerRoot,
			attackerCombat,
			velocity,
			damage,
			hostAuthority: isHost,
			ammoResourceId: ammoResourceId,
			shaftLength: length,
			burnDamage: burnDamage, burnSeconds: burnSeconds );
		proj.Radius = Math.Max( 1.2f, thickness * 0.75f );

		go.Enabled = true;
		HostNetworkSpawn.TrySpawn( go );
		return go;
	}

	/// <summary>
	/// The arrow's look, as a child of <paramref name="parent"/>: the ammo's item model (<c>item_world_models.json</c>,
	/// built with its length along model +Y and the head at the +Y end) turned so the shaft runs along the parent's +X,
	/// scaled to <paramref name="lengthUnits"/> and centred on the parent. No item model: the old brown cylinder.
	/// The parent stays unscaled. Used by the flying arrow, the stuck pickup and the arrow nocked on the bow.
	/// </summary>
	internal static ModelRenderer AttachArrowVisual( GameObject parent, string ammoResourceId, float lengthUnits, float thicknessUnits )
	{
		var child = new GameObject( true, "arrow_visual" );
		child.Parent = parent;
		child.Tags.Add( "ignore" );
		var renderer = child.Components.Create<ModelRenderer>();

		Model model = null;
		if ( ItemWorldModelCatalog.TryGetModelPath( ammoResourceId, out var path ) )
			model = Model.Load( path );

		if ( model is { IsError: false } && model.Bounds.Size.y > 0.01f )
		{
			renderer.Model = model;
			var s = lengthUnits / model.Bounds.Size.y;
			child.LocalRotation = Rotation.FromYaw( -90f );
			child.LocalScale = new Vector3( s );
			child.LocalPosition = -(child.LocalRotation * (model.Bounds.Center * s));
			return renderer;
		}

		model = Model.Load( CylinderModelPath ) ?? Model.Load( FallbackBoxModelPath );
		renderer.Model = model;
		renderer.Tint = ShaftTint;
		var size = model?.Bounds.Size ?? Vector3.One;
		var scale = new Vector3(
			lengthUnits / Math.Max( 0.01f, size.x ),
			thicknessUnits / Math.Max( 0.01f, size.y ),
			thicknessUnits / Math.Max( 0.01f, size.z ) );
		child.LocalScale = scale;
		child.LocalPosition = -((model?.Bounds.Center ?? Vector3.Zero) * scale);
		return renderer;
	}
}
