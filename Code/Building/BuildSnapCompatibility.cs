using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

static class BuildSnapCompatibility
{
	static readonly SnapEdgeId[] Empty = Array.Empty<SnapEdgeId>();

	public static bool CanConnect( BuildSnapRole anchorRole, BuildSnapRole targetRole )
	{
		if ( anchorRole == BuildSnapRole.Unknown || targetRole == BuildSnapRole.Unknown )
			return false;

		return anchorRole switch
		{
			BuildSnapRole.CornerNorthEast => targetRole == BuildSnapRole.CornerSouthWest,
			BuildSnapRole.CornerSouthWest => targetRole == BuildSnapRole.CornerNorthEast,
			BuildSnapRole.CornerNorthWest => targetRole == BuildSnapRole.CornerSouthEast,
			BuildSnapRole.CornerSouthEast => targetRole == BuildSnapRole.CornerNorthWest,
			_ => false,
		};
	}

	public static bool PrefersEdgeOnly( string placingPieceId, string targetPieceId ) =>
		IsRoof( placingPieceId ) && IsRoof( targetPieceId );

	public static IReadOnlyList<SnapEdgeId> GetPlacingEdgesForTarget(
		string placingPieceId,
		string targetPieceId,
		SnapEdgeId targetEdge,
		BuildPiece targetPiece,
		Vector3 rayOrigin,
		Vector3 rayDirection )
	{
		if ( string.IsNullOrWhiteSpace( placingPieceId ) || string.IsNullOrWhiteSpace( targetPieceId ) )
			return Empty;

		if ( IsWall( placingPieceId ) && IsFloor( targetPieceId ) )
			return new[] { SnapEdgeId.South };

		if ( IsFloor( placingPieceId ) && IsWall( targetPieceId ) )
		{
			var placingEdge = BuildSnapEdge.GetOpposite( targetEdge );
			return IsWallFloorEdgePair( placingEdge, targetEdge ) ? new[] { placingEdge } : Empty;
		}

		if ( IsRoof( placingPieceId ) && IsWall( targetPieceId ) )
		{
			if ( targetEdge != SnapEdgeId.North )
				return Empty;

			return new[] { SnapEdgeId.South, SnapEdgeId.North };
		}

		if ( IsRoof( placingPieceId ) && IsFloor( targetPieceId ) )
			return new[] { BuildSnapEdge.GetOpposite( targetEdge ) };

		if ( IsWall( placingPieceId ) && IsRoof( targetPieceId ) )
			return new[] { BuildSnapEdge.GetOpposite( targetEdge ) };

		if ( IsSameEdgeFamily( placingPieceId, targetPieceId ) )
			return new[] { BuildSnapEdge.GetOpposite( targetEdge ) };

		return Empty;
	}

	public static bool UsesSameEdgeAlignment(
		string placingPieceId,
		string targetPieceId,
		SnapEdgeId placingEdge,
		SnapEdgeId targetEdge,
		BuildPiece targetPiece )
	{
		if ( IsWall( placingPieceId ) && IsFloor( targetPieceId ) )
			return targetEdge == SnapEdgeId.South && placingEdge == SnapEdgeId.South;

		return IsRoof( placingPieceId )
		       && IsWall( targetPieceId )
		       && targetEdge == SnapEdgeId.North
		       && placingEdge == SnapEdgeId.North;
	}

	public static bool IsValidEdgePlacement(
		string placingPieceId,
		string targetPieceId,
		SnapEdge placingEdge,
		SnapEdge targetEdge,
		Transform placement,
		BuildPiece targetPiece ) => true;

	public static SnapEdgeId GetPreferredRoofOnWallPlacingEdge(
		BuildPiece wall,
		Vector3 rayOrigin,
		Vector3 rayDirection ) =>
		IsLookingAboveWall( wall, rayOrigin, rayDirection )
			? SnapEdgeId.South
			: SnapEdgeId.North;

	static bool IsLookingAboveWall( BuildPiece wall, Vector3 rayOrigin, Vector3 rayDirection )
	{
		if ( wall is null || !wall.IsValid() )
			return rayDirection.z > 0.15f;

		var topZ = GetWallTopZ( wall );
		return rayOrigin.z > topZ - 8f && rayDirection.z > 0.05f;
	}

	static float GetWallTopZ( BuildPiece wall )
	{
		var scale = BuildModuleDimensions.GetPieceLocalScale( wall.PieceId );
		var half = BuildColliderSnap.PrefabColliderSize * 0.5f;
		var rot = wall.GameObject.WorldRotation;
		var top = float.MinValue;
		foreach ( var role in new[] { BuildSnapRole.CornerNorthWest, BuildSnapRole.CornerNorthEast } )
		{
			var z = wall.GameObject.WorldPosition.z
			        + BuildColliderSnap.GetCornerSnapWorldOffset( wall.PieceId, role, rot, scale, half ).z;
			top = Math.Max( top, z );
		}

		return top;
	}

	static bool IsWallFloorEdgePair( SnapEdgeId placingEdge, SnapEdgeId targetEdge ) =>
		( targetEdge, placingEdge ) switch
		{
			( SnapEdgeId.North, SnapEdgeId.South ) => true,
			( SnapEdgeId.South, SnapEdgeId.North ) => true,
			( SnapEdgeId.East, SnapEdgeId.West ) => true,
			( SnapEdgeId.West, SnapEdgeId.East ) => true,
			_ => false,
		};

	static bool IsSameEdgeFamily( string placingPieceId, string targetPieceId ) =>
		( IsFloor( placingPieceId ) && IsFloor( targetPieceId ) )
		|| ( IsWall( placingPieceId ) && IsWall( targetPieceId ) )
		|| ( IsRoof( placingPieceId ) && IsRoof( targetPieceId ) );

	static bool IsWall( string pieceId ) =>
		string.Equals( pieceId, "wall", StringComparison.OrdinalIgnoreCase );

	static bool IsFloor( string pieceId ) =>
		string.Equals( pieceId, "foundation", StringComparison.OrdinalIgnoreCase );

	static bool IsRoof( string pieceId ) =>
		string.Equals( pieceId, "45roof", StringComparison.OrdinalIgnoreCase );
}
