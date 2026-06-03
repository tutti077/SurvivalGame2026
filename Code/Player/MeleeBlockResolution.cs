using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Server-authoritative directional block: attack ray must enter the held block region (footprint / guard)
/// before the defender body. Left teardrop = left-side region; right teardrop = right-side region.
/// Lateral holds may block left or right attacks when geometry passes and the attacker is in that teardrop's front arc
/// (blocks behind the defender or on the wrong flank fail even if the wide arc is along the ray).
/// </summary>
public static class MeleeBlockResolution
{
	public static float ComputeIncomingHitAngleDegrees( Vector3 defenderPos, float defenderYawDegrees, Vector3 hitPos )
	{
		var forward = YawToForwardFlat( defenderYawDegrees );
		var right = Vector3.Cross( forward, Vector3.Up ).Normal;
		var toHit = new Vector3( hitPos.x - defenderPos.x, 0f, hitPos.z - defenderPos.z );
		if ( toHit.LengthSquared < 1e-6f )
			return 0f;

		toHit = toHit.Normal;
		return MathF.Atan2( Vector3.Dot( toHit, right ), Vector3.Dot( toHit, forward ) ) * (180f / MathF.PI);
	}

	public static float ComputeIncomingHitAngleDegrees( PlayerCombat defender, Vector3 hitPos, Vector3 attackerPos )
	{
		if ( defender is null || !defender.GameObject.IsValid() )
			return 0f;

		var yaw = defender.IsAuthoritativeMeleeBlocking
			? defender.GetBlockCombatBasisYaw()
			: defender.GetMeleeCombatBasisRotation().Angles().yaw;
		var angleFromHit = ComputeIncomingHitAngleDegrees( defender.GameObject.WorldPosition, yaw, hitPos );
		if ( MathF.Abs( angleFromHit ) > 1e-3f )
			return angleFromHit;

		return ComputeIncomingHitAngleDegrees( defender.GameObject.WorldPosition, yaw, attackerPos );
	}

	public static bool IsInLateralBackArc( PlayerCombat pc, float angleDegrees ) =>
		MathF.Abs( angleDegrees ) > pc.MeleeBlockLateralHalfArcDegrees + 1e-4f;

	public static bool IsInOverheadBackArc( PlayerCombat pc, float angleDegrees ) =>
		MathF.Abs( angleDegrees ) > pc.MeleeBlockOverheadHalfArcDegrees + 1e-4f;

	public static bool IsInLeftBlockZone( PlayerCombat pc, float angleDegrees ) =>
		angleDegrees >= -pc.MeleeBlockLateralHalfArcDegrees - 1e-4f && angleDegrees <= 0f + 1e-4f;

	public static bool IsInRightBlockZone( PlayerCombat pc, float angleDegrees ) =>
		angleDegrees >= 0f - 1e-4f && angleDegrees <= pc.MeleeBlockLateralHalfArcDegrees + 1e-4f;

	public static bool IsInOverheadBlockZone( PlayerCombat pc, float angleDegrees ) =>
		MathF.Abs( angleDegrees ) <= pc.MeleeBlockOverheadHalfArcDegrees + 1e-4f;

	public static bool TryResolve(
		PlayerCombat defender,
		in MeleeBlockContact contact,
		bool logRejections,
		out float damageMultiplier,
		out float staggerMultiplier,
		out MeleeBlockRejectReason rejectReason ) =>
		TryResolve( defender, in contact, logRejections, out damageMultiplier, out staggerMultiplier, out rejectReason, out _ );

	public static bool TryResolve(
		PlayerCombat defender,
		in MeleeBlockContact contact,
		bool logRejections,
		out float damageMultiplier,
		out float staggerMultiplier,
		out MeleeBlockRejectReason rejectReason,
		out MeleeBlockValidationTrace trace )
	{
		damageMultiplier = 1f;
		staggerMultiplier = 1f;
		rejectReason = MeleeBlockRejectReason.None;
		trace = default;

		if ( defender is null || !defender.GameObject.IsValid() )
		{
			rejectReason = MeleeBlockRejectReason.NotBlocking;
			return false;
		}

		if ( !defender.IsAuthoritativeMeleeBlocking )
		{
			rejectReason = MeleeBlockRejectReason.NotBlocking;
			trace = new MeleeBlockValidationTrace { WasBlocking = false, RejectReason = rejectReason };
			return false;
		}

		if ( contact.HitSandboxTime + 1e-4 < defender.ServerBlockStartedAtSandbox )
		{
			rejectReason = MeleeBlockRejectReason.BlockStartedAfterHit;
			trace = new MeleeBlockValidationTrace
			{
				WasBlocking = true,
				BlockDirection = defender.AuthoritativeMeleeBlockDirection,
				RejectReason = rejectReason
			};
			if ( logRejections && defender.LogMeleeBlockRejectionsToConsole )
				Log.Info( $"[MeleeBlock] reject {defender.GameObject.Name}: block started after hit" );
			return false;
		}

		var blockDir = defender.AuthoritativeMeleeBlockDirection;
		var angle = ComputeIncomingAngleFromAttacker( defender, contact.AttackerPosition );
		trace = new MeleeBlockValidationTrace
		{
			IncomingAngleDegrees = angle,
			BlockDirection = blockDir,
			WasBlocking = true
		};

		if ( blockDir is not (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up) )
		{
			rejectReason = MeleeBlockRejectReason.InvalidBlockDirection;
			trace = trace with { RejectReason = rejectReason };
			return false;
		}

		var overheadAttack = IsOverheadAttack( in contact );
		if ( overheadAttack )
		{
			if ( blockDir != SwingDirs.Up )
			{
				rejectReason = MeleeBlockRejectReason.WrongBlockForAttackType;
				trace = trace with { RejectReason = rejectReason };
				LogReject( defender, contact, logRejections, rejectReason, angle, blockDir );
				return false;
			}
		}
		else if ( blockDir == SwingDirs.Up )
		{
			rejectReason = MeleeBlockRejectReason.WrongBlockForAttackType;
			trace = trace with { RejectReason = rejectReason };
			LogReject( defender, contact, logRejections, rejectReason, angle, blockDir );
			return false;
		}

		if ( !contact.AttackRayGeometryValidated )
		{
			rejectReason = MeleeBlockRejectReason.RayMissedBlockRegion;
			trace = trace with { RejectReason = rejectReason };
			LogReject( defender, contact, logRejections, rejectReason, angle, blockDir );
			return false;
		}

		if ( !HeldBlockFacesIncomingAttack( defender, blockDir, overheadAttack, angle, out rejectReason ) )
		{
			trace = trace with { RejectReason = rejectReason };
			LogReject( defender, contact, logRejections, rejectReason, angle, blockDir );
			return false;
		}

		rejectReason = MeleeBlockRejectReason.None;
		trace = trace with { RejectReason = rejectReason };
		damageMultiplier = Math.Clamp( defender.MeleeBlockedDamageMultiplier, 0f, 1f );
		staggerMultiplier = Math.Clamp( defender.MeleeBlockedStaggerMultiplier, 0f, 1f );
		return true;
	}

	public static bool IsOverheadAttack( in MeleeBlockContact contact )
	{
		if ( contact.AttackSwingDir == SwingDirs.Up )
			return true;

		return contact.AttackType == MeleeAttackTypes.Forward;
	}

	/// <summary>Incoming angle from attacker root vs block look yaw (0° ahead, − = left, + = right).</summary>
	public static float ComputeIncomingAngleFromAttacker( PlayerCombat defender, Vector3 attackerPosition )
	{
		if ( defender is null || !defender.GameObject.IsValid() )
			return 0f;

		var yaw = defender.IsAuthoritativeMeleeBlocking
			? defender.GetBlockCombatBasisYaw()
			: defender.GetMeleeCombatBasisRotation().Angles().yaw;
		return ComputeIncomingHitAngleDegrees( defender.GameObject.WorldPosition, yaw, attackerPosition );
	}

	/// <summary>
	/// Wide block arcs can sit along the ray before the torso center; require the attacker to be in the held teardrop's front wedge.
	/// </summary>
	public static bool HeldBlockFacesIncomingAttack(
		PlayerCombat pc,
		byte blockDir,
		bool overheadAttack,
		float incomingAngleDegrees,
		out MeleeBlockRejectReason reason )
	{
		reason = MeleeBlockRejectReason.None;

		if ( overheadAttack )
		{
			if ( IsInOverheadBackArc( pc, incomingAngleDegrees ) )
			{
				reason = MeleeBlockRejectReason.IncomingFromBackArc;
				return false;
			}

			if ( !IsInOverheadBlockZone( pc, incomingAngleDegrees ) )
			{
				reason = MeleeBlockRejectReason.WrongBlockForAngle;
				return false;
			}

			return true;
		}

		if ( IsInLateralBackArc( pc, incomingAngleDegrees ) )
		{
			reason = MeleeBlockRejectReason.IncomingFromBackArc;
			return false;
		}

		var inHeldZone = blockDir switch
		{
			SwingDirs.Left => IsInLeftBlockZone( pc, incomingAngleDegrees ),
			SwingDirs.Right => IsInRightBlockZone( pc, incomingAngleDegrees ),
			_ => false
		};

		if ( !inHeldZone )
		{
			reason = MeleeBlockRejectReason.WrongBlockForAngle;
			return false;
		}

		return true;
	}

	static void LogReject(
		PlayerCombat defender,
		in MeleeBlockContact contact,
		bool logRejections,
		MeleeBlockRejectReason rejectReason,
		float angle,
		byte blockDir )
	{
		if ( !logRejections || rejectReason == MeleeBlockRejectReason.None || !defender.LogMeleeBlockRejectionsToConsole )
			return;

		Log.Info(
			$"[MeleeBlock] reject {defender.GameObject.Name}: {rejectReason} angle={angle:0.#}° block={SwingDirs.Letter( blockDir )} swing={SwingDirs.Letter( contact.AttackSwingDir )} attack={MeleeAttackTypes.Label( contact.AttackType )} from {contact.AttackerRoot?.Name}" );
	}

	public static PlayerCombat FindDefenderCombat( DamageReceiver receiver )
	{
		if ( receiver is null || !receiver.GameObject.IsValid() )
			return null;

		for ( var p = receiver.GameObject; p.IsValid(); p = p.Parent )
		{
			var pc = p.Components.Get<PlayerCombat>();
			if ( pc is not null )
				return pc;
		}

		return null;
	}

	static Vector3 YawToForwardFlat( float yawDegrees )
	{
		var rot = new Angles( 0f, yawDegrees, 0f ).ToRotation();
		var f = new Vector3( rot.Forward.x, 0f, rot.Forward.z );
		return f.LengthSquared < 1e-8f ? Vector3.Forward : f.Normal;
	}
}
