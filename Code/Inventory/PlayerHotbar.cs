using System;
using Sandbox;

namespace Survival;

/// <summary>10-slot hotbar storage (host-authoritative). Client bindings steer auto-slot on pickup.</summary>
[Title( "Player Hotbar" )]
public sealed class PlayerHotbar : Component
{
	/// <summary>Slots per bank (the number row 1-0). Two banks: Alt swaps which one the HUD shows.</summary>
	public const int BankSize = 10;
	public const int BankCount = 2;
	public const int SlotCount = BankSize * BankCount;

	public event Action HotbarChanged;
	public event Action<int> ActiveSlotChanged;
	public event Action<int> ActiveBankChanged;

	/// <summary>The last selected slot (seeds / food "selected on the hotbar"). May sit in the hidden bank; -1 after a toggle-off.</summary>
	[Sync] public int ActiveSlotIndex { get; private set; }

	/// <summary>Bank the HUD shows (0 or 1). Alt toggles it; selection keys address this bank.</summary>
	[Sync] public int ActiveBank { get; private set; }

	/// <summary>
	/// Slot that feeds MainHand: the last selected slot that was NOT an offhand item (an empty or non-weapon
	/// slot clears the weapon, as a single selection always did). -1 = toggled off (bare hand). The HUD draws
	/// the gold square on this slot and on <see cref="OffHandSlotIndex"/>.
	/// </summary>
	[Sync] public int PrimarySlotIndex { get; private set; }

	/// <summary>Slot that feeds OffHand: the last selected slot holding a shield. -1 = no shield selected.</summary>
	[Sync] public int OffHandSlotIndex { get; private set; } = -1;

	readonly InventorySlot[] _slots = new InventorySlot[SlotCount];
	string[] _bindings = PlayerHotbarBindingsStore.CreateEmpty();
	bool _pushedBindingsToHost;

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
		{
			_bindings = PlayerHotbarBindingsStore.Load();
			TryPushBindingsToHost();
		}

		HotbarChanged?.Invoke();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		// Owner may not be networked on the first OnStart frame after spawn.
		if ( !_pushedBindingsToHost && IsLocalManagingClient() )
			TryPushBindingsToHost();
	}

	/// <summary>
	/// Owning client → host: bindings steer host craft/pickup into ghost hotbar slots.
	/// Without this, the host only sees empty bindings and dumps crafts into the bag.
	/// </summary>
	void TryPushBindingsToHost()
	{
		if ( _pushedBindingsToHost )
			return;

		if ( GameObject.Network is not { Active: true } )
			return;

		if ( Networking.IsHost )
		{
			_pushedBindingsToHost = true;
			return;
		}

		if ( !IsLocalManagingClient() )
			return;

		_pushedBindingsToHost = true;
		RpcHostSetBindings( _bindings );
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
		var handChanged = ClassifySelection( index );
		if ( ActiveSlotIndex == index )
		{
			if ( handChanged )
				ActiveSlotChanged?.Invoke( index );
			return;
		}

		ActiveSlotIndex = index;
		ActiveSlotChanged?.Invoke( index );
	}

	/// <summary>
	/// Key / click on a slot: selecting a slot that is already the primary or the offhand selection UNEQUIPS it
	/// (Mark 2026-11-02: press the slot's button again and the gold square goes away); anything else selects.
	/// </summary>
	public void SelectOrToggleSlot( int index )
	{
		index = WrapSlotIndex( index );
		var offhand = IsOffHandSlot( index );
		var selected = offhand ? OffHandSlotIndex == index : PrimarySlotIndex == index;
		if ( !selected )
		{
			SetActiveSlot( index );
			return;
		}

		if ( offhand )
			OffHandSlotIndex = -1;
		else
			PrimarySlotIndex = -1;

		if ( ActiveSlotIndex == index )
			ActiveSlotIndex = -1;

		ActiveSlotChanged?.Invoke( ActiveSlotIndex );
	}

	bool IsOffHandSlot( int index )
	{
		var slot = GetSlot( index );
		return !slot.IsEmpty && EquipmentCatalog.IsHotbarOffHandResource( slot.ResourceId );
	}

	/// <summary>
	/// A selected shield goes to the offhand and leaves the primary alone; anything else (weapon, tool, seed,
	/// empty) becomes the primary selection. Returns true when either hand index moved.
	/// </summary>
	bool ClassifySelection( int index )
	{
		if ( IsOffHandSlot( index ) )
		{
			if ( OffHandSlotIndex == index )
				return false;

			OffHandSlotIndex = index;
			return true;
		}

		if ( PrimarySlotIndex == index )
			return false;

		PrimarySlotIndex = index;
		return true;
	}

	/// <summary>Wheel / mouse4-5: step within the bank on screen. A selection sitting in the other bank enters this one from the edge.</summary>
	public void StepActiveSlot( int delta )
	{
		if ( delta == 0 )
			return;

		var bank = ActiveBank;
		var inBank = ActiveSlotIndex >= 0 && BankOf( ActiveSlotIndex ) == bank
			? IndexInBank( ActiveSlotIndex ) + delta
			: (delta > 0 ? 0 : BankSize - 1);
		inBank = ((inBank % BankSize) + BankSize) % BankSize;
		SetActiveSlot( bank * BankSize + inBank );
	}

	/// <summary>Number row: slot <paramref name="indexInBank"/> (0-9) of the bank on screen.</summary>
	public void SelectBankSlot( int indexInBank )
	{
		if ( indexInBank < 0 || indexInBank >= BankSize )
			return;

		SetActiveSlot( ActiveBank * BankSize + indexInBank );
	}

	/// <summary>Alt: show the other bank. Selection and equipped hands are untouched.</summary>
	public void ToggleBank()
	{
		ActiveBank = (ActiveBank + 1) % BankCount;
		ActiveBankChanged?.Invoke( ActiveBank );
	}

	public static int BankOf( int slotIndex ) => WrapSlotIndex( slotIndex ) / BankSize;

	public static int IndexInBank( int slotIndex ) => WrapSlotIndex( slotIndex ) % BankSize;

	public static int WrapSlotIndex( int index )
	{
		var wrapped = index % SlotCount;
		return wrapped < 0 ? wrapped + SlotCount : wrapped;
	}

	/// <summary>Count of a resource in hotbar item stacks (not bindings/ghosts).</summary>
	public int CountResource( string resourceId ) =>
		InventoryStackRules.CountResource( _slots, resourceId );

	/// <summary>Host: pickup into hotbar stacks, then empty slots with a matching binding ghost.</summary>
	public int TryAddResourcePickup( string resourceId, int amount, int wear = 0, string crafterName = null, int tier = 0 )
	{
		if ( amount <= 0 || string.IsNullOrWhiteSpace( resourceId ) || !HasHostAuthority )
			return amount;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var remaining = TryAddToMatchingStacks( resourceId, amount );
		return TryAddToBindingSlots( resourceId, remaining, wear, crafterName, tier );
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

	int TryAddToBindingSlots( string resourceId, int amount, int wear = 0, string crafterName = null, int tier = 0 )
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

			remaining = TryAddToSlot( i, resourceId, remaining, rememberSlot: true, wear: wear, crafterName: crafterName, tier: tier );
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
	public int TryAddResourceOverflow( string resourceId, int amount, int wear = 0 )
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

				remaining = TryAddToSlot( i, resourceId, remaining, rememberSlot: pass != 1, wear: wear );
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

	int TryAddToSlot( int slotIndex, string resourceId, int amount, bool rememberSlot = false, int wear = 0, string crafterName = null, int tier = 0 )
	{
		if ( amount <= 0 )
			return 0;

		var maxStack = ResourceCatalog.GetMaxStack( resourceId );
		ref var slot = ref _slots[slotIndex];

		if ( slot.IsEmpty )
		{
			var place = Math.Min( amount, maxStack );
			slot = new InventorySlot { ResourceId = resourceId, Count = place, Wear = wear, CrafterName = crafterName, Tier = tier };
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

		CommitBindings();
	}

	public void SetBinding( int slotIndex, string resourceId ) => RememberResourceSlot( slotIndex, resourceId );

	public void ClearBinding( int slotIndex )
	{
		if ( slotIndex < 0 || slotIndex >= SlotCount || string.IsNullOrWhiteSpace( _bindings[slotIndex] ) )
			return;

		_bindings[slotIndex] = string.Empty;
		CommitBindings();
	}

	/// <summary>
	/// Persist locally, keep host authority in sync (for craft/pickup), and mirror to the owning client when host mutates.
	/// </summary>
	void CommitBindings()
	{
		PersistBindingsLocal();
		HotbarChanged?.Invoke();

		if ( GameObject.Network is not { Active: true } )
			return;

		if ( Networking.IsHost )
		{
			// Host is the placement authority; push so the owner UI/ghosts stay aligned.
			if ( GameObject.Network.Owner is { } owner
			     && !ConnectionIdentity.SameClient( owner, Connection.Local ) )
				RpcOwnerApplyBindings( _bindings );
			return;
		}

		if ( IsLocalManagingClient() )
			RpcHostSetBindings( _bindings );
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

	[Rpc.Host]
	void RpcHostSetBindings( string[] bindings )
	{
		if ( !Networking.IsHost )
			return;

		if ( Rpc.Caller is { } caller
		     && GameObject.Network is { Active: true, Owner: { } owner }
		     && caller.Id != owner.Id )
		{
			Log.Warning( $"[PlayerHotbar] RpcHostSetBindings ignored: caller ≠ owner." );
			return;
		}

		if ( !TryApplyBindings( bindings ) )
			return;

		HotbarChanged?.Invoke();
	}

	[Rpc.Owner]
	void RpcOwnerApplyBindings( string[] bindings )
	{
		if ( !TryApplyBindings( bindings ) )
			return;

		PersistBindingsLocal();
		HotbarChanged?.Invoke();
	}

	bool TryApplyBindings( string[] bindings )
	{
		if ( bindings is null || bindings.Length != SlotCount )
			return false;

		_bindings = bindings;
		return true;
	}

	public bool OwnerTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryPickupAll( slotIndex, out picked );

		if ( !InventoryStackRules.PickupAll( _slots, slotIndex, out picked ) )
			return false;

		ClearBindingIfSlotEmpty( slotIndex );
		NotifyChanged();
		RpcHostPickupAll( slotIndex );
		return true;
	}

	public bool HostTryPickupAll( int slotIndex, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( !HasHostAuthority || !InventoryStackRules.PickupAll( _slots, slotIndex, out picked ) )
			return false;

		ClearBindingIfSlotEmpty( slotIndex );
		NotifyChanged();
		return true;
	}

	public bool OwnerTryTakeOne( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		if ( !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
		{
			var slot = GetSlot( slotIndex );
			if ( slot.IsEmpty || !HostTryTakeOne( slotIndex ) )
				return false;

			taken = new InventorySlot { ResourceId = slot.ResourceId, Count = 1, Wear = slot.Wear, CrafterName = slot.CrafterName, Tier = slot.Tier };
			return true;
		}

		if ( !InventoryStackRules.TakeOne( _slots, slotIndex, out taken ) )
			return false;

		ClearBindingIfSlotEmpty( slotIndex );
		NotifyChanged();
		RpcHostTakeOne( slotIndex );
		return true;
	}

	public bool OwnerTryDropOne( int slotIndex, in InventoryCursorStack held, out int placedCount )
	{
		placedCount = 0;
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
		{
			if ( !HostTryDropOne( slotIndex, held ) )
				return false;

			placedCount = 1;
			return true;
		}

		if ( !InventoryStackRules.DropOne( _slots, slotIndex, held, out placedCount ) )
			return false;

		RpcHostDropOne( slotIndex, held.ResourceId, held.Count, held.Wear, held.CrafterName ?? string.Empty, held.Tier );
		UpdateBindingFromSlot( slotIndex );
		NotifyChanged();
		return true;
	}

	public bool OwnerTryTakeHalf( int slotIndex, out InventorySlot taken )
	{
		taken = InventorySlot.Empty;
		if ( !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
		{
			var slot = GetSlot( slotIndex );
			var half = slot.Count / 2;
			if ( half <= 0 || !HostTryTakeHalf( slotIndex ) )
				return false;

			taken = new InventorySlot { ResourceId = slot.ResourceId, Count = half, Wear = slot.Wear, CrafterName = slot.CrafterName, Tier = slot.Tier };
			return true;
		}

		if ( !InventoryStackRules.TakeHalf( _slots, slotIndex, out taken ) )
			return false;

		ClearBindingIfSlotEmpty( slotIndex );
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

		var heldResourceId = held.ResourceId;
		var heldCount = held.Count;
		var heldWear = held.Wear;
		var heldCrafter = held.CrafterName ?? string.Empty;

		if ( !InventoryStackRules.PlaceHalf( _slots, slotIndex, ref held ) )
			return false;

		RpcHostPlaceHalf( slotIndex, heldResourceId, heldCount, heldWear, heldCrafter, heldTier );
		UpdateBindingFromSlot( slotIndex );
		NotifyChanged();
		return true;
	}

	public bool HostTryTakeOne( int slotIndex )
	{
		if ( !HasHostAuthority || !InventoryStackRules.TakeOne( _slots, slotIndex, out _ ) )
		var heldTier = held.Tier;
			return false;

		ClearBindingIfSlotEmpty( slotIndex );
		NotifyChanged();
		return true;
	}

	public bool HostTryDropOne( int slotIndex, in InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		var wasEmpty = GetSlot( slotIndex ).IsEmpty;
		if ( !InventoryStackRules.DropOne( _slots, slotIndex, held, out _ ) )
			return false;

		if ( wasEmpty )
			UpdateBindingFromSlot( slotIndex );

		NotifyChanged();
		return true;
	}

	public bool HostTryTakeHalf( int slotIndex )
	{
		if ( !HasHostAuthority || !InventoryStackRules.TakeHalf( _slots, slotIndex, out _ ) )
			return false;

		ClearBindingIfSlotEmpty( slotIndex );
		NotifyChanged();
		return true;
	}

	public bool HostTryPlaceHalf( int slotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		var wasEmpty = GetSlot( slotIndex ).IsEmpty;
		if ( !InventoryStackRules.PlaceHalf( _slots, slotIndex, ref held ) )
			return false;

		if ( wasEmpty )
			UpdateBindingFromSlot( slotIndex );

		NotifyChanged();
		return true;
	}

	public bool OwnerTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryFinishDragDrop( sourceSlotIndex, targetSlotIndex, ref held );

		var heldResourceId = held.ResourceId;
		var heldCount = held.Count;
		var heldWear = held.Wear;
		var heldCrafter = held.CrafterName ?? string.Empty;
		var sourceBefore = GetSlot( sourceSlotIndex );

		if ( !InventoryStackRules.FinishDragDrop( _slots, sourceSlotIndex, targetSlotIndex, ref held ) )
			return false;

		RpcHostFinishDragDrop( sourceSlotIndex, targetSlotIndex, heldResourceId, heldCount, heldWear, heldCrafter, heldTier );
		UpdateBindingFromSlot( targetSlotIndex );
		if ( SlotChangedFrom( sourceSlotIndex, sourceBefore ) )
			UpdateBindingFromSlot( sourceSlotIndex );

		NotifyChanged();
		return true;
	}
		var heldTier = held.Tier;

	public bool HostTryFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		var sourceBefore = GetSlot( sourceSlotIndex );
		if ( !InventoryStackRules.FinishDragDrop( _slots, sourceSlotIndex, targetSlotIndex, ref held ) )
			return false;

		UpdateBindingFromSlot( targetSlotIndex );
		if ( SlotChangedFrom( sourceSlotIndex, sourceBefore ) )
			UpdateBindingFromSlot( sourceSlotIndex );

		NotifyChanged();
		return true;
	}

	public bool HostTryPlaceHeld( int slotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority || !InventoryStackRules.PlaceHeld( _slots, slotIndex, ref held ) )
			return false;

		UpdateBindingFromSlot( slotIndex );
		NotifyChanged();
		return true;
	}

	public bool HostTrySwapDragToSlot( int sourceSlotIndex, int targetSlotIndex, ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority || !InventoryStackRules.SwapDragToSlot( _slots, sourceSlotIndex, targetSlotIndex, ref held ) )
			return false;

		UpdateBindingFromSlot( targetSlotIndex );
		UpdateBindingFromSlot( sourceSlotIndex );
		NotifyChanged();
		return true;
	}

	/// <summary>Host: absorb a cursor stack into matching stacks, then empties. Returns true when fully absorbed.</summary>
	public bool HostTryReturnStack( ref InventoryCursorStack held )
	{
		if ( !HasHostAuthority )
			return false;

		Span<bool> wasEmpty = stackalloc bool[SlotCount];
		for ( var i = 0; i < SlotCount; i++ )
			wasEmpty[i] = _slots[i].IsEmpty;

		if ( !InventoryStackRules.AbsorbStack( _slots, ref held ) )
			return false;

		for ( var i = 0; i < SlotCount; i++ )
		{
			if ( wasEmpty[i] && !_slots[i].IsEmpty )
				UpdateBindingFromSlot( i );
		}

		NotifyChanged();
		return held.IsEmpty;
	}

	/// <summary>Whether a slot differs from a snapshot (used to re-bind the source slot after a drag swap).</summary>
	bool SlotChangedFrom( int slotIndex, in InventorySlot before )
	{
		if ( slotIndex < 0 || slotIndex >= SlotCount )
			return false;

		var now = _slots[slotIndex];
		return now.Count != before.Count
		       || !string.Equals( now.ResourceId ?? string.Empty, before.ResourceId ?? string.Empty, StringComparison.OrdinalIgnoreCase );
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
		var wears = new int[SlotCount];
		var crafters = new string[SlotCount];
		for ( var i = 0; i < SlotCount; i++ )
		{
			ids[i] = _slots[i].ResourceId ?? string.Empty;
			counts[i] = _slots[i].Count;
			wears[i] = _slots[i].Wear;
			crafters[i] = _slots[i].CrafterName ?? string.Empty;
		}

		RpcOwnerHotbarSync( ids, counts, wears, crafters, tiers, ActiveSlotIndex, PrimarySlotIndex, OffHandSlotIndex );
	}

	[Rpc.Owner]
	void RpcOwnerHotbarSync( string[] resourceIds, int[] counts, int[] wears, string[] crafters, int[] tiers, int activeSlot, int primarySlot, int offHandSlot )
		var tiers = new int[SlotCount];
	{
		if ( resourceIds is null || counts is null )
			return;

		var n = Math.Min( SlotCount, Math.Min( resourceIds.Length, counts.Length ) );
		for ( var i = 0; i < n; i++ )
			tiers[i] = _slots[i].Tier;
		{
			var id = resourceIds[i];
			var c = counts[i];
			var w = wears is not null && i < wears.Length ? wears[i] : 0;
			var maker = crafters is not null && i < crafters.Length ? crafters[i] : null;
			_slots[i] = string.IsNullOrWhiteSpace( id ) || c <= 0
				? InventorySlot.Empty
				: new InventorySlot { ResourceId = id, Count = c, Wear = w, CrafterName = maker, Tier = tier };
		}

		ActiveSlotIndex = activeSlot < 0 ? -1 : WrapSlotIndex( activeSlot );
		PrimarySlotIndex = primarySlot < 0 ? -1 : WrapSlotIndex( primarySlot );
		OffHandSlotIndex = offHandSlot < 0 ? -1 : WrapSlotIndex( offHandSlot );
		HotbarChanged?.Invoke();
	}

	[Rpc.Host]
	void RpcHostPickupAll( int slotIndex )
			var tier = tiers is not null && i < tiers.Length ? tiers[i] : 0;
	{
		if ( !Networking.IsHost )
			return;

		HostTryPickupAll( slotIndex, out _ );
	}

	[Rpc.Host]
	void RpcHostFinishDragDrop( int sourceSlotIndex, int targetSlotIndex, string resourceId, int count, int wear, string crafter, int tier )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( resourceId, count, wear, crafter, tier );
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
	void RpcHostDropOne( int slotIndex, string heldResourceId, int heldCount, int heldWear, string heldCrafter, int heldTier )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( heldResourceId, heldCount, heldWear, heldCrafter, heldTier );
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
	void RpcHostPlaceHalf( int slotIndex, string heldResourceId, int heldCount, int heldWear, string heldCrafter, int heldTier )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( heldResourceId, heldCount, heldWear, heldCrafter, heldTier );
		HostTryPlaceHalf( slotIndex, ref held );
	}

	/// <summary>Host: add durability wear to one hotbar slot (clamped to the item's max). Returns new wear.</summary>
	public int HostAddWearToSlot( int slotIndex, int amount = 1 )
	{
		if ( !HasHostAuthority || amount <= 0 || slotIndex < 0 || slotIndex >= SlotCount )
			return 0;

		ref var slot = ref _slots[slotIndex];
		if ( slot.IsEmpty || !ToolDurability.HasDurability( slot.ResourceId ) )
			return 0;

		var next = ToolDurability.ClampWear( slot, slot.Wear + amount );
		if ( next == slot.Wear )
			return slot.Wear;

		slot.Wear = next;
		NotifyChanged();
		return next;
	}

	/// <summary>Host: repair one hotbar slot back to full durability.</summary>
	public bool HostClearWear( int slotIndex )
	{
		if ( !HasHostAuthority || slotIndex < 0 || slotIndex >= SlotCount )
			return false;

		ref var slot = ref _slots[slotIndex];
		if ( slot.IsEmpty || slot.Wear <= 0 )
			return false;

		slot.Wear = 0;
		NotifyChanged();
		return true;
	/// <summary>Host: set the workbench upgrade tier of one hotbar stack (<see cref="EquipmentUpgrade"/>).</summary>
	public bool HostSetTier( int slotIndex, int tier )
	{
		if ( !HasHostAuthority || slotIndex < 0 || slotIndex >= SlotCount )
			return false;

		ref var slot = ref _slots[slotIndex];
		tier = EquipmentUpgrade.NormalizeTier( tier );
		if ( slot.IsEmpty || !EquipmentUpgrade.IsUpgradable( slot ) || slot.Tier == tier )
			return false;

		slot.Tier = tier;
		NotifyChanged();
		return true;
	}

	}
}
