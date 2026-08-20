using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

static class BuildSnapCompatibility
{
	static readonly SnapEdgeId[] Empty = Array.Empty<SnapEdgeId>();

	/// <summary>
	/// The natural mate for a pair of snaps. This is a <b>preference</b>, not a gate: it decides
	/// which corner leads the Q/E cycle and therefore what auto-placement picks, while every other
	/// corner stays reachable by cycling.
	/// </summary>
	public static bool CanConnect( BuildSnapRole anchorRole, BuildSnapRole targetRole )
	{
		if ( anchorRole == BuildSnapRole.Unknown || targetRole == BuildSnapRole.Unknown )
			return false;

		// A beam end mates the opposite end of another beam (stacking posts, chaining rails) and
		// any plate corner, so a post can carry a floor or wall corner.
		if ( BuildSnapLayout.IsAxisRole( anchorRole ) || BuildSnapLayout.IsAxisRole( targetRole ) )
		{
			if ( BuildSnapLayout.IsAxisRole( anchorRole ) && BuildSnapLayout.IsAxisRole( targetRole ) )
				return anchorRole != targetRole;

			return true;
		}

		return anchorRole switch
		{
			BuildSnapRole.CornerNorthEast => targetRole == BuildSnapRole.CornerSouthWest,
			BuildSnapRole.CornerSouthWest => targetRole == BuildSnapRole.CornerNorthEast,
			BuildSnapRole.CornerNorthWest => targetRole == BuildSnapRole.CornerSouthEast,
			BuildSnapRole.CornerSouthEast => targetRole == BuildSnapRole.CornerNorthWest,
			_ => false,
		};
	}

	static readonly SnapEdgeId[] AllThinEdges =
	{
		SnapEdgeId.North,
		SnapEdgeId.South,
		SnapEdgeId.East,
		SnapEdgeId.West,
	};

	static readonly SnapEdgeId[] BottomLipOnly =
	{
		SnapEdgeId.South,
	};

	static readonly SnapEdgeId[] LevelLips =
	{
		SnapEdgeId.South,
		SnapEdgeId.North,
	};

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

		// Beams have two end points, not edges — they snap point-to-point via CanConnect only.
		if ( BuildSnapLayout.UsesAxisEnds( placingPieceId ) || BuildSnapLayout.UsesAxisEnds( targetPieceId ) )
			return Empty;

		if ( IsWall( placingPieceId ) && IsFloor( targetPieceId ) )
		{
			// Bottom lip only — North (top) on a floor edge sinks the wall through the deck.
			return BottomLipOnly;
		}

		if ( IsFloor( placingPieceId ) && IsWall( targetPieceId ) )
		{
			var placingEdge = BuildSnapEdge.GetOpposite( targetEdge );
			return IsWallFloorEdgePair( placingEdge, targetEdge ) ? new[] { placingEdge } : Empty;
		}

		if ( IsRoof( placingPieceId ) && IsWall( targetPieceId ) )
		{
			if ( targetEdge != SnapEdgeId.North )
				return Empty;

			// Bottom lip first so default / Q/E index 0 seats the eave on the wall top.
			return LevelLips;
		}

		if ( IsRoof( placingPieceId ) && IsFloor( targetPieceId ) )
		{
			// Pitch makes North/South the level lips. Both so we can hang below or sit above.
			return LevelLips;
		}

		if ( IsFloor( placingPieceId ) && IsRoof( targetPieceId ) )
		{
			// Snap floors onto roof lips (ridge / eave).
			return LevelLips;
		}

		if ( IsWall( placingPieceId ) && IsRoof( targetPieceId ) )
			return new[] { BuildSnapEdge.GetOpposite( targetEdge ) };

		if ( IsRoof( placingPieceId ) && IsRoof( targetPieceId ) )
		{
			// Upward + downward roof chains: try every lip; TryAlignToEdge keeps fits.
			return AllThinEdges;
		}

		if ( IsSameEdgeFamily( placingPieceId, targetPieceId ) )
		{
			// All lips so Q/E can cycle held corners/edges on the locked target seam.
			return AllThinEdges;
		}

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
			return placingEdge == SnapEdgeId.South;

		if ( IsRoof( placingPieceId ) && IsFloor( targetPieceId ) )
			return false;

		if ( IsFloor( placingPieceId ) && IsRoof( targetPieceId ) )
			return false;

		// Roof↔roof: same lip = hang/extend along the slope; opposite = flush abut.
		if ( IsRoof( placingPieceId ) && IsRoof( targetPieceId ) )
			return placingEdge == targetEdge;

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
		BuildPiece targetPiece )
	{
		// Wall top-lip on a floor edge buries half the wall in the deck — never allow it.
		if ( IsWall( placingPieceId ) && IsFloor( targetPieceId ) && placingEdge.Id != SnapEdgeId.South )
			return false;

		// Roof↔floor: allow sit-above and hang-down; aim scoring picks which.
		return true;
	}

	/// <summary>
	/// Lower is better — used to pick among several yaw fits that all mate an edge.
	/// When <paramref name="preferHangDown"/>, prefer plates hanging below the floor edge.
	/// </summary>
	public static float ScoreRoofElevation(
		string roofPieceId,
		Transform roofPlacement,
		BuildPiece targetPiece,
		bool preferHangDown = false )
	{
		if ( targetPiece is null || !targetPiece.IsValid() )
			return 0f;

		// Roof↔roof: no above/below bias — downward chains must score fairly.
		if ( IsRoof( targetPiece.PieceId ) )
			return 0f;

		var floorZ = targetPiece.GameObject.WorldPosition.z;
		var centerZ = roofPlacement.Position.z;
		var avgZ = GetSnapCornersAverageWorldZ( roofPieceId, roofPlacement );

		if ( preferHangDown )
		{
			// Prefer center below the deck (raised-lip mate); penalize sit-above.
			var abovePenalty = centerZ >= floorZ - BuildModuleDimensions.SnapThinHalfUnits ? 500f : 0f;
			return abovePenalty + ( avgZ - floorZ ) + ( centerZ - floorZ );
		}

		// Strongly prefer center above the floor; then prefer higher average.
		var belowPenalty = centerZ < floorZ ? 500f : 0f;
		return belowPenalty - ( avgZ - floorZ ) - ( centerZ - floorZ );
	}

	/// <summary>Ridge / upper lip — mates a floor edge so the plate hangs downward.</summary>
	public static SnapEdgeId RoofTopLip => SnapEdgeId.North;

	public static float GetSnapCornersAverageWorldZ( string pieceId, Transform placement )
	{
		var sum = 0f;
		var count = 0;
		foreach ( var role in CornerRoles )
		{
			sum += GetCornerWorldZ( pieceId, placement, role );
			count++;
		}

		return count > 0 ? sum / count : placement.Position.z;
	}

	static readonly BuildSnapRole[] CornerRoles =
	{
		BuildSnapRole.CornerNorthEast,
		BuildSnapRole.CornerNorthWest,
		BuildSnapRole.CornerSouthEast,
		BuildSnapRole.CornerSouthWest,
	};

	static float GetCornerWorldZ( string pieceId, Transform placement, BuildSnapRole role )
	{
		var orientedRot = placement.Rotation * BuildModuleDimensions.GetPrefabLocalRotation( pieceId );
		var scale = BuildModuleDimensions.GetPieceLocalScale( pieceId );
		var half = BuildColliderSnap.PrefabColliderSize * 0.5f;
		return placement.Position.z
		       + BuildColliderSnap.GetCornerSnapWorldOffset( pieceId, role, orientedRot, scale, half ).z;
	}

	/// <summary>Lower eave lip for pitched roofs (North = ridge / upper lip).</summary>
	public static SnapEdgeId RoofBottomLip => SnapEdgeId.South;

	static bool IsWallFloorEdgePair( SnapEdgeId placingEdge, SnapEdgeId targetEdge ) =>
		( targetEdge, placingEdge ) switch
		{
			( SnapEdgeId.North, SnapEdgeId.South ) => true,
			( SnapEdgeId.South, SnapEdgeId.North ) => true,
			( SnapEdgeId.East, SnapEdgeId.West ) => true,
			( SnapEdgeId.West, SnapEdgeId.East ) => true,
			_ => false,
		};

	public static bool IsSameEdgeFamily( string placingPieceId, string targetPieceId ) =>
		BuildPieceFamily.IsSameFamily( placingPieceId, targetPieceId );

	static bool IsWall( string pieceId ) => BuildPieceFamily.IsWall( pieceId );

	static bool IsFloor( string pieceId ) => BuildPieceFamily.IsFloor( pieceId );

	/// <summary>Stairs share the roof's lip rules — both climb between levels.</summary>
	static bool IsRoof( string pieceId ) => BuildPieceFamily.IsRampLike( pieceId );
}
