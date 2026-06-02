using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Server-authoritative directional block: incoming hit angle vs defender facing and held block direction.
/// 0° = straight ahead; negative = attacker's hit from defender's left; positive = from right.
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
		var angle = ComputeIncomingHitAngleDegrees( defender, contact.HitPosition, contact.AttackerPosition );
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

		var blocked = blockDir switch
		{
			SwingDirs.Up => TryResolveOverheadBlock( defender, angle, out rejectReason ),
			SwingDirs.Left => TryResolveLateralBlock( defender, angle, isLeftBlock: true, out rejectReason ),
			_ => TryResolveLateralBlock( defender, angle, isLeftBlock: false, out rejectReason )
		};

		trace = trace with { RejectReason = rejectReason };

		if ( !blocked )
		{
			if ( logRejections && rejectReason != MeleeBlockRejectReason.None && defender.LogMeleeBlockRejectionsToConsole )
			{
				Log.Info(
					$"[MeleeBlock] reject {defender.GameObject.Name}: {rejectReason} angle={angle:0.#}° block={SwingDirs.Letter( blockDir )} from {contact.AttackerRoot?.Name}" );
			}

			return false;
		}

		damageMultiplier = Math.Clamp( defender.MeleeBlockedDamageMultiplier, 0f, 1f );
		staggerMultiplier = Math.Clamp( defender.MeleeBlockedStaggerMultiplier, 0f, 1f );
		return true;
	}

	static bool TryResolveOverheadBlock( PlayerCombat pc, float angleDegrees, out MeleeBlockRejectReason reason )
	{
		if ( IsInOverheadBackArc( pc, angleDegrees ) )
		{
			reason = MeleeBlockRejectReason.IncomingFromBackArc;
			return false;
		}

		if ( !IsInOverheadBlockZone( pc, angleDegrees ) )
		{
			reason = MeleeBlockRejectReason.WrongBlockForAngle;
			return false;
		}

		reason = MeleeBlockRejectReason.None;
		return true;
	}

	static bool TryResolveLateralBlock( PlayerCombat pc, float angleDegrees, bool isLeftBlock, out MeleeBlockRejectReason reason )
	{
		if ( IsInLateralBackArc( pc, angleDegrees ) )
		{
			reason = MeleeBlockRejectReason.IncomingFromBackArc;
			return false;
		}

		var inZone = isLeftBlock ? IsInLeftBlockZone( pc, angleDegrees ) : IsInRightBlockZone( pc, angleDegrees );
		if ( !inZone )
		{
			reason = MeleeBlockRejectReason.WrongBlockForAngle;
			return false;
		}

		reason = MeleeBlockRejectReason.None;
		return true;
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
