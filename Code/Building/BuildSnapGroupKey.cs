using System;

namespace Survival;

/// <summary>Identifies which built piece attachment Q/E stays locked to.</summary>
public readonly struct BuildSnapGroupKey : IEquatable<BuildSnapGroupKey>
{
	public Guid TargetPieceId { get; init; }
	public bool IsEdgeSnap { get; init; }
	public SnapEdgeId TargetEdgeId { get; init; }
	public BuildSnapRole TargetCornerRole { get; init; }

	public static BuildSnapGroupKey ForEdge( BuildPiece target, SnapEdgeId edgeId ) => new()
	{
		TargetPieceId = target.GameObject.Id,
		IsEdgeSnap = true,
		TargetEdgeId = edgeId,
	};

	public static BuildSnapGroupKey ForCorner( BuildPiece target, BuildSnapRole role ) => new()
	{
		TargetPieceId = target.GameObject.Id,
		IsEdgeSnap = false,
		TargetCornerRole = role,
	};

	public bool Equals( BuildSnapGroupKey other ) =>
		TargetPieceId == other.TargetPieceId
		&& IsEdgeSnap == other.IsEdgeSnap
		&& TargetEdgeId == other.TargetEdgeId
		&& TargetCornerRole == other.TargetCornerRole;

	public override bool Equals( object obj ) => obj is BuildSnapGroupKey other && Equals( other );

	public override int GetHashCode() => HashCode.Combine( TargetPieceId, IsEdgeSnap, TargetEdgeId, TargetCornerRole );
}
