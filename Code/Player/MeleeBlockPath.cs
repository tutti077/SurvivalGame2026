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
	/// First point along <paramref name="rayOrigin"/>→<paramref name="rayEnd"/> that enters the held guard line
	/// (same samples as debug viz), expanded by guard sphere radius + <paramref name="extraThickness"/>.
	/// </summary>
	public static bool TryRaycastActiveGuardVolume(
		PlayerCombat defender,
		Vector3 rayOrigin,
		Vector3 rayEnd,
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
		var sampleCount = defender.GetBlockGuardSampleCount();
		Span<Vector3> samples = stackalloc Vector3[48];
		var count = BuildGuardSamples( defender, blockDir, sampleCount, samples );
		if ( count < 1 )
			return false;

		var hitRadius = Math.Max( 0.5f, defender.BlockSampleSphereRadius + Math.Max( 0f, extraThickness ) );
		var stepLen = Math.Max( 0.75f, hitRadius * 0.5f );
		var steps = Math.Max( 1, (int)MathF.Ceiling( lineLen / stepLen ) );

		for ( var i = 1; i <= steps; i++ )
		{
			var dist = lineLen * (i / (float)steps);
			var point = rayOrigin + rayDir * dist;
			if ( DistancePointToGuardPolyline( point, samples, count ) > hitRadius + 1e-4f )
				continue;

			distanceAlongRay = dist;
			hitPosition = point;
			return true;
		}

		return false;
	}

	/// <summary>True when the active guard is struck along the ray before <paramref name="bodyHitDistanceAlongRay"/>.</summary>
	public static bool RayHitsActiveGuardBeforeDistance(
		PlayerCombat defender,
		Vector3 rayOrigin,
		Vector3 rayEnd,
		float bodyHitDistanceAlongRay,
		float extraThickness,
		out Vector3 guardHitPosition )
	{
		guardHitPosition = default;
		if ( !TryRaycastActiveGuardVolume( defender, rayOrigin, rayEnd, extraThickness, out var guardDist, out guardHitPosition ) )
			return false;

		return guardDist <= bodyHitDistanceAlongRay + 1e-4f;
	}

	public static float ProjectDistanceAlongRay( Vector3 rayOrigin, Vector3 rayUnitDir, Vector3 worldPoint )
	{
		var along = Vector3.Dot( worldPoint - rayOrigin, rayUnitDir );
		return MathF.Max( 0f, along );
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
}
