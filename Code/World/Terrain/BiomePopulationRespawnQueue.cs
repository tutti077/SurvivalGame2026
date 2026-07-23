using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Delayed respawns for biome population when the entity dies while its chunk is still loaded.</summary>
public static class BiomePopulationRespawnQueue
{
	struct Pending
	{
		public string SlotKey;
		public string PrefabPath;
		public EnemyType EnemyType;
		public int Tier;
		public bool Respawn;
		public float RespawnDelaySeconds;
		public string Near;
		public Vector3 WorldPosition;
		public GameObject ChunkRoot;
		public double ReadyAt;
	}

	static readonly List<Pending> Queue = new();

	public static void Clear() => Queue.Clear();

	public static void Enqueue(
		string slotKey,
		BiomePopulationEntry entry,
		Vector3 worldPosition,
		GameObject chunkRoot,
		float delaySeconds )
	{
		if ( string.IsNullOrWhiteSpace( slotKey ) || chunkRoot is null || !chunkRoot.IsValid() )
			return;

		Queue.Add( new Pending
		{
			SlotKey = slotKey,
			PrefabPath = entry.PrefabPath,
			EnemyType = entry.EnemyType,
			Tier = entry.Tier,
			Respawn = entry.Respawn,
			RespawnDelaySeconds = entry.RespawnDelaySeconds,
			Near = entry.Near,
			WorldPosition = worldPosition,
			ChunkRoot = chunkRoot,
			ReadyAt = Time.NowDouble + Math.Max( 0f, delaySeconds ),
		} );
	}

	public static void EnqueueFromSlot(
		string slotKey,
		string prefabPath,
		EnemyType enemyType,
		int tier,
		bool respawn,
		float respawnDelaySeconds,
		Vector3 worldPosition,
		GameObject chunkRoot )
	{
		if ( !respawn || string.IsNullOrWhiteSpace( slotKey ) || chunkRoot is null || !chunkRoot.IsValid() )
			return;

		Queue.Add( new Pending
		{
			SlotKey = slotKey,
			PrefabPath = prefabPath,
			EnemyType = enemyType,
			Tier = Math.Max( 1, tier ),
			Respawn = true,
			RespawnDelaySeconds = respawnDelaySeconds,
			WorldPosition = worldPosition,
			ChunkRoot = chunkRoot,
			ReadyAt = Time.NowDouble + Math.Max( 0f, respawnDelaySeconds ),
		} );
	}

	public static void Tick()
	{
		if ( Queue.Count == 0 )
			return;

		for ( var i = Queue.Count - 1; i >= 0; i-- )
		{
			var pending = Queue[i];
			if ( Time.NowDouble < pending.ReadyAt )
				continue;

			Queue.RemoveAt( i );

			if ( pending.ChunkRoot is null || !pending.ChunkRoot.IsValid() )
				continue;

			if ( !BiomePopulationRegistry.ShouldSpawnNow( pending.SlotKey ) )
				continue;

			BiomePopulationScatter.SpawnAtWorld(
				pending.ChunkRoot,
				new BiomePopulationEntry
				{
					EntityId = pending.SlotKey.Split( ':' )[0],
					PrefabPath = pending.PrefabPath,
					EnemyType = pending.EnemyType,
					Tier = pending.Tier,
					Respawn = pending.Respawn,
					RespawnDelaySeconds = pending.RespawnDelaySeconds,
					Near = pending.Near,
					SpacingMeters = 250f,
					SpawnWeight = 1f,
				},
				pending.SlotKey,
				pending.WorldPosition );
		}
	}
}
