using Sandbox;

namespace Survival;

/// <summary>
/// Spawns world pickups when a held stack cannot fit in the bag.
/// Stub until resource pickup entities exist.
/// </summary>
public static class HeldStackWorldDrop
{
	public static void TryDrop( GameObject owner, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty || owner is null || !owner.IsValid() )
			return;

		// TODO: spawn a pickup entity at the owner's feet and clear held after a successful spawn.
		Log.Info( $"[HeldStackWorldDrop] {owner.Name}: would drop {held.Count}x {held.ResourceId} (world pickup not implemented)." );
	}
}
