namespace Survival;

/// <summary>
/// The six key-bind slots (1–6) on the Augments page. Slots hold augment <i>ids</i>, not items:
/// nothing is ever picked up or placed through the item paths — assignment happens through the
/// interaction's binding-only drag (<see cref="PlayerAugments.OwnerTrySetBind"/>).
/// </summary>
public sealed class PlayerAugmentBindGridHost : IInventoryGridHost
{
	public const string GridIdValue = "augment_binds";

	public string GridId => GridIdValue;
	public PlayerInventory Inventory { get; }
	public PlayerHotbar Hotbar => null;

	readonly PlayerAugments _augments;

	public PlayerAugmentBindGridHost( PlayerAugments augments, PlayerInventory inventory )
	{
		_augments = augments;
		Inventory = inventory;
	}

	public int SlotCount => PlayerAugments.BindCount;

	public InventorySlot GetSlot( int index )
	{
		var id = _augments?.GetBind( index );
		return string.IsNullOrWhiteSpace( id )
			? InventorySlot.Empty
			: new InventorySlot { ResourceId = id, Count = 1 };
	}

	public bool OwnerTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		return false;
	}

	public bool OwnerTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held ) => false;

	public bool OwnerTryPlaceHeld( int slotIndex, ref InventoryCursorStack held ) => false;

	public bool OwnerTryReturnStack( ref InventoryCursorStack held ) => false;

	public bool OwnerTryTakeOne( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		return false;
	}

	public bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held, out int placedCount )
	{
		placedCount = 0;
		return false;
	}

	public bool OwnerTryTakeHalf( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		return false;
	}

	public bool OwnerTryPlaceHalf( int slotIndex, ref InventoryCursorStack held ) => false;

	public bool TryFindQuickMoveTarget( in InventorySlot stack, int fromSlotIndex, out int targetSlotIndex )
	{
		targetSlotIndex = -1;
		return false;
	}
}
