using System;
using Sandbox;

namespace Survival;

/// <summary>Hub console commands (rows in <see cref="ConsoleHelp"/>).</summary>
public static class HubDevCommands
{
	public const string HubPrefabPath = "prefabs/hub/hub.prefab";

	/// <summary>Host: take your pawn to the hub (spawning it if needed) as if through a portal right where you stand.</summary>
	[ConCmd( "hub" )]
	public static void ConCmdHub()
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Hub] hub: host only (clients build a portal)." );
			return;
		}

		var pawn = FindLocalPawn();
		if ( pawn is null )
		{
			Log.Warning( "[Hub] hub: no local pawn." );
			return;
		}

		var hub = HubWorld.Instance;
		if ( hub is not { IsValid: true } )
		{
			var prefab = GameObject.GetPrefab( HubPrefabPath );
			hub = HubWorld.HostEnsureSpawned( pawn.Scene, prefab,
				new Vector3( 0f, 0f, TerrainWorldUnits.MetersToEngine( 3000f ) ) );
			if ( hub is null )
				return;
		}

		hub.HostRememberReturn( pawn, pawn.WorldTransform );
		var arrive = hub.ArrivalTransform;
		pawn.Components.Get<PlayerMovement>()?.HostApplyEventSpawn( arrive.Position + Vector3.Up * 8f, arrive.Rotation );
		pawn.Components.Get<PlayerInventoryInteraction>()?.HostRefreshHubRecord( grantDailyAllowance: true );
	}

	/// <summary>Host: set your hub token balance.</summary>
	[ConCmd( "hub_tokens" )]
	public static void ConCmdTokens( int amount = -1 )
	{
		var pawn = FindLocalPawn();
		if ( pawn is null || (Networking.IsActive && !Networking.IsHost) )
			return;

		var record = HubStore.Get( HubIdentity.SteamKey( pawn ) );
		if ( amount >= 0 )
		{
			record.Tokens = amount;
			HubStore.Save( record );
			pawn.Components.Get<PlayerInventoryInteraction>()?.HostRefreshHubRecord();
		}

		Log.Info( $"[Hub] {record.Name} has {record.Tokens} tokens, arena {record.ArenaRecordText}, stratagem best {record.StratagemBest}." );
	}

	/// <summary>Re-read data/stratagems.json.</summary>
	[ConCmd( "stratagem_reload" )]
	public static void ConCmdStratagemReload()
	{
		StratagemCatalog.ForceReload();
		Log.Info( $"[Stratagem] {StratagemCatalog.All.Count} codes loaded." );
	}

	static GameObject FindLocalPawn()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return null;

		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is not null && vitals.IsLocalInputOwnedPawn() )
				return vitals.GameObject;
		}

		return null;
	}
}
