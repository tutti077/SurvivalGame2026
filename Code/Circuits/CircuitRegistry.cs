using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Host solve of the wire graph. Event-driven: anything that changes the graph (a wire, a lever, a
/// node appearing or dying) calls <see cref="MarkDirty"/>, and the next host frame solves once.
/// Nothing here runs per frame while the world is quiet.
/// <para>
/// Wires carry power both ways: every group of nodes joined by wires is one circuit, and the whole
/// circuit is live when any source in it (a thrown lever) is on — two levers on one circuit are an
/// OR, and the only way it is off is every lever off. Each node's <see cref="CircuitNode.Powered"/>
/// is its circuit's state; its device turns that into <see cref="CircuitNode.Output"/>
/// (<see cref="ICircuitDevice.ComputeOutput"/>) for visuals and for the source check.
/// </para>
/// </summary>
public static class CircuitRegistry
{
	/// <summary>Longest wire the host accepts (m). The stripper walks the player between spheres, so this is a sanity cap, not a mechanic.</summary>
	public const float MaxWireLengthMeters = 50f;

	static bool _dirty;
	static readonly Dictionary<Guid, CircuitNode> ById = new();
	static readonly Dictionary<Guid, List<Guid>> Neighbours = new();
	static readonly Dictionary<Guid, int> CircuitOf = new();
	static readonly List<bool> CircuitLive = new();
	static readonly Stack<Guid> Walk = new();

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

		// Undirected adjacency: a wire stored on either end joins both ends.
		Neighbours.Clear();
		foreach ( var pair in ById )
		{
			var links = pair.Value.Links;
			for ( var i = 0; i < links.Count; i++ )
			{
				var other = links[i].TargetId;
				if ( !ById.ContainsKey( other ) )
					continue;

				Adjacent( pair.Key ).Add( other );
				Adjacent( other ).Add( pair.Key );
			}
		}

		// Flood each circuit once; it is live when any source node in it says so.
		CircuitOf.Clear();
		CircuitLive.Clear();
		foreach ( var start in ById.Keys )
		{
			if ( CircuitOf.ContainsKey( start ) )
				continue;

			var circuit = CircuitLive.Count;
			var live = false;
			Walk.Clear();
			Walk.Push( start );
			CircuitOf[start] = circuit;

			while ( Walk.Count > 0 )
			{
				var id = Walk.Pop();
				var node = ById[id];
				if ( node.IsSourceKind && node.Device is { } source && source.ComputeOutput( false ) )
					live = true;

				if ( !Neighbours.TryGetValue( id, out var adjacent ) )
					continue;

				for ( var i = 0; i < adjacent.Count; i++ )
				{
					var next = adjacent[i];
					if ( CircuitOf.ContainsKey( next ) )
						continue;

					CircuitOf[next] = circuit;
					Walk.Push( next );
				}
			}

			CircuitLive.Add( live );
		}

		foreach ( var pair in ById )
		{
			var node = pair.Value;
			var powered = CircuitLive[CircuitOf[pair.Key]];
			var output = node.Device?.ComputeOutput( powered ) ?? powered;
			node.HostApplySolve( powered, output );
		}
	}

	static List<Guid> Adjacent( Guid id )
	{
		if ( !Neighbours.TryGetValue( id, out var list ) )
		{
			list = new List<Guid>( 4 );
			Neighbours[id] = list;
		}

		return list;
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

		// A wire is one two-way connection whichever end it is stored on. Keep it on the source end
		// when there is one (the overlay reads nicer), and find an existing wire on either end.
		if ( target.IsSourceKind && !source.IsSourceKind )
			(source, target) = (target, source);
		if ( target.HasLinkTo( source.GameObject.Id ) )
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
