using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

public readonly struct BuildPlacementResult
{
	public bool IsValid { get; init; }
	public bool HasSurfaceHit { get; init; }
	public bool SnappedToStructure { get; init; }
	public int SnapCandidateIndex { get; init; }
	public int SnapCandidateCount { get; init; }
	public BuildSnapGroupKey? ActiveSnapGroup { get; init; }
	public int SnapAnchorVariantIndex { get; init; }
	public Vector3 Position { get; init; }
	public Rotation Rotation { get; init; }
	public Vector3 SurfaceNormal { get; init; }
	public BuildSnapCandidate? SnapCandidate { get; init; }

	/// <summary>View ray used for placement (camera → max range).</summary>
	public bool HasRayDebug { get; init; }
	public Vector3 RayOrigin { get; init; }
	public Vector3 RayEnd { get; init; }
	public bool HasRayHit { get; init; }
	public Vector3 RayHitPosition { get; init; }
	/// <summary>Crosshair focus — closest point on view ray to a snap in range.</summary>
	public Vector3 ProbePosition { get; init; }
	public Vector3 AimDropPosition { get; init; }
}

/// <summary>Camera-ray placement with optional piece-to-piece snapping.</summary>
public static class BuildPlacementUtility
{
	public const float MaxGroundSlopeDegrees = 70f;
	const float AimDropDistance = 192f;

	public static bool TryGetViewRay( GameObject pawn, out Vector3 origin, out Vector3 direction ) =>
		BuildViewCamera.TryGetViewRay( pawn, out origin, out direction );

	public static BuildPlacementResult ComputePlacement(
		BuildPieceData data,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Scene scene,
		GameObject pawn,
		GameObject ignorePreview,
		Vector3 rayOrigin,
		Vector3 rayDirection,
		float yawDegrees,
		float maxRange,
		int snapAnchorVariantIndex,
		BuildSnapGroupKey? lockedSnapGroup )
	{
		var dir = rayDirection.Normal;
		if ( data is null || !pawn.IsValid() || !scene.IsValid() || dir.LengthSquared < 1e-8f )
			return new BuildPlacementResult { IsValid = false };

		var aimDrop = rayOrigin + dir * Math.Min( AimDropDistance, maxRange * 0.75f );
		var rayEnd = rayOrigin + dir * maxRange;
		var trace = TraceBuildRay( scene, pawn, ignorePreview, rayOrigin, rayEnd );
		var aimLand = BuildSnapCrosshair.ResolveAimLandPoint(
			rayOrigin,
			dir,
			maxRange,
			trace.Hit,
			trace.Hit ? trace.HitPosition : default );

		BuildPlacementResult? TrySnapFromCandidates( IReadOnlyList<BuildSnapCandidate> list )
		{
			if ( list is null || list.Count == 0 )
				return null;

			if ( !BuildSnapCandidateGrouper.TryPickCandidate(
				     list,
				     snapAnchorVariantIndex,
				     lockedSnapGroup,
				     data.Id,
				     placingSnaps,
				     rayOrigin,
				     dir,
				     aimLand,
				     maxRange,
				     out var snap,
				     out var variantCount ) )
				return null;

			return WithRayDebug( new BuildPlacementResult
			{
				IsValid = snap.IsValid,
				HasSurfaceHit = true,
				SnappedToStructure = true,
				SnapCandidateIndex = snap.AnchorVariantIndex,
				SnapCandidateCount = variantCount,
				ActiveSnapGroup = snap.GroupKey,
				SnapAnchorVariantIndex = snap.AnchorVariantIndex,
				Position = snap.Placement.Position,
				Rotation = snap.Placement.Rotation,
				SurfaceNormal = Vector3.Up,
				SnapCandidate = snap,
			} );
		}

		var candidates = BuildSnapPlacement.CollectCandidates(
			data,
			placingSnaps,
			scene,
			ignorePreview,
			rayOrigin,
			dir,
			aimLand,
			yawDegrees,
			maxRange );

		var bestRay = BuildSnapPlacement.GetBestRayScore( candidates );
		var view = BuildSnapCrosshair.BuildViewContext( rayOrigin, dir, maxRange, aimLand, bestRay );
		BuildPlacementResult WithRayDebug( BuildPlacementResult result ) => result with
		{
			HasRayDebug = true,
			RayOrigin = rayOrigin,
			RayEnd = rayEnd,
			HasRayHit = trace.Hit,
			RayHitPosition = trace.Hit ? trace.HitPosition : default,
			ProbePosition = view.CrosshairPoint,
			AimDropPosition = aimDrop,
		};

		// Where the piece would sit with no snapping at all — the fallback returned when no snap
		// commits (or none exists). No longer part of the snap commit decision itself; see
		// BuildSnapCrosshair.ShouldCommitSnap.
		var ground = ComputeGroundPlacement(
			data,
			placingSnaps,
			scene,
			pawn,
			ignorePreview,
			rayOrigin,
			rayDirection,
			yawDegrees,
			maxRange,
			snapAnchorVariantIndex );

		var snapResult = TrySnapFromCandidates( candidates );
		if ( snapResult is { } snapped && snapped.SnapCandidate is { } committed )
		{
			var aimOk = BuildSnapCrosshair.ShouldCommitSnap( committed, ground.Position );
			// Sticky lock: once Q/E-locked to a seam, keep that snap while aim stays near it
			// so cycling doesn't reset when the face-commit band flickers.
			var sticky = lockedSnapGroup is { } locked
			             && committed.GroupKey.Equals( locked )
			             && ( aimOk
			                  || committed.RayScore.AimLandDistance
			                  <= BuildSnapCrosshair.AimCommitRadius * 1.5f );

			if ( aimOk || sticky )
				return snapped;

			// A valid seam existed but did not commit — log both gate distances so a miss shows
			// which one (aim vs proximity) was out of band and by how much.
			BuildSnapDebug.LogEdgeReject(
				data.Id,
				committed.TargetPiece is { IsValid: true } tp ? tp.PieceId : "?",
				committed.TargetEdgeId,
				$"snap found ({committed.GroupKey}) but commit gate held it back — aim-land "
				+ $"{committed.RayScore.AimLandDistance:0} vs {BuildSnapCrosshair.AimCommitRadius:0}, "
				+ $"ghost move {Vector3.DistanceBetween( committed.Placement.Position, ground.Position ):0} "
				+ $"vs {BuildSnapCrosshair.SnapEngageDistance:0}" );
		}

		var groundVariantCount = CountGroundHoldVariants( placingSnaps );
		var groundIndex = groundVariantCount > 0
			? ( ( snapAnchorVariantIndex % groundVariantCount ) + groundVariantCount ) % groundVariantCount
			: 0;

		return WithRayDebug( ground with
		{
			SnapCandidateIndex = groundIndex,
			SnapCandidateCount = groundVariantCount,
			SnapAnchorVariantIndex = groundIndex,
			SnappedToStructure = false,
			SnapCandidate = null,
			ActiveSnapGroup = null,
		} );
	}

	/// <summary>Centre, then one variant per snap the piece owns — three on a triangle, two on a beam.</summary>
	static int CountGroundHoldVariants( IReadOnlyList<BuildSnapPoint> placingSnaps )
	{
		if ( placingSnaps is null || placingSnaps.Count == 0 )
			return 1;

		var corners = 0;
		for ( var i = 0; i < placingSnaps.Count; i++ )
		{
			if ( IsHoldCornerRole( placingSnaps[i].Role ) )
				corners++;
		}

		return 1 + corners;
	}

	/// <summary>Any real snap can be the held point — a beam cycles its two ends, a plate its four corners.</summary>
	static bool IsHoldCornerRole( BuildSnapRole role ) =>
		role is BuildSnapRole.CornerNorthEast
			or BuildSnapRole.CornerNorthWest
			or BuildSnapRole.CornerSouthEast
			or BuildSnapRole.CornerSouthWest
			or BuildSnapRole.AxisStart
			or BuildSnapRole.AxisEnd;

	public static BuildPlacementResult ComputeGroundPlacement(
		BuildPieceData data,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Scene scene,
		GameObject pawn,
		GameObject ignorePreview,
		Vector3 rayOrigin,
		Vector3 rayDirection,
		float yawDegrees,
		float maxRange,
		int holdVariantIndex = 0 )
	{
		var invalid = new BuildPlacementResult { IsValid = false, HasSurfaceHit = false };
		if ( data is null || !pawn.IsValid() || !scene.IsValid() )
			return invalid;

		var dir = rayDirection.Normal;
		if ( dir.LengthSquared < 1e-8f )
			return invalid;

		var half = BuildModuleDimensions.GetHalfExtents( data.Id );
		var rotation = Rotation.FromYaw( yawDegrees );
		var sitHalf = BuildModuleDimensions.GetGroundSitHalfExtent( data.Id, rotation );
		var hasSurface = false;
		Vector3 position;
		Vector3 normal = Vector3.Up;

		var end = rayOrigin + dir * maxRange;
		var trace = TraceBuildRay( scene, pawn, ignorePreview, rayOrigin, end );
		if ( trace.Hit )
		{
			// Stations and furniture (non-structural pieces) are not build surfaces — red ghost,
			// no placement. Without this the piece placed and instantly collapsed (a bench gives
			// no support), which reads as a bug instead of a rule.
			if ( IsNonStructuralPieceHit( trace.GameObject ) )
			{
				return new BuildPlacementResult
				{
					HasSurfaceHit = true,
					IsValid = false,
					Position = trace.HitPosition + Vector3.Up * sitHalf,
					Rotation = rotation,
					SurfaceNormal = Vector3.Up,
				};
			}

			hasSurface = true;
			normal = trace.Normal.Normal;
			if ( normal.LengthSquared < 1e-8f )
				normal = Vector3.Up;

			var slopeAngle = Vector3.GetAngle( normal, Vector3.Up );
			if ( slopeAngle > MaxGroundSlopeDegrees )
			{
				return new BuildPlacementResult
				{
					HasSurfaceHit = true,
					IsValid = false,
					Position = trace.HitPosition + Vector3.Up * sitHalf,
					Rotation = rotation,
					SurfaceNormal = normal,
				};
			}

			var up = slopeAngle < 5f ? Vector3.Up : normal;
			rotation = Rotation.FromYaw( yawDegrees );
			if ( slopeAngle >= 5f )
			{
				var forward = Vector3.VectorPlaneProject( rotation.Forward, up ).Normal;
				if ( forward.LengthSquared < 1e-8f )
					forward = Vector3.VectorPlaneProject( Vector3.Forward, up ).Normal;
				rotation = Rotation.LookAt( forward, up );
			}

			sitHalf = BuildModuleDimensions.GetGroundSitHalfExtent( data.Id, rotation );
			position = trace.HitPosition + up * sitHalf;
		}
		else
		{
			position = DropFromCameraAim( scene, pawn, ignorePreview, rayOrigin, dir, maxRange, sitHalf, out hasSurface );
		}

		position = ApplyGroundHoldOffset( data.Id, placingSnaps, position, rotation, holdVariantIndex );

		if ( Vector3.DistanceBetween( rayOrigin, position ) > maxRange )
		{
			return new BuildPlacementResult
			{
				IsValid = false,
				HasSurfaceHit = hasSurface,
				Position = position,
				Rotation = rotation,
				SurfaceNormal = normal,
			};
		}

		// Overlap runs in the snap frame — yaw × kit quarter-turn × baked pitch — so a pitched
		// roof is tested as the tilted plate it is, not as a flat slab at its center height.
		var isValid = !OverlapsExistingPieces( scene, ignorePreview, data.Id, position, rotation, half );

		return new BuildPlacementResult
		{
			IsValid = isValid,
			HasSurfaceHit = hasSurface,
			Position = position,
			Rotation = rotation,
			SurfaceNormal = normal,
		};
	}

	/// <summary>
	/// Hold variant 0 = center (no offset). 1+ = shift so that corner sits over the aim/hit point.
	/// </summary>
	static Vector3 ApplyGroundHoldOffset(
		string pieceId,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Vector3 centerPosition,
		Rotation rotation,
		int holdVariantIndex )
	{
		// Default hold: the ghost hangs by its ground-contact centroid, not its box center — a
		// pitched roof's contact edge sits a meter from its origin, and holding by origin left the
		// piece dangling beside the crosshair. Zero offset for axis-aligned pieces.
		if ( holdVariantIndex <= 0 || placingSnaps is null || placingSnaps.Count == 0 )
			return centerPosition - BuildModuleDimensions.GetGroundContactOffsetXY( pieceId, rotation );

		var cornerOrdinal = 0;
		for ( var i = 0; i < placingSnaps.Count; i++ )
		{
			var role = placingSnaps[i].Role;
			if ( !IsHoldCornerRole( role ) )
				continue;

			cornerOrdinal++;
			if ( cornerOrdinal != holdVariantIndex )
				continue;

			var orientedRot = rotation;
			var scale = BuildModuleDimensions.GetPieceLocalScale( pieceId );
			var half = BuildColliderSnap.GetColliderHalfForPiece( pieceId );
			var cornerOffset = BuildColliderSnap.GetCornerSnapWorldOffset(
				pieceId,
				role,
				orientedRot,
				scale,
				half );
			// Keep height from sit; slide XY so the held corner is over the aim point.
			return centerPosition - cornerOffset.WithZ( 0f );
		}

		return centerPosition;
	}

	static Vector3 DropFromCameraAim(
		Scene scene,
		GameObject pawn,
		GameObject ignorePreview,
		Vector3 rayOrigin,
		Vector3 rayDirection,
		float maxRange,
		float sitHalfZ,
		out bool foundSurface )
	{
		foundSurface = false;
		var aimDistance = Math.Min( AimDropDistance, maxRange * 0.75f );
		var aimPoint = rayOrigin + rayDirection * aimDistance;

		var downStart = aimPoint + Vector3.Up * 512f;
		var downEnd = aimPoint - Vector3.Up * 4096f;
		var downTrace = scene.Trace.Ray( downStart, downEnd )
			.IgnoreGameObjectHierarchy( pawn )
			.WithoutTags( "player" )
			.Run();

		if ( downTrace.Hit
		     && !IsIgnoredTraceHit( downTrace.GameObject, ignorePreview )
		     && !IsPlayerTraceHit( downTrace.GameObject )
		     && !IsNonStructuralPieceHit( downTrace.GameObject ) )
		{
			foundSurface = true;
			return downTrace.HitPosition + Vector3.Up * sitHalfZ;
		}

		return aimPoint + Vector3.Up * sitHalfZ;
	}

	public static bool TryTraceBuildPiece(
		Scene scene,
		GameObject pawn,
		GameObject ignorePreview,
		Vector3 rayOrigin,
		Vector3 rayDirection,
		float maxRange,
		out BuildPiece piece )
	{
		piece = null;
		if ( !scene.IsValid() )
			return false;

		var dir = rayDirection.Normal;
		if ( dir.LengthSquared < 1e-8f )
			return false;

		var end = rayOrigin + dir * maxRange;
		var trace = TraceBuildRay( scene, pawn, ignorePreview, rayOrigin, end );
		if ( !trace.Hit || trace.GameObject is null || !trace.GameObject.IsValid() )
			return false;

		piece = FindBuildPieceOnHierarchy( trace.GameObject );
		return piece is not null && piece.IsValid() && !piece.IsPreviewGhost;
	}

	public static BuildPiece FindBuildPieceOnHierarchy( GameObject go )
	{
		for ( var current = go; current.IsValid(); current = current.Parent )
		{
			var piece = current.Components.Get<BuildPiece>();
			if ( piece is not null && piece.IsValid() )
				return piece;
		}

		return null;
	}

	static SceneTraceResult TraceBuildRay( Scene scene, GameObject pawn, GameObject ignorePreview, Vector3 start, Vector3 end )
	{
		// Third-person: camera rays clip the local pawn/hitboxes and drop aim onto the body →
		// false ground placement. Ignore player (+ triggers so roof root triggers don't steal hits).
		var dir = (end - start);
		var maxDist = dir.Length;
		if ( maxDist < 1e-4f )
			return default;

		dir /= maxDist;
		var from = start;
		const int maxSkips = 6;
		for ( var i = 0; i < maxSkips; i++ )
		{
			var remaining = end - from;
			if ( remaining.Length < 1e-3f )
				break;

			var trace = scene.Trace.Ray( from, end )
				.IgnoreGameObjectHierarchy( pawn )
				.WithoutTags( "player" )
				.Run();

			if ( !trace.Hit )
			{
				trace = scene.Trace.Ray( from, end )
					.IgnoreGameObjectHierarchy( pawn )
					.WithoutTags( "player" )
					.UseHitboxes()
					.Run();
			}

			if ( !trace.Hit )
				return default;

			if ( IsIgnoredTraceHit( trace.GameObject, ignorePreview )
			     || IsPlayerTraceHit( trace.GameObject ) )
			{
				from = trace.HitPosition + dir * 8f;
				continue;
			}

			return trace;
		}

		return default;
	}

	static bool IsPlayerTraceHit( GameObject hit )
	{
		for ( var current = hit; current.IsValid(); current = current.Parent )
		{
			if ( current.Tags.Has( "player" ) )
				return true;
			if ( current.Components.Get<PlayerVitals>() is not null )
				return true;
			if ( current.Components.Get<PlayerController>() is not null )
				return true;
		}

		return false;
	}

	/// <summary>The hit is a placed non-structural build piece (station, chest, campfire) — never a build surface.</summary>
	static bool IsNonStructuralPieceHit( GameObject hit )
	{
		for ( var current = hit; current.IsValid(); current = current.Parent )
		{
			var piece = current.Components.Get<BuildPiece>();
			if ( piece is null )
				continue;

			if ( piece.IsPreviewGhost )
				return false;

			return BuildPieceCatalog.TryGet( piece.PieceId, out var data ) && !data.IsStructural;
		}

		return false;
	}

	/// <summary>The hit is the pawn's own placement ghost (or any preview-tagged object) — never a build surface.</summary>
	static bool IsIgnoredTraceHit( GameObject hit, GameObject ignorePreview )
	{
		if ( hit is null || !hit.IsValid() )
			return true;

		if ( ignorePreview.IsValid() && hit.Root == ignorePreview.Root )
			return true;

		return hit.Tags.Has( "buildpreview" );
	}

	/// <summary>
	/// <paramref name="tableFrameHalf"/> is the piece's table-frame half extents — the snap frame
	/// applied here (<see cref="BuildColliderSnap.GetSnapFrame"/>) already carries the kit-mesh
	/// quarter turn AND the baked prefab pitch, so pitched roofs and 45° braces are tested as the
	/// tilted plates they are instead of flat slabs at their center height.
	/// </summary>
	public static bool OverlapsExistingPieces(
		Scene scene,
		GameObject ignorePreview,
		string placingPieceId,
		Vector3 position,
		Rotation rotation,
		Vector3 tableFrameHalf,
		GameObject ignoreHierarchy = null )
	{
		var candidate = new Transform( position, BuildColliderSnap.GetSnapFrame( placingPieceId, rotation ) );

		foreach ( var piece in scene.GetAllComponents<BuildPiece>() )
		{
			if ( piece is null || !piece.IsValid() || piece.IsPreviewGhost )
				continue;

			if ( ignorePreview.IsValid() && piece.GameObject == ignorePreview )
				continue;

			if ( ignoreHierarchy.IsValid() && piece.GameObject == ignoreHierarchy )
				continue;

			var target = new Transform(
				piece.GameObject.WorldPosition,
				BuildColliderSnap.GetSnapWorldRotation( piece.GameObject, piece.PieceId ) );
			if ( Overlaps( candidate, tableFrameHalf, target, BuildColliderSnap.GetColliderHalfForPiece( piece.PieceId ) ) )
				return true;
		}

		return false;
	}

	/// <summary>
	/// Thin-half skin so flush coplanar mates (floor beside floor) are not treated as solid hits.
	/// Must stay below two thin-halves combined or wall-vs-wall thin-axis tests go inert.
	/// </summary>
	static float OverlapContactSkin => BuildModuleDimensions.SnapThinHalfUnits;

	/// <summary>
	/// Symmetric box test: both boxes must overlap in each other's frame. For same-orientation
	/// pairs (snapped kit pieces, parallel pitched roofs) this is exact; for rotated pairs the
	/// one-frame version alone over-reports — a thin plate seen from a rotated neighbor's frame
	/// sums like a chunk — and that false positive is what kept free-placed roofs apart.
	/// </summary>
	static bool Overlaps( Transform a, Vector3 halfA, Transform b, Vector3 halfB ) =>
		OverlapsInFrame( a, halfA, b, halfB ) && OverlapsInFrame( b, halfB, a, halfA );

	static bool OverlapsInFrame( Transform a, Vector3 halfA, Transform b, Vector3 halfB )
	{
		var delta = a.Rotation.Inverse * (b.Position - a.Position);
		var skin = OverlapContactSkin;
		var sx = halfA.x + halfB.x - skin;
		var sy = halfA.y + halfB.y - skin;
		var sz = halfA.z + halfB.z - skin;
		return Math.Abs( delta.x ) < sx
		       && Math.Abs( delta.y ) < sy
		       && Math.Abs( delta.z ) < sz;
	}
}
