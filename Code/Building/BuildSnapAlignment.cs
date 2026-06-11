using System;
using Sandbox;

namespace Survival;

/// <summary>Yaw and corner pairing for edge snaps that must follow a built piece's orientation.</summary>
static class BuildSnapAlignment
{
	public static bool UsesEdgeRelativeYaw( string placingPieceId, string targetPieceId ) =>
		( IsWall( placingPieceId ) && IsFloor( targetPieceId ) )
		|| ( IsFloor( placingPieceId ) && IsWall( targetPieceId ) )
		|| ( IsRoof( placingPieceId ) && IsWall( targetPieceId ) )
		|| ( IsWall( placingPieceId ) && IsRoof( targetPieceId ) )
		|| IsSameEdgeFamily( placingPieceId, targetPieceId );

	public static Rotation GetEdgeSnapYaw( BuildPiece targetPiece, float scrollYawDegrees )
	{
		if ( targetPiece is null || !targetPiece.IsValid() )
			return Rotation.FromYaw( scrollYawDegrees );

		var targetYaw = targetPiece.GameObject.WorldRotation.Angles().yaw;
		var delta = DeltaDegrees( scrollYawDegrees, targetYaw );
		var steps = MathF.Round( delta / 90f );
		return Rotation.FromYaw( targetYaw + steps * 90f );
	}

	public static bool TryFitEdge(
		string placingPieceId,
		SnapEdge placingEdge,
		Vector3 targetWorldA,
		Vector3 targetWorldB,
		Rotation alignedYaw,
		out Transform placement )
	{
		placement = default;
		var orientedRot = alignedYaw * BuildModuleDimensions.GetPrefabLocalRotation( placingPieceId );
		var scale = BuildModuleDimensions.GetPieceLocalScale( placingPieceId );
		var colliderHalf = BuildColliderSnap.PrefabColliderSize * 0.5f;

		var a0 = BuildColliderSnap.GetCornerSnapWorldOffset(
			placingPieceId, placingEdge.CornerA, orientedRot, scale, colliderHalf );
		var a1 = BuildColliderSnap.GetCornerSnapWorldOffset(
			placingPieceId, placingEdge.CornerB, orientedRot, scale, colliderHalf );

		if ( TryFitEdgePair( targetWorldA, targetWorldB, a0, a1, alignedYaw, out placement ) )
			return true;

		return TryFitEdgePair( targetWorldB, targetWorldA, a0, a1, alignedYaw, out placement );
	}

	static bool TryFitEdgePair(
		Vector3 targetA,
		Vector3 targetB,
		Vector3 anchorA,
		Vector3 anchorB,
		Rotation alignedYaw,
		out Transform placement )
	{
		placement = default;
		var pos = targetA - anchorA;
		var mateError = Vector3.DistanceBetween( pos + anchorB, targetB );
		if ( mateError > BuildSnapEdge.EdgeAlignTolerance )
			return false;

		placement = new Transform( pos, alignedYaw );
		return true;
	}

	static float DeltaDegrees( float a, float b )
	{
		var delta = ( a - b ) % 360f;
		if ( delta > 180f )
			delta -= 360f;
		if ( delta < -180f )
			delta += 360f;

		return delta;
	}

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
