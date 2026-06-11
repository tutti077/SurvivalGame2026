using System;
using System.Collections.Generic;

namespace Survival;

/// <summary>Default placing-anchor order when Q/E cycles on a locked built snap.</summary>
static class BuildSnapAutoRules
{
	static readonly BuildSnapRole[] BottomWallAnchors =
	{
		BuildSnapRole.CornerSouthWest,
		BuildSnapRole.CornerSouthEast,
	};

	static readonly BuildSnapRole[] TopWallAnchors =
	{
		BuildSnapRole.CornerNorthWest,
		BuildSnapRole.CornerNorthEast,
	};

	public static bool IsAnchorAllowed(
		string placingPieceId,
		string targetPieceId,
		BuildSnapRole anchorRole,
		BuildSnapRole targetRole,
		bool isEdgeSnap ) => true;

	public static int GetAnchorPriorityIndex(
		string placingPieceId,
		string targetPieceId,
		BuildSnapRole anchorRole,
		BuildSnapRole targetRole,
		bool isEdgeSnap )
	{
		if ( isEdgeSnap )
			return 0;

		if ( IsWall( placingPieceId ) && IsWall( targetPieceId ) )
		{
			if ( IsTopWallRole( targetRole ) )
				return GetRoleIndex( anchorRole, BottomWallAnchors );

			return GetRoleIndex( anchorRole, TopWallAnchors );
		}

		if ( IsWall( placingPieceId ) && IsFloor( targetPieceId ) )
			return GetRoleIndex( anchorRole, BottomWallAnchors );

		if ( IsWall( placingPieceId ) && IsRoof( targetPieceId ) )
			return GetRoleIndex( anchorRole, BottomWallAnchors );

		if ( IsRoof( placingPieceId ) && IsWall( targetPieceId ) )
			return GetRoleIndex( anchorRole, TopWallAnchors );

		if ( IsRoof( placingPieceId ) && IsFloor( targetPieceId ) )
			return GetRoleIndex( anchorRole, BottomWallAnchors );

		if ( IsFloor( placingPieceId ) && IsRoof( targetPieceId ) )
			return GetRoleIndex( anchorRole, TopWallAnchors );

		if ( IsFloor( placingPieceId ) && IsFloor( targetPieceId ) )
			return (int)anchorRole;

		if ( IsRoof( placingPieceId ) && IsRoof( targetPieceId ) )
			return (int)anchorRole;

		if ( IsFloor( placingPieceId ) && IsWall( targetPieceId ) )
		{
			if ( IsBottomWallRole( targetRole ) )
				return GetRoleIndex( anchorRole, TopWallAnchors );

			return (int)anchorRole;
		}

		return (int)anchorRole;
	}

	public static float ScoreAnchorPriority( int priorityIndex ) => priorityIndex * 6f;

	static bool IsTopWallRole( BuildSnapRole role ) =>
		role is BuildSnapRole.CornerNorthEast or BuildSnapRole.CornerNorthWest;

	static bool IsBottomWallRole( BuildSnapRole role ) =>
		role is BuildSnapRole.CornerSouthEast or BuildSnapRole.CornerSouthWest;

	static int GetRoleIndex( BuildSnapRole role, IReadOnlyList<BuildSnapRole> priority )
	{
		for ( var i = 0; i < priority.Count; i++ )
		{
			if ( priority[i] == role )
				return i;
		}

		return priority.Count + (int)role;
	}

	static bool IsWall( string pieceId ) =>
		string.Equals( pieceId, "wall", StringComparison.OrdinalIgnoreCase );

	static bool IsFloor( string pieceId ) =>
		string.Equals( pieceId, "foundation", StringComparison.OrdinalIgnoreCase );

	static bool IsRoof( string pieceId ) =>
		string.Equals( pieceId, "45roof", StringComparison.OrdinalIgnoreCase );
}
