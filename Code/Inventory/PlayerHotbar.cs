using System;
using Sandbox;

namespace Survival;

/// <summary>10-slot hotbar storage (host-authoritative). Client bindings steer auto-slot on pickup.</summary>
[Title( "Player Hotbar" )]
public sealed class PlayerHotbar : Component
{
	public const int SlotCount = 10;

	public event Action HotbarChanged;
	public event Action<int> ActiveSlotChanged;

	[Sync] public int ActiveSlotIndex { get; private set; }

	readonly InventorySlot[] _slots = new InventorySlot[SlotCount];
	string[] _bindings = PlayerHotbarBindingsStore.CreateEmpty();

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	public ReadOnlySpan<InventorySlot> Slots => _slots;

	public InventorySlot GetSlot( int index )
	{
		if ( index < 0 || index >= SlotCount )
			return InventorySlot.Empty;
		return _slots[index];
	}

	public string GetBinding( int index )
	{
		if ( index < 0 || index >= SlotCount )
			return string.Empty;
		return _bindings[index] ?? string.Empty;
	}

	protected override void OnStart()
	{
		base.OnStart();
		if ( IsLocalManagingClient() )
			_bindings = PlayerHotbarBindingsStore.Load();

		HotbarChanged?.Invoke();
	}

	public bool IsLocalManagingClient()
	{
		if ( GameObject.Network is not { Active: true } )
			return true;

		if ( GameObject.Network.Owner is not { } owner )
			return Networking.IsHost;

		return ConnectionIdentity.SameClient( owner, Connection.Local );
	}

	public void SetActiveSlot( int index )
	{
		index = WrapSlotIndex( index );
		if ( ActiveSlotIndex == index )
			return;

		ActiveSlotIndex = index;
		ActiveSlotChanged?.Invoke( index );
	}

	public void StepActiveSlot( int delta )
	{
		if ( delta == 0 )
			return;

		SetActiveSlot( ActiveSlotIndex + delta );
	}

	public static int WrapSlotIndex( int index )
	{
		var wrapped = index % SlotCount;
		return wrapped < 0 ? wrapped + SlotCount : wrapped;
	}

	/// <summary>Host: overflow into hotbar when inventory has no room (remembered slot, stacks, then any empty slot).</summary>
	public int TryAddResourceOverflow( string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) || !HasHostAuthority )
			return amount;

		var remaining = amount;

		for ( var pass = 0; pass < 3 && remaining > 0; pass++ )
		{
			for ( var i = 0; i < SlotCount && remaining > 0; i++ )
			{
				var matches = pass switch
				{
					0 => BindingMatches( i, resourceId ),
					1 => !_slots[i].IsEmpty
					     && string.Equals( _slots[i].ResourceId, resourceId, StringComparison.OrdinalIgnoreCase ),
					2 => _slots[i].IsEmpty,
					_ => false
				};

				if ( !matches )
					continue;

				remaining = TryAddToSlot( i, resourceId, remaining, rememberSlot: pass != 1 );
			}
		}

		return remaining;
	}

	/// <summary>How many of <paramref name="amount"/> could fit via overflow rules (no mutation).</summary>
	public int PeekOverflowAcceptAmount( string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) )
			return 0;

		var remaining = amount;

		for ( var pass = 0; pass < 3 && remaining > 0; pass++ )
		{
			for ( var i = 0; i < SlotCount && remaining > 0; i++ )
			{
				var matches = pass switch
				{
					0 => BindingMatches( i, resourceId ),
					1 => !_slots[i].IsEmpty
					     && string.Equals( _slots[i].ResourceId, resourceId, StringComparison.OrdinalIgnoreCase ),
					2 => _slots[i].IsEmpty,
					_ => false
				};

				if ( !matches )
					continue;

				remaining = PeekAddToSlot( _slots[i], resourceId, remaining );
			}
		}

		return amount - remaining;
	}

	static int PeekAddToSlot( in InventorySlot slot, string resourceId, int amount )
	{
		if ( amount <= 0 )
			return 0;

		var maxStack = ResourceCatalog.GetMaxStack( resourceId );

		if ( slot.IsEmpty )
			return Math.Max( 0, amount - Math.Min( amount, maxStack ) );

		if ( !string.Equals( slot.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase ) )
			return amount;

		var room = maxStack - slot.Count;
		if ( room <= 0 )
			return amount;

		return Math.Max( 0, amount - Math.Min( amount, room ) );
	}

	int TryAddToSlot( int slotIndex, string resourceId, int amount, bool rememberSlot = false )
	{
		if ( amount <= 0 )
			return 0;

		var maxStack = ResourceCatalog.GetMaxStack( resourceId );
		ref var slot = ref _slots[slotIndex];

		if ( slot.IsEmpty )
		{
			var place = Math.Min( amount, maxStack );
			slot = new InventorySlot { ResourceId = resourceId, Count = place };
			if ( rememberSlot )
				RememberResourceSlot( slotIndex, resourceId );

			NotifyChanged();
			return amount - place;
		}

		if ( !string.Equals( slot.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase ) )
			return amount;

		var room = maxStack - slot.Count;
		if ( room <= 0 )
			return amount;

		var add = Math.Min( amount, room );
		slot.Count += add;
		NotifyChanged();
		return amount - add;
	}

	bool BindingMatches( int slotIndex, string resourceId )
	{
		var binding = GetBinding( slotIndex );
		return !string.IsNullOrWhiteSpace( binding )
		       && string.Equals( binding, resourceId, StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>Remember one hotbar slot per resource type (clears ghosts on other slots for that type).</summary>
	public void RememberResourceSlot( int slotIndex, string resourceId )
	{
		if ( slotIndex < 0 || slotIndex >= SlotCount )
			return;

		if ( string.IsNullOrWhiteSpace( resourceId ) )
		{
			ClearBinding( slotIndex );
			return;
		}

		var changed = false;
		for ( var i = 0; i < SlotCount; i++ )
		{
			if ( i == slotIndex )
				continue;

			if ( !string.Equals( _bindings[i], resourceId, StringComparison.OrdinalIgnoreCase ) )
				continue;

			_bindings[i] = string.Empty;
			changed = true;
		}

		if ( !string.Equals( _bindings[slotIndex], resourceId, StringComparison.OrdinalIgnoreCase ) )
		{
			_bindings[slotIndex] = resourceId;
			changed = true;
		}

		if ( !changed )
			return;

		PersistBindingsLocal();
		RpcSyncBindings( _bindings );
	}

	public void SetBinding( int slotIndex, string resourceId ) => RememberResourceSlot( slotIndex, resourceId );

	public void ClearBinding( int slotIndex )
	{
		if ( slotIndex < 0 || slotIndex >= SlotCount || string.IsNullOrWhiteSpace( _bindings[slotIndex] ) )
			return;

		_bindings[slotIndex] = string.Empty;
		PersistBindingsLocal();
		RpcSyncBindings( _bindings );
	}

	void PersistBindingsLocal()
	{
		if ( IsLocalManagingClient() )
			PlayerHotbarBindingsStore.Save( _bindings );
	}

	[Rpc.Owner]
	void RpcSyncBindings( string[] bindings )
	{
		if ( bindings is null || bindings.Length != SlotCount )
			return;

		_bindings = bindings;
		HotbarChanged?.Invoke();
	}

	public bool OwnerTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryPickupAll( slotIndex, out picked );

		if ( !TryGetSlotRef( slotIndex, out var slot ) || slot.IsEmpty )
			return false;

		_slots[slotIndex] = InventorySlot.Empty;
		NotifyChanged();
		RpcHostPickupAll( slotIndex );
		picked = slot;
		return true;
	}

	public bool HostTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( !HasHostAuthority || !TryGetSlotRef( slotIndex, out var slot ) || slot.IsEmpty )
			return false;

		picked = slot;
		_slots[slotIndex] = InventorySlot.Empty;
		NotifyChanged();
		return true;
	}

	public bool OwnerTryTakeOne( int slotIndex )
	{
		if ( !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryTakeOne( slotIndex );

		if ( !TryGetSlotRef( slotIndex, out var slot ) || slot.IsEmpty )
			return false;

		_slots[slotIndex].Count--;
		if ( _slots[slotIndex].Count <= 0 )
			_slots[slotIndex] = InventorySlot.Empty;

		NotifyChanged();
		RpcHostTakeOne( slotIndex );
		return true;
	}

	public bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryDropOne( slotIndex, held );

		if ( !TryGetSlotRef( slotIndex, out var dest ) )
			return false;

		if ( !dest.IsEmpty && !string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		RpcHostDropOne( slotIndex, held.ResourceId, held.Count );
		if ( dest.IsEmpty )
			_slots[slotIndex] = new InventorySlot { ResourceId = held.ResourceId, Count = 1 };
		else
			_slots[slotIndex].Count++;

		UpdateBindingFromSlot( slotIndex );
		NotifyChanged();
		return true;
	}

	public bool OwnerTryTakeHalf( int slotIndex )
	{
		if ( !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryTakeHalf( slotIndex );

		if ( !TryGetSlotRef( slotIndex, out var slot ) || slot.IsEmpty )
			return false;

		var half = slot.Count / 2;
		if ( half <= 0 )
			return false;

		_slots[slotIndex].Count -= half;
		if ( _slots[slotIndex].Count <= 0 )
			_slots[slotIndex] = InventorySlot.Empty;

		NotifyChanged();
		RpcHostTakeHalf( slotIndex );
		return true;
	}

	public bool OwnerTryPlaceHalf( int slotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryPlaceHalf( slotIndex, ref held );

		if ( !TryGetSlotRef( slotIndex, out var dest ) )
			return false;

		var half = held.Count / 2;
		if ( half <= 0 )
			return false;

		if ( !dest.IsEmpty && !string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		RpcHostPlaceHalf( slotIndex, held.ResourceId, held.Count );
		if ( dest.IsEmpty )
			_slots[slotIndex] = new InventorySlot { ResourceId = held.ResourceId, Count = half };
		else
			_slots[slotIndex].Count += half;

		held.Count -= half;
		if ( held.Count <= 0 )
			held.Clear();

		UpdateBindingFromSlot( slotIndex );
		NotifyChanged();
		return true;
	}

	public bool HostTryTakeOne( int slotIndex )
	{
		if ( !HasHostAuthority || !TryGetSlotRef( slotIndex, out var slot ) || slot.IsEmpty )
			return false;

		_slots[slotIndex].Count--;
		if ( _slots[slotIndex].Count <= 0 )
			_slots[slotIndex] = InventorySlot.Empty;

		NotifyChanged();
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
			UpdateBindingFromSlot( slotIndex );
			NotifyChanged();
			return true;
		}

		if ( !string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		var maxStack = ResourceCatalog.GetMaxStack( held.ResourceId );
		if ( dest.Count >= maxStack )
			return false;

		dest.Count++;
		NotifyChanged();
		return true;
	}

	public bool HostTryTakeHalf( int slotIndex )
	{
		if ( !HasHostAuthority || !TryGetSlotRef( slotIndex, out var slot ) || slot.IsEmpty )
			return false;

		var half = slot.Count / 2;
		if ( half <= 0 )
			return false;

		_slots[slotIndex].Count -= half;
		if ( _slots[slotIndex].Count <= 0 )
			_slots[slotIndex] = InventorySlot.Empty;

		NotifyChanged();
		return true;
	}

	public bool HostTryPlaceHalf( int slotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !HasHostAuthority || !TryGetSlotRef( slotIndex, out _ ) )
			return false;

		var half = held.Count / 2;
		if ( half <= 0 )
			return false;

		ref var dest = ref _slots[slotIndex];

		if ( dest.IsEmpty )
		{
			dest = new InventorySlot { ResourceId = held.ResourceId, Count = half };
			held.Count -= half;
			if ( held.Count <= 0 )
				held.Clear();

			UpdateBindingFromSlot( slotIndex );
			NotifyChanged();
			return true;
		}

		if ( !string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		var maxStack = ResourceCatalog.GetMaxStack( held.ResourceId );
		var room = maxStack - dest.Count;
		if ( room <= 0 )
			return false;

		var add = Math.Min( half, room );
		dest.Count += add;
		held.Count -= add;
		if ( held.Count <= 0 )
			held.Clear();

		NotifyChanged();
		return true;
	}

	public bool OwnerTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryFinishDragDrop( sourceSlotIndex, targetSlotIndex, ref held );

		if ( sourceSlotIndex == targetSlotIndex )
		{
			_slots[targetSlotIndex] = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
			held.Clear();
			NotifyChanged();
			RpcHostFinishDragDrop( sourceSlotIndex, targetSlotIndex, held.ResourceId, held.Count );
			return true;
		}

		if ( !TryGetSlotRef( targetSlotIndex, out var target ) )
			return false;

		if ( !target.IsEmpty && !string.Equals( target.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return HostTrySwapDragToSlot( sourceSlotIndex, targetSlotIndex, ref held );

		RpcHostFinishDragDrop( sourceSlotIndex, targetSlotIndex, held.ResourceId, held.Count );
		return ClientTryApplyPlaceHeld( targetSlotIndex, ref held );
	}

	public bool HostTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !HasHostAuthority )
			return false;

		if ( sourceSlotIndex == targetSlotIndex )
		{
			_slots[targetSlotIndex] = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
			held.Clear();
			UpdateBindingFromSlot( targetSlotIndex );
			NotifyChanged();
			return true;
		}

		if ( !TryGetSlotRef( targetSlotIndex, out var target ) )
			return false;

		if ( !target.IsEmpty && !string.Equals( target.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return HostTrySwapDragToSlot( sourceSlotIndex, targetSlotIndex, ref held );

		return HostTryPlaceHeld( targetSlotIndex, ref held );
	}

	public bool HostTryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !HasHostAuthority || !TryGetSlotRef( slotIndex, out _ ) )
			return false;

		ref var dest = ref _slots[slotIndex];

		if ( dest.IsEmpty )
		{
			dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
			held.Clear();
			UpdateBindingFromSlot( slotIndex );
			NotifyChanged();
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
				UpdateBindingFromSlot( slotIndex );
				NotifyChanged();
				return true;
			}

			var add = Math.Min( held.Count, room );
			dest.Count += add;
			held.Count -= add;
			if ( held.Count <= 0 )
				held.Clear();

			UpdateBindingFromSlot( slotIndex );
			NotifyChanged();
			return true;
		}

		var swap = dest;
		dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
		held.Set( swap.ResourceId, swap.Count );
		UpdateBindingFromSlot( slotIndex );
		NotifyChanged();
		return true;
	}

	public bool HostTrySwapDragToSlot( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !HasHostAuthority || sourceSlotIndex == targetSlotIndex )
			return false;

		if ( !TryGetSlotRef( targetSlotIndex, out var target ) || target.IsEmpty )
			return false;

		if ( string.Equals( target.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		var displaced = target;
		_slots[targetSlotIndex] = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
		_slots[sourceSlotIndex] = displaced;
		held.Clear();
		UpdateBindingFromSlot( targetSlotIndex );
		UpdateBindingFromSlot( sourceSlotIndex );
		NotifyChanged();
		return true;
	}

	bool ClientTryApplyPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( HasHostAuthority || held.IsEmpty || !TryGetSlotRef( slotIndex, out _ ) )
			return HostTryPlaceHeld( slotIndex, ref held );

		ref var dest = ref _slots[slotIndex];
		if ( dest.IsEmpty )
		{
			dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
			held.Clear();
			NotifyChanged();
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
				NotifyChanged();
				return true;
			}

			var add = Math.Min( held.Count, room );
			dest.Count += add;
			held.Count -= add;
			if ( held.Count <= 0 )
				held.Clear();

			NotifyChanged();
			return true;
		}

		var swap = dest;
		dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
		held.Set( swap.ResourceId, swap.Count );
		NotifyChanged();
		return true;
	}

	public bool OwnerTryReturnStack( ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryReturnStack( ref held );

		for ( var i = 0; i < SlotCount; i++ )
		{
			if ( _slots[i].IsEmpty )
				continue;

			if ( !string.Equals( _slots[i].ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
				continue;

			var maxStack = ResourceCatalog.GetMaxStack( held.ResourceId );
			var room = maxStack - _slots[i].Count;
			if ( room <= 0 )
				continue;

			var add = Math.Min( held.Count, room );
			_slots[i].Count += add;
			NotifyChanged();
			return add == held.Count;
		}

		for ( var i = 0; i < SlotCount; i++ )
		{
			if ( !_slots[i].IsEmpty )
				continue;

			_slots[i] = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
			NotifyChanged();
			return true;
		}

		return false;
	}

	public bool HostTryReturnStack( ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !HasHostAuthority )
			return false;

		for ( var i = 0; i < SlotCount; i++ )
		{
			if ( _slots[i].IsEmpty )
				continue;

			if ( !string.Equals( _slots[i].ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
				continue;

			var maxStack = ResourceCatalog.GetMaxStack( held.ResourceId );
			var room = maxStack - _slots[i].Count;
			if ( room <= 0 )
				continue;

			var add = Math.Min( held.Count, room );
			_slots[i].Count += add;
			held.Count -= add;
			if ( held.Count <= 0 )
				held.Clear();

			NotifyChanged();
			return held.IsEmpty;
		}

		for ( var i = 0; i < SlotCount; i++ )
		{
			if ( !_slots[i].IsEmpty )
				continue;

			_slots[i] = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
			held.Clear();
			UpdateBindingFromSlot( i );
			NotifyChanged();
			return true;
		}

		return false;
	}

	void UpdateBindingFromSlot( int slotIndex )
	{
		if ( slotIndex < 0 || slotIndex >= SlotCount || _slots[slotIndex].IsEmpty )
			return;

		RememberResourceSlot( slotIndex, _slots[slotIndex].ResourceId );
	}

	void NotifyChanged()
	{
		HotbarChanged?.Invoke();

		if ( GameObject.Network is not { Active: true } || !Networking.IsHost )
			return;

		if ( GameObject.Network.Owner is not { } owner )
			return;

		if ( ConnectionIdentity.SameClient( owner, Connection.Local ) )
			return;

		var ids = new string[SlotCount];
		var counts = new int[SlotCount];
		for ( var i = 0; i < SlotCount; i++ )
		{
			ids[i] = _slots[i].ResourceId ?? string.Empty;
			counts[i] = _slots[i].Count;
		}

		RpcOwnerHotbarSync( ids, counts, ActiveSlotIndex );
	}

	[Rpc.Owner]
	void RpcOwnerHotbarSync( string[] resourceIds, int[] counts, int activeSlot )
	{
		if ( resourceIds is null || counts is null )
			return;

		var n = Math.Min( SlotCount, Math.Min( resourceIds.Length, counts.Length ) );
		for ( var i = 0; i < n; i++ )
		{
			var id = resourceIds[i];
			var c = counts[i];
			_slots[i] = string.IsNullOrWhiteSpace( id ) || c <= 0
				? InventorySlot.Empty
				: new InventorySlot { ResourceId = id, Count = c };
		}

		ActiveSlotIndex = WrapSlotIndex( activeSlot );
		HotbarChanged?.Invoke();
	}

	[Rpc.Host]
	void RpcHostPickupAll( int slotIndex )
	{
		if ( !Networking.IsHost )
			return;

		HostTryPickupAll( slotIndex, out _ );
	}

	[Rpc.Host]
	void RpcHostFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, string resourceId, int count )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( resourceId, count );
		HostTryFinishDragDrop( sourceSlotIndex, targetSlotIndex, ref held );
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

	bool TryGetSlotRef( int index, out InventorySlot slot )
	{
		if ( index < 0 || index >= SlotCount )
		{
			slot = InventorySlot.Empty;
			return false;
		}

		slot = _slots[index];
		return true;
	}
}
