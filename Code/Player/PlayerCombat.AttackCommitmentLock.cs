using System;
using Sandbox;

namespace Survival;

/// <summary>
/// During a committed melee swing (windup → recovery): walk-only on the ground, slowed look,
/// and a soft cone clamp around the aim at attack start.
/// </summary>
public partial class PlayerCombat : Component, PlayerController.IEvents
{
	[Property, Group( "Combat — Attack commitment" ), Title( "Walk-only + look lock enabled" )]
	public bool MeleeAttackCommitmentLockEnabled { get; set; } = true;

	/// <summary>Multiplies Walk/Run wish speed while committed (0.1 = 10% speed). Sprint is suppressed.</summary>
	[Property, Group( "Combat — Attack commitment" ), Title( "Move speed scale while attacking" ), Range( 0.05f, 1f ), Step( 0.05f )]
	public float MeleeAttackMoveSpeedScale { get; set; } = 0.1f;

	/// <summary>Multiplies per-frame look delta while the swing is committed (1 = normal, 0.4 = 40% mouse rate).</summary>
	[Property, Group( "Combat — Attack commitment" ), Title( "Look sensitivity scale" ), Range( 0f, 1f ), Step( 0.05f )]
	public float MeleeAttackLookSensitivityScale { get; set; } = 0.4f;

	[Property, Group( "Combat — Attack commitment" ), Title( "Max yaw from aim at swing start (°)" ), Range( 0f, 90f ), Step( 1f )]
	public float MeleeAttackMaxYawDegrees { get; set; } = 40f;

	[Property, Group( "Combat — Attack commitment" ), Title( "Max pitch from aim at swing start (°)" ), Range( 0f, 90f ), Step( 1f )]
	public float MeleeAttackMaxPitchDegrees { get; set; } = 30f;

	/// <summary>Host→peers: true for the full authoritative swing lifetime (windup through recovery).</summary>
	[Sync( SyncFlags.FromHost )]
	public bool MeleeAttackLocomotionLock { get; private set; }

	bool _meleeLookLockActive;
	Angles _meleeLookLockBasis;

	/// <summary>
	/// Ground move is slowed + sprint suppressed while a committed sword swing OR combat recovery / hit
	/// reaction (including post-shove lock) is active — including local press/windup before the host starts the sweep.
	/// Airborne velocity is left alone for the sprint gate; wish speeds still scale when walk-only is active on ground.
	/// </summary>
	public bool IsMeleeAttackWalkOnlyActive =>
		IsCombatActionLocked
		|| (MeleeAttackCommitmentLockEnabled && (MeleeAttackLocomotionLock || IsLocalAttackCommitmentPreview()));

	/// <summary>Owner prediction: slow + no sprint from Attack1 press through post-release phase / windup.</summary>
	bool IsLocalAttackCommitmentPreview()
	{
		if ( !MeleeAttackCommitmentLockEnabled || !IsLocalCombatDriver() )
			return false;

		// Inventory/crafting soft-cursor uses Attack1 for hold-to-craft / drag — not a sword swing.
		var menu = Components.Get<PlayerGameMenuController>();
		if ( menu is not null && menu.IsMenuOpen )
			return false;

		if ( ServerHasActiveMeleeAttackAction || _primarySwingPhaseActive || _primary.Down )
			return true;

		if ( Input.Down( PrimaryAttackAction ) )
			return true;

		var anim = Components.Get<PlayerAnimation>();
		return anim is not null && anim.IsMeleeWindupHoldActive;
	}

	/// <summary>Host: begin/end the commitment lock for the authoritative melee action.</summary>
	internal void SetMeleeAttackCommitmentLock( bool active )
	{
		if ( !MeleeAttackCommitmentLockEnabled )
			active = false;

		MeleeAttackLocomotionLock = active;

		if ( active )
			BeginMeleeAttackLookLock();
		else
			EndMeleeAttackLookLock();
	}

	void TickMeleeAttackLookLockFromSync()
	{
		if ( !MeleeAttackCommitmentLockEnabled )
		{
			if ( _meleeLookLockActive )
				EndMeleeAttackLookLock();
			return;
		}

		var want = MeleeAttackLocomotionLock || ServerHasActiveMeleeAttackAction;
		if ( want && !_meleeLookLockActive )
			BeginMeleeAttackLookLock();
		else if ( !want && _meleeLookLockActive )
			EndMeleeAttackLookLock();
	}

	void BeginMeleeAttackLookLock()
	{
		_meleeLookLockBasis = GetCurrentEyeAnglesForLookLock();
		_meleeLookLockActive = true;
	}

	void EndMeleeAttackLookLock()
	{
		_meleeLookLockActive = false;
	}

	/// <summary>
	/// PlayerController passes the per-frame look <b>delta</b>. Scale it, then keep absolute aim inside the start cone.
	/// </summary>
	public void OnEyeAngles( ref Angles angles )
	{
		if ( !ShouldApplyMeleeAttackLookLockLocally() )
			return;

		var scale = Math.Clamp( MeleeAttackLookSensitivityScale, 0f, 1f );
		angles = new Angles( angles.pitch * scale, angles.yaw * scale, angles.roll * scale );

		var controller = Components.Get<PlayerController>();
		if ( controller is null )
			return;

		var current = controller.EyeAngles;
		var proposed = new Angles(
			current.pitch + angles.pitch,
			current.yaw + angles.yaw,
			current.roll + angles.roll );

		var clamped = ClampEyeAnglesToMeleeLookCone( _meleeLookLockBasis, proposed );
		angles = new Angles(
			NormalizeAngleDelta( clamped.pitch - current.pitch ),
			NormalizeAngleDelta( clamped.yaw - current.yaw ),
			NormalizeAngleDelta( clamped.roll - current.roll ) );
	}

	bool ShouldApplyMeleeAttackLookLockLocally() =>
		MeleeAttackCommitmentLockEnabled
		&& _meleeLookLockActive
		&& IsLocalCombatDriver();

	Angles ClampEyeAnglesToMeleeLookCone( in Angles basis, in Angles proposed )
	{
		var maxYaw = Math.Max( 0f, MeleeAttackMaxYawDegrees );
		var maxPitch = Math.Max( 0f, MeleeAttackMaxPitchDegrees );

		var dyaw = Math.Clamp( NormalizeAngleDelta( proposed.yaw - basis.yaw ), -maxYaw, maxYaw );
		var dpitch = Math.Clamp( NormalizeAngleDelta( proposed.pitch - basis.pitch ), -maxPitch, maxPitch );

		return new Angles( basis.pitch + dpitch, basis.yaw + dyaw, proposed.roll );
	}

	Angles GetCurrentEyeAnglesForLookLock()
	{
		var controller = Components.Get<PlayerController>();
		if ( controller is not null )
			return controller.EyeAngles;

		var cam = ResolveIntentCamera();
		if ( cam.IsValid() )
			return cam.WorldRotation.Angles();

		return WorldRotation.Angles();
	}

	static float NormalizeAngleDelta( float delta )
	{
		while ( delta > 180f )
			delta -= 360f;
		while ( delta < -180f )
			delta += 360f;
		return delta;
	}
}
