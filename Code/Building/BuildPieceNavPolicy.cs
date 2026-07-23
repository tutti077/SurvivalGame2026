using System;

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

		if ( pieceId.Contains( "stair", StringComparison.OrdinalIgnoreCase )
		     || pieceId.Contains( "ramp", StringComparison.OrdinalIgnoreCase )
		     || pieceId.Contains( "bridge", StringComparison.OrdinalIgnoreCase )
		     || pieceId.Contains( "gate", StringComparison.OrdinalIgnoreCase )
		     || string.Equals( pieceId, "45roof", StringComparison.OrdinalIgnoreCase ) )
			return BuildNavCategory.WalkablePath;

		return BuildNavCategory.Blocking;
	}

	public static BBox ExpandForLocalBake( BBox bounds ) =>
		new( bounds.Mins - new Vector3( LocalBakePadding, LocalBakePadding, LocalBakePadding ),
			bounds.Maxs + new Vector3( LocalBakePadding, LocalBakePadding, LocalBakePadding ) );
}
