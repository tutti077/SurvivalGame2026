using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Survival;

public sealed class EquipmentStatModifiersData
{
	public float Armor { get; set; }
	public float Damage { get; set; }
	public float Speed { get; set; }
}

/// <summary>One equippable item row from <c>data/equipment_profiles.json</c>.</summary>
public sealed class EquipmentProfileData
{
	[JsonPropertyName( "resourceId" )]
	public string ResourceId { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	public string Slot { get; set; } = string.Empty;

	public List<string> AllowedSlots { get; set; } = new();

	public List<string> Actions { get; set; } = new();

	public bool TwoHanded { get; set; }

	/// <summary>
	/// Melee timing class from <c>data/melee_weapon_classes.json</c> (oneHanded / twoHanded / spear / dagger).
	/// Empty on a PrimaryMelee item falls back to <see cref="MeleeWeaponClassCatalog.DefaultClassId"/>.
	/// </summary>
	[JsonPropertyName( "weaponClass" )]
	public string WeaponClass { get; set; } = string.Empty;

	/// <summary>Optional per-weapon melee timing overrides — only fields present in JSON replace the class value.</summary>
	[JsonPropertyName( "meleeOverrides" )]
	public MeleeTimingOverridesData MeleeOverrides { get; set; }

	[JsonPropertyName( "hotbarEquipable" )]
	public bool HotbarEquipable { get; set; } = true;

	public EquipmentStatModifiersData StatModifiers { get; set; } = new();

	/// <summary>
	/// Ammo family this weapon accepts (e.g. <c>bow</c>). Empty = not a ranged ammo weapon.
	/// Must match ammo item <c>ammoType</c> from crafting recipes.
	/// </summary>
	[JsonPropertyName( "ammoType" )]
	public string AmmoType { get; set; } = string.Empty;

	public string ToolPrefab { get; set; } = string.Empty;

	public string HandDisplayPrefab { get; set; } = string.Empty;

	/// <summary>Harvest tool category for world nodes (e.g. <c>Axe</c>). Empty = not a harvest tool.</summary>
	[JsonPropertyName( "harvestToolType" )]
	public string HarvestToolType { get; set; } = string.Empty;

	/// <summary>Total durability uses before the item breaks. 0 = item never wears.</summary>
	[JsonPropertyName( "durabilityMax" )]
	public int DurabilityMax { get; set; }

	/// <summary>
	/// Passive drain: one durability tick per this many seconds while equipped in MainHand
	/// (torch/lantern). 0 = no passive drain; weapon hits still cost 1 tick each.
	/// </summary>
	[JsonPropertyName( "durabilityDrainSecondsEquipped" )]
	public float DurabilityDrainSecondsEquipped { get; set; }

	/// <summary>Minimum tool tier for harvest nodes that require one.</summary>
	[JsonPropertyName( "harvestToolTier" )]
	public int HarvestToolTier { get; set; }

	public int InventorySlotBonus { get; set; }

	/// <summary>Max grapple ray / attach range in meters (converted at runtime).</summary>
	public float GrappleMaxRangeMeters { get; set; } = 60f;

	/// <summary>
	/// How long the rope may be paid out with Q, in meters. Its own value rather than a copy of
	/// <see cref="GrappleMaxRangeMeters"/>, so a short hook shot can still be let out into a long
	/// swing. Zero falls back to the attach range.
	/// </summary>
	public float GrappleHardMaxLengthMeters { get; set; } = 60f;

	/// <summary>E/Q winch rate (meters/sec). Same speed both ways, taut or slack.</summary>
	public float GrappleRetractMetersPerSecond { get; set; } = 8f;

	public float GrappleAttachStaminaCost { get; set; }

	/// <summary>Stamina drained per second while attached and airborne.</summary>
	public float GrappleAirborneStaminaPerSecond { get; set; } = 3f;
}
