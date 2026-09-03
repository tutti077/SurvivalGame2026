namespace Survival;

/// <summary>
/// Event ids that quest objectives listen for (<c>data/quests.json</c> → <c>objectives[].event</c>).
/// Emitters call <see cref="PlayerQuests.HostReport"/> (host-validated actions) or
/// <see cref="PlayerQuests.OwnerReport"/> (owner-local actions) on the acting pawn.
/// </summary>
public static class QuestEventIds
{
	// ---- Emitted today ----------------------------------------------------------------------

	/// <summary>Match = resource id, amount = stack added. <see cref="PlayerInventory.ResourcePickedUp"/>.</summary>
	public const string ResourceCollected = "resource_collected";

	/// <summary>Any edible resource added to inventory (<see cref="FoodCatalog.IsEdible"/>).</summary>
	public const string FoodCollected = "food_collected";

	/// <summary>Match = recipe id, amount = output count. <see cref="PlayerCrafting.HostTryCraft"/>.</summary>
	public const string ItemCrafted = "item_crafted";

	/// <summary>A <see cref="ChopableTree"/> broke from this pawn's axe.</summary>
	public const string TreeChopped = "tree_chopped";

	/// <summary>Host accepted a grapple attach.</summary>
	public const string GrappleAttached = "grapple_attached";

	/// <summary>Match = build piece id. Non-blueprint placement that actually stood.</summary>
	public const string PieceBuilt = "piece_built";

	/// <summary>Match = species / enemy kind id (lower-case: "fox", "whitetail", "scav", …).</summary>
	public const string EntityKilled = "entity_killed";

	/// <summary>Match = augment resource id. Any socket install.</summary>
	public const string AugmentInstalled = "augment_installed";

	/// <summary>Owner deployed the wingsuit.</summary>
	public const string WingsuitDeployed = "wingsuit_deployed";

	/// <summary>Match = <see cref="TerrainPreviewBiomeId"/> name. Fires once each time the pawn's biome changes.</summary>
	public const string BiomeEntered = "biome_entered";

	// ---- Reserved: referenced by quests.json but nothing emits them yet ---------------------

	/// <summary>Stasis pod spawn does not exist yet.</summary>
	public const string PodExited = "pod_exited";

	/// <summary>Map markers do not exist yet (map only shows the player position).</summary>
	public const string MapMarkerPlaced = "map_marker_placed";

	/// <summary>Beds do not exist yet.</summary>
	public const string SleptInBed = "slept_in_bed";

	/// <summary>Armor slots do not exist yet (equipment slots are mainHand / grapple / wingsuit).</summary>
	public const string ArmorSetEquipped = "armor_set_equipped";
}
