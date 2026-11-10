using System;
using Sandbox;

namespace Survival;

/// <summary>
/// During a committed melee swing (windup → recovery): walk-only on the ground. The look is free through the windup /
/// held heavy (the swing aims wherever the player faces); once the strike swoops it is slowed — never limited, the
/// player can spin as far as they like (Mark 2026-11-05) — with the pitch held near the strike-start pitch. The swing
/// turns with the look throughout (<c>PlayerCombat.LiveSwingBasisYaw</c>).
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
		|| _bolaCharging
		|| (MeleeAttackCommitmentLockEnabled && (MeleeAttackLocomotionLock || IsLocalAttackCommitmentPreview()));

	/// <summary>Owner prediction: slow + no sprint from Attack1 press through post-release phase / windup.</summary>
	bool IsLocalAttackCommitmentPreview()
	{
		if ( !MeleeAttackCommitmentLockEnabled || !IsLocalCombatDriver() )
			return false;

		// Inventory/crafting soft-cursor uses Attack1 for craft clicks / drag — not a sword swing.
		var menu = Components.Get<PlayerGameMenuController>();
		if ( menu is not null && menu.IsMenuOpen )
			return false;

		// Tools that use Attack1 for their own job (wire stripper drag, hammer, hoe) never swing:
		// same gate as the owner swing tick, so holding the button keeps run + jump.
		var equipped = Components.Get<PlayerEquippedItem>();
		if ( equipped is not null && !equipped.HasAction( EquippedItemActions.PrimaryMelee ) )
			return false;

		if ( ServerHasActiveMeleeAttackAction || _ownerExpectsHostMeleeBusy )
			return true;

		if ( Input.Down( PrimaryAttackAction ) )
			return true;

		var anim = Components.Get<PlayerAnimation>();
		return anim is not null && anim.IsMeleeSwingSnapActive;
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
		{
			_meleeLookSwoopActive = false;
			return;
		}

		if ( !_meleeLookSwoopActive )
		{
			// The strike just started: the pitch cone is measured from where the player looks now.
			_meleeLookSwoopActive = true;
			_meleeLookLockBasis = GetCurrentEyeAnglesForLookLock();
		}

		var scale = Math.Clamp( MeleeAttackLookSensitivityScale, 0f, 1f );
		angles = new Angles( angles.pitch * scale, angles.yaw * scale, angles.roll * scale );

		var controller = Components.Get<PlayerController>();
		if ( controller is null )
			return;

		var current = controller.EyeAngles;

		// Yaw: just slowed (above), never limited.
		// Pitch: absolute cone around the pitch at the swing start.
		var maxPitch = Math.Max( 0f, MeleeAttackMaxPitchDegrees );
		var proposedPitch = current.pitch + angles.pitch;
		var dpitch = Math.Clamp( NormalizeAngleDelta( proposedPitch - _meleeLookLockBasis.pitch ), -maxPitch, maxPitch );
		var clampedPitch = _meleeLookLockBasis.pitch + dpitch;

		angles = new Angles( NormalizeAngleDelta( clampedPitch - current.pitch ), angles.yaw, angles.roll );
	}

	/// <summary>Only while the strike swoops on this owner's screen — the windup and a held heavy turn freely.</summary>
	bool ShouldApplyMeleeAttackLookLockLocally() =>
		MeleeAttackCommitmentLockEnabled
		&& _meleeLookLockActive
		&& IsLocalCombatDriver()
		&& Components.Get<PlayerAnimation>() is { IsMeleeStrikePresenting: true };

	/// <summary>The look lock is applying this frame (the yaw sum restarts from the facing at each strike).</summary>
	bool _meleeLookSwoopActive;

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
