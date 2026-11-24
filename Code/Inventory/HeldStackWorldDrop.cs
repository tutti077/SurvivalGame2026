using System;
using Sandbox;

namespace Survival;

/// <summary>Spawns physics world pickups when a held stack is dropped into the world.</summary>
public static class HeldStackWorldDrop
{
	const string DropPrefabPath = "prefabs/environment/cloverHills/rocks/rock.prefab";
	const float DefaultColliderRadius = 8f;

	public static bool TryDrop( GameObject owner, ref InventoryCursorStack held ) =>
		TryDrop( owner, ref held, Mouse.Position );

	public static bool TryDropAtPlayer( GameObject owner, ref InventoryCursorStack held, int dropCount = -1 )
	{
		if ( held.IsEmpty || owner is null || !owner.IsValid() )
			return false;

		if ( dropCount < 0 )
			dropCount = held.Count;
		else
			dropCount = Math.Clamp( dropCount, 1, held.Count );

		var scene = owner.Scene.IsValid() ? owner.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		if ( !TryGetPlayerDropSpawnTransform( owner, out var spawnPos, out var forward ) )
			return false;

		spawnPos = SnapSpawnAboveGround( scene, owner, spawnPos );

		var spawned = SpawnInventorySingles( scene, owner, held.ResourceId, dropCount, spawnPos, forward, toss: false, held.Wear, held.CrafterName, held.Tier );
		if ( spawned <= 0 )
			return false;

		held.Count -= spawned;
		if ( held.Count <= 0 )
			held.Clear();
		return true;
	}

	public static bool TryDrop( GameObject owner, ref InventoryCursorStack held, Vector2 screenPosition )
	{
		if ( held.IsEmpty || owner is null || !owner.IsValid() )
			return false;

		var scene = owner.Scene.IsValid() ? owner.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		if ( !TryBuildAimRay( owner, screenPosition, out var origin, out var direction ) )
			return false;

		var end = origin + direction * 640f;
		var trace = scene.Trace.Ray( origin, end ).IgnoreGameObjectHierarchy( owner ).Run();
		var spawnPos = trace.Hit
			? trace.HitPosition + trace.Normal * 6f
			: owner.WorldPosition + direction * 56f + Vector3.Up * 24f;

		spawnPos = SnapSpawnAboveGround( scene, owner, spawnPos );

		var spawned = SpawnInventorySingles( scene, owner, held.ResourceId, held.Count, spawnPos, direction, toss: true, held.Wear, held.CrafterName, held.Tier );
		if ( spawned <= 0 )
			return false;

		held.Count -= spawned;
		if ( held.Count <= 0 )
			held.Clear();
		return true;
	}

	/// <summary>
	/// A stack dumped out of an inventory lands as one pickup per unit, fanned out, and never clumps back together
	/// (Mark 2026-11-19: a dumped pile has to be easy to find, not "a tiny stick in the grass"). Returns how many spawned.
	/// </summary>
	static int SpawnInventorySingles( Scene scene, GameObject owner, string resourceId, int count, Vector3 spawnPos, Vector3 direction, bool toss, int wear, string crafterName, int tier )
	{
		var spreadUnits = TerrainWorldUnits.MetersToEngine( 0.2f );
		var spawned = 0;
		for ( var i = 0; i < count; i++ )
		{
			var outward = Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) ).Forward;
			var at = count > 1 ? spawnPos + outward * Sandbox.Game.Random.Float( 0f, spreadUnits ) : spawnPos;
			var instance = TrySpawnWorldDrop( scene, resourceId, 1, at, owner, inventoryDrop: true, wear: wear, crafterName: crafterName, tier: tier );
			if ( instance is null || !instance.IsValid() )
				break;

			if ( toss )
				ApplyToss( instance, direction );
			else
				ApplyPlayerRelease( instance, direction );

			if ( count > 1 && instance.Components.Get<Rigidbody>() is { } body && body.IsValid() )
				body.Velocity += outward * Sandbox.Game.Random.Float( 10f, 45f ) + Vector3.Up * Sandbox.Game.Random.Float( 0f, 20f );

			spawned++;
		}

		return spawned;
	}

	/// <summary>
	/// Spawn a physics world pickup pile (magnet pickup once it has lain on the ground 1 s).
	/// Pass <paramref name="inventoryDrop"/> only for a stack dropped out of an inventory: it waits 5 s on the
	/// ground instead, so a toss is not scooped straight back up; loot / harvest leave it false.
	/// </summary>
	public static GameObject TrySpawnWorldDrop(
		Scene scene,
		string resourceId,
		int count,
		Vector3 worldPosition,
		GameObject ignoreHierarchy = null,
		bool inventoryDrop = false,
		int wear = 0,
		string crafterName = null,
		int tier = 0,
		string sourceModelPath = null,
		float sourceScale = 1f )
	{
		if ( !scene.IsValid() || string.IsNullOrWhiteSpace( resourceId ) || count <= 0 )
			return null;

		worldPosition = SnapSpawnAboveGround( scene, ignoreHierarchy, worldPosition );

		var instance = SpawnDropPrefab( scene, resourceId, count, worldPosition, ignoreHierarchy, inventoryDrop, wear, crafterName, tier, sourceModelPath, sourceScale );
		return instance is not null && instance.IsValid() ? instance : null;
	}

	static GameObject SpawnDropPrefab(
		Scene scene,
		string resourceId,
		int count,
		Vector3 worldPosition,
		GameObject ignoreHierarchy,
		bool inventoryDrop,
		int wear = 0,
		string crafterName = null,
		int tier = 0,
		string sourceModelPath = null,
		float sourceScale = 1f )
	{
		var instance = BuildPrefabUtility.GetTemplate( DropPrefabPath )?.Clone();
		if ( instance is null || !instance.IsValid() )
			instance = CreateFallbackDropObject( scene, resourceId );

		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[HeldStackWorldDrop] Failed to spawn drop for {count}x {resourceId}." );
			return null;
		}

		instance.NetworkMode = NetworkMode.Object;
		instance.Parent = scene;
		instance.Name = $"drop_{ResourceCatalog.NormalizeResourceId( resourceId )}";
		instance.WorldPosition = worldPosition;
		instance.WorldRotation = Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) );

		var scale = 0.24f + Sandbox.Game.Random.Float( 0f, 0.1f );
		instance.LocalScale = new Vector3( scale, scale, scale );

		PrepareSpawnedDrop( instance, resourceId, count, inventoryDrop, wear, crafterName, tier, sourceModelPath, sourceScale );
		instance.Enabled = true;
		HostNetworkSpawn.TrySpawn( instance );
		return instance;
	}

	static GameObject CreateFallbackDropObject( Scene scene, string resourceId )
	{
		var go = new GameObject( true, $"drop_{ResourceCatalog.NormalizeResourceId( resourceId )}" );
		go.Parent = scene;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( "models/dev/sphere.vmdl" );
		renderer.Tint = ResourceCatalog.Resolve( resourceId ).FallbackColor.WithAlpha( 1f );
		return go;
	}

	static void PrepareSpawnedDrop(
		GameObject instance,
		string resourceId,
		int count,
		bool inventoryDrop,
		int wear = 0,
		string crafterName = null,
		int tier = 0,
		string sourceModelPath = null,
		float sourceScale = 1f )
	{
		foreach ( var definition in instance.Components.GetAll<ResourceItemDefinition>( FindMode.EverythingInSelf ) )
			definition?.Destroy();

		var drop = instance.Components.Get<WorldDroppedResource>() ?? instance.Components.Create<WorldDroppedResource>();
		drop.Configure( resourceId, count, wear, crafterName, tier );
		if ( inventoryDrop )
			drop.MarkInventoryDrop();

		ApplyResourceVisual( instance, resourceId, sourceModelPath, sourceScale );
		EnsurePhysics( instance );
		instance.Tags.Add( "worlddrop" );
		if ( !inventoryDrop )
			WorldDroppedResourceMerge.TryMergeCluster( drop );
	}

	static void ApplyResourceVisual( GameObject instance, string resourceId, string sourceModelPath = null, float sourceScale = 1f )
	{
		var renderer = instance.Components.Get<ModelRenderer>();
		if ( renderer is null )
			return;

		// A plant picked by hand drops as the plant that stood there (Mark 2026-11-18: "a 1:1 placed model vs physics model
		// after you harvest it"): the same model at the same size, when that model is one of the item's listed world models
		// (a mushroom's cap drops as that mushroom; the daisy seed that comes with a daisy keeps its own model).
		if ( ItemWorldModelCatalog.HasVariant( resourceId, sourceModelPath ) && Model.Load( sourceModelPath ) is { IsError: false } source )
		{
			renderer.Model = source;
			renderer.MaterialOverride = null;
			renderer.Tint = Color.White;
			instance.LocalScale = Vector3.One * Math.Clamp( sourceScale, 0.25f, 4f );
			return;
		}

		// The item's own model when it has one (40 u/m: drawn at scale 1, untinted; an id with variants picks one per drop); else the tinted dev rock
		if ( ItemWorldModelCatalog.TryGetModelPath( resourceId, out var modelPath, Sandbox.Game.Random.Int( 0, 1 << 20 ) ) && Model.Load( modelPath ) is { IsError: false } model )
		{
			renderer.Model = model;
			renderer.MaterialOverride = null;
			renderer.Tint = Color.White;
			instance.LocalScale = Vector3.One;
			return;
		}

		var def = ResourceCatalog.Resolve( resourceId );
		renderer.Tint = def.FallbackColor.WithAlpha( 1f );
	}

	static void EnsurePhysics( GameObject root )
	{
		foreach ( var collider in root.Components.GetAll<Collider>( FindMode.EverythingInSelf ) )
		{
			if ( collider is null )
				continue;

			if ( collider is ModelCollider )
			{
				collider.Destroy();
				continue;
			}

			if ( collider.IsTrigger )
				continue;

			collider.Destroy();
		}

		// An item model collides with its own convex hull (every item vmdl carries PhysicsHullFromRender), so a sword lies
		// flat and a pouch sits on its base - the old sphere off the biggest dimension held thin items up in the air
		// (Mark 2026-11-05: "they just kinda float"). Only the tinted fallback rock keeps a sphere.
		var model = root.Components.Get<ModelRenderer>()?.Model;
		Collider solid;
		if ( model is { IsError: false, Physics.Parts.Count: > 0 } )
		{
			var hull = root.Components.Create<ModelCollider>();
			hull.Model = model;
			solid = hull;
		}
		else
		{
			var sphere = root.Components.Create<SphereCollider>();
			sphere.Radius = EstimateColliderRadius( root );
			solid = sphere;
		}

		solid.Static = false;
		solid.Friction = 0.85f;
		solid.Elasticity = 0.05f;

		var body = root.Components.Get<Rigidbody>() ?? root.Components.Create<Rigidbody>();
		body.MotionEnabled = true;
		body.Gravity = true;
		body.GravityScale = 1f;
		body.EnableImpactDamage = false;
		body.MassOverride = 1.5f;
		body.LinearDamping = 0.45f;
		body.AngularDamping = 3.5f;
		body.SleepThreshold = 12f;
		body.StartAsleep = false;
		body.ResetInertiaTensor();
	}

	static float EstimateColliderRadius( GameObject root )
	{
		var renderer = root.Components.Get<ModelRenderer>();
		if ( renderer?.Model is not null )
		{
			var bounds = renderer.Model.Bounds;
			var scale = root.WorldScale;
			var size = new Vector3(
				bounds.Size.x * MathF.Abs( scale.x ),
				bounds.Size.y * MathF.Abs( scale.y ),
				bounds.Size.z * MathF.Abs( scale.z ) );
			var radius = MathF.Max( size.x, MathF.Max( size.y, size.z ) ) * 0.34f;
			if ( radius > 1f )
				return Math.Clamp( radius, 5f, 14f );
		}

		return DefaultColliderRadius;
	}

	static Vector3 SnapSpawnAboveGround( Scene scene, GameObject ignore, Vector3 desiredPos )
	{
		if ( !scene.IsValid() )
			return desiredPos;

		var start = desiredPos + Vector3.Up * TerrainWorldUnits.MetersToEngine( 2f );
		var end = desiredPos - Vector3.Up * TerrainWorldUnits.MetersToEngine( 4f );
		var trace = scene.Trace.Ray( start, end ).IgnoreGameObjectHierarchy( ignore ).Run();
		if ( !trace.Hit )
			return desiredPos;

		return trace.HitPosition + trace.Normal * TerrainWorldUnits.MetersToEngine( 0.08f );
	}

	static void ApplyPlayerRelease( GameObject instance, Vector3 forward )
	{
		var body = instance.Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return;

		var dir = forward.WithZ( 0 ).Normal;
		if ( dir.LengthSquared < 1e-8f )
			dir = Vector3.Forward;

		body.Velocity = dir * Sandbox.Game.Random.Float( 6f, 18f )
		                + Vector3.Up * Sandbox.Game.Random.Float( -4f, 10f );
		body.AngularVelocity = Vector3.Zero;
	}

	/// <summary>
	/// Host: harvest loot as single pickups tossed out around <paramref name="origin"/> (hand picking, nests):
	/// it lands, and every pawn's magnet takes it 1 s later.
	/// </summary>
	public static void SpawnLootBurst( Scene scene, HarvestLootItem[] loot, Vector3 origin, GameObject ignoreHierarchy, float minRadius = 6f, float maxRadius = 20f, string sourceModelPath = null, float sourceScale = 1f )
	{
		if ( !scene.IsValid() || loot is null )
			return;

		foreach ( var item in loot )
		{
			if ( item.Amount <= 0 || string.IsNullOrWhiteSpace( item.ResourceId ) )
				continue;

			for ( var i = 0; i < item.Amount; i++ )
			{
				var outward = Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) ).Forward;
				var at = origin + outward * Sandbox.Game.Random.Float( minRadius, maxRadius );
				var instance = TrySpawnWorldDrop( scene, item.ResourceId, 1, at, ignoreHierarchy, sourceModelPath: sourceModelPath, sourceScale: sourceScale );
				if ( instance is not null && instance.IsValid() )
					ApplyScatterBurst( instance, outward );
			}
		}
	}

	/// <summary>Give loot singles a short outward toss so a burst doesn’t land as one pile.</summary>
	public static void ApplyScatterBurst( GameObject instance, Vector3 outward )
	{
		var body = instance.Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return;

		var dir = outward.WithZ( 0 );
		if ( dir.LengthSquared < 1e-8f )
			dir = Vector3.Forward;
		else
			dir = dir.Normal;

		body.MotionEnabled = true;
		body.Velocity = dir * Sandbox.Game.Random.Float( 25f, 55f )
		                + Vector3.Up * Sandbox.Game.Random.Float( 15f, 40f );
		body.AngularVelocity = new Vector3(
			Sandbox.Game.Random.Float( -40f, 40f ),
			Sandbox.Game.Random.Float( -40f, 40f ),
			Sandbox.Game.Random.Float( -40f, 40f ) );
	}

	static bool TryGetPlayerDropSpawnTransform( GameObject owner, out Vector3 spawnPos, out Vector3 forward )
	{
		spawnPos = default;
		if ( !BuildViewCamera.TryGetHorizontalFacingForward( owner, out forward ) )
			return false;

		spawnPos = owner.WorldPosition
		           + forward * TerrainWorldUnits.MetersToEngine( 1f )
		           + Vector3.Up * TerrainWorldUnits.MetersToEngine( 1.05f );
		return true;
	}

	static void ApplyToss( GameObject instance, Vector3 throwDirection )
	{
		var body = instance.Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return;

		var dir = throwDirection.Normal;
		if ( dir.LengthSquared < 1e-8f )
			dir = Vector3.Forward;

		body.Velocity = dir * Sandbox.Game.Random.Float( 35f, 70f )
		                + Vector3.Up * Sandbox.Game.Random.Float( 18f, 40f );
		body.AngularVelocity = new Vector3(
			Sandbox.Game.Random.Float( -35f, 35f ),
			Sandbox.Game.Random.Float( -35f, 35f ),
			Sandbox.Game.Random.Float( -35f, 35f ) );
	}

	static bool TryBuildAimRay( GameObject owner, Vector2 screenPosition, out Vector3 origin, out Vector3 direction )
	{
		origin = default;
		direction = default;

		var cam = BuildViewCamera.Resolve( owner );
		if ( !cam.IsValid() )
			return BuildViewCamera.TryGetViewRay( owner, out origin, out direction );

		origin = cam.WorldPosition;

		var screenSize = Screen.Size;
		if ( screenSize.x < 1f || screenSize.y < 1f )
		{
			direction = cam.WorldRotation.Forward.Normal;
			return direction.LengthSquared > 1e-8f;
		}

		var ndcX = screenPosition.x / screenSize.x * 2f - 1f;
		var ndcY = 1f - screenPosition.y / screenSize.y * 2f;
		var fovRad = Math.Clamp( cam.FieldOfView, 20f, 110f ) * MathF.PI / 180f;
		var aspect = screenSize.x / screenSize.y;
		var tanHalfFov = MathF.Tan( fovRad * 0.5f );

		direction = (
			cam.WorldRotation.Forward
			+ cam.WorldRotation.Right * ( ndcX * tanHalfFov * aspect )
			+ cam.WorldRotation.Up * ( ndcY * tanHalfFov )
		).Normal;

		return direction.LengthSquared > 1e-8f;
	}
}
