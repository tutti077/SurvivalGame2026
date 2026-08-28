using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Shared player-local combat-space attack path for host hits and debug overlay on <see cref="PlayerCombat"/>.
/// Local axes: +X forward, +Y up, +Z right — every attack basis carries yaw + camera pitch 1:1 (clamped to the up/down caps),
/// rotating about the shoulder-height pitch pivot (see <see cref="GetPitchPivotHeightLocal"/>), not the pawn origin.
/// </summary>
public static class MeleeAttackPath
{
	const float Deg2Rad = MathF.PI / 180f;

	public static float GetActiveDurationSeconds( PlayerCombat pc, byte attackType, bool isHeavy = false )
	{
		if ( attackType == MeleeAttackTypes.Stab )
			return Math.Max( 0.04f, pc.GetMeleeSpecialActiveSeconds() );

		return Math.Max( 0.04f, pc.GetMeleeActiveSeconds( isHeavy ) );
	}

	public static float GetAttackRange( PlayerCombat pc, byte attackType ) =>
		Math.Max( 8f, pc.GetMeleeAttackRangeUnits( attackType ) );

	public static void GetArcDegreeSpan( PlayerCombat pc, byte attackType, out float startDeg, out float endDeg )
	{
		// Stab has no arc — a synthetic 0→1 span keeps the shared progress mappers linear.
		if ( attackType == MeleeAttackTypes.Stab )
		{
			startDeg = 0f;
			endDeg = 1f;
			return;
		}

		if ( attackType == MeleeAttackTypes.Left )
		{
			var half = Math.Max( 1f, pc.GetMeleeLateralArcDegrees() ) * 0.5f;
			startDeg = -half;
			endDeg = half;
			return;
		}

		if ( attackType == MeleeAttackTypes.Right )
		{
			var half = Math.Max( 1f, pc.GetMeleeLateralArcDegrees() ) * 0.5f;
			startDeg = half;
			endDeg = -half;
			return;
		}

		GetForwardArcDegreeSpan( pc, out startDeg, out endDeg );
	}

	static void GetForwardArcDegreeSpan( PlayerCombat pc, out float startDeg, out float endDeg )
	{
		var total = Math.Clamp( pc.GetMeleeForwardArcTotalDegrees(), 90f, 180f );
		startDeg = pc.GetMeleeForwardArcStartDegrees();
		endDeg = startDeg - total;
	}

	/// <summary>Windup / Active / Recovery from elapsed seconds after the active sweep begins.</summary>
	public static byte ClassifyActiveState( PlayerCombat pc, byte attackType, float activeElapsedSeconds, bool isHeavy = false )
	{
		activeElapsedSeconds = Math.Max( 0f, activeElapsedSeconds );
		if ( activeElapsedSeconds <= GetActiveDurationSeconds( pc, attackType, isHeavy ) + 1e-5f )
			return MeleeAttackStates.Active;
		return MeleeAttackStates.Recovery;
	}

	public static float ActiveProgressFromElapsed( PlayerCombat pc, byte attackType, float activeElapsedSeconds, bool isHeavy = false )
	{
		var total = GetActiveDurationSeconds( pc, attackType, isHeavy );
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

	public static float ArcDegreeToProgress01( PlayerCombat pc, byte attackType, float arcDegree )
	{
		GetArcDegreeSpan( pc, attackType, out var startDeg, out var endDeg );
		return ArcDegreeToProgress01( startDeg, endDeg, arcDegree );
	}

	/// <summary>Number of path samples along the attack arc (e.g. 150° ÷ 1° → 151 inclusive steps).</summary>
	public static int GetArcPathSampleCount( PlayerCombat pc, byte attackType, float degreeStep )
	{
		GetArcDegreeSpan( pc, attackType, out var startDeg, out var endDeg );
		degreeStep = Math.Max( 1f, degreeStep );
		var span = MathF.Abs( endDeg - startDeg );
		return Math.Max( 1, (int)MathF.Floor( span / degreeStep ) + 1 );
	}

	/// <summary>Stroke progress 0–1 for arc sample index 0…count−1.</summary>
	public static float ArcSampleIndexToProgress01( int sampleCount, int sampleIndex )
	{
		sampleCount = Math.Max( 1, sampleCount );
		sampleIndex = Math.Clamp( sampleIndex, 0, sampleCount - 1 );
		return sampleCount <= 1 ? 0f : sampleIndex / (float)(sampleCount - 1);
	}

	/// <summary>Sample index for an arc angle along the attack fan (matches <see cref="ForEachArcDegreeStep"/> spacing).</summary>
	public static int ArcDegreeToSampleIndex( PlayerCombat pc, byte attackType, float degreeStep, float arcDegree )
	{
		GetArcDegreeSpan( pc, attackType, out var startDeg, out var endDeg );
		var count = GetArcPathSampleCount( pc, attackType, degreeStep );
		var t = ArcDegreeToProgress01( startDeg, endDeg, arcDegree );
		return Math.Clamp( (int)MathF.Round( t * (count - 1) ), 0, count - 1 );
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

	/// <summary>Signed yaw bucket for debug keying; keeps continuous turning steps at <paramref name="degreeStep"/> spacing.</summary>
	public static int QuantizeYawDegrees( float yawDegrees, float degreeStep )
	{
		degreeStep = Math.Max( 1f, degreeStep );
		return (int)MathF.Round( Angles.NormalizeAngle( yawDegrees ) / degreeStep );
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

		TransformLocalToWorld( attacker, pc, attackType, combatBasis, pivotLocal, pivotLocal, out var pivotWorld, out _ );
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
		TransformLocalToWorld( attacker, pc, attackType, combatBasis, tipLocal, heelLocal, out tipWorld, out heelWorld );
	}

	public static void EvaluateLocalBlade(
		PlayerCombat pc,
		byte attackType,
		float arcProgress01,
		out Vector3 tipLocal,
		out Vector3 heelLocal )
	{
		arcProgress01 = Math.Clamp( arcProgress01, 0f, 1f );

		if ( attackType == MeleeAttackTypes.Stab )
			EvaluateStabLocal( pc, arcProgress01, out tipLocal, out heelLocal );
		else if ( attackType == MeleeAttackTypes.Forward )
			EvaluateForwardLocal( pc, arcProgress01, out tipLocal, out heelLocal );
		else
			EvaluateLateralLocal( pc, attackType, arcProgress01, out tipLocal, out heelLocal );
	}

	/// <summary>Fraction of full stab reach where the blade tip starts the thrust (blade already in front of the body).</summary>
	const float StabStartReachFraction = 0.35f;

	/// <summary>
	/// Special stab: straight thrust on local +X at side-slash height; the tip travels from
	/// <see cref="StabStartReachFraction"/> of reach to full reach over the active window. Basis pitch
	/// aims the line 1:1 (rotating about the shoulder pivot), so the thrust goes where the crosshair looks.
	/// </summary>
	static void EvaluateStabLocal(
		PlayerCombat pc,
		float t,
		out Vector3 tipLocal,
		out Vector3 heelLocal )
	{
		ComputeStabTip( pc, t, out tipLocal );

		var range = GetAttackRange( pc, MeleeAttackTypes.Stab );
		var heelBack = Math.Clamp( pc.MeleeBladeHeelFraction, 0.04f, 0.6f ) * range;
		heelLocal = tipLocal - new Vector3( heelBack, 0f, 0f );
	}

	static void ComputeStabTip( PlayerCombat pc, float t, out Vector3 tipLocal )
	{
		t = Math.Clamp( t, 0f, 1f );
		var range = GetAttackRange( pc, MeleeAttackTypes.Stab );
		var forward = Lerp( range * StabStartReachFraction, range, t );
		tipLocal = new Vector3( forward, pc.ServerEyeHeight + pc.MeleeAttackZaxisStart, 0f );
	}

	/// <summary>
	/// Side slash: arc in local XZ (forward/right); basis pitch tips the plane. Height from <see cref="PlayerCombat.MeleeAttackZaxisStart"/> + tilt.
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
		out Vector3 tipLocal ) =>
		ComputeForwardTip( pc, startDeg, endDeg, t, applyLean: true, out tipLocal );

	static void ComputeForwardTip(
		PlayerCombat pc,
		float startDeg,
		float endDeg,
		float t,
		bool applyLean,
		out Vector3 tipLocal )
	{
		t = Math.Clamp( t, 0f, 1f );
		var range = GetAttackRange( pc, MeleeAttackTypes.Forward );
		var angleRad = Lerp( startDeg, endDeg, t ) * Deg2Rad;

		var pivot = GetForwardSwingPivotLocal( pc, t );
		var reachScale = Math.Clamp( pc.MeleeAttackForwardPathReachScale, 0.75f, 1.15f );
		var pivotForward = range * 0.14f;
		var verticalScale = Math.Clamp( pc.MeleeAttackForwardArcVerticalScale, 0.2f, 2f );

		// +X reach ≈ lateral slashes, scaled by <see cref="PlayerCombat.MeleeAttackForwardPathReachScale"/>.
		var arcForward = pivotForward + MathF.Cos( angleRad ) * range * reachScale;
		var arcUp = pivot.y + MathF.Sin( angleRad ) * range * verticalScale;

		tipLocal = new Vector3( arcForward, arcUp, pc.MeleeAttackForwardPlaneRightOffset );

		ApplyForwardPlaneTilt( ref tipLocal, pc.ServerEyeHeight, pc.MeleeAttackTiltDegreesForward * Deg2Rad );
		if ( applyLean )
		ClampForwardLocalFromPivot( ref tipLocal, pivot, range * 1.14f * reachScale, pc );
	}

	/// <summary>Head-adjacent arc origin in combat-local space (+X forward, +Y up, +Z right).</summary>
	static Vector3 GetForwardSwingPivotLocal( PlayerCombat pc, float strokeProgress01 )
	{
		strokeProgress01 = Math.Clamp( strokeProgress01, 0f, 1f );
		var range = GetAttackRange( pc, MeleeAttackTypes.Forward );
		var forward = Lerp( range * 0.14f, range * 0.18f, strokeProgress01 );
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
		maxReach *= Math.Clamp( pc.MeleeAttackForwardMaxReachFraction, 0.85f, 1.25f );
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

	/// <summary>
	/// Distance from the pitch pivot to the mid-swing (arc 0°) blade tip, including the forward offset.
	/// This is the sphere radius the cursor-alignment solve intersects with the camera ray; lean is skipped so the solve cannot recurse into itself.
	/// </summary>
	internal static float GetMidSwingTipRadius( PlayerCombat pc, byte attackType )
	{
		Vector3 tipLocal;
		if ( attackType == MeleeAttackTypes.Stab )
		{
			// Aim the cursor solve at full extension — where the stab actually lands.
			ComputeStabTip( pc, 1f, out tipLocal );
		}
		else if ( attackType == MeleeAttackTypes.Forward )
		{
			GetForwardArcDegreeSpan( pc, out var startDeg, out var endDeg );
			var t = ArcDegreeToProgress01( startDeg, endDeg, 0f );
			ComputeForwardTip( pc, startDeg, endDeg, t, applyLean: false, out tipLocal );
		}
		else
		{
			GetArcDegreeSpan( pc, attackType, out var startDeg, out var endDeg );
			var t = ArcDegreeToProgress01( startDeg, endDeg, 0f );
			ComputeLateralTip( pc, attackType, startDeg, endDeg, t, out tipLocal );
		}

		var pivotY = GetPitchPivotHeightLocal( pc, attackType );
		var dx = tipLocal.x + pc.GetMeleeSwingForwardOffsetUnits();
		var dy = tipLocal.y - pivotY;
		return MathF.Sqrt( dx * dx + dy * dy );
	}

	/// <summary>Height on the pawn the pitched basis rotates about — shoulder-adjacent so tipping the swing never pushes the arc off the body.</summary>
	internal static float GetPitchPivotHeightLocal( PlayerCombat pc, byte attackType ) =>
		attackType == MeleeAttackTypes.Forward
			? pc.ServerEyeHeight + pc.MeleeAttackForwardPivotUpFromEye
			: pc.ServerEyeHeight + pc.MeleeAttackZaxisStart;

	static void TransformLocalToWorld(
		GameObject attacker,
		PlayerCombat pc,
		byte attackType,
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

		var pivotY = GetPitchPivotHeightLocal( pc, attackType );
		var forwardPush = pc.GetMeleeSwingForwardOffsetUnits();
		var origin = attacker.WorldPosition + Vector3.Up * pivotY;
		var forward = combatBasis.Forward;
		var up = combatBasis.Up;
		var right = combatBasis.Right;
		tipWorld = origin + forward * (tipLocal.x + forwardPush) + up * (tipLocal.y - pivotY) + right * tipLocal.z;
		heelWorld = origin + forward * (heelLocal.x + forwardPush) + up * (heelLocal.y - pivotY) + right * heelLocal.z;
	}
}
