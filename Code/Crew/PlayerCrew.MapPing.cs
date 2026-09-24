using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Map pings (middle mouse on the Map page). The owner broadcasts a world position; every
/// machine whose local player is in the sender's crew (or is the sender) shows it for a few
/// seconds through <see cref="MapPingFeed"/>. Lives on the crew component because a ping is a
/// crew-scoped message, not a map-page detail.
/// </summary>
public sealed partial class PlayerCrew
{
	/// <summary>Owner-side entry: broadcast a ping at <paramref name="worldMeters"/> (world meters from center).</summary>
	public void OwnerSendMapPing( Vector2 worldMeters )
	{
		var name = CrewRegistry.ResolvePawnDisplayName( GameObject );
		RpcMapPing( worldMeters, name );
	}

	[Rpc.Broadcast]
	void RpcMapPing( Vector2 worldMeters, string senderName )
	{
		if ( !LocalPlayerCanSeePingFrom( PlayerKey ) )
			return;

		MapPingFeed.Add( worldMeters, senderName );
	}

	/// <summary>True when the local input-owned pawn is the sender or shares the sender's crew.</summary>
	static bool LocalPlayerCanSeePingFrom( Guid senderKey )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;

		foreach ( var crew in scene.GetAllComponents<PlayerCrew>() )
		{
			if ( crew is null || !crew.IsValid() )
				continue;

			var vitals = crew.Components.Get<PlayerVitals>();
			if ( vitals is null || !vitals.IsLocalInputOwnedPawn() )
				continue;

			if ( crew.PlayerKey == senderKey )
				return true;

			var myCrew = crew.GetMyCrew();
			if ( myCrew is null )
				return false;

			foreach ( var member in myCrew.Members )
			{
				if ( member.PlayerId == senderKey )
					return true;
			}

			return false;
		}

		return false;
	}
}
