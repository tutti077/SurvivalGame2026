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
			yawDegrees,
			maxRange );

		var bestRay = BuildSnapPlacement.GetBestRayScore( candidates );
		var view = BuildSnapCrosshair.BuildViewContext( rayOrigin, dir, maxRange, bestRay );
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

		var snapResult = TrySnapFromCandidates( candidates );
		if ( snapResult is not null )
			return snapResult.Value;

		var ground = ComputeGroundPlacement(
			data,
			scene,
			pawn,
			ignorePreview,
			rayOrigin,
			rayDirection,
			yawDegrees,
			maxRange );

		return WithRayDebug( ground with
		{
			SnapCandidateIndex = 0,
			SnapCandidateCount = 0,
			SnappedToStructure = false,
		} );
	}

	public static BuildPlacementResult ComputeGroundPlacement(
		BuildPieceData data,
		Scene scene,
		GameObject pawn,
		GameObject ignorePreview,
		Vector3 rayOrigin,
		Vector3 rayDirection,
		float yawDegrees,
		float maxRange )
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

		var isValid = !OverlapsExistingPieces( scene, ignorePreview, position, rotation, half );

		return new BuildPlacementResult
		{
			IsValid = isValid,
			HasSurfaceHit = hasSurface,
			Position = position,
			Rotation = rotation,
			SurfaceNormal = normal,
		};
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
			.Run();

		if ( downTrace.Hit && !IsIgnoredTraceHit( downTrace.GameObject, ignorePreview ) )
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
		var trace = scene.Trace.Ray( start, end ).IgnoreGameObjectHierarchy( pawn ).Run();
		if ( trace.Hit && !IsIgnoredTraceHit( trace.GameObject, ignorePreview ) )
			return trace;

		trace = scene.Trace.Ray( start, end ).IgnoreGameObjectHierarchy( pawn ).UseHitboxes().Run();
		if ( trace.Hit && !IsIgnoredTraceHit( trace.GameObject, ignorePreview ) )
			return trace;

		return default;
	}

	static bool IsIgnoredTraceHit( GameObject hit, GameObject ignorePreview )
	{
		if ( hit is null || !hit.IsValid() )
			return true;

		if ( ignorePreview.IsValid() && hit.Root == ignorePreview.Root )
			return true;

		return hit.Tags.Has( "buildpreview" );
	}

	public static bool OverlapsExistingPieces(
		Scene scene,
		GameObject ignorePreview,
		Vector3 position,
		Rotation rotation,
		Vector3 halfExtents,
		GameObject ignoreHierarchy = null )
	{
		var candidate = new Transform( position, rotation );

		foreach ( var piece in scene.GetAllComponents<BuildPiece>() )
		{
			if ( piece is null || !piece.IsValid() || piece.IsPreviewGhost )
				continue;

			if ( ignorePreview.IsValid() && piece.GameObject == ignorePreview )
				continue;

			if ( ignoreHierarchy.IsValid() && piece.GameObject == ignoreHierarchy )
				continue;

			if ( Overlaps( candidate, halfExtents, piece.GameObject.WorldTransform, piece.HalfExtents ) )
				return true;
		}

		return false;
	}

	static bool Overlaps( Transform a, Vector3 halfA, Transform b, Vector3 halfB )
	{
		var delta = a.Rotation.Inverse * (b.Position - a.Position);
		return Math.Abs( delta.x ) < halfA.x + halfB.x
		       && Math.Abs( delta.y ) < halfA.y + halfB.y
		       && Math.Abs( delta.z ) < halfA.z + halfB.z;
	}
}
