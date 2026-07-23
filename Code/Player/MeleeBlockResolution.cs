using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Server-authoritative block: hold Attack2 to cover a front facing arc (± half of <see cref="PlayerCombat.MeleeBlockFrontArcDegrees"/>).
/// Teardrop L/R/U is input/HUD only and does not gate success. Confirm with a body-radius shell ray hit before the body.
/// Outcomes (duration / HP / stamina) come from <see cref="MeleeBlockStaggerCatalog"/>.
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

		var yaw = GetFacingYawDegrees( defender );
		var angleFromHit = ComputeIncomingHitAngleDegrees( defender.GameObject.WorldPosition, yaw, hitPos );
		if ( MathF.Abs( angleFromHit ) > 1e-3f )
			return angleFromHit;

		return ComputeIncomingHitAngleDegrees( defender.GameObject.WorldPosition, yaw, attackerPos );
	}

	public static float GetFacingYawDegrees( PlayerCombat defender )
	{
		if ( defender is null || !defender.GameObject.IsValid() )
			return 0f;

		return defender.IsAuthoritativeMeleeBlocking
			? defender.GetBlockCombatBasisYaw()
			: defender.GetMeleeCombatBasisRotation().Angles().yaw;
	}

	public static float GetFrontHalfArcDegrees( PlayerCombat pc ) =>
		Math.Max( 1f, pc.MeleeBlockFrontArcDegrees ) * 0.5f;

	public static bool IsOutsideFrontArc( PlayerCombat pc, float angleDegrees ) =>
		MathF.Abs( angleDegrees ) > GetFrontHalfArcDegrees( pc ) + 1e-4f;

	public static bool IsInsideFrontArc( PlayerCombat pc, float angleDegrees ) =>
		!IsOutsideFrontArc( pc, angleDegrees );

	public static bool TryResolve(
		PlayerCombat defender,
		in MeleeBlockContact contact,
		bool logRejections,
		out MeleeBlockOutcome outcome,
		out MeleeBlockRejectReason rejectReason ) =>
		TryResolve( defender, in contact, logRejections, out outcome, out rejectReason, out _ );

	public static bool TryResolve(
		PlayerCombat defender,
		in MeleeBlockContact contact,
		bool logRejections,
		out MeleeBlockOutcome outcome,
		out MeleeBlockRejectReason rejectReason,
		out MeleeBlockValidationTrace trace )
	{
		outcome = default;
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

		var angle = ComputeIncomingAngleFromAttacker( defender, contact.AttackerPosition );
		var blockDir = defender.AuthoritativeMeleeBlockDirection;
		var perfectParry = IsPerfectParry( defender, contact.HitSandboxTime );
		trace = new MeleeBlockValidationTrace
		{
			IncomingAngleDegrees = angle,
			BlockDirection = blockDir,
			WasBlocking = true,
			WasPerfectParry = perfectParry
		};

		if ( IsOutsideFrontArc( defender, angle ) )
		{
			rejectReason = MeleeBlockRejectReason.IncomingFromBackArc;
			trace = trace with { RejectReason = rejectReason };
			LogReject( defender, contact, logRejections, rejectReason, angle, blockDir, perfectParry );
			return false;
		}

		if ( !contact.AttackRayGeometryValidated )
		{
			rejectReason = MeleeBlockRejectReason.RayMissedBlockRegion;
			trace = trace with { RejectReason = rejectReason };
			LogReject( defender, contact, logRejections, rejectReason, angle, blockDir, perfectParry );
			return false;
		}

		outcome = MeleeBlockStaggerCatalog.Resolve( contact.AttackWasHeavy, perfectParry );
		rejectReason = MeleeBlockRejectReason.None;
		trace = trace with
		{
			RejectReason = rejectReason,
			OutcomeId = outcome.OutcomeId,
			StaggerDurationSeconds = outcome.DurationSeconds,
			HealthDamage = outcome.HealthDamage,
			StaminaCost = outcome.StaminaCost
		};

		if ( perfectParry )
		{
			Log.Info(
				$"[MeleeBlock] PARRY {defender.GameObject.Name}: {outcome.OutcomeId} heavy={contact.AttackWasHeavy} "
				+ $"angle={angle:0.#}° duration={outcome.DurationSeconds:0.###}s hp={outcome.HealthDamage:0.#} stam={outcome.StaminaCost:0.#} "
				+ $"from {contact.AttackerRoot?.Name}" );
		}

		return true;
	}

	public static bool IsPerfectParry( PlayerCombat defender, double hitSandboxTime )
	{
		if ( defender is null )
			return false;

		var window = Math.Max( 0f, defender.MeleeBlockParryWindowSeconds );
		if ( window <= 1e-6f )
			return false;

		var blockAge = hitSandboxTime - defender.ServerBlockStartedAtSandbox;
		return blockAge >= -1e-4 && blockAge <= window + 1e-4;
	}

	public static float ComputeIncomingAngleFromAttacker( PlayerCombat defender, Vector3 attackerPosition )
	{
		if ( defender is null || !defender.GameObject.IsValid() )
			return 0f;

		return ComputeIncomingHitAngleDegrees(
			defender.GameObject.WorldPosition,
			GetFacingYawDegrees( defender ),
			attackerPosition );
	}

	static void LogReject(
		PlayerCombat defender,
		in MeleeBlockContact contact,
		bool logRejections,
		MeleeBlockRejectReason rejectReason,
		float angle,
		byte blockDir,
		bool perfectParry )
	{
		if ( !logRejections || rejectReason == MeleeBlockRejectReason.None || !defender.LogMeleeBlockRejectionsToConsole )
			return;

		Log.Info(
			$"[MeleeBlock] reject {defender.GameObject.Name}: {rejectReason} angle={angle:0.#}° "
			+ $"teardrop={SwingDirs.Letter( blockDir )} (hud-only) heavy={contact.AttackWasHeavy} parryCandidate={perfectParry} "
			+ $"swing={SwingDirs.Letter( contact.AttackSwingDir )} from {contact.AttackerRoot?.Name}" );
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
