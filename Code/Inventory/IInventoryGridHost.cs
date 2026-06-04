namespace Survival;

/// <summary>
/// One inventory grid the local player can interact with (player bag, hotbar, chest, etc.).
/// </summary>
public interface IInventoryGridHost
{
	string GridId { get; }

	PlayerInventory Inventory { get; }

	PlayerHotbar Hotbar { get; }

	int SlotCount { get; }

	InventorySlot GetSlot( int index );

	bool OwnerTryPickupAll( int slotIndex, out InventorySlot picked );

	bool OwnerTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held );

	bool OwnerTryPlaceHeld( int slotIndex, ref InventoryCursorStack held );

	bool OwnerTryReturnStack( ref InventoryCursorStack held );

	bool OwnerTryTakeOne( int slotIndex, out InventorySlot taken );

	bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held, out int placedCount );

	bool OwnerTryTakeHalf( int slotIndex, out InventorySlot taken );

	bool OwnerTryPlaceHalf( int slotIndex, ref InventoryCursorStack held );

	/// <summary>First merge or empty slot in this grid, excluding <paramref name="fromSlotIndex"/>.</summary>
	bool TryFindQuickMoveTarget( in InventorySlot stack, int fromSlotIndex, out int targetSlotIndex );
}
