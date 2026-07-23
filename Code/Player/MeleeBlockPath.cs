using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Facing-arc body shell for blocks: ray must enter a body-radius sector (± half front arc) before the body hit.
/// Teardrop direction is not used for geometry — only facing yaw + body radius.
/// </summary>
public static class MeleeBlockPath
{
	public static float GetBodyShellRadius( PlayerCombat pc ) =>
		Math.Max( 4f, pc.MeleeBlockBodyRadius );

	public static void GetFrontArcAngleRange( PlayerCombat pc, out float startDeg, out float endDeg )
	{
		var half = MeleeBlockResolution.GetFrontHalfArcDegrees( pc );
		startDeg = -half;
		endDeg = half;
	}

	/// <summary>Vertical posts around the body shell perimeter (debug viz).</summary>
	public static int BuildGuardSamples( PlayerCombat pc, byte combatDir, int sampleCount, Span<Vector3> samples )
	{
		_ = combatDir;
		if ( !pc.GameObject.IsValid() || samples.Length < 2 )
			return 0;

		sampleCount = Math.Clamp( sampleCount, 2, samples.Length );
		GetFrontArcAngleRange( pc, out var startDeg, out var endDeg );
		var basis = pc.GetBlockCombatBasisRotation();
		var origin = pc.GameObject.WorldPosition;
		var radius = GetBodyShellRadius( pc );
		var yMin = pc.BlockGroundArcHeightOffset;
		var yMax = pc.ServerEyeHeight + Math.Max( 0f, pc.BlockShellHeightPadding );

		for ( var i = 0; i < sampleCount; i++ )
		{
			var t = sampleCount <= 1 ? 0f : i / (float)(sampleCount - 1);
			var ang = startDeg + (endDeg - startDeg) * t;
			var rad = ang * MathF.PI / 180f;
			var localLow = new Vector3( MathF.Cos( rad ) * radius, yMin, MathF.Sin( rad ) * radius );
			// Alternate low/high along the rim so the polyline reads as a shell edge.
			var local = (i & 1) == 0
				? localLow
				: new Vector3( localLow.x, yMax, localLow.z );
			samples[i] = LocalCombatToWorld( origin, basis, local );
		}

		return sampleCount;
	}

	public static void EnumerateGroundArcSegments(
		PlayerCombat pc,
		byte blockDir,
		int segments,
		Action<Vector3, Vector3> onSegment )
	{
		_ = blockDir;
		if ( onSegment is null || !pc.GameObject.IsValid() )
			return;

		var basis = pc.GetBlockCombatBasisRotation();
		var origin = pc.GameObject.WorldPosition + Vector3.Up * pc.BlockGroundArcHeightOffset;
		var radius = GetBodyShellRadius( pc );
		GetFrontArcAngleRange( pc, out var startDeg, out var endDeg );
		segments = Math.Max( 4, segments );

		Vector3 PointAt( float deg )
		{
			var rad = deg * MathF.PI / 180f;
			var local = new Vector3( MathF.Cos( rad ) * radius, 0f, MathF.Sin( rad ) * radius );
			return LocalCombatToWorld( origin, basis, local );
		}

		var prev = PointAt( startDeg );
		for ( var i = 1; i <= segments; i++ )
		{
			var t = i / (float)segments;
			var angle = startDeg + (endDeg - startDeg) * t;
			var next = PointAt( angle );
			onSegment( prev, next );
			prev = next;
		}
	}

	public static void GetGroundArcAngleRange( PlayerCombat pc, byte blockDir, out float startDeg, out float endDeg )
	{
		_ = blockDir;
		GetFrontArcAngleRange( pc, out startDeg, out endDeg );
	}

	static Vector3 LocalCombatToWorld( Vector3 origin, Rotation combatBasis, Vector3 local ) =>
		origin
		+ combatBasis.Forward * local.x
		+ combatBasis.Up * local.y
		+ combatBasis.Right * local.z;

	/// <summary>First point along the ray inside the body-radius front shell, before <paramref name="beforeDistance"/>.</summary>
	public static bool RayEntersFootprintBeforeDistance(
		PlayerCombat defender,
		Vector3 rayOrigin,
		Vector3 rayEnd,
		float beforeDistance,
		float extraThickness )
	{
		return TryGetFirstFootprintEntryBeforeDistance(
			defender, rayOrigin, rayEnd, beforeDistance, extraThickness, out _, out _ );
	}

	public static bool TryGetFirstFootprintEntryBeforeDistance(
		PlayerCombat defender,
		Vector3 rayOrigin,
		Vector3 rayEnd,
		float beforeDistance,
		float extraThickness,
		out float distanceAlongRay,
		out Vector3 hitPosition )
	{
		distanceAlongRay = 0f;
		hitPosition = default;

		if ( defender is null || !defender.GameObject.IsValid() || !defender.IsAuthoritativeMeleeBlocking )
			return false;

		var delta = rayEnd - rayOrigin;
		var lineLen = delta.Length;
		if ( lineLen < 1e-4f )
			return false;

		var rayDir = delta / lineLen;
		var pad = Math.Max( 0f, extraThickness );
		var stepLen = Math.Max( 1f, Math.Max( 0.5f, defender.BlockSampleSphereRadius + pad ) * 0.5f );
		var maxDist = Math.Min( lineLen, Math.Max( 0f, beforeDistance ) );
		var steps = Math.Max( 1, (int)MathF.Ceiling( maxDist / stepLen ) );

		for ( var i = 1; i <= steps; i++ )
		{
			var dist = maxDist * (i / (float)steps);
			var point = rayOrigin + rayDir * dist;
			if ( !IsPointInsideActiveBlockFootprint( defender, point, pad ) )
				continue;

			distanceAlongRay = dist;
			hitPosition = point;
			return true;
		}

		return false;
	}

	/// <summary>Body-shell entry before the body cutoff (replaces the old extended guard-line raycast).</summary>
	public static bool TryRaycastGuardLine(
		PlayerCombat defender,
		Vector3 rayOrigin,
		Vector3 rayEnd,
		float beforeDistance,
		float extraThickness,
		out float distanceAlongRay,
		out Vector3 hitPosition ) =>
		TryGetFirstFootprintEntryBeforeDistance(
			defender, rayOrigin, rayEnd, beforeDistance, extraThickness, out distanceAlongRay, out hitPosition );

	/// <summary>Alias kept for callers — body-shell entry before body distance.</summary>
	public static bool TryRaycastBlockGuardLine(
		PlayerCombat defender,
		Vector3 rayOrigin,
		Vector3 rayEnd,
		float beforeBodyDistance,
		float extraThickness,
		out float guardLineDistance,
		out Vector3 guardLinePosition ) =>
		TryGetFirstFootprintEntryBeforeDistance(
			defender, rayOrigin, rayEnd, beforeBodyDistance, extraThickness, out guardLineDistance, out guardLinePosition );

	public static void GetActiveBlockFootprintAngleRange( PlayerCombat pc, byte blockDir, out float startDeg, out float endDeg )
	{
		_ = blockDir;
		GetFrontArcAngleRange( pc, out startDeg, out endDeg );
	}

	public static bool IsPointInsideActiveBlockFootprint( PlayerCombat pc, Vector3 pointWorld, float extraThickness = 0f )
	{
		if ( pc is null || !pc.GameObject.IsValid() || !pc.IsAuthoritativeMeleeBlocking )
			return false;

		return IsPointInsideActiveBlockFootprintCore( pc, pointWorld, extraThickness );
	}

	static bool IsPointInsideActiveBlockFootprintCore( PlayerCombat pc, Vector3 pointWorld, float extraThickness )
	{
		var basis = pc.GetBlockCombatBasisRotation();
		var root = pc.GameObject.WorldPosition;
		var to = pointWorld - root;
		var localForward = Vector3.Dot( to, basis.Forward );
		var localRight = Vector3.Dot( to, basis.Right );
		var localUp = Vector3.Dot( to, basis.Up );

		// Allow a tiny behind-origin pad so near-side hits still register on the shell.
		if ( localForward < -Math.Max( 1f, extraThickness ) - 1e-4f )
			return false;

		var angleDeg = MathF.Atan2( localRight, localForward ) * (180f / MathF.PI);
		if ( MeleeBlockResolution.IsOutsideFrontArc( pc, angleDeg ) )
			return false;

		var radial = MathF.Sqrt( localForward * localForward + localRight * localRight );
		var radialMax = GetBodyShellRadius( pc ) + extraThickness;
		if ( radial > radialMax + 1e-4f )
			return false;

		GetActiveBlockFootprintVerticalRange( pc, 0, extraThickness, out var yMin, out var yMax );
		return localUp >= yMin - 1e-4f && localUp <= yMax + 1e-4f;
	}

	public static void GetActiveBlockFootprintVerticalRange(
		PlayerCombat pc,
		byte blockDir,
		float extraThickness,
		out float yMin,
		out float yMax )
	{
		_ = blockDir;
		var pad = Math.Max( 0f, extraThickness );
		var samplePad = Math.Max( 1f, pc.BlockSampleSphereRadius );
		yMin = pc.BlockGroundArcHeightOffset - pad;
		yMax = pc.ServerEyeHeight + Math.Max( 0f, pc.BlockShellHeightPadding ) + samplePad + pad;
	}

	static Vector3 FootprintPointToWorld( Vector3 root, Rotation basis, float angleDeg, float radial, float localUp )
	{
		var rad = angleDeg * MathF.PI / 180f;
		var local = new Vector3( MathF.Cos( rad ) * radial, localUp, MathF.Sin( rad ) * radial );
		return LocalCombatToWorld( root, basis, local );
	}

	public static Vector3 GetFootprintPointWorld(
		PlayerCombat pc,
		byte blockDir,
		float angleDeg,
		float radial,
		float localUp )
	{
		_ = blockDir;
		var root = pc.GameObject.WorldPosition;
		var basis = pc.GetBlockCombatBasisRotation();
		return FootprintPointToWorld( root, basis, angleDeg, radial, localUp );
	}

	public static bool RayHitsActiveGuardBeforeDistance(
		PlayerCombat defender,
		Vector3 rayOrigin,
		Vector3 rayEnd,
		float bodyHitDistanceAlongRay,
		float extraThickness,
		out Vector3 guardHitPosition ) =>
		TryRaycastBlockGuardLine( defender, rayOrigin, rayEnd, bodyHitDistanceAlongRay, extraThickness,
			out _, out guardHitPosition );

	public static void EnumerateFootprintSolidTriangles(
		PlayerCombat pc,
		byte blockDir,
		Action<Vector3, Vector3, Vector3> emitTriangle )
	{
		_ = blockDir;
		if ( emitTriangle is null || pc is null || !pc.GameObject.IsValid() )
			return;

		var root = pc.GameObject.WorldPosition;
		var basis = pc.GetBlockCombatBasisRotation();
		GetFrontArcAngleRange( pc, out var arcStartDeg, out var arcEndDeg );
		GetActiveBlockFootprintVerticalRange( pc, 0, 0f, out var yMin, out var yMax );
		var radialMax = GetBodyShellRadius( pc );

		var arcSpan = MathF.Max( 1f, arcEndDeg - arcStartDeg );
		var angleSteps = Math.Clamp( (int)MathF.Ceiling( arcSpan / 10f ), 8, 28 );
		var originLow = root + basis.Up * yMin;
		var originHigh = root + basis.Up * yMax;

		for ( var i = 0; i < angleSteps; i++ )
		{
			var t0 = i / (float)angleSteps;
			var t1 = (i + 1) / (float)angleSteps;
			var ang0 = arcStartDeg + arcSpan * t0;
			var ang1 = arcStartDeg + arcSpan * t1;

			var aLow = FootprintPointToWorld( root, basis, ang0, radialMax, yMin );
			var bLow = FootprintPointToWorld( root, basis, ang1, radialMax, yMin );
			var aHigh = FootprintPointToWorld( root, basis, ang0, radialMax, yMax );
			var bHigh = FootprintPointToWorld( root, basis, ang1, radialMax, yMax );

			emitTriangle( originLow, aLow, bLow );
			emitTriangle( originHigh, bHigh, aHigh );
			emitTriangle( aLow, aHigh, bHigh );
			emitTriangle( aLow, bHigh, bLow );

			if ( i == 0 )
			{
				emitTriangle( originLow, originHigh, aHigh );
				emitTriangle( originLow, aHigh, aLow );
			}

			if ( i == angleSteps - 1 )
			{
				emitTriangle( originLow, bLow, originHigh );
				emitTriangle( originLow, originHigh, bHigh );
			}
		}
	}

	public static float ProjectDistanceAlongRay( Vector3 rayOrigin, Vector3 rayUnitDir, Vector3 worldPoint )
	{
		var along = Vector3.Dot( worldPoint - rayOrigin, rayUnitDir );
		return MathF.Max( 0f, along );
	}
}
