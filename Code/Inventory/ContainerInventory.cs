using System;
using Sandbox;

namespace Survival;

/// <summary>
/// World-object item storage (chest, death loot, etc.) — host-authoritative slot grid.
/// Stack rules come from <see cref="InventoryStackRules"/>. After NetworkSpawn, contents are
/// Broadcast to all peers (shared view). Clients predict locally then host confirms; concurrent
/// takes serialize on the host (first Rpc wins, losers get a full contents resync).
/// </summary>
[Title( "Container Inventory" )]
public sealed class ContainerInventory : Component
{
	[Property] public int SlotCount { get; set; } = InventoryDefaults.DefaultSlotCount;

	[Property] public int Columns { get; set; } = InventoryDefaults.DefaultColumns;

	[Property] public string DisplayName { get; set; } = "Chest";

	/// <summary>Players can only remove items (death loot bags). Host fill still works via <see cref="HostDepositStack"/>.</summary>
	[Property, Title( "Take Only (no deposits)" )]
	public bool TakeOnly { get; set; }

	/// <summary>Destroy the owning object once the last item is removed (death loot bags).</summary>
	[Property, Title( "Destroy When Emptied" )]
	public bool DestroyWhenEmpty { get; set; }

	/// <summary>Bumps when host contents change — remotes can detect a missed Broadcast.</summary>
	/// <summary>
	/// One slot that only accepts <see cref="RestrictedSlotResourceId"/> (a fuel tank, a furnace input).
	/// -1 = no restriction. Prefab-authored, so every peer knows it without a broadcast. Deposits into
	/// that slot are refused for anything else, and stack absorbs / quick-moves skip it unless the item
	/// matches. First user: the vehicle fuel slot (<see cref="Vehicle"/>).
	/// </summary>
	[Property, Group( "Restricted Slot" ), Title( "Restricted Slot Index" )]
	public int RestrictedSlotIndex { get; set; } = -1;

	[Property, Group( "Restricted Slot" ), Title( "Restricted Slot Resource Id" )]
	public string RestrictedSlotResourceId { get; set; } = string.Empty;

	/// <summary>Short label painted on the empty restricted slot ("FUEL").</summary>
	[Property, Group( "Restricted Slot" ), Title( "Restricted Slot Label" )]
	public string RestrictedSlotLabel { get; set; } = string.Empty;

	[Sync( SyncFlags.FromHost )]
	public int ContentsVersion { get; set; }

	public event Action ContentsChanged;

	InventorySlot[] _slots = Array.Empty<InventorySlot>();
	int _lastSeenContentsVersion = -1;

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnStart()
	{
		base.OnStart();
		EnsureSlotArray();
		_lastSeenContentsVersion = ContentsVersion;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			RpcHostRequestContentsSync();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( Networking.IsHost || GameObject.Network is not { Active: true } )
			return;

		if ( ContentsVersion == _lastSeenContentsVersion )
			return;

		_lastSeenContentsVersion = ContentsVersion;
		RpcHostRequestContentsSync();
	}

	public InventorySlot GetSlot( int index )
	{
		EnsureSlotArray();
		if ( index < 0 || index >= _slots.Length )
			return InventorySlot.Empty;
		return _slots[index];
	}

	public bool HasRestrictedSlot => RestrictedSlotIndex >= 0 && !string.IsNullOrWhiteSpace( RestrictedSlotResourceId );

	public bool IsSlotRestricted( int slotIndex ) => HasRestrictedSlot && slotIndex == RestrictedSlotIndex;

	/// <summary>May this resource sit in this slot? Everything is fine outside the restricted slot.</summary>
	public bool SlotAccepts( int slotIndex, string resourceId )
	{
		if ( !IsSlotRestricted( slotIndex ) )
			return true;
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return true;
		return ResourceCatalog.ResourceIdsMatch( resourceId, RestrictedSlotResourceId );
	}

	/// <summary>Drag / drop between <paramref name="sourceSlotIndex"/> and <paramref name="targetSlotIndex"/> with <paramref name="held"/> landing on the target: both ends must accept what they receive.</summary>
	bool DragAccepts( int sourceSlotIndex, int targetSlotIndex, in InventoryCursorStack held )
	{
		if ( !HasRestrictedSlot )
			return true;

		EnsureSlotArray();
		if ( !SlotAccepts( targetSlotIndex, held.ResourceId ) )
			return false;

		// Whatever sits on the target moves back to the source.
		if ( targetSlotIndex >= 0 && targetSlotIndex < _slots.Length && !_slots[targetSlotIndex].IsEmpty
		     && !SlotAccepts( sourceSlotIndex, _slots[targetSlotIndex].ResourceId ) )
			return false;

		return true;
	}

	/// <summary>Absorb into every slot the stack may use (the restricted slot only when it matches).</summary>
	bool AbsorbRespectingRestriction( ref InventoryCursorStack held )
	{
		EnsureSlotArray();
		if ( !HasRestrictedSlot || RestrictedSlotIndex >= _slots.Length )
			return InventoryStackRules.AbsorbStack( _slots, ref held );

		if ( SlotAccepts( RestrictedSlotIndex, held.ResourceId ) )
		{
			// The matching item fills its own slot first (fuel goes in the tank), then spills over.
			var single = new[] { _slots[RestrictedSlotIndex] };
			var changedFirst = InventoryStackRules.AbsorbStack( single, ref held );
			if ( changedFirst )
				_slots[RestrictedSlotIndex] = single[0];
			if ( held.IsEmpty )
				return changedFirst;
			return InventoryStackRules.AbsorbStack( _slots, ref held ) || changedFirst;
		}

		var view = new InventorySlot[_slots.Length - 1];
		for ( int i = 0, j = 0; i < _slots.Length; i++ )
		{
			if ( i == RestrictedSlotIndex )
				continue;
			view[j++] = _slots[i];
		}

		var changed = InventoryStackRules.AbsorbStack( view, ref held );
		if ( changed )
		{
			for ( int i = 0, j = 0; i < _slots.Length; i++ )
			{
				if ( i == RestrictedSlotIndex )
					continue;
				_slots[i] = view[j++];
			}
		}

		return changed;
	}

	/// <summary>Host: put up to <paramref name="count"/> of a resource straight into one slot (dev fuel fill). Returns the amount placed.</summary>
	public int HostTryDepositIntoSlot( int slotIndex, string resourceId, int count )
	{
		if ( !HasHostAuthority || count <= 0 || string.IsNullOrWhiteSpace( resourceId ) )
			return 0;

		EnsureSlotArray();
		if ( slotIndex < 0 || slotIndex >= _slots.Length )
			return 0;

		var id = ResourceCatalog.NormalizeResourceId( resourceId );
		if ( !SlotAccepts( slotIndex, id ) )
			return 0;

		ref var slot = ref _slots[slotIndex];
		if ( !slot.IsEmpty && !ResourceCatalog.ResourceIdsMatch( slot.ResourceId, id ) )
			return 0;

		var add = ResourceCatalog.ClampAddToStack( id, slot.IsEmpty ? 0 : slot.Count, count );
		if ( add <= 0 )
			return 0;

		if ( slot.IsEmpty )
			slot = new InventorySlot { ResourceId = id, Count = add };
		else
			slot.Count += add;

		Apply( true );
		return add;
	}

	/// <summary>Host: take up to <paramref name="count"/> out of one slot (fuel burn, furnace input). Returns the amount removed.</summary>
	public int HostTryRemoveFromSlot( int slotIndex, int count )
	{
		if ( !HasHostAuthority || count <= 0 )
			return 0;

		EnsureSlotArray();
		if ( slotIndex < 0 || slotIndex >= _slots.Length || _slots[slotIndex].IsEmpty )
			return 0;

		ref var slot = ref _slots[slotIndex];
		var take = Math.Min( count, slot.Count );
		slot.Count -= take;
		if ( slot.Count <= 0 )
			slot = InventorySlot.Empty;

		Apply( true );
		return take;
	}

	/// <summary>Owner/client entry: predict locally, host confirms and Broadcasts shared truth.</summary>
	public bool OwnerTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		EnsureSlotArray();
		if ( HasHostAuthority )
			return TryPickupAll( slotIndex, out picked );

		var ok = InventoryStackRules.PickupAll( _slots, slotIndex, out picked );
		if ( ok )
			ContentsChanged?.Invoke();
		RpcHostPickupAll( slotIndex );
		return ok;
	}

	public bool OwnerTryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		EnsureSlotArray();
		if ( HasHostAuthority )
			return TryPlaceHeld( slotIndex, ref held );

		if ( TakeOnly || !SlotAccepts( slotIndex, held.ResourceId ) )
			return false;

		var snapshot = held;
		var ok = InventoryStackRules.PlaceHeld( _slots, slotIndex, ref held );
		if ( ok )
			ContentsChanged?.Invoke();
		RpcHostPlaceHeld( slotIndex, snapshot.ResourceId ?? string.Empty, snapshot.Count, snapshot.Wear, snapshot.CrafterName ?? string.Empty );
		return ok;
	}

	public bool OwnerTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		EnsureSlotArray();
		if ( HasHostAuthority )
			return TryFinishDragDrop( sourceSlotIndex, targetSlotIndex, ref held );

		if ( TakeOnly )
			return false;

		var snapshot = held;
		if ( !DragAccepts( sourceSlotIndex, targetSlotIndex, held ) )
			return false;

		var ok = InventoryStackRules.FinishDragDrop( _slots, sourceSlotIndex, targetSlotIndex, ref held );
		if ( ok )
			ContentsChanged?.Invoke();
		RpcHostFinishDragDrop( sourceSlotIndex, targetSlotIndex, snapshot.ResourceId ?? string.Empty, snapshot.Count, snapshot.Wear, snapshot.CrafterName ?? string.Empty );
		return ok;
	}

	public bool OwnerTrySwapDragToSlot( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		EnsureSlotArray();
		if ( HasHostAuthority )
			return TrySwapDragToSlot( sourceSlotIndex, targetSlotIndex, ref held );

		if ( TakeOnly )
			return false;

		var snapshot = held;
		if ( !DragAccepts( sourceSlotIndex, targetSlotIndex, held ) )
			return false;

		var ok = InventoryStackRules.SwapDragToSlot( _slots, sourceSlotIndex, targetSlotIndex, ref held );
		if ( ok )
			ContentsChanged?.Invoke();
		RpcHostSwapDragToSlot( sourceSlotIndex, targetSlotIndex, snapshot.ResourceId ?? string.Empty, snapshot.Count, snapshot.Wear, snapshot.CrafterName ?? string.Empty );
		return ok;
	}

	public bool OwnerTryTakeOne( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		EnsureSlotArray();
		if ( HasHostAuthority )
			return TryTakeOne( slotIndex, out taken );

		var ok = InventoryStackRules.TakeOne( _slots, slotIndex, out taken );
		if ( ok )
			ContentsChanged?.Invoke();
		RpcHostTakeOne( slotIndex );
		return ok;
	}

	public bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held, out int placedCount )
	{
		placedCount = 0;
		EnsureSlotArray();
		if ( HasHostAuthority )
			return TryDropOne( slotIndex, held, out placedCount );

		if ( TakeOnly )
			return false;

		if ( !SlotAccepts( slotIndex, held.ResourceId ) )
			return false;

		var ok = InventoryStackRules.DropOne( _slots, slotIndex, held, out placedCount );
		if ( ok )
			ContentsChanged?.Invoke();
		RpcHostDropOne( slotIndex, held.ResourceId ?? string.Empty, held.Count, held.Wear, held.CrafterName ?? string.Empty );
		return ok;
	}

	public bool OwnerTryTakeHalf( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		EnsureSlotArray();
		if ( HasHostAuthority )
			return TryTakeHalf( slotIndex, out taken );

		var ok = InventoryStackRules.TakeHalf( _slots, slotIndex, out taken );
		if ( ok )
			ContentsChanged?.Invoke();
		RpcHostTakeHalf( slotIndex );
		return ok;
	}

	public bool OwnerTryPlaceHalf( int slotIndex, ref InventoryCursorStack held )
	{
		EnsureSlotArray();
		if ( HasHostAuthority )
			return TryPlaceHalf( slotIndex, ref held );

		if ( TakeOnly )
			return false;

		var snapshot = held;
		if ( !SlotAccepts( slotIndex, held.ResourceId ) )
			return false;

		var ok = InventoryStackRules.PlaceHalf( _slots, slotIndex, ref held );
		if ( ok )
			ContentsChanged?.Invoke();
		RpcHostPlaceHalf( slotIndex, snapshot.ResourceId ?? string.Empty, snapshot.Count, snapshot.Wear, snapshot.CrafterName ?? string.Empty );
		return ok;
	}

	public bool OwnerTryAbsorbStack( ref InventoryCursorStack held )
	{
		EnsureSlotArray();
		if ( HasHostAuthority )
			return TryAbsorbStack( ref held );

		if ( TakeOnly )
			return false;

		var snapshot = held;
		AbsorbRespectingRestriction( ref held );
		ContentsChanged?.Invoke();
		RpcHostAbsorbStack( snapshot.ResourceId ?? string.Empty, snapshot.Count, snapshot.Wear, snapshot.CrafterName ?? string.Empty );
		return held.IsEmpty;
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
		if ( !HasHostAuthority || TakeOnly || !SlotAccepts( slotIndex, held.ResourceId ) )
			return false;

		EnsureSlotArray();
		return Apply( InventoryStackRules.PlaceHeld( _slots, slotIndex, ref held ) );
	}

	public bool TryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority || TakeOnly || !DragAccepts( sourceSlotIndex, targetSlotIndex, held ) )
			return false;

		EnsureSlotArray();
		return Apply( InventoryStackRules.FinishDragDrop( _slots, sourceSlotIndex, targetSlotIndex, ref held ) );
	}

	public bool TrySwapDragToSlot( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority || TakeOnly || !DragAccepts( sourceSlotIndex, targetSlotIndex, held ) )
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
		if ( !HasHostAuthority || TakeOnly || !SlotAccepts( slotIndex, held.ResourceId ) )
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
		if ( !HasHostAuthority || TakeOnly || !SlotAccepts( slotIndex, held.ResourceId ) )
			return false;

		EnsureSlotArray();
		return Apply( InventoryStackRules.PlaceHalf( _slots, slotIndex, ref held ) );
	}

	public bool TryAbsorbStack( ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority || TakeOnly )
			return false;

		EnsureSlotArray();
		Apply( AbsorbRespectingRestriction( ref held ) );
		return held.IsEmpty;
	}

	public bool TryFindQuickMoveTarget( in InventorySlot stack, out int targetSlotIndex )
	{
		targetSlotIndex = -1;
		if ( TakeOnly )
			return false;

		EnsureSlotArray();
		if ( HasRestrictedSlot && RestrictedSlotIndex < _slots.Length && !stack.IsEmpty
		     && ResourceCatalog.ResourceIdsMatch( stack.ResourceId, RestrictedSlotResourceId ) )
		{
			// Fuel shift-clicked into the buggy lands in the tank when there is room.
			var tank = _slots[RestrictedSlotIndex];
			if ( tank.IsEmpty || ( ResourceCatalog.ResourceIdsMatch( tank.ResourceId, stack.ResourceId ) && tank.Count < ResourceCatalog.GetMaxStack( stack.ResourceId ) ) )
			{
				targetSlotIndex = RestrictedSlotIndex;
				return true;
			}
		}

		var excluded = HasRestrictedSlot && !SlotAccepts( RestrictedSlotIndex, stack.ResourceId ) ? RestrictedSlotIndex : -1;
		return InventoryStackRules.TryFindQuickMoveTarget( _slots, stack, excluded, out targetSlotIndex );
	}

	public int HostDepositStack( string resourceId, int count )
	{
		if ( !HasHostAuthority || count <= 0 || string.IsNullOrWhiteSpace( resourceId ) )
			return 0;

		EnsureSlotArray();
		var held = new InventoryCursorStack();
		held.Set( ResourceCatalog.NormalizeResourceId( resourceId ), count );
		Apply( AbsorbRespectingRestriction( ref held ) );
		return count - held.Count;
	}

	public bool IsEmpty
	{
		get
		{
			EnsureSlotArray();
			for ( var i = 0; i < _slots.Length; i++ )
			{
				if ( !_slots[i].IsEmpty )
					return false;
			}

			return true;
		}
	}

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
		if ( !changed )
			return false;

		ContentsChanged?.Invoke();
		PushContentsToPeers();

		if ( DestroyWhenEmpty && IsEmpty && GameObject.IsValid() )
			GameObject.Destroy();

		return true;
	}

	void PushContentsToPeers()
	{
		if ( !HasHostAuthority || GameObject.Network is not { Active: true } )
			return;

		EnsureSlotArray();
		var ids = new string[_slots.Length];
		var counts = new int[_slots.Length];
		var wears = new int[_slots.Length];
		var crafters = new string[_slots.Length];
		for ( var i = 0; i < _slots.Length; i++ )
		{
			ids[i] = _slots[i].ResourceId ?? string.Empty;
			counts[i] = _slots[i].Count;
			wears[i] = _slots[i].Wear;
			crafters[i] = _slots[i].CrafterName ?? string.Empty;
		}

		ContentsVersion++;
		RpcBroadcastContents( ids, counts, wears, crafters, DisplayName ?? string.Empty, TakeOnly, SlotCount, Columns );
	}

	void ApplyNetworkedContents( string[] ids, int[] counts, int[] wears, string[] crafters, string displayName, bool takeOnly, int slotCount, int columns )
	{
		if ( ids is null || counts is null )
			return;

		SlotCount = Math.Max( 1, slotCount );
		Columns = Math.Max( 1, columns );
		DisplayName = displayName ?? DisplayName;
		TakeOnly = takeOnly;
		EnsureSlotArray();

		var n = Math.Min( _slots.Length, Math.Min( ids.Length, counts.Length ) );
		for ( var i = 0; i < n; i++ )
		{
			var id = ids[i];
			var c = counts[i];
			var w = wears is not null && i < wears.Length ? wears[i] : 0;
			var maker = crafters is not null && i < crafters.Length ? crafters[i] : null;
			_slots[i] = string.IsNullOrWhiteSpace( id ) || c <= 0
				? InventorySlot.Empty
				: new InventorySlot { ResourceId = ResourceCatalog.NormalizeResourceId( id ), Count = c, Wear = w, CrafterName = maker };
		}

		for ( var i = n; i < _slots.Length; i++ )
			_slots[i] = InventorySlot.Empty;

		_lastSeenContentsVersion = ContentsVersion;
		ContentsChanged?.Invoke();
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
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	void RpcBroadcastContents( string[] ids, int[] counts, int[] wears, string[] crafters, string displayName, bool takeOnly, int slotCount, int columns )
	{
		if ( Networking.IsHost )
			return;

		ApplyNetworkedContents( ids, counts, wears, crafters, displayName, takeOnly, slotCount, columns );
	}

	[Rpc.Host]
	void RpcHostRequestContentsSync()
	{
		if ( !Networking.IsHost )
			return;

		PushContentsToPeers();
	}

	[Rpc.Host]
	void RpcHostPickupAll( int slotIndex )
	{
		if ( !Networking.IsHost )
			return;
		TryPickupAll( slotIndex, out _ );
	}

	[Rpc.Host]
	void RpcHostPlaceHeld( int slotIndex, string resourceId, int count, int wear, string crafter )
	{
		if ( !Networking.IsHost )
			return;
		var held = MakeHeld( resourceId, count, wear, crafter );
		TryPlaceHeld( slotIndex, ref held );
	}

	[Rpc.Host]
	void RpcHostFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, string resourceId, int count, int wear, string crafter )
	{
		if ( !Networking.IsHost )
			return;
		var held = MakeHeld( resourceId, count, wear, crafter );
		TryFinishDragDrop( sourceSlotIndex, targetSlotIndex, ref held );
	}

	[Rpc.Host]
	void RpcHostSwapDragToSlot( int sourceSlotIndex, int targetSlotIndex, string resourceId, int count, int wear, string crafter )
	{
		if ( !Networking.IsHost )
			return;
		var held = MakeHeld( resourceId, count, wear, crafter );
		TrySwapDragToSlot( sourceSlotIndex, targetSlotIndex, ref held );
	}

	[Rpc.Host]
	void RpcHostTakeOne( int slotIndex )
	{
		if ( !Networking.IsHost )
			return;
		TryTakeOne( slotIndex, out _ );
	}

	[Rpc.Host]
	void RpcHostDropOne( int slotIndex, string resourceId, int count, int wear, string crafter )
	{
		if ( !Networking.IsHost )
			return;
		var held = MakeHeld( resourceId, count, wear, crafter );
		TryDropOne( slotIndex, held, out _ );
	}

	[Rpc.Host]
	void RpcHostTakeHalf( int slotIndex )
	{
		if ( !Networking.IsHost )
			return;
		TryTakeHalf( slotIndex, out _ );
	}

	[Rpc.Host]
	void RpcHostPlaceHalf( int slotIndex, string resourceId, int count, int wear, string crafter )
	{
		if ( !Networking.IsHost )
			return;
		var held = MakeHeld( resourceId, count, wear, crafter );
		TryPlaceHalf( slotIndex, ref held );
	}

	[Rpc.Host]
	void RpcHostAbsorbStack( string resourceId, int count, int wear, string crafter )
	{
		if ( !Networking.IsHost )
			return;
		var held = MakeHeld( resourceId, count, wear, crafter );
		TryAbsorbStack( ref held );
	}

	static InventoryCursorStack MakeHeld( string resourceId, int count, int wear = 0, string crafter = null )
	{
		var held = new InventoryCursorStack();
		if ( !string.IsNullOrWhiteSpace( resourceId ) && count > 0 )
			held.Set( ResourceCatalog.NormalizeResourceId( resourceId ), count, wear, crafter );
		return held;
	}
}
