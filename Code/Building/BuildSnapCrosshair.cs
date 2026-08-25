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
	/// How close the crosshair's aim-land point must be to a seam for a snap to commit by aim —
	/// one of the two gates in <see cref="ShouldCommitSnap"/> (the other is
	/// <see cref="SnapEngageDistance"/>, on the ghost itself).
	/// </summary>
	public static float AimCommitRadius =>
		BuildModuleDimensions.SnapModuleHalfUnits * 0.425f;

	/// <summary>
	/// How far a snap may pull the held ghost to seat it. Half a metre — the piece engages while it
	/// is hovering about that close to where the seam wants it, however loosely the crosshair itself
	/// is aimed. Distance between the candidate placement and the free (unsnapped) ghost position is
	/// exactly the distance the mating snap points are apart, since both use the same rotation.
	/// </summary>
	public static float SnapEngageDistance =>
		BuildModuleDimensions.SnapModuleHalfUnits * 0.5f;

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
	/// Does the held piece snap to this seam? Two ways in, either is enough:
	/// <para>
	/// <b>Aim</b> — the crosshair's aim-land point is within <see cref="AimCommitRadius"/>
	/// of the seam. You looked right at the joint, so the joint engages.
	/// </para>
	/// <para>
	/// <b>Proximity</b> — the snap would move the ghost no more than <see cref="SnapEngageDistance"/>
	/// from where it is already hovering. The piece is nearly seated, so it seats — however loosely
	/// the crosshair is aimed. This is the distance between the mating snap points themselves
	/// (candidate and ghost share a rotation, so the centre delta and the snap-point delta are the
	/// same number). Aim alone was not enough: holding a wall against a seam while the crosshair
	/// rested a metre down the face left a perfectly seated ghost refusing to click in.
	/// </para>
	/// </summary>
	public static bool ShouldCommitSnap( BuildSnapCandidate candidate, Vector3 freePlacementPosition )
	{
		if ( !candidate.RayScore.IsValid )
			return false;

		var piece = candidate.TargetPiece;
		if ( piece is null || !piece.IsValid() || !piece.GameObject.IsValid() )
			return false;

		if ( candidate.RayScore.AimLandDistance <= AimCommitRadius )
			return true;

		return Vector3.DistanceBetween( candidate.Placement.Position, freePlacementPosition )
		       <= SnapEngageDistance;
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
