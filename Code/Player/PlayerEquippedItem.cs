using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Exposes active hotbar selection and MainHand paperdoll actions for other gameplay systems.
/// Equip logic lives on <see cref="PlayerEquipment"/>.
/// </summary>
[Title( "Player Equipped Item" )]
public sealed class PlayerEquippedItem : Component
{
	PlayerHotbar _hotbar;
	PlayerEquipment _equipment;
	string _equippedResourceId = string.Empty;
	EquippedItemActions _equippedActions;
	string _activeHotbarResourceId = string.Empty;

	public string EquippedResourceId => _equippedResourceId;
	public EquippedItemActions EquippedActions => _equippedActions;
	public string ActiveHotbarResourceId => _activeHotbarResourceId;
	public int ActiveHotbarSlotIndex => _hotbar?.ActiveSlotIndex ?? 0;

	public bool HasEquippedItem => !string.IsNullOrWhiteSpace( _equippedResourceId );

	public event Action EquippedChanged;

	public bool HasAction( EquippedItemActions action ) =>
		action != EquippedItemActions.None && ( _equippedActions & action ) == action;

	protected override void OnStart()
	{
		base.OnStart();
		_hotbar = Components.Get<PlayerHotbar>();
		_equipment = Components.Get<PlayerEquipment>();

		if ( _hotbar is not null )
		{
			_hotbar.ActiveSlotChanged += OnHotbarSelectionChanged;
			_hotbar.HotbarChanged += RefreshFromSources;
		}

		if ( _equipment is not null )
			_equipment.EquipmentChanged += RefreshFromSources;

		RefreshFromSources();
	}

	protected override void OnDestroy()
	{
		if ( _hotbar is not null )
		{
			_hotbar.ActiveSlotChanged -= OnHotbarSelectionChanged;
			_hotbar.HotbarChanged -= RefreshFromSources;
		}

		if ( _equipment is not null )
			_equipment.EquipmentChanged -= RefreshFromSources;

		base.OnDestroy();
	}

	void OnHotbarSelectionChanged( int _ ) => RefreshFromSources();

	void RefreshFromSources()
	{
		var previousId = _equippedResourceId;
		var previousActions = _equippedActions;
		var previousHotbarId = _activeHotbarResourceId;

		if ( _hotbar is null )
			_activeHotbarResourceId = string.Empty;
		else
		{
			var slot = _hotbar.GetSlot( _hotbar.ActiveSlotIndex );
			_activeHotbarResourceId = slot.IsEmpty ? string.Empty : ResourceCatalog.NormalizeResourceId( slot.ResourceId );
		}

		if ( _equipment is null )
		{
			_equippedResourceId = string.Empty;
			_equippedActions = EquippedItemActions.None;
		}
		else
		{
			_equippedResourceId = _equipment.GetSlotResourceId( EquipmentSlot.MainHand );
			_equippedActions = _equipment.MainHandActions;
		}

		if ( string.Equals( previousId, _equippedResourceId, StringComparison.OrdinalIgnoreCase )
		     && previousActions == _equippedActions
		     && string.Equals( previousHotbarId, _activeHotbarResourceId, StringComparison.OrdinalIgnoreCase ) )
			return;

		EquippedChanged?.Invoke();
	}
}
