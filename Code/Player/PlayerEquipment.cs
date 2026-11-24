using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Orchestrates paperdoll slots, aggregated stats, and tool prefab spawning.
/// Paperdoll slots are host-authoritative (same pattern as bag/hotbar) so remote clients' Hook equip reaches grapple validation.
/// A remote owner sends intent only (pick up / place / swap a slot, which hotbar item is in a hand): the item itself comes
/// from the pawn's host cursor (<c>PlayerInventory.HostCursor.cs</c>) or the host's own hotbar, never from the client.
/// </summary>
[Title( "Player Equipment" )]
public sealed partial class PlayerEquipment : Component
{
	public const int SlotCount = 12;

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

	/// <summary>
	/// Host→all peers OffHand presentation id (the shield on the left forearm, <c>PlayerAnimation.Shield.cs</c>).
	/// Same rule as the main hand: paperdoll slots stay owner-private, remotes read this alone.
	/// </summary>
	[Sync( SyncFlags.FromHost )]
	public string NetworkedOffHandResourceId { get; set; } = string.Empty;

	string _lastAppliedNetworkedMainHand = string.Empty;

	PlayerHotbar _hotbar;

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	public EquippedItemActions MainHandActions =>
		EquipmentCatalog.GetActions( GetSlotResourceId( EquipmentSlot.MainHand ) );

	public bool MainHandHasAction( EquippedItemActions action ) =>
		action != EquippedItemActions.None && ( MainHandActions & action ) == action;

	/// <summary>Actions of the OffHand paperdoll item (a shield carries <see cref="EquippedItemActions.Block"/>).</summary>
	public EquippedItemActions OffHandActions =>
		EquipmentCatalog.GetActions( GetSlotResourceId( EquipmentSlot.OffHand ) );

	public bool OffHandHasAction( EquippedItemActions action ) =>
		action != EquippedItemActions.None && ( OffHandActions & action ) == action;

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
		TickHandDraw();
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

	/// <summary>
	/// Re-equip both hands from the hotbar (keys, scroll, click, contents changed): MainHand mirrors
	/// <see cref="PlayerHotbar.PrimarySlotIndex"/>, OffHand mirrors <see cref="PlayerHotbar.OffHandSlotIndex"/>
	/// (a shield). Neither hand is an independent equip destination.
	/// </summary>
	public void SyncEquipFromActiveHotbar()
	{
		if ( _hotbar is null || !CanSyncFromHotbar() )
			return;

		EquipHandFromHotbar( EquipmentSlot.MainHand, _hotbar.PrimarySlotIndex, instant: false );
		EquipHandFromHotbar( EquipmentSlot.OffHand, _hotbar.OffHandSlotIndex, instant: false );
	}

	/// <summary>Scroll hotbar: equip active hotbar stack to MainHand only.</summary>
	public void EquipMainHandFromActiveHotbar() => SyncEquipFromActiveHotbar();

	/// <summary>Tests / hacks: equip one hotbar stack straight into MainHand (no draw time).</summary>
	public void EquipFromHotbarSlot( int hotbarIndex ) => EquipHandFromHotbar( EquipmentSlot.MainHand, hotbarIndex, instant: true );

	void EquipHandFromHotbar( EquipmentSlot hand, int hotbarIndex, bool instant )
	{
		if ( _hotbar is null )
			return;

		if ( hotbarIndex < 0 || !TryResolveHotbarEquipResourceId( hotbarIndex, out var resourceId ) )
		{
			OwnerDrawIntoHand( hand, InventorySlot.Empty );
			return;
		}

		var fits = EquipmentCatalog.TryGet( resourceId, out var profile )
		           && (hand == EquipmentSlot.OffHand
			           ? EquipmentCatalog.IsHotbarOffHandItem( profile )
			           : EquipmentCatalog.IsHotbarMainHandItem( profile ));
		if ( !fits )
		{
			OwnerDrawIntoHand( hand, InventorySlot.Empty );
			return;
		}

		var hotbarStack = _hotbar.GetSlot( hotbarIndex );
		OwnerDrawIntoHand( hand, CreateEquippedStack( resourceId, hotbarStack.Wear, hotbarStack.CrafterName, hotbarStack.Tier ), instant );
	}

	/// <summary>MainHand and OffHand mirror the hotbar — never paperdoll storage.</summary>
	static bool IsHotbarDrivenSlot( EquipmentSlot slot ) =>
		slot is EquipmentSlot.MainHand or EquipmentSlot.OffHand;

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
		if ( !IsLocalManagingClient() || !PickupFromSlot( slot, out picked ) )
			return false;

		if ( !HasHostAuthority )
			RpcHostPickupFromSlot( (int)slot );
		return true;
	}

	public bool OwnerTryPlaceIntoSlot( EquipmentSlot slot, ref InventoryCursorStack held )
	{
		if ( !IsLocalManagingClient() || !PlaceIntoSlot( slot, ref held ) )
			return false;

		if ( !HasHostAuthority )
			RpcHostPlaceIntoSlot( (int)slot );
		return true;
	}

	public bool OwnerTryFinishDragDrop( EquipmentSlot sourceSlot, EquipmentSlot targetSlot, ref InventoryCursorStack held )
	{
		if ( !IsLocalManagingClient() || sourceSlot == targetSlot )
			return false;

		if ( !held.IsEmpty )
			return OwnerTryPlaceIntoSlot( targetSlot, ref held );

		if ( !SwapSlots( sourceSlot, targetSlot ) )
			return false;

		if ( !HasHostAuthority )
			RpcHostSwapSlots( (int)sourceSlot, (int)targetSlot );
		return true;
	}

	/// <summary>Rule: the stack leaves a storage slot. MainHand / OffHand are hotbar-driven display only, never storage.</summary>
	bool PickupFromSlot( EquipmentSlot slot, out InventorySlot picked )
	{
		picked = InventorySlot.Empty;
		if ( IsHotbarDrivenSlot( slot ) )
			return false;

		var current = GetSlot( slot );
		if ( current.IsEmpty )
			return false;

		picked = current;
		ApplySlotLocal( slot, InventorySlot.Empty );
		return true;
	}

	/// <summary>
	/// Rule: one unit of <paramref name="held"/> is worn in <paramref name="slot"/>; a piece already there
	/// swaps onto the cursor, which is only possible when that unit was the whole stack.
	/// </summary>
	bool PlaceIntoSlot( EquipmentSlot slot, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || IsHotbarDrivenSlot( slot ) )
			return false;

		if ( !EquipmentCatalog.TryGet( held.ResourceId, out var profile ) )
			return false;

		// Hotbar hand items (weapons, tools, shields) are not paperdoll storage (hook/armor still are).
		if ( EquipmentCatalog.IsHotbarHandItem( profile ) )
			return false;

		if ( !EquipmentCatalog.IsSlotAllowed( profile, slot ) )
			return false;

		var previous = GetSlot( slot );
		if ( !previous.IsEmpty && held.Count > 1 )
			return false;

		ApplySlotLocal( slot, CreateEquippedStack( held.ResourceId, held.Wear, held.CrafterName, held.Tier ) );

		held.Count--;
		if ( held.Count <= 0 )
			held.Clear();

		if ( !previous.IsEmpty )
			held.Set( previous.ResourceId, previous.Count, previous.Wear, previous.CrafterName, previous.Tier );

		return true;
	}

	/// <summary>Rule: two storage slots trade places (the moved piece must fit its new slot).</summary>
	bool SwapSlots( EquipmentSlot sourceSlot, EquipmentSlot targetSlot )
	{
		if ( sourceSlot == targetSlot || IsHotbarDrivenSlot( sourceSlot ) || IsHotbarDrivenSlot( targetSlot ) )
			return false;

		var sourceStack = GetSlot( sourceSlot );
		if ( sourceStack.IsEmpty || !EquipmentCatalog.TryGet( sourceStack.ResourceId, out var profile ) )
			return false;

		if ( EquipmentCatalog.IsHotbarHandItem( profile ) || !EquipmentCatalog.IsSlotAllowed( profile, targetSlot ) )
			return false;

		var targetStack = GetSlot( targetSlot );
		ApplySlotLocal( targetSlot, sourceStack );
		ApplySlotLocal( sourceSlot, targetStack );
		return true;
	}

	public bool TryFindQuickEquipSlot( string resourceId, EquipmentSlot fromSlot, out EquipmentSlot targetSlot )
	{
		targetSlot = default;
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		if ( !EquipmentCatalog.TryGet( resourceId, out var profile ) )
			return false;

		// Weapons / tools / shields belong on the hotbar, not paperdoll storage.
		if ( EquipmentCatalog.IsHotbarHandItem( profile ) )
			return false;

		for ( var i = 0; i < SlotCount; i++ )
		{
			var slot = (EquipmentSlot)i;
			if ( slot == fromSlot || IsHotbarDrivenSlot( slot ) )
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

	/// <summary>
	/// A hand mirrors the hotbar: apply locally; a remote owner only tells the host which item it selected
	/// and the host equips its own hotbar copy of it (<see cref="HostSetHandFromHotbar"/>).
	/// </summary>
	void OwnerSetHandSlot( EquipmentSlot hand, InventorySlot stack )
	{
		ApplySlotLocal( hand, stack );

		if ( HasHostAuthority )
		{
			PushEquipmentToOwner();
			return;
		}

		if ( !IsLocalManagingClient() )
			return;

		RpcHostSetHandSlot( (int)hand, stack.ResourceId ?? string.Empty );
	}

	void ApplySlotLocal( EquipmentSlot slot, InventorySlot stack )
	{
		var index = (int)slot;
		if ( index < 0 || index >= SlotCount )
			return;

		var previous = _slots[index];
		_slots[index] = stack;

		// A two-handed main hand (bow) no longer empties the OffHand slot - the shield stays equipped and
		// PlayerAnimation.Shield.cs just hides it while both hands are busy.

		if ( previous.ResourceId == stack.ResourceId && previous.Count == stack.Count )
			return;

		if ( HasHostAuthority && slot == EquipmentSlot.MainHand )
			NetworkedMainHandResourceId = stack.IsEmpty ? string.Empty : (stack.ResourceId ?? string.Empty);
		if ( HasHostAuthority && slot == EquipmentSlot.OffHand )
			NetworkedOffHandResourceId = stack.IsEmpty ? string.Empty : (stack.ResourceId ?? string.Empty);

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
		var wears = new int[SlotCount];
		var crafters = new string[SlotCount];
		var tiers = new int[SlotCount];
		for ( var i = 0; i < SlotCount; i++ )
		{
			ids[i] = _slots[i].ResourceId ?? string.Empty;
			counts[i] = _slots[i].Count;
			wears[i] = _slots[i].Wear;
			crafters[i] = _slots[i].CrafterName ?? string.Empty;
			tiers[i] = _slots[i].Tier;
		}

		RpcOwnerEquipmentSync( ids, counts, wears, crafters, tiers );
	}

	// ── Remote owner intent ─────────────────────────────────────────────────────────────────────────────

	PlayerInventory _cursorInventory;

	PlayerInventory CursorInventory => _cursorInventory ??= Components.Get<PlayerInventory>();

	/// <summary>Host, inside an RPC: it came from the client that owns this pawn and names a real slot.</summary>
	bool AcceptsOwnerRpc( int slotIndex )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || slotIndex < 0 || slotIndex >= SlotCount )
			return false;

		if ( GameObject.Network is not { Active: true, Owner: { } owner } || Rpc.Caller is not { } caller )
			return true;

		return ConnectionIdentity.SameClient( caller, owner );
	}

	/// <summary>Host: a storage slot's piece lands on the pawn's host cursor.</summary>
	public void HostCursorPickupFromSlot( EquipmentSlot slot )
	{
		if ( CursorInventory is not { } cursor )
			return;

		cursor.HostCursorMakeRoomFor( GetSlotResourceId( slot ) );
		if ( PickupFromSlot( slot, out var picked ) )
			cursor.HostCursorCredit( picked );
		else
			PushEquipmentToOwner();
	}

	/// <summary>Host: the pawn's host cursor is worn in a storage slot.</summary>
	public void HostCursorPlaceIntoSlot( EquipmentSlot slot )
	{
		if ( CursorInventory is not { } cursor )
			return;

		if ( !PlaceIntoSlot( slot, ref cursor.HostCursor ) )
			PushEquipmentToOwner();
	}

	public void HostSwapSlots( EquipmentSlot sourceSlot, EquipmentSlot targetSlot )
	{
		if ( !SwapSlots( sourceSlot, targetSlot ) )
			PushEquipmentToOwner();
	}

	/// <summary>
	/// Host: a remote owner says which hotbar item is in this hand. The host equips its own copy of that
	/// stack (wear, maker, tier) and refuses an item its hotbar does not hold.
	/// </summary>
	public bool HostSetHandFromHotbar( EquipmentSlot hand, string resourceId )
	{
		if ( !IsHotbarDrivenSlot( hand ) )
			return false;

		if ( string.IsNullOrWhiteSpace( resourceId ) )
		{
			ApplySlotLocal( hand, InventorySlot.Empty );
			return true;
		}

		if ( !TryFindHotbarStackForHand( hand, resourceId, out var stack ) )
		{
			PushEquipmentToOwner();
			return false;
		}

		ApplySlotLocal( hand, CreateEquippedStack( stack.ResourceId, stack.Wear, stack.CrafterName, stack.Tier ) );
		return true;
	}

	bool TryFindHotbarStackForHand( EquipmentSlot hand, string resourceId, out InventorySlot stack )
	{
		stack = InventorySlot.Empty;
		_hotbar ??= Components.Get<PlayerHotbar>();
		if ( _hotbar is null || !EquipmentCatalog.TryGet( resourceId, out var profile ) )
			return false;

		var fits = hand == EquipmentSlot.OffHand
			? EquipmentCatalog.IsHotbarOffHandItem( profile )
			: EquipmentCatalog.IsHotbarMainHandItem( profile );
		if ( !fits )
			return false;

		// The selected slot first (its wear is the one in use), then any slot holding the item: the
		// selection index is owner-synced and can land a tick after this RPC.
		var selected = hand == EquipmentSlot.OffHand ? _hotbar.OffHandSlotIndex : _hotbar.PrimarySlotIndex;
		if ( IsUsableHotbarStack( selected, resourceId, out stack ) )
			return true;

		for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
		{
			if ( IsUsableHotbarStack( i, resourceId, out stack ) )
				return true;
		}

		return false;
	}

	bool IsUsableHotbarStack( int hotbarIndex, string resourceId, out InventorySlot stack )
	{
		stack = hotbarIndex >= 0 && hotbarIndex < PlayerHotbar.SlotCount ? _hotbar.GetSlot( hotbarIndex ) : InventorySlot.Empty;
		return !stack.IsEmpty
		       && !ToolDurability.IsBroken( stack )
		       && ResourceCatalog.ResourceIdsMatch( stack.ResourceId, resourceId );
	}

	[Rpc.Host]
	void RpcHostPickupFromSlot( int slotIndex )
	{
		if ( AcceptsOwnerRpc( slotIndex ) )
			HostCursorPickupFromSlot( (EquipmentSlot)slotIndex );
	}

	[Rpc.Host]
	void RpcHostPlaceIntoSlot( int slotIndex )
	{
		if ( AcceptsOwnerRpc( slotIndex ) )
			HostCursorPlaceIntoSlot( (EquipmentSlot)slotIndex );
	}

	[Rpc.Host]
	void RpcHostSwapSlots( int sourceSlotIndex, int targetSlotIndex )
	{
		if ( AcceptsOwnerRpc( sourceSlotIndex ) && AcceptsOwnerRpc( targetSlotIndex ) )
			HostSwapSlots( (EquipmentSlot)sourceSlotIndex, (EquipmentSlot)targetSlotIndex );
	}

	[Rpc.Host]
	void RpcHostSetHandSlot( int slotIndex, string resourceId )
	{
		if ( AcceptsOwnerRpc( slotIndex ) )
			HostSetHandFromHotbar( (EquipmentSlot)slotIndex, resourceId );
	}

	/// <summary>Host: put a hook straight into the Grapple slot (terrain-test kit spawn, debug flows) - a host decision, never a client claim.</summary>
	public bool HostEquipGrappleDirect( string resourceId )
		=> HostEquipSlotDirect( resourceId, EquipmentSlot.Grapple, "grapple", "Grapple" );

	/// <summary>Host: put a wingsuit straight into the Wingsuit slot (terrain-test kit spawn, debug flows).</summary>
	public bool HostEquipWingsuitDirect( string resourceId )
		=> HostEquipSlotDirect( resourceId, EquipmentSlot.Wingsuit, "wingsuit", "Wingsuit" );

	bool HostEquipSlotDirect( string resourceId, EquipmentSlot slot, string slotName, string actionName )
	{
		if ( !HasHostAuthority )
			return false;

		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		if ( !EquipmentCatalog.TryGet( resourceId, out var profile ) || profile is null )
			return false;

		if ( !ProfileMatchesSlot( profile, slotName, actionName ) )
			return false;

		ApplySlotLocal( slot, CreateEquippedStack( resourceId ) );
		PushEquipmentToOwner();
		return true;
	}

	static bool ProfileMatchesSlot( EquipmentProfileData profile, string slotName, string actionName )
	{
		if ( profile is null )
			return false;

		if ( string.Equals( profile.Slot, slotName, StringComparison.OrdinalIgnoreCase ) )
			return true;

		if ( profile.AllowedSlots is not null )
		{
			for ( var i = 0; i < profile.AllowedSlots.Count; i++ )
			{
				if ( string.Equals( profile.AllowedSlots[i], slotName, StringComparison.OrdinalIgnoreCase ) )
					return true;
			}
		}

		if ( profile.Actions is not null )
		{
			for ( var i = 0; i < profile.Actions.Count; i++ )
			{
				if ( string.Equals( profile.Actions[i], actionName, StringComparison.OrdinalIgnoreCase ) )
					return true;
			}
		}

		return false;
	}

	[Rpc.Owner]
	void RpcOwnerEquipmentSync( string[] resourceIds, int[] counts, int[] wears, string[] crafters, int[] tiers )
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
			var w = wears is not null && i < wears.Length ? wears[i] : 0;
			var maker = crafters is not null && i < crafters.Length ? crafters[i] : null;
			var tier = tiers is not null && i < tiers.Length ? tiers[i] : 0;
			_slots[i] = string.IsNullOrWhiteSpace( id ) || c <= 0
				? InventorySlot.Empty
				: new InventorySlot { ResourceId = id, Count = c, Wear = w, CrafterName = maker, Tier = tier };
		}

		RefreshDerivedState();
		EquipmentChanged?.Invoke();
	}

	void RefreshDerivedState()
	{
		if ( HasHostAuthority )
		{
			NetworkedMainHandResourceId = GetSlotResourceId( EquipmentSlot.MainHand ) ?? string.Empty;
			NetworkedOffHandResourceId = GetSlotResourceId( EquipmentSlot.OffHand ) ?? string.Empty;
		}

		RefreshActiveTool();
		RefreshArmorState();
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
			else if ( bindable is ToolHoe hoe )
				hoe.BindPawn( GameObject );
			else if ( bindable is ToolPickaxe pickaxe )
				pickaxe.BindPawn( GameObject );
			else if ( bindable is ToolWireStripper stripper )
				stripper.BindPawn( GameObject );
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

	static InventorySlot CreateEquippedStack( string resourceId, int wear = 0, string crafterName = null, int tier = 0 ) =>
		new()
		{
			ResourceId = ResourceCatalog.NormalizeResourceId( resourceId ),
			Count = 1,
			Wear = Math.Max( 0, wear ),
			CrafterName = crafterName,
			Tier = tier,
		};
}
