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
		|| ( IsRoof( placingPieceId ) && IsFloor( targetPieceId ) )
		|| ( IsFloor( placingPieceId ) && IsRoof( targetPieceId ) )
		|| IsSameEdgeFamily( placingPieceId, targetPieceId );

	public static Rotation GetEdgeSnapYaw( BuildPiece targetPiece, float scrollYawDegrees )
	{
		if ( targetPiece is null || !targetPiece.IsValid() )
			return Rotation.FromYaw( scrollYawDegrees );

		var targetYaw = targetPiece.GameObject.WorldRotation.Angles().yaw;
		var delta = DeltaDegrees( scrollYawDegrees, targetYaw );
		// AwayFromZero: banker's rounding sent an exact 45° half-step back to 0, so the first
		// scroll tick past halfway did nothing and the next appeared to jump a full quarter turn.
		var steps = MathF.Round( delta / 90f, MidpointRounding.AwayFromZero );
		return Rotation.FromYaw( targetYaw + steps * 90f );
	}

	/// <summary>
	/// How far the scroll yaw sits from the target's 90° grid. A flush edge mate only exists on that
	/// grid, so callers use this to tell "player wants the piece square with this one" from
	/// "player is deliberately angling it out" instead of rounding the intent away.
	/// </summary>
	public static float OffGridYawDegrees( BuildPiece targetPiece, float scrollYawDegrees )
	{
		if ( targetPiece is null || !targetPiece.IsValid() )
			return 0f;

		var targetYaw = targetPiece.GameObject.WorldRotation.Angles().yaw;
		var delta = DeltaDegrees( scrollYawDegrees, targetYaw );
		var steps = MathF.Round( delta / 90f, MidpointRounding.AwayFromZero );
		return Math.Abs( delta - steps * 90f );
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
		var orientedRot = alignedYaw;
		var scale = BuildModuleDimensions.GetPieceLocalScale( placingPieceId );
		var colliderHalf = BuildColliderSnap.GetColliderHalfForPiece( placingPieceId );

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

	/// <summary>Signed shortest angle from <paramref name="b"/> to <paramref name="a"/>, in [-180, 180].</summary>
	public static float DeltaDegrees( float a, float b )
	{
		var delta = ( a - b ) % 360f;
		if ( delta > 180f )
			delta -= 360f;
		if ( delta < -180f )
			delta += 360f;

		return delta;
	}

	static bool IsSameEdgeFamily( string placingPieceId, string targetPieceId ) =>
		BuildPieceFamily.IsSameFamily( placingPieceId, targetPieceId );

	static bool IsWall( string pieceId ) => BuildPieceFamily.IsWall( pieceId );

	static bool IsFloor( string pieceId ) => BuildPieceFamily.IsFloor( pieceId );

	/// <summary>Stairs align on edges like roofs do — see BuildPieceFamily.IsRampLike.</summary>
	static bool IsRoof( string pieceId ) => BuildPieceFamily.IsRampLike( pieceId );
}
