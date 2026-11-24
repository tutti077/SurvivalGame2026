using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Body facing (Mark 2026-11-17: "when we spin our mouse it rotates the player ... It should basically always keep the
/// player facing where your mouse is looking", then "prevent players from rotating too fast, the players body should have
/// a max rotation speed (and sword attacks have no effect on that)"; 540°/s was "a tad too loose", 360 since). The body
/// always turns toward the look yaw, never faster than <see cref="BodyTurnDegreesPerSecond"/> — standing, walking, swinging, blocking, first or third person
/// alike. Every machine runs the same tracker on the pawn's synced <see cref="PlayerController.EyeAngles"/>, so the host's
/// copy of a client's body (which the swing arc turns with, <see cref="PlayerCombat"/>) obeys the cap too.
/// <see cref="PlayerAnimation"/> applies it to the Body renderer child; the stock controller's own body rotation is
/// switched off (<see cref="TickControllerBodyRotationOff"/>). Seated, in the wingsuit or in the stasis pod those
/// systems own the body and the tracker just follows the look.
/// </summary>
public partial class PlayerMovement
{
	[Property, Group( "Camera" ), Title( "Body turn rate (deg/s)" ), Description( "The body always faces the mouse look, turning no faster than this. A swing turned with the body hits harder (PlayerCombat Turn damage)." ), Range( 90f, 1440f ), Step( 10f )]
	public float BodyTurnDegreesPerSecond { get; set; } = 360f;

	double _bodyYawAt = double.NaN;
	float _bodyYawUnwrapped;
	/// <summary>The look yaw the body chases, counted on through full turns like the body's own.</summary>
	float _lookYawUnwrapped;
	float _bodyTurnRate;

	/// <summary>The body tracker drives the Body renderer (not seated, gliding, in the pod or lying in a bed).</summary>
	public bool OwnsBodyFacing => !IsSeated && !IsWingsuitDeployed && !PodLocked && !IsInBed;

	/// <summary>Body yaw (deg), counted on through full turns (a spin never wraps) — swings measure their turn on it.</summary>
	public float BodyYawUnwrapped
	{
		get
		{
			TickBodyYaw();
			return _bodyYawUnwrapped;
		}
	}

	/// <summary>Body yaw (deg, -180..180).</summary>
	public float BodyYawDegrees => Angles.NormalizeAngle( BodyYawUnwrapped );

	/// <summary>Signed body turn speed this frame (deg/s; + = turning left, s&amp;box yaw).</summary>
	public float BodyTurnRateDegreesPerSecond
	{
		get
		{
			TickBodyYaw();
			return _bodyTurnRate;
		}
	}

	/// <summary>Face the look at once (spawns, teleports, scripted test resets).</summary>
	public void SnapBodyYawToLook() => _bodyYawAt = double.NaN;

	/// <summary>Advance the tracker to this frame (idempotent per frame; any caller may ask first).</summary>
	void TickBodyYaw()
	{
		var now = Time.NowDouble;
		if ( _bodyYawAt == now )
			return;

		_controller ??= Components.Get<PlayerController>( FindMode.EverythingInSelf );
		if ( _controller is null || !_controller.IsValid() )
			return;

		// The look is counted on through full turns too, and the body chases that: a look that runs more than half a
		// turn ahead of the body keeps the body turning the way the mouse went (Mark 2026-11-18: "if you do a full spin
		// the player rotates the other direction ... they should just rotate the right direction at the set pace" - the
		// shortest way to a wrapped look yaw flipped the turn, and the swing arc with it). Whole turns of backlog are
		// dropped, so the body never owes more than one turn.
		var eye = _controller.EyeAngles.yaw;
		if ( double.IsNaN( _bodyYawAt ) || !OwnsBodyFacing )
			_lookYawUnwrapped = _bodyYawUnwrapped + Angles.NormalizeAngle( eye - _bodyYawUnwrapped );
		else
			_lookYawUnwrapped += Angles.NormalizeAngle( eye - _lookYawUnwrapped );

		var toLook = _lookYawUnwrapped - _bodyYawUnwrapped;
		if ( MathF.Abs( toLook ) >= 360f )
		{
			toLook -= 360f * MathF.Truncate( toLook / 360f );
			_lookYawUnwrapped = _bodyYawUnwrapped + toLook;
		}

		if ( double.IsNaN( _bodyYawAt ) || !OwnsBodyFacing )
		{
			_bodyYawUnwrapped += toLook;
			_bodyTurnRate = 0f;
			_bodyYawAt = now;
			return;
		}

		// Clamped so a long hitch never reads as a fast turn (the body just keeps catching up).
		var dt = (float)Math.Clamp( now - _bodyYawAt, 0.0, 0.1 );
		_bodyYawAt = now;
		var maxStep = Math.Max( 1f, BodyTurnDegreesPerSecond ) * dt;
		var step = Math.Clamp( toLook, -maxStep, maxStep );
		_bodyYawUnwrapped += step;
		_bodyTurnRate = dt > 1e-5f ? step / dt : 0f;
	}

	/// <summary>
	/// Every machine, every frame: the stock controller never turns the body itself (180° = the look can never get past
	/// its limit; the speed stays above 0, which stock code reads as "turn instantly"). The tracker above owns facing.
	/// </summary>
	void TickControllerBodyRotationOff()
	{
		_controller ??= Components.Get<PlayerController>( FindMode.EverythingInSelf );
		if ( _controller is null || !_controller.IsValid() )
			return;

		if ( _controller.RotationAngleLimit != 180f )
			_controller.RotationAngleLimit = 180f;
		if ( _controller.RotationSpeed != 0.001f )
			_controller.RotationSpeed = 0.001f;
		TickBodyYaw();
	}
}
