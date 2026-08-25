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

	/// <summary>Fired on the managing client when resources are successfully added (harvest, craft output, etc.).</summary>
	public event Action<ResourcePickupNotice> ResourcePickedUp;

	InventorySlot[] _slots = Array.Empty<InventorySlot>();

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnStart()
	{
		base.OnStart();
		ResourceItemLibraryHost.EnsureSpawned( Scene );
		EnsureSlotArray();
		MigrateLegacyResourceSlots();
	}

	void MigrateLegacyResourceSlots()
	{
		if ( !HasHostAuthority )
			return;

		EnsureSlotArray();

		var changed = false;
		for ( var i = 0; i < _slots.Length; i++ )
		{
			if ( _slots[i].IsEmpty )
				continue;

			var normalized = ResourceCatalog.NormalizeResourceId( _slots[i].ResourceId );
			if ( string.Equals( normalized, _slots[i].ResourceId, StringComparison.OrdinalIgnoreCase ) )
				continue;

			_slots[i] = new InventorySlot { ResourceId = normalized, Count = _slots[i].Count, Wear = _slots[i].Wear };
			changed = true;
		}

		if ( changed )
			NotifyInventoryChanged();
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

	/// <summary>Total count of a resource in inventory plus hotbar stacks.</summary>
	public int CountResource( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return 0;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var total = CountResourceInInventory( resourceId );

		var hotbar = Components.Get<PlayerHotbar>();
		if ( hotbar is not null )
			total += hotbar.CountResource( resourceId );

		return total;
	}

	int CountResourceInInventory( string resourceId )
	{
		EnsureSlotArray();
		return InventoryStackRules.CountResource( _slots, resourceId );
	}

	public bool HasResources( IReadOnlyList<CraftingIngredient> ingredients )
	{
		if ( ingredients is null || ingredients.Count == 0 )
			return false;

		for ( var i = 0; i < ingredients.Count; i++ )
		{
			var ing = ingredients[i];
			if ( ing is null || string.IsNullOrWhiteSpace( ing.ResourceId ) || ing.Amount <= 0 )
				return false;

			if ( CountResource( ing.ResourceId ) < ing.Amount )
				return false;
		}

		return true;
	}

	/// <summary>Host/offline: remove stacked resources for crafting.</summary>
	public bool HostTryConsumeResources( IReadOnlyList<CraftingIngredient> ingredients )
	{
		if ( !HasHostAuthority || ingredients is null || ingredients.Count == 0 )
			return false;

		if ( !HasResources( ingredients ) )
			return false;

		EnsureSlotArray();

		var hotbar = Components.Get<PlayerHotbar>();

		for ( var i = 0; i < ingredients.Count; i++ )
		{
			var ing = ingredients[i];
			var remaining = ing.Amount;

			if ( hotbar is not null )
				remaining = hotbar.TryConsumeResource( ing.ResourceId, remaining );

			for ( var slotIndex = 0; slotIndex < _slots.Length && remaining > 0; slotIndex++ )
			{
				ref var slot = ref _slots[slotIndex];
				if ( slot.IsEmpty )
					continue;

				if ( !ResourceCatalog.ResourceIdsMatch( slot.ResourceId, ing.ResourceId ) )
					continue;

				var take = Math.Min( remaining, slot.Count );
				slot.Count -= take;
				remaining -= take;
				if ( slot.Count <= 0 )
					slot = InventorySlot.Empty;
			}

			if ( remaining > 0 )
				return false;
		}

		NotifyInventoryChanged();
		return true;
	}

	/// <summary>Local UI check: whether <paramref name="amount"/> more of a resource could fit.</summary>
	public bool CanFitResource( string resourceId, int amount ) => SimulateFitRoom( resourceId, amount );

	/// <summary>Whether the inventory has room for more of a resource (host/offline).</summary>
	public bool HostCanFitResource( string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) || !HasHostAuthority )
			return false;

		return SimulateFitRoom( resourceId, amount );
	}

	bool SimulateFitRoom( string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		return PeekAcceptAmount( resourceId, amount ) >= amount;
	}

	/// <summary>True if inventory plus hotbar overflow can hold at least <paramref name="amount"/>.</summary>
	public bool CanAcceptResource( string resourceId, int amount ) =>
		PeekAcceptAmount( resourceId, amount ) >= amount;

	/// <summary>True if every listed resource amount can fit (hotbar stacks, inventory, then hotbar spillover).</summary>
	public bool CanAcceptResourceBundle( IReadOnlyList<(string ResourceId, int Amount)> needs )
	{
		if ( needs is null || needs.Count == 0 )
			return false;

		EnsureSlotArray();

		var scratchInv = new InventorySlot[_slots.Length];
		for ( var i = 0; i < _slots.Length; i++ )
			scratchInv[i] = _slots[i];

		var hotbar = Components.Get<PlayerHotbar>();
		InventorySlot[] scratchHot = null;
		if ( hotbar is not null )
		{
			scratchHot = new InventorySlot[PlayerHotbar.SlotCount];
			for ( var i = 0; i < scratchHot.Length; i++ )
				scratchHot[i] = hotbar.GetSlot( i );
		}

		foreach ( var (resourceId, amount) in needs )
		{
			if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) )
				continue;

			var normalized = ResourceCatalog.NormalizeResourceId( resourceId );
			var remaining = amount;

			if ( scratchHot is not null )
				remaining = hotbar.SimulatePickupAbsorb( scratchHot, normalized, remaining );

			if ( remaining > 0 )
				remaining = PeekInventoryAbsorb( scratchInv, normalized, remaining );

			if ( remaining > 0 )
				return false;
		}

		return true;
	}

	/// <summary>Host/offline: deposit multiple harvest loot lines (guaranteed lines should be pre-checked).</summary>
	public bool HostTryAddHarvestLoot( HarvestLootItem[] loot )
	{
		if ( loot is null || loot.Length == 0 )
			return false;

		if ( !HasHostAuthority )
			return false;

		var addedAny = false;
		foreach ( var item in loot )
		{
			if ( item.Amount <= 0 || string.IsNullOrWhiteSpace( item.ResourceId ) )
				continue;

			if ( HostTryAddResource( item.ResourceId, item.Amount ) )
				addedAny = true;
		}

		return addedAny;
	}

	/// <summary>How much of <paramref name="amount"/> can fit (hotbar stacks + binding ghosts, then inventory).</summary>
	public int PeekAcceptAmount( string resourceId, int amount )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) )
			return 0;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		EnsureSlotArray();

		var remaining = amount;
		var hotbar = Components.Get<PlayerHotbar>();

		if ( hotbar is not null )
		{
			var hotbarAccept = hotbar.PeekPickupAcceptAmount( resourceId, remaining );
			remaining -= hotbarAccept;
		}

		if ( remaining > 0 )
			remaining = PeekInventoryAbsorb( _slots, resourceId, remaining );

		return amount - remaining;
	}

	static int PeekInventoryAbsorb( InventorySlot[] slots, string resourceId, int remaining )
	{
		if ( remaining <= 0 )
			return remaining;

		var maxStack = ResourceCatalog.GetMaxStack( resourceId );

		while ( remaining > 0 )
		{
			var progressed = false;

			for ( var i = 0; i < slots.Length && remaining > 0; i++ )
			{
				if ( slots[i].IsEmpty )
					continue;

				if ( !string.Equals( slots[i].ResourceId, resourceId, StringComparison.OrdinalIgnoreCase ) )
					continue;

				var room = maxStack - slots[i].Count;
				if ( room <= 0 )
					continue;

				var add = Math.Min( remaining, room );
				remaining -= add;
				progressed = true;
			}

			if ( remaining <= 0 )
				break;

			var placed = false;
			for ( var i = 0; i < slots.Length; i++ )
			{
				if ( !slots[i].IsEmpty )
					continue;

				var add = Math.Min( remaining, maxStack );
				remaining -= add;
				placed = true;
				break;
			}

			if ( !progressed && !placed )
				break;
		}

		return remaining;
	}

	int PeekInventoryAbsorb( string resourceId, int remaining ) =>
		PeekInventoryAbsorb( _slots, resourceId, remaining );

	/// <summary>Host/offline: add harvested resources into inventory. <paramref name="wear"/> carries pre-worn durability (world pickups).</summary>
	public bool HostTryAddResource( string resourceId, int amount, int wear = 0 )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		if ( !HasHostAuthority )
			return false;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		EnsureSlotArray();

		var remaining = amount;
		var hotbar = Components.Get<PlayerHotbar>();

		if ( hotbar is not null )
			remaining = hotbar.TryAddResourcePickup( resourceId, remaining, wear );

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
						break;
				}
			}

			if ( !TryFindFirstEmptySlot( out var emptyIndex ) )
				break;

			var place = Math.Min( remaining, maxStack );
			_slots[emptyIndex] = new InventorySlot { ResourceId = resourceId, Count = place, Wear = wear };
			remaining -= place;
		}

		var added = amount - remaining;
		if ( added <= 0 )
		{
			if ( LogInventory )
				Log.Warning( $"[PlayerInventory] {GameObject.Name}: inventory full — could not add {remaining} {resourceId}." );
			return false;
		}

		NotifyInventoryChanged();
		ReportResourcePickedUp( resourceId, added );

		if ( LogInventory )
			Log.Info( $"[PlayerInventory] {GameObject.Name}: +{added} {resourceId}{( remaining > 0 ? $" ({remaining} lost — full)" : "" )}." );

		return remaining <= 0;
	}

	void ReportResourcePickedUp( string resourceId, int amountAdded )
	{
		if ( amountAdded <= 0 )
			return;

		var notice = new ResourcePickupNotice( resourceId, amountAdded );

		if ( IsLocalManagingClient() )
		{
			ResourcePickedUp?.Invoke( notice );
			return;
		}

		if ( GameObject.Network is { Active: true } && Networking.IsHost && GameObject.Network.Owner is not null )
			RpcOwnerResourcePickedUp( resourceId, amountAdded );
	}

	[Rpc.Owner]
	void RpcOwnerResourcePickedUp( string resourceId, int amountAdded )
	{
		if ( amountAdded <= 0 || !IsLocalManagingClient() )
			return;

		ResourcePickedUp?.Invoke( new ResourcePickupNotice( resourceId, amountAdded ) );
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
		var wears = new int[_slots.Length];
		for ( var i = 0; i < _slots.Length; i++ )
		{
			ids[i] = _slots[i].ResourceId ?? string.Empty;
			counts[i] = _slots[i].Count;
			wears[i] = _slots[i].Wear;
		}

		RpcOwnerInventorySync( ids, counts, wears );
	}

	[Rpc.Owner]
	void RpcOwnerInventorySync( string[] resourceIds, int[] counts, int[] wears )
	{
		if ( resourceIds is null || counts is null )
			return;

		EnsureSlotArray();
		var n = Math.Min( _slots.Length, Math.Min( resourceIds.Length, counts.Length ) );
		for ( var i = 0; i < n; i++ )
		{
			var id = resourceIds[i];
			var c = counts[i];
			var w = wears is not null && i < wears.Length ? wears[i] : 0;
			_slots[i] = string.IsNullOrWhiteSpace( id ) || c <= 0
				? InventorySlot.Empty
				: new InventorySlot { ResourceId = id, Count = c, Wear = w };
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

		EnsureSlotArray();
		if ( !InventoryStackRules.PickupAll( _slots, slotIndex, out picked ) )
			return false;

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

		RpcHostPlaceHeld( slotIndex, held.ResourceId, held.Count, held.Wear );
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

		RpcHostSwapDragToSlot( sourceSlotIndex, targetSlotIndex, held.ResourceId, held.Count, held.Wear );
		return ClientTryApplySwapDrag( sourceSlotIndex, targetSlotIndex, ref held );
	}

	/// <summary>Completes a left-drag onto <paramref name="targetSlotIndex"/> (swap when occupied by a different item).</summary>
	public bool OwnerTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryFinishDragDrop( sourceSlotIndex, targetSlotIndex, ref held );

		if ( !TryGetSlotRef( targetSlotIndex, out _ ) )
			return false;

		RpcHostFinishDragDrop( sourceSlotIndex, targetSlotIndex, held.ResourceId, held.Count, held.Wear );
		return ClientTryApplyFinishDragDrop( sourceSlotIndex, targetSlotIndex, ref held );
	}

	public bool HostTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		if ( !InventoryStackRules.FinishDragDrop( _slots, sourceSlotIndex, targetSlotIndex, ref held ) )
			return false;

		NotifyInventoryChanged();
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

			taken = new InventorySlot { ResourceId = slot.ResourceId, Count = 1, Wear = slot.Wear };
			return true;
		}

		if ( !InventoryStackRules.TakeOne( _slots, slotIndex, out taken ) )
			return false;

		NotifyInventoryChanged();
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

		EnsureSlotArray();
		if ( !InventoryStackRules.DropOne( _slots, slotIndex, held, out placedCount ) )
			return false;

		RpcHostDropOne( slotIndex, held.ResourceId, held.Count, held.Wear );
		NotifyInventoryChanged();
		return true;
	}

	public bool OwnerTryTakeHalf( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		if ( !IsLocalManagingClient() || !TryGetSlotRef( slotIndex, out var slot ) || slot.IsEmpty )
			return false;

		var half = HostPeekTakeHalfAmount( slotIndex );
		if ( half <= 0 )
			return false;

		if ( HasHostAuthority )
		{
			if ( !HostTryTakeHalf( slotIndex ) )
				return false;

			taken = new InventorySlot { ResourceId = slot.ResourceId, Count = half, Wear = slot.Wear };
			return true;
		}

		if ( !InventoryStackRules.TakeHalf( _slots, slotIndex, out taken ) )
			return false;

		NotifyInventoryChanged();
		RpcHostTakeHalf( slotIndex );
		return true;
	}

	public bool OwnerTryPlaceHalf( int slotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryPlaceHalf( slotIndex, ref held );

		var heldResourceId = held.ResourceId;
		var heldCount = held.Count;
		var heldWear = held.Wear;

		EnsureSlotArray();
		if ( !InventoryStackRules.PlaceHalf( _slots, slotIndex, ref held ) )
			return false;

		RpcHostPlaceHalf( slotIndex, heldResourceId, heldCount, heldWear );
		NotifyInventoryChanged();
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
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		if ( !InventoryStackRules.PickupAll( _slots, slotIndex, out picked ) )
			return false;

		NotifyInventoryChanged();
		return true;
	}

	public bool HostTryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		if ( !InventoryStackRules.PlaceHeld( _slots, slotIndex, ref held ) )
			return false;

		NotifyInventoryChanged();
		return true;
	}

	/// <summary>Drag-drop swap between slots (source was emptied when the drag started).</summary>
	public bool HostTrySwapDragToSlot( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		if ( !InventoryStackRules.SwapDragToSlot( _slots, sourceSlotIndex, targetSlotIndex, ref held ) )
			return false;

		NotifyInventoryChanged();
		return true;
	}

	public bool HostTryTakeOne( int slotIndex )
	{
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		if ( !InventoryStackRules.TakeOne( _slots, slotIndex, out _ ) )
			return false;

		NotifyInventoryChanged();
		return true;
	}

	public bool HostTryDropOne( int slotIndex, in InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		if ( !InventoryStackRules.DropOne( _slots, slotIndex, held, out _ ) )
			return false;

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
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		if ( !InventoryStackRules.TakeHalf( _slots, slotIndex, out _ ) )
			return false;

		NotifyInventoryChanged();
		return true;
	}

	public int HostPeekPlaceHalfAmount( in InventoryCursorStack held ) =>
		held.IsEmpty ? 0 : held.Count / 2;

	public bool HostTryPlaceHalf( int slotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		if ( !InventoryStackRules.PlaceHalf( _slots, slotIndex, ref held ) )
			return false;

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
			var add = ResourceCatalog.ClampAddToStack( from.ResourceId, to.Count, from.Count );
			if ( add <= 0 )
				return false;

			to.Count += add;
			from.Count -= add;
			if ( from.Count <= 0 )
				from = InventorySlot.Empty;

			NotifyInventoryChanged();
			return true;
		}

		return false;
	}

	public bool HostTryReturnStack( in InventoryCursorStack held )
	{
		var copy = held;
		return HostTryAbsorbCursorStackIntoBag( ref copy );
	}

	/// <summary>Merges held stack into bag slots (matching stacks, then empties). Returns true if fully absorbed.</summary>
	public bool OwnerTryAbsorbCursorStackIntoBag( ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryAbsorbCursorStackIntoBag( ref held );

		RpcHostAbsorbCursorStackIntoBag( held.ResourceId, held.Count, held.Wear );
		return ClientTryApplyAbsorbCursorStackIntoBag( ref held );
	}

	public bool HostTryAbsorbCursorStackIntoBag( ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !HasHostAuthority )
			return false;

		AbsorbCursorStackIntoBagSlots( ref held, notify: true );
		return held.IsEmpty;
	}

	bool ClientTryApplyAbsorbCursorStackIntoBag( ref InventoryCursorStack held )
	{
		if ( HasHostAuthority )
			return HostTryAbsorbCursorStackIntoBag( ref held );

		AbsorbCursorStackIntoBagSlots( ref held, notify: true );
		return held.IsEmpty;
	}

	bool AbsorbCursorStackIntoBagSlots( ref InventoryCursorStack held, bool notify )
	{
		EnsureSlotArray();
		var changed = InventoryStackRules.AbsorbStack( _slots, ref held );

		if ( changed && notify )
			NotifyInventoryChanged();

		return changed;
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

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		EnsureSlotArray();

		for ( var i = 0; i < _slots.Length; i++ )
		{
			if ( _slots[i].IsEmpty )
				continue;

			if ( !ResourceCatalog.ResourceIdsMatch( _slots[i].ResourceId, resourceId ) )
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
		if ( HasHostAuthority )
			return HostTryPlaceHeld( slotIndex, ref held );

		EnsureSlotArray();
		if ( !InventoryStackRules.PlaceHeld( _slots, slotIndex, ref held ) )
			return false;

		NotifyInventoryChanged();
		return true;
	}

	bool ClientTryApplySwapDrag( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( HasHostAuthority )
			return HostTrySwapDragToSlot( sourceSlotIndex, targetSlotIndex, ref held );

		EnsureSlotArray();
		if ( !InventoryStackRules.SwapDragToSlot( _slots, sourceSlotIndex, targetSlotIndex, ref held ) )
			return false;

		NotifyInventoryChanged();
		return true;
	}

	bool ClientTryApplyFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( HasHostAuthority )
			return HostTryFinishDragDrop( sourceSlotIndex, targetSlotIndex, ref held );

		EnsureSlotArray();
		if ( !InventoryStackRules.FinishDragDrop( _slots, sourceSlotIndex, targetSlotIndex, ref held ) )
			return false;

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
	void RpcHostAbsorbCursorStackIntoBag( string resourceId, int count, int wear )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( resourceId, count, wear );
		HostTryAbsorbCursorStackIntoBag( ref held );

		if ( !held.IsEmpty )
			HeldStackWorldDrop.TryDrop( GameObject, ref held );
	}

	[Rpc.Host]
	void RpcHostPlaceHeld( int slotIndex, string resourceId, int count, int wear )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( resourceId, count, wear );
		HostTryPlaceHeld( slotIndex, ref held );
	}

	[Rpc.Host]
	void RpcHostSwapDragToSlot( int sourceSlotIndex, int targetSlotIndex, string resourceId, int count, int wear )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( resourceId, count, wear );
		HostTrySwapDragToSlot( sourceSlotIndex, targetSlotIndex, ref held );
	}

	[Rpc.Host]
	void RpcHostFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, string resourceId, int count, int wear )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( resourceId, count, wear );
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
	void RpcHostDropOne( int slotIndex, string heldResourceId, int heldCount, int heldWear )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( heldResourceId, heldCount, heldWear );
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
	void RpcHostPlaceHalf( int slotIndex, string heldResourceId, int heldCount, int heldWear )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( heldResourceId, heldCount, heldWear );
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

	/// <summary>Host: repair one bag slot back to full durability.</summary>
	public bool HostClearWear( int slotIndex )
	{
		if ( !HasHostAuthority )
			return false;

		EnsureSlotArray();
		if ( slotIndex < 0 || slotIndex >= _slots.Length )
			return false;

		ref var slot = ref _slots[slotIndex];
		if ( slot.IsEmpty || slot.Wear <= 0 )
			return false;

		slot.Wear = 0;
		NotifyInventoryChanged();
		return true;
	}

	/// <summary>Local player requests a craft (same RPC path as other inventory host actions).</summary>
	public bool OwnerTryCraftRecipe( string recipeId )
	{
		if ( string.IsNullOrWhiteSpace( recipeId ) || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return TryCraftRecipeOnHost( recipeId );

		RpcHostCraftRecipe( recipeId );
		return true;
	}

	bool TryCraftRecipeOnHost( string recipeId )
	{
		var crafting = Components.Get<PlayerCrafting>();
		return crafting is not null && crafting.HostTryCraft( recipeId );
	}

	[Rpc.Host]
	void RpcHostCraftRecipe( string recipeId )
	{
		if ( !Networking.IsHost )
			return;

		TryCraftRecipeOnHost( recipeId );
	}
}
