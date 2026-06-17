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
		var scene = Scene;
		if ( !scene.IsValid() )
			return;

		BuildNavMeshSync.EnsureBuildTraversalSettings( scene );

		var spawnPos = SpawnPoint is { IsValid: true }
			? SpawnPoint.WorldPosition
			: GameObject.WorldPosition;

		var instance = BuildPrefabUtility.GetTemplate( PrefabPath )?.Clone();
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[EnemySpawnButton] Failed to clone prefab '{PrefabPath}'." );
			return;
		}

		instance.Parent = scene;
		instance.WorldPosition = spawnPos;
		if ( SpawnPoint is { IsValid: true } )
			instance.WorldRotation = SpawnPoint.WorldRotation;

		EntityEnemySetup.Configure( instance, EnemyType, Tier, SpawnHealth );
	}
}
