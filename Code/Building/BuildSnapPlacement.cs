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
		var targetEdges = BuildSnapLayout.GetEdges( targetPiece.PieceId );
		for ( var ei = 0; ei < targetEdges.Count; ei++ )
		{
			var targetEdge = targetEdges[ei];

			// A triangle is missing one plate corner, so one of the four named edges is really a
			// single corner with open air past it. Mating to it would pin the piece against nothing.
			if ( !BuildSnapLayout.HasEdge( targetPiece.PieceId, targetEdge ) )
				continue;

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
					    yawDegrees,
					    placingEdgeIds,
					    rayOrigin ) )
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
				if ( !TryGetOwnedEdge( placingData.Id, placingEdgeIds[pi], out var placingEdge ) )
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
				{
					BuildSnapDebug.LogEdgeReject( placingData.Id, targetPiece.PieceId, targetEdge.Id,
						$"no flush fit for the {placingEdge.Id} lip at yaw {yawDegrees:0}" );
					continue;
				}

				if ( !BuildSnapCompatibility.IsValidEdgePlacement(
					     placingData.Id,
					     targetPiece.PieceId,
					     placingEdge,
					     targetEdge,
					     placement,
					     targetPiece ) )
				{
					BuildSnapDebug.LogEdgeReject( placingData.Id, targetPiece.PieceId, targetEdge.Id,
						$"{placingEdge.Id} lip rejected as an invalid placement" );
					continue;
				}

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
				// piece back over the target. Only meaningful when both pieces name their edges in the
				// same frame — between families they do not. A wall's North and South are its top and
				// bottom, a floor's are two of its four sides, so comparing them scores a wall's lip
				// against a floor edge by nothing better than the letter it was given: the floor's
				// north and south seams got a preference and its east and west got none. That is a
				// standing asymmetry between otherwise identical edges of the same deck.
				if ( BuildSnapCompatibility.IsSameEdgeFamily( placingData.Id, targetPiece.PieceId ) )
				{
					score = score with
					{
						Combined = score.Combined
						           + ( placingEdge.Id == BuildSnapEdge.GetOpposite( targetEdge.Id ) ? -1f : 1f ),
					};
				}

				// Join as much of the piece as the seam allows. A 1 m wall brought to a 2 m wall's
				// edge should sit flush along its whole height at the end you are aiming at, not meet
				// it at a corner and hang off into space — both fit, and without this the corner mate
				// wins whenever it happens to sit marginally nearer the crosshair.
				var contact = ScoreEdgeContact(
					placingData.Id,
					placingEdge,
					placement,
					t0.Position,
					t1.Position );
				score = score with { Combined = score.Combined - contact * EdgeContactWeight };

				// The piece goes on the side of the target you are looking at. One rule, applied to
				// every pair, replacing the per-family elevation and look biases that used to sit
				// here — those each encoded a guess about one combination and disagreed at the
				// seams, which is how a wall sank into a deck you were stood on top of and a roof
				// hung under a wall top you were aiming at.
				// Landing through the target, away from the player, is not a worse mate — it is not a
				// mate. Scoring it merely expensively still let it win whenever it was the only
				// candidate that survived, which is how a wall kept ending up under a deck being
				// looked at from on top.
				if ( ScoreThroughFacePenalty( placement, targetPiece, rayOrigin ) > 0f )
				{
					BuildSnapDebug.LogEdgeReject( placingData.Id, targetPiece.PieceId, targetEdge.Id,
						$"{placingEdge.Id} lip lands through the face, away from you" );
					continue;
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
			     && TryPickMatingEdge( placingData.Id, placingEdgeIds, targetEdge.Id, out bestPlacingEdge )
			     && TryCornerMateToEdge(
				     placingData.Id,
				     bestPlacingEdge,
				     t0.Position,
				     t1.Position,
				     aimLand,
				     yawDegrees,
				     out bestPlacement )
			     // This path produces one placement and skips the scoring loop above, so the face
			     // rule has to be asked here too — otherwise it is a way in for exactly the mate the
			     // scoring exists to reject, which is how a wall still went under a deck being looked
			     // at from on top. It also runs far more often now that a snap may not turn the piece:
			     // when nothing fits at the player's rotation, everything lands here.
			     && ScoreThroughFacePenalty( bestPlacement, targetPiece, rayOrigin ) <= 0f )
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
	/// The placing corner that completes a flush edge at this rotation, found by testing real
	/// geometry rather than trusting edge names — names live in each piece's own frame and carry no
	/// world meaning across a seam (a floor's "north" is one of its four sides, a wall's is its top).
	/// Every end that measures flush is a candidate; overlap directions are rejected, and when more
	/// than one honest fit remains (a perpendicular T can extend to either side of the seam) the one
	/// extending toward the player's side wins. Null when nothing verifies — mismatched lip lengths,
	/// or an off-axis yaw the player is deliberately holding.
	/// </summary>
	static BuildSnapRole? FindFlushRole(
		string placingPieceId,
		string targetPieceId,
		BuildPiece targetPiece,
		SnapEdge targetEdge,
		IReadOnlyList<SnapEdgeId> placingEdgeIds,
		Transform attachWorld,
		Vector3 farTargetWorld,
		float yawDegrees,
		Vector3 rayOrigin )
	{
		if ( placingEdgeIds is null )
			return null;

		var sameFamily = BuildSnapCompatibility.IsSameEdgeFamily( placingPieceId, targetPieceId );

		// A wall's East/West edges are its two vertical ENDS, and edge names live in the piece's own
		// frame — they say nothing about world sides. Demanding the name-opposite end (old rule) only
		// works where two parallel walls face each other; at a run's END post, the one wall that owns
		// the seam names it from its own frame, and when that name demanded the held wall's far end,
		// the piece mated extending the wrong way — the "flipped last wall" that a 180° rotation
		// (which swaps the held piece's edge names) appeared to fix. Both vertical ends are offered
		// here and geometry chooses below.
		var wallSideSeam = IsWall( placingPieceId ) && IsWall( targetPieceId )
		                   && targetEdge.Id is SnapEdgeId.East or SnapEdgeId.West;

		var seamMid = ( attachWorld.Position + farTargetWorld ) * 0.5f;
		var outward = ( seamMid - targetPiece.GameObject.WorldPosition ).WithZ( 0f );
		outward = outward.LengthSquared > 1e-6f ? outward.Normal : Vector3.Zero;
		var toPlayer = ( rayOrigin - seamMid ).WithZ( 0f );
		toPlayer = toPlayer.LengthSquared > 1e-6f ? toPlayer.Normal : Vector3.Zero;

		BuildSnapRole? best = null;
		var bestSide = float.MinValue;

		for ( var i = 0; i < placingEdgeIds.Count; i++ )
		{
			if ( !TryGetOwnedEdge( placingPieceId, placingEdgeIds[i], out var placingEdge ) )
				continue;

			if ( wallSideSeam )
			{
				// A vertical seam takes a vertical end — either of them; never a horizontal lip.
				if ( placingEdge.Id is not (SnapEdgeId.East or SnapEdgeId.West) )
					continue;
			}
			else if ( !( IsWall( placingPieceId ) && IsFloor( targetPieceId ) ) )
			{
				// Parallel-face pairs keep the name gate: opposite edge extends, same edge overlaps.
				// Cross-family wall-on-floor is exempt — its lips share no frame with the floor.
				var sameEdgeAlignment = BuildSnapCompatibility.UsesSameEdgeAlignment(
					placingPieceId, targetPieceId, placingEdge.Id, targetEdge.Id, targetPiece );
				var wantsOpposite = placingEdge.Id == BuildSnapEdge.GetOpposite( targetEdge.Id );
				var wantsSame = placingEdge.Id == targetEdge.Id;
				if ( sameEdgeAlignment ? !wantsSame : !wantsOpposite )
					continue;
			}

			for ( var swap = 0; swap < 2; swap++ )
			{
				var anchorRole = swap == 0 ? placingEdge.CornerA : placingEdge.CornerB;
				var farRole = swap == 0 ? placingEdge.CornerB : placingEdge.CornerA;

				if ( !TryAlignToSnap(
					     placingPieceId,
					     new BuildSnapPoint( anchorRole, Vector3.Zero, Rotation.Identity ),
					     attachWorld,
					     targetPiece,
					     yawDegrees,
					     out var placement ) )
					continue;

				var farWorld = GetPlacingSnapWorld( placingPieceId, farRole, placement );
				if ( Vector3.DistanceBetween( farWorld, farTargetWorld ) > BuildSnapEdge.EdgeAlignTolerance )
					continue;

				if ( !BuildSnapCompatibility.IsValidEdgePlacement(
					     placingPieceId, targetPieceId, placingEdge, targetEdge, placement, targetPiece ) )
					continue;

				if ( !sameFamily )
					return anchorRole;   // Cross-family: one honest fit, take it.

				// Same family: several ends can measure "flush" on the same seam, so geometry picks.
				// A vertical wall end is direction-blind (both corners on one plumb line), which let a
				// mate lying back OVER the built wall read as flush ("connects the far snap points").
				var offset = ( placement.Position - seamMid ).WithZ( 0f );
				if ( Vector3.Dot( offset, outward ) < -BuildSnapEdge.EdgeAlignTolerance )
					continue;   // Lays back over the target — overlap in a flush fit's clothes.

				// A perpendicular T can extend to either side of the seam; both are honest joins.
				// Default to the side the player is on — they hold the piece where they stand, and
				// rotation / Q&E still reach the far side deliberately.
				var side = Vector3.Dot( offset, toPlayer );
				if ( best is null || side > bestSide )
				{
					best = anchorRole;
					bestSide = side;
				}
			}
		}

		return best;
	}

	/// <summary>
	/// Corner on the placing piece that meets <paramref name="anchorRole"/> when the two pieces sit
	/// square across the seam — the same corner mirrored across the seam's own axis. Reads only the
	/// seam, never the yaw. Fallback only, for pairs <see cref="FindFlushRole"/> finds no verified fit
	/// for (mismatched lip lengths, an off-axis yaw the player is deliberately holding).
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
		float yawDegrees,
		IReadOnlyList<SnapEdgeId> placingEdgeIds,
		Vector3 rayOrigin )
	{
		// Which end of the seam is anchored follows the crosshair, not the rotation.
		var attachRole = Vector3.DistanceBetween( aimLand, targetWorldB )
		                 < Vector3.DistanceBetween( aimLand, targetWorldA )
			? targetEdge.CornerB
			: targetEdge.CornerA;

		if ( FindSnapIndex( targetPiece.SnapPoints, attachRole ) < 0 )
		{
			BuildSnapDebug.LogEdgeReject( placingData.Id, targetPiece.PieceId, targetEdge.Id,
				$"target has no {attachRole} snap to attach to" );
			return false;
		}

		var wallOnWall = IsWall( placingData.Id ) && IsWall( targetPiece.PieceId );

		// A wall's South edge is its bottom lip: mating anything to it hangs the new wall below the
		// built one — buried in the ground on any ground-level build. Aiming low on a wall's face
		// put this seam closest and it won the group pick ("upside-down T"). Never offer it.
		if ( wallOnWall && targetEdge.Id == SnapEdgeId.South )
			return false;

		var attachWorld = targetPiece.GetSnapWorldTransform( FindSnap( targetPiece, attachRole ) );
		var farTargetWorld = attachRole == targetEdge.CornerA ? targetWorldB : targetWorldA;

		var flushRole = FindFlushRole(
			placingData.Id,
			targetPiece.PieceId,
			targetPiece,
			targetEdge,
			placingEdgeIds,
			attachWorld,
			farTargetWorld,
			yawDegrees,
			rayOrigin );

		// The North edge is the top lip — a real stack when the held wall lies flush along it, junk
		// when the held wall is perpendicular (no flush fit exists on a horizontal edge, and the
		// fallback would hang a diagonal corner mate there). With both lips constrained, the vertical
		// side edges own a perpendicular join from either side of the built wall.
		if ( wallOnWall && targetEdge.Id == SnapEdgeId.North && flushRole is null )
			return false;

		// Mirror table is the fallback when geometry cannot verify a fit at this rotation
		// (mismatched lip lengths, an off-axis yaw the player is deliberately holding).
		var squareRole = flushRole ?? MirrorRoleAcrossEdge( attachRole, targetEdge.Id );
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
		string placingPieceId,
		IReadOnlyList<SnapEdgeId> placingEdgeIds,
		SnapEdgeId targetEdge,
		out SnapEdge placingEdge )
	{
		placingEdge = default;
		var opposite = BuildSnapEdge.GetOpposite( targetEdge );
		for ( var i = 0; i < placingEdgeIds.Count; i++ )
		{
			if ( placingEdgeIds[i] == opposite
			     && TryGetOwnedEdge( placingPieceId, opposite, out placingEdge ) )
				return true;
		}

		// Head-on lip is missing on this piece (a triangle has only two full edges) — take any
		// other offered lip it does have rather than dropping the seam.
		for ( var i = 0; i < placingEdgeIds.Count; i++ )
		{
			if ( TryGetOwnedEdge( placingPieceId, placingEdgeIds[i], out placingEdge ) )
				return true;
		}

		return false;
	}

	/// <summary>
	/// An edge the placing piece actually owns, resolved from its own edge list — the only source a
	/// <see cref="SnapEdgeId.Diagonal"/> can come from (a full plate has all four corners, so a
	/// corner-existence test alone would grant it a phantom diagonal).
	/// </summary>
	static bool TryGetOwnedEdge( string placingPieceId, SnapEdgeId edgeId, out SnapEdge edge ) =>
		BuildSnapLayout.TryGetPieceEdge( placingPieceId, edgeId, out edge )
		&& BuildSnapLayout.HasEdge( placingPieceId, edge );

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

	/// <summary>
	/// Origins closer than this (units) count as the same spot for the co-location veto. The
	/// duplicate-in-place candidate this kills lands EXACTLY on the existing piece (snap math is
	/// exact), so this only needs to absorb float noise — keep it well under the ~1.5-unit minimum
	/// separation of two free-placed walls, or stacking on either of a close pair gets vetoed too.
	/// </summary>
	const float CoLocationOriginEpsilon = 0.5f;

	/// <summary>
	/// True when placing this candidate would drop a piece of the same type at an existing piece's
	/// exact origin and orientation — a duplicate in place, never a placement anyone wants offered.
	/// Different yaw/pitch at the same origin stays allowed (crossed walls, X-braces).
	/// </summary>
	static bool IsLikePieceCoLocation( Scene scene, GameObject ignorePreview, string placingPieceId, Transform placement )
	{
		RefreshPieceScratch( scene );
		for ( var i = 0; i < PieceScratch.Count; i++ )
		{
			var piece = PieceScratch[i];
			if ( piece is null || !piece.IsValid() || piece.IsPreviewGhost )
				continue;

			if ( ignorePreview.IsValid() && piece.GameObject == ignorePreview )
				continue;

			if ( !string.Equals( piece.PieceId, placingPieceId, StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( (piece.GameObject.WorldPosition - placement.Position).LengthSquared
			     > CoLocationOriginEpsilon * CoLocationOriginEpsilon )
				continue;

			var existing = piece.GameObject.WorldRotation;
			if ( (existing.Forward - placement.Rotation.Forward).LengthSquared < 0.01f
			     && (existing.Up - placement.Rotation.Up).LengthSquared < 0.01f )
				return true;
		}

		return false;
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
		// Overlap is not used to void snap candidates (ground placement still checks overlap),
		// with one exception: a like-piece co-location. Mating a lip to the same-named lip
		// reproduces the piece exactly in place (a wall sharing an existing wall's bottom snaps),
		// and that junk candidate must never win auto-snap. It stays IN the list as an invalid
		// member — the grouper prefers groups with a valid mate and the ghost shows red if cycled
		// onto it — because dropping it made the Q/E count (and the held anchor, via the index
		// modulo) flicker with yaw: fine-rotating a triangle floor through the one duplicating
		// yaw went 3/3 → 2/2 → 3/3 and silently changed which corner was held. Same type + same
		// origin + same orientation only — an X-brace of two 45° beams, and two triangle floors
		// closing a square at 180°, share an origin on purpose.
		var coLocated = IsLikePieceCoLocation( scene, ignorePreview, placingPieceId, placement );

		var anchorIndex = FindSnapIndex( placingSnaps, anchorRole );
		var targetIndex = FindSnapIndex( targetPiece.SnapPoints, targetRole );

		// Tie-break only, not an override: a ±25/40 bias here used to beat any real aim-distance
		// difference outright, so a wall corner-joining an adjacent wall always lost to that wall's
		// own floor mate — even when the corner was plainly the closer, better-aimed candidate. That
		// is why a third wall could never close a corner: whichever edge of the floor was in reach
		// (varying with the player's angle, which is why it looked direction-dependent) always won,
		// landing the new wall on the same seam the existing wall already occupied. Small enough now
		// to only settle genuine near-ties.
		var scoreBias = 0f;
		if ( IsWall( placingPieceId ) && IsFloor( targetPiece.PieceId ) )
			scoreBias -= 3f;
		else if ( IsWall( placingPieceId ) && IsWall( targetPiece.PieceId ) )
		{
			scoreBias += 3f;

			// The corner of a wall belongs to both its top lip (stack a storey) and its side edge
			// (close a corner), so aiming there scores both seams nearly alike. Prefer the side join;
			// the stack stays one Q/E away. The isEdgeSnap guard matters: point-snap candidates carry
			// targetEdge as default, which reads as North and would collect this penalty by accident.
			if ( isEdgeSnap && targetEdge == SnapEdgeId.North )
				scoreBias += 20f;
		}

		CandidateScratch.Add( new BuildSnapCandidate
		{
			IsValid = !coLocated,
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
		var orientedRot = placement.Rotation;
		var scale = BuildModuleDimensions.GetPieceLocalScale( placingPieceId );
		var half = BuildColliderSnap.GetColliderHalfForPiece( placingPieceId );
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

		if ( candidate.IsEdgeSnap && TryGetEdgeForCorner( placingPieceId, anchorRole, out var edge ) )
		{
			var a = GetPlacingSnapWorld( placingPieceId, edge.CornerA, candidate.Placement );
			var b = GetPlacingSnapWorld( placingPieceId, edge.CornerB, candidate.Placement );
			return ( a + b ) * 0.5f;
		}

		return GetPlacingSnapWorld( placingPieceId, anchorRole, candidate.Placement );
	}

	static bool TryGetEdgeForCorner( string pieceId, BuildSnapRole corner, out SnapEdge edge )
	{
		var edges = BuildSnapLayout.GetEdges( pieceId );
		for ( var i = 0; i < edges.Count; i++ )
		{
			var candidate = edges[i];
			if ( candidate.CornerA != corner && candidate.CornerB != corner )
				continue;

			// Skip the seam a triangle's cut corner would complete — it has no far end.
			if ( !BuildSnapLayout.HasEdge( pieceId, candidate ) )
				continue;

			edge = candidate;
			return true;
		}

		edge = default;
		return false;
	}

	/// <summary>
	/// Greater than 0 when the placement ends up <b>through</b> the target, on the opposite side of
	/// its face from the player — a wall carried on past a deck you are stood on top of. Zero for
	/// anything at or in front of the face, so a wall continuing the run alongside its neighbour is
	/// left alone rather than discouraged.
	/// <para>
	/// The face normal is the target's own flat axis, not the direction from its centre to the aim
	/// point. That was the earlier mistake: on a 3-unit-thick deck the aim point sits barely above
	/// the centre and up to a half-module out from it, so centre-to-aim is very nearly horizontal
	/// and told us which <i>edge</i> was being aimed at rather than which <i>face</i>. Standing on
	/// the deck and sinking through it both scored about +1, so nothing separated them.
	/// </para>
	/// </summary>
	static float ScoreThroughFacePenalty( Transform placement, BuildPiece targetPiece, Vector3 rayOrigin )
	{
		if ( targetPiece is null || !targetPiece.IsValid() )
			return 0f;

		var half = BuildColliderSnap.GetColliderHalfForPiece( targetPiece.PieceId );
		var flat = BuildModuleDimensions.ResolveThinAxis( half );
		if ( flat < 0 )
			return 0f;   // Beams and cubes have no face to be on the wrong side of.

		var localNormal = flat switch
		{
			0 => new Vector3( 1f, 0f, 0f ),
			1 => new Vector3( 0f, 1f, 0f ),
			_ => new Vector3( 0f, 0f, 1f ),
		};

		var center = targetPiece.GameObject.WorldPosition;
		var normal = BuildColliderSnap.GetSnapWorldRotation( targetPiece.GameObject, targetPiece.PieceId )
			* localNormal;

		// Point the normal at whichever side the player is standing on.
		if ( Vector3.Dot( rayOrigin - center, normal ) < 0f )
			normal = -normal;

		var advance = Vector3.Dot( placement.Position - center, normal );
		if ( advance >= 0f )
			return 0f;

		return Math.Clamp( -advance / BuildModuleDimensions.SnapModuleHalfUnits, 0f, 1f );
	}

	/// <summary>
	/// How strongly a flush join is preferred over a corner meeting. Sized to outrank the lip and
	/// look-direction biases below it, so contact decides first and those only break ties between
	/// mates that join the same amount.
	/// </summary>
	const float EdgeContactWeight = 60f;

	/// <summary>
	/// Fraction (0–1) of the shorter lip that is actually in contact with the target seam once the
	/// piece is placed. Lips that line up but sit apart score 0, so a corner-to-corner meeting can
	/// never read as a face join.
	/// </summary>
	static float ScoreEdgeContact(
		string placingPieceId,
		SnapEdge placingEdge,
		Transform placement,
		Vector3 targetA,
		Vector3 targetB )
	{
		var axis = targetB - targetA;
		var axisLen = axis.Length;
		if ( axisLen < 1e-3f )
			return 0f;

		var scale = BuildModuleDimensions.GetPieceLocalScale( placingPieceId );
		var half = BuildColliderSnap.GetColliderHalfForPiece( placingPieceId );
		var a = placement.Position + BuildColliderSnap.GetCornerSnapWorldOffset(
			placingPieceId, placingEdge.CornerA, placement.Rotation, scale, half );
		var b = placement.Position + BuildColliderSnap.GetCornerSnapWorldOffset(
			placingPieceId, placingEdge.CornerB, placement.Rotation, scale, half );

		var dir = axis / axisLen;
		var pa = Vector3.Dot( a - targetA, dir );
		var pb = Vector3.Dot( b - targetA, dir );

		// Off-seam distance: a lip parallel to the target but standing away from it is not touching.
		var gapA = Vector3.DistanceBetween( a, targetA + dir * Math.Clamp( pa, 0f, axisLen ) );
		var gapB = Vector3.DistanceBetween( b, targetA + dir * Math.Clamp( pb, 0f, axisLen ) );
		if ( Math.Max( gapA, gapB ) > BuildSnapEdge.EdgeAlignTolerance )
			return 0f;

		var lo = Math.Max( 0f, Math.Min( pa, pb ) );
		var hi = Math.Min( axisLen, Math.Max( pa, pb ) );
		var overlap = Math.Max( 0f, hi - lo );

		var shorter = Math.Min( axisLen, Math.Abs( pb - pa ) );
		return shorter < 1e-3f ? 0f : Math.Clamp( overlap / shorter, 0f, 1f );
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
		out Transform placement )
	{
		placement = default;
		if ( targetPiece is null || !targetPiece.IsValid() )
			return false;

		var wallOnFloor = IsWall( placingPieceId ) && IsFloor( targetPiece.PieceId );
		var roofOnFloor = IsRoof( placingPieceId ) && IsFloor( targetPiece.PieceId );
		var floorOnRoof = IsFloor( placingPieceId ) && IsRoof( targetPiece.PieceId );
		var roofOnRoof = IsRoof( placingPieceId ) && IsRoof( targetPiece.PieceId );
		var wallOnRoof = IsWall( placingPieceId ) && IsRoof( targetPiece.PieceId );
		// Multi-lip families expose several placing edges for Q/E — don't force opposite-only.
		// Wall↔wall is NOT one of them: for a flat plate, mating a lip to the same-named lip is an
		// exact overlap, and because ties keep the first fit in [N,S,E,W] order that overlap won
		// auto-placement for the target's top and right edges.
		if ( !wallOnFloor && !roofOnFloor && !floorOnRoof && !roofOnRoof && !wallOnRoof )
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

			// A snap moves the piece; it does not turn it. The player owns rotation via scroll, and
			// this only rounds it to the target's 90° grid so the mate can sit flush. This used to
			// search all four quarter turns and keep the best-scoring one, which let a snap spin a
			// wall away from the way the player had it pointed — the seam fit, but not the piece the
			// player was holding. There is one candidate now: theirs. If it does not fit at that
			// rotation, that is the correct answer, and scrolling round is how you get the mate.
			var alignedYaw = BuildSnapAlignment.GetEdgeSnapYaw( targetPiece, yawDegrees );

			if ( !BuildSnapAlignment.TryFitEdge(
				     placingPieceId,
				     placingEdge,
				     targetWorldA,
				     targetWorldB,
				     alignedYaw,
				     out var candidate ) )
				return false;

			if ( !BuildSnapCompatibility.IsValidEdgePlacement(
				     placingPieceId,
				     targetPiece.PieceId,
				     placingEdge,
				     targetEdge,
				     candidate,
				     targetPiece ) )
				return false;

			placement = candidate;
			return true;
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

		var orientedRot = alignedYaw;
		var scale = BuildModuleDimensions.GetPieceLocalScale( placingPieceId );
		var colliderHalf = BuildColliderSnap.GetColliderHalfForPiece( placingPieceId );
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
		var orientedRot = alignedYaw;
		var scale = BuildModuleDimensions.GetPieceLocalScale( placingPieceId );
		var colliderHalf = BuildColliderSnap.GetColliderHalfForPiece( placingPieceId );

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
