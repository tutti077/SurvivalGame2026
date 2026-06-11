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

	/// <summary>Half module size — ray must pass within this distance (50% of 1.5 m piece).</summary>
	public static float SnapReachRadius => BuildModuleDimensions.ModuleHalfUnits;

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
		var best = new RayTargetScore { Combined = float.MaxValue, Perpendicular = float.MaxValue };
		for ( var t = 0f; t <= 1.001f; t += 0.125f )
		{
			var sample = ScorePoint( rayOrigin, rayDirection, Vector3.Lerp( worldA, worldB, t, false ), maxRange );
			if ( !sample.IsValid )
				continue;

			if ( sample.Perpendicular < best.Perpendicular - 0.01f
			     || ( Math.Abs( sample.Perpendicular - best.Perpendicular ) <= 0.01f
			          && sample.Combined < best.Combined ) )
			{
				best = sample with { ClosestOnTarget = Vector3.Lerp( worldA, worldB, t, false ) };
			}
		}

		return best;
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
