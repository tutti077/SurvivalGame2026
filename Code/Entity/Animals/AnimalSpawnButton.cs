using Sandbox;

namespace Survival;

/// <summary>Press the configured input action to spawn a test animal at the configured spawn point.</summary>
[Title( "Animal Spawn Button" )]
public sealed class AnimalSpawnButton : Component
{
	[Property] public GameObject SpawnPoint { get; set; }

	[Property] public string PrefabPath { get; set; } = "prefabs/entity/fox.prefab";

	[Property] public AnimalSpecies Species { get; set; } = AnimalSpecies.Fox;

	[Property, Title( "Override max HP (0 = behavior default)" )]
	public float SpawnHealth { get; set; }

	[Property, Title( "Input action" )]
	public string InputAction { get; set; } = "SpawnAnimal";

	[Property, Title( "Log AI state changes on spawned animals" )]
	public bool LogStateDebug { get; set; }

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( string.IsNullOrWhiteSpace( InputAction ) || !Input.Pressed( InputAction ) )
			return;

		SpawnAnimal();
	}

	void SpawnAnimal()
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
			Log.Warning( $"[AnimalSpawnButton] Failed to clone prefab '{PrefabPath}'." );
			return;
		}

		instance.Parent = scene;
		instance.WorldPosition = spawnPos;
		if ( SpawnPoint is { IsValid: true } )
			instance.WorldRotation = SpawnPoint.WorldRotation;

		AnimalSetup.Configure( instance, Species, SpawnHealth, LogStateDebug );

		// Clone alone is host-local — remotes never see the animal without NetworkSpawn.
		if ( Networking.IsActive && !HostNetworkSpawn.TrySpawn( instance ) )
		{
			Log.Warning( $"[AnimalSpawnButton] NetworkSpawn failed for '{PrefabPath}' — destroying local clone." );
			instance.Destroy();
		}
	}
}
