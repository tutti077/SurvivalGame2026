using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Shared player-local combat-space attack path for host hits and debug overlay on <see cref="PlayerCombat"/>.
/// Local axes: +X forward, +Y up, +Z right — L/R uses yaw-only horizontal basis; overhead uses yaw + influenced pitch.
/// </summary>
public static class MeleeAttackPath
{
	const float Deg2Rad = MathF.PI / 180f;

	public static float GetActiveDurationSeconds( PlayerCombat pc, byte attackType )
	{
		GetPhaseDurations( pc, attackType, out var early, out var active, out var late );
		return Math.Max( 0.04f, early + active + late );
	}

	public static void GetPhaseDurations( PlayerCombat pc, byte attackType, out float early, out float active, out float late )
	{
		early = Math.Max( 0f, pc.MeleeEarlyActiveDuration );
		active = Math.Max( 0f, pc.MeleeActiveDuration );
		late = Math.Max( 0f, pc.MeleeLateActiveDuration );
	}

	public static float GetAttackRange( PlayerCombat pc, byte attackType ) =>
		attackType == MeleeAttackTypes.Forward
			? Math.Max( 8f, pc.MeleeAttackRangeForward )
			: Math.Max( 8f, pc.MeleeAttackRangeLeftRight );

	public static void GetArcDegreeSpan( PlayerCombat pc, byte attackType, out float startDeg, out float endDeg )
	{
		if ( attackType == MeleeAttackTypes.Left )
		{
			var half = Math.Max( 1f, pc.MeleeLateralArcTotalDegrees ) * 0.5f;
			startDeg = -half;
			endDeg = half;
			return;
		}

		if ( attackType == MeleeAttackTypes.Right )
		{
			var half = Math.Max( 1f, pc.MeleeLateralArcTotalDegrees ) * 0.5f;
			startDeg = half;
			endDeg = -half;
			return;
		}

		GetForwardArcDegreeSpan( pc, out startDeg, out endDeg );
	}

	static void GetForwardArcDegreeSpan( PlayerCombat pc, out float startDeg, out float endDeg )
	{
		var total = Math.Clamp( pc.MeleeAttackForwardArcTotalDegrees, 90f, 180f );
		startDeg = pc.MeleeAttackForwardArcStartDegrees;
		endDeg = startDeg - total;
	}

	/// <summary>Time-based EarlyActive / Active / LateActive from elapsed seconds after the active swing begins.</summary>
	public static byte ClassifyActiveState( PlayerCombat pc, byte attackType, float activeElapsedSeconds )
	{
		activeElapsedSeconds = Math.Max( 0f, activeElapsedSeconds );
		GetPhaseDurations( pc, attackType, out var earlyDur, out var activeDur, out _ );
		var earlyEnd = earlyDur;
		var activeEnd = earlyDur + activeDur;
		if ( activeElapsedSeconds <= earlyEnd + 1e-5f )
			return MeleeAttackStates.EarlyActive;
		if ( activeElapsedSeconds <= activeEnd + 1e-5f )
			return MeleeAttackStates.Active;
		return MeleeAttackStates.LateActive;
	}

	/// <summary>Time-based state from normalized active progress (0–1 over full active window).</summary>
	public static byte ClassifyActiveStateFromProgress( PlayerCombat pc, byte attackType, float activeProgress01 )
	{
		activeProgress01 = Math.Clamp( activeProgress01, 0f, 1f );
		var total = GetActiveDurationSeconds( pc, attackType );
		return ClassifyActiveState( pc, attackType, activeProgress01 * total );
	}

	/// <summary>Elapsed seconds (from active start) where EarlyActive→Active and Active→LateActive begin.</summary>
	public static void GetPhaseBoundaryElapsedSeconds( PlayerCombat pc, byte attackType, out float activePhaseStart, out float latePhaseStart )
	{
		GetPhaseDurations( pc, attackType, out var earlyDur, out var activeDur, out _ );
		activePhaseStart = earlyDur;
		latePhaseStart = earlyDur + activeDur;
	}

	public static float ActiveProgressFromElapsed( PlayerCombat pc, byte attackType, float activeElapsedSeconds )
	{
		var total = GetActiveDurationSeconds( pc, attackType );
		if ( total <= 1e-6f )
			return 0f;
		return Math.Clamp( activeElapsedSeconds / total, 0f, 1f );
	}

	public static void EvaluateWorldBlade(
		GameObject attacker,
		PlayerCombat pc,
		byte attackType,
		float arcProgress01,
		out Vector3 tipWorld,
		out Vector3 heelWorld )
	{
		EvaluateWorldBlade( attacker, pc, attackType, arcProgress01, pc.GetMeleeCombatBasisRotation( attackType ), out tipWorld, out heelWorld );
	}

	public static void EvaluateWorldBlade(
		GameObject attacker,
		PlayerCombat pc,
		byte attackType,
		float arcProgress01,
		Rotation combatBasis,
		out Vector3 tipWorld,
		out Vector3 heelWorld )
	{
		EvaluateLocalBlade( pc, attackType, arcProgress01, out var tipLocal, out var heelLocal );
		TransformLocalToWorld( attacker, combatBasis, tipLocal, heelLocal, out tipWorld, out heelWorld );
	}

	public static void EvaluateLocalBlade(
		PlayerCombat pc,
		byte attackType,
		float arcProgress01,
		out Vector3 tipLocal,
		out Vector3 heelLocal )
	{
		arcProgress01 = Math.Clamp( arcProgress01, 0f, 1f );

		if ( attackType == MeleeAttackTypes.Forward )
			EvaluateForwardLocal( pc, arcProgress01, out tipLocal, out heelLocal );
		else
			EvaluateLateralLocal( pc, attackType, arcProgress01, out tipLocal, out heelLocal );
	}

	/// <summary>
	/// Side slash: arc in local XZ (forward/right); yaw-only combat basis. Height from <see cref="PlayerCombat.MeleeAttackZaxisStart"/> + tilt only.
	/// </summary>
	static void EvaluateLateralLocal(
		PlayerCombat pc,
		byte attackType,
		float t,
		out Vector3 tipLocal,
		out Vector3 heelLocal )
	{
		GetArcDegreeSpan( pc, attackType, out var startDeg, out var endDeg );
		ComputeLateralTip( pc, attackType, startDeg, endDeg, t, out tipLocal );

		var prevT = Math.Max( 0f, t - 0.04f );
		ComputeLateralTip( pc, attackType, startDeg, endDeg, prevT, out var prevTip );
		var travel = tipLocal - prevTip;
		if ( travel.LengthSquared < 1e-6f )
		{
			travel = attackType == MeleeAttackTypes.Right
				? new Vector3( 0f, 0f, -1f )
				: new Vector3( 0f, 0f, 1f );
		}

		var range = GetAttackRange( pc, attackType );
		var heelBack = Math.Clamp( pc.MeleeBladeHeelFraction, 0.04f, 0.6f ) * range;
		heelLocal = tipLocal - travel.Normal * heelBack;
	}

	/// <summary>Total vertical drop across a lateral slash from tilt degrees only (0° = none).</summary>
	static float LateralTiltVerticalDrop( float range, float tiltDegrees )
	{
		if ( tiltDegrees <= 0.01f )
			return 0f;

		var tiltRad = Math.Clamp( tiltDegrees, 0f, 89f ) * Deg2Rad;
		var maxDrop = range * 0.42f;
		return MathF.Min( range * MathF.Tan( tiltRad ) * 0.28f, maxDrop );
	}

	static void ComputeLateralTip(
		PlayerCombat pc,
		byte attackType,
		float startDeg,
		float endDeg,
		float t,
		out Vector3 tipLocal )
	{
		var range = GetAttackRange( pc, attackType );
		var angleRad = Lerp( startDeg, endDeg, t ) * Deg2Rad;

		var pivotForward = range * 0.14f;
		var arcForward = pivotForward + MathF.Cos( angleRad ) * range;
		var arcRight = MathF.Sin( angleRad ) * range;

		var tiltDeg = attackType == MeleeAttackTypes.Left
			? pc.MeleeAttackTiltDegreesLeft
			: pc.MeleeAttackTiltDegreesRight;
		var startHeight = pc.ServerEyeHeight + pc.MeleeAttackZaxisStart;
		var dropTotal = LateralTiltVerticalDrop( range, tiltDeg );
		var y = startHeight - dropTotal * t;

		tipLocal = new Vector3( arcForward, y, arcRight );
	}

	/// <summary>
	/// Overhead chop: same arc math as lateral, but in the vertical forward-up plane (+X/+Y).
	/// </summary>
	static void EvaluateForwardLocal(
		PlayerCombat pc,
		float progress01,
		out Vector3 tipLocal,
		out Vector3 heelLocal )
	{
		GetForwardArcDegreeSpan( pc, out var startDeg, out var endDeg );
		ComputeForwardTip( pc, startDeg, endDeg, progress01, out tipLocal );

		var prevProgress = Math.Max( 0f, progress01 - 0.04f );
		ComputeForwardTip( pc, startDeg, endDeg, prevProgress, out var prevTip );
		var travel = tipLocal - prevTip;
		if ( travel.LengthSquared < 1e-6f )
			travel = new Vector3( 0.55f, 0.25f, 0f );

		var range = GetAttackRange( pc, MeleeAttackTypes.Forward );
		var heelBack = Math.Clamp( pc.MeleeBladeHeelFraction, 0.04f, 0.6f ) * range;
		heelLocal = tipLocal - travel.Normal * heelBack;
	}

	/// <summary>
	/// Vertical-plane arc: angle 0° = +X forward, 90° = +Y up. Sweeps overhead (positive sin) from start→end once.
	/// </summary>
	static void ComputeForwardTip(
		PlayerCombat pc,
		float startDeg,
		float endDeg,
		float t,
		out Vector3 tipLocal )
	{
		t = Math.Clamp( t, 0f, 1f );
		var range = GetAttackRange( pc, MeleeAttackTypes.Forward );
		var reachMul = GetForwardReachMultiplier( pc, t );
		var effectiveRange = range * reachMul;

		var angleRad = Lerp( startDeg, endDeg, t ) * Deg2Rad;

		var pivotForward = effectiveRange * 0.14f;
		var pivotUp = pc.ServerEyeHeight + effectiveRange * 0.06f;
		var arcForward = pivotForward + MathF.Cos( angleRad ) * effectiveRange;
		var arcUp = pivotUp + MathF.Sin( angleRad ) * effectiveRange;

		tipLocal = new Vector3( arcForward, arcUp, pc.MeleeAttackForwardPlaneRightOffset );

		ApplyForwardPlaneTilt( ref tipLocal, pc.ServerEyeHeight, pc.MeleeAttackTiltDegreesForward * Deg2Rad );
		ApplyForwardPitchLean( ref tipLocal, pc );
		ClampForwardLocalToRange( ref tipLocal, pc, range * reachMul );
	}

	static float GetForwardReachMultiplier( PlayerCombat pc, float progress01 )
	{
		progress01 = Math.Clamp( progress01, 0f, 1f );
		var start = Math.Clamp( pc.MeleeAttackForwardReachStartMultiplier, 0.05f, 2f );
		var active = Math.Clamp( pc.MeleeAttackForwardReachActiveMultiplier, 0.05f, 2f );
		var end = Math.Clamp( pc.MeleeAttackForwardReachEndMultiplier, 0.05f, 2f );

		if ( progress01 <= 0.5f )
			return Lerp( start, active, SmoothStep( progress01 / 0.5f ) );

		return Lerp( active, end, SmoothStep( (progress01 - 0.5f) / 0.5f ) );
	}

	static void ApplyForwardPitchLean( ref Vector3 local, PlayerCombat pc )
	{
		var leanDeg = pc.GetForwardMeleePitchLeanDegrees();
		if ( MathF.Abs( leanDeg ) < 0.01f )
			return;

		var pivot = new Vector2( 0f, pc.ServerEyeHeight * 0.5f );
		local = RotateLocalXYAbout( pivot, leanDeg * Deg2Rad, local );
	}

	static Vector3 RotateLocalXYAbout( Vector2 pivot, float rad, Vector3 p )
	{
		var x = p.x - pivot.x;
		var y = p.y - pivot.y;
		var c = MathF.Cos( rad );
		var s = MathF.Sin( rad );
		return new Vector3(
			pivot.x + x * c - y * s,
			pivot.y + x * s + y * c,
			p.z );
	}

	static void ApplyForwardPlaneTilt( ref Vector3 p, float chestY, float tiltRad )
	{
		if ( MathF.Abs( tiltRad ) <= 1e-5f )
			return;

		var relY = p.y - chestY;
		var z = p.z;
		p.y = chestY + relY * MathF.Cos( tiltRad ) - z * MathF.Sin( tiltRad );
		p.z = relY * MathF.Sin( tiltRad ) + z * MathF.Cos( tiltRad );
	}

	static void ClampForwardLocalToRange( ref Vector3 local, PlayerCombat pc, float maxReach )
	{
		maxReach *= Math.Clamp( pc.MeleeAttackForwardMaxReachFraction, 0.55f, 1.05f );
		var dist = local.Length;
		if ( dist > maxReach && dist > 1e-4f )
			local *= maxReach / dist;
	}

	static float Lerp( float a, float b, float t ) => a + (b - a) * Math.Clamp( t, 0f, 1f );

	static float SmoothStep( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t);
	}

	public static void BuildArcSamples(
		GameObject attacker,
		PlayerCombat pc,
		byte attackType,
		int sampleCount,
		Span<MeleePathSample> samples )
	{
		sampleCount = Math.Clamp( sampleCount, 2, samples.Length );
		for ( var i = 0; i < sampleCount; i++ )
		{
			var t = i / (float)(sampleCount - 1);
			EvaluateWorldBlade( attacker, pc, attackType, t, out var tip, out var heel );
			samples[i] = new MeleePathSample
			{
				TipWorld = tip,
				HeelWorld = heel,
				ArcProgress01 = t,
				AttackState = ClassifyActiveStateFromProgress( pc, attackType, t )
			};
		}
	}

	static void TransformLocalToWorld(
		GameObject attacker,
		Rotation combatBasis,
		Vector3 tipLocal,
		Vector3 heelLocal,
		out Vector3 tipWorld,
		out Vector3 heelWorld )
	{
		if ( !attacker.IsValid() )
		{
			tipWorld = tipLocal;
			heelWorld = heelLocal;
			return;
		}

		var origin = attacker.WorldPosition;
		var forward = combatBasis.Forward;
		var up = combatBasis.Up;
		var right = combatBasis.Right;
		tipWorld = origin + forward * tipLocal.x + up * tipLocal.y + right * tipLocal.z;
		heelWorld = origin + forward * heelLocal.x + up * heelLocal.y + right * heelLocal.z;
	}
}
