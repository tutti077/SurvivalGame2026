using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Player-owned augment bank (crafted storage) + 18 installed body sockets.
/// Crafted outputs land in the bank; drag/shift-click installs onto the matching socket.
/// </summary>
[Title( "Player Augments" )]
public sealed class PlayerAugments : Component
{
	public const int BankSlotCount = InventoryDefaults.DefaultSlotCount;
	public const int BankColumns = InventoryDefaults.DefaultColumns;

	public event Action AugmentsChanged;

	/// <summary>
	/// When true, installed + bank + bag augments follow death-loot rules like resources.
	/// Standard difficulty keeps them (false).
	/// </summary>
	[Property, Group( "Death" ), Title( "Drop augments on death" )]
	public bool DropAugmentsOnDeath { get; set; }

	InventorySlot[] _installed = new InventorySlot[AugmentSlots.Count];
	InventorySlot[] _bank = new InventorySlot[BankSlotCount];

	PlayerInventory _inventory;

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	/// <summary>Bumps when bank or installed sockets change (UI refresh).</summary>
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

	public bool HasAbility( AugmentAbility ability )
	{
		if ( ability == AugmentAbility.None )
			return false;

		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			var stack = _installed[i];
			if ( stack.IsEmpty || !AugmentCatalog.TryGet( stack.ResourceId, out var def ) )
				continue;

			if ( def.ResolvedAbility == ability )
				return true;
		}

		return false;
	}

	public bool TryGetInstalledDefinition( AugmentAbility ability, out AugmentDefinition definition )
	{
		definition = null;
		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			var stack = _installed[i];
			if ( stack.IsEmpty || !AugmentCatalog.TryGet( stack.ResourceId, out var def ) )
				continue;

			if ( def.ResolvedAbility != ability )
				continue;

			definition = def;
			return true;
		}

		return false;
	}

	public float GetJumpHeightMultiplier()
	{
		if ( !TryGetInstalledDefinition( AugmentAbility.JumpHeight, out var def ) )
			return 1f;

		return Math.Max( 1f, def.JumpHeightMultiplier );
	}

	/// <summary>Craft at the station: consume ingredients from bag, grant output into the augment bank.</summary>
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

		_inventory ??= Components.Get<PlayerInventory>();
		if ( _inventory is null )
			return false;

		AugmentCatalog.EnsureLoaded();
		if ( !AugmentCatalog.TryGet( augmentId, out var def ) || !def.IsUnlockedByDefault )
			return false;

		if ( def.Ingredients is null || def.Ingredients.Count == 0 )
			return false;

		if ( !_inventory.HasResources( def.Ingredients ) )
			return false;

		if ( !HostCanFitBank( def.Id, 1 ) )
			return false;

		if ( !_inventory.HostTryConsumeResources( def.Ingredients ) )
			return false;

		if ( !HostTryAddToBank( def.Id, 1 ) )
			return false;

		NotifyChanged();
		return true;
	}

	public bool CanCraft( string augmentId )
	{
		_inventory ??= Components.Get<PlayerInventory>();
		if ( _inventory is null || !AugmentCatalog.TryGet( augmentId, out var def ) || !def.IsUnlockedByDefault )
			return false;

		if ( def.Ingredients is null || def.Ingredients.Count == 0 )
			return false;

		return _inventory.HasResources( def.Ingredients ) && HostCanFitBank( def.Id, 1 );
	}

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

		if ( !AugmentCatalog.TryGet( held.ResourceId, out var def ) )
			return false;

		if ( !AugmentCatalog.IsSlotAllowed( def, slot ) )
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

		if ( !AugmentCatalog.IsSlotAllowed( def, target ) )
			return false;

		var targetStack = GetInstalled( target );
		OwnerSetInstalled( target, sourceStack );
		OwnerSetInstalled( source, targetStack );
		return true;
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

	public bool TryFindInstallSlot( string resourceId, out AugmentSlot slot )
	{
		slot = default;
		if ( !AugmentCatalog.TryGet( resourceId, out var def ) || !def.TryGetSlot( out slot ) )
			return false;

		return GetInstalled( slot ).IsEmpty;
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
			ApplyInstalledLocal( (AugmentSlot)i, InventorySlot.Empty );

		for ( var i = 0; i < BankSlotCount; i++ )
			ApplyBankLocal( i, InventorySlot.Empty );

		NotifyChanged();
		PushFullStateToOwner();
	}

	void OwnerSetInstalled( AugmentSlot slot, InventorySlot stack )
	{
		ApplyInstalledLocal( slot, stack );

		if ( !string.IsNullOrWhiteSpace( stack.ResourceId ) && stack.Count > 0 )
			Components.Get<PlayerQuests>()?.OwnerReport( QuestEventIds.AugmentInstalled, stack.ResourceId );

		if ( HasHostAuthority )
		{
			PushFullStateToOwner();
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
			PushFullStateToOwner();
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
		NotifyChanged();
	}

	void ApplyBankLocal( int index, InventorySlot stack )
	{
		if ( index < 0 || index >= BankSlotCount )
			return;

		_bank[index] = stack;
		NotifyChanged();
	}

	void NotifyChanged()
	{
		ContentsVersion++;
		AugmentsChanged?.Invoke();
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
		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			installedIds[i] = _installed[i].ResourceId ?? string.Empty;
			installedCounts[i] = _installed[i].Count;
		}

		var bankIds = new string[BankSlotCount];
		var bankCounts = new int[BankSlotCount];
		for ( var i = 0; i < BankSlotCount; i++ )
		{
			bankIds[i] = _bank[i].ResourceId ?? string.Empty;
			bankCounts[i] = _bank[i].Count;
		}

		RpcOwnerSyncFull( installedIds, installedCounts, bankIds, bankCounts );
	}

	[Rpc.Host]
	void RpcHostCraftAugment( string augmentId )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		if ( HostTryCraft( augmentId ) )
			PushFullStateToOwner();
	}

	[Rpc.Host]
	void RpcHostSetInstalled( int slotIndex, string resourceId, int count )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
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
			if ( !AugmentCatalog.TryGet( stack.ResourceId, out var def )
			     || !AugmentCatalog.IsSlotAllowed( def, (AugmentSlot)slotIndex ) )
				return;
		}

		ApplyInstalledLocal( (AugmentSlot)slotIndex, stack );
		PushFullStateToOwner();
	}

	[Rpc.Host]
	void RpcHostSetBank( int index, string resourceId, int count )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
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
	void RpcOwnerSyncFull( string[] installedIds, int[] installedCounts, string[] bankIds, int[] bankCounts )
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

		NotifyChanged();
	}
}
