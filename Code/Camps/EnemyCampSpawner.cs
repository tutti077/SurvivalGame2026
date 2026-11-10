using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Host: stamps an <see cref="EnemyCampData"/> layout into the world — build pieces through the
/// shared host placement path, then the guarding entities. The debug key (<see cref="EnemyCampSpawnButton"/>)
/// and the world's generated camps (biome population camp rows, <see cref="EnemyCampSites"/>) both end here; a
/// generated camp gives every guard its own <see cref="BiomePopulationSlot"/> so guards respawn, sleep into
/// <see cref="EntityChunkMemory"/> and come back exactly like the biome population.
/// </summary>
public static class EnemyCampSpawner
{
	/// <summary>
	/// Spawn <paramref name="campId"/> with its origin dropped onto the ground under
	/// <paramref name="origin"/>. Returns false only when nothing at all was spawned.
	/// </summary>
	public static bool TrySpawn( Scene scene, string campId, Vector3 origin, float yawDegrees )
		=> TrySpawn( scene, campId, origin, yawDegrees, null, default ) is not null;

	/// <summary>
	/// Spawn <paramref name="campId"/> and return its <see cref="EnemyCamp"/> root (null when nothing was spawned).
	/// <paramref name="slotPrefix"/> set = a generated world camp: guard <c>i</c> is population slot <c>{slotPrefix}:{i}</c>
	/// with <paramref name="slotEntry"/>'s respawn rule, and the placed pieces are kept on the root for a world reset.
	/// </summary>
	public static EnemyCamp TrySpawn( Scene scene, string campId, Vector3 origin, float yawDegrees, string slotPrefix, BiomePopulationEntry slotEntry )
	{
		if ( !scene.IsValid() )
			return null;

		if ( !EnemyCampCatalog.TryGet( campId, out var camp ) )
		{
			Log.Warning( $"[EnemyCampSpawner] Unknown camp '{campId}'." );
			return null;
		}

		BuildNavMeshSync.EnsureBuildTraversalSettings( scene );

		var ground = FindGround( scene, origin );
		var campRotation = Rotation.FromYaw( yawDegrees );

		var placed = 0;
		var pieces = new List<GameObject>();
		for ( var i = 0; i < camp.Pieces.Count; i++ )
		{
			var piece = camp.Pieces[i];
			if ( piece is null || string.IsNullOrWhiteSpace( piece.PieceId ) )
				continue;

			var rotation = Rotation.FromYaw( yawDegrees + piece.YawDegrees );
			var position = ground + EnemyCampLayout.ToWorldOffset( piece.X, piece.Y, piece.Z, campRotation );

			// Pieces sit on the camp's ground plane the same way a hammer-placed piece sits on terrain.
			position += Vector3.Up * BuildModuleDimensions.GetGroundSitHalfExtent( piece.PieceId, rotation );

			if ( BuildAuthority.HostPlacePiece( scene, piece.PieceId, new Transform( position, rotation ), piece.Blueprint, out var spawnedPiece ) )
			{
				placed++;
				pieces.Add( spawnedPiece );
			}
			else
				Log.Warning( $"[EnemyCampSpawner] Camp '{camp.Id}': failed to place piece '{piece.PieceId}'." );
		}

		var spawned = SpawnGuards( scene, camp, ground, yawDegrees, slotPrefix, slotEntry );
		var root = CreateCampRoot( scene, ground, yawDegrees, camp, pieces );

		var tag = slotPrefix is null ? "" : $" (world camp {slotPrefix})";
		Log.Info( $"[EnemyCampSpawner] Camp '{camp.Id}' at {ground}: {placed}/{camp.Pieces.Count} pieces, {spawned}/{camp.Entities.Count} entities{tag}." );
		if ( placed > 0 || spawned > 0 )
			return root;
		root.GameObject.Destroy();
		return null;
	}

	/// <summary>
	/// The camp's guards around <paramref name="ground"/> (its origin). With a <paramref name="slotPrefix"/> only the guards
	/// whose slot may spawn now (alive elsewhere, remembered or still waiting out a respawn = skipped) — the same rule the
	/// biome population uses on a chunk reload. Returns how many spawned.
	/// </summary>
	public static int SpawnGuards( Scene scene, EnemyCampData camp, Vector3 ground, float yawDegrees, string slotPrefix, BiomePopulationEntry slotEntry )
	{
		var campRotation = Rotation.FromYaw( yawDegrees );
		var spawned = 0;
		for ( var i = 0; i < camp.Entities.Count; i++ )
		{
			var entity = camp.Entities[i];
			if ( entity is null )
				continue;

			var slotKey = slotPrefix is null ? null : $"{slotPrefix}:{i}";
			if ( slotKey is not null && !BiomePopulationRegistry.ShouldSpawnNow( slotKey ) )
				continue;

			var position = ground + EnemyCampLayout.ToWorldOffset( entity.X, entity.Y, entity.Z, campRotation );
			var rotation = Rotation.FromYaw( yawDegrees + entity.YawDegrees );
			if ( TrySpawnEntity( scene, entity, position, rotation, ground, camp, slotKey, slotEntry ) )
				spawned++;
		}
		return spawned;
	}

	static bool TrySpawnEntity( Scene scene, EnemyCampEntityData entity, Vector3 position, Rotation rotation, Vector3 campCenter, EnemyCampData camp, string slotKey, BiomePopulationEntry slotEntry )
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

		EntityEnemySetup.Configure( instance, enemyType, entity.Tier, entity.Health, prefabPath );

		// Camp leash: both rings are centered on the camp origin and shared by every guard.
		// Entity values override the camp defaults but keep the same center.
		var wanderMeters = entity.WanderDistanceMeters > 0f ? entity.WanderDistanceMeters : camp.WanderDistanceMeters;
		var travelMeters = entity.MaxTravelDistanceMeters > 0f ? entity.MaxTravelDistanceMeters : camp.MaxTravelDistanceMeters;
		instance.Components.Get<EntityBrain>()?.SetLeash(
			campCenter,
			wanderMeters * EnemyCampLayout.UnitsPerMeter,
			travelMeters * EnemyCampLayout.UnitsPerMeter );

		// A generated camp's guard is a population slot: respawn after death, sleep into entity memory, come back
		if ( slotKey is not null )
		{
			var slot = instance.Components.Get<BiomePopulationSlot>() ?? instance.Components.Create<BiomePopulationSlot>();
			slot.Configure( slotKey, slotEntry with { PrefabPath = prefabPath, EnemyType = enemyType, Tier = Math.Max( 1, entity.Tier ) } );
		}

		// Clone alone is host-local — remotes never see the entity without NetworkSpawn.
		if ( Networking.IsActive && !HostNetworkSpawn.TrySpawn( instance ) )
		{
			Log.Warning( $"[EnemyCampSpawner] NetworkSpawn failed for '{prefabPath}' — destroying local clone." );
			instance.Destroy();
			return false;
		}

		if ( slotKey is not null )
			BiomePopulationRegistry.NotifySpawned( slotKey, instance );
		return true;
	}

	/// <summary>Host-local camp root at the origin: records the leash and keeps the debug rings drawn while the camp exists.</summary>
	static EnemyCamp CreateCampRoot( Scene scene, Vector3 center, float yawDegrees, EnemyCampData camp, List<GameObject> pieces )
	{
		var root = new GameObject( true, $"enemy_camp_{camp.Id}" );
		root.Parent = scene;
		root.WorldPosition = center;
		root.WorldRotation = Rotation.FromYaw( yawDegrees );
		root.Flags |= GameObjectFlags.NotSaved;

		var marker = root.Components.Create<EnemyCamp>();
		marker.CampId = camp.Id;
		marker.WanderDistance = camp.WanderDistanceMeters * EnemyCampLayout.UnitsPerMeter;
		marker.MaxTravelDistance = camp.MaxTravelDistanceMeters * EnemyCampLayout.UnitsPerMeter;
		marker.Pieces.AddRange( pieces );
		return marker;
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
