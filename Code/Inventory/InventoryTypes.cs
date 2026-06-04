namespace Survival;

/// <summary>
/// One stack stored at a fixed grid index in <see cref="PlayerInventory"/> (slot 0 = top-left, row-major).
/// Drag-and-drop moves stacks between indices; harvest/craft uses the next open index automatically.
/// </summary>
public struct InventorySlot
{
	public string ResourceId { get; set; }
	public int Count { get; set; }

	public bool IsEmpty => string.IsNullOrWhiteSpace( ResourceId ) || Count <= 0;

	public static InventorySlot Empty => default;
}

public static class InventoryDefaults
{
	public const int DefaultSlotCount = 16;
	public const int DefaultColumns = 4;
}
