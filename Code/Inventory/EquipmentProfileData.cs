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

	/// <summary>Minimum tool tier for harvest nodes that require one.</summary>
	[JsonPropertyName( "harvestToolTier" )]
	public int HarvestToolTier { get; set; }

	public int InventorySlotBonus { get; set; }

	/// <summary>Max grapple ray / attach range in meters (converted at runtime).</summary>
	public float GrappleMaxRangeMeters { get; set; } = 30f;

	/// <summary>Hold-to-shorten rope rate (meters/sec).</summary>
	public float GrappleRetractMetersPerSecond { get; set; } = 2.5f;

	/// <summary>
	/// Faster reel-in while rope is slack (not bearing hang/swing load), meters/sec.
	/// </summary>
	[JsonPropertyName( "grappleSlackRetractMetersPerSecond" )]
	public float GrappleSlackRetractMetersPerSecond { get; set; } = 7f;

	/// <summary>Within this many meters of max length the rope counts as taut.</summary>
	[JsonPropertyName( "grappleTautSlackMeters" )]
	public float GrappleTautSlackMeters { get; set; } = 0.75f;

	/// <summary>Extra slack still treated as loaded when swing centripetal demand is high.</summary>
	[JsonPropertyName( "grappleSwingLoadSlackGraceMeters" )]
	public float GrappleSwingLoadSlackGraceMeters { get; set; } = 2.5f;

	/// <summary>Fraction of gravity for v_tan²/radius before a near-taut swing counts as loaded.</summary>
	[JsonPropertyName( "grappleSwingLoadCentripetalGravityFraction" )]
	public float GrappleSwingLoadCentripetalGravityFraction { get; set; } = 0.35f;

	/// <summary>Hold-to-pay-out rope rate (meters/sec).</summary>
	public float GrappleDetractMetersPerSecond { get; set; } = 4f;

	public float GrappleAttachStaminaCost { get; set; }

	/// <summary>Stamina drained per second while attached and airborne.</summary>
	public float GrappleAirborneStaminaPerSecond { get; set; } = 3f;
}
