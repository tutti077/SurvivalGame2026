using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Carry weight: the total a pawn carries (<see cref="InventoryWeight.TotalCarried"/>), cached here and refreshed from
/// the inventory / hotbar / paperdoll change events for the Inventory menu's readout. It is a number only: weight
/// slows nothing (Mark 2026-11-16: "Can you stop the whole item weight reduces speed thing?" - the run / walk scale, the
/// overweight walk and the grapple scale of 2026-11-10 are deleted).
/// </summary>
public sealed partial class PlayerMovement
{
	/// <summary>Cached total the pawn carries (inventory + hotbar + worn), rounded to 0.1.</summary>
	public float CarryWeight { get; private set; }

	PlayerInventory _carryInventory;
	PlayerHotbar _carryHotbar;
	bool _carryHooked;

	void InitializeCarryWeight()
	{
		if ( _carryHooked )
			return;

		_carryInventory = Components.Get<PlayerInventory>();
		_carryHotbar = Components.Get<PlayerHotbar>();
		if ( _equipment is null || !_equipment.IsValid() )
			_equipment = Components.Get<PlayerEquipment>();

		if ( _carryInventory is not null )
			_carryInventory.InventoryChanged += RecomputeCarryWeight;
		if ( _carryHotbar is not null )
			_carryHotbar.HotbarChanged += RecomputeCarryWeight;
		if ( _equipment is not null )
			_equipment.EquipmentChanged += RecomputeCarryWeight;

		_carryHooked = true;
		RecomputeCarryWeight();
	}

	void ShutdownCarryWeight()
	{
		if ( !_carryHooked )
			return;

		if ( _carryInventory is not null )
			_carryInventory.InventoryChanged -= RecomputeCarryWeight;
		if ( _carryHotbar is not null )
			_carryHotbar.HotbarChanged -= RecomputeCarryWeight;
		if ( _equipment is not null )
			_equipment.EquipmentChanged -= RecomputeCarryWeight;

		_carryHooked = false;
	}

	/// <summary>Once per grid change: sum the stacks.</summary>
	public void RecomputeCarryWeight()
	{
		CarryWeight = InventoryWeight.TotalCarried( _carryInventory, _carryHotbar, _equipment );
	}
}
