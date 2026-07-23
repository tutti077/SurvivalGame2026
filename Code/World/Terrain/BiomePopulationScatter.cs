using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-only biome entity density: spacing cells in a biome, spawn weight for tiny patches,
/// respawn via <see cref="BiomePopulationRegistry"/>. Anchor (<c>near</c>) is reserved for later.
/// </summary>
public static class BiomePopulationScatter
{
	public static void PopulateChunk(
		GameObject chunkRoot,
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float chunkSizeMeters,
		int worldSeed )
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
				ScatterEntry( chunkRoot, coord, settings, backend, chunkSize, chunkMinX, chunkMinY, worldSeed, biome, entry );
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

				var slotKey = $"{entry.EntityId}:g:{cx}:{cy}";
				if ( !BiomePopulationRegistry.ShouldSpawnNow( slotKey ) )
					continue;

				var roll = TerrainPreviewNoise.Hash01( worldSeed + 4110, cx, cy );
				if ( roll > weight )
					continue;

				if ( !TrySpawnEntity( chunkRoot, entry, slotKey, chunkMinX, chunkMinY, wx, wy, sample.HeightMeters ) )
					continue;

				spawned++;
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
		var instance = ClonePrefab( entry.PrefabPath );
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[BiomePopulation] Failed to clone '{entry.PrefabPath}'." );
			return false;
		}

		instance.Parent = chunkRoot;
		var localMeters = new Vector3( worldXMeters - chunkMinX, worldYMeters - chunkMinY, heightMeters );
		instance.LocalPosition = TerrainWorldUnits.MetersToEngine( localMeters );
		instance.Name = $"pop_{entry.EntityId}_{slotKey.GetHashCode():x}";

		EntityEnemySetup.Configure( instance, entry.EnemyType, entry.Tier );

		var slot = instance.Components.Get<BiomePopulationSlot>() ?? instance.Components.Create<BiomePopulationSlot>();
		slot.Configure( slotKey, entry );

		BiomePopulationRegistry.NotifySpawned( slotKey, instance );
		return true;
	}

	/// <summary>Respawn helper used by <see cref="BiomePopulationRespawnQueue"/> (world-space position).</summary>
	public static bool SpawnAtWorld(
		GameObject chunkRoot,
		BiomePopulationEntry entry,
		string slotKey,
		Vector3 worldPosition )
	{
		if ( chunkRoot is null || !chunkRoot.IsValid() )
			return false;

		var instance = ClonePrefab( entry.PrefabPath );
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[BiomePopulation] Failed to clone '{entry.PrefabPath}' for respawn." );
			return false;
		}

		instance.Parent = chunkRoot;
		instance.WorldPosition = worldPosition;
		instance.Name = $"pop_{entry.EntityId}_{slotKey.GetHashCode():x}";

		EntityEnemySetup.Configure( instance, entry.EnemyType, entry.Tier );

		var slot = instance.Components.Get<BiomePopulationSlot>() ?? instance.Components.Create<BiomePopulationSlot>();
		slot.Configure( slotKey, entry );

		BiomePopulationRegistry.NotifySpawned( slotKey, instance );
		return true;
	}

	static bool IsInsideLandDisk( TerrainPreviewSettings settings, float wx, float wy )
	{
		var r = settings.TotalWorldRadiusMeters;
		return (wx * wx) + (wy * wy) <= r * r;
	}

	static GameObject ClonePrefab( string path )
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
