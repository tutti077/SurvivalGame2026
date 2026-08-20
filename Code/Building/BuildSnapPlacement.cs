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
	/// <summary>Q/E order within a locked group (lower first). Always set by the collector.</summary>
	public int CycleOrder { get; init; }
	public BuildSnapGroupKey GroupKey { get; init; }
	/// <summary>
	/// Aim-independent tiebreak for Q/E order. Scores move every frame as the mouse drifts, so
	/// ordering on them re-shuffled the list under a held Q/E index and made cycling skip entries.
	/// </summary>
	public int HoldOrder { get; init; }
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
		Vector3 aimLand,
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

			var hasEdgeSeams = CollectEdgeCandidates(
				placingData,
				placingSnaps,
				scene,
				ignorePreview,
				targetPiece,
				rayOrigin,
				dir,
				aimLand,
				yawDegrees,
				maxRange );

			// Point snaps only matter where no edge seam exists (beams, cross-family pairs). Adding
			// them alongside edge seams would be dead weight: a corner is an endpoint of its own
			// edge, so the edge group always scores at least as well and would always win.
			if ( !hasEdgeSeams )
				CollectPointSnapCandidates(
					placingData,
					placingSnaps,
					scene,
					ignorePreview,
					targetPiece,
					aimLand,
					yawDegrees,
					rayOrigin,
					dir,
					maxRange );
		}

		BuildSnapCandidateGrouper.FinalizeCandidates( CandidateScratch, placingSnaps );
		return CandidateScratch;
	}

	public static BuildSnapCrosshair.RayTargetScore? GetBestRayScore( IReadOnlyList<BuildSnapCandidate> candidates )
	{
		if ( candidates is null || candidates.Count == 0 )
			return null;

		return candidates[0].RayScore;
	}

	/// <summary>Q/E order offset for corner pairs that are not the natural CanConnect mate.</summary>
	const int NonPreferredHoldCycleBase = 8;

	/// <summary>Slack around a target's 90° grid that still counts as "square with it".</summary>
	const float FlushYawToleranceDegrees = 5f;

	/// <summary>Returns true when this pair has any mating edge seam at all.</summary>
	static bool CollectEdgeCandidates(
		BuildPieceData placingData,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Scene scene,
		GameObject ignorePreview,
		BuildPiece targetPiece,
		Vector3 rayOrigin,
		Vector3 rayDir,
		Vector3 aimLand,
		float yawDegrees,
		float maxRange )
	{
		var hasEdgeSeams = false;
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
			var edgeAim = BuildSnapCrosshair.ScoreSegmentToAimLand(
				rayOrigin,
				rayDir,
				aimLand,
				t0.Position,
				t1.Position,
				maxRange );

			// Plates (floors / walls / doors) take the static corner path: a fixed anchor and a fixed
			// list, so scroll only spins the piece. Ramps still run the fitted path below.
			if ( !IsRoof( placingData.Id ) && !IsRoof( targetPiece.PieceId ) )
			{
				if ( !edgeAim.IsValid )
					continue;

				if ( CollectStaticCornerCandidates(
					    placingData,
					    placingSnaps,
					    scene,
					    ignorePreview,
					    targetPiece,
					    targetEdge,
					    t0.Position,
					    t1.Position,
					    edgeAim,
					    aimLand,
					    yawDegrees ) )
					hasEdgeSeams = true;

				continue;
			}

			var bestAbutScore = float.MaxValue;
			var hasAbut = false;
			SnapEdge bestPlacingEdge = default;
			Transform bestPlacement = default;
			BuildSnapCrosshair.RayTargetScore bestScore = default;

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

				var targetZForAim = targetPiece.GameObject.WorldPosition.z;
				var lookingDownRoof = aimLand.z < targetZForAim - 8f || rayDir.z < -0.12f;
				var lookingUpRoof = aimLand.z > targetZForAim + 8f || rayDir.z > 0.12f;
				var preferHangDownOnFloor = IsRoof( placingData.Id )
				                           && IsFloor( targetPiece.PieceId )
				                           && lookingDownRoof;

				if ( !TryAlignToEdge(
					     placingData.Id,
					     placingEdge,
					     targetPiece,
					     targetEdge,
					     t0.Position,
					     t1.Position,
					     yawDegrees,
					     sameEdge,
					     preferHangDownOnFloor,
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

				var score = ScorePlacingEdge(
					placingData.Id,
					placingEdge,
					placement,
					rayOrigin,
					rayDir,
					aimLand,
					maxRange );
				if ( !edgeAim.IsValid && !score.IsValid )
					continue;

				// Prefer the built seam closest to aim-land (slotting), not the ghost placement score.
				if ( edgeAim.IsValid )
					score = edgeAim;

				// Abutting the opposite lip extends the structure; mating the same-named lip lays the
				// piece back over the target. Both can fit, and leaving the tie to the [N,S,E,W] scan
				// order meant a piece joined on one side and overlapped on the mirrored side — that is
				// why a roof took a seam on its right but not its left. Small enough that the roof
				// elevation biases below still decide real preferences.
				score = score with
				{
					Combined = score.Combined
					           + ( placingEdge.Id == BuildSnapEdge.GetOpposite( targetEdge.Id ) ? -1f : 1f ),
				};

				if ( IsRoof( placingData.Id ) && IsRoof( targetPiece.PieceId ) )
				{
					var placeZ = placement.Position.z;
					if ( lookingDownRoof )
						score = score with { Combined = score.Combined + ( placeZ < targetZForAim ? -28f : 28f ) };
					else if ( lookingUpRoof )
						score = score with { Combined = score.Combined + ( placeZ > targetZForAim ? -28f : 28f ) };
				}

				if ( IsRoof( placingData.Id ) && IsWall( targetPiece.PieceId ) )
				{
					var preferred = BuildSnapCompatibility.RoofBottomLip;
					score = score with
					{
						Combined = score.Combined + ( placingEdge.Id == preferred ? -40f : 40f ),
					};
				}

				if ( IsRoof( placingData.Id ) && IsFloor( targetPiece.PieceId ) )
				{
					var placeZ = placement.Position.z;
					var elev = BuildSnapCompatibility.ScoreRoofElevation(
						placingData.Id,
						placement,
						targetPiece,
						preferHangDown: preferHangDownOnFloor );
					// Hang-down mates the ridge (North); sit-above mates the eave (South).
					var preferred = preferHangDownOnFloor
						? BuildSnapCompatibility.RoofTopLip
						: BuildSnapCompatibility.RoofBottomLip;
					var lipBias = placingEdge.Id == preferred ? -24f : 24f;
					var lookBias = 0f;
					if ( preferHangDownOnFloor )
						lookBias = placeZ < targetZForAim ? -28f : 28f;
					else if ( lookingUpRoof )
						lookBias = placeZ > targetZForAim ? -28f : 28f;

					score = score with { Combined = score.Combined + elev + lipBias + lookBias };
				}

				if ( !hasAbut || score.Combined < bestAbutScore )
				{
					hasAbut = true;
					bestAbutScore = score.Combined;
					bestPlacingEdge = placingEdge;
					bestPlacement = placement;
					bestScore = score;
				}
			}

			// No flush mate: either the lips are different lengths or the player scrolled off-axis.
			// Mate corner-to-corner rather than dropping the snap.
			if ( !hasAbut && edgeAim.IsValid
			     && TryPickMatingEdge( placingEdgeIds, targetEdge.Id, out bestPlacingEdge )
			     && TryCornerMateToEdge(
				     placingData.Id,
				     bestPlacingEdge,
				     t0.Position,
				     t1.Position,
				     aimLand,
				     yawDegrees,
				     out bestPlacement ) )
			{
				hasAbut = true;
				bestScore = edgeAim;
			}

			if ( !hasAbut )
				continue;

			// Q/E index 0 = center / auto (best edge abut for this seam).
			TryAddCandidate(
				placingData.Id,
				scene,
				ignorePreview,
				targetPiece,
				placingSnaps,
				bestPlacingEdge.CornerA,
				targetEdge.CornerA,
				bestPlacement,
				bestScore,
				isEdgeSnap: true,
				targetEdge.Id,
				cycleOrder: 0 );

			hasEdgeSeams = true;
			if ( !edgeAim.IsValid )
				continue;

			CollectHoldCornerVariants(
				placingData,
				placingSnaps,
				scene,
				ignorePreview,
				targetPiece,
				targetEdge,
				t0.Position,
				t1.Position,
				edgeAim,
				aimLand,
				yawDegrees );
		}

		return hasEdgeSeams;
	}

	/// <summary>
	/// Corner on the placing piece that meets <paramref name="anchorRole"/> when the two pieces sit
	/// square across the seam — the same corner mirrored across the seam's own axis. Reads only the
	/// seam, never the yaw.
	/// </summary>
	static BuildSnapRole MirrorRoleAcrossEdge( BuildSnapRole anchorRole, SnapEdgeId targetEdge )
	{
		var mirrorEastWest = targetEdge is SnapEdgeId.East or SnapEdgeId.West;
		return anchorRole switch
		{
			BuildSnapRole.CornerNorthEast => mirrorEastWest
				? BuildSnapRole.CornerNorthWest
				: BuildSnapRole.CornerSouthEast,
			BuildSnapRole.CornerNorthWest => mirrorEastWest
				? BuildSnapRole.CornerNorthEast
				: BuildSnapRole.CornerSouthWest,
			BuildSnapRole.CornerSouthEast => mirrorEastWest
				? BuildSnapRole.CornerSouthWest
				: BuildSnapRole.CornerNorthEast,
			BuildSnapRole.CornerSouthWest => mirrorEastWest
				? BuildSnapRole.CornerSouthEast
				: BuildSnapRole.CornerNorthWest,
			_ => anchorRole,
		};
	}

	/// <summary>
	/// The whole Q/E list for a plate seam: one anchored built corner, and every corner of the placing
	/// piece hung from it in a fixed role order. Nothing in here looks at yaw, which is the point —
	/// the fitted path re-derived both the anchor and the corner pairing from the current rotation, so
	/// scrolling silently moved the joint and grew or shrank the list (the 4/4 vs 4/5 flicker).
	/// Rotation now spins the piece about a joint that cannot move.
	/// </summary>
	static bool CollectStaticCornerCandidates(
		BuildPieceData placingData,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Scene scene,
		GameObject ignorePreview,
		BuildPiece targetPiece,
		SnapEdge targetEdge,
		Vector3 targetWorldA,
		Vector3 targetWorldB,
		BuildSnapCrosshair.RayTargetScore edgeAim,
		Vector3 aimLand,
		float yawDegrees )
	{
		// Which end of the seam is anchored follows the crosshair, not the rotation.
		var attachRole = Vector3.DistanceBetween( aimLand, targetWorldB )
		                 < Vector3.DistanceBetween( aimLand, targetWorldA )
			? targetEdge.CornerB
			: targetEdge.CornerA;

		if ( FindSnapIndex( targetPiece.SnapPoints, attachRole ) < 0 )
			return false;

		var attachWorld = targetPiece.GetSnapWorldTransform( FindSnap( targetPiece, attachRole ) );
		var squareRole = MirrorRoleAcrossEdge( attachRole, targetEdge.Id );
		var added = false;

		for ( var i = 0; i < placingSnaps.Count; i++ )
		{
			var anchorSnap = placingSnaps[i];
			if ( anchorSnap.Role == BuildSnapRole.Unknown )
				continue;

			if ( !TryAlignToSnap(
				     placingData.Id,
				     anchorSnap,
				     attachWorld,
				     targetPiece,
				     yawDegrees,
				     out var placement ) )
				continue;

			// The corner that sits square across the seam leads the cycle, so the default placement
			// is the flush one; the rest follow in fixed role order.
			var cycleOrder = anchorSnap.Role == squareRole
				? 0
				: 1 + BuildSnapLayout.GetHoldOrder( anchorSnap.Role );

			TryAddCandidate(
				placingData.Id,
				scene,
				ignorePreview,
				targetPiece,
				placingSnaps,
				anchorSnap.Role,
				attachRole,
				placement,
				edgeAim,
				isEdgeSnap: true,
				targetEdge.Id,
				cycleOrder );

			added = true;
		}

		return added;
	}

	/// <summary>
	/// Lip to mate when no flush fit exists: the one that would have abutted this seam head-on.
	/// </summary>
	static bool TryPickMatingEdge(
		IReadOnlyList<SnapEdgeId> placingEdgeIds,
		SnapEdgeId targetEdge,
		out SnapEdge placingEdge )
	{
		var opposite = BuildSnapEdge.GetOpposite( targetEdge );
		for ( var i = 0; i < placingEdgeIds.Count; i++ )
		{
			if ( placingEdgeIds[i] == opposite )
				return BuildSnapEdge.TryGetEdge( opposite, out placingEdge );
		}

		placingEdge = default;
		return placingEdgeIds.Count > 0 && BuildSnapEdge.TryGetEdge( placingEdgeIds[0], out placingEdge );
	}

	/// <summary>
	/// Q/E hold variants for an aimed seam. <b>One</b> built corner is the attach point for all of
	/// them — whichever end of the seam is nearer the crosshair — so Q/E only changes which corner
	/// of the placing piece lands there. Giving each variant its own aim-picked target corner is
	/// what made cycling look like it was moving the attachment instead of rotating the piece.
	/// </summary>
	static void CollectHoldCornerVariants(
		BuildPieceData placingData,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Scene scene,
		GameObject ignorePreview,
		BuildPiece targetPiece,
		SnapEdge targetEdge,
		Vector3 targetWorldA,
		Vector3 targetWorldB,
		BuildSnapCrosshair.RayTargetScore edgeAim,
		Vector3 aimLand,
		float yawDegrees )
	{
		var attachRole = Vector3.DistanceBetween( aimLand, targetWorldB )
		                 < Vector3.DistanceBetween( aimLand, targetWorldA )
			? targetEdge.CornerB
			: targetEdge.CornerA;

		var targetWorld = targetPiece.GetSnapWorldTransform( FindSnap( targetPiece, attachRole ) );
		for ( var i = 0; i < placingSnaps.Count; i++ )
		{
			var anchorSnap = placingSnaps[i];
			if ( anchorSnap.Role == BuildSnapRole.Unknown )
				continue;

			if ( !TryAlignToSnap(
				     placingData.Id,
				     anchorSnap,
				     targetWorld,
				     targetPiece,
				     yawDegrees,
				     out var placement ) )
				continue;

			// After the flush abut (0), cycle the placing corners in a fixed order.
			TryAddCandidate(
				placingData.Id,
				scene,
				ignorePreview,
				targetPiece,
				placingSnaps,
				anchorSnap.Role,
				attachRole,
				placement,
				edgeAim,
				isEdgeSnap: true,
				targetEdge.Id,
				cycleOrder: 1 + BuildSnapLayout.GetHoldOrder( anchorSnap.Role ) );
		}
	}

	/// <summary>
	/// Point snaps for pairs with no mating edge seam (beams, cross-family). Each built snap is an
	/// attach point and the variants under it hang the placing piece from each of its own snaps.
	/// </summary>
	static void CollectPointSnapCandidates(
		BuildPieceData placingData,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Scene scene,
		GameObject ignorePreview,
		BuildPiece targetPiece,
		Vector3 aimLand,
		float yawDegrees,
		Vector3 rayOrigin,
		Vector3 rayDir,
		float maxRange )
	{
		var targetSnaps = targetPiece.SnapPoints;
		for ( var t = 0; t < targetSnaps.Count; t++ )
		{
			var targetSnap = targetSnaps[t];
			if ( targetSnap.Role == BuildSnapRole.Unknown )
				continue;

			var targetWorld = targetPiece.GetSnapWorldTransform( targetSnap );
			// TryAlignToSnap puts the held snap exactly on the built snap, so scoring the ghost
			// would score this same point — one reach test per built snap covers the whole group.
			var builtReach = BuildSnapCrosshair.ScorePointToAimLand(
				rayOrigin,
				rayDir,
				aimLand,
				targetWorld.Position,
				maxRange );
			if ( !builtReach.IsValid )
				continue;

			for ( var i = 0; i < placingSnaps.Count; i++ )
			{
				var anchorSnap = placingSnaps[i];
				if ( anchorSnap.Role == BuildSnapRole.Unknown )
					continue;

				if ( !TryAlignToSnap(
					     placingData.Id,
					     anchorSnap,
					     targetWorld,
					     targetPiece,
					     yawDegrees,
					     out var placement ) )
					continue;

				// CanConnect is a preference, not a filter: the natural mate leads the cycle so the
				// default pick is unchanged, but every corner stays reachable with Q/E.
				var holdOrder = BuildSnapLayout.GetHoldOrder( anchorSnap.Role );
				var cycleOrder = BuildSnapCompatibility.CanConnect( anchorSnap.Role, targetSnap.Role )
					? holdOrder
					: NonPreferredHoldCycleBase + holdOrder;

				TryAddCandidate(
					placingData.Id,
					scene,
					ignorePreview,
					targetPiece,
					placingSnaps,
					anchorSnap.Role,
					targetSnap.Role,
					placement,
					builtReach,
					isEdgeSnap: false,
					targetEdge: default,
					cycleOrder: cycleOrder );
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
		SnapEdgeId targetEdge,
		int cycleOrder )
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
			CycleOrder = cycleOrder,
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

	static BuildSnapCrosshair.RayTargetScore ScorePlacingEdge(
		string placingPieceId,
		SnapEdge placingEdge,
		Transform placement,
		Vector3 rayOrigin,
		Vector3 rayDir,
		Vector3 aimLand,
		float maxRange )
	{
		var a = GetPlacingSnapWorld( placingPieceId, placingEdge.CornerA, placement );
		var b = GetPlacingSnapWorld( placingPieceId, placingEdge.CornerB, placement );
		return BuildSnapCrosshair.ScoreSegmentToAimLand( rayOrigin, rayDir, aimLand, a, b, maxRange );
	}

	static bool IsRoof( string pieceId ) => BuildPieceFamily.IsRoof( pieceId );

	static bool IsFloor( string pieceId ) => BuildPieceFamily.IsFloor( pieceId );

	static bool IsWall( string pieceId ) => BuildPieceFamily.IsWall( pieceId );

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
		out Transform placement ) =>
		TryAlignToEdge(
			placingPieceId,
			placingEdge,
			targetPiece,
			targetEdge,
			targetWorldA,
			targetWorldB,
			yawDegrees,
			sameEdgeAlignment,
			preferHangDownOnFloor: false,
			out placement );

	public static bool TryAlignToEdge(
		string placingPieceId,
		SnapEdge placingEdge,
		BuildPiece targetPiece,
		SnapEdge targetEdge,
		Vector3 targetWorldA,
		Vector3 targetWorldB,
		float yawDegrees,
		bool sameEdgeAlignment,
		bool preferHangDownOnFloor,
		out Transform placement )
	{
		placement = default;
		if ( targetPiece is null || !targetPiece.IsValid() )
			return false;

		var wallOnFloor = IsWall( placingPieceId ) && IsFloor( targetPiece.PieceId );
		var roofOnFloor = IsRoof( placingPieceId ) && IsFloor( targetPiece.PieceId );
		var floorOnRoof = IsFloor( placingPieceId ) && IsRoof( targetPiece.PieceId );
		var roofOnRoof = IsRoof( placingPieceId ) && IsRoof( targetPiece.PieceId );
		// Multi-lip families expose several placing edges for Q/E — don't force opposite-only.
		// Wall↔wall is NOT one of them: for a flat plate, mating a lip to the same-named lip is an
		// exact overlap, and because ties keep the first fit in [N,S,E,W] order that overlap won
		// auto-placement for the target's top and right edges.
		if ( !wallOnFloor && !roofOnFloor && !floorOnRoof && !roofOnRoof )
		{
			if ( !sameEdgeAlignment && placingEdge.Id != BuildSnapEdge.GetOpposite( targetEdge.Id ) )
				return false;

			if ( sameEdgeAlignment && placingEdge.Id != targetEdge.Id )
				return false;
		}

		if ( BuildSnapAlignment.UsesEdgeRelativeYaw( placingPieceId, targetPiece.PieceId ) )
		{
			// Off the target's 90° grid the player is angling the piece out on purpose. Rounding to
			// the grid here is what turned a 45° scroll tick into a quarter turn; refusing the flush
			// mate hands the seam to the hinge path, which holds the joint at any angle.
			if ( BuildSnapAlignment.OffGridYawDegrees( targetPiece, yawDegrees ) > FlushYawToleranceDegrees )
				return false;

			// Edge direction defines orientation — try all 90° steps so interior E/W seams
			// still fit when scroll yaw was left on a N/S wall (and vice versa).
			// When several yaws fit (common for pitched roofs), keep sit-above or hang-down
			// according to aim instead of the first geometric match.
			var baseYaw = BuildSnapAlignment.GetEdgeSnapYaw( targetPiece, yawDegrees );
			var found = false;
			var bestScore = float.MaxValue;
			var hang = preferHangDownOnFloor && roofOnFloor;
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
					? BuildSnapCompatibility.ScoreRoofElevation(
						placingPieceId,
						candidate,
						targetPiece,
						preferHangDown: hang )
					: 0f;
				// Prefer the step closest to where the player has actually scrolled. Ordering by
				// step index instead made rotation jump between fits and re-snap elsewhere.
				var yawOffset = Math.Abs( BuildSnapAlignment.DeltaDegrees(
					alignedYaw.Angles().yaw,
					yawDegrees ) );
				var score = elev + yawOffset * 0.01f;
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

	/// <summary>
	/// Corner-to-corner mate for a seam that has no flush fit. <see cref="BuildSnapAlignment.TryFitEdge"/>
	/// only succeeds when the two lips are parallel <b>and the same length</b>, so it never fires for
	/// mixed sizes (1 m wall on a 2 m floor edge) or for an off-axis yaw. Landing a corner on the
	/// aimed corner covers both: equal lips give the same result the flush fit would, unequal lips sit
	/// flush at the end of the longer edge instead of centred on it, and any yaw pivots on that corner.
	/// </summary>
	static bool TryCornerMateToEdge(
		string placingPieceId,
		SnapEdge placingEdge,
		Vector3 targetWorldA,
		Vector3 targetWorldB,
		Vector3 aimLand,
		float yawDegrees,
		out Transform placement )
	{
		placement = default;
		var alignedYaw = GetPlacementYaw( yawDegrees );
		var orientedRot = alignedYaw * BuildModuleDimensions.GetPrefabLocalRotation( placingPieceId );
		var scale = BuildModuleDimensions.GetPieceLocalScale( placingPieceId );
		var colliderHalf = BuildColliderSnap.PrefabColliderSize * 0.5f;

		// Anchor on whichever end of the seam the crosshair is nearer — the corner being pointed at.
		var aimNearerB = Vector3.DistanceBetween( aimLand, targetWorldB )
		                 < Vector3.DistanceBetween( aimLand, targetWorldA );
		var anchorWorld = aimNearerB ? targetWorldB : targetWorldA;
		var seamDirection = ( ( aimNearerB ? targetWorldA : targetWorldB ) - anchorWorld ).Normal;

		var found = false;
		var bestAlong = float.MinValue;

		// Either end of the mating lip can take the anchored corner. Keep the one that runs the rest
		// of the piece back along the seam: the seam is the only direction the joint exists in, so
		// the opposite choice hangs the piece off the end into open space.
		for ( var i = 0; i < 2; i++ )
		{
			var role = i == 0 ? placingEdge.CornerA : placingEdge.CornerB;
			var farRole = i == 0 ? placingEdge.CornerB : placingEdge.CornerA;

			var offset = BuildColliderSnap.GetCornerSnapWorldOffset(
				placingPieceId,
				role,
				orientedRot,
				scale,
				colliderHalf );
			var farOffset = BuildColliderSnap.GetCornerSnapWorldOffset(
				placingPieceId,
				farRole,
				orientedRot,
				scale,
				colliderHalf );

			var along = Vector3.Dot( farOffset - offset, seamDirection );
			if ( found && along <= bestAlong )
				continue;

			found = true;
			bestAlong = along;
			placement = new Transform( anchorWorld - offset, alignedYaw );
		}

		return found;
	}
}
