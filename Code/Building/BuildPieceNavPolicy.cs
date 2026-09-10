using System;
using Sandbox;

namespace Survival;

/// <summary>How a build piece affects nav after a local tile rebake.</summary>
public enum BuildNavCategory
{
	/// <summary>Walls, roofs, chests — carve holes / block traversal on the mesh.</summary>
	Blocking = 0,

	/// <summary>Stairs, ramps, bridges — reshape walkable surfaces on rebake.</summary>
	WalkablePath = 1
}

public static class BuildPieceNavPolicy
{
	const float LocalBakePadding = 160f;

	public static BuildNavCategory GetCategory( string pieceId )
	{
		if ( string.IsNullOrWhiteSpace( pieceId ) )
			return BuildNavCategory.Blocking;

		if ( BuildPieceFamily.IsStairs( pieceId )
		     || BuildPieceFamily.IsRoof( pieceId )
		     || pieceId.Contains( "bridge", StringComparison.OrdinalIgnoreCase )
		     || pieceId.Contains( "gate", StringComparison.OrdinalIgnoreCase ) )
			return BuildNavCategory.WalkablePath;

		return BuildNavCategory.Blocking;
	}

	/// <summary>
	/// Is this object (or an ancestor) a stairs / roof / bridge / gate piece — something entities
	/// walk UP rather than through? Movement clips, path validation and the breach "in the way"
	/// probe must never read a stair riser or a roof edge as a wall.
	/// </summary>
	public static bool IsWalkablePathObject( GameObject go )
	{
		var piece = BuildPlacementUtility.FindBuildPieceOnHierarchy( go );
		return piece is not null && piece.IsValid() && GetCategory( piece.PieceId ) == BuildNavCategory.WalkablePath;
	}

	public static BBox ExpandForLocalBake( BBox bounds ) =>
		new( bounds.Mins - new Vector3( LocalBakePadding, LocalBakePadding, LocalBakePadding ),
			bounds.Maxs + new Vector3( LocalBakePadding, LocalBakePadding, LocalBakePadding ) );
}
