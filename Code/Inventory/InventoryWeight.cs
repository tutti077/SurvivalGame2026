using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Carry weight of items (Mark 2026-11-10). Weights are in tenths: a unit never weighs less than 0.1 and
/// every total is rounded to 0.1. Per-unit values come from <c>weight</c> on the <c>resources.json</c> row
/// or the <c>equipment_profiles.json</c> profile; unset rows resolve to <see cref="DefaultGearWeight"/>
/// for gear (anything with an equipment profile, or a single-stack resource row) and
/// <see cref="DefaultStackableUnitWeight"/> for stackable goods. Wood 1, stone 2, tools and armor 5.
/// The total a pawn carries is cached on <see cref="PlayerMovement.CarryWeight"/> and recomputed only
/// when a grid changes — never per frame.
/// </summary>
public static class InventoryWeight
{
	public const float Step = 0.1f;
	public const float DefaultStackableUnitWeight = 0.1f;
	public const float DefaultGearWeight = 5f;

	/// <summary>Snap to the 0.1 grid (never below one step for a positive input).</summary>
	public static float Round( float weight )
	{
		if ( weight <= 0f )
			return 0f;
		var steps = MathF.Round( weight / Step );
		return MathF.Max( 1f, steps ) * Step;
	}

	/// <summary>Weight of one unit of <paramref name="resourceId"/>; 0 for an empty id.</summary>
	public static float UnitWeight( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return 0f;

		if ( EquipmentCatalog.TryGet( resourceId, out var profile ) )
			return Round( profile.Weight >= 0f ? profile.Weight : DefaultGearWeight );

		if ( ResourceDefinitionCatalog.TryGet( resourceId, out var data ) )
		{
			if ( data.Weight >= 0f )
				return Round( data.Weight );
			return data.MaxStack <= 1 ? DefaultGearWeight : DefaultStackableUnitWeight;
		}

		return DefaultStackableUnitWeight;
	}

	public static float StackWeight( in InventorySlot slot ) =>
		slot.IsEmpty ? 0f : UnitWeight( slot.ResourceId ) * slot.Count;

	public static float Total( ReadOnlySpan<InventorySlot> slots )
	{
		var total = 0f;
		for ( var i = 0; i < slots.Length; i++ )
			total += StackWeight( slots[i] );
		return total;
	}

	/// <summary>
	/// Everything the pawn carries: the inventory grid, both hotbar banks and the worn paperdoll.
	/// MainHand / OffHand on the paperdoll mirror hotbar stacks, so they are skipped here.
	/// </summary>
	public static float TotalCarried( PlayerInventory inventory, PlayerHotbar hotbar, PlayerEquipment equipment )
	{
		var total = 0f;
		if ( inventory is not null && inventory.IsValid() )
			total += Total( inventory.Slots );
		if ( hotbar is not null && hotbar.IsValid() )
			total += Total( hotbar.Slots );
		if ( equipment is not null && equipment.IsValid() )
		{
			foreach ( var slot in Enum.GetValues<EquipmentSlot>() )
			{
				if ( slot == EquipmentSlot.MainHand || slot == EquipmentSlot.OffHand )
					continue;
				total += StackWeight( equipment.GetSlot( slot ) );
			}
		}

		return MathF.Round( total / Step ) * Step;
	}
}
