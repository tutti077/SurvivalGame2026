using System;
using System.Collections.Generic;

namespace Survival;

/// <summary>Merges nearby <see cref="WorldDroppedResource"/> piles into full catalog stacks.</summary>
internal static class WorldDroppedResourceMerge
{
	const float MergeRadiusMeters = 0.5f;

	public static void TryMergeCluster( WorldDroppedResource trigger )
	{
		if ( trigger is null || !trigger.IsAvailable || !trigger.IsMergeAuthority )
			return;

		var resourceId = trigger.ResourceId;
		var radius = TerrainWorldUnits.MetersToEngine( MergeRadiusMeters );
		var center = trigger.GameObject.WorldPosition;

		var cluster = new List<WorldDroppedResource>();
		foreach ( var drop in WorldDroppedResourceRegistry.Drops )
		{
			if ( drop is null || !drop.IsAvailable )
				continue;

			if ( !string.Equals( drop.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( HorizontalDistanceBetween( center, drop.GameObject.WorldPosition ) > radius )
				continue;

			cluster.Add( drop );
		}

		if ( cluster.Count <= 1 )
			return;

		var maxStack = ResourceCatalog.GetMaxStack( resourceId );
		var total = 0;
		for ( var i = 0; i < cluster.Count; i++ )
			total += cluster[i].StackCount;

		cluster.Sort( ( a, b ) => a.GameObject.Id.CompareTo( b.GameObject.Id ) );

		var remaining = total;
		for ( var i = 0; i < cluster.Count; i++ )
		{
			var drop = cluster[i];
			if ( remaining <= 0 )
			{
				drop.Count = 0;
				drop.GameObject.Destroy();
				continue;
			}

			var assign = Math.Min( remaining, maxStack );
			drop.Configure( resourceId, assign );
			remaining -= assign;
		}
	}

	static float HorizontalDistanceBetween( Vector3 a, Vector3 b )
	{
		var dx = a.x - b.x;
		var dy = a.y - b.y;
		return MathF.Sqrt( dx * dx + dy * dy );
	}
}
