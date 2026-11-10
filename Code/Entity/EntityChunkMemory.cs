using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;

namespace Survival;

/// <summary>
/// One despawned entity as the host remembers it: everything needed to clone it back where it stood,
/// facing the same way, with the health it had. Position is world engine units; the chunk it is filed
/// under is the terrain chunk (or a 64 m grid cell in a scene without streamed terrain). A population
/// entity that was never cloned (its cell was beyond the range when the chunk populated) is a record with
/// <see cref="CurrentHealth"/> 0 = full.
/// </summary>
public sealed class EntityMemoryRecord
{
	public string PrefabPath;
	/// <summary>Set for an animal (cloned back through <see cref="AnimalSetup"/>); null = an enemy.</summary>
	public AnimalSpecies? Species;
	/// <summary>Animals: the calm state it left in (Idle / Wander / Graze).</summary>
	public AnimalAiState AnimalState = AnimalAiState.Wander;
	public EnemyType EnemyType;
	public int Tier;
	/// <summary>0 = the species' / type's default.</summary>
	public float MaxHealth;
	/// <summary>0 = full (never spawned).</summary>
	public float CurrentHealth;
	public Vector3 WorldPosition;
	public float YawDegrees;
	public Vector3 HomePosition;
	public Vector3 LeashCenter;
	public float LeashWanderDistance;
	public float LeashMaxTravelDistance;
	/// <summary>Biome population binding (null for camp guards and console spawns).</summary>
	public string SlotKey;
	public bool SlotRespawn;
	public float SlotRespawnDelaySeconds;
	public double RememberedAt;
}

/// <summary>
/// Host memory of entities that left the live world (Mark 2026-11-02: a scav that is not tracking a player and has
/// no player within range writes its location and health to server memory and despawns; 2026-11-04: animals too, on
/// their own shorter range: "Make all ferals/ascendants at 300/350m and animals can be 100/90 for now"). <see cref="EntityBrain"/> and
/// <see cref="AnimalBrain"/> call <see cref="Remember(EntityBrain)"/> / <see cref="Remember(AnimalBrain)"/> from their 2 Hz
/// range checks, and biome population files a cell that is already out of range straight here without cloning it
/// (<see cref="TryRememberUnspawned"/>). This system ticks the records at the same rate and clones each one back
/// (<see cref="EntityEnemySetup.Configure"/> or <see cref="AnimalSetup.Configure"/> + the remembered health / leash /
/// state / population slot) once a viewer is within its kind's return range (<see cref="EnemyReturnRangeMeters"/> /
/// <see cref="AnimalReturnRangeMeters"/>) AND there is standable ground under it — on streamed terrain that is the chunk's
/// collider coming online inside Collision Range, exactly where fresh population spawns. In-memory only: a new world
/// load clears it (<c>TerrainWorldManager.UnloadAll</c>). <c>entity_memory</c> dumps it.
/// </summary>
public sealed class EntityChunkMemory : GameObjectSystem
{
	// Entity ranges (Mark 2026-11-04: "Make all ferals/ascendants at 300/350m and animals can be 100/90 for now"). Each pair is
	// despawn / return in metres: no viewer within the despawn range and an entity leaves the world (or sleeps, when something
	// holds it here); a remembered one comes back, and a sleeper wakes, once a viewer is within the return range — the gap
	// keeps the 2 Hz checks from thrashing at the edge. Converted to engine units once, in AnyViewerWithin.

	/// <summary>Enemies (<see cref="EntityBrain"/>, every <see cref="EntityKind"/> — feral, robot, ascended): leave the world past this.</summary>
	public const float EnemyDespawnRangeMeters = 350f;

	/// <summary>Enemies come back / wake within this.</summary>
	public const float EnemyReturnRangeMeters = 300f;

	/// <summary>Animals (<see cref="AnimalBrain"/>): leave the world past this.</summary>
	public const float AnimalDespawnRangeMeters = 100f;

	/// <summary>Animals come back / wake within this.</summary>
	public const float AnimalReturnRangeMeters = 90f;

	public static float DespawnRangeMetersFor( AnimalSpecies? species ) => species is null ? EnemyDespawnRangeMeters : AnimalDespawnRangeMeters;

	public static float ReturnRangeMetersFor( AnimalSpecies? species ) => species is null ? EnemyReturnRangeMeters : AnimalReturnRangeMeters;

	const float TickSeconds = 0.5f;
	/// <summary>Restores per tick: a chunk full of remembered scavs lands over a few frames, never in one.</summary>
	const int MaxRestoresPerTick = 2;
	/// <summary>Grid cell for scenes without streamed terrain (testscene1 and friends).</summary>
	const float FallbackCellMeters = 64f;

	readonly Dictionary<TerrainChunkCoord, List<EntityMemoryRecord>> _chunks = new();
	readonly List<Vector3> _viewers = new();
	readonly List<TerrainChunkCoord> _emptyChunks = new();
	double _nextTickAt;

	/// <summary>Records currently held.</summary>
	public int Count { get; private set; }
	public int RememberedTotal { get; private set; }
	public int RestoredTotal { get; private set; }

	public EntityChunkMemory( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 10, Tick, "EntityChunkMemory" );
	}

	public static EntityChunkMemory Get( Scene scene ) => scene is null || !scene.IsValid() ? null : scene.GetSystem<EntityChunkMemory>();

	/// <summary>Forget everything (world unload / reload: the records belong to the old world).</summary>
	public void Clear()
	{
		_chunks.Clear();
		Count = 0;
	}

	/// <summary>
	/// Host: file <paramref name="brain"/>'s entity under its chunk. Returns false when the entity cannot be brought back
	/// (no source prefab) — the caller then leaves it alive. Marks a biome population slot as remembered so a chunk
	/// reload does not spawn a second one at the slot's origin.
	/// </summary>
	public bool Remember( EntityBrain brain )
	{
		if ( brain is null || !brain.IsValid() || string.IsNullOrWhiteSpace( brain.SourcePrefabPath ) )
			return false;

		var go = brain.GameObject;
		var vitals = brain.Vitals ?? go.Components.Get<EntityVitals>();
		if ( vitals is null )
			return false;

		var record = new EntityMemoryRecord
		{
			PrefabPath = brain.SourcePrefabPath,
			EnemyType = vitals.EnemyType,
			Tier = Math.Max( 1, vitals.Tier ),
			MaxHealth = Math.Max( 1f, vitals.CurrentHealthMax ),
			CurrentHealth = Math.Clamp( vitals.CurrentHealth, 0.001f, Math.Max( 1f, vitals.CurrentHealthMax ) ),
			WorldPosition = go.WorldPosition,
			YawDegrees = go.WorldRotation.Yaw(),
			HomePosition = brain.HomePosition,
			LeashCenter = brain.LeashCenter,
			LeashWanderDistance = brain.LeashWanderDistance,
			LeashMaxTravelDistance = brain.LeashMaxTravelDistance,
			RememberedAt = Time.NowDouble,
		};

		BindSlot( record, go );
		File( record );
		return true;
	}

	/// <summary>Host: file a calm, untamed animal (the caller checked). Same contract as the enemy overload.</summary>
	public bool Remember( AnimalBrain brain )
	{
		if ( brain is null || !brain.IsValid() || string.IsNullOrWhiteSpace( brain.SourcePrefabPath ) )
			return false;

		var go = brain.GameObject;
		var vitals = brain.Vitals ?? go.Components.Get<EntityVitals>();
		if ( vitals is null )
			return false;

		var record = new EntityMemoryRecord
		{
			PrefabPath = brain.SourcePrefabPath,
			Species = brain.Species,
			AnimalState = brain.CurrentState,
			EnemyType = vitals.EnemyType,
			Tier = Math.Max( 1, vitals.Tier ),
			MaxHealth = Math.Max( 1f, vitals.CurrentHealthMax ),
			CurrentHealth = Math.Clamp( vitals.CurrentHealth, 0.001f, Math.Max( 1f, vitals.CurrentHealthMax ) ),
			WorldPosition = go.WorldPosition,
			YawDegrees = go.WorldRotation.Yaw(),
			HomePosition = brain.HomePosition,
			RememberedAt = Time.NowDouble,
		};

		BindSlot( record, go );
		File( record );
		return true;
	}

	/// <summary>
	/// Host, biome population: a cell whose spot has no viewer within its kind's despawn range goes straight into
	/// memory instead of being cloned and despawned on its first range check. False = spawn it live.
	/// </summary>
	public static bool TryRememberUnspawned( Scene scene, BiomePopulationEntry entry, string slotKey, Vector3 worldPosition )
	{
		var memory = Get( scene );
		if ( memory is null || string.IsNullOrWhiteSpace( entry.PrefabPath ) || AnyViewerWithin( scene, worldPosition, DespawnRangeMetersFor( entry.Species ) ) )
			return false;

		var record = new EntityMemoryRecord
		{
			PrefabPath = entry.PrefabPath.Trim(),
			Species = entry.Species,
			EnemyType = entry.EnemyType,
			Tier = Math.Max( 1, entry.Tier ),
			WorldPosition = worldPosition,
			HomePosition = worldPosition,
			RememberedAt = Time.NowDouble,
		};

		if ( !string.IsNullOrWhiteSpace( slotKey ) )
		{
			record.SlotKey = slotKey;
			record.SlotRespawn = entry.Respawn;
			record.SlotRespawnDelaySeconds = entry.RespawnDelaySeconds;
			BiomePopulationRegistry.NotifyRemembered( slotKey );
		}

		memory.File( record );
		return true;
	}

	static void BindSlot( EntityMemoryRecord record, GameObject go )
	{
		var slot = go.Components.Get<BiomePopulationSlot>();
		if ( slot is null || string.IsNullOrWhiteSpace( slot.SlotKey ) )
			return;

		record.SlotKey = slot.SlotKey;
		record.SlotRespawn = slot.Respawn;
		record.SlotRespawnDelaySeconds = slot.RespawnDelaySeconds;
		BiomePopulationRegistry.NotifyRemembered( slot.SlotKey );
	}

	void File( EntityMemoryRecord record )
	{
		var coord = ChunkOf( record.WorldPosition );
		if ( !_chunks.TryGetValue( coord, out var list ) )
		{
			list = new List<EntityMemoryRecord>();
			_chunks[coord] = list;
		}

		list.Add( record );
		Count++;
		RememberedTotal++;
	}

	void Tick()
	{
		if ( Count == 0 || Time.NowDouble < _nextTickAt )
			return;
		_nextTickAt = Time.NowDouble + TickSeconds;

		if ( Scene is null || !Scene.IsValid() || !IsHost() )
			return;

		CollectViewers( Scene, _viewers );
		if ( _viewers.Count == 0 )
			return;

		var manager = FindTerrainManager();
		var restores = 0;
		_emptyChunks.Clear();

		foreach ( var (coord, list) in _chunks )
		{
			for ( var i = list.Count - 1; i >= 0 && restores < MaxRestoresPerTick; i-- )
			{
				var record = list[i];
				var wakeMeters = ReturnRangeMetersFor( record.Species );
				// Streamed terrain: an entity only exists where the chunk collider is live, like fresh population.
				if ( manager is not null )
					wakeMeters = Math.Min( wakeMeters, manager.CollisionRangeMeters );

				if ( !AnyViewerWithin( _viewers, record.WorldPosition, wakeMeters ) )
					continue;

				if ( !TryFindGround( record.WorldPosition, out var feet ) )
					continue;   // chunk collider not online yet — next tick

				list.RemoveAt( i );
				Count--;
				restores++;
				if ( TryRestore( record, feet ) )
					RestoredTotal++;
			}

			if ( list.Count == 0 )
				_emptyChunks.Add( coord );

			if ( restores >= MaxRestoresPerTick )
				break;
		}

		foreach ( var coord in _emptyChunks )
			_chunks.Remove( coord );
	}

	bool TryRestore( EntityMemoryRecord record, Vector3 feet )
	{
		var instance = BiomePopulationScatter.ClonePrefab( record.PrefabPath );
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[EntityChunkMemory] Failed to clone '{record.PrefabPath}' — record dropped." );
			return false;
		}

		instance.Parent = Scene;
		instance.WorldPosition = feet;
		instance.WorldRotation = Rotation.FromYaw( record.YawDegrees );

		if ( record.Species is { } species )
		{
			AnimalSetup.Configure( instance, species, record.MaxHealth, prefabPath: record.PrefabPath );
			var animal = instance.Components.Get<AnimalBrain>();
			if ( animal is not null )
			{
				animal.SetHomePosition( record.HomePosition );
				animal.ResumeRememberedState( record.AnimalState );
			}
		}
		else
		{
			EntityEnemySetup.Configure( instance, record.EnemyType, record.Tier, record.MaxHealth, record.PrefabPath );

			var brain = instance.Components.Get<EntityBrain>();
			if ( brain is not null )
			{
				brain.SetHomePosition( record.HomePosition );
				if ( record.LeashWanderDistance > 0f )
					brain.SetLeash( record.LeashCenter, record.LeashWanderDistance, record.LeashMaxTravelDistance );
			}
		}

		if ( record.CurrentHealth > 0f )
			instance.Components.Get<EntityVitals>()?.RestoreHealth( record.CurrentHealth );

		if ( !string.IsNullOrWhiteSpace( record.SlotKey ) )
		{
			var slot = instance.Components.Get<BiomePopulationSlot>() ?? instance.Components.Create<BiomePopulationSlot>();
			slot.Configure( record.SlotKey, new BiomePopulationEntry
			{
				EntityId = record.SlotKey.Split( ':' )[0],
				PrefabPath = record.PrefabPath,
				EnemyType = record.EnemyType,
				Species = record.Species,
				Tier = record.Tier,
				Respawn = record.SlotRespawn,
				RespawnDelaySeconds = record.SlotRespawnDelaySeconds,
				SpacingMeters = 250f,
				SpawnWeight = 1f,
			} );
		}

		if ( Networking.IsActive && !HostNetworkSpawn.TrySpawn( instance ) )
		{
			Log.Warning( $"[EntityChunkMemory] NetworkSpawn failed for '{record.PrefabPath}' — destroying local clone." );
			instance.Destroy();
			return false;
		}

		if ( !string.IsNullOrWhiteSpace( record.SlotKey ) )
			BiomePopulationRegistry.NotifySpawned( record.SlotKey, instance );

		return true;
	}

	/// <summary>Standable ground under the remembered spot (physics first, then any collider). False while the chunk has no live collider.</summary>
	bool TryFindGround( Vector3 near, out Vector3 feet )
	{
		feet = near;
		var from = near + Vector3.Up * 256f;
		var to = near + Vector3.Down * 1024f;

		var tr = Scene.Trace.Ray( from, to )
			.Radius( 8f )
			.UsePhysicsWorld()
			.WithoutTags( "player", "enemy", "buildpreview" )
			.Run();

		if ( !tr.Hit )
		{
			tr = Scene.Trace.Ray( from, to )
				.Radius( 8f )
				.WithoutTags( "player", "enemy", "buildpreview" )
				.Run();
		}

		if ( !tr.Hit || tr.Normal.z < 0.35f )
			return false;

		feet = tr.HitPosition + Vector3.Up * 2f;
		return true;
	}

	TerrainChunkCoord ChunkOf( Vector3 worldPos )
	{
		var meters = TerrainWorldUnits.EngineToMeters( worldPos );
		var manager = FindTerrainManager();
		if ( manager is not null )
		{
			var settings = manager.BuildGenerationSettings();
			if ( settings is not null )
				return TerrainChunkStreaming.WorldToChunkCoord( meters.x, meters.y, settings.TotalWorldRadiusMeters, Math.Max( 32f, manager.ChunkSizeMeters ) );
		}

		return new TerrainChunkCoord( (int)MathF.Floor( meters.x / FallbackCellMeters ), (int)MathF.Floor( meters.y / FallbackCellMeters ) );
	}

	TerrainWorldManager FindTerrainManager()
	{
		foreach ( var m in Scene.GetAllComponents<TerrainWorldManager>() )
		{
			if ( m is not null && m.IsValid() && m.Enabled )
				return m;
		}

		return null;
	}

	bool IsHost() => Scene.Network?.Active != true || Networking.IsHost;

	/// <summary>Who counts as "a player near here": every player pawn, or the scene camera when there is no pawn (the terrainTest fly camera).</summary>
	public static void CollectViewers( Scene scene, List<Vector3> into )
	{
		into.Clear();
		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is null || !vitals.IsValid() )
				continue;
			into.Add( vitals.WorldPosition );
		}

		if ( into.Count > 0 )
			return;

		var cam = scene.Camera;
		if ( cam is not null && cam.IsValid() )
			into.Add( cam.WorldPosition );
	}

	public static bool AnyViewerWithin( List<Vector3> viewers, Vector3 here, float meters )
	{
		var range = TerrainWorldUnits.MetersToEngine( meters );
		var rangeSq = range * range;
		for ( var i = 0; i < viewers.Count; i++ )
		{
			if ( (viewers[i] - here).LengthSquared <= rangeSq )
				return true;
		}

		return false;
	}

	/// <summary>Same viewer rule, one entity at a time (the brain's 2 Hz check).</summary>
	public static bool AnyViewerWithin( Scene scene, Vector3 here, float meters )
	{
		if ( scene is null || !scene.IsValid() )
			return false;

		var range = TerrainWorldUnits.MetersToEngine( meters );
		var rangeSq = range * range;
		var anyPawn = false;
		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is null || !vitals.IsValid() )
				continue;
			anyPawn = true;
			if ( (vitals.WorldPosition - here).LengthSquared <= rangeSq )
				return true;
		}

		if ( anyPawn )
			return false;

		var cam = scene.Camera;
		return cam is not null && cam.IsValid() && (cam.WorldPosition - here).LengthSquared <= rangeSq;
	}

	[ConCmd( "entity_memory" )]
	public static void ConCmdDump()
	{
		var memory = Get( Sandbox.Game.ActiveScene );
		if ( memory is null )
		{
			Log.Info( "[EntityChunkMemory] no active scene." );
			return;
		}

		var sb = new StringBuilder();
		sb.Append( $"[EntityChunkMemory] {memory.Count} remembered in {memory._chunks.Count} chunks (remembered {memory.RememberedTotal}, restored {memory.RestoredTotal} this world)" );
		foreach ( var (coord, list) in memory._chunks )
		{
			sb.Append( $"\n  chunk {coord.X},{coord.Y}: {list.Count}" );
			foreach ( var r in list )
			{
				var m = TerrainWorldUnits.EngineToMeters( r.WorldPosition );
				var slot = string.IsNullOrWhiteSpace( r.SlotKey ) ? "" : $" slot {r.SlotKey}";
				var kind = r.Species is { } sp ? $"{sp} ({r.AnimalState})" : $"{r.EnemyType} T{r.Tier}";
				var hp = r.CurrentHealth > 0f ? $"{r.CurrentHealth:0}/{r.MaxHealth:0} hp" : "full";
				sb.Append( $"\n    {kind} {hp} at ({m.x:0},{m.y:0},{m.z:0}) m, {Time.NowDouble - r.RememberedAt:0} s ago{slot}" );
			}
		}

		Log.Info( sb.ToString() );
	}
}
