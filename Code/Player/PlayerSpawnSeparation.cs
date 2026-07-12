using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-only: spread NetworkHelper spawns that share one SpawnPoint, and cull unowned duplicate pawns.
/// </summary>
[Title( "Player Spawn Separation" )]
public sealed class PlayerSpawnSeparation : Component
{
	[Property] public float MinSeparation { get; set; } = 96f;
	[Property] public float UnownedDestroyDelaySeconds { get; set; } = 2.5f;

	TimeUntil _unownedCheck;

	protected override void OnStart()
	{
		base.OnStart();

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		SeparateByConnectionIndex();
		_unownedCheck = UnownedDestroyDelaySeconds;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( !Networking.IsHost )
			return;

		if ( _unownedCheck > 0f )
			return;

		_unownedCheck = UnownedDestroyDelaySeconds;
		DestroyUnownedDuplicatePawns();
	}

	void SeparateByConnectionIndex()
	{
		var index = ResolveOwnerConnectionIndex();
		if ( index <= 0 )
			return;

		var origin = GameObject.WorldPosition;
		GameObject.WorldPosition = origin + Vector3.Right * ( MinSeparation * index );

		if ( GameObject.Network is { Active: true } )
			GameObject.Network.ClearInterpolation();
	}

	int ResolveOwnerConnectionIndex()
	{
		var owner = GameObject.Network is { Active: true, Owner: { } o } ? o : null;
		if ( owner is null )
			return 0;

		var index = 0;
		foreach ( var connection in Connection.All )
		{
			if ( connection is null )
				continue;

			if ( ConnectionIdentity.SameClient( connection, owner ) )
				return index;

			index++;
		}

		return 0;
	}

	void DestroyUnownedDuplicatePawns()
	{
		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return;

		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is null || !vitals.GameObject.IsValid() )
				continue;

			var go = vitals.GameObject;
			if ( go == GameObject )
				continue;

			// Only networked player roots — never touch scene props / NetworkManager.
			if ( go.Components.Get<PlayerController>() is null )
				continue;

			if ( go.Network is not { Active: true } )
				continue;

			if ( go.Network.Owner is not null )
				continue;

			Log.Warning( $"[PlayerSpawnSeparation] Destroying unowned duplicate pawn '{go.Name}'." );
			go.Destroy();
		}
	}
}
