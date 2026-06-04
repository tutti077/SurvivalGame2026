using System;

namespace Survival;

/// <summary>Default <see cref="IInventoryGridHost"/> for a pawn <see cref="PlayerInventory"/>.</summary>
public sealed class PlayerInventoryGridHost : IInventoryGridHost
{
	public string GridId { get; }
	public PlayerInventory Inventory { get; }

	public PlayerInventoryGridHost( string gridId, PlayerInventory inventory )
	{
		GridId = gridId;
		Inventory = inventory;
	}

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
