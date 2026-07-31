using System;
using Sandbox;

namespace Survival;

/// <summary>
/// World-object item storage (chest, etc.) — a host-authoritative slot grid. All stack rules
/// come from <see cref="InventoryStackRules"/>; this component only adds the authority gate
/// and change notifications. Authored on the container prefab; the menu UI talks to it through
/// <see cref="ContainerInventoryGridHost"/>. Contents live for the session on the host
/// (build pieces are host-side objects today; client replication rides on build networking later).
/// </summary>
[Title( "Container Inventory" )]
public sealed class ContainerInventory : Component
{
	[Property] public int SlotCount { get; set; } = InventoryDefaults.DefaultSlotCount;

	[Property] public int Columns { get; set; } = InventoryDefaults.DefaultColumns;

	[Property] public string DisplayName { get; set; } = "Chest";

	public event Action ContentsChanged;

	InventorySlot[] _slots = Array.Empty<InventorySlot>();

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnStart()
	{
		base.OnStart();
		EnsureSlotArray();
	}

	public InventorySlot GetSlot( int index )
	{
		EnsureSlotArray();
		if ( index < 0 || index >= _slots.Length )
			return InventorySlot.Empty;
		return _slots[index];
	}

	public bool TryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		return Apply( InventoryStackRules.PickupAll( _slots, slotIndex, out picked ) );
	}

	public bool TryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		return Apply( InventoryStackRules.PlaceHeld( _slots, slotIndex, ref held ) );
	}

	/// <summary>Completes a left-drag onto a slot in this container (swap when occupied by a different item).</summary>
	public bool TryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		return Apply( InventoryStackRules.FinishDragDrop( _slots, sourceSlotIndex, targetSlotIndex, ref held ) );
	}

	/// <summary>Drag-drop swap: held stack goes to target, displaced stack returns to the (emptied) source slot.</summary>
	public bool TrySwapDragToSlot( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		return Apply( InventoryStackRules.SwapDragToSlot( _slots, sourceSlotIndex, targetSlotIndex, ref held ) );
	}

	public bool TryTakeOne( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		return Apply( InventoryStackRules.TakeOne( _slots, slotIndex, out taken ) );
	}

	public bool TryDropOne( int slotIndex, in InventoryCursorStack held, out int placedCount )
	{
		placedCount = 0;
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		return Apply( InventoryStackRules.DropOne( _slots, slotIndex, held, out placedCount ) );
	}

	public bool TryTakeHalf( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		return Apply( InventoryStackRules.TakeHalf( _slots, slotIndex, out taken ) );
	}

	public bool TryPlaceHalf( int slotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		return Apply( InventoryStackRules.PlaceHalf( _slots, slotIndex, ref held ) );
	}

	/// <summary>Merges a cursor stack into matching stacks, then empties. Returns true when fully absorbed.</summary>
	public bool TryAbsorbStack( ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		Apply( InventoryStackRules.AbsorbStack( _slots, ref held ) );
		return held.IsEmpty;
	}

	/// <summary>Quick-move destination: matching stack with room first, then first empty slot.</summary>
	public bool TryFindQuickMoveTarget( in InventorySlot stack, out int targetSlotIndex )
	{
		EnsureSlotArray();
		return InventoryStackRules.TryFindQuickMoveTarget( _slots, stack, -1, out targetSlotIndex );
	}

	/// <summary>Finds an openable container on the hit object or its parents (skips build previews/blueprints).</summary>
	public static bool TryFindOnHierarchy( GameObject hitObject, out ContainerInventory container )
	{
		container = null;
		if ( hitObject is null || !hitObject.IsValid() )
			return false;

		for ( var current = hitObject; current.IsValid(); current = current.Parent )
		{
			var candidate = current.Components.Get<ContainerInventory>();
			if ( candidate is null || !candidate.Enabled )
				continue;

			if ( candidate.GameObject.Tags.Has( "buildpreview" ) )
				continue;

			var piece = candidate.Components.Get<BuildPiece>();
			if ( piece is not null && (piece.IsPreviewGhost || piece.IsBlueprint) )
				continue;

			container = candidate;
			return true;
		}

		return false;
	}

	bool Apply( bool changed )
	{
		if ( changed )
			ContentsChanged?.Invoke();

		return changed;
	}

	void EnsureSlotArray()
	{
		var count = Math.Max( 1, SlotCount );
		if ( _slots.Length == count )
			return;

		var next = new InventorySlot[count];
		if ( _slots.Length > 0 )
			Array.Copy( _slots, next, Math.Min( _slots.Length, next.Length ) );
		_slots = next;
		SlotCount = count;
	}
}
