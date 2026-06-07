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

	/// <summary>Count of a resource in hotbar item stacks (not bindings/ghosts).</summary>
	public int CountResource( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return 0;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var total = 0;
		for ( var i = 0; i < SlotCount; i++ )
		{
			if ( _slots[i].IsEmpty )
				continue;

			if ( !ResourceCatalog.ResourceIdsMatch( _slots[i].ResourceId, resourceId ) )
				continue;

			total += _slots[i].Count;
		}

		return total;
	}

	/// <summary>Host: pickup into hotbar stacks, then empty slots with a matching binding ghost.</summary>
	public int TryAddResourcePickup( string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) || !HasHostAuthority )
			return amount;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var remaining = TryAddToMatchingStacks( resourceId, amount );
		return TryAddToBindingSlots( resourceId, remaining );
	}

	/// <summary>Host: merge pickup into existing hotbar stacks.</summary>
	public int TryAddToMatchingStacks( string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) || !HasHostAuthority )
			return amount;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var remaining = amount;

		for ( var i = 0; i < SlotCount && remaining > 0; i++ )
		{
			if ( _slots[i].IsEmpty )
				continue;

			if ( !ResourceCatalog.ResourceIdsMatch( _slots[i].ResourceId, resourceId ) )
				continue;

			remaining = TryAddToSlot( i, resourceId, remaining );
		}

		return remaining;
	}

	int TryAddToBindingSlots( string resourceId, int amount )
	{
		if ( amount <= 0 )
			return 0;

		var remaining = amount;

		for ( var i = 0; i < SlotCount && remaining > 0; i++ )
		{
			if ( !_slots[i].IsEmpty )
				continue;

			if ( !BindingMatches( i, resourceId ) )
				continue;

			remaining = TryAddToSlot( i, resourceId, remaining, rememberSlot: true );
		}

		return remaining;
	}

	/// <summary>How much could fit via hotbar stacks plus binding ghosts (read-only).</summary>
	public int PeekPickupAcceptAmount( string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) )
			return 0;

		var accepted = PeekMatchingStackAcceptAmount( resourceId, amount );
		var remaining = amount - accepted;
		if ( remaining <= 0 )
			return accepted;

		return accepted + PeekBindingGhostAcceptAmount( resourceId, remaining );
	}

	/// <summary>Simulate pickup absorb on a hotbar slot copy (stacks then binding ghosts).</summary>
	public int SimulatePickupAbsorb( InventorySlot[] slots, string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) || slots is null || slots.Length != SlotCount )
			return amount;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var remaining = amount;

		for ( var i = 0; i < SlotCount && remaining > 0; i++ )
		{
			if ( slots[i].IsEmpty )
				continue;

			if ( !ResourceCatalog.ResourceIdsMatch( slots[i].ResourceId, resourceId ) )
				continue;

			remaining = SimulateAddToSlot( ref slots[i], resourceId, remaining );
		}

		for ( var i = 0; i < SlotCount && remaining > 0; i++ )
		{
			if ( !slots[i].IsEmpty )
				continue;

			if ( !BindingMatches( i, resourceId ) )
				continue;

			remaining = SimulateAddToSlot( ref slots[i], resourceId, remaining );
		}

		return remaining;
	}

	/// <summary>How much could merge into existing hotbar stacks (read-only).</summary>
	public int PeekMatchingStackAcceptAmount( string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) )
			return 0;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var remaining = amount;

		for ( var i = 0; i < SlotCount && remaining > 0; i++ )
		{
			if ( _slots[i].IsEmpty )
				continue;

			if ( !ResourceCatalog.ResourceIdsMatch( _slots[i].ResourceId, resourceId ) )
				continue;

			remaining = PeekAddToSlot( _slots[i], resourceId, remaining );
		}

		return amount - remaining;
	}

	/// <summary>How much could fit into empty slots with a matching binding ghost (read-only).</summary>
	public int PeekBindingGhostAcceptAmount( string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) )
			return 0;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var remaining = amount;

		for ( var i = 0; i < SlotCount && remaining > 0; i++ )
		{
			if ( !_slots[i].IsEmpty )
				continue;

			if ( !BindingMatches( i, resourceId ) )
				continue;

			remaining = PeekAddToSlot( _slots[i], resourceId, remaining );
		}

		return amount - remaining;
	}

	/// <summary>Host: consume up to <paramref name="amount"/> from hotbar stacks.</summary>
	public int TryConsumeResource( string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) || !HasHostAuthority )
			return amount;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var remaining = amount;

		for ( var i = 0; i < SlotCount && remaining > 0; i++ )
		{
			if ( _slots[i].IsEmpty )
				continue;

			if ( !ResourceCatalog.ResourceIdsMatch( _slots[i].ResourceId, resourceId ) )
				continue;

			var take = Math.Min( remaining, _slots[i].Count );
			_slots[i].Count -= take;
			remaining -= take;
			if ( _slots[i].Count <= 0 )
				_slots[i] = InventorySlot.Empty;
		}

		if ( remaining < amount )
			NotifyChanged();

		return remaining;
	}

	/// <summary>Host: spill into hotbar when inventory has no room (remembered slot, stacks, then any empty slot).</summary>
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

	/// <summary>Simulate overflow placement into a slot copy (mutates <paramref name="slots"/>).</summary>
	internal int SimulateOverflowAbsorb( InventorySlot[] slots, string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) || slots is null || slots.Length != SlotCount )
			return amount;

		var remaining = amount;

		for ( var pass = 0; pass < 3 && remaining > 0; pass++ )
		{
			for ( var i = 0; i < SlotCount && remaining > 0; i++ )
			{
				var matches = pass switch
				{
					0 => BindingMatches( i, resourceId ),
					1 => !slots[i].IsEmpty
					     && string.Equals( slots[i].ResourceId, resourceId, StringComparison.OrdinalIgnoreCase ),
					2 => slots[i].IsEmpty,
					_ => false
				};

				if ( !matches )
					continue;

				remaining = SimulateAddToSlot( ref slots[i], resourceId, remaining );
			}
		}

		return remaining;
	}

	static int SimulateAddToSlot( ref InventorySlot slot, string resourceId, int amount )
	{
		if ( amount <= 0 )
			return 0;

		var maxStack = ResourceCatalog.GetMaxStack( resourceId );

		if ( slot.IsEmpty )
		{
			var place = Math.Min( amount, maxStack );
			slot = new InventorySlot { ResourceId = resourceId, Count = place };
			return amount - place;
		}

		if ( !string.Equals( slot.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase ) )
			return amount;

		var room = maxStack - slot.Count;
		if ( room <= 0 )
			return amount;

		var add = Math.Min( amount, room );
		slot.Count += add;
		return amount - add;
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

	/// <summary>Local client: forget a hotbar slot binding (ghost icon).</summary>
	public bool OwnerClearBinding( int slotIndex )
	{
		if ( !IsLocalManagingClient() || slotIndex < 0 || slotIndex >= SlotCount )
			return false;

		if ( string.IsNullOrWhiteSpace( _bindings[slotIndex] ) )
			return false;

		ClearBinding( slotIndex );
		return true;
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
		ClearBindingIfSlotEmpty( slotIndex );
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
		ClearBindingIfSlotEmpty( slotIndex );
		NotifyChanged();
		return true;
	}

	public bool OwnerTryTakeOne( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		if ( !IsLocalManagingClient() || !TryGetSlotRef( slotIndex, out var slot ) || slot.IsEmpty )
			return false;

		if ( HasHostAuthority )
		{
			if ( !HostTryTakeOne( slotIndex ) )
				return false;

			taken = new InventorySlot { ResourceId = slot.ResourceId, Count = 1 };
			return true;
		}

		_slots[slotIndex].Count--;
		if ( _slots[slotIndex].Count <= 0 )
		{
			_slots[slotIndex] = InventorySlot.Empty;
			ClearBindingIfSlotEmpty( slotIndex );
		}

		taken = new InventorySlot { ResourceId = slot.ResourceId, Count = 1 };
		NotifyChanged();
		RpcHostTakeOne( slotIndex );
		return true;
	}

	public bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held, out int placedCount )
	{
		placedCount = 0;
		if ( held.IsEmpty || !IsLocalManagingClient() || !TryGetSlotRef( slotIndex, out var dest ) )
			return false;

		if ( !dest.IsEmpty && !string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		if ( HasHostAuthority )
		{
			if ( !HostTryDropOne( slotIndex, held ) )
				return false;

			placedCount = 1;
			return true;
		}

		var add = dest.IsEmpty
			? ResourceCatalog.ClampAddToStack( held.ResourceId, 0, 1 )
			: ResourceCatalog.ClampAddToStack( held.ResourceId, dest.Count, 1 );
		if ( add <= 0 )
			return false;

		RpcHostDropOne( slotIndex, held.ResourceId, held.Count );
		if ( dest.IsEmpty )
			_slots[slotIndex] = new InventorySlot { ResourceId = held.ResourceId, Count = add };
		else
			_slots[slotIndex].Count += add;

		placedCount = add;
		UpdateBindingFromSlot( slotIndex );
		NotifyChanged();
		return true;
	}

	public bool OwnerTryTakeHalf( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		if ( !IsLocalManagingClient() || !TryGetSlotRef( slotIndex, out var slot ) || slot.IsEmpty )
			return false;

		var half = slot.Count / 2;
		if ( half <= 0 )
			return false;

		if ( HasHostAuthority )
		{
			if ( !HostTryTakeHalf( slotIndex ) )
				return false;

			taken = new InventorySlot { ResourceId = slot.ResourceId, Count = half };
			return true;
		}

		_slots[slotIndex].Count -= half;
		if ( _slots[slotIndex].Count <= 0 )
		{
			_slots[slotIndex] = InventorySlot.Empty;
			ClearBindingIfSlotEmpty( slotIndex );
		}

		taken = new InventorySlot { ResourceId = slot.ResourceId, Count = half };
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

		var add = dest.IsEmpty
			? ResourceCatalog.ClampAddToStack( held.ResourceId, 0, half )
			: ResourceCatalog.ClampAddToStack( held.ResourceId, dest.Count, half );
		if ( add <= 0 )
			return false;

		RpcHostPlaceHalf( slotIndex, held.ResourceId, held.Count );
		if ( dest.IsEmpty )
			_slots[slotIndex] = new InventorySlot { ResourceId = held.ResourceId, Count = add };
		else
			_slots[slotIndex].Count += add;

		held.Count -= add;
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
		{
			_slots[slotIndex] = InventorySlot.Empty;
			ClearBindingIfSlotEmpty( slotIndex );
		}

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
		{
			_slots[slotIndex] = InventorySlot.Empty;
			ClearBindingIfSlotEmpty( slotIndex );
		}

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
			var place = ResourceCatalog.ClampAddToStack( held.ResourceId, 0, half );
			if ( place <= 0 )
				return false;

			dest = new InventorySlot { ResourceId = held.ResourceId, Count = place };
			held.Count -= place;
			if ( held.Count <= 0 )
				held.Clear();

			UpdateBindingFromSlot( slotIndex );
			NotifyChanged();
			return true;
		}

		if ( !string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		var add = ResourceCatalog.ClampAddToStack( held.ResourceId, dest.Count, half );
		if ( add <= 0 )
			return false;

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
			if ( !TryFinishDragDropOntoSameSlot( targetSlotIndex, ref held ) )
				return false;

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
			return TryFinishDragDropOntoSameSlot( targetSlotIndex, ref held );

		if ( !TryGetSlotRef( targetSlotIndex, out var target ) )
			return false;

		if ( !target.IsEmpty && !string.Equals( target.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
			return HostTrySwapDragToSlot( sourceSlotIndex, targetSlotIndex, ref held );

		return HostTryPlaceHeld( targetSlotIndex, ref held );
	}

	bool TryFinishDragDropOntoSameSlot( int slotIndex, ref InventoryCursorStack held )
	{
		if ( !TryGetSlotRef( slotIndex, out _ ) )
			return false;

		ref var slot = ref _slots[slotIndex];
		if ( slot.IsEmpty )
		{
			slot = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
			held.Clear();
			UpdateBindingFromSlot( slotIndex );
			NotifyChanged();
			return true;
		}

		if ( !ResourceCatalog.ResourceIdsMatch( slot.ResourceId, held.ResourceId ) )
			return false;

		var add = ResourceCatalog.ClampAddToStack( held.ResourceId, slot.Count, held.Count );
		if ( add <= 0 )
			return false;

		slot.Count += add;
		held.Count -= add;
		if ( held.Count <= 0 )
			held.Clear();

		UpdateBindingFromSlot( slotIndex );
		NotifyChanged();
		return true;
	}

	public bool HostTryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !HasHostAuthority || !TryGetSlotRef( slotIndex, out _ ) )
			return false;

		ref var dest = ref _slots[slotIndex];

		if ( dest.IsEmpty )
		{
			var place = ResourceCatalog.ClampAddToStack( held.ResourceId, 0, held.Count );
			if ( place <= 0 )
				return false;

			dest = new InventorySlot { ResourceId = held.ResourceId, Count = place };
			held.Count -= place;
			if ( held.Count <= 0 )
				held.Clear();

			UpdateBindingFromSlot( slotIndex );
			NotifyChanged();
			return true;
		}

		if ( string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
		{
			var room = ResourceCatalog.GetMaxStack( held.ResourceId ) - dest.Count;
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
			var place = ResourceCatalog.ClampAddToStack( held.ResourceId, 0, held.Count );
			if ( place <= 0 )
				return false;

			dest = new InventorySlot { ResourceId = held.ResourceId, Count = place };
			held.Count -= place;
			if ( held.Count <= 0 )
				held.Clear();

			NotifyChanged();
			return true;
		}

		if ( string.Equals( dest.ResourceId, held.ResourceId, StringComparison.OrdinalIgnoreCase ) )
		{
			var add = ResourceCatalog.ClampAddToStack( held.ResourceId, dest.Count, held.Count );
			if ( add <= 0 )
			{
				var displaced = dest;
				dest = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
				held.Set( displaced.ResourceId, displaced.Count );
				NotifyChanged();
				return true;
			}

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

	void ClearBindingIfSlotEmpty( int slotIndex )
	{
		if ( slotIndex < 0 || slotIndex >= SlotCount || !_slots[slotIndex].IsEmpty )
			return;

		ClearBinding( slotIndex );
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
