using Sandbox;

namespace Survival;

/// <summary>Press the configured input action (H by default) to spawn a test boss at the configured spawn point.</summary>
[Title( "Boss Spawn Button" )]
public sealed class BossSpawnButton : Component
{
	[Property] public GameObject SpawnPoint { get; set; }

	[Property, Title( "Boss id (data/bosses.json)" )]
	public string BossId { get; set; } = "scav_warlord";

	[Property, Title( "Input action" )]
	public string InputAction { get; set; } = "SpawnBoss";

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( string.IsNullOrWhiteSpace( InputAction ) || !Input.Pressed( InputAction ) )
			return;

		var anchor = SpawnPoint is { IsValid: true } ? SpawnPoint : GameObject;
		BossSpawner.TrySpawn( Scene, BossId, anchor.WorldPosition, anchor.WorldRotation.Angles().yaw );
	}
}
