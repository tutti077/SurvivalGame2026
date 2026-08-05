using Sandbox;

namespace Survival;

/// <summary>
/// Host-only NetworkSpawn helper. Listen-server / offline: no-ops when networking is inactive.
/// Runtime clones must call this or remotes never see the object (Clone alone is machine-local).
/// </summary>
public static class HostNetworkSpawn
{
	/// <summary>
	/// Marks the object <see cref="NetworkMode.Object"/> and NetworkSpawns it on the host.
	/// Safe to call offline (returns without spawning).
	/// </summary>
	public static bool TrySpawn( GameObject go )
	{
		if ( go is null || !go.IsValid() )
			return false;

		if ( !Networking.IsActive )
			return false;

		if ( !Networking.IsHost )
			return false;

		if ( go.Network is { Active: true } )
			return true;

		go.NetworkMode = NetworkMode.Object;
		go.NetworkSpawn();
		return go.Network is { Active: true };
	}
}
