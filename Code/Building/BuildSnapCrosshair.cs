using System;
using Sandbox;

namespace Survival;

/// <summary>Scores how close a built snap is to where the crosshair lands (aim point within hammer range).</summary>
public static class BuildSnapCrosshair
{
	public const float MinAlongRay = 0f;
	public const float AlongRayWeight = 0.015f;
	/// <summary>Tiebreaker only — ray perpendicular distance is secondary to aim-land distance.</summary>
	public const float CameraDistanceWeight = 0.002f;

	/// <summary>
	/// When the crosshair hits nothing (or only something far past), snap scoring uses this
	/// distance along the view ray — not max hammer range (that made high open slots unreachable).
	/// </summary>
	public const float AimProbeDistance = 256f;

	/// <summary>Full module reach so aiming near tile centers still finds neighboring seams.</summary>
	public static float SnapReachRadius =>
		BuildModuleDimensions.SnapModuleHalfUnits * 2f;

	/// <summary>
	/// How far past / beside a piece the aim point may still pick its snaps
	/// (look at the ground just past an edge → still snaps that edge).
	/// </summary>
	public static float AimLandSnapRadius =>
		BuildModuleDimensions.SnapModuleHalfUnits * 4f;

	/// <summary>
	/// Aim must be this close to a seam/corner to <b>commit</b> to snap while looking
	/// at the piece face. ~10% of half-module — mid-face stays free for crooked stacking.
	/// </summary>
	public static float SnapCommitRadius =>
		BuildModuleDimensions.SnapModuleHalfUnits * 0.1f;

	/// <summary>
	/// When the aim lands <b>past / beside</b> a piece (not on its face), allow a wider
	/// band so the far edge is still reachable by looking just beyond it.
	/// </summary>
	public static float LookPastSnapCommitRadius =>
		BuildModuleDimensions.SnapModuleHalfUnits * 0.85f;

	public readonly struct RayTargetScore
	{
		public float Perpendicular { get; init; }
		public float AlongRay { get; init; }
		public float CameraDistance { get; init; }
		public float AimLandDistance { get; init; }
		public float Combined { get; init; }
		public Vector3 PointOnRay { get; init; }
		public Vector3 ClosestOnTarget { get; init; }
		public bool IsValid { get; init; }
	}

	/// <summary>
	/// Tight face band vs wider look-past band. Mid-face stays free; far edges work when
	/// aiming just past the piece onto the ground beyond.
	/// </summary>
	public static bool ShouldCommitSnap( BuildSnapCandidate candidate, Vector3 aimLand )
	{
		if ( !candidate.RayScore.IsValid )
			return false;

		var piece = candidate.TargetPiece;
		if ( piece is null || !piece.IsValid() || !piece.GameObject.IsValid() )
			return candidate.RayScore.AimLandDistance <= LookPastSnapCommitRadius;

		// Pitched roofs: the "face" isn't a flat XY deck, so planar on-face tests fail and
		// block top/bottom lip snaps. Commit from aim→seam distance with the look-past band.
		if ( string.Equals( piece.PieceId, "45roof", StringComparison.OrdinalIgnoreCase ) )
			return candidate.RayScore.AimLandDistance <= LookPastSnapCommitRadius;

		if ( !TryPlanarEdgeDistance( piece, aimLand, out var edgeDist, out var onFace ) )
		{
			// Aim probe floating near a high seam (no solid hit) — use the wider band.
			return candidate.RayScore.AimLandDistance <= LookPastSnapCommitRadius;
		}

		return onFace
			? edgeDist <= SnapCommitRadius
			: edgeDist <= LookPastSnapCommitRadius;
	}

	/// <summary>
	/// Planar distance from aim to the piece rectangle edge (inside = dist to nearest side;
	/// outside = dist to boundary). <paramref name="onFace"/> when aim projects onto the deck.
	/// </summary>
	static bool TryPlanarEdgeDistance(
		BuildPiece piece,
		Vector3 aimLand,
		out float edgeDist,
		out bool onFace )
	{
		edgeDist = float.MaxValue;
		onFace = false;

		var go = piece.GameObject;
		var half = piece.HalfExtents;
		if ( half.x < 1e-4f || half.y < 1e-4f || half.z < 1e-4f )
			return false;

		// HalfExtents are world-sized; ignore LocalScale so we compare in meters-of-extents space.
		var delta = go.WorldRotation.Inverse * (aimLand - go.WorldPosition);
		var isWall = string.Equals( piece.PieceId, "wall", StringComparison.OrdinalIgnoreCase );

		float planX;
		float planY;
		float halfX;
		float halfY;
		float thinAbs;
		float thinLimit;
		if ( isWall )
		{
			planX = delta.x;
			planY = delta.z;
			halfX = half.x;
			halfY = half.z;
			thinAbs = Math.Abs( delta.y );
			thinLimit = half.y + BuildModuleDimensions.SnapModuleHalfUnits * 0.35f;
		}
		else
		{
			planX = delta.x;
			planY = delta.y;
			halfX = half.x;
			halfY = half.y;
			thinAbs = Math.Abs( delta.z );
			// Ground just past / below a floor still counts as look-past.
			thinLimit = half.z + BuildModuleDimensions.SnapModuleHalfUnits * 0.75f;
		}

		if ( thinAbs > thinLimit )
			return false;

		edgeDist = DistanceToRectEdge( planX, planY, halfX, halfY );
		onFace = Math.Abs( planX ) <= halfX && Math.Abs( planY ) <= halfY;
		return true;
	}

	static float DistanceToRectEdge( float x, float y, float halfX, float halfY )
	{
		var ax = Math.Abs( x );
		var ay = Math.Abs( y );
		if ( ax <= halfX && ay <= halfY )
			return Math.Min( halfX - ax, halfY - ay );

		var dx = Math.Max( 0f, ax - halfX );
		var dy = Math.Max( 0f, ay - halfY );
		return MathF.Sqrt( dx * dx + dy * dy );
	}

	/// <summary>
	/// Where the crosshair lands for snap picking: nearby surface hit, else a probe point
	/// along the view ray (not the max-range ray end — that broke looking at elevated open slots).
	/// </summary>
	public static Vector3 ResolveAimLandPoint(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		float maxRange,
		bool hasHit,
		Vector3 hitPosition )
	{
		var dir = rayDirection.Normal;
		var probeDist = Math.Min( AimProbeDistance, maxRange );
		var probePoint = rayOrigin + dir * probeDist;

		if ( !hasHit )
			return probePoint;

		var along = Vector3.Dot( hitPosition - rayOrigin, dir );
		// Hit behind the ray start (third-person origin push) — still usable if near.
		if ( along < 0f )
			return along > -AimLandSnapRadius ? hitPosition : probePoint;

		if ( along > maxRange )
			return probePoint;

		// Far scenery past where you're aiming — keep the probe so nearby seams/slots win.
		if ( along > probeDist * 1.15f )
			return probePoint;

		return hitPosition;
	}

	/// <summary>
	/// Primary Valheim-style score: distance from aim-land point to the snap (or closest point on an edge).
	/// Snaps must still lie within hammer range of the camera.
	/// </summary>
	public static RayTargetScore ScorePointToAimLand(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		Vector3 aimLand,
		Vector3 worldPoint,
		float maxRange )
	{
		var invalid = new RayTargetScore { Combined = float.MaxValue };
		var dir = rayDirection.Normal;
		if ( dir.LengthSquared < 1e-8f )
			return invalid;

		var toPoint = worldPoint - rayOrigin;
		var distFromCam = toPoint.Length;
		if ( distFromCam > maxRange + AimLandSnapRadius )
			return invalid;

		var along = Vector3.Dot( toPoint, dir );
		// Allow snaps slightly beside / past the aim when looking past a piece.
		if ( along < -AimLandSnapRadius )
			return invalid;

		var aimDist = Vector3.DistanceBetween( worldPoint, aimLand );
		if ( aimDist > AimLandSnapRadius )
			return invalid;

		var pointOnRay = rayOrigin + dir * Math.Max( 0f, along );
		var perp = Vector3.DistanceBetween( worldPoint, pointOnRay );

		return new RayTargetScore
		{
			Perpendicular = perp,
			AlongRay = along,
			CameraDistance = distFromCam,
			AimLandDistance = aimDist,
			// Aim land wins; tiny ray + depth weights only break ties.
			Combined = aimDist + perp * 0.05f + along * AlongRayWeight + distFromCam * CameraDistanceWeight,
			PointOnRay = pointOnRay,
			ClosestOnTarget = worldPoint,
			IsValid = true,
		};
	}

	public static RayTargetScore ScoreSegmentToAimLand(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		Vector3 aimLand,
		Vector3 worldA,
		Vector3 worldB,
		float maxRange )
	{
		var invalid = new RayTargetScore { Combined = float.MaxValue, Perpendicular = float.MaxValue };
		var dir = rayDirection.Normal;
		if ( dir.LengthSquared < 1e-8f )
			return invalid;

		var closestOnSeg = ClosestPointOnSegment( aimLand, worldA, worldB );
		var toClosest = closestOnSeg - rayOrigin;
		var distFromCam = toClosest.Length;
		if ( distFromCam > maxRange + AimLandSnapRadius )
			return invalid;

		var along = Vector3.Dot( toClosest, dir );
		if ( along < -AimLandSnapRadius )
			return invalid;

		var aimDist = Vector3.DistanceBetween( closestOnSeg, aimLand );
		if ( aimDist > AimLandSnapRadius )
			return invalid;

		var pointOnRay = rayOrigin + dir * Math.Max( 0f, along );
		var perp = Vector3.DistanceBetween( closestOnSeg, pointOnRay );

		return new RayTargetScore
		{
			Perpendicular = perp,
			AlongRay = along,
			CameraDistance = distFromCam,
			AimLandDistance = aimDist,
			Combined = aimDist + perp * 0.05f + along * AlongRayWeight + distFromCam * CameraDistanceWeight,
			PointOnRay = pointOnRay,
			ClosestOnTarget = closestOnSeg,
			IsValid = true,
		};
	}

	public static RayTargetScore ScorePoint(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		Vector3 worldPoint,
		float maxRange )
	{
		var invalid = new RayTargetScore { Combined = float.MaxValue };
		var dir = rayDirection.Normal;
		if ( dir.LengthSquared < 1e-8f )
			return invalid;

		var along = Vector3.Dot( worldPoint - rayOrigin, dir );
		if ( along < MinAlongRay || along > maxRange )
			return invalid;

		var pointOnRay = rayOrigin + dir * along;
		var perp = Vector3.DistanceBetween( worldPoint, pointOnRay );
		if ( perp > SnapReachRadius )
			return invalid;

		var camDist = Vector3.DistanceBetween( rayOrigin, worldPoint );
		return new RayTargetScore
		{
			Perpendicular = perp,
			AlongRay = along,
			CameraDistance = camDist,
			AimLandDistance = perp,
			Combined = perp + along * AlongRayWeight + camDist * CameraDistanceWeight,
			PointOnRay = pointOnRay,
			ClosestOnTarget = worldPoint,
			IsValid = true,
		};
	}

	public static RayTargetScore ScoreSegment(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		Vector3 worldA,
		Vector3 worldB,
		float maxRange )
	{
		var invalid = new RayTargetScore { Combined = float.MaxValue, Perpendicular = float.MaxValue };
		var dir = rayDirection.Normal;
		if ( dir.LengthSquared < 1e-8f )
			return invalid;

		if ( !TryClosestPointsRaySegment(
			     rayOrigin,
			     dir,
			     worldA,
			     worldB,
			     out var pointOnRay,
			     out var pointOnSeg,
			     out var along ) )
			return invalid;

		if ( along < MinAlongRay || along > maxRange )
			return invalid;

		var perp = Vector3.DistanceBetween( pointOnRay, pointOnSeg );
		if ( perp > SnapReachRadius )
			return invalid;

		var camDist = Vector3.DistanceBetween( rayOrigin, pointOnSeg );
		return new RayTargetScore
		{
			Perpendicular = perp,
			AlongRay = along,
			CameraDistance = camDist,
			AimLandDistance = perp,
			Combined = perp + along * AlongRayWeight + camDist * CameraDistanceWeight,
			PointOnRay = pointOnRay,
			ClosestOnTarget = pointOnSeg,
			IsValid = true,
		};
	}

	static Vector3 ClosestPointOnSegment( Vector3 point, Vector3 a, Vector3 b )
	{
		var ab = b - a;
		var lenSq = ab.LengthSquared;
		if ( lenSq < 1e-8f )
			return a;

		var t = Math.Clamp( Vector3.Dot( point - a, ab ) / lenSq, 0f, 1f );
		return a + ab * t;
	}

	/// <summary>Closest points between an infinite-capped ray and a finite segment.</summary>
	static bool TryClosestPointsRaySegment(
		Vector3 rayOrigin,
		Vector3 rayDir,
		Vector3 segA,
		Vector3 segB,
		out Vector3 pointOnRay,
		out Vector3 pointOnSeg,
		out float along )
	{
		pointOnRay = default;
		pointOnSeg = default;
		along = 0f;

		var seg = segB - segA;
		var segLenSq = seg.LengthSquared;
		if ( segLenSq < 1e-8f )
		{
			along = Vector3.Dot( segA - rayOrigin, rayDir );
			pointOnRay = rayOrigin + rayDir * Math.Max( 0f, along );
			pointOnSeg = segA;
			along = Math.Max( 0f, along );
			return true;
		}

		var r = rayOrigin - segA;
		var rdSeg = Vector3.Dot( rayDir, seg );
		var rr = 1f; // rayDir is unit
		var ss = segLenSq;
		var rDir = Vector3.Dot( r, rayDir );
		var rSeg = Vector3.Dot( r, seg );

		var denom = rr * ss - rdSeg * rdSeg;
		float t;
		float u;
		if ( Math.Abs( denom ) < 1e-8f )
		{
			u = Math.Clamp( rSeg / ss, 0f, 1f );
			t = Vector3.Dot( segA + seg * u - rayOrigin, rayDir );
		}
		else
		{
			t = ( rdSeg * rSeg - ss * rDir ) / denom;
			u = ( rr * rSeg - rdSeg * rDir ) / denom;
			if ( u < 0f )
			{
				u = 0f;
				t = Vector3.Dot( segA - rayOrigin, rayDir );
			}
			else if ( u > 1f )
			{
				u = 1f;
				t = Vector3.Dot( segB - rayOrigin, rayDir );
			}
		}

		if ( t < 0f )
			t = 0f;

		along = t;
		pointOnRay = rayOrigin + rayDir * t;
		pointOnSeg = segA + seg * u;
		return true;
	}

	public static bool IsInReach(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		Vector3 worldPoint,
		float maxRange ) =>
		ScorePoint( rayOrigin, rayDirection, worldPoint, maxRange ).IsValid;

	public static bool IsInAimLandReach(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		Vector3 aimLand,
		Vector3 worldPoint,
		float maxRange ) =>
		ScorePointToAimLand( rayOrigin, rayDirection, aimLand, worldPoint, maxRange ).IsValid;

	public static bool IsSegmentInReach(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		Vector3 worldA,
		Vector3 worldB,
		float maxRange ) =>
		ScoreSegment( rayOrigin, rayDirection, worldA, worldB, maxRange ).IsValid;

	public static bool IsGroupStillReachable(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		Vector3 targetSnapWorld,
		float maxRange ) =>
		IsInReach( rayOrigin, rayDirection, targetSnapWorld, maxRange );

	public static bool IsMateReachable(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		Vector3 builtWorld,
		Vector3 mateWorld,
		float maxRange ) =>
		IsInReach( rayOrigin, rayDirection, builtWorld, maxRange )
		|| IsInReach( rayOrigin, rayDirection, mateWorld, maxRange );

	public static bool IsMateReachableFromAim(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		Vector3 aimLand,
		Vector3 builtWorld,
		Vector3 mateWorld,
		float maxRange ) =>
		IsInAimLandReach( rayOrigin, rayDirection, aimLand, builtWorld, maxRange )
		|| IsInAimLandReach( rayOrigin, rayDirection, aimLand, mateWorld, maxRange );

	public static BuildSnapViewContext BuildViewContext(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		float maxRange,
		Vector3 aimLand,
		RayTargetScore? bestTarget )
	{
		var dir = rayDirection.Normal;
		if ( bestTarget is not { IsValid: true } best )
		{
			return new BuildSnapViewContext
			{
				RayOrigin = rayOrigin,
				RayDirection = dir,
				MaxRange = maxRange,
				CrosshairPoint = aimLand,
				HasCrosshairFocus = false,
			};
		}

		return new BuildSnapViewContext
		{
			RayOrigin = rayOrigin,
			RayDirection = dir,
			MaxRange = maxRange,
			CrosshairPoint = best.ClosestOnTarget,
			HasCrosshairFocus = true,
		};
	}
}
