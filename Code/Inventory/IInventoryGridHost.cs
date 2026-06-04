namespace Survival;

/// <summary>
/// One inventory grid the local player can interact with (player bag, chest, etc.).
/// Used for shift+left quick-move into another area.
/// </summary>
public interface IInventoryGridHost
{
	string GridId { get; }

	PlayerInventory Inventory { get; }

	/// <summary>First merge or empty slot in this grid, excluding <paramref name="fromSlotIndex"/>.</summary>
	bool TryFindQuickMoveTarget( in InventorySlot stack, int fromSlotIndex, out int targetSlotIndex );
}
