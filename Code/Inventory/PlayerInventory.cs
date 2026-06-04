using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Per-pawn item storage (host-authoritative). Slots fill row-major from top-left; same resource stacks when already present.
/// </summary>
[Title( "Player Inventory" )]
public sealed class PlayerInventory : Component
{
	[Property] public int SlotCount { get; set; } = InventoryDefaults.DefaultSlotCount;

	[Property] public int Columns { get; set; } = InventoryDefaults.DefaultColumns;

	[Property, Group( "Debug" )] public bool LogInventory { get; set; }

	public event Action InventoryChanged;

	InventorySlot[] _slots = Array.Empty<InventorySlot>();

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnStart()
	{
		base.OnStart();
		EnsureSlotArray();
	}

	public ReadOnlySpan<InventorySlot> Slots => _slots;

	public int Rows => Columns > 0 ? (int)Math.Ceiling( SlotCount / (float)Columns ) : SlotCount;

	public InventorySlot GetSlot( int index )
	{
		EnsureSlotArray();
		if ( index < 0 || index >= _slots.Length )
			return InventorySlot.Empty;
		return _slots[index];
	}

	/// <summary>Host/offline: add harvested resources into inventory.</summary>
	public bool HostTryAddResource( string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();

		var remaining = amount;
		while ( remaining > 0 )
		{
			var maxStack = ResourceCatalog.GetMaxStack( resourceId );

			if ( TryFindStackSlot( resourceId, out var stackIndex ) )
			{
				var room = maxStack - _slots[stackIndex].Count;
				if ( room > 0 )
				{
					var add = Math.Min( remaining, room );
					_slots[stackIndex].Count += add;
					remaining -= add;
					if ( remaining <= 0 )
					{
						NotifyInventoryChanged();
						if ( LogInventory )
							Log.Info( $"[PlayerInventory] {GameObject.Name}: stacked +{amount} {resourceId} in slot {stackIndex} → {_slots[stackIndex].Count}." );
						return true;
					}
				}
			}

			if ( !TryFindFirstEmptySlot( out var emptyIndex ) )
				break;

			var place = Math.Min( remaining, maxStack );
			_slots[emptyIndex] = new InventorySlot { ResourceId = resourceId, Count = place };
			remaining -= place;
		}

		if ( remaining < amount )
		{
			NotifyInventoryChanged();
			if ( LogInventory )
				Log.Info( $"[PlayerInventory] {GameObject.Name}: placed +{amount - remaining} {resourceId} (partial)." );
		}

		if ( remaining > 0 )
		{
			if ( LogInventory )
				Log.Warning( $"[PlayerInventory] {GameObject.Name}: inventory full — could not add {remaining} {resourceId}." );
			return remaining < amount;
		}

		return true;
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

	void NotifyInventoryChanged()
	{
		InventoryChanged?.Invoke();

		if ( GameObject.Network is not { Active: true } || !Networking.IsHost )
			return;

		if ( GameObject.Network.Owner is not { } owner )
			return;

		if ( ConnectionIdentity.SameClient( owner, Connection.Local ) )
			return;

		var ids = new string[_slots.Length];
		var counts = new int[_slots.Length];
		for ( var i = 0; i < _slots.Length; i++ )
		{
			ids[i] = _slots[i].ResourceId ?? string.Empty;
			counts[i] = _slots[i].Count;
		}

		RpcOwnerInventorySync( ids, counts );
	}

	[Rpc.Owner]
	void RpcOwnerInventorySync( string[] resourceIds, int[] counts )
	{
		if ( resourceIds is null || counts is null )
			return;

		EnsureSlotArray();
		var n = Math.Min( _slots.Length, Math.Min( resourceIds.Length, counts.Length ) );
		for ( var i = 0; i < n; i++ )
		{
			var id = resourceIds[i];
			var c = counts[i];
			_slots[i] = string.IsNullOrWhiteSpace( id ) || c <= 0
				? InventorySlot.Empty
				: new InventorySlot { ResourceId = id, Count = c };
		}

		InventoryChanged?.Invoke();
	}

	public bool IsLocalManagingClient()
	{
		if ( GameObject.Network is not { Active: true } )
			return true;

		if ( GameObject.Network.Owner is not { } owner )
			return Networking.IsHost;

		return ConnectionIdentity.SameClient( owner, Connection.Local );
	}

	public bool OwnerTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryPickupAll( slotIndex, out picked );

		if ( !TryGetSlotRef( slotIndex, out picked ) || picked.IsEmpty )
			return false;

		EnsureSlotArray();
		_slots[slotIndex] = InventorySlot.Empty;
		NotifyInventoryChanged();
		RpcHostPickupAll( slotIndex );
		return true;
	}

	public bool OwnerTryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryPlaceHeld( slotIndex, ref held );

		if ( !TryGetSlotRef( slotIndex, out _ ) )
			return false;

		RpcHostPlaceHeld( slotIndex, held.ResourceId, held.Count );
		return ClientTryApplyPlaceHeld( slotIndex, ref held );
	}

	/// <summary>Drag-drop swap: held stack goes to <paramref name="targetSlotIndex"/>, displaced stack returns to <paramref name="sourceSlotIndex"/>.</summary>
	public bool OwnerTrySwapDragToSlot( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() || sourceSlotIndex == targetSlotIndex )
			return false;

		if ( HasHostAuthority )
			return HostTrySwapDragToSlot( sourceSlotIndex, targetSlotIndex, ref held );

		if ( !TryGetSlotRef( targetSlotIndex, out var target ) || target.IsEmpty )
			return false;

		if ( string.Equals( target.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		RpcHostSwapDragToSlot( sourceSlotIndex, targetSlotIndex, held.ResourceId, held.Count );
		return ClientTryApplySwapDrag( sourceSlotIndex, targetSlotIndex, ref held );
	}

	public bool OwnerTryTakeOne( int slotIndex )
	{
		if ( !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryTakeOne( slotIndex );

		RpcHostTakeOne( slotIndex );
		return true;
	}

	public bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryDropOne( slotIndex, held );

		RpcHostDropOne( slotIndex, held.ResourceId, held.Count );
		return true;
	}

	public bool OwnerTryTakeHalf( int slotIndex )
	{
		if ( !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryTakeHalf( slotIndex );

		RpcHostTakeHalf( slotIndex );
		return true;
	}

	public bool OwnerTryPlaceHalf( int slotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryPlaceHalf( slotIndex, ref held );

		RpcHostPlaceHalf( slotIndex, held.ResourceId, held.Count );
		var half = held.Count / 2;
		if ( half > 0 )
		{
			held.Count -= half;
			if ( held.Count <= 0 )
				held.Clear();
		}

		return true;
	}

	public bool OwnerTryQuickMove( int fromSlotIndex, IInventoryGridHost targetGrid )
	{
		if ( !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryQuickMove( fromSlotIndex, targetGrid );

		RpcHostQuickMove( fromSlotIndex, targetGrid?.GridId ?? string.Empty );
		return true;
	}

	public bool HostTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( !HasHostAuthority || !TryGetSlotRef( slotIndex, out var slot ) )
			return false;

		if ( slot.IsEmpty )
			return false;

		picked = slot;
		_slots[slotIndex] = InventorySlot.Empty;
		NotifyInventoryChanged();
		return true;
	}

	public bool HostTryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !HasHostAuthority || !TryGetSlotRef( slotIndex, out _ ) )
			return false;

		EnsureSlotArray();
		ref var dest = ref _slots[slotIndex];

		if ( dest.IsEmpty )
		{
			dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
			held.Clear();
			NotifyInventoryChanged();
			return true;
		}

		if ( string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
		{
			var maxStack = ResourceCatalog.GetMaxStack( held.ResourceId );
			var room = maxStack - dest.Count;
			if ( room <= 0 )
			{
				var displaced = dest;
				dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
				held.Set( displaced.ResourceId, displaced.Count );
				NotifyInventoryChanged();
				return true;
			}

			var add = Math.Min( held.Count, room );
			dest.Count += add;
			held.Count -= add;
			if ( held.Count <= 0 )
				held.Clear();

			NotifyInventoryChanged();
			return true;
		}

		var swap = dest;
		dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
		held.Set( swap.ResourceId, swap.Count );
		NotifyInventoryChanged();
		return true;
	}

	/// <summary>Drag-drop swap between slots (source was emptied when the drag started).</summary>
	public bool HostTrySwapDragToSlot( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !HasHostAuthority || sourceSlotIndex == targetSlotIndex )
			return false;

		EnsureSlotArray();
		if ( sourceSlotIndex < 0 || sourceSlotIndex >= _slots.Length || targetSlotIndex < 0 || targetSlotIndex >= _slots.Length )
			return false;

		ref var target = ref _slots[targetSlotIndex];
		if ( target.IsEmpty )
			return false;

		if ( string.Equals( target.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		var displaced = target;
		target = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
		_slots[sourceSlotIndex] = displaced;
		held.Clear();
		NotifyInventoryChanged();
		return true;
	}

	public bool HostTryTakeOne( int slotIndex )
	{
		if ( !HasHostAuthority || !TryGetSlotRef( slotIndex, out var slot ) || slot.IsEmpty )
			return false;

		_slots[slotIndex].Count--;
		if ( _slots[slotIndex].Count <= 0 )
			_slots[slotIndex] = InventorySlot.Empty;

		NotifyInventoryChanged();
		return true;
	}

	public bool HostTryDropOne( int slotIndex, in InventoryCursorStack held )
	{
		if ( held.IsEmpty || !HasHostAuthority || !TryGetSlotRef( slotIndex, out _ ) )
			return false;

		ref var dest = ref _slots[slotIndex];

		if ( dest.IsEmpty )
		{
			dest = new InventorySlot { ResourceId = held.ResourceId, Count = 1 };
			NotifyInventoryChanged();
			return true;
		}

		if ( !string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		dest.Count++;
		NotifyInventoryChanged();
		return true;
	}

	public int HostPeekTakeHalfAmount( int slotIndex )
	{
		if ( !TryGetSlotRef( slotIndex, out var slot ) || slot.IsEmpty )
			return 0;

		return slot.Count / 2;
	}

	public bool HostTryTakeHalf( int slotIndex )
	{
		var half = HostPeekTakeHalfAmount( slotIndex );
		if ( half <= 0 || !HasHostAuthority )
			return false;

		_slots[slotIndex].Count -= half;
		if ( _slots[slotIndex].Count <= 0 )
			_slots[slotIndex] = InventorySlot.Empty;

		NotifyInventoryChanged();
		return true;
	}

	public int HostPeekPlaceHalfAmount( in InventoryCursorStack held ) =>
		held.IsEmpty ? 0 : held.Count / 2;

	public bool HostTryPlaceHalf( int slotIndex, ref InventoryCursorStack held )
	{
		var half = HostPeekPlaceHalfAmount( held );
		if ( half <= 0 || !HasHostAuthority || !TryGetSlotRef( slotIndex, out _ ) )
			return false;

		ref var dest = ref _slots[slotIndex];

		if ( dest.IsEmpty )
		{
			dest = new InventorySlot { ResourceId = held.ResourceId, Count = half };
			held.Count -= half;
			if ( held.Count <= 0 )
				held.Clear();
			NotifyInventoryChanged();
			return true;
		}

		if ( !string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		dest.Count += half;
		held.Count -= half;
		if ( held.Count <= 0 )
			held.Clear();
		NotifyInventoryChanged();
		return true;
	}

	public bool HostTryQuickMove( int fromSlotIndex, IInventoryGridHost targetGrid )
	{
		if ( !HasHostAuthority || targetGrid?.Inventory is null || targetGrid.Inventory != this )
			return false;

		if ( !TryGetSlotRef( fromSlotIndex, out var source ) || source.IsEmpty )
			return false;

		if ( !targetGrid.TryFindQuickMoveTarget( source, fromSlotIndex, out var targetIndex ) )
			return false;

		ref var from = ref _slots[fromSlotIndex];
		ref var to = ref _slots[targetIndex];

		if ( to.IsEmpty )
		{
			to = from;
			from = InventorySlot.Empty;
			NotifyInventoryChanged();
			return true;
		}

		if ( string.Equals( to.ResourceId, from.ResourceId, StringComparison.OrdinalIgnoreCase ) )
		{
			to.Count += from.Count;
			from = InventorySlot.Empty;
			NotifyInventoryChanged();
			return true;
		}

		return false;
	}

	public bool HostTryReturnStack( in InventoryCursorStack held )
	{
		if ( held.IsEmpty || !HasHostAuthority )
			return false;

		EnsureSlotArray();

		if ( TryFindStackSlot( held.ResourceId, out var stackIndex ) )
		{
			_slots[stackIndex].Count += held.Count;
			NotifyInventoryChanged();
			return true;
		}

		if ( TryFindFirstEmptySlot( out var emptyIndex ) )
		{
			_slots[emptyIndex] = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
			NotifyInventoryChanged();
			return true;
		}

		return false;
	}

	public bool TryFindFirstEmptySlot( out int slotIndex )
	{
		slotIndex = -1;
		EnsureSlotArray();

		for ( var i = 0; i < _slots.Length; i++ )
		{
			if ( !_slots[i].IsEmpty )
				continue;

			slotIndex = i;
			return true;
		}

		return false;
	}

	public bool TryFindStackSlot( string resourceId, out int slotIndex )
	{
		slotIndex = -1;
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		EnsureSlotArray();

		for ( var i = 0; i < _slots.Length; i++ )
		{
			if ( _slots[i].IsEmpty )
				continue;

			if ( !string.Equals( _slots[i].ResourceId, resourceId, StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( _slots[i].Count >= ResourceCatalog.GetMaxStack( resourceId ) )
				continue;

			slotIndex = i;
			return true;
		}

		return false;
	}

	bool ClientTryApplyPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( HasHostAuthority || held.IsEmpty || !TryGetSlotRef( slotIndex, out _ ) )
			return HostTryPlaceHeld( slotIndex, ref held );

		EnsureSlotArray();
		ref var dest = ref _slots[slotIndex];

		if ( dest.IsEmpty )
		{
			dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
			held.Clear();
			NotifyInventoryChanged();
			return true;
		}

		if ( string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
		{
			var maxStack = ResourceCatalog.GetMaxStack( held.ResourceId );
			var room = maxStack - dest.Count;
			if ( room <= 0 )
			{
				var displaced = dest;
				dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
				held.Set( displaced.ResourceId, displaced.Count );
				NotifyInventoryChanged();
				return true;
			}

			var add = Math.Min( held.Count, room );
			dest.Count += add;
			held.Count -= add;
			if ( held.Count <= 0 )
				held.Clear();

			NotifyInventoryChanged();
			return true;
		}

		var swap = dest;
		dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
		held.Set( swap.ResourceId, swap.Count );
		NotifyInventoryChanged();
		return true;
	}

	bool ClientTryApplySwapDrag( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( HasHostAuthority )
			return HostTrySwapDragToSlot( sourceSlotIndex, targetSlotIndex, ref held );

		if ( held.IsEmpty || sourceSlotIndex == targetSlotIndex )
			return false;

		EnsureSlotArray();
		if ( sourceSlotIndex < 0 || sourceSlotIndex >= _slots.Length || targetSlotIndex < 0 || targetSlotIndex >= _slots.Length )
			return false;

		ref var target = ref _slots[targetSlotIndex];
		if ( target.IsEmpty )
			return false;

		if ( string.Equals( target.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		var displaced = target;
		target = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
		_slots[sourceSlotIndex] = displaced;
		held.Clear();
		NotifyInventoryChanged();
		return true;
	}

	bool TryGetSlotRef( int index, out InventorySlot slot )
	{
		EnsureSlotArray();
		if ( index < 0 || index >= _slots.Length )
		{
			slot = InventorySlot.Empty;
			return false;
		}

		slot = _slots[index];
		return true;
	}

	[Rpc.Host]
	void RpcHostPickupAll( int slotIndex )
	{
		if ( !Networking.IsHost )
			return;

		HostTryPickupAll( slotIndex, out _ );
	}

	[Rpc.Host]
	void RpcHostPlaceHeld( int slotIndex, string resourceId, int count )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( resourceId, count );
		HostTryPlaceHeld( slotIndex, ref held );
	}

	[Rpc.Host]
	void RpcHostSwapDragToSlot( int sourceSlotIndex, int targetSlotIndex, string resourceId, int count )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( resourceId, count );
		HostTrySwapDragToSlot( sourceSlotIndex, targetSlotIndex, ref held );
	}

	[Rpc.Host]
	void RpcHostTakeOne( int slotIndex )
	{
		if ( !Networking.IsHost )
			return;

		HostTryTakeOne( slotIndex );
	}

	[Rpc.Host]
	void RpcHostDropOne( int slotIndex, string heldResourceId, int heldCount )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( heldResourceId, heldCount );
		HostTryDropOne( slotIndex, held );
	}

	[Rpc.Host]
	void RpcHostTakeHalf( int slotIndex )
	{
		if ( !Networking.IsHost )
			return;

		HostTryTakeHalf( slotIndex );
	}

	[Rpc.Host]
	void RpcHostPlaceHalf( int slotIndex, string heldResourceId, int heldCount )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( heldResourceId, heldCount );
		HostTryPlaceHalf( slotIndex, ref held );
	}

	[Rpc.Host]
	void RpcHostQuickMove( int fromSlotIndex, string targetGridId )
	{
		if ( !Networking.IsHost || string.IsNullOrWhiteSpace( targetGridId ) )
			return;

		var grid = new PlayerInventoryGridHost( targetGridId, this );
		HostTryQuickMove( fromSlotIndex, grid );
	}
}
