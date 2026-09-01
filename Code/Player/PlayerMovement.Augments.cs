using System;
using Sandbox;

namespace Survival;

/// <summary>Augment-driven jump abilities (Jump Legs, Double Jump).</summary>
public sealed partial class PlayerMovement
{
	bool _doubleJumpUsed;
	bool _wasGroundedForDoubleJump = true;
	bool _pendingJumpLegsScale;
	PlayerAugments _augments;

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

		TickDoubleJumpFlightState( augments );

		var jumpPressed = !string.IsNullOrWhiteSpace( JumpInputAction ) && Input.Pressed( JumpInputAction );
		if ( !jumpPressed )
			return;

		if ( IsHitReactionActive() )
			return;

		// No mid-air hop off the rope — same rule as normal jump while grappled.
		if ( GrappleAttached )
			return;

		// Air hop: any time while airborne until used once this flight (jump-launch or walk-off).
		if ( !_controller.IsOnGround
		     && augments.HasAbility( AugmentAbility.DoubleJump )
		     && !_doubleJumpUsed )
		{
			if ( TryPerformDoubleJump() )
			{
				_doubleJumpUsed = true;
				ClearActionIfPressed( JumpInputAction );
			}
		}
	}

	/// <summary>Grounded → recharge. Airborne (from jump or cliff) → keep charge until the air hop is spent.</summary>
	void TickDoubleJumpFlightState( PlayerAugments augments )
	{
		var grounded = _controller.IsOnGround;
		if ( grounded )
		{
			_doubleJumpUsed = false;
		}
		else if ( _wasGroundedForDoubleJump && augments.HasAbility( AugmentAbility.DoubleJump ) )
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

	void OnAugmentJumped()
	{
		var augments = ResolveAugments();
		if ( augments is null || !augments.HasAbility( AugmentAbility.JumpHeight ) )
			return;

		_pendingJumpLegsScale = true;
	}

	void TickPendingJumpLegsScale()
	{
		if ( !_pendingJumpLegsScale )
			return;

		_pendingJumpLegsScale = false;
		var mult = ResolveAugments()?.GetJumpHeightMultiplier() ?? 1f;
		if ( mult <= 1.001f )
			return;

		ApplyJumpHeightMultiplier( mult );
	}

	void ApplyJumpHeightMultiplier( float multiplier )
	{
		var body = Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return;

		var up = Vector3.Up;
		var upwardSpeed = Vector3.Dot( body.Velocity, up );
		if ( upwardSpeed <= 1e-4f )
			return;

		var boosted = upwardSpeed * multiplier;
		body.Velocity += up * (boosted - upwardSpeed);
	}

	void OnAugmentLanded()
	{
		_doubleJumpUsed = false;
	}
}
