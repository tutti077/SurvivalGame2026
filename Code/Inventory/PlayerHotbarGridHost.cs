using System;

namespace Survival;

/// <summary><see cref="IInventoryGridHost"/> for the pawn hotbar.</summary>
public sealed class PlayerHotbarGridHost : IInventoryGridHost
{
	public string GridId => "hotbar";
	public PlayerInventory Inventory => null;
	public PlayerHotbar Hotbar { get; }

	public PlayerHotbarGridHost( PlayerHotbar hotbar ) => Hotbar = hotbar;

	public int SlotCount => PlayerHotbar.SlotCount;

	public InventorySlot GetSlot( int index ) =>
		Hotbar is not null ? Hotbar.GetSlot( index ) : InventorySlot.Empty;

	public bool OwnerTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		return Hotbar is not null && Hotbar.OwnerTryPickupAll( slotIndex, out picked );
	}

	public bool OwnerTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		return Hotbar is not null && Hotbar.OwnerTryFinishDragDrop( sourceSlotIndex, targetSlotIndex, ref held );
	}

	public bool OwnerTryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( Hotbar is null )
			return false;

		if ( Hotbar.HasHostAuthority )
			return Hotbar.HostTryPlaceHeld( slotIndex, ref held );

		var heldCopy = held;
		if ( !Hotbar.OwnerTryFinishDragDrop( -1, slotIndex, ref heldCopy ) )
			return false;

		held = heldCopy;
		return true;
	}

	public bool OwnerTryReturnStack( ref InventoryCursorStack held )
	{
		if ( Hotbar is null || held.IsEmpty )
			return false;

		if ( Hotbar.HasHostAuthority )
			return Hotbar.HostTryReturnStack( ref held );

		return false;
	}

	public bool OwnerTryTakeOne( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		return Hotbar is not null && Hotbar.OwnerTryTakeOne( slotIndex, out taken );
	}

	public bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held, out int placedCount )
	{
		placedCount = 0;
		return Hotbar is not null && Hotbar.OwnerTryDropOne( slotIndex, held, out placedCount );
	}

	public bool OwnerTryTakeHalf( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		return Hotbar is not null && Hotbar.OwnerTryTakeHalf( slotIndex, out taken );
	}

	public bool OwnerTryPlaceHalf( int slotIndex, ref InventoryCursorStack held ) =>
		Hotbar is not null && Hotbar.OwnerTryPlaceHalf( slotIndex, ref held );

	public bool TryFindQuickMoveTarget( in InventorySlot stack, int fromSlotIndex, out int targetSlotIndex )
	{
		targetSlotIndex = -1;
		return Hotbar is not null
		       && InventoryStackRules.TryFindQuickMoveTarget( Hotbar.Slots, stack, fromSlotIndex, out targetSlotIndex );
	}
}
