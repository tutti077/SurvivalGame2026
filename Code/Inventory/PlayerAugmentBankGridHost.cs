using System;

namespace Survival;

/// <summary>Grid host for the player augment bank (crafted storage).</summary>
public sealed class PlayerAugmentBankGridHost : IInventoryGridHost
{
	public string GridId => "augment_bank";
	public PlayerInventory Inventory { get; }
	public PlayerHotbar Hotbar => null;

	readonly PlayerAugments _augments;

	public PlayerAugmentBankGridHost( PlayerAugments augments, PlayerInventory inventory )
	{
		_augments = augments;
		Inventory = inventory;
	}

	public int SlotCount => PlayerAugments.BankSlotCount;

	public InventorySlot GetSlot( int index )
	{
		if ( _augments is null || index < 0 || index >= SlotCount )
			return InventorySlot.Empty;

		return _augments.GetBankSlot( index );
	}

	public bool OwnerTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( _augments is null )
			return false;

		return _augments.OwnerTryPickupBank( slotIndex, out picked );
	}

	public bool OwnerTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( _augments is null )
			return false;

		return _augments.OwnerTryFinishBankDrag( sourceSlotIndex, targetSlotIndex, ref held );
	}

	public bool OwnerTryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( _augments is null )
			return false;

		return _augments.OwnerTryPlaceHeldBank( slotIndex, ref held );
	}

	public bool OwnerTryReturnStack( ref InventoryCursorStack held ) => false;

	public bool OwnerTryTakeOne( int slotIndex, out InventorySlot taken ) =>
		OwnerTryPickupAll( slotIndex, out taken );

	public bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held, out int placedCount )
	{
		placedCount = 0;
		if ( _augments is null || held.IsEmpty )
			return false;

		var copy = held;
		if ( !_augments.OwnerTryPlaceHeldBank( slotIndex, ref copy ) )
			return false;

		placedCount = held.Count - copy.Count;
		return placedCount > 0;
	}

	public bool OwnerTryTakeHalf( int slotIndex, out InventorySlot taken ) =>
		OwnerTryTakeOne( slotIndex, out taken );

	public bool OwnerTryPlaceHalf( int slotIndex, ref InventoryCursorStack held ) =>
		OwnerTryPlaceHeld( slotIndex, ref held );

	public bool TryFindQuickMoveTarget( in InventorySlot stack, int fromSlotIndex, out int targetSlotIndex )
	{
		targetSlotIndex = -1;
		if ( _augments is null || stack.IsEmpty || !AugmentCatalog.IsAugment( stack.ResourceId ) )
			return false;

		if ( !_augments.TryFindEmptyBankSlot( out var empty ) )
			return false;

		targetSlotIndex = empty;
		return targetSlotIndex != fromSlotIndex;
	}
}
