using Sandbox;

namespace Survival;

/// <summary>
/// Host: stamps an <see cref="EnemyCampData"/> layout into the world — build pieces through the
/// shared host placement path, then the guarding entities. The debug key and (later) biome
/// population both end here.
/// </summary>
public static class EnemyCampSpawner
{
	/// <summary>
	/// Spawn <paramref name="campId"/> with its origin dropped onto the ground under
	/// <paramref name="origin"/>. Returns false only when nothing at all was spawned.
	/// </summary>
	public static bool TrySpawn( Scene scene, string campId, Vector3 origin, float yawDegrees )
	{
		if ( !scene.IsValid() )
			return false;

		if ( !EnemyCampCatalog.TryGet( campId, out var camp ) )
		{
			Log.Warning( $"[EnemyCampSpawner] Unknown camp '{campId}'." );
			return false;
		}

		BuildNavMeshSync.EnsureBuildTraversalSettings( scene );

		var ground = FindGround( scene, origin );
		var campRotation = Rotation.FromYaw( yawDegrees );

		var placed = 0;
		for ( var i = 0; i < camp.Pieces.Count; i++ )
		{
			var piece = camp.Pieces[i];
			if ( piece is null || string.IsNullOrWhiteSpace( piece.PieceId ) )
				continue;

			var rotation = Rotation.FromYaw( yawDegrees + piece.YawDegrees );
			var position = ground + EnemyCampLayout.ToWorldOffset( piece.X, piece.Y, piece.Z, campRotation );

			// Pieces sit on the camp's ground plane the same way a hammer-placed piece sits on terrain.
			position += Vector3.Up * BuildModuleDimensions.GetGroundSitHalfExtent( piece.PieceId, rotation );

			if ( BuildAuthority.HostPlacePiece( scene, piece.PieceId, new Transform( position, rotation ), piece.Blueprint, out _ ) )
				placed++;
			else
				Log.Warning( $"[EnemyCampSpawner] Camp '{camp.Id}': failed to place piece '{piece.PieceId}'." );
		}

		var spawned = 0;
		for ( var i = 0; i < camp.Entities.Count; i++ )
		{
			var entity = camp.Entities[i];
			if ( entity is null )
				continue;

			var position = ground + EnemyCampLayout.ToWorldOffset( entity.X, entity.Y, entity.Z, campRotation );
			var rotation = Rotation.FromYaw( yawDegrees + entity.YawDegrees );
			if ( TrySpawnEntity( scene, entity, position, rotation, ground, camp ) )
				spawned++;
		}

		CreateCampRoot( scene, ground, camp );

		Log.Info( $"[EnemyCampSpawner] Camp '{camp.Id}' at {ground}: {placed}/{camp.Pieces.Count} pieces, {spawned}/{camp.Entities.Count} entities." );
		return placed > 0 || spawned > 0;
	}

	static bool TrySpawnEntity( Scene scene, EnemyCampEntityData entity, Vector3 position, Rotation rotation, Vector3 campCenter, EnemyCampData camp )
	{
		var enemyType = entity.ResolveEnemyType();
		var prefabPath = entity.Prefab;
		var instance = BuildPrefabUtility.GetTemplate( prefabPath )?.Clone();
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[EnemyCampSpawner] Failed to clone prefab '{prefabPath}' for {enemyType}." );
			return false;
		}

		instance.Parent = scene;
		instance.WorldPosition = position;
		instance.WorldRotation = rotation;

		EntityEnemySetup.Configure( instance, enemyType, entity.Tier, entity.Health );

		// Camp leash: both rings are centered on the camp origin and shared by every guard.
		// Entity values override the camp defaults but keep the same center.
		var wanderMeters = entity.WanderDistanceMeters > 0f ? entity.WanderDistanceMeters : camp.WanderDistanceMeters;
		var travelMeters = entity.MaxTravelDistanceMeters > 0f ? entity.MaxTravelDistanceMeters : camp.MaxTravelDistanceMeters;
		instance.Components.Get<EntityBrain>()?.SetLeash(
			campCenter,
			wanderMeters * EnemyCampLayout.UnitsPerMeter,
			travelMeters * EnemyCampLayout.UnitsPerMeter );

		// Clone alone is host-local — remotes never see the entity without NetworkSpawn.
		if ( Networking.IsActive && !HostNetworkSpawn.TrySpawn( instance ) )
		{
			Log.Warning( $"[EnemyCampSpawner] NetworkSpawn failed for '{prefabPath}' — destroying local clone." );
			instance.Destroy();
			return false;
		}

		return true;
	}

	/// <summary>Host-local camp root at the origin: records the leash and keeps the debug rings drawn while the camp exists.</summary>
	static void CreateCampRoot( Scene scene, Vector3 center, EnemyCampData camp )
	{
		var root = new GameObject( true, $"enemy_camp_{camp.Id}" );
		root.Parent = scene;
		root.WorldPosition = center;

		var marker = root.Components.Create<EnemyCamp>();
		marker.CampId = camp.Id;
		marker.WanderDistance = camp.WanderDistanceMeters * EnemyCampLayout.UnitsPerMeter;
		marker.MaxTravelDistance = camp.MaxTravelDistanceMeters * EnemyCampLayout.UnitsPerMeter;
	}

	/// <summary>Standable ground under <paramref name="near"/>; falls back to the point itself when nothing is below.</summary>
	static Vector3 FindGround( Scene scene, Vector3 near )
	{
		var from = near + Vector3.Up * 512f;
		var to = near + Vector3.Down * 4096f;

		var tr = scene.Trace.Ray( from, to )
			.Radius( 8f )
			.UsePhysicsWorld()
			.WithoutTags( "player", "enemy", "buildpreview" )
			.Run();

		if ( !tr.Hit )
		{
			tr = scene.Trace.Ray( from, to )
				.Radius( 8f )
				.WithoutTags( "player", "enemy", "buildpreview" )
				.Run();
		}

		if ( !tr.Hit || tr.Normal.z < 0.35f )
			return near;

		return tr.HitPosition;
	}
}
