using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Augment-driven movement: Double Jump (passive, Jump key in the air), Spring Legs (trigger),
/// Recovery Slide (trigger) and the Grapple Drive winch scale. All read live state from
/// <see cref="PlayerAugments.IsAbilityOn"/>; triggers arrive through the Try* entry points.
/// </summary>
public sealed partial class PlayerMovement
{
	bool _doubleJumpUsed;
	bool _wasGroundedForDoubleJump = true;
	PlayerAugments _augments;

	Vector3 _slideDir;
	float _slideSpeed;
	float _slideRefundStamina;
	double _slideUntil;
	bool _slideActive;

	PlayerAugments ResolveAugments() =>
		_augments ??= Components.Get<PlayerAugments>();

	/// <summary>
	/// One mid-air hop per flight while Double Jump is installed — available after a grounded jump
	/// <b>or</b> walking off a ledge, at any time before landing.
	/// </summary>
	void TickAugmentJumpGates()
	{
		if ( !IsLocalMovementDriver() )
			return;

		var augments = ResolveAugments();
		if ( augments is null )
			return;

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null )
			return;

		var hasDoubleJump = augments.IsAbilityOn( AugmentAbility.DoubleJump );
		TickDoubleJumpFlightState( hasDoubleJump );

		var jumpPressed = !string.IsNullOrWhiteSpace( JumpInputAction ) && Input.Pressed( JumpInputAction );
		if ( !jumpPressed )
			return;

		if ( IsHitReactionActive() )
			return;

		// No mid-air hop off the rope — same rule as normal jump while grappled.
		if ( GrappleAttached )
			return;

		// Air hop: any time while airborne until used once this flight (jump-launch or walk-off).
		if ( !_controller.IsOnGround && hasDoubleJump && !_doubleJumpUsed )
		{
			if ( TryPerformDoubleJump() )
			{
				_doubleJumpUsed = true;
				ClearActionIfPressed( JumpInputAction );
			}
		}
	}

	/// <summary>Grounded → recharge. Airborne (from jump or cliff) → keep charge until the air hop is spent.</summary>
	void TickDoubleJumpFlightState( bool hasDoubleJump )
	{
		var grounded = _controller.IsOnGround;
		if ( grounded )
		{
			_doubleJumpUsed = false;
		}
		else if ( _wasGroundedForDoubleJump && hasDoubleJump )
		{
			// Just left the ground — grant the air hop for this flight.
			_doubleJumpUsed = false;
		}

		_wasGroundedForDoubleJump = grounded;
	}

	bool TryPerformDoubleJump()
	{
		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null )
			return false;

		var jumpSpeed = Math.Max( 1f, _controller.JumpSpeed );
		var body = Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return false;

		var v = body.Velocity;
		var up = Vector3.Dot( v, Vector3.Up );
		body.Velocity = v + Vector3.Up * (jumpSpeed - up);
		return true;
	}

	void OnAugmentLanded()
	{
		_doubleJumpUsed = false;
	}

	/// <summary>Spring Legs trigger: grounded launch at <paramref name="multiplier"/> × the controller jump speed. False when not grounded / locked.</summary>
	public bool TryAugmentSpringJump( float multiplier )
	{
		if ( !IsLocalMovementDriver() )
			return false;

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null || !_controller.IsValid() || !_controller.IsOnGround )
			return false;

		if ( IsHitReactionActive() || TrapLocked || GrappleAttached || WingsuitDeployed || EventInputLocked )
			return false;

		var jumpSpeed = Math.Max( 1f, _controller.JumpSpeed ) * Math.Max( 1f, multiplier );
		_controller.Jump( Vector3.Up * jumpSpeed );
		return true;
	}

	/// <summary>
	/// Recovery Slide trigger: while sprinting on the ground, hold the current heading at sprint speed
	/// for <see cref="AugmentDefinition.EffectSeconds"/>, then refund <see cref="AugmentDefinition.EffectScale"/> stamina.
	/// </summary>
	public bool TryAugmentRecoverySlide( AugmentDefinition def )
	{
		if ( def is null || !IsLocalMovementDriver() || _slideActive )
			return false;

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null || !_controller.IsValid() || !_controller.IsOnGround )
			return false;

		if ( IsHitReactionActive() || TrapLocked || GrappleAttached || WingsuitDeployed || EventInputLocked )
			return false;

		if ( !WantsSprintStaminaSpend() )
			return false;

		var body = _controller.Body ?? Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return false;

		var flat = body.Velocity.WithZ( 0f );
		if ( flat.LengthSquared < 1e-3f )
			flat = GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( flat.LengthSquared < 1e-6f )
			return false;

		_slideDir = flat.Normal;
		_slideSpeed = Math.Max( flat.Length, 60f );
		_slideUntil = Time.NowDouble + Math.Max( 0.1f, def.EffectSeconds );
		_slideRefundStamina = Math.Max( 0f, def.EffectScale );
		_slideActive = true;
		TickAugmentSlideMotion();
		return true;
	}

	/// <summary>Every PreInput frame: re-assert the slide velocity (controller friction would decay it), then pay out the refund.</summary>
	void TickAugmentSlideMotion()
	{
		if ( !_slideActive )
			return;

		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() || _controller is null )
		{
			_slideActive = false;
			return;
		}

		if ( Time.NowDouble >= _slideUntil || !_controller.IsOnGround || TrapLocked || IsHitReactionActive() )
		{
			_slideActive = false;
			if ( _slideRefundStamina > 0f )
				_vitals?.RequestVitalsDelta( 0f, _slideRefundStamina );
			return;
		}

		body.Velocity = new Vector3( _slideDir.x * _slideSpeed, _slideDir.y * _slideSpeed, body.Velocity.z );
	}

	/// <summary>Grapple Drive: sprint held while the rope winches in = EffectScale × winch rate.</summary>
	float GrappleDriveWinchScale()
	{
		var augments = ResolveAugments();
		if ( augments is null || !augments.IsAbilityOn( AugmentAbility.GrappleDrive ) )
			return 1f;

		if ( string.IsNullOrWhiteSpace( SprintInputAction ) || !Input.Down( SprintInputAction ) )
			return 1f;

		return augments.TryGetActiveDefinition( AugmentAbility.GrappleDrive, out var def )
			? Math.Max( 1f, def.EffectScale )
			: 1f;
	}
}
