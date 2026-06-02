using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Directional block guard samples and ground-zone arcs in combat-local space (+X forward, +Y up, +Z right).
/// Guard line and feet arc share the same edge angles and transform with live view yaw each frame.
/// </summary>
public static class MeleeBlockPath
{
	public static int BuildGuardSamples( PlayerCombat pc, byte combatDir, int sampleCount, Span<Vector3> samples )
	{
		if ( !pc.GameObject.IsValid() || samples.Length < 2 )
			return 0;

		if ( combatDir is not (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up) )
			combatDir = SwingDirs.Up;

		sampleCount = Math.Clamp( sampleCount, 2, samples.Length );

		return combatDir switch
		{
			SwingDirs.Up => BuildOverheadSamples( pc, sampleCount, samples ),
			_ => BuildLateralSamples( pc, combatDir, sampleCount, samples )
		};
	}

	static int BuildOverheadSamples( PlayerCombat pc, int sampleCount, Span<Vector3> samples )
	{
		var basis = pc.GetBlockCombatBasisRotation();
		var origin = pc.GameObject.WorldPosition;
		var lineLength = pc.BlockLineLength;
		var chestY = pc.ServerEyeHeight;
		var centerLocal = new Vector3(
			pc.BlockOverheadForwardOffset,
			chestY + pc.BlockOverheadUpOffset,
			0f );
		var axisLocal = new Vector3( 0f, 0f, 1f );

		for ( var i = 0; i < sampleCount; i++ )
		{
			var t = sampleCount <= 1 ? 0f : i / (float)(sampleCount - 1);
			var local = centerLocal + axisLocal * ((t - 0.5f) * lineLength);
			samples[i] = LocalCombatToWorld( origin, basis, local );
		}

		return sampleCount;
	}

	static int BuildLateralSamples( PlayerCombat pc, byte lateralSide, int sampleCount, Span<Vector3> samples )
	{
		var basis = pc.GetBlockCombatBasisRotation();
		var origin = pc.GameObject.WorldPosition;
		var lineLength = pc.BlockLineLength;
		GetLateralBlockAngles( pc, lateralSide, out _, out _, out var guardEdgeDeg );

		var rad = guardEdgeDeg * MathF.PI / 180f;
		var reach = pc.BlockGuardReach;
		var centerLocal = new Vector3(
			MathF.Cos( rad ) * reach,
			pc.ServerEyeHeight,
			MathF.Sin( rad ) * reach );

		for ( var i = 0; i < sampleCount; i++ )
		{
			var t = sampleCount <= 1 ? 0f : i / (float)(sampleCount - 1);
			var local = centerLocal + new Vector3( 0f, (t - 0.5f) * lineLength, 0f );
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
		if ( onSegment is null || !pc.GameObject.IsValid() )
			return;

		var basis = pc.GetBlockCombatBasisRotation();
		var origin = pc.GameObject.WorldPosition + Vector3.Up * pc.BlockGroundArcHeightOffset;
		var radius = Math.Max( 8f, pc.BlockGroundArcRadius );

		GetGroundArcAngleRange( pc, blockDir, out var startDeg, out var endDeg );
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
		if ( blockDir == SwingDirs.Up )
		{
			var half = pc.MeleeBlockOverheadHalfArcDegrees;
			startDeg = -half;
			endDeg = half;
			return;
		}

		GetLateralBlockAngles( pc, blockDir, out startDeg, out endDeg, out _ );
	}

	/// <summary>
	/// Shared feet-arc span and guard-line edge. Matches teardrop L/R (same side as vertical guard).
	/// In this project +Z combat-right / +angle maps to teardrop-left guard side.
	/// </summary>
	public static void GetLateralBlockAngles( PlayerCombat pc, byte blockDir, out float arcStartDeg, out float arcEndDeg, out float guardEdgeDeg )
	{
		var half = pc.MeleeBlockLateralHalfArcDegrees;
		if ( blockDir == SwingDirs.Left )
		{
			arcStartDeg = 0f;
			arcEndDeg = half;
			guardEdgeDeg = half;
			return;
		}

		arcStartDeg = -half;
		arcEndDeg = 0f;
		guardEdgeDeg = -half;
	}

	static Vector3 LocalCombatToWorld( Vector3 origin, Rotation combatBasis, Vector3 local ) =>
		origin
		+ combatBasis.Forward * local.x
		+ combatBasis.Up * local.y
		+ combatBasis.Right * local.z;

	/// <summary>
	/// First point along the ray inside the footprint wedge (same side/angles as guard line + ground arc).
	/// </summary>
	public static bool RayEntersFootprintBeforeDistance(
		PlayerCombat defender,
		Vector3 rayOrigin,
		Vector3 rayEnd,
		float beforeDistance,
		float extraThickness )
	{
		if ( defender is null || !defender.GameObject.IsValid() || !defender.IsAuthoritativeMeleeBlocking )
			return false;

		var blockDir = defender.AuthoritativeMeleeBlockDirection;
		if ( blockDir is not (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up) )
			return false;

		var delta = rayEnd - rayOrigin;
		var lineLen = delta.Length;
		if ( lineLen < 1e-4f )
			return false;

		var rayDir = delta / lineLen;
		var pad = Math.Max( 0f, extraThickness );
		var stepLen = Math.Max( 1.5f, Math.Max( 0.75f, defender.BlockSampleSphereRadius + pad ) * 0.5f );
		var maxDist = Math.Min( lineLen, Math.Max( 0f, beforeDistance ) );
		var steps = Math.Max( 1, (int)MathF.Ceiling( maxDist / stepLen ) );

		for ( var i = 1; i <= steps; i++ )
		{
			var dist = maxDist * (i / (float)steps);
			var point = rayOrigin + rayDir * dist;
			if ( IsPointInsideActiveBlockFootprint( defender, blockDir, point, pad ) )
				return true;
		}

		return false;
	}

	/// <summary>
	/// First hit on the held guard polyline along the ray, up to <paramref name="beforeDistance"/>.
	/// </summary>
	public static bool TryRaycastGuardLine(
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

		var blockDir = defender.AuthoritativeMeleeBlockDirection;
		if ( blockDir is not (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up) )
			return false;

		var delta = rayEnd - rayOrigin;
		var lineLen = delta.Length;
		if ( lineLen < 1e-4f )
			return false;

		var rayDir = delta / lineLen;
		var pad = Math.Max( 0f, extraThickness );
		var hitRadius = Math.Max( 0.5f, defender.BlockSampleSphereRadius + pad );
		var sampleCount = defender.GetBlockGuardSampleCount();
		Span<Vector3> samples = stackalloc Vector3[48];
		var count = BuildGuardSamples( defender, blockDir, sampleCount, samples );
		if ( count < 1 )
			return false;

		var stepLen = Math.Max( 1f, hitRadius * 0.5f );
		var maxDist = Math.Min( lineLen, Math.Max( 0f, beforeDistance ) );
		var steps = Math.Max( 1, (int)MathF.Ceiling( maxDist / stepLen ) );
		var bestDist = float.MaxValue;

		for ( var i = 1; i <= steps; i++ )
		{
			var dist = maxDist * (i / (float)steps);
			var point = rayOrigin + rayDir * dist;
			if ( DistancePointToGuardPolyline( point, samples, count ) > hitRadius + 1e-4f )
				continue;
			if ( dist >= bestDist )
				continue;

			bestDist = dist;
			distanceAlongRay = dist;
			hitPosition = point;
		}

		return bestDist < float.MaxValue;
	}

	/// <summary>
	/// Block only when the ray enters the footprint wedge, then hits the guard line before the body cutoff.
	/// </summary>
	public static bool TryRaycastBlockGuardLine(
		PlayerCombat defender,
		Vector3 rayOrigin,
		Vector3 rayEnd,
		float beforeBodyDistance,
		float extraThickness,
		out float guardLineDistance,
		out Vector3 guardLinePosition )
	{
		guardLineDistance = 0f;
		guardLinePosition = default;

		if ( !RayEntersFootprintBeforeDistance( defender, rayOrigin, rayEnd, beforeBodyDistance, extraThickness ) )
			return false;

		return TryRaycastGuardLine( defender, rayOrigin, rayEnd, beforeBodyDistance, extraThickness,
			out guardLineDistance, out guardLinePosition );
	}

	/// <summary>Horizontal wedge — same angles as guard line + ground arc (teardrop side).</summary>
	public static void GetActiveBlockFootprintAngleRange( PlayerCombat pc, byte blockDir, out float startDeg, out float endDeg ) =>
		GetGroundArcAngleRange( pc, blockDir, out startDeg, out endDeg );

	public static bool IsPointInsideActiveBlockFootprint( PlayerCombat pc, Vector3 pointWorld, float extraThickness = 0f )
	{
		if ( pc is null || !pc.GameObject.IsValid() || !pc.IsAuthoritativeMeleeBlocking )
			return false;

		var blockDir = pc.AuthoritativeMeleeBlockDirection;
		if ( blockDir is not (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up) )
			return false;

		return IsPointInsideActiveBlockFootprint( pc, blockDir, pointWorld, extraThickness );
	}

	static bool IsPointInsideActiveBlockFootprint( PlayerCombat pc, byte blockDir, Vector3 pointWorld, float extraThickness )
	{
		var basis = pc.GetBlockCombatBasisRotation();
		var root = pc.GameObject.WorldPosition;
		var to = pointWorld - root;
		var localForward = Vector3.Dot( to, basis.Forward );
		var localRight = Vector3.Dot( to, basis.Right );
		var localUp = Vector3.Dot( to, basis.Up );

		if ( localForward < -Math.Max( 1f, extraThickness ) - 1e-4f )
			return false;

		var angleDeg = MathF.Atan2( localRight, localForward ) * (180f / MathF.PI);
		if ( blockDir == SwingDirs.Up )
		{
			if ( MeleeBlockResolution.IsInOverheadBackArc( pc, angleDeg ) )
				return false;
		}
		else if ( MeleeBlockResolution.IsInLateralBackArc( pc, angleDeg ) )
		{
			return false;
		}

		GetActiveBlockFootprintAngleRange( pc, blockDir, out var arcStartDeg, out var arcEndDeg );
		if ( !IsAngleBetweenInclusive( angleDeg, arcStartDeg, arcEndDeg ) )
			return false;

		var radial = MathF.Sqrt( localForward * localForward + localRight * localRight );
		var radialMax = Math.Max( 8f, pc.BlockGroundArcRadius ) + extraThickness;
		if ( radial > radialMax + 1e-4f )
			return false;

		GetActiveBlockFootprintVerticalRange( pc, blockDir, extraThickness, out var yMin, out var yMax );
		return localUp >= yMin - 1e-4f && localUp <= yMax + 1e-4f;
	}

	public static float GetOverheadGuardLineHeight( PlayerCombat pc ) =>
		pc.ServerEyeHeight + pc.BlockOverheadUpOffset;

	public static void GetActiveBlockFootprintVerticalRange(
		PlayerCombat pc,
		byte blockDir,
		float extraThickness,
		out float yMin,
		out float yMax )
	{
		var pad = Math.Max( 0f, extraThickness );
		var samplePad = Math.Max( 1f, pc.BlockSampleSphereRadius );
		yMin = pc.BlockGroundArcHeightOffset - pad;
		// L/R/U share the same vertical span: feet arc → overhead guard line height.
		yMax = GetOverheadGuardLineHeight( pc ) + samplePad + pad;
	}

	static bool IsAngleBetweenInclusive( float angleDeg, float startDeg, float endDeg ) =>
		angleDeg >= startDeg - 1e-4f && angleDeg <= endDeg + 1e-4f;

	static Vector3 FootprintPointToWorld( Vector3 root, Rotation basis, float angleDeg, float radial, float localUp )
	{
		var rad = angleDeg * MathF.PI / 180f;
		var local = new Vector3( MathF.Cos( rad ) * radial, localUp, MathF.Sin( rad ) * radial );
		return LocalCombatToWorld( root, basis, local );
	}

	/// <summary>World point on the block footprint wedge (combat-local horizontal angle + radial reach + height).</summary>
	public static Vector3 GetFootprintPointWorld(
		PlayerCombat pc,
		byte blockDir,
		float angleDeg,
		float radial,
		float localUp )
	{
		var root = pc.GameObject.WorldPosition;
		var basis = pc.GetBlockCombatBasisRotation();
		return FootprintPointToWorld( root, basis, angleDeg, radial, localUp );
	}

	/// <summary>True when footprint is entered and the guard line is hit before the body cutoff.</summary>
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
		if ( emitTriangle is null || pc is null || !pc.GameObject.IsValid() )
			return;

		if ( blockDir is not (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up) )
			return;

		var root = pc.GameObject.WorldPosition;
		var basis = pc.GetBlockCombatBasisRotation();
		GetActiveBlockFootprintAngleRange( pc, blockDir, out var arcStartDeg, out var arcEndDeg );
		GetActiveBlockFootprintVerticalRange( pc, blockDir, 0f, out var yMin, out var yMax );
		var radialMax = Math.Max( 8f, pc.BlockGroundArcRadius );

		var arcSpan = MathF.Max( 1f, arcEndDeg - arcStartDeg );
		var angleSteps = Math.Clamp( (int)MathF.Ceiling( arcSpan / 8f ), 6, 20 );
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

	static float DistancePointToGuardPolyline( Vector3 point, Span<Vector3> samples, int count )
	{
		var best = float.MaxValue;
		for ( var i = 0; i < count; i++ )
			best = MathF.Min( best, point.Distance( samples[i] ) );

		for ( var i = 1; i < count; i++ )
			best = MathF.Min( best, DistancePointToSegment( point, samples[i - 1], samples[i] ) );

		return best;
	}

	static float DistancePointToSegment( Vector3 point, Vector3 a, Vector3 b )
	{
		var ab = b - a;
		var lenSq = ab.LengthSquared;
		if ( lenSq < 1e-8f )
			return point.Distance( a );

		var t = Math.Clamp( Vector3.Dot( point - a, ab ) / lenSq, 0f, 1f );
		return point.Distance( a + ab * t );
	}

	public static float ProjectDistanceAlongRay( Vector3 rayOrigin, Vector3 rayUnitDir, Vector3 worldPoint )
	{
		var along = Vector3.Dot( worldPoint - rayOrigin, rayUnitDir );
		return MathF.Max( 0f, along );
	}
}
