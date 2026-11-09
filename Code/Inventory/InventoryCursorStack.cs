namespace Survival;

/// <summary>Client-side stack held on the cursor while the inventory menu is open.</summary>
public struct InventoryCursorStack
{
	public string ResourceId { get; private set; }
	public int Count { get; set; }

	/// <summary>Durability uses consumed by the held item (0 = fresh). Rides along so drags keep wear.</summary>
	public int Wear { get; set; }

	/// <summary>Crafter display name carried by the held item (equipment only; empty = untracked).</summary>
	public string CrafterName { get; set; }

	/// <summary>Upgrade tier carried by the held item (see <see cref="InventorySlot.Tier"/>).</summary>
	public int Tier { get; set; }

	public bool IsEmpty => string.IsNullOrWhiteSpace( ResourceId ) || Count <= 0;

	public void Clear()
	{
		ResourceId = null;
		Count = 0;
		Wear = 0;
		CrafterName = null;
		Tier = 0;
	}

	public void Set( string resourceId, int count, int wear = 0, string crafterName = null, int tier = 0 )
	{
		ResourceId = resourceId;
		Count = count;
		Wear = wear;
		CrafterName = crafterName;
		Tier = tier;
	}

	public bool CanStack( string resourceId ) =>
		!IsEmpty && !string.IsNullOrWhiteSpace( resourceId )
		&& string.Equals( ResourceId, resourceId, System.StringComparison.OrdinalIgnoreCase );
}
