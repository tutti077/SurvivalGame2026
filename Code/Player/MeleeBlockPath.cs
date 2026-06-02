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
}
