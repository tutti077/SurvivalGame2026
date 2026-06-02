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
		GetPhaseDurations( pc, out var early, out var active, out var late );
		_ = attackType;
		return Math.Max( 0.04f, early + active + late );
	}

	public static void GetPhaseDurations( PlayerCombat pc, out float early, out float active, out float late )
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

	/// <summary>Time-based EarlyActive / Active / LateActive / Recovery from elapsed seconds after the active swing begins.</summary>
	public static byte ClassifyActiveState( PlayerCombat pc, byte attackType, float activeElapsedSeconds )
	{
		activeElapsedSeconds = Math.Max( 0f, activeElapsedSeconds );
		GetPhaseDurations( pc, out var earlyDur, out var activeDur, out var lateDur );
		var earlyEnd = earlyDur;
		var activePhaseEnd = earlyDur + activeDur;
		var latePhaseEnd = earlyDur + activeDur + lateDur;
		if ( activeElapsedSeconds <= earlyEnd + 1e-5f )
			return MeleeAttackStates.EarlyActive;
		if ( activeElapsedSeconds <= activePhaseEnd + 1e-5f )
			return MeleeAttackStates.Active;
		if ( activeElapsedSeconds <= latePhaseEnd + 1e-5f )
			return MeleeAttackStates.LateActive;
		return MeleeAttackStates.Recovery;
	}

	/// <summary>Time-based state from normalized active progress (0–1 over full active window).</summary>
	public static byte ClassifyActiveStateFromProgress( PlayerCombat pc, byte attackType, float activeProgress01 )
	{
		activeProgress01 = Math.Clamp( activeProgress01, 0f, 1f );
		var total = GetActiveDurationSeconds( pc, attackType );
		return ClassifyActiveState( pc, attackType, activeProgress01 * total );
	}

	/// <summary>Elapsed seconds (from active start) where EarlyActive→Active, Active→LateActive, and LateActive→Recovery begin.</summary>
	public static void GetPhaseBoundaryElapsedSeconds( PlayerCombat pc, byte attackType, out float activePhaseStart, out float latePhaseStart )
	{
		GetPhaseDurations( pc, out var earlyDur, out var activeDur, out _ );
		activePhaseStart = earlyDur;
		latePhaseStart = earlyDur + activeDur;
	}

	/// <summary>End of the timed active window (early + active + late) in seconds from active start.</summary>
	public static float GetLatePhaseEndElapsedSeconds( PlayerCombat pc, byte attackType )
	{
		GetPhaseDurations( pc, out var earlyDur, out var activeDur, out var lateDur );
		return earlyDur + activeDur + lateDur;
	}

	public static float ActiveProgressFromElapsed( PlayerCombat pc, byte attackType, float activeElapsedSeconds )
	{
		var total = GetActiveDurationSeconds( pc, attackType );
		if ( total <= 1e-6f )
			return 0f;
		return Math.Clamp( activeElapsedSeconds / total, 0f, 1f );
	}

	/// <summary>Maps an arc angle (degrees along the attack fan) to normalized stroke progress 0–1.</summary>
	public static float ArcDegreeToProgress01( float startDeg, float endDeg, float arcDegree )
	{
		var span = endDeg - startDeg;
		if ( MathF.Abs( span ) < 1e-4f )
			return 0f;
		return Math.Clamp( (arcDegree - startDeg) / span, 0f, 1f );
	}

	/// <summary>Inverse of <see cref="ArcDegreeToProgress01"/> — arc angle at stroke progress 0–1.</summary>
	public static float ArcProgress01ToDegree( float startDeg, float endDeg, float progress01 ) =>
		startDeg + (endDeg - startDeg) * Math.Clamp( progress01, 0f, 1f );

	public static bool IsArcDegreeRevealed( PlayerCombat pc, byte attackType, float arcDegree, float activeProgress01 )
	{
		GetArcDegreeSpan( pc, attackType, out var startDeg, out var endDeg );
		var t = ArcDegreeToProgress01( startDeg, endDeg, arcDegree );
		return t <= Math.Clamp( activeProgress01, 0f, 1f ) + 1e-4f;
	}

	/// <summary>Invokes <paramref name="perDegree"/> only for arc steps revealed up to <paramref name="activeProgress01"/>.</summary>
	public static void ForEachRevealedArcDegreeStep(
		PlayerCombat pc,
		byte attackType,
		float degreeStep,
		float activeProgress01,
		Action<float> perDegree )
	{
		if ( perDegree is null )
			return;

		ForEachArcDegreeStep( pc, attackType, degreeStep, arcDegree =>
		{
			if ( IsArcDegreeRevealed( pc, attackType, arcDegree, activeProgress01 ) )
				perDegree( arcDegree );
		} );
	}

	/// <summary>Number of path samples along the attack arc (e.g. 150° ÷ 1° → 150).</summary>
	public static int GetArcPathSampleCount( PlayerCombat pc, byte attackType, float degreeStep )
	{
		GetArcDegreeSpan( pc, attackType, out var startDeg, out var endDeg );
		degreeStep = Math.Max( 1f, degreeStep );
		return Math.Max( 1, (int)MathF.Round( MathF.Abs( endDeg - startDeg ) / degreeStep ) );
	}

	/// <summary>Stroke progress 0–1 for arc sample index 0…count−1.</summary>
	public static float ArcSampleIndexToProgress01( int sampleCount, int sampleIndex )
	{
		sampleCount = Math.Max( 1, sampleCount );
		sampleIndex = Math.Clamp( sampleIndex, 0, sampleCount - 1 );
		return sampleCount <= 1 ? 0f : sampleIndex / (float)(sampleCount - 1);
	}

	/// <summary>Arc angle for debug sample index along the attack path.</summary>
	public static float ArcSampleIndexToDegree( PlayerCombat pc, byte attackType, float degreeStep, int sampleIndex )
	{
		GetArcDegreeSpan( pc, attackType, out var startDeg, out var endDeg );
		var count = GetArcPathSampleCount( pc, attackType, degreeStep );
		return ArcProgress01ToDegree( startDeg, endDeg, ArcSampleIndexToProgress01( count, sampleIndex ) );
	}

	public static int RevealedArcSampleExclusiveEnd( float activeProgress01, int sampleCount )
	{
		if ( activeProgress01 < 1e-5f )
			return 0;

		return Math.Min( sampleCount, Math.Max( 1, (int)MathF.Ceiling( activeProgress01 * sampleCount - 1e-4f ) ) );
	}

	/// <summary>Invokes <paramref name="perSampleIndex"/> for each new arc sample revealed since the last draw (contiguous, no gaps).</summary>
	public static void ForEachNewlyRevealedArcSampleIndex(
		PlayerCombat pc,
		byte attackType,
		float degreeStep,
		float lastProgress01,
		float currentProgress01,
		Action<int> perSampleIndex )
	{
		if ( perSampleIndex is null )
			return;

		currentProgress01 = Math.Clamp( currentProgress01, 0f, 1f );
		if ( currentProgress01 < 1e-5f )
			return;

		var count = GetArcPathSampleCount( pc, attackType, degreeStep );
		var currentEnd = RevealedArcSampleExclusiveEnd( currentProgress01, count );
		var lastEnd = lastProgress01 < 0f ? 0 : RevealedArcSampleExclusiveEnd( lastProgress01, count );

		for ( var i = lastEnd; i < currentEnd; i++ )
			perSampleIndex( i );
	}

	/// <summary>Yaw bucket 0…(360/<paramref name="degreeStep"/>−1) for full-turn debug (e.g. 72 at 5°).</summary>
	public static int YawDegreesToDebugBucket( float yawDegrees, float degreeStep )
	{
		degreeStep = Math.Max( 1f, degreeStep );
		var bucketCount = Math.Max( 1, (int)MathF.Round( 360f / degreeStep ) );
		var yaw = Angles.NormalizeAngle( yawDegrees );
		var bucket = (int)MathF.Floor( (yaw + 180f) / degreeStep );
		return Math.Clamp( bucket, 0, bucketCount - 1 );
	}

	/// <summary>Max rotation debug spokes for a full 360° turn (e.g. 72 at 5°).</summary>
	public static int GetRotationDebugSpokeCount( float degreeStep ) =>
		Math.Max( 1, (int)(360f / Math.Max( 1f, degreeStep )) );

	/// <summary>Arc pivot in world space (debug spokes + sweep basis). Forward attacks use head-adjacent local offsets.</summary>
	public static Vector3 GetSwingPivotWorld( GameObject attacker, PlayerCombat pc, byte attackType ) =>
		GetSwingPivotWorld( attacker, pc, attackType, pc.GetMeleeCombatBasisRotation( attackType ) );

	public static Vector3 GetSwingPivotWorld( GameObject attacker, PlayerCombat pc, byte attackType, Rotation combatBasis )
	{
		if ( !attacker.IsValid() || pc is null )
			return pc?.GameObject.IsValid() == true ? pc.GameObject.WorldPosition : Vector3.Zero;
		var range = GetAttackRange( pc, attackType );
		Vector3 pivotLocal;

		if ( attackType == MeleeAttackTypes.Forward )
			pivotLocal = GetForwardSwingPivotLocal( pc, 0f );
		else
		{
			pivotLocal = new Vector3(
				range * 0.14f,
				pc.ServerEyeHeight + pc.MeleeAttackZaxisStart,
				0f );
		}

		TransformLocalToWorld( attacker, combatBasis, pivotLocal, pivotLocal, out var pivotWorld, out _ );
		return pivotWorld;
	}

	/// <summary>World blade sample at a point on the arc fan (by degrees along start→end).</summary>
	public static void EvaluateWorldBladeAtArcDegree(
		GameObject attacker,
		PlayerCombat pc,
		byte attackType,
		float arcDegree,
		out Vector3 tipWorld,
		out Vector3 heelWorld ) =>
		EvaluateWorldBladeAtArcDegree( attacker, pc, attackType, arcDegree, pc.GetMeleeCombatBasisRotation( attackType ),
			out tipWorld, out heelWorld );

	public static void EvaluateWorldBladeAtArcDegree(
		GameObject attacker,
		PlayerCombat pc,
		byte attackType,
		float arcDegree,
		Rotation combatBasis,
		out Vector3 tipWorld,
		out Vector3 heelWorld )
	{
		GetArcDegreeSpan( pc, attackType, out var startDeg, out var endDeg );
		var t = ArcDegreeToProgress01( startDeg, endDeg, arcDegree );
		EvaluateWorldBlade( attacker, pc, attackType, t, combatBasis, out tipWorld, out heelWorld );
	}

	/// <summary>Which timed phase this arc degree belongs to if the stroke reached this point on the path (0°→end° maps to early→late).</summary>
	public static byte ClassifyActiveStateForArcDegree( PlayerCombat pc, byte attackType, float arcDegree )
	{
		GetArcDegreeSpan( pc, attackType, out var startDeg, out var endDeg );
		var progress01 = ArcDegreeToProgress01( startDeg, endDeg, arcDegree );
		return ClassifyActiveStateFromProgress( pc, attackType, progress01 );
	}

	/// <summary>Invokes <paramref name="perDegree"/> for each debug step along the arc (inclusive endpoints).</summary>
	public static void ForEachArcDegreeStep(
		PlayerCombat pc,
		byte attackType,
		float degreeStep,
		Action<float> perDegree )
	{
		if ( perDegree is null )
			return;

		degreeStep = Math.Max( 1f, degreeStep );
		var count = GetArcPathSampleCount( pc, attackType, degreeStep );
		for ( var i = 0; i < count; i++ )
			perDegree( ArcSampleIndexToDegree( pc, attackType, degreeStep, i ) );
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

		var pivot = GetForwardSwingPivotLocal( pc, t );
		var forwardScale = GetForwardArcForwardScale( pc, t );
		var verticalScale = Math.Clamp( pc.MeleeAttackForwardArcVerticalScale, 0.2f, 2f );

		var arcForward = pivot.x + MathF.Cos( angleRad ) * effectiveRange * forwardScale;
		var arcUp = pivot.y + MathF.Sin( angleRad ) * effectiveRange * verticalScale;

		tipLocal = new Vector3( arcForward, arcUp, pc.MeleeAttackForwardPlaneRightOffset );

		ApplyForwardPlaneTilt( ref tipLocal, pc.ServerEyeHeight, pc.MeleeAttackTiltDegreesForward * Deg2Rad );
		ApplyForwardPitchLean( ref tipLocal, pc, pivot );
		ClampForwardLocalFromPivot( ref tipLocal, pivot, range * reachMul, pc );
	}

	/// <summary>Head-adjacent arc origin in combat-local space (+X forward, +Y up, +Z right).</summary>
	static Vector3 GetForwardSwingPivotLocal( PlayerCombat pc, float strokeProgress01 )
	{
		strokeProgress01 = Math.Clamp( strokeProgress01, 0f, 1f );
		var forward = Lerp(
			pc.MeleeAttackForwardPivotForwardLocal,
			pc.MeleeAttackForwardPivotForwardLocalEnd,
			strokeProgress01 );
		var up = pc.ServerEyeHeight + pc.MeleeAttackForwardPivotUpFromEye;
		return new Vector3( forward, up, pc.MeleeAttackForwardPivotRightOffset );
	}

	/// <summary>Forward (cos) scale along the stroke — low at windup, full by mid-swing.</summary>
	static float GetForwardArcForwardScale( PlayerCombat pc, float strokeProgress01 )
	{
		strokeProgress01 = Math.Clamp( strokeProgress01, 0f, 1f );
		var start = Math.Clamp( pc.MeleeAttackForwardArcForwardScaleStart, 0.15f, 1.5f );
		var end = Math.Clamp( pc.MeleeAttackForwardArcForwardScale, 0.15f, 1.5f );
		return Lerp( start, end, SmoothStep( Math.Min( strokeProgress01 * 2f, 1f ) ) );
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

	static void ApplyForwardPitchLean( ref Vector3 local, PlayerCombat pc, Vector3 swingPivotLocal )
	{
		var leanDeg = pc.GetForwardMeleePitchLeanDegrees();
		if ( MathF.Abs( leanDeg ) < 0.01f )
			return;

		var pivot = new Vector2( swingPivotLocal.x, swingPivotLocal.y );
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

	static void ClampForwardLocalFromPivot( ref Vector3 local, Vector3 pivotLocal, float maxReach, PlayerCombat pc )
	{
		maxReach *= Math.Clamp( pc.MeleeAttackForwardMaxReachFraction, 0.55f, 1.05f );
		var offset = local - pivotLocal;
		var dist = offset.Length;
		if ( dist > maxReach && dist > 1e-4f )
			local = pivotLocal + offset * (maxReach / dist);
	}

	static float Lerp( float a, float b, float t ) => a + (b - a) * Math.Clamp( t, 0f, 1f );

	static float SmoothStep( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t);
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
