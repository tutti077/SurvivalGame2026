using Sandbox;

namespace Survival;

/// <summary>
/// Host→client world identity for listen-server joins. Lives on the scene NetworkManager
/// (same object as <see cref="CombatAuthority"/>) so Sync reaches remotes. Clients wait for
/// <see cref="HostWorldReady"/> before terrain mesh/vegetation scatter.
/// </summary>
[Title( "World Network Session" )]
public sealed class WorldNetworkSession : Component
{
	public static WorldNetworkSession Instance { get; private set; }

	[Sync( SyncFlags.FromHost )]
	public int WorldSeed { get; set; } = 1337;

	[Sync( SyncFlags.FromHost )]
	public string WorldName { get; set; } = "TestWorld";

	[Sync( SyncFlags.FromHost )]
	public bool HostWorldReady { get; set; }

	protected override void OnEnabled()
	{
		base.OnEnabled();
		Instance = this;
	}

	protected override void OnDisabled()
	{
		if ( Instance == this )
			Instance = null;
		base.OnDisabled();
	}

	/// <summary>Host: publish seed/name so joining clients can generate the same world.</summary>
	public void HostPublish( string worldName, int worldSeed )
	{
		if ( Networking.IsActive && !Networking.IsHost )
			return;

		WorldName = string.IsNullOrWhiteSpace( worldName ) ? "TestWorld" : worldName;
		WorldSeed = worldSeed;
		HostWorldReady = true;
	}
}
