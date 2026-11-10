using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-only biome entity density: spacing cells in a biome, spawn weight for tiny patches,
/// respawn via <see cref="BiomePopulationRegistry"/>. Anchor (<c>near</c>) is reserved for later.
/// Camp rows (<c>camp</c> set) stamp a whole enemy camp per cell instead (<see cref="EnemyCampSites"/>).
/// </summary>
public static class BiomePopulationScatter
{
	public static void PopulateChunk(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float chunkSizeMeters,
		int worldSeed,
		TerrainVegetationScatter.ForestPatchField forest = default )
	{
		if ( chunkRoot is null || !chunkRoot.IsValid() || backend is null || settings is null )
			return;

		BiomePopulationCatalog.EnsureLoaded();

		var chunkSize = Math.Max( 32f, chunkSizeMeters );
		var worldRadius = settings.TotalWorldRadiusMeters;
		var chunkMinX = -worldRadius + (coord.X * chunkSize);
		var chunkMinY = -worldRadius + (coord.Y * chunkSize);

		foreach ( TerrainPreviewBiomeId biome in Enum.GetValues<TerrainPreviewBiomeId>() )
		{
			if ( biome is TerrainPreviewBiomeId.None or TerrainPreviewBiomeId.Water )
				continue;

			var entries = BiomePopulationCatalog.GetEntries( biome );
			if ( entries.Count == 0 )
				continue;

			foreach ( var entry in entries )
			{
				if ( entry.CampId is not null )
					EnemyCampSites.ScatterCampEntry( chunkRoot, settings, backend, chunkSize, chunkMinX, chunkMinY, worldSeed, biome, entry, forest );
				else
					ScatterEntry( chunkRoot, coord, settings, backend, chunkSize, chunkMinX, chunkMinY, worldSeed, biome, entry );
			}
		}
	}

	static int ScatterEntry(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float chunkSize,
		float chunkMinX,
		float chunkMinY,
		int worldSeed,
		TerrainPreviewBiomeId biome,
		BiomePopulationEntry entry )
	{
		_ = coord;

		// Anchor rules (near trees etc.) are reserved — free biome spawn for v1.
		_ = entry.Near;

		var spacing = Math.Clamp( entry.SpacingMeters, 16f, 4000f );
		var weight = Math.Clamp( entry.SpawnWeight, 0f, 1f );
		if ( weight <= 1e-4f )
			return 0;

		// World-grid cells (not per-chunk). spacing 250m on 64m chunks must be ~1 try per ~15 chunks,
		// not Max(1, Floor(64/250))=1 scav attempt every chunk.
		var cellMinX = (int)MathF.Floor( chunkMinX / spacing );
		var cellMaxX = (int)MathF.Floor( (chunkMinX + chunkSize - 1e-3f) / spacing );
		var cellMinY = (int)MathF.Floor( chunkMinY / spacing );
		var cellMaxY = (int)MathF.Floor( (chunkMinY + chunkSize - 1e-3f) / spacing );
		var spawned = 0;

		for ( var cy = cellMinY; cy <= cellMaxY; cy++ )
		{
			for ( var cx = cellMinX; cx <= cellMaxX; cx++ )
			{
				var jitterX = (TerrainPreviewNoise.Hash01( worldSeed + 4101, cx, cy ) - 0.5f) * spacing * 0.35f;
				var jitterY = (TerrainPreviewNoise.Hash01( worldSeed + 4102, cx, cy ) - 0.5f) * spacing * 0.35f;
				var wx = (cx + 0.5f) * spacing + jitterX;
				var wy = (cy + 0.5f) * spacing + jitterY;

				// Point belongs to this chunk's populate pass only.
				if ( wx < chunkMinX || wx >= chunkMinX + chunkSize || wy < chunkMinY || wy >= chunkMinY + chunkSize )
					continue;

				if ( !IsInsideLandDisk( settings, wx, wy ) )
					continue;

				var sample = backend.Sample( settings, wx, wy );
				if ( !sample.IsInsideWorld || !sample.IsOnLand || sample.OceanHeight01 > 0.5f )
					continue;

				var resolved = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, wx, wy );
				if ( resolved.BiomeId != biome )
					continue;

				var roll = TerrainPreviewNoise.Hash01( worldSeed + 4110, cx, cy );
				if ( roll > weight )
					continue;

				// Herds: the cell may hold a group; member 0 stands on the cell point (and keeps the single-spawn slot key),
				// the others round it. Every member is its own population slot, so it sleeps, restores and respawns alone.
				var members = 1;
				if ( entry.GroupChance > 0f && TerrainPreviewNoise.Hash01( worldSeed + 4120, cx, cy ) < entry.GroupChance )
				{
					var sizeRoll = TerrainPreviewNoise.Hash01( worldSeed + 4121, cx, cy );
					members = Math.Clamp( entry.GroupMin + (int)(sizeRoll * (entry.GroupMax - entry.GroupMin + 1)), entry.GroupMin, entry.GroupMax );
				}

				for ( var m = 0; m < members; m++ )
				{
					var slotKey = m == 0 ? $"{entry.EntityId}:g:{cx}:{cy}" : $"{entry.EntityId}:g:{cx}:{cy}:m{m}";
					if ( !BiomePopulationRegistry.ShouldSpawnNow( slotKey ) )
						continue;

					var mx = wx;
					var my = wy;
					var mz = sample.HeightMeters;
					if ( m > 0 )
					{
						var ang = TerrainPreviewNoise.Hash01( worldSeed + 4130 + m, cx, cy ) * MathF.PI * 2f;
						var dist = (0.35f + (0.65f * TerrainPreviewNoise.Hash01( worldSeed + 4140 + m, cx, cy ))) * entry.GroupRadiusMeters;
						mx = wx + (MathF.Cos( ang ) * dist);
						my = wy + (MathF.Sin( ang ) * dist);
						if ( !IsInsideLandDisk( settings, mx, my ) )
							continue;
						var memberSample = backend.Sample( settings, mx, my );
						if ( !memberSample.IsInsideWorld || !memberSample.IsOnLand || memberSample.OceanHeight01 > 0.5f )
							continue;
						mz = memberSample.HeightMeters;
					}

					if ( TrySpawnEntity( chunkRoot, entry, slotKey, chunkMinX, chunkMinY, mx, my, mz ) )
						spawned++;
				}
			}
		}

		return spawned;
	}

	static bool TrySpawnEntity(
		GameObject chunkRoot,
		BiomePopulationEntry entry,
		string slotKey,
		float chunkMinX,
		float chunkMinY,
		float worldXMeters,
		float worldYMeters,
		float heightMeters )
	{
		var localEngine = TerrainWorldUnits.MetersToEngine( new Vector3( worldXMeters - chunkMinX, worldYMeters - chunkMinY, heightMeters ) );

		// Beyond the entity range: filed straight into memory, cloned only once a viewer comes near (EntityChunkMemory).
		if ( EntityChunkMemory.TryRememberUnspawned( chunkRoot.Scene, entry, slotKey, chunkRoot.WorldTransform.PointToWorld( localEngine ) ) )
			return true;

		var instance = ClonePrefab( entry.PrefabPath );
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[BiomePopulation] Failed to clone '{entry.PrefabPath}'." );
			return false;
		}

		instance.Parent = chunkRoot;
		instance.LocalPosition = localEngine;
		instance.Name = $"pop_{entry.EntityId}_{slotKey.GetHashCode():x}";

		if ( entry.Species is { } species )
			AnimalSetup.Configure( instance, species, prefabPath: entry.PrefabPath );
		else
			EntityEnemySetup.Configure( instance, entry.EnemyType, entry.Tier, 0f, entry.PrefabPath );

		var slot = instance.Components.Get<BiomePopulationSlot>() ?? instance.Components.Create<BiomePopulationSlot>();
		slot.Configure( slotKey, entry );

		// Reparent to scene before NetworkSpawn — chunk roots are local/non-networked.
		var scene = chunkRoot.Scene;
		if ( scene.IsValid() )
		{
			var worldPos = instance.WorldPosition;
			var worldRot = instance.WorldRotation;
			instance.Parent = scene;
			instance.WorldPosition = worldPos;
			instance.WorldRotation = worldRot;
		}

		if ( Networking.IsActive )
		{
			if ( !HostNetworkSpawn.TrySpawn( instance ) )
			{
				Log.Warning( $"[BiomePopulation] NetworkSpawn failed for '{entry.PrefabPath}' — destroying local clone." );
				instance.Destroy();
				return false;
			}
		}
		else
		{
			HostNetworkSpawn.TrySpawn( instance );
		}

		BiomePopulationRegistry.NotifySpawned( slotKey, instance );
		return true;
	}

	/// <summary>Respawn helper used by <see cref="BiomePopulationRespawnQueue"/> (world-space position). A camp guard comes back on its camp's leash (<paramref name="leashWander"/> &gt; 0).</summary>
	public static bool SpawnAtWorld(
		GameObject chunkRoot,
		BiomePopulationEntry entry,
		string slotKey,
		Vector3 worldPosition,
		Vector3 leashCenter = default,
		float leashWander = 0f,
		float leashMaxTravel = 0f )
	{
		if ( chunkRoot is null || !chunkRoot.IsValid() )
			return false;

		// A plain population respawn out of range waits in memory like a fresh cell; a camp guard keeps its leash and spawns live.
		if ( leashWander <= 0f && EntityChunkMemory.TryRememberUnspawned( chunkRoot.Scene, entry, slotKey, worldPosition ) )
			return true;

		var instance = ClonePrefab( entry.PrefabPath );
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[BiomePopulation] Failed to clone '{entry.PrefabPath}' for respawn." );
			return false;
		}

		instance.Parent = chunkRoot;
		instance.WorldPosition = worldPosition;
		instance.Name = $"pop_{entry.EntityId}_{slotKey.GetHashCode():x}";

		if ( entry.Species is { } species )
			AnimalSetup.Configure( instance, species, prefabPath: entry.PrefabPath );
		else
			EntityEnemySetup.Configure( instance, entry.EnemyType, entry.Tier, 0f, entry.PrefabPath );
		if ( leashWander > 0f )
			instance.Components.Get<EntityBrain>()?.SetLeash( leashCenter, leashWander, leashMaxTravel );

		var slot = instance.Components.Get<BiomePopulationSlot>() ?? instance.Components.Create<BiomePopulationSlot>();
		slot.Configure( slotKey, entry );

		var scene = chunkRoot.Scene;
		if ( scene.IsValid() )
		{
			var worldPos = instance.WorldPosition;
			var worldRot = instance.WorldRotation;
			instance.Parent = scene;
			instance.WorldPosition = worldPos;
			instance.WorldRotation = worldRot;
		}

		if ( Networking.IsActive )
		{
			if ( !HostNetworkSpawn.TrySpawn( instance ) )
			{
				Log.Warning( $"[BiomePopulation] NetworkSpawn failed for respawn '{entry.PrefabPath}' — destroying local clone." );
				instance.Destroy();
				return false;
			}
		}
		else
		{
			HostNetworkSpawn.TrySpawn( instance );
		}

		BiomePopulationRegistry.NotifySpawned( slotKey, instance );
		return true;
	}

	static bool IsInsideLandDisk( TerrainPreviewSettings settings, float wx, float wy )
	{
		var r = settings.TotalWorldRadiusMeters;
		return (wx * wx) + (wy * wy) <= r * r;
	}

	/// <summary>Clone an entity prefab by path (template cache first, then the resource library). Shared with <see cref="EntityChunkMemory"/>.</summary>
	public static GameObject ClonePrefab( string path )
	{
		path = (path ?? string.Empty).Trim().Replace( '\\', '/' );
		var template = BuildPrefabUtility.GetTemplate( path );
		if ( template is not null && template.IsValid() )
			return template.Clone();

		var prefabFile = ResourceLibrary.Get<PrefabFile>( path );
		if ( prefabFile is null )
			return null;

		var prefabScene = SceneUtility.GetPrefabScene( prefabFile );
		return prefabScene?.Clone();
	}
}
