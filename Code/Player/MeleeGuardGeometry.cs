using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Where a guarding weapon physically is, and whether a swing's weapon runs into it (Mark 2026-11-18: "where the sword is
/// should block attacks that come from that direction ... we should be checking for physical collision"; Mark 2026-11-23:
/// "the block hitbox on an overhead isnt where the weapon is ... if a weapons arc were to collide with a weapon AS its
/// going to its block position then it should count as a block").
/// <para>Every guard is a bar that turns with the <b>body</b> (not the look; <see cref="PlayerCombat"/> group
/// <i>Combat — Block (guard bar)</i>, meters): a left / right guard is upright in front of that shoulder, the overhead
/// guard is level across above the head. A swing is stopped when its weapon line, on the way to the body, comes within
/// the catch radius of the bar - whatever the swing, whatever side the marker called.</para>
/// <para>The bar also exists while the weapon travels (<see cref="GetGuardBarAt"/>): rising from the waist carry to its
/// guard over <see cref="PlayerCombat.BlockActivationSeconds"/> (the catch radius grows with the rise, so a guard pressed
/// at the instant of contact catches nothing), and crossing from the old guard to the new one over
/// <see cref="PlayerCombat.BlockSwitchSeconds"/>.</para>
/// <para>Analytic on purpose: the bar is the designed place of the guard pose, never a bone or a collider read from
/// the animation - the host has to get the same answer for a pawn it does not render. Sampled once per contact
/// (<see cref="MeleeBlockResolution.TryResolve"/>) and once per swing for the marker, never per frame.</para>
/// </summary>
public static class MeleeGuardGeometry
{
	/// <summary>Blade samples along the swing for one check.</summary>
	const int PathSteps = 48;
	/// <summary>A weapon line this close (m, flat) to the body's axis, below the top of the head, has reached the body: a guard beyond that point is on the far side.</summary>
	const float BodyCoreMeters = 0.1f;

	// The waist carry the guard rises from (m, body frame: forward / right / up): hilt at the right hip, tip forward and up.
	static readonly Vector3 RestHiltMeters = new( 0.2f, 0.25f, 0.95f );
	static readonly Vector3 RestTipMeters = new( 0.75f, 0.1f, 1.3f );

	/// <summary>Result of one swing-against-guard check (<see cref="TryMeetGuard"/>).</summary>
	public readonly struct GuardMeet
	{
		/// <summary>The weapon came within the catch radius of the guard before it reached the body.</summary>
		public bool Caught { get; init; }
		/// <summary>Nearest distance between the weapon line and the guard bar (engine units) at <see cref="WeaponPoint"/>.</summary>
		public float Distance { get; init; }
		/// <summary>Point on the attacker's weapon line where the two are nearest (the first catch, else the closest pass).</summary>
		public Vector3 WeaponPoint { get; init; }
		/// <summary>Point on the guard bar nearest <see cref="WeaponPoint"/>.</summary>
		public Vector3 GuardPoint { get; init; }
	}

	/// <summary>The yaw the guard bar turns with: the body's (every machine tracks it), else the guard's own basis.</summary>
	public static float GetGuardYawDegrees( PlayerCombat defender ) =>
		defender.Components.Get<PlayerMovement>( FindMode.EverythingInSelf ) is { } movement
			? movement.BodyYawDegrees
			: defender.GetBlockCombatBasisYaw();

	/// <summary>
	/// World ends of a raised guard's bar: left / right upright in front of that shoulder (bottom → top), up level
	/// across above the head (left → right).
	/// </summary>
	public static void GetGuardBar( PlayerCombat defender, byte guardDir, out Vector3 a, out Vector3 b )
	{
		var upm = defender.GetPawnUnitsPerMeter();
		var basis = new Angles( 0f, GetGuardYawDegrees( defender ), 0f ).ToRotation();
		var origin = defender.GameObject.WorldPosition;

		if ( guardDir is not (SwingDirs.Left or SwingDirs.Right) )
		{
			var centre = origin + basis.Forward * (defender.GuardBarOverheadForwardMetersOrDefault * upm)
			                    + Vector3.Up * (defender.GuardBarOverheadHeightMetersOrDefault * upm);
			var half = basis.Right * (defender.GuardBarOverheadHalfWidthMetersOrDefault * upm);
			a = centre - half;
			b = centre + half;
			return;
		}

		var front = origin + basis.Forward * (defender.GuardBarForwardMetersOrDefault * upm);
		var side = basis.Right * (defender.GuardBarSideMetersOrDefault * upm * (guardDir == SwingDirs.Right ? 1f : -1f));
		a = front + side + Vector3.Up * (defender.GuardBarBottomMetersOrDefault * upm);
		b = front + side + Vector3.Up * (defender.GuardBarTopMetersOrDefault * upm);
	}

	/// <summary>World ends (hilt → tip) of the weapon in the waist carry a guard rises from.</summary>
	static void GetRestBar( PlayerCombat defender, out Vector3 a, out Vector3 b )
	{
		var upm = defender.GetPawnUnitsPerMeter();
		var basis = new Angles( 0f, GetGuardYawDegrees( defender ), 0f ).ToRotation();
		var origin = defender.GameObject.WorldPosition;
		a = origin + (basis.Forward * RestHiltMeters.x + basis.Right * RestHiltMeters.y + Vector3.Up * RestHiltMeters.z) * upm;
		b = origin + (basis.Forward * RestTipMeters.x + basis.Right * RestTipMeters.y + Vector3.Up * RestTipMeters.z) * upm;
	}

	/// <summary>
	/// Host: where the guarding weapon is at <paramref name="atSandbox"/> - on its way up from the waist, on its way across
	/// from the previous guard, or at rest in its guard. <paramref name="catchScale"/> is the share of the catch radius it
	/// has there (0 → 1 over the rise, 1 otherwise).
	/// </summary>
	public static void GetGuardBarAt( PlayerCombat defender, double atSandbox, out Vector3 a, out Vector3 b, out float catchScale )
	{
		var dir = defender.AuthoritativeMeleeBlockDirection;
		GetGuardBar( defender, dir, out a, out b );
		catchScale = 1f;

		var rise = defender.GetGuardRise01( atSandbox );
		if ( rise < 1f )
		{
			GetRestBar( defender, out var restA, out var restB );
			a = Vector3.Lerp( restA, a, rise );
			b = Vector3.Lerp( restB, b, rise );
			catchScale = rise;
			return;
		}

		var cross = defender.GetGuardSwitch01( atSandbox, out var fromDir );
		if ( cross < 1f && fromDir != dir )
		{
			GetGuardBar( defender, fromDir, out var fromA, out var fromB );
			// An upright bar turning level (or back): keep the hilt end with the hilt end.
			a = Vector3.Lerp( fromA, a, cross );
			b = Vector3.Lerp( fromB, b, cross );
		}
	}

	/// <summary>
	/// Host: the attacker's swing, as it is aimed right now, against the defender's guard as it is at
	/// <paramref name="atSandbox"/> (rising / crossing / raised).
	/// </summary>
	public static GuardMeet TryMeetGuard( PlayerCombat defender, PlayerCombat attacker, byte attackType, double atSandbox )
	{
		if ( defender is null || attacker is null || !defender.GameObject.IsValid() || !attacker.GameObject.IsValid() )
			return new GuardMeet { Distance = float.MaxValue };

		GetGuardBarAt( defender, atSandbox, out var a, out var b, out var catchScale );
		return MeetBar( defender, a, b, defender.GuardCatchRadiusMetersOrDefault * defender.GetPawnUnitsPerMeter() * catchScale,
			attacker, attackType );
	}

	/// <summary>
	/// The attacker's weapon, as the swing is aimed right now, runs into the defender's fully raised
	/// <paramref name="guardDir"/> guard before it reaches the body.
	/// </summary>
	public static bool GuardInterceptsSwing( PlayerCombat defender, byte guardDir, PlayerCombat attacker, byte attackType )
	{
		if ( defender is null || attacker is null || !defender.GameObject.IsValid() || !attacker.GameObject.IsValid() )
			return false;

		GetGuardBar( defender, guardDir, out var a, out var b );
		return MeetBar( defender, a, b, defender.GuardCatchRadiusMetersOrDefault * defender.GetPawnUnitsPerMeter(), attacker, attackType ).Caught;
	}

	/// <summary>
	/// Nearest the weapon line comes to the fully raised <paramref name="guardDir"/> guard before it reaches the body, in
	/// engine units (the first pass inside the catch radius, else the closest pass).
	/// </summary>
	public static float ClosestCrossingDistance( PlayerCombat defender, byte guardDir, PlayerCombat attacker, byte attackType )
	{
		GetGuardBar( defender, guardDir, out var a, out var b );
		return MeetBar( defender, a, b, defender.GuardCatchRadiusMetersOrDefault * defender.GetPawnUnitsPerMeter(), attacker, attackType ).Distance;
	}

	/// <summary>
	/// Follows the weapon line (swing pivot → tip, the line the damage ray runs along; the path's own heel is only the
	/// outer fifth of the reach and passes behind a defender standing close) along the swing until it reaches the body's
	/// core - a guard past that point is on the far side of the body. Returns the first pass within
	/// <paramref name="catchRadius"/> of the bar, else the closest pass.
	/// </summary>
	static GuardMeet MeetBar( PlayerCombat defender, Vector3 barA, Vector3 barB, float catchRadius, PlayerCombat attacker, byte attackType )
	{
		var basis = attacker.GetMeleeCombatBasisRotation( attackType );
		var pivot = MeleeAttackPath.GetSwingPivotWorld( attacker.GameObject, attacker, attackType, basis );
		var center = defender.GameObject.WorldPosition;
		var core = BodyCoreMeters * defender.GetPawnUnitsPerMeter();
		var headTop = center.z + defender.ServerEyeHeight + Math.Max( 0f, defender.BlockShellHeightPadding );

		var best = float.MaxValue;
		Vector3 bestWeapon = default, bestGuard = default;
		for ( var i = 0; i <= PathSteps; i++ )
		{
			MeleeAttackPath.EvaluateWorldBlade( attacker.GameObject, attacker, attackType, i / (float)PathSteps, basis, out var tip, out _ );
			if ( ReachedBodyCore( pivot, tip, center, core, headTop ) )
				break;

			var distance = SegmentDistance( pivot, tip, barA, barB, out var onWeapon, out var onGuard );
			if ( distance < best )
			{
				best = distance;
				bestWeapon = onWeapon;
				bestGuard = onGuard;
			}

			if ( distance <= catchRadius )
				return new GuardMeet { Caught = true, Distance = distance, WeaponPoint = onWeapon, GuardPoint = onGuard };
		}

		return new GuardMeet { Distance = best, WeaponPoint = bestWeapon, GuardPoint = bestGuard };
	}

	/// <summary>
	/// The weapon line a-b passes within <paramref name="core"/> (flat) of the vertical axis through
	/// <paramref name="center"/>, at or below the top of the head - an overhead still above the head has not arrived.
	/// </summary>
	static bool ReachedBodyCore( Vector3 a, Vector3 b, Vector3 center, float core, float headTopZ )
	{
		var along = (b - a).WithZ( 0f );
		var lenSq = along.LengthSquared;
		var t = lenSq > 1e-6f ? Math.Clamp( Vector3.Dot( (center - a).WithZ( 0f ), along ) / lenSq, 0f, 1f ) : 0f;
		var point = a + (b - a) * t;
		return (point - center).WithZ( 0f ).Length <= core && point.z <= headTopZ;
	}

	/// <summary>
	/// Where this swing's blade first reaches the defender's body column (the block shell radius), as the swing is aimed
	/// right now. False when it never gets there (out of reach, aimed elsewhere).
	/// </summary>
	public static bool TryFindBodyEntry( PlayerCombat defender, PlayerCombat attacker, byte attackType, out Vector3 entry )
	{
		entry = default;
		if ( defender is null || attacker is null || !defender.GameObject.IsValid() || !attacker.GameObject.IsValid() )
			return false;

		var center = defender.GameObject.WorldPosition;
		var radius = MeleeBlockPath.GetBodyShellRadius( defender );
		var basis = attacker.GetMeleeCombatBasisRotation( attackType );
		var heel = MeleeAttackPath.GetSwingPivotWorld( attacker.GameObject, attacker, attackType, basis );
		for ( var i = 0; i <= PathSteps; i++ )
		{
			MeleeAttackPath.EvaluateWorldBlade( attacker.GameObject, attacker, attackType, i / (float)PathSteps, basis, out var tip, out _ );

			// Closest point of the blade to the body's vertical axis, measured flat.
			var along = (tip - heel).WithZ( 0f );
			var lenSq = along.LengthSquared;
			var t = lenSq > 1e-6f ? Math.Clamp( Vector3.Dot( (center - heel).WithZ( 0f ), along ) / lenSq, 0f, 1f ) : 0f;
			var point = heel + (tip - heel) * t;
			if ( (point - center).WithZ( 0f ).LengthSquared > radius * radius )
				continue;

			entry = point;
			return true;
		}

		return false;
	}

	/// <summary>Shortest distance between segments p1-q1 and p2-q2, and the nearest point on each.</summary>
	static float SegmentDistance( Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 on1, out Vector3 on2 )
	{
		var d1 = q1 - p1;
		var d2 = q2 - p2;
		var r = p1 - p2;
		var a = d1.LengthSquared;
		var e = d2.LengthSquared;
		var f = Vector3.Dot( d2, r );
		float s, t;

		if ( a <= 1e-6f && e <= 1e-6f )
		{
			s = 0f;
			t = 0f;
		}
		else if ( a <= 1e-6f )
		{
			s = 0f;
			t = Math.Clamp( f / e, 0f, 1f );
		}
		else
		{
			var c = Vector3.Dot( d1, r );
			if ( e <= 1e-6f )
			{
				t = 0f;
				s = Math.Clamp( -c / a, 0f, 1f );
			}
			else
			{
				var b = Vector3.Dot( d1, d2 );
				var denom = a * e - b * b;
				s = denom > 1e-6f ? Math.Clamp( (b * f - c * e) / denom, 0f, 1f ) : 0f;
				t = (b * s + f) / e;
				if ( t < 0f )
				{
					t = 0f;
					s = Math.Clamp( -c / a, 0f, 1f );
				}
				else if ( t > 1f )
				{
					t = 1f;
					s = Math.Clamp( (b - c) / a, 0f, 1f );
				}
			}
		}

		on1 = p1 + d1 * s;
		on2 = p2 + d2 * t;
		return (on1 - on2).Length;
	}
}
