using System;
using System.Collections.Generic;

namespace Survival;

/// <summary>
/// Clumps nearby same-resource <see cref="WorldDroppedResource"/> piles into the triggering pile,
/// absorbing closest-first and stopping at one full catalog stack. Piles that still hold items
/// after the trigger fills keep their remainder in place.
/// </summary>
internal static class WorldDroppedResourceMerge
{
	const float MergeRadiusMeters = 3f;

	public static void TryMergeCluster( WorldDroppedResource trigger )
	{
		if ( trigger is null || !trigger.IsAvailable || !trigger.IsMergeAuthority )
			return;

		if ( !trigger.IsReadyToMerge )
			return;

		var resourceId = trigger.ResourceId;
		var room = ResourceCatalog.GetMaxStack( resourceId ) - trigger.StackCount;
		if ( room <= 0 )
			return;

		var radius = TerrainWorldUnits.MetersToEngine( MergeRadiusMeters );
		var center = trigger.GameObject.WorldPosition;

		var cluster = new List<WorldDroppedResource>();
		foreach ( var drop in WorldDroppedResourceRegistry.Drops )
		{
			if ( drop is null || ReferenceEquals( drop, trigger ) || !drop.IsAvailable || !drop.IsReadyToMerge )
				continue;

			if ( !string.Equals( drop.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( HorizontalDistanceBetween( center, drop.GameObject.WorldPosition ) > radius )
				continue;

			cluster.Add( drop );
		}

		if ( cluster.Count == 0 )
			return;

		// Absorb closest piles first so the clump forms around the trigger.
		cluster.Sort( ( a, b ) =>
			HorizontalDistanceBetween( center, a.GameObject.WorldPosition )
				.CompareTo( HorizontalDistanceBetween( center, b.GameObject.WorldPosition ) ) );

		var absorbed = 0;
		for ( var i = 0; i < cluster.Count && room > 0; i++ )
		{
			var drop = cluster[i];
			var take = Math.Min( room, drop.StackCount );
			if ( take <= 0 )
				continue;

			room -= take;
			absorbed += take;
			drop.Count -= take;

			if ( drop.Count <= 0 )
			{
				drop.Count = 0;
				drop.GameObject.Destroy();
			}
		}

		if ( absorbed > 0 )
			trigger.Count += absorbed;
	}

	static float HorizontalDistanceBetween( Vector3 a, Vector3 b )
	{
		var dx = a.x - b.x;
		var dy = a.y - b.y;
		return MathF.Sqrt( dx * dx + dy * dy );
	}
}
