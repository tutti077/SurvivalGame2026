using Sandbox;

namespace Survival;

/// <summary>
/// Host→client world identity for listen-server joins. Lives on the scene NetworkManager
/// (same object as <see cref="CombatAuthority"/>) so Sync reaches remotes. Clients wait for
/// <see cref="HostWorldReady"/> before terrain mesh/vegetation scatter. Also carries the host-wide dev settings
/// (<see cref="DevToolsSettings.InfiniteStamina"/>, <see cref="DevToolsSettings.DayLocked"/>) to every peer.
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

	/// <summary>Host-wide dev setting (Settings → Dev tools): stamina costs nothing for anyone.</summary>
	[Sync( SyncFlags.FromHost )]
	public bool DevInfiniteStamina { get; set; } = true;

	/// <summary>Host-wide dev setting (Settings → Dev tools): the clock holds at noon on every peer.</summary>
	[Sync( SyncFlags.FromHost )]
	public bool DevDayLocked { get; set; }

	protected override void OnEnabled()
	{
		base.OnEnabled();
		Instance = this;
		HostPublishDevSettings();
	}

	/// <summary>Host / offline: copy the host's saved dev settings into the synced fields.</summary>
	public void HostPublishDevSettings()
	{
		if ( !DevToolsSettings.CanChangeHostSettings )
			return;

		DevInfiniteStamina = DevToolsSettings.SavedInfiniteStamina;
		DevDayLocked = DevToolsSettings.SavedDayLocked;
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
