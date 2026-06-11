using System;

namespace Survival;

/// <summary><see cref="IInventoryGridHost"/> for the pawn paperdoll on <see cref="PlayerEquipment"/>.</summary>
public sealed class PlayerEquipmentPaperdollGridHost : IInventoryGridHost
{
	public string GridId => "paperdoll";
	public PlayerInventory Inventory { get; }
	public PlayerHotbar Hotbar => null;

	readonly PlayerEquipment _equipment;

	public PlayerEquipmentPaperdollGridHost( PlayerEquipment equipment, PlayerInventory inventory )
	{
		_equipment = equipment;
		Inventory = inventory;
	}

	public int SlotCount => PlayerEquipment.SlotCount;

	public InventorySlot GetSlot( int index )
	{
		if ( _equipment is null || index < 0 || index >= SlotCount )
			return InventorySlot.Empty;

		return _equipment.GetSlot( (EquipmentSlot)index );
	}

	public bool OwnerTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( _equipment is null || slotIndex < 0 || slotIndex >= SlotCount )
			return false;

		return _equipment.OwnerTryPickupFromSlot( (EquipmentSlot)slotIndex, out picked );
	}

	public bool OwnerTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( _equipment is null )
			return false;

		if ( sourceSlotIndex < 0 || sourceSlotIndex >= SlotCount || targetSlotIndex < 0 || targetSlotIndex >= SlotCount )
			return false;

		return _equipment.OwnerTryFinishDragDrop(
			(EquipmentSlot)sourceSlotIndex,
			(EquipmentSlot)targetSlotIndex,
			ref held );
	}

	public bool OwnerTryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( _equipment is null || slotIndex < 0 || slotIndex >= SlotCount )
			return false;

		return _equipment.OwnerTryPlaceIntoSlot( (EquipmentSlot)slotIndex, ref held );
	}

	public bool OwnerTryReturnStack( ref InventoryCursorStack held ) => false;

	public bool OwnerTryTakeOne( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		if ( _equipment is null || slotIndex < 0 || slotIndex >= SlotCount )
			return false;

		var slot = _equipment.GetSlot( (EquipmentSlot)slotIndex );
		if ( slot.IsEmpty )
			return false;

		taken = new InventorySlot { ResourceId = slot.ResourceId, Count = 1 };
		if ( slot.Count <= 1 )
			_equipment.OwnerTryPickupFromSlot( (EquipmentSlot)slotIndex, out _ );
		return true;
	}

	public bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held, out int placedCount )
	{
		placedCount = 0;
		if ( _equipment is null || held.IsEmpty || slotIndex < 0 || slotIndex >= SlotCount )
			return false;

		var heldCopy = held;
		if ( !_equipment.OwnerTryPlaceIntoSlot( (EquipmentSlot)slotIndex, ref heldCopy ) )
			return false;

		placedCount = held.Count - heldCopy.Count;
		return placedCount > 0;
	}

	public bool OwnerTryTakeHalf( int slotIndex, out InventorySlot taken ) =>
		OwnerTryTakeOne( slotIndex, out taken );

	public bool OwnerTryPlaceHalf( int slotIndex, ref InventoryCursorStack held ) =>
		OwnerTryPlaceHeld( slotIndex, ref held );

	public bool TryFindQuickMoveTarget( in InventorySlot stack, int fromSlotIndex, out int targetSlotIndex )
	{
		targetSlotIndex = -1;
		if ( _equipment is null || stack.IsEmpty )
			return false;

		var fromSlot = fromSlotIndex >= 0 && fromSlotIndex < SlotCount
			? (EquipmentSlot)fromSlotIndex
			: EquipmentSlot.MainHand;

		if ( !_equipment.TryFindQuickEquipSlot( stack.ResourceId, fromSlot, out var target ) )
			return false;

		targetSlotIndex = (int)target;
		return true;
	}
}
