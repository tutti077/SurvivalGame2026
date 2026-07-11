using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

public readonly struct BuildSnapCandidate
{
	public bool IsValid { get; init; }
	public bool IsEdgeSnap { get; init; }
	public SnapEdgeId TargetEdgeId { get; init; }
	public Transform Placement { get; init; }
	public BuildPiece TargetPiece { get; init; }
	public int TargetSnapIndex { get; init; }
	public int AnchorSnapIndex { get; init; }
	public float Score { get; init; }
	public BuildSnapGroupKey GroupKey { get; init; }
	public int AnchorPriority { get; init; }
	public int AnchorVariantIndex { get; init; }
	public BuildSnapCrosshair.RayTargetScore RayScore { get; init; }
}

/// <summary>Find snap pairs — nearest target snap/edge to the view ray wins.</summary>
static class BuildSnapPlacement
{
	static readonly List<BuildSnapCandidate> CandidateScratch = new();
	static readonly List<BuildPiece> PieceScratch = new();
	static int _cachedPieceRevision = -1;

	public static IReadOnlyList<BuildSnapCandidate> CollectCandidates(
		BuildPieceData placingData,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Scene scene,
		GameObject ignorePreview,
		Vector3 rayOrigin,
		Vector3 rayDirection,
		float yawDegrees,
		float maxRange )
	{
		CandidateScratch.Clear();
		if ( placingData is null || placingSnaps is null || placingSnaps.Count == 0 || !scene.IsValid() )
			return CandidateScratch;

		var dir = rayDirection.Normal;
		if ( dir.LengthSquared < 1e-8f )
			return CandidateScratch;

		RefreshPieceScratch( scene );
		for ( var p = 0; p < PieceScratch.Count; p++ )
		{
			var targetPiece = PieceScratch[p];
			if ( targetPiece is null || !targetPiece.IsValid() || targetPiece.IsPreviewGhost )
				continue;

			if ( ignorePreview.IsValid() && targetPiece.GameObject == ignorePreview )
				continue;

			CollectEdgeCandidates(
				placingData,
				placingSnaps,
				scene,
				ignorePreview,
				targetPiece,
				rayOrigin,
				dir,
				yawDegrees,
				maxRange );

			CollectCornerCandidates(
				placingData,
				placingSnaps,
				scene,
				ignorePreview,
				targetPiece,
				rayOrigin,
				dir,
				yawDegrees,
				maxRange );
		}

		BuildSnapCandidateGrouper.FinalizeCandidates(
			CandidateScratch,
			placingData.Id,
			placingSnaps );
		return CandidateScratch;
	}

	public static BuildSnapCrosshair.RayTargetScore? GetBestRayScore( IReadOnlyList<BuildSnapCandidate> candidates )
	{
		if ( candidates is null || candidates.Count == 0 )
			return null;

		return candidates[0].RayScore;
	}

	static void CollectEdgeCandidates(
		BuildPieceData placingData,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Scene scene,
		GameObject ignorePreview,
		BuildPiece targetPiece,
		Vector3 rayOrigin,
		Vector3 rayDir,
		float yawDegrees,
		float maxRange )
	{
		for ( var ei = 0; ei < BuildSnapEdge.ThinPlaneEdges.Length; ei++ )
		{
			var targetEdge = BuildSnapEdge.ThinPlaneEdges[ei];
			var placingEdgeIds = BuildSnapCompatibility.GetPlacingEdgesForTarget(
				placingData.Id,
				targetPiece.PieceId,
				targetEdge.Id,
				targetPiece,
				rayOrigin,
				rayDir );
			if ( placingEdgeIds.Count == 0 )
				continue;

			var t0 = targetPiece.GetSnapWorldTransform( FindSnap( targetPiece, targetEdge.CornerA ) );
			var t1 = targetPiece.GetSnapWorldTransform( FindSnap( targetPiece, targetEdge.CornerB ) );

			for ( var pi = 0; pi < placingEdgeIds.Count; pi++ )
			{
				if ( !BuildSnapEdge.TryGetEdge( placingEdgeIds[pi], out var placingEdge ) )
					continue;

				var sameEdge = BuildSnapCompatibility.UsesSameEdgeAlignment(
					placingData.Id,
					targetPiece.PieceId,
					placingEdge.Id,
					targetEdge.Id,
					targetPiece );

				if ( !TryAlignToEdge(
					     placingData.Id,
					     placingEdge,
					     targetPiece,
					     targetEdge,
					     t0.Position,
					     t1.Position,
					     yawDegrees,
					     sameEdge,
					     out var placement ) )
					continue;

				if ( !BuildSnapCompatibility.IsValidEdgePlacement(
					     placingData.Id,
					     targetPiece.PieceId,
					     placingEdge,
					     targetEdge,
					     placement,
					     targetPiece ) )
					continue;

				var builtReach = BuildSnapCrosshair.ScoreSegment(
					rayOrigin,
					rayDir,
					t0.Position,
					t1.Position,
					maxRange );
				var score = ScorePlacingEdge(
					placingData.Id,
					placingEdge,
					placement,
					rayOrigin,
					rayDir,
					maxRange );
				if ( !builtReach.IsValid && !score.IsValid )
					continue;

				if ( IsRoof( placingData.Id ) && IsWall( targetPiece.PieceId ) )
				{
					var preferred = BuildSnapCompatibility.GetPreferredRoofOnWallPlacingEdge(
						targetPiece,
						rayOrigin,
						rayDir );
					score = score with
					{
						Combined = score.Combined + ( placingEdge.Id == preferred ? -14f : 14f ),
					};
				}

				if ( IsRoof( placingData.Id ) && IsFloor( targetPiece.PieceId ) )
				{
					var elev = BuildSnapCompatibility.ScoreRoofElevation(
						placingData.Id,
						placement,
						targetPiece );
					score = score with { Combined = score.Combined + elev };
				}

				TryAddCandidate(
					placingData.Id,
					scene,
					ignorePreview,
					targetPiece,
					placingSnaps,
					placingEdge.CornerA,
					targetEdge.CornerA,
					placement,
					score,
					isEdgeSnap: true,
					targetEdge.Id );
			}
		}
	}

	static void CollectCornerCandidates(
		BuildPieceData placingData,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Scene scene,
		GameObject ignorePreview,
		BuildPiece targetPiece,
		Vector3 rayOrigin,
		Vector3 rayDir,
		float yawDegrees,
		float maxRange )
	{
		if ( BuildSnapCompatibility.PrefersEdgeOnly( placingData.Id, targetPiece.PieceId ) )
			return;

		var targetSnaps = targetPiece.SnapPoints;
		for ( var targetIndex = 0; targetIndex < targetSnaps.Count; targetIndex++ )
		{
			var targetSnap = targetSnaps[targetIndex];
			var targetWorld = targetPiece.GetSnapWorldTransform( targetSnap );

			for ( var anchorIndex = 0; anchorIndex < placingSnaps.Count; anchorIndex++ )
			{
				var anchorSnap = placingSnaps[anchorIndex];
				if ( !BuildSnapCompatibility.CanConnect( anchorSnap.Role, targetSnap.Role ) )
					continue;

				if ( !TryAlignToSnap(
					     placingData.Id,
					     anchorSnap,
					     targetWorld,
					     targetPiece,
					     yawDegrees,
					     out var placement ) )
					continue;

				var builtReach = BuildSnapCrosshair.ScorePoint(
					rayOrigin,
					rayDir,
					targetWorld.Position,
					maxRange );
				var rayScore = ScorePlacingSnap(
					placingData.Id,
					anchorSnap.Role,
					placement,
					rayOrigin,
					rayDir,
					maxRange );
				if ( !builtReach.IsValid && !rayScore.IsValid )
					continue;

				TryAddCandidate(
					placingData.Id,
					scene,
					ignorePreview,
					targetPiece,
					placingSnaps,
					anchorSnap.Role,
					targetSnap.Role,
					placement,
					rayScore,
					isEdgeSnap: false,
					targetEdge: default );
			}
		}
	}

	static void TryAddCandidate(
		string placingPieceId,
		Scene scene,
		GameObject ignorePreview,
		BuildPiece targetPiece,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		BuildSnapRole anchorRole,
		BuildSnapRole targetRole,
		Transform placement,
		BuildSnapCrosshair.RayTargetScore rayScore,
		bool isEdgeSnap,
		SnapEdgeId targetEdge )
	{
		// Snap points are never consumed — multiple pieces may mate to the same built snaps.
		// Overlap is not used to void snap candidates (ground placement still checks overlap).
		var anchorIndex = FindSnapIndex( placingSnaps, anchorRole );
		var targetIndex = FindSnapIndex( targetPiece.SnapPoints, targetRole );

		// Prefer floor mates when placing walls so perimeter wall tops don't steal interior seams.
		var scoreBias = 0f;
		if ( IsWall( placingPieceId ) && IsFloor( targetPiece.PieceId ) )
			scoreBias -= 25f;
		else if ( IsWall( placingPieceId ) && IsWall( targetPiece.PieceId ) )
			scoreBias += 40f;

		CandidateScratch.Add( new BuildSnapCandidate
		{
			IsValid = true,
			IsEdgeSnap = isEdgeSnap,
			TargetEdgeId = targetEdge,
			Placement = placement,
			TargetPiece = targetPiece,
			TargetSnapIndex = targetIndex,
			AnchorSnapIndex = anchorIndex,
			Score = rayScore.Combined + scoreBias,
			RayScore = rayScore,
		} );
	}

	public static Vector3 GetPlacingSnapWorld(
		string placingPieceId,
		BuildSnapRole role,
		Transform placement )
	{
		var orientedRot = placement.Rotation * BuildModuleDimensions.GetPrefabLocalRotation( placingPieceId );
		var scale = BuildModuleDimensions.GetPieceLocalScale( placingPieceId );
		var half = BuildColliderSnap.PrefabColliderSize * 0.5f;
		return placement.Position + BuildColliderSnap.GetCornerSnapWorldOffset(
			placingPieceId,
			role,
			orientedRot,
			scale,
			half );
	}

	public static Vector3 GetBuiltFocusPoint( BuildSnapCandidate candidate )
	{
		var piece = candidate.TargetPiece;
		if ( piece is null || !piece.IsValid() )
			return default;

		if ( candidate.IsEdgeSnap && BuildSnapEdge.TryGetEdge( candidate.TargetEdgeId, out var edge ) )
		{
			var a = piece.GetSnapWorldTransform( FindSnap( piece, edge.CornerA ) ).Position;
			var b = piece.GetSnapWorldTransform( FindSnap( piece, edge.CornerB ) ).Position;
			return ( a + b ) * 0.5f;
		}

		if ( candidate.TargetSnapIndex < 0 || candidate.TargetSnapIndex >= piece.SnapPoints.Count )
			return default;

		return piece.GetSnapWorldTransform( piece.SnapPoints[candidate.TargetSnapIndex] ).Position;
	}

	public static Vector3 GetMateFocusPoint(
		BuildSnapCandidate candidate,
		string placingPieceId,
		IReadOnlyList<BuildSnapPoint> placingSnaps )
	{
		if ( placingSnaps is null
		     || candidate.AnchorSnapIndex < 0
		     || candidate.AnchorSnapIndex >= placingSnaps.Count )
			return default;

		var anchorRole = placingSnaps[candidate.AnchorSnapIndex].Role;

		if ( candidate.IsEdgeSnap && TryGetEdgeForCorner( anchorRole, out var edge ) )
		{
			var a = GetPlacingSnapWorld( placingPieceId, edge.CornerA, candidate.Placement );
			var b = GetPlacingSnapWorld( placingPieceId, edge.CornerB, candidate.Placement );
			return ( a + b ) * 0.5f;
		}

		return GetPlacingSnapWorld( placingPieceId, anchorRole, candidate.Placement );
	}

	static bool TryGetEdgeForCorner( BuildSnapRole corner, out SnapEdge edge )
	{
		for ( var i = 0; i < BuildSnapEdge.ThinPlaneEdges.Length; i++ )
		{
			var candidate = BuildSnapEdge.ThinPlaneEdges[i];
			if ( candidate.CornerA == corner || candidate.CornerB == corner )
			{
				edge = candidate;
				return true;
			}
		}

		edge = default;
		return false;
	}

	static BuildSnapCrosshair.RayTargetScore ScorePlacingSnap(
		string placingPieceId,
		BuildSnapRole role,
		Transform placement,
		Vector3 rayOrigin,
		Vector3 rayDir,
		float maxRange ) =>
		BuildSnapCrosshair.ScorePoint(
			rayOrigin,
			rayDir,
			GetPlacingSnapWorld( placingPieceId, role, placement ),
			maxRange );

	static BuildSnapCrosshair.RayTargetScore ScorePlacingEdge(
		string placingPieceId,
		SnapEdge placingEdge,
		Transform placement,
		Vector3 rayOrigin,
		Vector3 rayDir,
		float maxRange )
	{
		var a = GetPlacingSnapWorld( placingPieceId, placingEdge.CornerA, placement );
		var b = GetPlacingSnapWorld( placingPieceId, placingEdge.CornerB, placement );
		return BuildSnapCrosshair.ScoreSegment( rayOrigin, rayDir, a, b, maxRange );
	}

	static bool IsRoof( string pieceId ) =>
		string.Equals( pieceId, "45roof", StringComparison.OrdinalIgnoreCase );

	static bool IsFloor( string pieceId ) =>
		string.Equals( pieceId, "foundation", StringComparison.OrdinalIgnoreCase );

	static bool IsWall( string pieceId ) =>
		string.Equals( pieceId, "wall", StringComparison.OrdinalIgnoreCase );

	static BuildSnapPoint FindSnap( BuildPiece piece, BuildSnapRole role )
	{
		var snaps = piece.SnapPoints;
		for ( var i = 0; i < snaps.Count; i++ )
		{
			if ( snaps[i].Role == role )
				return snaps[i];
		}

		return default;
	}

	static int FindSnapIndex( IReadOnlyList<BuildSnapPoint> snaps, BuildSnapRole role )
	{
		for ( var i = 0; i < snaps.Count; i++ )
		{
			if ( snaps[i].Role == role )
				return i;
		}

		return -1;
	}

	static void RefreshPieceScratch( Scene scene )
	{
		var count = 0;
		foreach ( var piece in scene.GetAllComponents<BuildPiece>() )
		{
			if ( piece is not null && piece.IsValid() )
				count++;
		}

		// Count-only revision misses demolish+place swaps; always rebuild when count changes
		// or cache was explicitly invalidated.
		if ( count == _cachedPieceRevision && PieceScratch.Count == count && count > 0 )
		{
			var allValid = true;
			for ( var i = 0; i < PieceScratch.Count; i++ )
			{
				if ( PieceScratch[i] is null || !PieceScratch[i].IsValid() )
				{
					allValid = false;
					break;
				}
			}

			if ( allValid )
				return;
		}

		_cachedPieceRevision = count;
		PieceScratch.Clear();
		foreach ( var piece in scene.GetAllComponents<BuildPiece>() )
		{
			if ( piece is not null && piece.IsValid() )
				PieceScratch.Add( piece );
		}
	}

	public static void InvalidatePieceCache() => _cachedPieceRevision = -1;

	public static bool TryAlignToEdge(
		string placingPieceId,
		SnapEdge placingEdge,
		BuildPiece targetPiece,
		SnapEdge targetEdge,
		Vector3 targetWorldA,
		Vector3 targetWorldB,
		float yawDegrees,
		bool sameEdgeAlignment,
		out Transform placement )
	{
		placement = default;
		if ( targetPiece is null || !targetPiece.IsValid() )
			return false;

		var wallOnFloor = IsWall( placingPieceId ) && IsFloor( targetPiece.PieceId );
		var roofOnFloor = IsRoof( placingPieceId ) && IsFloor( targetPiece.PieceId );
		if ( !wallOnFloor && !roofOnFloor )
		{
			if ( !sameEdgeAlignment && placingEdge.Id != BuildSnapEdge.GetOpposite( targetEdge.Id ) )
				return false;

			if ( sameEdgeAlignment && placingEdge.Id != targetEdge.Id )
				return false;
		}

		if ( BuildSnapAlignment.UsesEdgeRelativeYaw( placingPieceId, targetPiece.PieceId ) )
		{
			// Edge direction defines orientation — try all 90° steps so interior E/W seams
			// still fit when scroll yaw was left on a N/S wall (and vice versa).
			// When several yaws fit (common for pitched roofs), keep the one that sits above
			// the target instead of the first geometric match (often underground).
			var baseYaw = BuildSnapAlignment.GetEdgeSnapYaw( targetPiece, yawDegrees );
			var found = false;
			var bestScore = float.MaxValue;
			for ( var step = 0; step < 4; step++ )
			{
				var alignedYaw = Rotation.FromYaw( baseYaw.Angles().yaw + step * 90f );
				if ( !BuildSnapAlignment.TryFitEdge(
					     placingPieceId,
					     placingEdge,
					     targetWorldA,
					     targetWorldB,
					     alignedYaw,
					     out var candidate ) )
					continue;

				if ( !BuildSnapCompatibility.IsValidEdgePlacement(
					     placingPieceId,
					     targetPiece.PieceId,
					     placingEdge,
					     targetEdge,
					     candidate,
					     targetPiece ) )
					continue;

				var elev = IsRoof( placingPieceId )
					? BuildSnapCompatibility.ScoreRoofElevation( placingPieceId, candidate, targetPiece )
					: 0f;
				// Prefer the scroll-aligned step when elevation ties.
				var score = elev + step * 0.01f;
				if ( !found || score < bestScore )
				{
					placement = candidate;
					bestScore = score;
					found = true;
				}
			}

			return found;
		}

		return BuildSnapAlignment.TryFitEdge(
			placingPieceId,
			placingEdge,
			targetWorldA,
			targetWorldB,
			GetPlacementYaw( yawDegrees ),
			out placement );
	}

	public static bool TryAlignToSnap(
		string placingPieceId,
		BuildSnapPoint anchorSnap,
		Transform targetWorld,
		BuildPiece targetPiece,
		float yawDegrees,
		out Transform placement )
	{
		placement = default;
		var alignedYaw = GetPlacementYaw( yawDegrees );

		var pitch = BuildModuleDimensions.GetPrefabLocalRotation( placingPieceId );
		var orientedRot = alignedYaw * pitch;
		var scale = BuildModuleDimensions.GetPieceLocalScale( placingPieceId );
		var colliderHalf = BuildColliderSnap.PrefabColliderSize * 0.5f;
		var anchorOffset = BuildColliderSnap.GetCornerSnapWorldOffset(
			placingPieceId,
			anchorSnap.Role,
			orientedRot,
			scale,
			colliderHalf );
		var worldPos = targetWorld.Position - anchorOffset;

		placement = new Transform( worldPos, alignedYaw );
		return true;
	}

	static Rotation GetPlacementYaw( float yawDegrees ) => Rotation.FromYaw( yawDegrees );
}
