using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Player-owned augment state: the crafted bank, the 18 body sockets and the enhancement level of
/// each body part.
/// <para>
/// <b>Enhance</b> (augment cores) opens sockets one at a time per body part. <b>Placing</b> an augment
/// on the paper doll is free and only a plan — the socket shows as pending. <b>Augment</b> (gold coins)
/// commits every pending socket at once; only a socket whose placed augment matches its committed id
/// is active (grants its ability). Pulling an augment out stops its effect immediately and costs
/// nothing. Crafting at the station spends bag materials and drops the augment into the bank.
/// </para>
/// </summary>
[Title( "Player Augments" )]
public sealed partial class PlayerAugments : Component
{
	public const int BankSlotCount = InventoryDefaults.DefaultSlotCount;
	/// <summary>Bank is a wide strip under the paper doll (two rows).</summary>
	public const int BankColumns = 8;

	public event Action AugmentsChanged;

	/// <summary>
	/// When true, installed + bank + bag augments follow death-loot rules like resources.
	/// Standard difficulty keeps them (false).
	/// </summary>
	[Property, Group( "Death" ), Title( "Drop augments on death" )]
	public bool DropAugmentsOnDeath { get; set; }

	/// <summary>
	/// Owner's <see cref="GameHacks.FreeAugments"/> mirrored onto the pawn so the host honours it:
	/// the Augment button charges 0 gold while set.
	/// </summary>
	[Sync] public bool FreeAugmentsHack { get; private set; }

	/// <summary>Owner mirrors the console flag onto the synced pawn state (cheap compare, writes only on change).</summary>
	void PushHackFlags()
	{
		if ( GameObject.Network is { Active: true } net && !net.IsOwner )
			return;

		if ( FreeAugmentsHack != GameHacks.FreeAugments )
			FreeAugmentsHack = GameHacks.FreeAugments;
	}

	readonly InventorySlot[] _installed = new InventorySlot[AugmentSlots.Count];
	readonly string[] _committed = new string[AugmentSlots.Count];
	readonly InventorySlot[] _bank = new InventorySlot[BankSlotCount];
	readonly int[] _unlocked = new int[AugmentBodyParts.Count];

	PlayerInventory _inventory;

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	/// <summary>Bumps when bank, sockets, commits or enhancements change (UI refresh).</summary>
	public int ContentsVersion { get; private set; }

	protected override void OnStart()
	{
		base.OnStart();
		_inventory = Components.Get<PlayerInventory>();
		AugmentCatalog.EnsureLoaded();
	}

	public bool IsLocalManagingClient()
	{
		if ( GameObject.Network is not { Active: true } )
			return true;

		if ( GameObject.Network.Owner is not { } owner )
			return Networking.IsHost;

		return ConnectionIdentity.SameClient( owner, Connection.Local );
	}

	// ── Read ────────────────────────────────────────────────────────────────────────────────

	public InventorySlot GetInstalled( AugmentSlot slot )
	{
		var i = (int)slot;
		if ( i < 0 || i >= AugmentSlots.Count )
			return InventorySlot.Empty;

		return _installed[i];
	}

	public InventorySlot GetBankSlot( int index )
	{
		if ( index < 0 || index >= BankSlotCount )
			return InventorySlot.Empty;

		return _bank[index];
	}

	public int GetUnlockedCount( AugmentBodyPart part )
	{
		var p = (int)part;
		return p < 0 || p >= AugmentBodyParts.Count ? 0 : _unlocked[p];
	}

	public bool IsSlotUnlocked( AugmentSlot slot )
	{
		var i = (int)slot;
		if ( i < 0 || i >= AugmentSlots.Count )
			return false;

		return AugmentSlots.IndexInPart( slot ) < GetUnlockedCount( AugmentSlots.PartOf( slot ) );
	}

	/// <summary>Placed augment matches the paid-for id → its ability is live.</summary>
	public bool IsSlotActive( AugmentSlot slot )
	{
		var i = (int)slot;
		if ( i < 0 || i >= AugmentSlots.Count )
			return false;

		var stack = _installed[i];
		return !stack.IsEmpty && ResourceCatalog.ResourceIdsMatch( stack.ResourceId, _committed[i] );
	}

	/// <summary>Placed but not yet paid for — the Augment button is what turns it on.</summary>
	public bool IsSlotPending( AugmentSlot slot )
	{
		var i = (int)slot;
		if ( i < 0 || i >= AugmentSlots.Count )
			return false;

		return !_installed[i].IsEmpty && !IsSlotActive( slot );
	}

	public bool HasPendingInstalls()
	{
		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			if ( IsSlotPending( (AugmentSlot)i ) )
				return true;
		}

		return false;
	}

	/// <summary>Gold the Augment button will charge right now (sum of every pending socket's install cost; 0 under the freeAugments hack).</summary>
	public int ComputePendingGoldCost()
	{
		if ( FreeAugmentsHack )
			return 0;

		var total = 0;
		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			var slot = (AugmentSlot)i;
			if ( !IsSlotPending( slot ) )
				continue;

			if ( AugmentCatalog.TryGet( _installed[i].ResourceId, out var def ) )
				total += def.InstallGoldCost( slot );
		}

		return total;
	}

	/// <summary>Cores the enhance button of this part costs next (0 = all three sockets open).</summary>
	public int GetNextEnhanceCoreCost( AugmentBodyPart part ) =>
		AugmentBodyParts.NextSlotCoreCost( part, GetUnlockedCount( part ) );

	public int CountCores() => ResolveInventory()?.CountResource( AugmentCurrency.CoreResourceId ) ?? 0;

	public int CountGold() => ResolveInventory()?.CountResource( AugmentCurrency.GoldResourceId ) ?? 0;

	public bool CanEnhance( AugmentBodyPart part )
	{
		var cost = GetNextEnhanceCoreCost( part );
		return cost > 0 && CountCores() >= cost;
	}

	public bool CanCommitAugments()
	{
		if ( !HasPendingInstalls() )
			return false;

		return CountGold() >= ComputePendingGoldCost();
	}

	/// <summary>True when <paramref name="augmentId"/> sits in an <b>active</b> (paid-for) socket.</summary>
	public bool IsInstalledActive( string augmentId )
	{
		if ( string.IsNullOrWhiteSpace( augmentId ) )
			return false;

		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			if ( IsSlotActive( (AugmentSlot)i ) && ResourceCatalog.ResourceIdsMatch( _installed[i].ResourceId, augmentId ) )
				return true;
		}

		return false;
	}

	/// <summary>First <b>active</b> socket whose augment grants <paramref name="ability"/> (toggle state ignored — see <see cref="IsAbilityOn"/>).</summary>
	public bool TryGetActiveDefinition( AugmentAbility ability, out AugmentDefinition definition )
	{
		definition = null;
		if ( ability == AugmentAbility.None )
			return false;

		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			if ( !IsSlotActive( (AugmentSlot)i ) )
				continue;

			if ( !AugmentCatalog.TryGet( _installed[i].ResourceId, out var def ) || def.ResolvedAbility != ability )
				continue;

			definition = def;
			return true;
		}

		return false;
	}

	// ── Craft (bag materials → bank) ────────────────────────────────────────────────────────

	public bool OwnerTryCraft( string augmentId )
	{
		if ( !IsLocalManagingClient() || string.IsNullOrWhiteSpace( augmentId ) )
			return false;

		if ( HasHostAuthority )
			return HostTryCraft( augmentId );

		RpcHostCraftAugment( augmentId );
		return true;
	}

	bool HostTryCraft( string augmentId )
	{
		if ( !HasHostAuthority )
			return false;

		var inventory = ResolveInventory();
		if ( inventory is null )
			return false;

		AugmentCatalog.EnsureLoaded();
		if ( !AugmentCatalog.TryGet( augmentId, out var def ) || !def.IsUnlockedByDefault )
			return false;

		// allCrafting hack (owner's flag mirrored onto the pawn): materials are waived, same as bench recipes.
		var free = Components.Get<PlayerCrafting>() is { AllCraftingHack: true };
		var hasCost = def.Ingredients is { Count: > 0 };
		if ( !free && !hasCost )
			return false;

		if ( !free && !inventory.HasResources( def.Ingredients ) )
			return false;

		if ( !HostCanFitBank( def.Id, 1 ) )
			return false;

		if ( !free && !inventory.HostTryConsumeResources( def.Ingredients ) )
			return false;

		if ( !HostTryAddToBank( def.Id, 1 ) )
			return false;

		NotifyChanged();
		return true;
	}

	public bool CanCraft( string augmentId )
	{
		var inventory = ResolveInventory();
		if ( inventory is null || !AugmentCatalog.TryGet( augmentId, out var def ) || !def.IsUnlockedByDefault )
			return false;

		if ( !HostCanFitBank( def.Id, 1 ) )
			return false;

		if ( GameHacks.AllCrafting )
			return true;

		return def.Ingredients is { Count: > 0 } && inventory.HasResources( def.Ingredients );
	}

	// ── Enhance (augment cores → open a socket) ─────────────────────────────────────────────

	public bool OwnerTryEnhance( AugmentBodyPart part )
	{
		if ( !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryEnhance( part );

		RpcHostEnhance( (int)part );
		return true;
	}

	bool HostTryEnhance( AugmentBodyPart part )
	{
		if ( !HasHostAuthority )
			return false;

		var p = (int)part;
		if ( p < 0 || p >= AugmentBodyParts.Count )
			return false;

		var cost = AugmentBodyParts.NextSlotCoreCost( part, _unlocked[p] );
		if ( cost <= 0 )
			return false;

		var inventory = ResolveInventory();
		if ( inventory is null )
			return false;

		var price = new List<CraftingIngredient>
		{
			new() { ResourceId = AugmentCurrency.CoreResourceId, Amount = cost },
		};

		if ( !inventory.HasResources( price ) || !inventory.HostTryConsumeResources( price ) )
			return false;

		_unlocked[p] = Math.Min( AugmentBodyParts.SlotsPerPart, _unlocked[p] + 1 );
		NotifyChanged();
		return true;
	}

	// ── Augment (gold coins → commit every pending socket) ──────────────────────────────────

	public bool OwnerTryCommitAugments()
	{
		if ( !IsLocalManagingClient() )
			return false;

		if ( HasHostAuthority )
			return HostTryCommitAugments();

		RpcHostCommitAugments();
		return true;
	}

	bool HostTryCommitAugments()
	{
		if ( !HasHostAuthority )
			return false;

		var inventory = ResolveInventory();
		if ( inventory is null )
			return false;

		var cost = 0;
		var anyPending = false;
		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			var slot = (AugmentSlot)i;
			if ( !IsSlotPending( slot ) )
				continue;

			// A pending socket must hold a real augment that fits an open socket — the client only sent intent.
			if ( !IsSlotUnlocked( slot )
			     || !AugmentCatalog.TryGet( _installed[i].ResourceId, out var def )
			     || !def.AllowsSlot( slot ) )
				return false;

			anyPending = true;
			cost += def.InstallGoldCost( slot );
		}

		if ( !anyPending )
			return false;

		// freeAugments hack: the owner's flag rides on the pawn, so the host waives the gold too.
		if ( FreeAugmentsHack )
			cost = 0;

		if ( cost > 0 )
		{
			var price = new List<CraftingIngredient>
			{
				new() { ResourceId = AugmentCurrency.GoldResourceId, Amount = cost },
			};

			if ( !inventory.HasResources( price ) || !inventory.HostTryConsumeResources( price ) )
				return false;
		}

		var quests = Components.Get<PlayerQuests>();
		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			var stack = _installed[i];
			var wasPending = IsSlotPending( (AugmentSlot)i );
			_committed[i] = stack.IsEmpty ? string.Empty : ResourceCatalog.NormalizeResourceId( stack.ResourceId );

			if ( wasPending )
				quests?.HostReport( QuestEventIds.AugmentInstalled, _committed[i] );
		}

		NotifyChanged();
		return true;
	}

	// ── Station close: nothing stays on the doll unless it was paid for ────────────────────

	/// <summary>
	/// Owner: the station closed. Every pending socket empties back to the bank, then the bag, then
	/// the ground; stale committed ids on empty sockets are cleared. Paid-for sockets are untouched.
	/// </summary>
	public void OwnerRevertPendingSockets()
	{
		if ( !IsLocalManagingClient() )
			return;

		if ( HasHostAuthority )
			HostRevertPendingSockets();
		else
			RpcHostRevertPending();
	}

	void HostRevertPendingSockets()
	{
		if ( !HasHostAuthority )
			return;

		var inventory = ResolveInventory();
		var changed = false;
		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			var slot = (AugmentSlot)i;
			var stack = _installed[i];
			if ( stack.IsEmpty )
			{
				if ( !string.IsNullOrWhiteSpace( _committed[i] ) )
				{
					_committed[i] = string.Empty;
					changed = true;
				}

				continue;
			}

			if ( IsSlotActive( slot ) )
				continue;

			_installed[i] = InventorySlot.Empty;
			_committed[i] = string.Empty;
			changed = true;
			HostStoreOrDrop( stack, inventory );
		}

		if ( changed )
			NotifyChanged();
	}

	/// <summary>Bank if it fits, else the bag, else dropped at the player's feet — never lost.</summary>
	void HostStoreOrDrop( in InventorySlot stack, PlayerInventory inventory )
	{
		var id = ResourceCatalog.NormalizeResourceId( stack.ResourceId );
		var count = Math.Max( 1, stack.Count );

		if ( HostCanFitBank( id, count ) && HostTryAddToBank( id, count ) )
			return;

		if ( inventory is not null && inventory.HostCanFitResource( id, count ) && inventory.HostTryAddResource( id, count ) )
			return;

		var held = new InventoryCursorStack();
		held.Set( id, count );
		HeldStackWorldDrop.TryDropAtPlayer( GameObject, ref held );
	}

	[Rpc.Host]
	void RpcHostRevertPending()
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerOwner() )
			return;

		HostRevertPendingSockets();
	}

	// ── Bank ────────────────────────────────────────────────────────────────────────────────

	bool HostCanFitBank( string resourceId, int count )
	{
		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var remaining = count;
		var maxStack = Math.Max( 1, ResourceCatalog.GetMaxStack( resourceId ) );

		for ( var i = 0; i < BankSlotCount && remaining > 0; i++ )
		{
			var slot = _bank[i];
			if ( slot.IsEmpty )
			{
				remaining -= Math.Min( remaining, maxStack );
				continue;
			}

			if ( !ResourceCatalog.ResourceIdsMatch( slot.ResourceId, resourceId ) )
				continue;

			remaining -= ResourceCatalog.ClampAddToStack( resourceId, slot.Count, remaining );
		}

		return remaining <= 0;
	}

	bool HostTryAddToBank( string resourceId, int count )
	{
		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var remaining = count;
		var maxStack = Math.Max( 1, ResourceCatalog.GetMaxStack( resourceId ) );

		for ( var i = 0; i < BankSlotCount && remaining > 0; i++ )
		{
			var slot = _bank[i];
			if ( slot.IsEmpty || !ResourceCatalog.ResourceIdsMatch( slot.ResourceId, resourceId ) )
				continue;

			var add = ResourceCatalog.ClampAddToStack( resourceId, slot.Count, remaining );
			if ( add <= 0 )
				continue;

			ApplyBankLocal( i, new InventorySlot { ResourceId = resourceId, Count = slot.Count + add } );
			remaining -= add;
		}

		for ( var i = 0; i < BankSlotCount && remaining > 0; i++ )
		{
			if ( !_bank[i].IsEmpty )
				continue;

			var take = Math.Min( remaining, maxStack );
			ApplyBankLocal( i, new InventorySlot { ResourceId = resourceId, Count = take } );
			remaining -= take;
		}

		return remaining <= 0;
	}

	public bool OwnerTryPickupBank( int index, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( !IsLocalManagingClient() || index < 0 || index >= BankSlotCount )
			return false;

		var current = _bank[index];
		if ( current.IsEmpty )
			return false;

		picked = current;
		OwnerSetBank( index, InventorySlot.Empty );
		return true;
	}

	public bool OwnerTryPlaceHeldBank( int index, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() || index < 0 || index >= BankSlotCount )
			return false;

		if ( !AugmentCatalog.IsAugment( held.ResourceId ) )
			return false;

		var id = ResourceCatalog.NormalizeResourceId( held.ResourceId );
		var maxStack = Math.Max( 1, ResourceCatalog.GetMaxStack( id ) );
		var existing = _bank[index];

		if ( existing.IsEmpty )
		{
			var take = Math.Min( held.Count, maxStack );
			OwnerSetBank( index, new InventorySlot { ResourceId = id, Count = take } );
			held.Count -= take;
			if ( held.Count <= 0 )
				held.Clear();
			return true;
		}

		if ( !ResourceCatalog.ResourceIdsMatch( existing.ResourceId, id ) )
		{
			// Swap when cursor holds a full move (single-item augments).
			if ( held.Count != 1 || existing.Count != 1 )
				return false;

			OwnerSetBank( index, new InventorySlot { ResourceId = id, Count = 1 } );
			held.Set( existing.ResourceId, existing.Count );
			return true;
		}

		var add = ResourceCatalog.ClampAddToStack( id, existing.Count, held.Count );
		if ( add <= 0 )
			return false;

		OwnerSetBank( index, new InventorySlot { ResourceId = id, Count = existing.Count + add } );
		held.Count -= add;
		if ( held.Count <= 0 )
			held.Clear();
		return true;
	}

	public bool OwnerTryFinishBankDrag( int source, int target, ref InventoryCursorStack held )
	{
		if ( !IsLocalManagingClient() || source == target )
			return false;

		if ( !held.IsEmpty )
			return OwnerTryPlaceHeldBank( target, ref held );

		if ( source < 0 || source >= BankSlotCount || target < 0 || target >= BankSlotCount )
			return false;

		var a = _bank[source];
		var b = _bank[target];
		OwnerSetBank( target, a );
		OwnerSetBank( source, b );
		return true;
	}

	public bool TryFindEmptyBankSlot( out int index )
	{
		for ( var i = 0; i < BankSlotCount; i++ )
		{
			if ( _bank[i].IsEmpty )
			{
				index = i;
				return true;
			}
		}

		index = -1;
		return false;
	}

	// ── Sockets (paper doll placement — free, pending until Augment) ────────────────────────

	public bool OwnerTryPickupInstalled( AugmentSlot slot, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( !IsLocalManagingClient() )
			return false;

		var current = GetInstalled( slot );
		if ( current.IsEmpty )
			return false;

		picked = current;
		OwnerSetInstalled( slot, InventorySlot.Empty );
		return true;
	}

	public bool OwnerTryPlaceIntoInstalled( AugmentSlot slot, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( !IsSlotUnlocked( slot ) )
			return false;

		if ( !AugmentCatalog.TryGet( held.ResourceId, out var def ) || !def.AllowsSlot( slot ) )
			return false;

		var incoming = new InventorySlot
		{
			ResourceId = ResourceCatalog.NormalizeResourceId( held.ResourceId ),
			Count = 1,
		};
		var previous = GetInstalled( slot );
		OwnerSetInstalled( slot, incoming );

		held.Count--;
		if ( held.Count <= 0 )
			held.Clear();

		if ( !previous.IsEmpty )
		{
			if ( held.IsEmpty )
				held.Set( previous.ResourceId, previous.Count );
			else
				return false;
		}

		return true;
	}

	public bool OwnerTryFinishInstalledDrag( AugmentSlot source, AugmentSlot target, ref InventoryCursorStack held )
	{
		if ( !IsLocalManagingClient() || source == target )
			return false;

		if ( !held.IsEmpty )
			return OwnerTryPlaceIntoInstalled( target, ref held );

		var sourceStack = GetInstalled( source );
		if ( sourceStack.IsEmpty || !AugmentCatalog.TryGet( sourceStack.ResourceId, out var def ) )
			return false;

		if ( !IsSlotUnlocked( target ) || !def.AllowsSlot( target ) )
			return false;

		var targetStack = GetInstalled( target );
		if ( !targetStack.IsEmpty )
		{
			// Swap only when the displaced augment fits the source socket too.
			if ( !AugmentCatalog.TryGet( targetStack.ResourceId, out var other ) || !other.AllowsSlot( source ) )
				return false;
		}

		OwnerSetInstalled( target, sourceStack );
		OwnerSetInstalled( source, targetStack );
		return true;
	}

	/// <summary>First open, empty socket this augment fits (shift-click from the bank).</summary>
	public bool TryFindInstallSlot( string resourceId, out AugmentSlot slot )
	{
		slot = default;
		if ( !AugmentCatalog.TryGet( resourceId, out var def ) )
			return false;

		var allowed = def.AllowedSlots;
		for ( var i = 0; i < allowed.Count; i++ )
		{
			if ( IsSlotUnlocked( allowed[i] ) && GetInstalled( allowed[i] ).IsEmpty )
			{
				slot = allowed[i];
				return true;
			}
		}

		return false;
	}

	// ── Death ───────────────────────────────────────────────────────────────────────────────

	/// <summary>Host death path: collect installed + bank stacks when <see cref="DropAugmentsOnDeath"/>.</summary>
	public void HostCollectDeathDrops( List<(string ResourceId, int Count)> into )
	{
		if ( !HasHostAuthority || !DropAugmentsOnDeath || into is null )
			return;

		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			var s = _installed[i];
			if ( !s.IsEmpty )
				into.Add( (ResourceCatalog.NormalizeResourceId( s.ResourceId ), s.Count) );
		}

		for ( var i = 0; i < BankSlotCount; i++ )
		{
			var s = _bank[i];
			if ( !s.IsEmpty )
				into.Add( (ResourceCatalog.NormalizeResourceId( s.ResourceId ), s.Count) );
		}
	}

	public void HostClearAllForDeathDrop()
	{
		if ( !HasHostAuthority || !DropAugmentsOnDeath )
			return;

		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			ApplyInstalledLocal( (AugmentSlot)i, InventorySlot.Empty );
			_committed[i] = string.Empty;
		}

		for ( var i = 0; i < BankSlotCount; i++ )
			ApplyBankLocal( i, InventorySlot.Empty );

		NotifyChanged();
	}

	// ── Local apply + owner → host intent ───────────────────────────────────────────────────

	PlayerInventory ResolveInventory() => _inventory ??= Components.Get<PlayerInventory>();

	void OwnerSetInstalled( AugmentSlot slot, InventorySlot stack )
	{
		ApplyInstalledLocal( slot, stack );

		if ( HasHostAuthority )
		{
			NotifyChanged();
			return;
		}

		if ( !IsLocalManagingClient() )
			return;

		RpcHostSetInstalled( (int)slot, stack.ResourceId ?? string.Empty, stack.Count );
	}

	void OwnerSetBank( int index, InventorySlot stack )
	{
		ApplyBankLocal( index, stack );

		if ( HasHostAuthority )
		{
			NotifyChanged();
			return;
		}

		if ( !IsLocalManagingClient() )
			return;

		RpcHostSetBank( index, stack.ResourceId ?? string.Empty, stack.Count );
	}

	void ApplyInstalledLocal( AugmentSlot slot, InventorySlot stack )
	{
		var i = (int)slot;
		if ( i < 0 || i >= AugmentSlots.Count )
			return;

		_installed[i] = stack;
		ContentsVersion++;
		AugmentsChanged?.Invoke();
	}

	void ApplyBankLocal( int index, InventorySlot stack )
	{
		if ( index < 0 || index >= BankSlotCount )
			return;

		_bank[index] = stack;
		ContentsVersion++;
		AugmentsChanged?.Invoke();
	}

	/// <summary>Any state change on the authority: bump the UI version and mirror to a remote owner.</summary>
	void NotifyChanged()
	{
		ContentsVersion++;
		AugmentsChanged?.Invoke();
		PushFullStateToOwner();
	}

	void PushFullStateToOwner()
	{
		if ( GameObject.Network is not { Active: true } || !Networking.IsHost )
			return;

		if ( GameObject.Network.Owner is not { } owner )
			return;

		if ( ConnectionIdentity.SameClient( owner, Connection.Local ) )
			return;

		var installedIds = new string[AugmentSlots.Count];
		var installedCounts = new int[AugmentSlots.Count];
		var committedIds = new string[AugmentSlots.Count];
		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			installedIds[i] = _installed[i].ResourceId ?? string.Empty;
			installedCounts[i] = _installed[i].Count;
			committedIds[i] = _committed[i] ?? string.Empty;
		}

		var bankIds = new string[BankSlotCount];
		var bankCounts = new int[BankSlotCount];
		for ( var i = 0; i < BankSlotCount; i++ )
		{
			bankIds[i] = _bank[i].ResourceId ?? string.Empty;
			bankCounts[i] = _bank[i].Count;
		}

		var unlocked = new int[AugmentBodyParts.Count];
		Array.Copy( _unlocked, unlocked, AugmentBodyParts.Count );

		RpcOwnerSyncFull( installedIds, installedCounts, bankIds, bankCounts, committedIds, unlocked );
	}

	bool IsCallerOwner()
	{
		if ( GameObject.Network is not { Active: true, Owner: { } owner } || Rpc.Caller is not { } caller )
			return true;

		return ConnectionIdentity.SameClient( caller, owner );
	}

	[Rpc.Host]
	void RpcHostCraftAugment( string augmentId )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerOwner() )
			return;

		HostTryCraft( augmentId );
	}

	[Rpc.Host]
	void RpcHostEnhance( int part )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerOwner() )
			return;

		HostTryEnhance( (AugmentBodyPart)part );
	}

	[Rpc.Host]
	void RpcHostCommitAugments()
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerOwner() )
			return;

		HostTryCommitAugments();
	}

	[Rpc.Host]
	void RpcHostSetInstalled( int slotIndex, string resourceId, int count )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerOwner() )
			return;

		if ( slotIndex < 0 || slotIndex >= AugmentSlots.Count )
			return;

		var stack = string.IsNullOrWhiteSpace( resourceId ) || count <= 0
			? InventorySlot.Empty
			: new InventorySlot
			{
				ResourceId = ResourceCatalog.NormalizeResourceId( resourceId ),
				Count = Math.Max( 1, count ),
			};

		if ( !stack.IsEmpty )
		{
			var slot = (AugmentSlot)slotIndex;
			if ( !IsSlotUnlocked( slot )
			     || !AugmentCatalog.TryGet( stack.ResourceId, out var def )
			     || !def.AllowsSlot( slot ) )
				return;
		}

		ApplyInstalledLocal( (AugmentSlot)slotIndex, stack );
		PushFullStateToOwner();
	}

	[Rpc.Host]
	void RpcHostSetBank( int index, string resourceId, int count )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerOwner() )
			return;

		if ( index < 0 || index >= BankSlotCount )
			return;

		var stack = string.IsNullOrWhiteSpace( resourceId ) || count <= 0
			? InventorySlot.Empty
			: new InventorySlot
			{
				ResourceId = ResourceCatalog.NormalizeResourceId( resourceId ),
				Count = Math.Max( 1, count ),
			};

		if ( !stack.IsEmpty && !AugmentCatalog.IsAugment( stack.ResourceId ) )
			return;

		ApplyBankLocal( index, stack );
		PushFullStateToOwner();
	}

	[Rpc.Owner]
	void RpcOwnerSyncFull(
		string[] installedIds, int[] installedCounts,
		string[] bankIds, int[] bankCounts,
		string[] committedIds, int[] unlocked )
	{
		if ( installedIds is not null )
		{
			var n = Math.Min( AugmentSlots.Count, installedIds.Length );
			for ( var i = 0; i < n; i++ )
			{
				var id = installedIds[i];
				var c = installedCounts is not null && i < installedCounts.Length ? installedCounts[i] : 0;
				_installed[i] = string.IsNullOrWhiteSpace( id ) || c <= 0
					? InventorySlot.Empty
					: new InventorySlot { ResourceId = id, Count = c };
			}
		}

		if ( committedIds is not null )
		{
			var n = Math.Min( AugmentSlots.Count, committedIds.Length );
			for ( var i = 0; i < n; i++ )
				_committed[i] = committedIds[i] ?? string.Empty;
		}

		if ( bankIds is not null )
		{
			var n = Math.Min( BankSlotCount, bankIds.Length );
			for ( var i = 0; i < n; i++ )
			{
				var id = bankIds[i];
				var c = bankCounts is not null && i < bankCounts.Length ? bankCounts[i] : 0;
				_bank[i] = string.IsNullOrWhiteSpace( id ) || c <= 0
					? InventorySlot.Empty
					: new InventorySlot { ResourceId = id, Count = c };
			}
		}

		if ( unlocked is not null )
		{
			var n = Math.Min( AugmentBodyParts.Count, unlocked.Length );
			for ( var i = 0; i < n; i++ )
				_unlocked[i] = Math.Clamp( unlocked[i], 0, AugmentBodyParts.SlotsPerPart );
		}

		ContentsVersion++;
		AugmentsChanged?.Invoke();
	}
}
