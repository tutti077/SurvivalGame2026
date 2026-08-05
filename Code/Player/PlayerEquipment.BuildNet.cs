using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Build hammer RPCs live on the networked pawn (<see cref="PlayerEquipment"/>), not on the
/// locally-cloned tool prefab — non-networked GameObjects cannot invoke Rpc.Host.
/// </summary>
public sealed partial class PlayerEquipment
{
	/// <summary>Owner/client: place a build piece (host validates + NetworkSpawns).</summary>
	public void OwnerRequestPlacePiece( string pieceId, Vector3 position, Rotation rotation, bool blueprintMode )
	{
		if ( !GameObject.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			BuildAuthority.TryPlacePiece( GameObject, pieceId, new Transform( position, rotation ), blueprintMode, out _ );
			return;
		}

		RpcHostPlacePiece( pieceId, position, rotation, blueprintMode );
	}

	/// <summary>Owner/client: repair (blueprint → solid) a networked build piece.</summary>
	public void OwnerRequestRepairBuildPiece( Guid targetId )
	{
		if ( !GameObject.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			if ( TryResolveBuildPiece( targetId, out var piece ) )
				BuildAuthority.TryRepairBuildPiece( GameObject, piece );
			return;
		}

		RpcHostRepairBuildPiece( targetId );
	}

	/// <summary>Owner/client: destroy a networked build piece.</summary>
	public void OwnerRequestDestroyBuildPiece( Guid targetId )
	{
		if ( !GameObject.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			if ( TryResolveBuildPiece( targetId, out var piece ) )
				BuildAuthority.TryDestroyBuildPiece( GameObject, piece );
			return;
		}

		RpcHostDestroyBuildPiece( targetId );
	}

	[Rpc.Host]
	void RpcHostPlacePiece( string pieceId, Vector3 position, Rotation rotation, bool blueprintMode )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		BuildAuthority.TryPlacePiece( GameObject, pieceId, new Transform( position, rotation ), blueprintMode, out _ );
	}

	[Rpc.Host]
	void RpcHostRepairBuildPiece( Guid targetId )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		if ( !TryResolveBuildPiece( targetId, out var piece ) )
			return;

		BuildAuthority.TryRepairBuildPiece( GameObject, piece );
	}

	[Rpc.Host]
	void RpcHostDestroyBuildPiece( Guid targetId )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		if ( !TryResolveBuildPiece( targetId, out var piece ) )
			return;

		BuildAuthority.TryDestroyBuildPiece( GameObject, piece );
	}

	static bool TryResolveBuildPiece( Guid targetId, out BuildPiece piece )
	{
		piece = null;
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;

		foreach ( var candidate in scene.GetAllComponents<BuildPiece>() )
		{
			if ( candidate is null || !candidate.IsValid() || candidate.GameObject.Id != targetId )
				continue;

			piece = candidate;
			return true;
		}

		return false;
	}
}
