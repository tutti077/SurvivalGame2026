using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// A bird nest up a Clover tree (Mark 2026-11-10, prompt 28). Gathering is the ordinary hand harvest on the sibling
/// <see cref="ResourceItemDefinition"/> (press E: 0-2 eggs, 0-4 feathers, one press, respawns). This component only
/// adds the tree rule: when a <see cref="ChopableTree"/> within <see cref="TreeReachMeters"/> comes down on the
/// host, the nest tips its contents onto the ground as world drops and counts as gathered. Nests register in a
/// static list so a falling tree scans a handful of nests, never the scene.
/// </summary>
[Title( "Bird Nest" )]
public sealed class BirdNest : Component
{
	/// <summary>Horizontal distance from a felled trunk within which this nest is "in that tree".</summary>
	[Property, Title( "Tree Reach (m)" ), Range( 1f, 10f ), Step( 0.5f )]
	public float TreeReachMeters { get; set; } = 4f;

	static readonly List<BirdNest> Live = new();

	protected override void OnEnabled()
	{
		base.OnEnabled();
		Live.Add( this );
	}

	protected override void OnDisabled()
	{
		Live.Remove( this );
		base.OnDisabled();
	}

	/// <summary>Host: a tree at <paramref name="tree"/> is down — every nest it carried spills.</summary>
	public static void HostNotifyTreeDown( GameObject tree )
	{
		if ( tree is null || !tree.IsValid() || Live.Count == 0 )
			return;

		var trunk = tree.WorldPosition;
		for ( var i = Live.Count - 1; i >= 0; i-- )
		{
			var nest = Live[i];
			if ( nest is null || !nest.IsValid() )
			{
				Live.RemoveAt( i );
				continue;
			}

			var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 1f, nest.TreeReachMeters ) );
			if ( (nest.WorldPosition - trunk).WithZ( 0f ).Length > reach )
				continue;

			nest.HostTipOut();
		}
	}

	/// <summary>Host: roll the nest's yields as drops under it and deplete it through the normal harvest path (so peers hide it too).</summary>
	void HostTipOut()
	{
		var node = Components.Get<ResourceItemDefinition>();
		if ( node is null || !node.IsValid() || node.IsDepleted )
			return;

		var result = node.TryPerformHarvestTick( HarvestToolType.Hand, 0 );
		if ( !result.Success || result.Loot is null )
			return;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		HeldStackWorldDrop.SpawnLootBurst( scene, result.Loot, WorldPosition, GameObject );
	}
}
