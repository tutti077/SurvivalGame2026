namespace Survival;

/// <summary>One resource stack gained by the local player (for pickup toast UI).</summary>
public readonly struct ResourcePickupNotice
{
	public string ResourceId { get; init; }
	public int Amount { get; init; }

	public ResourcePickupNotice( string resourceId, int amount )
	{
		ResourceId = resourceId;
		Amount = amount;
	}
}
