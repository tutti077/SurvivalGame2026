namespace Game;

/// <summary>Single inventory cell. Empty when <see cref="ItemId"/> is null/empty or <see cref="Count"/> is zero.</summary>
public struct InvSlot
{
	public string ItemId;
	public int Count;

	public readonly bool IsEmpty => string.IsNullOrEmpty( ItemId ) || Count <= 0;

	public static InvSlot Empty => new() { ItemId = "", Count = 0 };

	public static InvSlot Of( string itemId, int count ) => new() { ItemId = itemId ?? "", Count = count };
}
