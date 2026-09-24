using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Authored on every circuit-enabled prefab (next to its <see cref="BuildPiece"/> and its
/// <see cref="ICircuitDevice"/>). Holds the wire stripper's sphere, the host-solved wired input
/// (<see cref="Powered"/>), what the node pushes down its own wires (<see cref="Output"/>) and the
/// outgoing wires themselves — all <c>[Sync]</c>, so every machine draws the same graph and every
/// device reads the same state. A wire is stored on one of its two ends but carries power both
/// ways — the registry treats the graph as undirected.
/// The catalog flag <see cref="BuildPieceData.CircuitEnabled"/> must also be on for the piece.
/// </summary>
[Title( "Circuit Node" )]
public sealed class CircuitNode : Component
{
	/// <summary>Sphere size at build scale (50 u/m), 10% bigger under the crosshair. Started at 0.1 m; tripled because 0.1 m was too hard to hit.</summary>
	public const float SphereDiameterMeters = 0.3f;
	public const float HoverScale = 1.1f;

	static readonly List<CircuitNode> Active = new();

	[Property, Group( "Circuit" ), Title( "Kind (sphere colour)" )]
	public CircuitNodeKind Kind { get; set; } = CircuitNodeKind.Light;

	/// <summary>Where the sphere sits, in the piece's local units (0 = piece centre).</summary>
	[Property, Group( "Circuit" ), Title( "Sphere offset (units)" )]
	public Vector3 SphereLocalOffset { get; set; }

	/// <summary>Host → everyone: the circuit this node is wired into is live (any lever on it is on).</summary>
	[Sync] public bool Powered { get; private set; }

	/// <summary>Host → everyone: what this node contributes / shows (lever: thrown; sink: mirrors Powered).</summary>
	[Sync] public bool Output { get; private set; }

	/// <summary>Host → everyone: wires stored on this end (<see cref="CircuitLink.Encode"/>); each is two-way.</summary>
	[Sync] public string LinksEncoded { get; private set; } = string.Empty;

	readonly List<CircuitLink> _links = new();
	string _decodedFrom;
	ICircuitDevice _device;
	BuildPiece _piece;
	double _lastHostTickFrame = -1;

	public static IReadOnlyList<CircuitNode> All => Active;

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	public bool IsPreviewGhost =>
		(_piece ??= Components.Get<BuildPiece>()) is { IsPreviewGhost: true } || GameObject.Tags.Has( "buildpreview" );

	public string PieceId => (_piece ??= Components.Get<BuildPiece>())?.PieceId ?? string.Empty;

	/// <summary>Catalog says this piece may join a circuit (<c>"circuitEnabled": true</c> in build_pieces.json).</summary>
	public bool IsCircuitEnabled =>
		BuildPieceCatalog.TryGet( PieceId, out var data ) && data.CircuitEnabled;

	/// <summary>Can make a circuit live on its own (lever, sensor, generator) rather than only following it.</summary>
	public bool IsSourceKind => Kind is CircuitNodeKind.Switch or CircuitNodeKind.Sensor or CircuitNodeKind.Power;

	public ICircuitDevice Device => _device ??= FindDevice();

	/// <summary>The lever / light / gate component beside this node. Walks the concrete components, so it never leans on interface lookups.</summary>
	ICircuitDevice FindDevice()
	{
		foreach ( var component in Components.GetAll<Component>( FindMode.EverythingInSelf ) )
		{
			if ( component is ICircuitDevice device )
				return device;
		}

		return null;
	}

	public Vector3 SphereWorldPosition => WorldTransform.PointToWorld( SphereLocalOffset );

	public float SphereRadiusUnits => SphereDiameterMeters * BuildColliderSnap.PrefabColliderSize.x * 0.5f;

	/// <summary>Wires stored on this node, decoded once per synced change.</summary>
	public IReadOnlyList<CircuitLink> Links
	{
		get
		{
			var encoded = LinksEncoded ?? string.Empty;
			if ( _decodedFrom != encoded )
			{
				CircuitLink.Decode( encoded, _links );
				_decodedFrom = encoded;
			}

			return _links;
		}
	}

	public bool HasLinkTo( Guid targetId )
	{
		var links = Links;
		for ( var i = 0; i < links.Count; i++ )
		{
			if ( links[i].TargetId == targetId )
				return true;
		}

		return false;
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !Active.Contains( this ) )
			Active.Add( this );
		CircuitRegistry.MarkDirty();
	}

	protected override void OnDisabled()
	{
		Active.Remove( this );
		CircuitRegistry.MarkDirty();
		base.OnDisabled();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !HasHostAuthority || IsPreviewGhost )
			return;

		// One node per frame drains the dirty flag — the registry no-ops for the rest.
		if ( _lastHostTickFrame == Time.NowDouble )
			return;

		_lastHostTickFrame = Time.NowDouble;
		CircuitRegistry.TickHost();
	}

	// ------------------------------------------------------------------
	// Host writes
	// ------------------------------------------------------------------

	/// <summary>Host: add (true) or remove (false) the wire to <paramref name="targetId"/>. Returns whether anything changed.</summary>
	public bool HostSetLink( Guid targetId, CircuitCable cable, bool add )
	{
		if ( !HasHostAuthority || IsPreviewGhost )
			return false;

		var links = new List<CircuitLink>( Links );
		var index = links.FindIndex( l => l.TargetId == targetId );

		if ( add )
		{
			if ( index >= 0 )
			{
				if ( links[index].Cable == cable )
					return false;
				links[index] = new CircuitLink( targetId, cable );
			}
			else
			{
				links.Add( new CircuitLink( targetId, cable ) );
			}
		}
		else
		{
			if ( index < 0 )
				return false;
			links.RemoveAt( index );
		}

		LinksEncoded = CircuitLink.Encode( links );
		CircuitRegistry.MarkDirty();
		return true;
	}

	/// <summary>Host: drop wires whose target no longer exists (called from the solve, never per frame).</summary>
	public void HostPruneLinks( Func<Guid, bool> targetExists )
	{
		if ( !HasHostAuthority )
			return;

		var links = Links;
		var keep = new List<CircuitLink>( links.Count );
		for ( var i = 0; i < links.Count; i++ )
		{
			if ( targetExists( links[i].TargetId ) )
				keep.Add( links[i] );
		}

		if ( keep.Count != links.Count )
			LinksEncoded = CircuitLink.Encode( keep );
	}

	/// <summary>Host (registry only): commit the solved input / output. Returns whether either moved.</summary>
	internal bool HostApplySolve( bool powered, bool output )
	{
		var changed = false;
		if ( Powered != powered )
		{
			Powered = powered;
			changed = true;
		}

		if ( Output != output )
		{
			Output = output;
			changed = true;
		}

		return changed;
	}

	// ------------------------------------------------------------------
	// Lookup
	// ------------------------------------------------------------------

	public static CircuitNode FindById( Scene scene, Guid id )
	{
		if ( scene is null || !scene.IsValid() )
			return null;

		var go = scene.Directory.FindByGuid( id );
		return go is { IsValid: true } ? go.Components.Get<CircuitNode>() : null;
	}
}
