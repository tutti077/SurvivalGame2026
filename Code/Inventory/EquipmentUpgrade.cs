using System;
using System.Collections.Generic;

namespace Survival;

/// <summary>
/// Workbench equipment upgrades: weapons, tools and armor carry a tier on the stack
/// (<see cref="InventorySlot.Tier"/>, 1 = as crafted). Every tier adds 10 % (rounded) damage and
/// durability on top of the previous tier; armor pieces grow their armor points the same way.
/// Grapple hooks and wingsuits have no tiers. One upgrade costs the item's crafting recipe once
/// more and is validated on the host (<see cref="PlayerCrafting.HostTryUpgradeEquipment"/>).
/// </summary>
public static class EquipmentUpgrade
{
	/// <summary>Tier every item is crafted at.</summary>
	public const int BaseTier = 1;

	/// <summary>Highest tier the workbench offers (I … V).</summary>
	public const int MaxTier = 5;

	/// <summary>Per-tier growth of every upgradable stat, applied on top of the previous tier.</summary>
	public const float StepFraction = 0.10f;

	static readonly string[] RomanNumerals = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };

	/// <summary>Clamp a stored tier into the valid range (old stacks carry 0 → tier I).</summary>
	public static int NormalizeTier( int tier ) => Math.Clamp( tier <= 0 ? BaseTier : tier, BaseTier, MaxTier );

	public static string ToRoman( int tier )
	{
		tier = NormalizeTier( tier );
		return tier < RomanNumerals.Length ? RomanNumerals[tier] : tier.ToString();
	}

	/// <summary>
	/// A stat at <paramref name="tier"/>: the base value grown by <see cref="StepFraction"/> per tier,
	/// rounded at every step (8 → 9 → 10 → 11 → 12; 100 → 110 → 121 → 133 → 146). A step is never
	/// less than +1, so small stats still move (armor 3 → 4 → 5, axe damage 4 → 5 → 6) instead of
	/// rounding back to where they were.
	/// </summary>
	public static float ScaleStat( float baseValue, int tier )
	{
		if ( baseValue <= 0f )
			return 0f;

		var value = baseValue;
		for ( var t = BaseTier; t < NormalizeTier( tier ); t++ )
			value = MathF.Max( MathF.Round( value * (1f + StepFraction) ), value + 1f );

		return value;
	}

	public static int ScaleStat( int baseValue, int tier ) => (int)ScaleStat( (float)baseValue, tier );

	/// <summary>
	/// Weapons, tools and armor: anything with durability, damage or armor on its profile, except
	/// grapple hooks and wingsuits (no tiers, no numeral).
	/// </summary>
	public static bool IsUpgradable( EquipmentProfileData profile )
	{
		if ( profile is null )
			return false;

		var slot = EquipmentCatalog.GetPrimarySlot( profile );
		if ( slot is EquipmentSlot.Grapple or EquipmentSlot.Wingsuit )
			return false;

		if ( profile.DurabilityMax > 0 )
			return true;

		var stats = profile.StatModifiers;
		return stats is not null && (stats.Damage > 0f || stats.Armor > 0f);
	}

	public static bool IsUpgradable( string resourceId ) =>
		EquipmentCatalog.TryGet( resourceId, out var profile ) && IsUpgradable( profile );

	public static bool IsUpgradable( in InventorySlot slot ) =>
		!slot.IsEmpty && IsUpgradable( slot.ResourceId );

	public static bool CanUpgradeFurther( in InventorySlot slot ) =>
		IsUpgradable( slot ) && NormalizeTier( slot.Tier ) < MaxTier;

	/// <summary>Profile damage at the stack's tier (0 when the item has none).</summary>
	public static float GetDamage( EquipmentProfileData profile, int tier )
	{
		var baseDamage = profile?.StatModifiers?.Damage ?? 0f;
		return IsUpgradable( profile ) ? ScaleStat( baseDamage, tier ) : Math.Max( 0f, baseDamage );
	}

	/// <summary>Profile armor at the stack's tier (0 when the item has none).</summary>
	public static float GetArmor( EquipmentProfileData profile, int tier )
	{
		var baseArmor = EquipmentCatalog.GetArmor( profile );
		return IsUpgradable( profile ) ? ScaleStat( baseArmor, tier ) : baseArmor;
	}

	/// <summary>Profile durability max at the stack's tier (0 when the item never wears).</summary>
	public static int GetDurabilityMax( EquipmentProfileData profile, int tier )
	{
		var baseMax = Math.Max( 0, profile?.DurabilityMax ?? 0 );
		return IsUpgradable( profile ) ? ScaleStat( baseMax, tier ) : baseMax;
	}

	/// <summary>
	/// Ingredients one upgrade costs: the item's crafting recipe. An item without a recipe cannot be
	/// upgraded (the list is null).
	/// </summary>
	public static List<CraftingIngredient> BuildCost( string resourceId )
	{
		var recipe = CraftingRecipeCatalog.Get( ResourceCatalog.NormalizeResourceId( resourceId ) );
		if ( recipe?.Ingredients is null )
			return null;

		var list = new List<CraftingIngredient>( recipe.Ingredients.Count );
		for ( var i = 0; i < recipe.Ingredients.Count; i++ )
		{
			var ing = recipe.Ingredients[i];
			if ( ing is null || string.IsNullOrWhiteSpace( ing.ResourceId ) )
				continue;

			list.Add( new CraftingIngredient { ResourceId = ing.ResourceId, Amount = Math.Max( 1, ing.Amount ) } );
		}

		return list;
	}

	/// <summary>Grid ids the workbench upgrade tab lists and the host accepts.</summary>
	public const string HotbarGridId = "hotbar";
	public const string BagGridId = "player";
}
