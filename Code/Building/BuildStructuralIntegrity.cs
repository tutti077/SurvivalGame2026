using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Valheim-style structural integrity for placed build pieces. Not physics: each piece holds one
/// support value — the best any touching neighbor can pass it, decayed by distance and direction —
/// and collapses when that value falls under its material minimum. There is no accumulated weight.
/// <para>
/// Host-only solver: <see cref="BuildAuthority"/> calls <see cref="HostOnPlaced"/> /
/// <see cref="HostOnRemoved"/> after mutations (event-driven — nothing runs per frame). Results
/// sync to clients through <see cref="BuildPiece.Support"/> for the hammer hover display.
/// Furniture and stations have no material (<see cref="BuildPieceData.MaterialId"/> empty) and are
/// entirely outside the graph.
/// </para>
/// </summary>
public static class BuildStructuralIntegrity
{
	/// <summary>Adjacency slack in build-collider units (~12 cm at 50 u/m) so flush snapped mates count as touching.</summary>
	const float ContactToleranceUnits = 6f;

	/// <summary>
	/// Every connection costs one full-module hop (2 m + Valheim's 0.1 bias) regardless of the
	/// pieces' real sizes or separation — support is budgeted in PIECES, not meters travelled, so a
	/// stack of thin horizontal beams runs out at the same count (8) as a stack of full walls.
	/// </summary>
	const float HopCostMeters = 2.1f;

	/// <summary>How far below a piece's lowest point the ground probe reaches (units).</summary>
	const float GroundProbeUnits = 10f;

	/// <summary>Spatial hash cell edge (units) — 4 m, comfortably above the largest piece extent.</summary>
	const float CellSizeUnits = 200f;

	static bool _initialized;

	// Terrain never changes at runtime, so a piece's grounded state is computed once at placement.
	static readonly Dictionary<BuildPiece, bool> GroundedCache = new();

	// Per-solve scratch — rebuilt on each place/destroy event, never per frame.
	static readonly Dictionary<(int, int, int), List<BuildPiece>> Cells = new();
	static readonly Dictionary<BuildPiece, BBox> BoundsCache = new();
	static readonly Dictionary<BuildPiece, float> Tentative = new();
	static readonly HashSet<BuildPiece> Finalized = new();
	static readonly List<BuildPiece> NeighborScratch = new();
	static readonly List<BuildPiece> DoomedScratch = new();

	/// <summary>Host: a piece was just placed. May destroy it again immediately (place-then-collapse).</summary>
	public static void HostOnPlaced( BuildPiece piece )
	{
		if ( piece is null || !piece.IsValid() || piece.IsPreviewGhost )
			return;

		var scene = piece.Scene;
		if ( !scene.IsValid() )
			return;

		if ( EnsureInitialized( scene ) )
			return; // Full-scene solve already covered the new piece.

		if ( BuildPieceCatalog.GetMaterialForPiece( piece.PieceId ) is null )
			return;

		BuildSpatialHash( scene );
		var component = CollectComponent( new List<BuildPiece> { piece } );
		SolveAndApply( scene, component );
	}

	/// <summary>
	/// Host: a piece was removed — re-solve what it used to touch and cascade collapses.
	/// <paramref name="removedRoot"/> is the destroyed piece's GameObject: engine destroys are
	/// deferred to end of frame, so without excluding it explicitly the dead piece still enumerates,
	/// still reads grounded from cache, and keeps "supporting" the structure it no longer holds —
	/// which is exactly how a tower kept floating after its base was demolished.
	/// </summary>
	public static void HostOnRemoved( Scene scene, BBox formerBounds, GameObject removedRoot )
	{
		if ( !scene.IsValid() )
			return;

		if ( EnsureInitialized( scene, removedRoot ) )
			return;

		BuildSpatialHash( scene, removedRoot );

		var seeds = new List<BuildPiece>();
		var query = Expand( formerBounds, ContactToleranceUnits * 2f );
		foreach ( var candidate in QueryCells( query ) )
		{
			if ( BoundsOverlap( BoundsCache[candidate], query ) )
				seeds.Add( candidate );
		}

		if ( seeds.Count == 0 )
			return;

		var component = CollectComponent( seeds );
		SolveAndApply( scene, component );
	}

	/// <summary>
	/// Host: one-time full-scene solve so scene-authored or pre-feature pieces get support values.
	/// Safe to call from hot-ish paths — it no-ops after the first run. Returns true when the full
	/// solve ran just now.
	/// </summary>
	public static bool EnsureInitialized( Scene scene ) => EnsureInitialized( scene, null );

	static bool EnsureInitialized( Scene scene, GameObject skipRoot )
	{
		if ( _initialized || !scene.IsValid() )
			return false;

		_initialized = true;
		BuildSpatialHash( scene, skipRoot );

		var all = new HashSet<BuildPiece>();
		foreach ( var list in Cells.Values )
		{
			foreach ( var piece in list )
				all.Add( piece );
		}

		SolveAndApply( scene, all );
		return true;
	}

	// ── Display helpers (client-safe: read the synced Support) ─────────────────────────────

	/// <summary>Support fraction (0 at collapse threshold, 1 at material max) and gradient color for a placed piece.</summary>
	public static bool TryGetSupportDisplay( BuildPiece piece, out float fraction, out Color color )
	{
		fraction = 0f;
		color = default;
		if ( piece is null || !piece.IsValid() || piece.IsPreviewGhost )
			return false;

		var material = BuildPieceCatalog.GetMaterialForPiece( piece.PieceId );
		if ( material is null )
			return false;

		var range = Math.Max( 1f, material.MaxSupport - material.MinSupport );
		fraction = Math.Clamp( (piece.Support - material.MinSupport) / range, 0f, 1f );
		color = ColorForFraction( fraction );
		return true;
	}

	/// <summary>Valheim-style gradient: blue at max support, green → yellow → orange → red toward collapse.</summary>
	public static Color ColorForFraction( float fraction )
	{
		var red = new Color( 0.95f, 0.12f, 0.1f );
		var orange = new Color( 1f, 0.5f, 0.1f );
		var yellow = new Color( 1f, 0.88f, 0.2f );
		var green = new Color( 0.3f, 0.88f, 0.3f );
		var blue = new Color( 0.3f, 0.55f, 1f );

		if ( fraction >= 0.995f )
			return blue;
		if ( fraction >= 0.75f )
			return green;
		if ( fraction >= 0.5f )
			return Color.Lerp( yellow, green, (fraction - 0.5f) / 0.25f );
		if ( fraction >= 0.25f )
			return Color.Lerp( orange, yellow, (fraction - 0.25f) / 0.25f );

		return Color.Lerp( red, orange, fraction / 0.25f );
	}

	// ── Graph construction ─────────────────────────────────────────────────────────────────

	static void BuildSpatialHash( Scene scene, GameObject skipRoot = null )
	{
		Cells.Clear();
		BoundsCache.Clear();

		foreach ( var piece in scene.GetAllComponents<BuildPiece>() )
		{
			if ( piece is null || !piece.IsValid() || piece.IsPreviewGhost )
				continue;

			// A piece destroyed this frame can still enumerate (engine destroy is deferred) —
			// reference compare, not IsValid: the dead object may already report invalid.
			if ( skipRoot is not null && piece.GameObject == skipRoot )
				continue;

			if ( BuildPieceCatalog.GetMaterialForPiece( piece.PieceId ) is null )
				continue;

			var bounds = ComputeWorldBounds( piece );
			BoundsCache[piece] = bounds;
			InsertIntoCells( piece, bounds );
		}

		// Grounded entries for pieces that no longer exist are dead weight — prune with the rebuild.
		DoomedScratch.Clear();
		foreach ( var entry in GroundedCache )
		{
			if ( entry.Key is null || !entry.Key.IsValid() )
				DoomedScratch.Add( entry.Key );
		}

		foreach ( var dead in DoomedScratch )
			GroundedCache.Remove( dead );

		DoomedScratch.Clear();
	}

	static void InsertIntoCells( BuildPiece piece, BBox bounds )
	{
		var min = CellOf( bounds.Mins );
		var max = CellOf( bounds.Maxs );
		for ( var x = min.Item1; x <= max.Item1; x++ )
		for ( var y = min.Item2; y <= max.Item2; y++ )
		for ( var z = min.Item3; z <= max.Item3; z++ )
		{
			var key = (x, y, z);
			if ( !Cells.TryGetValue( key, out var list ) )
			{
				list = new List<BuildPiece>();
				Cells[key] = list;
			}

			list.Add( piece );
		}
	}

	static BBox Expand( BBox bounds, float amount ) =>
		new( bounds.Mins - new Vector3( amount ), bounds.Maxs + new Vector3( amount ) );

	static bool BoundsOverlap( BBox a, BBox b ) =>
		a.Mins.x <= b.Maxs.x && a.Maxs.x >= b.Mins.x
		&& a.Mins.y <= b.Maxs.y && a.Maxs.y >= b.Mins.y
		&& a.Mins.z <= b.Maxs.z && a.Maxs.z >= b.Mins.z;

	static (int, int, int) CellOf( Vector3 position ) =>
		((int)MathF.Floor( position.x / CellSizeUnits ),
		 (int)MathF.Floor( position.y / CellSizeUnits ),
		 (int)MathF.Floor( position.z / CellSizeUnits ));

	static IEnumerable<BuildPiece> QueryCells( BBox bounds )
	{
		var seen = new HashSet<BuildPiece>();
		var min = CellOf( bounds.Mins );
		var max = CellOf( bounds.Maxs );
		for ( var x = min.Item1; x <= max.Item1; x++ )
		for ( var y = min.Item2; y <= max.Item2; y++ )
		for ( var z = min.Item3; z <= max.Item3; z++ )
		{
			if ( !Cells.TryGetValue( (x, y, z), out var list ) )
				continue;

			foreach ( var piece in list )
			{
				if ( seen.Add( piece ) )
					yield return piece;
			}
		}
	}

	static void CollectNeighbors( BuildPiece piece, List<BuildPiece> result )
	{
		result.Clear();
		if ( !BoundsCache.TryGetValue( piece, out var bounds ) )
			return;

		foreach ( var candidate in QueryCells( Expand( bounds, ContactToleranceUnits * 2f ) ) )
		{
			if ( candidate == piece )
				continue;

			if ( Touches( piece, candidate ) )
				result.Add( candidate );
		}
	}

	/// <summary>
	/// World AABB of the piece's true solid: table-frame halves swung through the snap frame
	/// (<see cref="BuildColliderSnap.GetSnapWorldRotation"/>), which composes the kit-mesh quarter
	/// turn AND the baked prefab pitch. Roofs and 45° beams carry their pitch in the mesh — their
	/// root is yaw-only, so a root-rotation box lies flat across empty air and misses everything
	/// the piece actually rests on. Built analytically (not GetBounds) so a piece spawned this
	/// frame has correct bounds before its renderers settle.
	/// </summary>
	static BBox ComputeWorldBounds( BuildPiece piece )
	{
		var rotation = BuildColliderSnap.GetSnapWorldRotation( piece.GameObject, piece.PieceId );
		var half = BuildColliderSnap.GetColliderHalfForPiece( piece.PieceId );
		var position = piece.GameObject.WorldPosition;

		var mins = new Vector3( float.MaxValue );
		var maxs = new Vector3( float.MinValue );
		for ( var xi = -1; xi <= 1; xi += 2 )
		for ( var yi = -1; yi <= 1; yi += 2 )
		for ( var zi = -1; zi <= 1; zi += 2 )
		{
			var corner = position + rotation * new Vector3( xi * half.x, yi * half.y, zi * half.z );
			mins = Vector3.Min( mins, corner );
			maxs = Vector3.Max( maxs, corner );
		}

		return new BBox( mins, maxs );
	}

	/// <summary>
	/// Adjacency: AABB broad-phase, then an oriented-box contact test (face-axis SAT, both frames)
	/// in each piece's snap frame. Plain AABB overlap was the fast first cut, but a 45° beam's AABB
	/// is a fat box around its whole diagonal — pieces floating well clear of the actual beam
	/// "touched" it and inherited support. The face-axis test is exact for parallel/snapped pieces
	/// and only slightly conservative on crossed diagonals (skipped cross axes over-connect, never
	/// under-connect — a missed real contact bricks a piece family, an extra one just shares
	/// support it plausibly should).
	/// </summary>
	static bool Touches( BuildPiece a, BuildPiece b )
	{
		if ( !BoundsCache.TryGetValue( a, out var boundsA ) || !BoundsCache.TryGetValue( b, out var boundsB ) )
			return false;

		if ( !BoundsOverlap( Expand( boundsA, ContactToleranceUnits ), boundsB ) )
			return false;

		var posA = a.GameObject.WorldPosition;
		var posB = b.GameObject.WorldPosition;
		var rotA = BuildColliderSnap.GetSnapWorldRotation( a.GameObject, a.PieceId );
		var rotB = BuildColliderSnap.GetSnapWorldRotation( b.GameObject, b.PieceId );
		var halfA = BuildColliderSnap.GetColliderHalfForPiece( a.PieceId );
		var halfB = BuildColliderSnap.GetColliderHalfForPiece( b.PieceId );

		return TouchesInFrame( posA, rotA, halfA, posB, rotB, halfB )
		       && TouchesInFrame( posB, rotB, halfB, posA, rotA, halfA );
	}

	/// <summary>Face-axis separation test in A's frame — B's oriented halves projected onto A's axes.</summary>
	static bool TouchesInFrame( Vector3 posA, Rotation rotA, Vector3 halfA, Vector3 posB, Rotation rotB, Vector3 halfB )
	{
		var delta = rotA.Inverse * (posB - posA);
		var rel = rotA.Inverse * rotB;
		var bx = rel * new Vector3( halfB.x, 0f, 0f );
		var by = rel * new Vector3( 0f, halfB.y, 0f );
		var bz = rel * new Vector3( 0f, 0f, halfB.z );

		var ex = Math.Abs( bx.x ) + Math.Abs( by.x ) + Math.Abs( bz.x );
		var ey = Math.Abs( bx.y ) + Math.Abs( by.y ) + Math.Abs( bz.y );
		var ez = Math.Abs( bx.z ) + Math.Abs( by.z ) + Math.Abs( bz.z );

		return Math.Abs( delta.x ) < halfA.x + ex + ContactToleranceUnits
		       && Math.Abs( delta.y ) < halfA.y + ey + ContactToleranceUnits
		       && Math.Abs( delta.z ) < halfA.z + ez + ContactToleranceUnits;
	}

	static HashSet<BuildPiece> CollectComponent( List<BuildPiece> seeds )
	{
		var component = new HashSet<BuildPiece>();
		var queue = new Queue<BuildPiece>();
		foreach ( var seed in seeds )
		{
			if ( BoundsCache.ContainsKey( seed ) && component.Add( seed ) )
				queue.Enqueue( seed );
		}

		while ( queue.Count > 0 )
		{
			var current = queue.Dequeue();
			CollectNeighbors( current, NeighborScratch );
			foreach ( var neighbor in NeighborScratch )
			{
				if ( component.Add( neighbor ) )
					queue.Enqueue( neighbor );
			}
		}

		return component;
	}

	// ── Grounding ──────────────────────────────────────────────────────────────────────────

	static bool IsGrounded( Scene scene, BuildPiece piece )
	{
		if ( GroundedCache.TryGetValue( piece, out var cached ) )
			return cached;

		var grounded = ProbeGrounded( scene, piece );
		GroundedCache[piece] = grounded;
		return grounded;
	}

	static bool ProbeGrounded( Scene scene, BuildPiece piece )
	{
		// Snap frame, not root rotation — baked-pitch pieces (roofs, 45° beams) keep a yaw-only
		// root, and the table-frame halves belong to this composed frame.
		var rotation = BuildColliderSnap.GetSnapWorldRotation( piece.GameObject, piece.PieceId );
		var origin = piece.GameObject.WorldPosition;
		var half = BuildColliderSnap.GetColliderHalfForPiece( piece.PieceId );

		// Probe from the LOWEST oriented corners — whatever the piece actually rests on. A wall or
		// floor keeps its whole bottom face; a pitched roof rests on its low edge, whose corners are
		// the only points near the ground (its underside face rides 30–70 units up the slope, past
		// any sane probe reach — which is why ground-placed roofs used to collapse). Each corner
		// also probes a copy pulled 25 % toward the piece's horizontal center, so a corner slightly
		// overhanging a terrain edge still finds the surface it sits against.
		var corners = new Vector3[8];
		var index = 0;
		var minZ = float.MaxValue;
		for ( var xi = -1; xi <= 1; xi += 2 )
		for ( var yi = -1; yi <= 1; yi += 2 )
		for ( var zi = -1; zi <= 1; zi += 2 )
		{
			var corner = origin + rotation * new Vector3( xi * half.x, yi * half.y, zi * half.z );
			corners[index++] = corner;
			minZ = MathF.Min( minZ, corner.z );
		}

		foreach ( var corner in corners )
		{
			if ( corner.z > minZ + ContactToleranceUnits )
				continue;

			if ( ProbeDownHitsAnchor( scene, piece, corner ) )
				return true;

			var pulled = new Vector3(
				corner.x + (origin.x - corner.x) * 0.25f,
				corner.y + (origin.y - corner.y) * 0.25f,
				corner.z );
			if ( ProbeDownHitsAnchor( scene, piece, pulled ) )
				return true;
		}

		return false;
	}

	static bool ProbeDownHitsAnchor( Scene scene, BuildPiece piece, Vector3 point )
	{
		var trace = scene.Trace
			.Ray( point + Vector3.Up * 4f, point - Vector3.Up * GroundProbeUnits )
			.IgnoreGameObjectHierarchy( piece.GameObject )
			.Run();

		return trace.Hit && IsWorldAnchorHit( trace );
	}

	/// <summary>Terrain, rocks, trees — any static world collider that is not part of a build piece.</summary>
	static bool IsWorldAnchorHit( SceneTraceResult trace )
	{
		var hit = trace.GameObject;
		if ( hit is null || !hit.IsValid() )
			return false;

		// Static world only — players, entities and dropped items never anchor a base.
		if ( trace.Body is not null && trace.Body.BodyType != PhysicsBodyType.Static )
			return false;

		for ( var current = hit; current.IsValid(); current = current.Parent )
		{
			if ( current.Components.Get<BuildPiece>() is not null )
				return false;
			if ( current.Tags.Has( "buildpreview" ) || current.Tags.Has( "player" ) )
				return false;
		}

		return true;
	}

	// ── Solve ──────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Max-relaxation Dijkstra over one connected component: grounded pieces seed at material max;
	/// each finalized piece offers its neighbors <c>min(childMax, S × (1 − L(θ)(d + 0.1)))</c>.
	/// Pieces that finalize below their material minimum never relax neighbors, so a single pass
	/// lands directly on the post-cascade state; those pieces are then destroyed together.
	/// </summary>
	static void SolveAndApply( Scene scene, HashSet<BuildPiece> component )
	{
		if ( component.Count == 0 )
			return;

		Tentative.Clear();
		Finalized.Clear();

		foreach ( var piece in component )
		{
			var material = BuildPieceCatalog.GetMaterialForPiece( piece.PieceId );
			Tentative[piece] = IsGrounded( scene, piece ) ? material.MaxSupport : 0f;
		}

		while ( Finalized.Count < component.Count )
		{
			BuildPiece best = null;
			var bestSupport = -1f;
			foreach ( var entry in Tentative )
			{
				if ( Finalized.Contains( entry.Key ) )
					continue;

				if ( entry.Value > bestSupport )
				{
					bestSupport = entry.Value;
					best = entry.Key;
				}
			}

			if ( best is null )
				break;

			Finalized.Add( best );

			var bestMaterial = BuildPieceCatalog.GetMaterialForPiece( best.PieceId );
			if ( bestSupport < bestMaterial.MinSupport )
				continue; // Collapsing pieces hold nothing up.

			CollectNeighbors( best, NeighborScratch );
			foreach ( var neighbor in NeighborScratch )
			{
				if ( Finalized.Contains( neighbor ) || !Tentative.ContainsKey( neighbor ) )
					continue;

				var offered = OfferedSupport( best, bestSupport, neighbor );
				if ( offered > Tentative[neighbor] )
					Tentative[neighbor] = offered;
			}
		}

		DoomedScratch.Clear();
		foreach ( var entry in Tentative )
		{
			var piece = entry.Key;
			if ( !piece.IsValid() )
				continue;

			piece.Support = entry.Value;
			var material = BuildPieceCatalog.GetMaterialForPiece( piece.PieceId );
			if ( entry.Value < material.MinSupport )
				DoomedScratch.Add( piece );
		}

		CollapseDoomed( scene, DoomedScratch );
	}

	static float OfferedSupport( BuildPiece parent, float parentSupport, BuildPiece child )
	{
		var childMaterial = BuildPieceCatalog.GetMaterialForPiece( child.PieceId );
		var toChild = child.GameObject.WorldPosition - parent.GameObject.WorldPosition;

		// Support from below is the cheap direction; sideways and hanging both pay horizontal loss.
		var upness = toChild.Length > 0.001f ? Math.Clamp( toChild.Normal.z, 0f, 1f ) : 1f;
		var loss = childMaterial.HorizontalLoss
		           + (childMaterial.VerticalLoss - childMaterial.HorizontalLoss) * upness;

		// Fixed per-piece hop, not real distance — wood: ×0.7375 per piece straight up (8 total
		// from a grounded piece), ×0.58 sideways (anchor + 4 out), whatever the pieces' sizes.
		var carried = parentSupport * (1f - loss * HopCostMeters);
		return Math.Clamp( carried, 0f, childMaterial.MaxSupport );
	}

	static void CollapseDoomed( Scene scene, List<BuildPiece> doomed )
	{
		if ( doomed.Count == 0 )
			return;

		// Bottom-up: destruction starts where the support was lost and climbs the structure.
		var queue = new List<BuildPiece>( doomed );
		doomed.Clear();
		queue.Sort( ( a, b ) =>
		{
			var az = a.IsValid() ? a.GameObject.WorldPosition.z : float.MaxValue;
			var bz = b.IsValid() ? b.GameObject.WorldPosition.z : float.MaxValue;
			return az.CompareTo( bz );
		} );

		// The first piece pops synchronously — a freshly placed unsupported piece must be gone
		// before BuildAuthority decides its hammer swing was free.
		HostDestroyCollapsed( scene, queue[0] );
		Log.Info( $"[BuildStructuralIntegrity] {queue.Count} piece(s) collapsing." );

		if ( queue.Count > 1 )
		{
			queue.RemoveAt( 0 );
			GetCollapseRunner( scene )?.Enqueue( queue );
		}
	}

	/// <summary>Destroy one collapsed piece with the bookkeeping every removal needs.</summary>
	public static void HostDestroyCollapsed( Scene scene, BuildPiece piece )
	{
		if ( piece is null || !piece.IsValid() )
			return;

		var bounds = piece.GameObject.GetBounds();
		piece.GameObject.Destroy();
		GroundedCache.Remove( piece );
		if ( scene.IsValid() )
			BuildNavMeshSync.OnBuildPieceBoundsChanged( scene, bounds );

		BuildSnapPlacement.InvalidatePieceCache();
	}

	static BuildCollapseRunner GetCollapseRunner( Scene scene )
	{
		if ( !scene.IsValid() )
			return null;

		foreach ( var runner in scene.GetAllComponents<BuildCollapseRunner>() )
		{
			if ( runner is not null && runner.IsValid() )
				return runner;
		}

		var go = new GameObject( true, "build_collapse_runner" );
		return go.Components.Create<BuildCollapseRunner>();
	}
}
