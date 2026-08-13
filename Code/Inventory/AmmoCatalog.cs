using System;

namespace Survival;

/// <summary>
/// Resolves ammo metadata from crafting recipes (<c>ammoType</c> / <c>damage</c>) and weapon
/// accepted ammo from <see cref="EquipmentProfileData.AmmoType"/>.
/// </summary>
public static class AmmoCatalog
{
	public static bool TryGetAmmoType( string resourceId, out string ammoType )
	{
		ammoType = null;
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var recipe = CraftingRecipeCatalog.Get( resourceId );
		if ( recipe is null || string.IsNullOrWhiteSpace( recipe.AmmoType ) )
			return false;

		ammoType = recipe.AmmoType.Trim();
		return ammoType.Length > 0;
	}

	public static bool IsAmmo( string resourceId ) => TryGetAmmoType( resourceId, out _ );

	public static float GetAmmoDamage( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return 0f;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		var recipe = CraftingRecipeCatalog.Get( resourceId );
		return recipe is null ? 0f : Math.Max( 0f, recipe.Damage );
	}

	public static bool TryGetWeaponAmmoType( string weaponResourceId, out string ammoType )
	{
		ammoType = null;
		if ( !EquipmentCatalog.TryGet( weaponResourceId, out var profile ) )
			return false;

		if ( string.IsNullOrWhiteSpace( profile.AmmoType ) )
			return false;

		ammoType = profile.AmmoType.Trim();
		return ammoType.Length > 0;
	}

	public static float GetWeaponDamage( string weaponResourceId )
	{
		if ( !EquipmentCatalog.TryGet( weaponResourceId, out var profile ) )
			return 0f;

		return Math.Max( 0f, profile.StatModifiers?.Damage ?? 0f );
	}

	public static bool AmmoMatchesWeapon( string ammoResourceId, string weaponResourceId )
	{
		if ( !TryGetAmmoType( ammoResourceId, out var ammoType ) )
			return false;

		if ( !TryGetWeaponAmmoType( weaponResourceId, out var weaponAmmoType ) )
			return false;

		return string.Equals( ammoType, weaponAmmoType, StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>
	/// Host: consume one ammo matching <paramref name="ammoType"/>. Prefers
	/// <paramref name="preferredResourceId"/> when it matches and is present; otherwise scans
	/// inventory then hotbar top-left → bottom-right.
	/// </summary>
	public static bool HostTryConsumeOneAmmo(
		PlayerInventory inventory,
		PlayerHotbar hotbar,
		string ammoType,
		string preferredResourceId,
		out string consumedResourceId )
	{
		consumedResourceId = null;
		if ( string.IsNullOrWhiteSpace( ammoType ) )
			return false;

		if ( inventory is null || !inventory.HasHostAuthority )
			return false;

		ammoType = ammoType.Trim();
		preferredResourceId = string.IsNullOrWhiteSpace( preferredResourceId )
			? null
			: ResourceCatalog.NormalizeResourceId( preferredResourceId );

		if ( preferredResourceId is not null
		     && TryGetAmmoType( preferredResourceId, out var preferredType )
		     && string.Equals( preferredType, ammoType, StringComparison.OrdinalIgnoreCase )
		     && CountOwnedResource( inventory, hotbar, preferredResourceId ) > 0 )
		{
			if ( HostTryConsumeOneResource( inventory, hotbar, preferredResourceId ) )
			{
				consumedResourceId = preferredResourceId;
				return true;
			}
		}

		if ( TryFindFirstAmmoResourceId( inventory, hotbar, ammoType, out var foundId )
		     && HostTryConsumeOneResource( inventory, hotbar, foundId ) )
		{
			consumedResourceId = foundId;
			return true;
		}

		return false;
	}

	public static bool HasAnyAmmoForType( PlayerInventory inventory, PlayerHotbar hotbar, string ammoType ) =>
		TryFindFirstAmmoResourceId( inventory, hotbar, ammoType, out _ );

	public static int CountAmmoForType( PlayerInventory inventory, PlayerHotbar hotbar, string ammoType )
	{
		if ( string.IsNullOrWhiteSpace( ammoType ) )
			return 0;

		ammoType = ammoType.Trim();
		var total = 0;
		if ( inventory is not null )
		{
			for ( var i = 0; i < inventory.SlotCount; i++ )
			{
				var slot = inventory.GetSlot( i );
				if ( slot.IsEmpty )
					continue;
				if ( TryGetAmmoType( slot.ResourceId, out var t )
				     && string.Equals( t, ammoType, StringComparison.OrdinalIgnoreCase ) )
					total += slot.Count;
			}
		}

		if ( hotbar is not null )
		{
			for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
			{
				var slot = hotbar.GetSlot( i );
				if ( slot.IsEmpty )
					continue;
				if ( TryGetAmmoType( slot.ResourceId, out var t )
				     && string.Equals( t, ammoType, StringComparison.OrdinalIgnoreCase ) )
					total += slot.Count;
			}
		}

		return total;
	}

	static bool TryFindFirstAmmoResourceId(
		PlayerInventory inventory,
		PlayerHotbar hotbar,
		string ammoType,
		out string resourceId )
	{
		resourceId = null;
		if ( inventory is not null )
		{
			for ( var i = 0; i < inventory.SlotCount; i++ )
			{
				var slot = inventory.GetSlot( i );
				if ( slot.IsEmpty )
					continue;
				if ( TryGetAmmoType( slot.ResourceId, out var t )
				     && string.Equals( t, ammoType, StringComparison.OrdinalIgnoreCase ) )
				{
					resourceId = ResourceCatalog.NormalizeResourceId( slot.ResourceId );
					return true;
				}
			}
		}

		if ( hotbar is not null )
		{
			for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
			{
				var slot = hotbar.GetSlot( i );
				if ( slot.IsEmpty )
					continue;
				if ( TryGetAmmoType( slot.ResourceId, out var t )
				     && string.Equals( t, ammoType, StringComparison.OrdinalIgnoreCase ) )
				{
					resourceId = ResourceCatalog.NormalizeResourceId( slot.ResourceId );
					return true;
				}
			}
		}

		return false;
	}

	static int CountOwnedResource( PlayerInventory inventory, PlayerHotbar hotbar, string resourceId )
	{
		if ( inventory is not null )
			return inventory.CountResource( resourceId );

		return hotbar?.CountResource( resourceId ) ?? 0;
	}

	static bool HostTryConsumeOneResource( PlayerInventory inventory, PlayerHotbar hotbar, string resourceId )
	{
		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );

		// Bag first (TL→BR), then hotbar — matches auto-select scan order.
		// (HostTryConsumeResources prefers hotbar first, so do not use it here.)
		if ( inventory is not null && inventory.HasHostAuthority )
		{
			for ( var i = 0; i < inventory.SlotCount; i++ )
			{
				var slot = inventory.GetSlot( i );
				if ( slot.IsEmpty || !ResourceCatalog.ResourceIdsMatch( slot.ResourceId, resourceId ) )
					continue;

				return inventory.HostTryTakeOne( i );
			}
		}

		if ( hotbar is not null && hotbar.HasHostAuthority )
			return hotbar.TryConsumeResource( resourceId, 1 ) == 0;

		return false;
	}
}
