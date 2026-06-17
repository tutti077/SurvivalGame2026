using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Orchestrates paperdoll slots, aggregated stats, tool prefab spawning, and combat enable/disable.
/// </summary>
[Title( "Player Equipment" )]
public sealed class PlayerEquipment : Component
{
	public const int SlotCount = 11;

	public event Action EquipmentChanged;

	readonly InventorySlot[] _slots = new InventorySlot[SlotCount];

	GameObject _toolsRoot;
	GameObject _activeToolInstance;
	string _activeToolResourceId = string.Empty;

	PlayerHotbar _hotbar;
	PlayerCombat _combat;

	public EquippedItemActions MainHandActions =>
		EquipmentCatalog.GetActions( GetSlotResourceId( EquipmentSlot.MainHand ) );

	public bool MainHandHasAction( EquippedItemActions action ) =>
		action != EquippedItemActions.None && ( MainHandActions & action ) == action;

	protected override void OnStart()
	{
		base.OnStart();
		_hotbar = Components.Get<PlayerHotbar>();
		_combat = Components.Get<PlayerCombat>();
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
			SetSlot( EquipmentSlot.MainHand, InventorySlot.Empty );
			return;
		}

		if ( !EquipmentCatalog.TryGet( resourceId, out var profile ) || !profile.HotbarEquipable )
			return;

		var target = EquipmentCatalog.GetPrimarySlot( profile );
		SetSlot( target, CreateEquippedStack( resourceId ) );
	}

	bool TryResolveHotbarEquipResourceId( int hotbarIndex, out string resourceId )
	{
		resourceId = string.Empty;
		if ( _hotbar is null || hotbarIndex < 0 || hotbarIndex >= PlayerHotbar.SlotCount )
			return false;

		var hotbarSlot = _hotbar.GetSlot( hotbarIndex );
		if ( !hotbarSlot.IsEmpty )
		{
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
		var current = GetSlot( slot );
		if ( current.IsEmpty )
			return false;

		picked = current;
		SetSlot( slot, InventorySlot.Empty );
		return true;
	}

	public bool OwnerTryPlaceIntoSlot( EquipmentSlot slot, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty )
			return false;

		if ( !EquipmentCatalog.TryGet( held.ResourceId, out var profile ) )
			return false;

		if ( !EquipmentCatalog.IsSlotAllowed( profile, slot ) )
			return false;

		var incoming = CreateEquippedStack( held.ResourceId );
		var previous = GetSlot( slot );
		SetSlot( slot, incoming );

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

	public bool OwnerTryFinishDragDrop( EquipmentSlot sourceSlot, EquipmentSlot targetSlot, ref InventoryCursorStack held )
	{
		if ( sourceSlot == targetSlot )
			return false;

		var sourceStack = GetSlot( sourceSlot );
		if ( !held.IsEmpty )
			return OwnerTryPlaceIntoSlot( targetSlot, ref held );

		if ( sourceStack.IsEmpty )
			return false;

		if ( !EquipmentCatalog.TryGet( sourceStack.ResourceId, out var profile ) )
			return false;

		if ( !EquipmentCatalog.IsSlotAllowed( profile, targetSlot ) )
			return false;

		var targetStack = GetSlot( targetSlot );
		SetSlot( targetSlot, sourceStack );
		SetSlot( sourceSlot, targetStack );
		return true;
	}

	public bool TryFindQuickEquipSlot( string resourceId, EquipmentSlot fromSlot, out EquipmentSlot targetSlot )
	{
		targetSlot = default;
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		if ( !EquipmentCatalog.TryGet( resourceId, out var profile ) )
			return false;

		for ( var i = 0; i < SlotCount; i++ )
		{
			var slot = (EquipmentSlot)i;
			if ( slot == fromSlot )
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

	void SetSlot( EquipmentSlot slot, InventorySlot stack )
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

		RefreshDerivedState();
		EquipmentChanged?.Invoke();
	}

	void RefreshDerivedState()
	{
		RefreshCombatEnabled();
		RefreshActiveTool();
	}

	void RefreshCombatEnabled()
	{
		if ( _combat is null )
			_combat = Components.Get<PlayerCombat>();

		if ( _combat is null )
			return;

		_combat.Enabled = MainHandHasAction( EquippedItemActions.PrimaryMelee );
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
