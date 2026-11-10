using Sandbox;

namespace Survival;

/// <summary>Binds a spawned population entity to its biome slot for respawn / permanent death.</summary>
public sealed class BiomePopulationSlot : Component
{
	[Property] public string SlotKey { get; set; }
	[Property] public bool Respawn { get; set; } = true;
	[Property] public float RespawnDelaySeconds { get; set; } = 90f;
	[Property] public string PrefabPath { get; set; }
	[Property] public EnemyType EnemyType { get; set; } = EnemyType.Scav;
	[Property] public int Tier { get; set; } = 1;
	/// <summary>Animal slots: the species the respawn is configured as (null = an enemy). Set from the entry, never authored.</summary>
	public AnimalSpecies? Species { get; set; }

	bool _died;
	EntityVitals _vitals;
	Vector3 _spawnWorldPos;
	GameObject _chunkRoot;

	public void Configure( string slotKey, BiomePopulationEntry entry )
	{
		SlotKey = slotKey;
		Respawn = entry.Respawn;
		RespawnDelaySeconds = entry.RespawnDelaySeconds;
		PrefabPath = entry.PrefabPath;
		EnemyType = entry.EnemyType;
		Tier = entry.Tier;
		Species = entry.Species;
	}

	protected override void OnStart()
	{
		_spawnWorldPos = GameObject.WorldPosition;
		_chunkRoot = GameObject.Parent;
		_vitals = Components.Get<EntityVitals>();
		if ( _vitals is not null )
			_vitals.OnDied += OnDied;
	}

	protected override void OnDestroy()
	{
		if ( _vitals is not null )
			_vitals.OnDied -= OnDied;

		if ( _died )
			return;

		BiomePopulationRegistry.NotifyUnloaded( SlotKey );
	}

	void OnDied()
	{
		HandleOwnerDied();
	}

	/// <summary>Called from vitals death and from <see cref="EntityBrain"/> before destroy so unload vs death is correct.</summary>
	public void HandleOwnerDied()
	{
		if ( _died )
			return;

		_died = true;
		BiomePopulationRegistry.NotifyDied( SlotKey, Respawn, RespawnDelaySeconds );

		if ( !Respawn )
			return;

		// A camp guard keeps its camp's leash through the respawn (read from the brain, so a guard restored from
		// EntityChunkMemory carries it too)
		var brain = Components.Get<EntityBrain>();
		BiomePopulationRespawnQueue.EnqueueFromSlot(
			SlotKey,
			PrefabPath,
			EnemyType,
			Tier,
			Respawn,
			RespawnDelaySeconds,
			_spawnWorldPos != default ? _spawnWorldPos : GameObject.WorldPosition,
			_chunkRoot.IsValid() ? _chunkRoot : GameObject.Parent,
			brain?.LeashCenter ?? default,
			brain?.LeashWanderDistance ?? 0f,
			brain?.LeashMaxTravelDistance ?? 0f,
			Species );
	}
}
