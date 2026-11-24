using System;
using Sandbox;

namespace Survival;

/// <summary>
/// The host's copy of the owning client's cursor stack: the item "in the hand" while it travels between
/// grids (bag, hotbar, chest, paperdoll, augment bank and sockets).
/// <para>
/// A remote client only sends intent ("pick up slot 3", "place on slot 7"). It never tells the host what
/// the cursor holds. A take credits this stack and a place spends it, so an item can only reach a slot if
/// the host saw it leave another one. The client still predicts with its own
/// <see cref="InventoryCursorStack"/>; when the two disagree the host's version wins and the owner is sent
/// the real slots.
/// </para>
/// <para>
/// The host's own pawn and offline play never use this: their cursor is the local one and they call the
/// <c>Host*</c> methods directly.
/// </para>
/// </summary>
public sealed partial class PlayerInventory
{
	InventoryCursorStack _hostCursor;

	/// <summary>Host: the cursor stack every grid's remote place / drop spends from.</summary>
	public ref InventoryCursorStack HostCursor => ref _hostCursor;

	/// <summary>Host: the pawn inventory driven by <paramref name="connection"/> (a chest RPC needs its caller's cursor).</summary>
	public static PlayerInventory FindForConnection( Scene scene, Connection connection )
	{
		if ( scene is null || !scene.IsValid() || connection is null )
			return null;

		foreach ( var inventory in scene.GetAllComponents<PlayerInventory>() )
		{
			if ( inventory.GameObject.Network is { Active: true, Owner: { } owner }
			     && ConnectionIdentity.SameClient( owner, connection ) )
				return inventory;
		}

		return null;
	}

	/// <summary>True when the running RPC came from the client that owns this pawn (or there is no remote owner to compare).</summary>
	bool IsCallerOwner()
	{
		if ( GameObject.Network is not { Active: true, Owner: { } owner } || Rpc.Caller is not { } caller )
			return true;

		return ConnectionIdentity.SameClient( caller, owner );
	}

	bool AcceptsOwnerRpc() => Networking.IsHost && GameObject.IsValid() && IsCallerOwner();

	/// <summary>
	/// Host, before a take: the client only takes onto an empty cursor or onto the same item. Anything else
	/// still here is a remainder the client no longer shows, so it goes back to the bag first. An empty
	/// <paramref name="resourceId"/> means the slot has nothing to take: the cursor is left alone.
	/// </summary>
	public void HostCursorMakeRoomFor( string resourceId )
	{
		if ( _hostCursor.IsEmpty || string.IsNullOrWhiteSpace( resourceId ) || _hostCursor.CanStack( resourceId ) )
			return;

		HostSettleCursor();
	}

	/// <summary>Host: a stack that left a grid lands on the cursor.</summary>
	public void HostCursorCredit( in InventorySlot taken )
	{
		if ( taken.IsEmpty )
			return;

		if ( _hostCursor.IsEmpty )
		{
			_hostCursor.Set( taken.ResourceId, taken.Count, taken.Wear, taken.CrafterName, taken.Tier );
			return;
		}

		if ( _hostCursor.CanStack( taken.ResourceId ) )
		{
			_hostCursor.Count += taken.Count;
			return;
		}

		// The cursor still holds something that would not settle (bag full, drop refused): never overwrite it.
		var overflow = new InventoryCursorStack();
		overflow.Set( taken.ResourceId, taken.Count, taken.Wear, taken.CrafterName, taken.Tier );
		AbsorbCursorStackIntoBagSlots( ref overflow, notify: true );
		if ( !overflow.IsEmpty && !HeldStackWorldDrop.TryDropAtPlayer( GameObject, ref overflow ) )
			Log.Warning( $"[PlayerInventory] {GameObject.Name}: {overflow.Count}x {overflow.ResourceId} had nowhere to go and was lost." );
	}

	/// <summary>Host: <paramref name="count"/> units left the cursor (a single drop, a toss into the world).</summary>
	public void HostCursorSpend( int count )
	{
		_hostCursor.Count -= count;
		if ( _hostCursor.Count <= 0 )
			_hostCursor.Clear();
	}

	/// <summary>
	/// Host: the cursor goes back to the bag; what does not fit drops at the pawn's feet. True when the
	/// cursor is empty afterwards.
	/// </summary>
	public bool HostSettleCursor()
	{
		if ( _hostCursor.IsEmpty )
			return true;

		AbsorbCursorStackIntoBagSlots( ref _hostCursor, notify: false );
		if ( !_hostCursor.IsEmpty )
			HeldStackWorldDrop.TryDropAtPlayer( GameObject, ref _hostCursor );

		// Always tell the owner: its own prediction of this absorb may have used a different stack.
		NotifyInventoryChanged();
		return _hostCursor.IsEmpty;
	}

	// ── Bag slots acted on with the host cursor (the bodies of the RPCs below) ──────────────────────────

	public void HostCursorPickupAll( int slotIndex )
	{
		HostCursorMakeRoomFor( GetSlot( slotIndex ).ResourceId );
		if ( HostTryPickupAll( slotIndex, out var picked ) )
			HostCursorCredit( picked );
		else
			PushSlotsToOwner();
	}

	public void HostCursorTakeOne( int slotIndex ) => HostCursorTake( slotIndex, half: false );

	public void HostCursorTakeHalf( int slotIndex ) => HostCursorTake( slotIndex, half: true );

	void HostCursorTake( int slotIndex, bool half )
	{
		var source = GetSlot( slotIndex );
		HostCursorMakeRoomFor( source.ResourceId );

		var count = half ? source.Count / 2 : 1;
		var taken = !source.IsEmpty && ( half ? HostTryTakeHalf( slotIndex ) : HostTryTakeOne( slotIndex ) );
		if ( !taken )
		{
			PushSlotsToOwner();
			return;
		}

		HostCursorCredit( new InventorySlot { ResourceId = source.ResourceId, Count = count, Wear = source.Wear, CrafterName = source.CrafterName, Tier = source.Tier } );
	}

	public void HostCursorPlaceHeld( int slotIndex )
	{
		if ( !HostTryPlaceHeld( slotIndex, ref _hostCursor ) )
			PushSlotsToOwner();
	}

	public void HostCursorFinishDragDrop( int sourceSlotIndex, int targetSlotIndex )
	{
		if ( !HostTryFinishDragDrop( sourceSlotIndex, targetSlotIndex, ref _hostCursor ) )
			PushSlotsToOwner();
	}

	public void HostCursorDropOne( int slotIndex )
	{
		if ( HostTryDropOne( slotIndex, _hostCursor ) )
			HostCursorSpend( 1 );
		else
			PushSlotsToOwner();
	}

	public void HostCursorPlaceHalf( int slotIndex )
	{
		if ( !HostTryPlaceHalf( slotIndex, ref _hostCursor ) )
			PushSlotsToOwner();
	}

	// ── Owner → host intent ─────────────────────────────────────────────────────────────────────────────

	/// <summary>Owner: whatever is left on the cursor is thrown away (the dev supply's surplus is simply not taken).</summary>
	public void OwnerDiscardCursor()
	{
		if ( !HasHostAuthority && IsLocalManagingClient() )
			RpcHostDiscardCursor();
	}

	[Rpc.Host]
	void RpcHostPickupAll( int slotIndex )
	{
		if ( AcceptsOwnerRpc() )
			HostCursorPickupAll( slotIndex );
	}

	[Rpc.Host]
	void RpcHostTakeOne( int slotIndex )
	{
		if ( AcceptsOwnerRpc() )
			HostCursorTakeOne( slotIndex );
	}

	[Rpc.Host]
	void RpcHostTakeHalf( int slotIndex )
	{
		if ( AcceptsOwnerRpc() )
			HostCursorTakeHalf( slotIndex );
	}

	[Rpc.Host]
	void RpcHostPlaceHeld( int slotIndex )
	{
		if ( AcceptsOwnerRpc() )
			HostCursorPlaceHeld( slotIndex );
	}

	[Rpc.Host]
	void RpcHostFinishDragDrop( int sourceSlotIndex, int targetSlotIndex )
	{
		if ( AcceptsOwnerRpc() )
			HostCursorFinishDragDrop( sourceSlotIndex, targetSlotIndex );
	}

	[Rpc.Host]
	void RpcHostDropOne( int slotIndex )
	{
		if ( AcceptsOwnerRpc() )
			HostCursorDropOne( slotIndex );
	}

	[Rpc.Host]
	void RpcHostPlaceHalf( int slotIndex )
	{
		if ( AcceptsOwnerRpc() )
			HostCursorPlaceHalf( slotIndex );
	}

	/// <summary>The owner's cursor went back to the bag (menu closed, quick-move leftover).</summary>
	[Rpc.Host]
	void RpcHostSettleCursor()
	{
		if ( !AcceptsOwnerRpc() )
			return;

		// Nothing to return means the owner's cursor was a prediction the host never agreed to: show it the real bag.
		if ( _hostCursor.IsEmpty )
			PushSlotsToOwner();
		else
			HostSettleCursor();
	}

	[Rpc.Host]
	void RpcHostDiscardCursor()
	{
		if ( AcceptsOwnerRpc() )
			_hostCursor.Clear();
	}
}
