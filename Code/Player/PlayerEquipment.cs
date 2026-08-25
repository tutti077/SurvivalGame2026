using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Orchestrates paperdoll slots, aggregated stats, and tool prefab spawning.
/// Paperdoll slots are host-authoritative (same pattern as bag/hotbar) so remote clients' Hook equip reaches grapple validation.
/// </summary>
[Title( "Player Equipment" )]
public sealed partial class PlayerEquipment : Component
{
	public const int SlotCount = 11;

	public event Action EquipmentChanged;

	InventorySlot[] _slots = new InventorySlot[SlotCount];

	GameObject _toolsRoot;
	GameObject _activeToolInstance;
	string _activeToolResourceId = string.Empty;

	/// <summary>
	/// Host→all peers MainHand presentation id. Inventory slots stay owner-private;
	/// remotes spawn tool meshes and resolve the hold pose from this Sync alone.
	/// </summary>
	[Sync( SyncFlags.FromHost )]
	public string NetworkedMainHandResourceId { get; set; } = string.Empty;

	string _lastAppliedNetworkedMainHand = string.Empty;

	PlayerHotbar _hotbar;

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	public EquippedItemActions MainHandActions =>
		EquipmentCatalog.GetActions( GetSlotResourceId( EquipmentSlot.MainHand ) );

	public bool MainHandHasAction( EquippedItemActions action ) =>
		action != EquippedItemActions.None && ( MainHandActions & action ) == action;

	protected override void OnStart()
	{
		base.OnStart();
		_hotbar = Components.Get<PlayerHotbar>();
		EquipmentCatalog.EnsureLoaded();
		EnsureToolsRoot();

		if ( _hotbar is not null )
		{
			_hotbar.HotbarChanged += OnHotbarChanged;
			_hotbar.ActiveSlotChanged += OnActiveSlotChanged;
		}

		RefreshDerivedState();
		SyncEquipFromActiveHotbar();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		TickRemoteMainHandPresentation();
	}

	protected override void OnDestroy()
	{
		if ( _hotbar is not null )
		{
			_hotbar.HotbarChanged -= OnHotbarChanged;
			_hotbar.ActiveSlotChanged -= OnActiveSlotChanged;
		}

		DestroyActiveTool();
		base.OnDestroy();
	}

	public bool IsLocalManagingClient()
	{
		if ( GameObject.Network is not { Active: true } )
			return true;

		if ( GameObject.Network.Owner is not { } owner )
			return Networking.IsHost;

		return ConnectionIdentity.SameClient( owner, Connection.Local );
	}

	public InventorySlot GetSlot( EquipmentSlot slot )
	{
		var index = (int)slot;
		if ( index < 0 || index >= SlotCount )
			return InventorySlot.Empty;

		return _slots[index];
	}

	public string GetSlotResourceId( EquipmentSlot slot ) =>
		GetSlot( slot ).ResourceId ?? string.Empty;

	public T GetActiveTool<T>() where T : Component
	{
		if ( _activeToolInstance is null || !_activeToolInstance.IsValid() )
			return null;

		return _activeToolInstance.Components.Get<T>();
	}

	/// <summary>Re-equip from the active hotbar slot (keys, scroll, click).</summary>
	public void SyncEquipFromActiveHotbar()
	{
		if ( _hotbar is null || !CanSyncFromHotbar() )
			return;

		EquipFromHotbarSlot( _hotbar.ActiveSlotIndex );
	}

	/// <summary>Scroll hotbar: equip active hotbar stack to MainHand only.</summary>
	public void EquipMainHandFromActiveHotbar() => SyncEquipFromActiveHotbar();

	/// <summary>Number key: equip hotbar stack to its profile primary slot.</summary>
	public void EquipFromHotbarSlot( int hotbarIndex )
	{
		if ( _hotbar is null )
			return;

		if ( !TryResolveHotbarEquipResourceId( hotbarIndex, out var resourceId ) )
		{
			OwnerSetSlot( EquipmentSlot.MainHand, InventorySlot.Empty );
			return;
		}

		if ( !EquipmentCatalog.TryGet( resourceId, out var profile ) || !EquipmentCatalog.IsHotbarMainHandItem( profile ) )
		{
			OwnerSetSlot( EquipmentSlot.MainHand, InventorySlot.Empty );
			return;
		}

		// MainHand mirrors the selected hotbar slot — not an independent equip destination.
		OwnerSetSlot( EquipmentSlot.MainHand, CreateEquippedStack( resourceId ) );
	}

	bool TryResolveHotbarEquipResourceId( int hotbarIndex, out string resourceId )
	{
		resourceId = string.Empty;
		if ( _hotbar is null || hotbarIndex < 0 || hotbarIndex >= PlayerHotbar.SlotCount )
			return false;

		var hotbarSlot = _hotbar.GetSlot( hotbarIndex );
		if ( !hotbarSlot.IsEmpty )
		{
			// Broken tools cannot be equipped — the hand stays empty until a workbench repair.
			// Wear changes raise HotbarChanged, so a tool breaking mid-use auto-unequips here.
			if ( ToolDurability.IsBroken( hotbarSlot ) )
				return false;

			resourceId = hotbarSlot.ResourceId;
			return !string.IsNullOrWhiteSpace( resourceId );
		}

		return false;
	}

	void OnHotbarChanged() => SyncEquipFromActiveHotbar();

	void OnActiveSlotChanged( int _ ) => SyncEquipFromActiveHotbar();

	bool CanSyncFromHotbar()
	{
		if ( _hotbar is null || !_hotbar.IsLocalManagingClient() )
			return false;

		var vitals = Components.Get<PlayerVitals>();
		return vitals is not null && vitals.IsLocalInputOwnedPawn();
	}

	public bool OwnerTryPickupFromSlot( EquipmentSlot slot, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( !IsLocalManagingClient() )
			return false;

		// MainHand is hotbar-driven display only — never inventory storage.
		if ( slot == EquipmentSlot.MainHand )
			return false;

		var current = GetSlot( slot );
		if ( current.IsEmpty )
			return false;

		picked = current;
		OwnerSetSlot( slot, InventorySlot.Empty );
		return true;
	}

	public bool OwnerTryPlaceIntoSlot( EquipmentSlot slot, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || !IsLocalManagingClient() )
			return false;

		if ( slot == EquipmentSlot.MainHand )
			return false;

		if ( !EquipmentCatalog.TryGet( held.ResourceId, out var profile ) )
			return false;

		// Hotbar MainHand weapons/tools are not paperdoll storage (hook/armor still are).
		if ( EquipmentCatalog.IsHotbarMainHandItem( profile ) )
			return false;

		if ( !EquipmentCatalog.IsSlotAllowed( profile, slot ) )
			return false;

		var incoming = CreateEquippedStack( held.ResourceId );
		var previous = GetSlot( slot );
		OwnerSetSlot( slot, incoming );

		held.Count--;
		if ( held.Count <= 0 )
			held.Clear();

		if ( !previous.IsEmpty )
		{
			if ( held.IsEmpty )
				held.Set( previous.ResourceId, previous.Count, previous.Wear );
			else
				return false;
		}

		return true;
	}

	public bool OwnerTryFinishDragDrop( EquipmentSlot sourceSlot, EquipmentSlot targetSlot, ref InventoryCursorStack held )
	{
		if ( !IsLocalManagingClient() )
			return false;

		if ( sourceSlot == targetSlot )
			return false;

		if ( sourceSlot == EquipmentSlot.MainHand || targetSlot == EquipmentSlot.MainHand )
			return false;

		var sourceStack = GetSlot( sourceSlot );
		if ( !held.IsEmpty )
			return OwnerTryPlaceIntoSlot( targetSlot, ref held );

		if ( sourceStack.IsEmpty )
			return false;

		if ( !EquipmentCatalog.TryGet( sourceStack.ResourceId, out var profile ) )
			return false;

		if ( EquipmentCatalog.IsHotbarMainHandItem( profile ) )
			return false;

		if ( !EquipmentCatalog.IsSlotAllowed( profile, targetSlot ) )
			return false;

		var targetStack = GetSlot( targetSlot );
		OwnerSetSlot( targetSlot, sourceStack );
		OwnerSetSlot( sourceSlot, targetStack );
		return true;
	}

	public bool TryFindQuickEquipSlot( string resourceId, EquipmentSlot fromSlot, out EquipmentSlot targetSlot )
	{
		targetSlot = default;
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		if ( !EquipmentCatalog.TryGet( resourceId, out var profile ) )
			return false;

		// Weapons/tools belong on the hotbar, not paperdoll storage.
		if ( EquipmentCatalog.IsHotbarMainHandItem( profile ) )
			return false;

		for ( var i = 0; i < SlotCount; i++ )
		{
			var slot = (EquipmentSlot)i;
			if ( slot == fromSlot || slot == EquipmentSlot.MainHand )
				continue;

			if ( !EquipmentCatalog.IsSlotAllowed( profile, slot ) )
				continue;

			if ( GetSlot( slot ).IsEmpty )
			{
				targetSlot = slot;
				return true;
			}
		}

		return false;
	}

	/// <summary>Owner/host mutation: apply locally and replicate to host (or owner from host).</summary>
	void OwnerSetSlot( EquipmentSlot slot, InventorySlot stack )
	{
		ApplySlotLocal( slot, stack );

		if ( HasHostAuthority )
		{
			PushEquipmentToOwner();
			return;
		}

		if ( !IsLocalManagingClient() )
			return;

		RpcHostSetEquipmentSlot( (int)slot, stack.ResourceId ?? string.Empty, stack.Count );
	}

	void ApplySlotLocal( EquipmentSlot slot, InventorySlot stack )
	{
		var index = (int)slot;
		if ( index < 0 || index >= SlotCount )
			return;

		var previous = _slots[index];
		_slots[index] = stack;

		if ( slot == EquipmentSlot.MainHand && !stack.IsEmpty
		     && EquipmentCatalog.TryGet( stack.ResourceId, out var profile ) && profile.TwoHanded )
			_slots[(int)EquipmentSlot.OffHand] = InventorySlot.Empty;

		if ( previous.ResourceId == stack.ResourceId && previous.Count == stack.Count )
			return;

		if ( HasHostAuthority && slot == EquipmentSlot.MainHand )
			NetworkedMainHandResourceId = stack.IsEmpty ? string.Empty : (stack.ResourceId ?? string.Empty);

		RefreshDerivedState();
		EquipmentChanged?.Invoke();
	}

	/// <summary>Remotes: drive tool mesh + hold pose from host Sync (not owner-private slots).</summary>
	void TickRemoteMainHandPresentation()
	{
		if ( !GameObject.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } )
			return;

		// Host + owning client already refresh from local slots.
		if ( Networking.IsHost || IsLocalManagingClient() )
			return;

		var id = NetworkedMainHandResourceId ?? string.Empty;
		if ( string.Equals( _lastAppliedNetworkedMainHand, id, StringComparison.OrdinalIgnoreCase )
		     && string.Equals( _activeToolResourceId, id, StringComparison.OrdinalIgnoreCase ) )
			return;

		_lastAppliedNetworkedMainHand = id;
		RefreshPresentationFromMainHandId( id );
	}

	void RefreshPresentationFromMainHandId( string mainHandId )
	{
		if ( string.IsNullOrWhiteSpace( mainHandId ) )
		{
			DestroyActiveTool();
			return;
		}

		if ( !EquipmentCatalog.TryGet( mainHandId, out var profile )
		     || string.IsNullOrWhiteSpace( profile.ToolPrefab ) )
		{
			DestroyActiveTool();
			return;
		}

		if ( string.Equals( _activeToolResourceId, mainHandId, StringComparison.OrdinalIgnoreCase )
		     && _activeToolInstance is { IsValid: true } )
			return;

		DestroyActiveTool();
		SpawnTool( profile.ToolPrefab, mainHandId );
	}

	void PushEquipmentToOwner()
	{
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

		RpcOwnerEquipmentSync( ids, counts );
	}

	[Rpc.Host]
	void RpcHostSetEquipmentSlot( int slotIndex, string resourceId, int count )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		if ( slotIndex < 0 || slotIndex >= SlotCount )
			return;

		var stack = string.IsNullOrWhiteSpace( resourceId ) || count <= 0
			? InventorySlot.Empty
			: new InventorySlot
			{
				ResourceId = ResourceCatalog.NormalizeResourceId( resourceId ),
				Count = Math.Max( 1, count ),
			};

		ApplySlotLocal( (EquipmentSlot)slotIndex, stack );
	}

	/// <summary>
	/// Host: accept a client-reported Grapple equip during attach validation when paperdoll RPC lagged.
	/// </summary>
	public bool HostAcceptClientGrappleEquip( string resourceId )
	{
		if ( !HasHostAuthority )
			return false;

		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		if ( !EquipmentCatalog.TryGet( resourceId, out var profile ) || profile is null )
			return false;

		if ( !IsGrappleProfile( profile ) )
			return false;

		ApplySlotLocal( EquipmentSlot.Grapple, CreateEquippedStack( resourceId ) );
		return true;
	}

	static bool IsGrappleProfile( EquipmentProfileData profile )
	{
		if ( profile is null )
			return false;

		if ( string.Equals( profile.Slot, "grapple", StringComparison.OrdinalIgnoreCase ) )
			return true;

		if ( profile.AllowedSlots is not null )
		{
			for ( var i = 0; i < profile.AllowedSlots.Count; i++ )
			{
				if ( string.Equals( profile.AllowedSlots[i], "grapple", StringComparison.OrdinalIgnoreCase ) )
					return true;
			}
		}

		if ( profile.Actions is not null )
		{
			for ( var i = 0; i < profile.Actions.Count; i++ )
			{
				if ( string.Equals( profile.Actions[i], "Grapple", StringComparison.OrdinalIgnoreCase ) )
					return true;
			}
		}

		return false;
	}

	[Rpc.Owner]
	void RpcOwnerEquipmentSync( string[] resourceIds, int[] counts )
	{
		if ( resourceIds is null || counts is null )
			return;

		if ( !IsLocalManagingClient() )
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

		RefreshDerivedState();
		EquipmentChanged?.Invoke();
	}

	void RefreshDerivedState()
	{
		if ( HasHostAuthority )
			NetworkedMainHandResourceId = GetSlotResourceId( EquipmentSlot.MainHand ) ?? string.Empty;

		RefreshActiveTool();
	}

	void RefreshActiveTool()
	{
		var mainHandId = GetSlotResourceId( EquipmentSlot.MainHand );
		if ( string.IsNullOrWhiteSpace( mainHandId ) )
		{
			DestroyActiveTool();
			return;
		}

		if ( !EquipmentCatalog.TryGet( mainHandId, out var profile )
		     || string.IsNullOrWhiteSpace( profile.ToolPrefab ) )
		{
			DestroyActiveTool();
			return;
		}

		if ( string.Equals( _activeToolResourceId, mainHandId, StringComparison.OrdinalIgnoreCase )
		     && _activeToolInstance is { IsValid: true } )
			return;

		DestroyActiveTool();
		SpawnTool( profile.ToolPrefab, mainHandId );
	}

	void SpawnTool( string prefabPath, string resourceId )
	{
		EnsureToolsRoot();
		var prefab = GameObject.GetPrefab( prefabPath );
		if ( prefab is null || !prefab.IsValid() )
		{
			Log.Warning( $"[PlayerEquipment] Tool prefab missing for '{resourceId}': {prefabPath}" );
			return;
		}

		_activeToolInstance = prefab.Clone();
		_activeToolInstance.Parent = _toolsRoot;
		_activeToolInstance.Name = $"tool_{resourceId}";
		_activeToolResourceId = resourceId;

		foreach ( var bindable in _activeToolInstance.Components.GetAll<Component>( FindMode.EverythingInSelf ) )
		{
			if ( bindable is ToolBuildHammer hammer )
				hammer.BindPawn( GameObject );
		}
	}

	void DestroyActiveTool()
	{
		if ( _activeToolInstance is { IsValid: true } )
			_activeToolInstance.Destroy();

		_activeToolInstance = null;
		_activeToolResourceId = string.Empty;
	}

	void EnsureToolsRoot()
	{
		if ( _toolsRoot is { IsValid: true } )
			return;

		_toolsRoot = new GameObject( true, "equipment_tools" );
		_toolsRoot.Parent = GameObject;
	}

	static InventorySlot CreateEquippedStack( string resourceId ) =>
		new()
		{
			ResourceId = ResourceCatalog.NormalizeResourceId( resourceId ),
			Count = 1,
		};
}
