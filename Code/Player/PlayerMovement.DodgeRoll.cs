using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Dodge roll: grounded A / D / S + Jump bursts the pawn sideways/back with a short
/// invulnerability window (W held = normal jump, so forward rolls don't exist). Motion reuses the
/// collision-clamped flat dash; the host stamps the authoritative i-frame window so
/// <see cref="PlayerVitals.ApplyDamageAfterArmor"/> can ignore hits landed mid-roll.
/// Presentation is the citizen animgraph roll state via <see cref="PlayerAnimation"/>.
/// </summary>
public sealed partial class PlayerMovement
{
	[Property, Group( "Dodge Roll" ), Title( "Roll input Left" )]
	public string RollLeftAction { get; set; } = "Left";

	[Property, Group( "Dodge Roll" ), Title( "Roll input Right" )]
	public string RollRightAction { get; set; } = "Right";

	[Property, Group( "Dodge Roll" ), Title( "Roll input Back" )]
	public string RollBackAction { get; set; } = "Backward";

	[Property, Group( "Dodge Roll" ), Title( "Forward (blocks roll)" )]
	public string RollBlockForwardAction { get; set; } = "Forward";

	[Property, Group( "Dodge Roll" ), Title( "Roll distance (m)" ), Description( "Designer meters; converted via BodyHeight/1.8 inside the dash." )]
	public float RollDistanceMeters { get; set; } = 2f;

	[Property, Group( "Dodge Roll" ), Title( "Invulnerability window (s)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float RollInvulnerabilitySeconds { get; set; } = 0.2f;

	[Property, Group( "Dodge Roll" ), Title( "Roll animation window (s)" ), Range( 0.1f, 1.5f ), Step( 0.05f )]
	public float RollAnimationSeconds { get; set; } = 0.2f;

	[Property, Group( "Dodge Roll" ), Title( "Roll recovery (s)" ), Description( "Minimum time between rolls. Direction + Jump presses inside it are eaten, not turned into hops." ), Range( 0f, 3f ), Step( 0.1f )]
	public float RollRecoverySeconds { get; set; } = 0.5f;

	/// <summary>The dash burst can flicker IsOnGround off for a frame or two — this keeps spam presses from being eaten.</summary>
	const float RollGroundedGraceSeconds = 0.15f;

	/// <summary>Vertical speeds above this are a real jump or fall, not a ground flicker — no air rolls.</summary>
	const float RollGraceMaxVerticalSpeed = 60f;

	/// <summary>Deadline on this machine's clock; only the authority's stamp matters for damage.</summary>
	double _rollInvulnUntil;

	/// <summary>Per-machine: owner uses it to pace input, the host re-checks it as the roll rate limit.</summary>
	double _rollRecoveryUntil;

	double _lastGroundedForRollAt;

	/// <summary>Checked by <see cref="PlayerVitals.ApplyDamageAfterArmor"/> on the authority.</summary>
	public bool IsDodgeRollInvulnerable => Time.NowDouble < _rollInvulnUntil;

	/// <summary>Owner input gate — must see Jump before stamina / grapple / wingsuit clear it.</summary>
	void TickDodgeRollGate()
	{
		if ( !IsLocalMovementDriver() )
			return;

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null )
			return;

		if ( _controller.IsOnGround )
			_lastGroundedForRollAt = Time.NowDouble;

		if ( string.IsNullOrWhiteSpace( JumpInputAction ) || !Input.Pressed( JumpInputAction ) )
			return;

		if ( IsHitReactionActive() || GrappleAttached )
			return;

		if ( !TryGetRollDirection( out var dir ) )
			return;

		// Recovery: eat the press instead of letting it fall through to a hop — a hop would leave
		// the pawn airborne when recovery ends and delay the next roll even further.
		if ( Time.NowDouble < _rollRecoveryUntil )
		{
			ClearActionIfPressed( JumpInputAction );
			return;
		}

		if ( !_controller.IsOnGround && !WasGroundedWithinRollGrace() )
			return;

		ClearActionIfPressed( JumpInputAction );
		StartDodgeRoll( dir );
	}

	/// <summary>
	/// Fixed-tick input race: <c>PlayerController.InputJump</c> (OnFixedUpdate) can consume a Space
	/// press before the frame's PreInput gate ever sees it — the pawn hops instead of rolling.
	/// Called first from <see cref="OnJumped"/>: when roll direction is held, the hop is cancelled
	/// and turned into the roll the input asked for (or just eaten during recovery).
	/// Returns true when normal jump handling must be skipped.
	/// </summary>
	bool TryConvertJumpIntoDodgeRoll()
	{
		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null )
			return false;

		if ( IsHitReactionActive() || GrappleAttached )
			return false;

		if ( !TryGetRollDirection( out var dir ) )
			return false;

		// Direction + Space is roll input, never a jump — cancel the launch either way.
		var body = _controller.Body ?? Components.Get<Rigidbody>();
		if ( body is not null && body.IsValid() && body.Velocity.z > 0f )
			body.Velocity = body.Velocity.WithZ( 0f );

		if ( Time.NowDouble < _rollRecoveryUntil )
			return true; // eaten, same as the PreInput path

		StartDodgeRoll( dir );
		return true;
	}

	void StartDodgeRoll( Vector3 dir )
	{
		Components.Get<PlayerAnimation>()?.BeginDodgeRoll( RollAnimationSeconds );

		// Owner preview; on a networked client the host stamps its own window in the Rpc.
		_rollInvulnUntil = Time.NowDouble + Math.Max( 0f, RollInvulnerabilitySeconds );

		PredictFlatDashMeters( dir, RollDistanceMeters );
		if ( GameObject.Network is { Active: true } )
			RpcHostDodgeRoll( dir );
		else
			ApplyFlatDashMeters( dir, RollDistanceMeters );

		// Stamped after the Rpc so a host-owned pawn's inline host check doesn't reject its own roll.
		_rollRecoveryUntil = Time.NowDouble + Math.Max( 0f, RollRecoverySeconds );
	}

	/// <summary>Grounded a blink ago and not launching or falling — treat as grounded for the roll.</summary>
	bool WasGroundedWithinRollGrace()
	{
		if ( Time.NowDouble - _lastGroundedForRollAt > RollGroundedGraceSeconds )
			return false;

		var body = _controller.Body ?? Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return true;

		return Math.Abs( body.Velocity.z ) < RollGraceMaxVerticalSpeed;
	}

	/// <summary>A/D → sideways, S → back (diagonals combine); W held blocks. Camera yaw so keys match what the player sees.</summary>
	bool TryGetRollDirection( out Vector3 dir )
	{
		dir = default;

		if ( !string.IsNullOrWhiteSpace( RollBlockForwardAction ) && Input.Down( RollBlockForwardAction ) )
			return false;

		var left = !string.IsNullOrWhiteSpace( RollLeftAction ) && Input.Down( RollLeftAction );
		var right = !string.IsNullOrWhiteSpace( RollRightAction ) && Input.Down( RollRightAction );
		var back = !string.IsNullOrWhiteSpace( RollBackAction ) && Input.Down( RollBackAction );
		if ( left && right )
		{
			left = false;
			right = false;
		}

		if ( !left && !right && !back )
			return false;

		var basis = Rotation.FromYaw( _controller.EyeAngles.yaw );
		var rightDir = basis.Right.WithZ( 0f );
		var backDir = -basis.Forward.WithZ( 0f );
		if ( rightDir.LengthSquared < 1e-6f )
		{
			rightDir = GameObject.WorldRotation.Right.WithZ( 0f );
			backDir = -GameObject.WorldRotation.Forward.WithZ( 0f );
		}

		var combined = Vector3.Zero;
		if ( left )
			combined -= rightDir;
		if ( right )
			combined += rightDir;
		if ( back )
			combined += backDir;

		if ( combined.LengthSquared < 1e-6f )
			return false;

		dir = combined.Normal;
		return true;
	}

	[Rpc.Host]
	void RpcHostDodgeRoll( Vector3 flatDir )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		// Rate limit: one roll per recovery window, whatever a modified client sends.
		if ( Time.NowDouble < _rollRecoveryUntil )
			return;

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null )
			return;

		// Lenient grounded check: the host's view of a remote pawn can flicker IsOnGround off
		// (dash burst, latency) — a small vertical speed still means "not jumping or falling".
		if ( !_controller.IsOnGround )
		{
			var body = _controller.Body ?? Components.Get<Rigidbody>();
			if ( body is not null && body.IsValid() && Math.Abs( body.Velocity.z ) >= RollGraceMaxVerticalSpeed )
				return;
		}

		// Host tuning is authoritative — the client only sends a direction, never a distance.
		_rollRecoveryUntil = Time.NowDouble + Math.Max( 0f, RollRecoverySeconds );
		_rollInvulnUntil = Time.NowDouble + Math.Max( 0f, RollInvulnerabilitySeconds );
		ApplyFlatDashMeters( flatDir, Math.Max( 0f, RollDistanceMeters ) );
	}
}
