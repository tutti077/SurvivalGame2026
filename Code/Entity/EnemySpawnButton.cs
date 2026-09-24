using Sandbox;

namespace Survival;

/// <summary>Press K (SpawnEnemy) to spawn a test enemy at the configured spawn point.</summary>
[Title( "Enemy Spawn Button" )]
public sealed class EnemySpawnButton : Component
{
	[Property] public GameObject SpawnPoint { get; set; }

	[Property] public string PrefabPath { get; set; } = "prefabs/entity/scavT1.prefab";

	[Property] public EnemyType EnemyType { get; set; } = EnemyType.Scav;

	[Property] public int Tier { get; set; } = 1;

	[Property, Title( "Override max HP (0 = archetype default)" )]
	public float SpawnHealth { get; set; }

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( !Input.Pressed( "SpawnEnemy" ) )
			return;

		SpawnEnemy();
	}

	void SpawnEnemy()
	{
		var spawnPos = SpawnPoint is { IsValid: true }
			? SpawnPoint.WorldPosition
			: GameObject.WorldPosition;
		var spawnRot = SpawnPoint is { IsValid: true } ? SpawnPoint.WorldRotation : GameObject.WorldRotation;

		HostSpawn( Scene, PrefabPath, EnemyType, Tier, spawnPos, spawnRot, SpawnHealth );
	}

	/// <summary>Host: stamp one enemy of <paramref name="enemyType"/> from <paramref name="prefabPath"/> at a spot. Console spawners use this too.</summary>
	public static GameObject HostSpawn( Scene scene, string prefabPath, EnemyType enemyType, int tier, Vector3 position, Rotation rotation, float healthOverride = 0f )
	{
		if ( scene is null || !scene.IsValid() )
			return null;

		if ( Networking.IsActive && !Networking.IsHost )
			return null;

		BuildNavMeshSync.EnsureBuildTraversalSettings( scene );

		var instance = BuildPrefabUtility.GetTemplate( prefabPath )?.Clone();
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[EnemySpawnButton] Failed to clone prefab '{prefabPath}'." );
			return null;
		}

		instance.Parent = scene;
		instance.WorldPosition = position;
		instance.WorldRotation = rotation;

		EntityEnemySetup.Configure( instance, enemyType, tier, healthOverride );

		// Clone alone is host-local — remotes never see the enemy without NetworkSpawn.
		if ( Networking.IsActive && !HostNetworkSpawn.TrySpawn( instance ) )
		{
			Log.Warning( $"[EnemySpawnButton] NetworkSpawn failed for '{prefabPath}' — destroying local clone." );
			instance.Destroy();
			return null;
		}

		return instance;
	}
}
