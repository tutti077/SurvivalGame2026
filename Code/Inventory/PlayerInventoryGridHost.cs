using System;

namespace Survival;

/// <summary>Default <see cref="IInventoryGridHost"/> for a pawn <see cref="PlayerInventory"/>.</summary>
public sealed class PlayerInventoryGridHost : IInventoryGridHost
{
	public string GridId { get; }
	public PlayerInventory Inventory { get; }
	public PlayerHotbar Hotbar => null;

	public PlayerInventoryGridHost( string gridId, PlayerInventory inventory )
	{
		GridId = gridId;
		Inventory = inventory;
	}

	public int SlotCount => Inventory?.SlotCount ?? 0;

	public InventorySlot GetSlot( int index ) =>
		Inventory is not null ? Inventory.GetSlot( index ) : InventorySlot.Empty;

	public bool OwnerTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		return Inventory is not null && Inventory.OwnerTryPickupAll( slotIndex, out picked );
	}

	public bool OwnerTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		return Inventory is not null && Inventory.OwnerTryFinishDragDrop( sourceSlotIndex, targetSlotIndex, ref held );
	}

	public bool OwnerTryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		return Inventory is not null && Inventory.OwnerTryPlaceHeld( slotIndex, ref held );
	}

	public bool OwnerTryReturnStack( ref InventoryCursorStack held )
	{
		if ( Inventory is null || held.IsEmpty )
			return false;

		if ( Inventory.HasHostAuthority )
			return Inventory.HostTryReturnStack( held );

		return false;
	}

	public bool OwnerTryTakeOne( int slotIndex ) =>
		Inventory is not null && Inventory.OwnerTryTakeOne( slotIndex );

	public bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held ) =>
		Inventory is not null && Inventory.OwnerTryDropOne( slotIndex, held );

	public bool OwnerTryTakeHalf( int slotIndex ) =>
		Inventory is not null && Inventory.OwnerTryTakeHalf( slotIndex );

	public bool OwnerTryPlaceHalf( int slotIndex, ref InventoryCursorStack held ) =>
		Inventory is not null && Inventory.OwnerTryPlaceHalf( slotIndex, ref held );

	public bool TryFindQuickMoveTarget( in InventorySlot stack, int fromSlotIndex, out int targetSlotIndex )
	{
		targetSlotIndex = -1;
		if ( Inventory is null || stack.IsEmpty )
			return false;

		var slots = Inventory.Slots;
		for ( var i = 0; i < slots.Length; i++ )
		{
			if ( i == fromSlotIndex )
				continue;

			if ( !slots[i].IsEmpty && !string.Equals( slots[i].ResourceId, stack.ResourceId, StringComparison.OrdinalIgnoreCase ) )
				continue;

			targetSlotIndex = i;
			return true;
		}

		return false;
	}
}
