using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Host solve of the wire graph. Event-driven: anything that changes the graph (a wire, a lever, a
/// node appearing or dying) calls <see cref="MarkDirty"/>, and the next host frame solves once.
/// Nothing here runs per frame while the world is quiet.
/// <para>
/// Every node's wired input is the OR of the outputs of the nodes wired into it; each device then
/// turns that input into its own output (<see cref="ICircuitDevice.ComputeOutput"/>). Passes repeat
/// until nothing moves, capped so a feedback loop can never hang the host.
/// </para>
/// </summary>
public static class CircuitRegistry
{
	/// <summary>Longest wire the host accepts (m). The stripper walks the player between spheres, so this is a sanity cap, not a mechanic.</summary>
	public const float MaxWireLengthMeters = 50f;

	const int MaxSolvePasses = 8;

	static bool _dirty;
	static readonly Dictionary<Guid, CircuitNode> ById = new();
	static readonly Dictionary<Guid, bool> Inputs = new();

	public static void MarkDirty() => _dirty = true;

	public static void TickHost()
	{
		if ( !_dirty )
			return;

		_dirty = false;
		Solve();
	}

	static void Solve()
	{
		ById.Clear();
		var nodes = CircuitNode.All;
		for ( var i = 0; i < nodes.Count; i++ )
		{
			var node = nodes[i];
			if ( node is null || !node.IsValid() || node.IsPreviewGhost )
				continue;

			ById[node.GameObject.Id] = node;
		}

		foreach ( var node in ById.Values )
			node.HostPruneLinks( ById.ContainsKey );

		for ( var pass = 0; pass < MaxSolvePasses; pass++ )
		{
			Inputs.Clear();
			foreach ( var node in ById.Values )
			{
				if ( !node.Output )
					continue;

				var links = node.Links;
				for ( var i = 0; i < links.Count; i++ )
					Inputs[links[i].TargetId] = true;
			}

			var changed = false;
			foreach ( var pair in ById )
			{
				var node = pair.Value;
				var powered = Inputs.TryGetValue( pair.Key, out var on ) && on;
				var output = node.Device?.ComputeOutput( powered ) ?? powered;
				if ( node.HostApplySolve( powered, output ) )
					changed = true;
			}

			if ( !changed )
				return;
		}
	}

	// ------------------------------------------------------------------
	// Host link commit
	// ------------------------------------------------------------------

	/// <summary>
	/// Host: the stripper released on <paramref name="target"/> after pressing on <paramref name="source"/>.
	/// An existing wire between the two is removed; otherwise one is laid in <paramref name="cable"/>.
	/// Validates once: both real, both catalog circuit-enabled, not the same node, wire under the cap,
	/// and the pawn within reach of the target it released on.
	/// </summary>
	public static bool HostToggleLink( CircuitNode source, CircuitNode target, CircuitCable cable, GameObject pawn, float reachMeters )
	{
		if ( source is null || !source.IsValid() || target is null || !target.IsValid() )
			return false;

		if ( !source.HasHostAuthority || source == target )
			return false;

		if ( source.IsPreviewGhost || target.IsPreviewGhost || !source.IsCircuitEnabled || !target.IsCircuitEnabled )
			return false;

		// Wires are stored on the node the signal leaves. Dragging bulb -> lever means the same wire as
		// lever -> bulb, so when only the released end is a source the wire is flipped onto it.
		if ( target.IsSourceKind && !source.IsSourceKind )
			(source, target) = (target, source);

		var maxLength = TerrainWorldUnits.MetersToEngine( MaxWireLengthMeters );
		if ( Vector3.DistanceBetween( source.SphereWorldPosition, target.SphereWorldPosition ) > maxLength )
			return false;

		if ( pawn is { IsValid: true } )
		{
			// The pawn is beside whichever sphere it released on; after a flip that may be either end.
			var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, reachMeters ) + BuildModuleDimensions.ModuleMeters );
			var nearTarget = Vector3.DistanceBetween( pawn.WorldPosition, target.SphereWorldPosition ) <= reach;
			var nearSource = Vector3.DistanceBetween( pawn.WorldPosition, source.SphereWorldPosition ) <= reach;
			if ( !nearTarget && !nearSource )
				return false;
		}

		var exists = source.HasLinkTo( target.GameObject.Id );
		return source.HostSetLink( target.GameObject.Id, cable, add: !exists );
	}

	// ------------------------------------------------------------------
	// Client pick
	// ------------------------------------------------------------------

	/// <summary>
	/// Sphere nearest the view ray within <paramref name="reachUnits"/>: a ray-to-centre test
	/// against the cached sphere positions, no scene trace. Pick tolerance is a few sphere radii so a
	/// 0.1 m sphere is not a pixel hunt.
	/// </summary>
	public static CircuitNode FindNodeUnderView( Vector3 origin, Vector3 direction, float reachUnits, out float hitDistance )
	{
		CircuitNode best = null;
		var bestRayDistance = float.MaxValue;
		hitDistance = 0f;

		var nodes = CircuitNode.All;
		for ( var i = 0; i < nodes.Count; i++ )
		{
			var node = nodes[i];
			if ( node is null || !node.IsValid() || node.IsPreviewGhost || !node.IsCircuitEnabled )
				continue;

			var center = node.SphereWorldPosition;
			var toCenter = center - origin;
			var t = Vector3.Dot( toCenter, direction );
			if ( t <= 0f || t > reachUnits )
				continue;

			var rayDistance = (toCenter - direction * t).Length;
			var tolerance = node.SphereRadiusUnits * 4f;
			if ( rayDistance > tolerance || rayDistance >= bestRayDistance )
				continue;

			best = node;
			bestRayDistance = rayDistance;
			hitDistance = t;
		}

		return best;
	}
}
