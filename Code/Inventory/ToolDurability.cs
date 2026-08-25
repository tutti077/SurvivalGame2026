using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Durability rules for tools/weapons. Config lives on equipment profiles
/// (<c>durabilityMax</c>, <c>durabilityDrainSecondsEquipped</c>); wear state rides
/// <see cref="InventorySlot.Wear"/>. Broken items (wear ≥ max) cannot attack, fire,
/// or build until repaired at a workbench.
/// </summary>
public static class ToolDurability
{
	/// <summary>Max durability uses for an item, or 0 when the item never wears.</summary>
	public static int GetMax( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return 0;

		return EquipmentCatalog.TryGet( resourceId, out var profile ) && profile is not null
			? Math.Max( 0, profile.DurabilityMax )
			: 0;
	}

	public static bool HasDurability( string resourceId ) => GetMax( resourceId ) > 0;

	/// <summary>Seconds per passive drain tick while equipped in MainHand (torch/lantern). 0 = none.</summary>
	public static float GetEquippedDrainSeconds( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return 0f;

		return EquipmentCatalog.TryGet( resourceId, out var profile ) && profile is not null
			? Math.Max( 0f, profile.DurabilityDrainSecondsEquipped )
			: 0f;
	}

	public static bool IsBroken( in InventorySlot slot )
	{
		if ( slot.IsEmpty )
			return false;

		var max = GetMax( slot.ResourceId );
		return max > 0 && slot.Wear >= max;
	}

	public static bool IsDamaged( in InventorySlot slot )
	{
		if ( slot.IsEmpty )
			return false;

		return GetMax( slot.ResourceId ) > 0 && slot.Wear > 0;
	}

	/// <summary>Remaining uses fraction (1 = fresh). 1 for items without durability.</summary>
	public static float GetRemainingFraction( in InventorySlot slot )
	{
		if ( slot.IsEmpty )
			return 1f;

		var max = GetMax( slot.ResourceId );
		if ( max <= 0 )
			return 1f;

		return Math.Clamp( 1f - slot.Wear / (float)max, 0f, 1f );
	}

	public static int ClampWear( string resourceId, int wear )
	{
		var max = GetMax( resourceId );
		return max <= 0 ? 0 : Math.Clamp( wear, 0, max );
	}

	/// <summary>Active hotbar stack of a pawn when it tracks durability (the equipped MainHand tool).</summary>
	public static bool TryGetActiveDurableTool( GameObject pawn, out PlayerHotbar hotbar, out int slotIndex, out InventorySlot slot )
	{
		hotbar = null;
		slotIndex = -1;
		slot = InventorySlot.Empty;
		if ( pawn is null || !pawn.IsValid() )
			return false;

		hotbar = pawn.Components.Get<PlayerHotbar>();
		if ( hotbar is null )
			return false;

		slotIndex = hotbar.ActiveSlotIndex;
		slot = hotbar.GetSlot( slotIndex );
		return !slot.IsEmpty && HasDurability( slot.ResourceId );
	}

	/// <summary>True when the pawn's equipped tool is out of durability.</summary>
	public static bool IsActiveToolBroken( GameObject pawn ) =>
		TryGetActiveDurableTool( pawn, out _, out _, out var slot ) && IsBroken( slot );

	/// <summary>Host: one wear tick on the pawn's equipped tool (build hammer place/repair, etc.).</summary>
	public static void HostAddWearToActiveTool( GameObject pawn, int amount = 1 )
	{
		if ( !TryGetActiveDurableTool( pawn, out var hotbar, out var slotIndex, out _ ) )
			return;

		if ( !hotbar.HasHostAuthority )
			return;

		hotbar.HostAddWearToSlot( slotIndex, amount );
	}
}
