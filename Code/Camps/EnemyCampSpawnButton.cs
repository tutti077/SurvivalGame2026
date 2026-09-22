using Sandbox;

namespace Survival;

/// <summary>Press the configured input action (I by default) to stamp a test enemy camp at the configured spawn point.</summary>
[Title( "Enemy Camp Spawn Button" )]
public sealed class EnemyCampSpawnButton : Component
{
	[Property] public GameObject SpawnPoint { get; set; }

	[Property, Title( "Camp id (data/enemy_camps.json)" )]
	public string CampId { get; set; } = "scav_outpost_test";

	[Property, Title( "Input action" )]
	public string InputAction { get; set; } = "SpawnEnemyCamp";

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( string.IsNullOrWhiteSpace( InputAction ) || !Input.Pressed( InputAction ) )
			return;

		var anchor = SpawnPoint is { IsValid: true } ? SpawnPoint : GameObject;
		EnemyCampSpawner.TrySpawn( Scene, CampId, anchor.WorldPosition, anchor.WorldRotation.Angles().yaw );
	}
}
