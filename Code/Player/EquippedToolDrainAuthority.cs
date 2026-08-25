using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Host: passive durability drain for equipped lights (torch/lantern) — 1 wear tick per
/// <c>durabilityDrainSecondsEquipped</c> while the item sits in the active hotbar slot.
/// Ticked from <see cref="CombatAuthority"/> so remote-owned pawns drain on listen servers too.
/// </summary>
public static class EquippedToolDrainAuthority
{
	static readonly Dictionary<Guid, float> DrainProgress = new();

	public static void HostTick( Scene scene )
	{
		if ( scene is null || !scene.IsValid() )
			return;

		foreach ( var hotbar in scene.GetAllComponents<PlayerHotbar>() )
		{
			if ( hotbar is null || !hotbar.GameObject.IsValid() || !hotbar.HasHostAuthority )
				continue;

			var key = hotbar.GameObject.Id;
			var slotIndex = hotbar.ActiveSlotIndex;
			var slot = hotbar.GetSlot( slotIndex );
			var drainSeconds = slot.IsEmpty ? 0f : ToolDurability.GetEquippedDrainSeconds( slot.ResourceId );

			if ( drainSeconds <= 0f || ToolDurability.IsBroken( slot ) )
			{
				DrainProgress.Remove( key );
				continue;
			}

			DrainProgress.TryGetValue( key, out var progress );
			progress += Time.Delta;

			while ( progress >= drainSeconds )
			{
				progress -= drainSeconds;
				if ( hotbar.HostAddWearToSlot( slotIndex, 1 ) >= ToolDurability.GetMax( slot.ResourceId ) )
				{
					progress = 0f;
					break;
				}
			}

			DrainProgress[key] = progress;
		}
	}
}
