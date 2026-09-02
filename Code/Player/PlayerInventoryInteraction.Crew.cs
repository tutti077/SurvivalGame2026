using System;
using Sandbox;

namespace Survival;

/// <summary>Crew intents (invite / accept / decline / leave) — client sends, host validates via <see cref="CrewRegistry"/>.</summary>
public sealed partial class PlayerInventoryInteraction
{
	const int CrewIntentInvite = 0;
	const int CrewIntentAccept = 1;
	const int CrewIntentDecline = 2;
	const int CrewIntentLeave = 3;

	public void OwnerCrewInvite( Guid targetPlayerId ) => SendCrewIntent( CrewIntentInvite, targetPlayerId );
	public void OwnerCrewAcceptInvite( Guid crewKey ) => SendCrewIntent( CrewIntentAccept, crewKey );
	public void OwnerCrewDeclineInvite( Guid crewKey ) => SendCrewIntent( CrewIntentDecline, crewKey );
	public void OwnerCrewLeave() => SendCrewIntent( CrewIntentLeave, default );

	void SendCrewIntent( int kind, Guid id )
	{
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			ApplyCrewIntentLocal( kind, id );
			return;
		}

		RpcHostCrewIntent( kind, id );
	}

	void ApplyCrewIntentLocal( int kind, Guid id )
	{
		switch ( kind )
		{
			case CrewIntentInvite:
				CrewRegistry.TryInvite( GameObject, id );
				break;
			case CrewIntentAccept:
				CrewRegistry.TryAcceptInvite( GameObject, id );
				break;
			case CrewIntentDecline:
				CrewRegistry.TryDeclineInvite( GameObject, id );
				break;
			case CrewIntentLeave:
				CrewRegistry.TryLeaveCrew( GameObject );
				break;
		}
	}

	[Rpc.Host]
	void RpcHostCrewIntent( int kind, Guid id )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
		{
			Log.Warning( "[Crew] Host rejected intent — caller is not pawn owner." );
			return;
		}

		ApplyCrewIntentLocal( kind, id );
	}

	public void OwnerCrewRename( string newName )
	{
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			CrewRegistry.TryRename( GameObject, newName );
			return;
		}

		RpcHostCrewRename( newName ?? "" );
	}

	[Rpc.Host]
	void RpcHostCrewRename( string newName )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
		{
			Log.Warning( "[Crew] Host rejected rename — caller is not pawn owner." );
			return;
		}

		CrewRegistry.TryRename( GameObject, newName );
	}
}
