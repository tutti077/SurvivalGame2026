using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host: what happens when an attacker's blade reaches a defender that has <see cref="PlayerCombat"/> (Mark 2026-11-14,
/// directional exchange). In order:
/// <list type="number">
/// <item><b>Chamber</b> — the defender's own swing mirrors the incoming attack and is either still held in its windup
/// (the blow is stopped, then the held swing strikes) or releases within ±<see cref="PlayerCombat.ChamberToleranceSeconds"/> of
/// this contact (<see cref="MeleeAttackTypes.Mirror"/>: overhead vs overhead, left vs
/// right, right vs left): the blades meet, nobody is hurt, the incoming swing stops as if blocked and the chambering swing
/// carries on.</item>
/// <item><b>Directional block</b> — the guard is up and active (<see cref="PlayerCombat.BlockActivationSeconds"/> after it
/// rose), the attacker is inside the front arc (<see cref="PlayerCombat.MeleeBlockFrontArcDegrees"/>), the attack ray met
/// the body-radius shell first, and the effective guard direction (a switch takes
/// <see cref="PlayerCombat.BlockSwitchSeconds"/>) is the side the blade comes in from (<see cref="ResolveIncomingSide"/>) — the
/// attacker's own side when it stands off to the defender's left / right, else left / right by where the swing started
/// relative to the defender and up for an overhead. Projectiles ignore the direction.</item>
/// </list>
/// Outcome costs come from <see cref="MeleeBlockStaggerCatalog"/>.
/// </summary>
public static class MeleeBlockResolution
{
	/// <summary>Signed angle (°) of <paramref name="point"/> around <paramref name="defenderPos"/>, 0 = straight ahead, + = to the right.</summary>
	public static float ComputeIncomingHitAngleDegrees( Vector3 defenderPos, float defenderYawDegrees, Vector3 point )
	{
		var basis = new Angles( 0f, defenderYawDegrees, 0f ).ToRotation();
		var to = (point - defenderPos).WithZ( 0f );
		if ( to.LengthSquared < 1e-6f )
			return 0f;

		to = to.Normal;
		return MathF.Atan2( Vector3.Dot( to, basis.Right ), Vector3.Dot( to, basis.Forward ) ) * (180f / MathF.PI);
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

		if ( defender.IsAuthoritativeMeleeBlocking )
			return defender.GetBlockCombatBasisYaw();

		// Not guarding: the body's facing (PlayerMovement.BodyTurn.cs, the same on every machine from the synced look) -
		// the host's copy of a client pawn never turns its root, so the root yaw was its spawn yaw.
		return defender.Components.Get<PlayerMovement>( FindMode.EverythingInSelf ) is { } movement
			? movement.BodyYawDegrees
			: defender.GetMeleeCombatBasisRotation().Angles().yaw;
	}

	/// <summary>
	/// The guard side against this swing as it was latched at the swing's windup start (Mark 2026-11-17: "lock side per
	/// swing"; <see cref="PlayerCombat.TryGetLatchedIncomingSide"/>), else resolved now (<see cref="ResolveIncomingSide"/>).
	/// </summary>
	public static byte ResolveIncomingSideLatched( PlayerCombat defender, GameObject attackerRoot, byte attackType )
	{
		if ( TryGetAttackerCombat( attackerRoot ) is { } attacker && attacker.TryGetLatchedIncomingSide( defender, out var latched ) )
			return latched;

		return ResolveIncomingSide( defender, attackerRoot, attackType );
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
		var blocked = TryResolveCore( defender, in contact, logRejections, out outcome, out rejectReason, out trace );

		// Playtest log: a blow that got past a guard its owner was working (the "I blocked in time" report).
		if ( !blocked && PlaytestLog.Enabled && defender is not null && defender.GameObject.IsValid() )
		{
			var incoming = contact.IgnoreGuardDirection || !contact.AttackerRoot.IsValid()
				? SwingDirs.Up
				: ResolveIncomingSideLatched( defender, contact.AttackerRoot, contact.AttackType );
			PlaytestLog.HostGuardMiss( defender, TryGetAttackerCombat( contact.AttackerRoot ), contact.HitSandboxTime, rejectReason, incoming );
		}

		return blocked;
	}

	static bool TryResolveCore(
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

		if ( TryChamber( defender, in contact ) )
		{
			outcome = MeleeBlockStaggerCatalog.Chamber();
			trace = new MeleeBlockValidationTrace { WasChambered = true, OutcomeId = outcome.OutcomeId };
			return true;
		}

		if ( !defender.IsAuthoritativeMeleeBlocking )
		{
			rejectReason = MeleeBlockRejectReason.NotBlocking;
			trace = new MeleeBlockValidationTrace { WasBlocking = false, RejectReason = rejectReason };
			return false;
		}

		var blockDir = defender.GetEffectiveGuardDirection( contact.HitSandboxTime );

		// The guarding weapon where it is right now - rising, crossing to another side, or raised - met by the blade.
		// Checked once, only when the rules below need it.
		bool? physical = null;
		bool PhysicallyMet( in MeleeBlockContact c ) => physical ??= TryPhysicalIntercept( defender, blockDir, in c );

		// The guard needs BlockActivationSeconds after it rose before it covers its side. Until then it stops only a
		// blade that runs into the weapon on its way up (Mark 2026-11-23).
		if ( contact.HitSandboxTime + 1e-4 < defender.ServerBlockActiveFromSandbox && !PhysicallyMet( in contact ) )
		{
			rejectReason = MeleeBlockRejectReason.BlockStartedAfterHit;
			trace = new MeleeBlockValidationTrace { WasBlocking = true, BlockDirection = blockDir, RejectReason = rejectReason };
			LogReject( defender, contact, logRejections, rejectReason, 0f, blockDir, SwingDirs.Up );
			return false;
		}

		var angle = ComputeIncomingAngleFromAttacker( defender, contact.AttackerPosition );
		var incoming = contact.IgnoreGuardDirection ? blockDir : ResolveIncomingSideLatched( defender, contact.AttackerRoot, contact.AttackType );
		trace = new MeleeBlockValidationTrace
		{
			IncomingAngleDegrees = angle,
			BlockDirection = blockDir,
			WasBlocking = true,
		};

		if ( IsOutsideFrontArc( defender, angle ) )
		{
			rejectReason = MeleeBlockRejectReason.IncomingFromBackArc;
			trace = trace with { RejectReason = rejectReason };
			LogReject( defender, contact, logRejections, rejectReason, angle, blockDir, incoming );
			return false;
		}

		if ( !contact.AttackRayGeometryValidated )
		{
			rejectReason = MeleeBlockRejectReason.RayMissedBlockRegion;
			trace = trace with { RejectReason = rejectReason };
			LogReject( defender, contact, logRejections, rejectReason, angle, blockDir, incoming );
			return false;
		}

		// The side the marker called always blocks once the guard is up. Any other guard - and a guard still rising -
		// stops the blow when the blade physically runs into the guarding weapon (the block pivot: the guard held on its
		// side, the body turned into the blow; a guard caught on its way up or across).
		if ( (incoming != blockDir || contact.HitSandboxTime + 1e-4 < defender.ServerBlockActiveFromSandbox) && PhysicallyMet( in contact ) )
			incoming = blockDir;

		if ( incoming != blockDir )
		{
			rejectReason = MeleeBlockRejectReason.WrongDirection;
			trace = trace with { RejectReason = rejectReason };
			LogReject( defender, contact, logRejections, rejectReason, angle, blockDir, incoming );
			return false;
		}

		outcome = MeleeBlockStaggerCatalog.Resolve( IsAttackerSwingHeavy( contact.AttackerRoot ) );
		trace = trace with
		{
			RejectReason = MeleeBlockRejectReason.None,
			OutcomeId = outcome.OutcomeId,
			HealthDamage = outcome.HealthDamage,
			StaminaCost = outcome.StaminaCost
		};
		return true;
	}

	/// <summary>
	/// Host: the attacker's blade runs into the defender's guarding weapon on its way in, where that weapon is at the
	/// contact - rising, crossing or raised (<see cref="MeleeGuardGeometry.TryMeetGuard"/>). Projectiles never do.
	/// </summary>
	static bool TryPhysicalIntercept( PlayerCombat defender, byte blockDir, in MeleeBlockContact contact )
	{
		if ( !defender.MeleePhysicalGuardEnabled || contact.IgnoreGuardDirection
		     || TryGetAttackerCombat( contact.AttackerRoot ) is not { } attacker || attacker == defender )
			return false;

		var meet = MeleeGuardGeometry.TryMeetGuard( defender, attacker, contact.AttackType, contact.HitSandboxTime );
		if ( !meet.Caught )
			return false;

		Log.Info( $"[MeleeBlock] {defender.GameObject.Name}: {SwingDirs.Letter( blockDir )} guard physically met "
		          + $"{contact.AttackerRoot.Name}'s {MeleeAttackTypes.Label( contact.AttackType )} blade "
		          + $"(rise {defender.GetGuardRise01( contact.HitSandboxTime ):0.##}, {meet.Distance:0}u apart)" );
		return true;
	}

	/// <summary>Host: the attacker's swing in flight is a heavy. Arrows and other non-swings are light.</summary>
	internal static bool IsAttackerSwingHeavy( GameObject attackerRoot ) =>
		TryGetAttackerCombat( attackerRoot ) is { } attacker
		&& attacker.TryGetServerSwingInfo( out var heavy, out _ )
		&& heavy;

	static PlayerCombat TryGetAttackerCombat( GameObject attackerRoot ) =>
		attackerRoot.IsValid() ? attackerRoot.Components.Get<PlayerCombat>( FindMode.EverythingInSelfAndDescendants ) : null;

	/// <summary>
	/// Host: the defender's own swing meets this blade — its release starts within the tolerance of the contact and it is
	/// the mirror of the incoming attack (Mark 2026-11-16: "it needs to mirror the attack. Overhead to overhead then left
	/// attack vs right and right attack vs left"). Attack types, not positions: the match holds at any angle between them.
	/// </summary>
	static bool TryChamber( PlayerCombat defender, in MeleeBlockContact contact )
	{
		if ( contact.IgnoreGuardDirection || TryGetAttackerCombat( contact.AttackerRoot ) is not { } attacker || attacker == defender )
			return false;

		// Held chamber (Mark 2026-11-18: "if were holding left click when the matching attack is coming ... it should
		// block the hit and then swing the sword after ... its kinda 2 phases"): the defender's swing is still winding up
		// with the button down and mirrors the blow, from the front arc - the blow is stopped, and the held swing is
		// released right then, so it strikes after as its own release rules allow.
		if ( defender.TryGetServerHeldWindup( out var heldType ) && heldType == MeleeAttackTypes.Mirror( contact.AttackType )
		     && IsInsideFrontArc( defender, ComputeIncomingAngleFromAttacker( defender, contact.AttackerPosition ) ) )
		{
			defender.HostOnChambered( attacker );
			defender.HostReleaseHeldSwingAfterChamber();
			Log.Info( $"[MeleeChamber] {defender.GameObject.Name} held {MeleeAttackTypes.Label( heldType )} met {contact.AttackerRoot.Name}'s "
			          + $"{MeleeAttackTypes.Label( contact.AttackType )} - blocked, the held swing strikes next" );
			return true;
		}

		if ( !defender.TryGetServerReleaseStart( out var releaseStart, out var defenderType ) )
			return false;

		var tolerance = Math.Max( 0f, defender.ChamberToleranceSeconds );
		if ( Math.Abs( contact.HitSandboxTime - releaseStart ) > tolerance + 1e-4 )
			return false;

		if ( defenderType != MeleeAttackTypes.Mirror( contact.AttackType ) )
			return false;

		defender.HostOnChambered( attacker );
		Log.Info( $"[MeleeChamber] {defender.GameObject.Name} chambered {contact.AttackerRoot.Name} "
		          + $"{MeleeAttackTypes.Label( defenderType )} vs {MeleeAttackTypes.Label( contact.AttackType )} "
		          + $"Δt={(contact.HitSandboxTime - releaseStart) * 1000.0:0}ms" );
		return true;
	}

	/// <summary>
	/// The guard side that stops this attack. An attacker standing off to the defender's side (more than
	/// <see cref="PlayerCombat.MeleeBlockFlankDegrees"/> from its facing) is blocked on that side whatever the swing
	/// (Mark 2026-11-16: "if they do overhead or anything else, right block. because they're to my RIGHT"). An attacker
	/// in front: up for an overhead, else left / right by where the swing started (its tip at progress 0) relative to
	/// the defender's facing.
	/// </summary>
	public static byte ResolveIncomingSide( PlayerCombat defender, GameObject attackerRoot, byte attackType )
	{
		// A side swing that reaches the body: the side of the body the blade arrives at, wherever the attacker stands
		// (Mark 2026-11-18: "if were really close it tells him itll hit him from the wrong side" - up close a small
		// sidestep put the attacker past the flank angle and the marker called the attacker's side while the blade came
		// round the other). Out of reach, or an overhead, the rules below decide.
		if ( attackType != MeleeAttackTypes.Forward && defender is not null && defender.GameObject.IsValid()
		     && MeleeGuardGeometry.TryFindBodyEntry( defender, TryGetAttackerCombat( attackerRoot ), attackType, out var entry ) )
		{
			var right = new Angles( 0f, GetFacingYawDegrees( defender ), 0f ).ToRotation().Right;
			var lateral = Vector3.Dot( entry - defender.GameObject.WorldPosition, right );
			if ( MathF.Abs( lateral ) > 2f )
				return lateral > 0f ? SwingDirs.Right : SwingDirs.Left;
		}

		if ( defender is not null && defender.GameObject.IsValid() && attackerRoot.IsValid() )
		{
			var flank = defender.MeleeBlockFlankDegrees;
			var angle = ComputeIncomingAngleFromAttacker( defender, attackerRoot.WorldPosition );
			if ( flank > 0f && MathF.Abs( angle ) > flank )
				return angle > 0f ? SwingDirs.Right : SwingDirs.Left;
		}

		if ( attackType == MeleeAttackTypes.Forward )
			return SwingDirs.Up;

		var attacker = TryGetAttackerCombat( attackerRoot );
		if ( attacker is null || defender is null )
			return SwingDirs.Up;

		var basis = attacker.GetMeleeCombatBasisRotation( attackType );
		MeleeAttackPath.EvaluateWorldBlade( attacker.GameObject, attacker, attackType, 0f, basis, out var startTip, out _ );
		var facing = new Angles( 0f, GetFacingYawDegrees( defender ), 0f ).ToRotation();
		var side = Vector3.Dot( startTip - defender.GameObject.WorldPosition, facing.Right );
		return side >= 0f ? SwingDirs.Right : SwingDirs.Left;
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
		byte incoming )
	{
		if ( !logRejections || rejectReason == MeleeBlockRejectReason.None || !defender.LogMeleeBlockRejectionsToConsole )
			return;

		Log.Info(
			$"[MeleeBlock] reject {defender.GameObject.Name}: {rejectReason} angle={angle:0.#}° "
			+ $"guard={SwingDirs.Letter( blockDir )} incoming={SwingDirs.Letter( incoming )} "
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
}
