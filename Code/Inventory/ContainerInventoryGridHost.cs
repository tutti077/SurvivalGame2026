namespace Survival;

/// <summary>
/// <see cref="IInventoryGridHost"/> for the currently opened world container (chest, etc.).
/// One instance lives on <see cref="PlayerInventoryInteraction"/>; <see cref="Container"/> is
/// swapped when the player opens/closes a container. All ops no-op while closed.
/// </summary>
public sealed class ContainerInventoryGridHost : IInventoryGridHost
{
	public const string ContainerGridId = "container";

	public string GridId => ContainerGridId;
	public PlayerInventory Inventory => null;
	public PlayerHotbar Hotbar => null;

	/// <summary>Currently opened container; null when no container UI is showing.</summary>
	public ContainerInventory Container { get; set; }

	public bool IsActive => Container is not null && Container.IsValid();

	public int SlotCount => IsActive ? Container.SlotCount : 0;

	public InventorySlot GetSlot( int index ) =>
		IsActive ? Container.GetSlot( index ) : InventorySlot.Empty;

	public bool OwnerTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		return IsActive && Container.TryPickupAll( slotIndex, out picked );
	}

	public bool OwnerTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held ) =>
		IsActive && Container.TryFinishDragDrop( sourceSlotIndex, targetSlotIndex, ref held );

	public bool OwnerTryPlaceHeld( int slotIndex, ref InventoryCursorStack held ) =>
		IsActive && Container.TryPlaceHeld( slotIndex, ref held );

	public bool OwnerTryReturnStack( ref InventoryCursorStack held ) =>
		IsActive && Container.TryAbsorbStack( ref held );

	public bool OwnerTryTakeOne( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		return IsActive && Container.TryTakeOne( slotIndex, out taken );
	}

	public bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held, out int placedCount )
	{
		placedCount = 0;
		return IsActive && Container.TryDropOne( slotIndex, held, out placedCount );
	}

	public bool OwnerTryTakeHalf( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		return IsActive && Container.TryTakeHalf( slotIndex, out taken );
	}

	public bool OwnerTryPlaceHalf( int slotIndex, ref InventoryCursorStack held ) =>
		IsActive && Container.TryPlaceHalf( slotIndex, ref held );

	/// <summary>Cross-grid destination lookup — <paramref name="fromSlotIndex"/> belongs to the source grid and is ignored.</summary>
	public bool TryFindQuickMoveTarget( in InventorySlot stack, int fromSlotIndex, out int targetSlotIndex )
	{
		targetSlotIndex = -1;
		return IsActive && Container.TryFindQuickMoveTarget( stack, out targetSlotIndex );
	}
}
