using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Spread NetworkHelper spawns that share one SpawnPoint, and cull duplicate / ghost pawns.
/// Host culls networked-unowned pawns; every peer also destroys orphan citizen Body meshes
/// that are not under a PlayerController (Object-mode child desync leftover).
/// </summary>
[Title( "Player Spawn Separation" )]
public sealed class PlayerSpawnSeparation : Component
{
	[Property] public float MinSeparation { get; set; } = 96f;
	[Property] public float UnownedDestroyDelaySeconds { get; set; } = 2.5f;

	TimeUntil _unownedCheck;
	bool _didSeparate;

	protected override void OnStart()
	{
		base.OnStart();

		// Offset only on the host spawn path (or offline). Proxies receive the host transform.
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			SeparateByConnectionIndex();
			_didSeparate = true;
		}

		_unownedCheck = UnownedDestroyDelaySeconds;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( !_didSeparate
		     && GameObject.Network is { Active: true }
		     && Networking.IsHost )
		{
			SeparateByConnectionIndex();
			_didSeparate = true;
		}

		if ( _unownedCheck > 0f )
			return;

		_unownedCheck = UnownedDestroyDelaySeconds;
		DestroyDuplicatePawns();
		DestroyOrphanCitizenBodies();
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

	void DestroyDuplicatePawns()
	{
		if ( !Networking.IsActive )
			return;

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

			// Only player roots — never scene props / NetworkManager / dummies without a controller.
			if ( go.Components.Get<PlayerController>() is null )
				continue;

			var net = go.Network;

			// Local leftover: cloned without NetworkSpawn. Only that peer sees it; stays undressed.
			if ( net is not { Active: true } )
			{
				Log.Warning( $"[PlayerSpawnSeparation] Destroying non-networked ghost pawn '{go.Name}'." );
				go.Destroy();
				continue;
			}

			// Networked but never owned — host authority only.
			if ( Networking.IsHost && net.Owner is null )
			{
				Log.Warning( $"[PlayerSpawnSeparation] Destroying unowned duplicate pawn '{go.Name}'." );
				go.Destroy();
			}
		}
	}

	/// <summary>
	/// Object-mode Body children under Object roots can deserialize as stray citizen meshes on
	/// the joining client (no PlayerController ancestor). Destroy those only — never walk to scene root.
	/// Skips enemy/scav citizen bodies (EntityBrain).
	/// </summary>
	void DestroyOrphanCitizenBodies()
	{
		if ( !Networking.IsActive )
			return;

		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return;

		foreach ( var renderer in scene.GetAllComponents<SkinnedModelRenderer>() )
		{
			if ( renderer is null || !renderer.IsValid() || !renderer.GameObject.IsValid() )
				continue;

			var model = renderer.Model;
			if ( model is null || !model.IsValid )
				continue;

			var path = model.ResourcePath ?? model.Name ?? string.Empty;
			if ( !path.Contains( "citizen/citizen", StringComparison.OrdinalIgnoreCase ) )
				continue;

			var go = renderer.GameObject;
			if ( !IsPlayerBodyOrphan( go ) )
				continue;

			Log.Warning( $"[PlayerSpawnSeparation] Destroying orphan citizen body '{go.Name}'." );
			go.Destroy();
		}
	}

	static bool IsPlayerBodyOrphan( GameObject go )
	{
		// Must look like the player Body child (name), not a random prop.
		if ( !string.Equals( go.Name, "Body", StringComparison.OrdinalIgnoreCase ) )
			return false;

		for ( var walk = go; walk is not null && walk.IsValid(); walk = walk.Parent )
		{
			if ( walk.Components.Get<PlayerController>() is not null )
				return false;

			if ( walk.Components.Get<EntityBrain>() is not null )
				return false;

			if ( walk.Components.Get<PlayerVitals>() is not null )
				return false;
		}

		// Detached Body at/near scene root — not under any pawn or enemy.
		return true;
	}
}
