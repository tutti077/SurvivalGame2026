using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Wire stripper intent: the owner released on a sphere after pressing on another
/// (<see cref="ToolWireStripper"/>). Sent as intent; the host validates once and commits through
/// <see cref="CircuitRegistry.HostToggleLink"/>.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	/// <summary>Owner: lay (or remove) the wire <paramref name="source"/> → <paramref name="target"/>.</summary>
	public void OwnerRequestToggleCircuitLink( CircuitNode source, CircuitNode target, CircuitCable cable, float reachMeters )
	{
		if ( source is null || !source.IsValid() || target is null || !target.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			CircuitRegistry.HostToggleLink( source, target, cable, GameObject, reachMeters );
			return;
		}

		RpcHostToggleCircuitLink( source.GameObject.Id, target.GameObject.Id, (int)cable, reachMeters );
	}

	[Rpc.Host]
	void RpcHostToggleCircuitLink( Guid sourceId, Guid targetId, int cable, float reachMeters )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		var source = CircuitNode.FindById( Scene, sourceId );
		var target = CircuitNode.FindById( Scene, targetId );
		var cableColor = (CircuitCable)Math.Clamp( cable, 0, CircuitCableExtensions.Count - 1 );

		// The reach the host applies is its own cap, not whatever the client sent.
		var reach = Math.Clamp( reachMeters, 0.5f, 12f );
		CircuitRegistry.HostToggleLink( source, target, cableColor, GameObject, reach );
	}
}
