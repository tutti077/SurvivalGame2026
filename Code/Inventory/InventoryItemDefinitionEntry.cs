using System;

namespace Game;

/// <summary>Designer-facing row on <see cref="ItemCatalog"/> (stack rules, icon path, world drop prefab).</summary>
[Serializable]
public sealed class InventoryItemDefinitionEntry
{
	/// <summary>Stable id used by <see cref="PickableItem.InventoryItemId"/> and save data.</summary>
	public string Id { get; set; } = "item";

	public string DisplayName { get; set; } = "Item";

	/// <summary>UI texture path on the content mount, e.g. <c>catalog/icons/sword.png</c> or <c>/ui/sbox-logo-square.svg</c> (leading slash = absolute in UI resolver).</summary>
	public string IconTexturePath { get; set; } = "";

	public bool Stackable { get; set; } = true;

	public int MaxStackSize { get; set; } = 64;

	/// <summary>Spawned when dropping from inventory; should include <see cref="PickableItem"/> with matching <see cref="PickableItem.InventoryItemId"/>.</summary>
	public GameObject WorldDroppedPrefab { get; set; }

	/// <summary>Optional prefab resource path (from JSON). Used when <see cref="WorldDroppedPrefab"/> is not set in the editor.</summary>
	public string WorldDropPrefabPath { get; set; } = "";
}
