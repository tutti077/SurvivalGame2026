using System;
using Sandbox;

namespace Survival;

/// <summary>Scores how close a built snap is to the view ray (crosshair wins over standing distance).</summary>
public static class BuildSnapCrosshair
{
	public const float MinAlongRay = 4f;
	public const float AlongRayWeight = 0.015f;
	/// <summary>Tiebreaker only — ray perpendicular distance is the primary rank.</summary>
	public const float CameraDistanceWeight = 0.002f;

	/// <summary>Full module reach so aiming near tile centers still finds neighboring seams.</summary>
	public static float SnapReachRadius =>
		BuildModuleDimensions.SnapModuleHalfUnits * 2f;

	public readonly struct RayTargetScore
	{
		public float Perpendicular { get; init; }
		public float AlongRay { get; init; }
		public float CameraDistance { get; init; }
		public float Combined { get; init; }
		public Vector3 PointOnRay { get; init; }
		public Vector3 ClosestOnTarget { get; init; }
		public bool IsValid { get; init; }
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
			Combined = perp + along * AlongRayWeight + camDist * CameraDistanceWeight,
			PointOnRay = pointOnRay,
			ClosestOnTarget = pointOnSeg,
			IsValid = true,
		};
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
			// Parallel — clamp segment param from ray origin projection.
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

	public static BuildSnapViewContext BuildViewContext(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		float maxRange,
		RayTargetScore? bestTarget )
	{
		var dir = rayDirection.Normal;
		var fallback = rayOrigin + dir * Math.Min( 192f, maxRange * 0.5f );
		if ( bestTarget is not { IsValid: true } best )
		{
			return new BuildSnapViewContext
			{
				RayOrigin = rayOrigin,
				RayDirection = dir,
				MaxRange = maxRange,
				CrosshairPoint = fallback,
				HasCrosshairFocus = false,
			};
		}

		return new BuildSnapViewContext
		{
			RayOrigin = rayOrigin,
			RayDirection = dir,
			MaxRange = maxRange,
			CrosshairPoint = best.PointOnRay,
			HasCrosshairFocus = true,
		};
	}
}
