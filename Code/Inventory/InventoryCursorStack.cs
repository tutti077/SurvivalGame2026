namespace Survival;

/// <summary>Client-side stack held on the cursor while the inventory menu is open.</summary>
public struct InventoryCursorStack
{
	public string ResourceId { get; private set; }
	public int Count { get; set; }

	/// <summary>Durability uses consumed by the held item (0 = fresh). Rides along so drags keep wear.</summary>
	public int Wear { get; set; }

	public bool IsEmpty => string.IsNullOrWhiteSpace( ResourceId ) || Count <= 0;

	public void Clear()
	{
		ResourceId = null;
		Count = 0;
		Wear = 0;
	}

	public void Set( string resourceId, int count, int wear = 0 )
	{
		ResourceId = resourceId;
		Count = count;
		Wear = wear;
	}

	public bool CanStack( string resourceId ) =>
		!IsEmpty && !string.IsNullOrWhiteSpace( resourceId )
		&& string.Equals( ResourceId, resourceId, System.StringComparison.OrdinalIgnoreCase );
}
