using System;

namespace Survival;

/// <summary>
/// Grid host for the 18 body sockets. The station uses it interactive; the Augments menu page uses a
/// <b>read-only</b> instance so its sockets hover for tooltips but never pick up, drop or swap.
/// </summary>
public sealed class PlayerAugmentInstalledGridHost : IInventoryGridHost
{
	public string GridId { get; }
	public PlayerInventory Inventory { get; }
	public PlayerHotbar Hotbar => null;

	readonly PlayerAugments _augments;
	readonly bool _readOnly;

	public PlayerAugmentInstalledGridHost( PlayerAugments augments, PlayerInventory inventory, bool readOnly )
	{
		_augments = augments;
		_readOnly = readOnly;
		Inventory = inventory;
		GridId = readOnly ? "augment_installed_view" : "augment_installed";
	}

	public int SlotCount => AugmentSlots.Count;

	public InventorySlot GetSlot( int index )
	{
		if ( _augments is null || index < 0 || index >= SlotCount )
			return InventorySlot.Empty;

		return _augments.GetInstalled( (AugmentSlot)index );
	}

	public bool OwnerTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( _readOnly || _augments is null || slotIndex < 0 || slotIndex >= SlotCount )
			return false;

		return _augments.OwnerTryPickupInstalled( (AugmentSlot)slotIndex, out picked );
	}

	public bool OwnerTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( _readOnly || _augments is null )
			return false;

		return _augments.OwnerTryFinishInstalledDrag(
			(AugmentSlot)sourceSlotIndex,
			(AugmentSlot)targetSlotIndex,
			ref held );
	}

	public bool OwnerTryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( _readOnly || _augments is null || slotIndex < 0 || slotIndex >= SlotCount )
			return false;

		return _augments.OwnerTryPlaceIntoInstalled( (AugmentSlot)slotIndex, ref held );
	}

	public bool OwnerTryReturnStack( ref InventoryCursorStack held ) => false;

	public bool OwnerTryTakeOne( int slotIndex, out InventorySlot taken ) =>
		OwnerTryPickupAll( slotIndex, out taken );

	public bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held, out int placedCount )
	{
		placedCount = 0;
		if ( _readOnly || _augments is null || held.IsEmpty )
			return false;

		var copy = held;
		if ( !_augments.OwnerTryPlaceIntoInstalled( (AugmentSlot)slotIndex, ref copy ) )
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
		if ( _readOnly || _augments is null || stack.IsEmpty )
			return false;

		if ( !_augments.TryFindInstallSlot( stack.ResourceId, out var slot ) )
			return false;

		targetSlotIndex = (int)slot;
		return targetSlotIndex != fromSlotIndex;
	}
}
