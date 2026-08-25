using System;

namespace Survival;

/// <summary>
/// Master stack rules for every slot-grid inventory (player bag, hotbar, world containers).
/// Pure slot-array operations — no authority checks, networking, or notifications. Components
/// wrap these with their own authority gates, RPCs, change events, and side effects (e.g.
/// hotbar binding ghosts). Add new storage types by wrapping these rules, not by copying them.
/// </summary>
public static class InventoryStackRules
{
	public static bool PickupAll( InventorySlot[] slots, int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( !IsValidIndex( slots, slotIndex ) || slots[slotIndex].IsEmpty )
			return false;

		picked = slots[slotIndex];
		slots[slotIndex] = InventorySlot.Empty;
		return true;
	}

	/// <summary>
	/// Place-all: fills an empty slot, merges same resource (swapping when the stack is full),
	/// swaps a different resource. <paramref name="held"/> keeps the remainder / displaced stack.
	/// </summary>
	public static bool PlaceHeld( InventorySlot[] slots, int slotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsValidIndex( slots, slotIndex ) )
			return false;

		ref var dest = ref slots[slotIndex];

		if ( dest.IsEmpty )
		{
			var place = ResourceCatalog.ClampAddToStack( held.ResourceId, 0, held.Count );
			if ( place <= 0 )
				return false;

			dest = new InventorySlot { ResourceId = held.ResourceId, Count = place, Wear = held.Wear, CrafterName = held.CrafterName };
			ReduceHeld( ref held, place );
			return true;
		}

		if ( string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
		{
			var room = ResourceCatalog.GetMaxStack( held.ResourceId ) - dest.Count;
			if ( room <= 0 )
			{
				var displaced = dest;
				dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count, Wear = held.Wear, CrafterName = held.CrafterName };
				held.Set( displaced.ResourceId, displaced.Count, displaced.Wear, displaced.CrafterName );
				return true;
			}

			var add = Math.Min( held.Count, room );
			dest.Count += add;
			ReduceHeld( ref held, add );
			return true;
		}

		var swap = dest;
		dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count, Wear = held.Wear, CrafterName = held.CrafterName };
		held.Set( swap.ResourceId, swap.Count, swap.Wear, swap.CrafterName );
		return true;
	}

	/// <summary>Drag finish onto the (emptied) source slot: place into empty or merge same resource — never swap.</summary>
	public static bool MergeHeldIntoSlot( InventorySlot[] slots, int slotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsValidIndex( slots, slotIndex ) )
			return false;

		ref var slot = ref slots[slotIndex];
		if ( slot.IsEmpty )
		{
			slot = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count, Wear = held.Wear, CrafterName = held.CrafterName };
			held.Clear();
			return true;
		}

		if ( !ResourceCatalog.ResourceIdsMatch( slot.ResourceId, held.ResourceId ) )
			return false;

		var add = ResourceCatalog.ClampAddToStack( held.ResourceId, slot.Count, held.Count );
		if ( add <= 0 )
			return false;

		slot.Count += add;
		ReduceHeld( ref held, add );
		return true;
	}

	/// <summary>Drag-drop swap: held goes to target, displaced stack returns to the (emptied) source slot.</summary>
	public static bool SwapDragToSlot( InventorySlot[] slots, int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || sourceSlotIndex == targetSlotIndex )
			return false;

		if ( !IsValidIndex( slots, sourceSlotIndex ) || !IsValidIndex( slots, targetSlotIndex ) )
			return false;

		ref var target = ref slots[targetSlotIndex];
		if ( target.IsEmpty || string.Equals( target.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		var displaced = target;
		target = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count, Wear = held.Wear, CrafterName = held.CrafterName };
		slots[sourceSlotIndex] = displaced;
		held.Clear();
		return true;
	}

	/// <summary>Completes a left-drag: same-slot merge, swap when occupied by a different item, else place.</summary>
	public static bool FinishDragDrop( InventorySlot[] slots, int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsValidIndex( slots, targetSlotIndex ) )
			return false;

		if ( sourceSlotIndex == targetSlotIndex )
			return MergeHeldIntoSlot( slots, targetSlotIndex, ref held );

		ref var target = ref slots[targetSlotIndex];
		if ( !target.IsEmpty && !string.Equals( target.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return SwapDragToSlot( slots, sourceSlotIndex, targetSlotIndex, ref held );

		return PlaceHeld( slots, targetSlotIndex, ref held );
	}

	public static bool TakeOne( InventorySlot[] slots, int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		if ( !IsValidIndex( slots, slotIndex ) || slots[slotIndex].IsEmpty )
			return false;

		var resourceId = slots[slotIndex].ResourceId;
		var wear = slots[slotIndex].Wear;
		var crafter = slots[slotIndex].CrafterName;
		slots[slotIndex].Count--;
		if ( slots[slotIndex].Count <= 0 )
			slots[slotIndex] = InventorySlot.Empty;

		taken = new InventorySlot { ResourceId = resourceId, Count = 1, Wear = wear, CrafterName = crafter };
		return true;
	}

	/// <summary>Right-click drop: one unit of the held stack into an empty or matching slot with room.</summary>
	public static bool DropOne( InventorySlot[] slots, int slotIndex, in InventoryCursorStack held, out int placedCount )
	{
		placedCount = 0;
		if ( held.IsEmpty || !IsValidIndex( slots, slotIndex ) )
			return false;

		ref var dest = ref slots[slotIndex];

		if ( dest.IsEmpty )
		{
			dest = new InventorySlot { ResourceId = held.ResourceId, Count = 1, Wear = held.Wear, CrafterName = held.CrafterName };
			placedCount = 1;
			return true;
		}

		if ( !string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		if ( dest.Count >= ResourceCatalog.GetMaxStack( held.ResourceId ) )
			return false;

		dest.Count++;
		placedCount = 1;
		return true;
	}

	public static bool TakeHalf( InventorySlot[] slots, int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		if ( !IsValidIndex( slots, slotIndex ) || slots[slotIndex].IsEmpty )
			return false;

		var half = slots[slotIndex].Count / 2;
		if ( half <= 0 )
			return false;

		var resourceId = slots[slotIndex].ResourceId;
		var wear = slots[slotIndex].Wear;
		var crafter = slots[slotIndex].CrafterName;
		slots[slotIndex].Count -= half;
		if ( slots[slotIndex].Count <= 0 )
			slots[slotIndex] = InventorySlot.Empty;

		taken = new InventorySlot { ResourceId = resourceId, Count = half, Wear = wear, CrafterName = crafter };
		return true;
	}

	public static bool PlaceHalf( InventorySlot[] slots, int slotIndex, ref InventoryCursorStack held )
	{
		var half = held.IsEmpty ? 0 : held.Count / 2;
		if ( half <= 0 || !IsValidIndex( slots, slotIndex ) )
			return false;

		ref var dest = ref slots[slotIndex];

		if ( dest.IsEmpty )
		{
			var place = ResourceCatalog.ClampAddToStack( held.ResourceId, 0, half );
			if ( place <= 0 )
				return false;

			dest = new InventorySlot { ResourceId = held.ResourceId, Count = place, Wear = held.Wear, CrafterName = held.CrafterName };
			ReduceHeld( ref held, place );
			return true;
		}

		if ( !string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		var add = ResourceCatalog.ClampAddToStack( held.ResourceId, dest.Count, half );
		if ( add <= 0 )
			return false;

		dest.Count += add;
		ReduceHeld( ref held, add );
		return true;
	}

	/// <summary>
	/// Merges a cursor stack into matching stacks, then empty slots. Returns true when anything
	/// changed; <paramref name="held"/> keeps the remainder.
	/// </summary>
	public static bool AbsorbStack( InventorySlot[] slots, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || slots is null )
			return false;

		var resourceId = ResourceCatalog.NormalizeResourceId( held.ResourceId );
		var changed = false;

		for ( var i = 0; i < slots.Length && held.Count > 0; i++ )
		{
			ref var slot = ref slots[i];
			if ( slot.IsEmpty || !ResourceCatalog.ResourceIdsMatch( slot.ResourceId, resourceId ) )
				continue;

			var add = ResourceCatalog.ClampAddToStack( resourceId, slot.Count, held.Count );
			if ( add <= 0 )
				continue;

			slot.Count += add;
			held.Count -= add;
			changed = true;
		}

		for ( var i = 0; i < slots.Length && held.Count > 0; i++ )
		{
			ref var slot = ref slots[i];
			if ( !slot.IsEmpty )
				continue;

			var add = ResourceCatalog.ClampAddToStack( resourceId, 0, held.Count );
			if ( add <= 0 )
				break;

			slot = new InventorySlot { ResourceId = resourceId, Count = add, Wear = held.Wear, CrafterName = held.CrafterName };
			held.Count -= add;
			changed = true;
		}

		if ( held.Count <= 0 )
			held.Clear();

		return changed;
	}

	/// <summary>
	/// Quick-move destination: matching stack with room first (full stacks would swap, not
	/// merge), then first empty slot. <paramref name="excludeSlotIndex"/> is for same-grid
	/// moves; pass -1 for cross-grid.
	/// </summary>
	public static bool TryFindQuickMoveTarget(
		ReadOnlySpan<InventorySlot> slots,
		in InventorySlot stack,
		int excludeSlotIndex,
		out int targetSlotIndex )
	{
		targetSlotIndex = -1;
		if ( stack.IsEmpty )
			return false;

		var maxStack = ResourceCatalog.GetMaxStack( stack.ResourceId );

		for ( var i = 0; i < slots.Length; i++ )
		{
			if ( i == excludeSlotIndex )
				continue;

			if ( slots[i].IsEmpty || !string.Equals( slots[i].ResourceId, stack.ResourceId, StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( slots[i].Count >= maxStack )
				continue;

			targetSlotIndex = i;
			return true;
		}

		for ( var i = 0; i < slots.Length; i++ )
		{
			if ( i == excludeSlotIndex || !slots[i].IsEmpty )
				continue;

			targetSlotIndex = i;
			return true;
		}

		return false;
	}

	/// <summary>Total count of a resource across all stacks (id is normalized here).</summary>
	public static int CountResource( ReadOnlySpan<InventorySlot> slots, string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return 0;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var total = 0;
		for ( var i = 0; i < slots.Length; i++ )
		{
			if ( slots[i].IsEmpty || !ResourceCatalog.ResourceIdsMatch( slots[i].ResourceId, resourceId ) )
				continue;

			total += slots[i].Count;
		}

		return total;
	}

	static void ReduceHeld( ref InventoryCursorStack held, int amount )
	{
		held.Count -= amount;
		if ( held.Count <= 0 )
			held.Clear();
	}

	static bool IsValidIndex( InventorySlot[] slots, int index ) =>
		slots is not null && index >= 0 && index < slots.Length;
}
