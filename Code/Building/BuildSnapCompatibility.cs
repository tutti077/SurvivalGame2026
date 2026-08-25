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

		if ( BuildSnapLayout.IsFoldRole( anchorRole ) || BuildSnapLayout.IsFoldRole( targetRole ) )
			return FoldCanConnect( anchorRole, targetRole );

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

	/// <summary>Both wall lips against a deck edge — stand on it or hang under it, aim side decides.</summary>
	static readonly SnapEdgeId[] WallDeckLips =
	{
		SnapEdgeId.South,
		SnapEdgeId.North,
	};

	static readonly SnapEdgeId[] LevelLips =
	{
		SnapEdgeId.South,
		SnapEdgeId.North,
	};

	/// <summary>
	/// Roof onto a wall top, eave first. <see cref="LevelLips"/> is ridge-first, which is what this
	/// case used to be handed even though its own comment said the eave should lead — so Q/E index 0
	/// seated the high lip on the wall and the roof reared up instead of sitting on it. Ordered from
	/// <see cref="RoofBottomLip"/> / <see cref="RoofTopLip"/> so it tracks them if the pitch changes.
	/// </summary>
	static readonly SnapEdgeId[] RoofOnWallLips =
	{
		SnapEdgeId.North,   // RoofBottomLip — low eave, sits on the wall top
		SnapEdgeId.South,   // RoofTopLip — ridge, still reachable by cycling
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
			// Both lips are offered and the aim-side score in BuildSnapPlacement picks: stood on the
			// deck when you are above it, hung beneath when you are under it. Deciding here instead
			// meant this pair answered the question differently from every other pair.
			return WallDeckLips;
		}

		if ( IsFloor( placingPieceId ) && IsWall( targetPieceId ) )
		{
			var placingEdge = BuildSnapEdge.GetOpposite( targetEdge );
			return IsWallFloorEdgePair( placingEdge, targetEdge ) ? new[] { placingEdge } : Empty;
		}

		if ( IsRoof( placingPieceId ) && IsWall( targetPieceId ) )
		{
			// Only the wall's top edge carries a roof.
			if ( targetEdge != SnapEdgeId.North )
				return Empty;

			return RoofOnWallLips;
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
		// A wall's lip against a deck is chosen by which side of the deck you are on, not by which
		// letter the seam happens to carry — the two pieces do not share an edge frame.
		if ( IsWall( placingPieceId ) && IsFloor( targetPieceId ) )
			return false;

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
		// Wall on a floor edge: the bottom lip always works. The top lip is only honest when the
		// whole wall ends up under the deck — that is the deliberate build-downward case. Anywhere
		// else it buries half the wall in the floor.
		if ( IsWall( placingPieceId ) && IsFloor( targetPieceId ) && placingEdge.Id != SnapEdgeId.South )
		{
			if ( placingEdge.Id != SnapEdgeId.North || targetPiece is null || !targetPiece.IsValid() )
				return false;

			return placement.Position.z < targetPiece.GameObject.WorldPosition.z;
		}

		// Roof↔floor: allow sit-above and hang-down; aim scoring picks which.
		return true;
	}

	/// <summary>High ridge lip after prefab pitch (−45° X, local South edge at +Z).</summary>
	public static SnapEdgeId RoofTopLip => SnapEdgeId.South;

	/// <summary>Low eave lip after prefab pitch (local North edge at −Z).</summary>
	public static SnapEdgeId RoofBottomLip => SnapEdgeId.North;

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

	static bool FoldCanConnect( BuildSnapRole a, BuildSnapRole b )
	{
		if ( BuildSnapLayout.IsFoldRole( a ) && BuildSnapLayout.IsFoldRole( b ) )
		{
			return a switch
			{
				BuildSnapRole.Fold0 => b == BuildSnapRole.Fold2,
				BuildSnapRole.Fold1 => b == BuildSnapRole.Fold3,
				BuildSnapRole.Fold2 => b == BuildSnapRole.Fold0,
				BuildSnapRole.Fold3 => b == BuildSnapRole.Fold1,
				_ => false,
			};
		}

		var fold = BuildSnapLayout.IsFoldRole( a ) ? a : b;
		var plate = BuildSnapLayout.IsFoldRole( a ) ? b : a;
		if ( !IsPlateCorner( plate ) )
			return false;

		return fold switch
		{
			BuildSnapRole.Fold0 => plate == BuildSnapRole.CornerSouthWest,
			BuildSnapRole.Fold1 => plate == BuildSnapRole.CornerSouthEast,
			BuildSnapRole.Fold2 => plate == BuildSnapRole.CornerNorthEast,
			BuildSnapRole.Fold3 => plate == BuildSnapRole.CornerNorthWest,
			_ => false,
		};
	}

	static bool IsPlateCorner( BuildSnapRole role ) =>
		role is BuildSnapRole.CornerNorthEast
			or BuildSnapRole.CornerNorthWest
			or BuildSnapRole.CornerSouthEast
			or BuildSnapRole.CornerSouthWest;
}
