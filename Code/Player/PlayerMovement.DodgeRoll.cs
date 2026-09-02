using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Dodge roll: grounded modifier + Jump bursts the pawn with a short invulnerability window.
/// Block held → backward roll (toward the camera); Crouch held → forward roll (away from it);
/// A/D held swaps either modifier to a left/right roll. All directions are CAMERA-relative
/// (eye yaw), never model-facing. Motion is a held velocity (distance / window) for the roll
/// window — real collision, no root teleports. The host stamps the authoritative i-frame window
/// so <see cref="PlayerVitals.ApplyDamageAfterArmor"/> can ignore hits landed mid-roll.
/// Presentation is the fitted roll clip via <see cref="PlayerAnimation"/>.
/// </summary>
public sealed partial class PlayerMovement
{
	[Property, Group( "Dodge Roll" ), Title( "Roll input Left" )]
	public string RollLeftAction { get; set; } = "Left";

	[Property, Group( "Dodge Roll" ), Title( "Roll input Right" )]
	public string RollRightAction { get; set; } = "Right";

	[Property, Group( "Dodge Roll" ), Title( "Roll input Back" )]
	public string RollBackAction { get; set; } = "Backward";

	[Property, Group( "Dodge Roll" ), Title( "Roll distance (m)" ), Description( "Designer meters; converted via BodyHeight/1.8 inside the dash." )]
	public float RollDistanceMeters { get; set; } = 2f;

	[Property, Group( "Dodge Roll" ), Title( "Invulnerability window (s)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float RollInvulnerabilitySeconds { get; set; } = 0.2f;

	[Property, Group( "Dodge Roll" ), Title( "Roll animation window (s)" ), Range( 0.1f, 1.5f ), Step( 0.05f )]
	public float RollAnimationSeconds { get; set; } = 0.2f;

	[Property, Group( "Dodge Roll" ), Title( "Roll recovery (s)" ), Description( "Minimum time between rolls. Modifier + Jump presses inside it are eaten, not turned into hops." ), Range( 0f, 3f ), Step( 0.1f )]
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

	/// <summary>Roll motion window on the driving machine; velocity is re-asserted until it expires.</summary>
	double _rollMotionUntil;

	Vector3 _rollMotionDir;

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

		TickRollMotion();

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

		// Modifier + Space is roll input, never a jump — cancel the launch either way.
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
		// The roll replaces the guard (i-frames + reposition) — re-guarding needs a Block re-press.
		Components.Get<PlayerCombat>()?.OnDodgeRollStarted();

		Components.Get<PlayerAnimation>()?.BeginDodgeRoll( RollAnimationSeconds );

		// Owner preview; a host/offline driver's stamp IS the authoritative one.
		_rollInvulnUntil = Time.NowDouble + Math.Max( 0f, RollInvulnerabilitySeconds );
		_rollRecoveryUntil = Time.NowDouble + Math.Max( 0f, RollRecoverySeconds );

		BeginRollMotion( dir );

		// Pure clients tell the host so it stamps authoritative i-frames; motion itself is
		// owner-driven velocity and reaches the host through normal position sync.
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			RpcHostDodgeRoll();
	}

	/// <summary>
	/// The roll moves by holding a flat velocity (distance / window) for the animation window,
	/// then stopping clean. No position writes: teleporting the root fought the rigidbody /
	/// controller (snap-backs), and a velocity burst overshot by a tick-dependent slide.
	/// </summary>
	void BeginRollMotion( Vector3 flatDir )
	{
		var flat = flatDir.WithZ( 0f );
		if ( flat.LengthSquared < 1e-6f )
			return;

		_rollMotionDir = flat.Normal;
		_rollMotionUntil = Time.NowDouble + RollMotionWindowSeconds;
		TickRollMotion();
	}

	float RollMotionWindowSeconds => Math.Max( 0.05f, RollAnimationSeconds );

	/// <summary>Every PreInput frame: re-assert the roll velocity so controller friction can't decay it.</summary>
	void TickRollMotion()
	{
		if ( _rollMotionUntil <= 0 )
			return;

		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
		{
			_rollMotionUntil = 0;
			return;
		}

		if ( Time.NowDouble >= _rollMotionUntil )
		{
			_rollMotionUntil = 0;
			body.Velocity = new Vector3( 0f, 0f, body.Velocity.z );
			return;
		}

		var bodyHeight = _controller is not null && _controller.IsValid()
			? Math.Max( 24f, _controller.BodyHeight )
			: 72f;
		// Citizen BodyHeight 72 ≈ 1.8m → ~40 engine units per designer meter.
		var speed = Math.Max( 0f, RollDistanceMeters ) * (bodyHeight / 1.8f) / RollMotionWindowSeconds;
		body.Velocity = new Vector3( _rollMotionDir.x * speed, _rollMotionDir.y * speed, body.Velocity.z );
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

	/// <summary>
	/// Camera-relative roll intent. Block held → back (toward the camera); Crouch held → forward
	/// (away from it); A/D held swaps either modifier to a sideways roll, and S swaps Crouch to a
	/// back roll. No modifier → not roll input, Space stays a jump. Block wins when both modifiers
	/// are held.
	/// </summary>
	bool TryGetRollDirection( out Vector3 dir )
	{
		dir = default;

		var blockAction = Components.Get<PlayerCombat>()?.BlockAction ?? "Attack2";
		var block = !string.IsNullOrWhiteSpace( blockAction ) && Input.Down( blockAction );
		var crouch = !string.IsNullOrWhiteSpace( SneakInputAction ) && Input.Down( SneakInputAction );
		if ( !block && !crouch )
			return false;

		var left = !string.IsNullOrWhiteSpace( RollLeftAction ) && Input.Down( RollLeftAction );
		var right = !string.IsNullOrWhiteSpace( RollRightAction ) && Input.Down( RollRightAction );
		if ( left && right )
		{
			left = false;
			right = false;
		}

		// Eye yaw = what the player sees; the model may face any way (Valheim free-look).
		var basis = Rotation.FromYaw( _controller.EyeAngles.yaw );
		var forwardDir = basis.Forward.WithZ( 0f );
		var rightDir = basis.Right.WithZ( 0f );
		if ( forwardDir.LengthSquared < 1e-6f )
		{
			forwardDir = GameObject.WorldRotation.Forward.WithZ( 0f );
			rightDir = GameObject.WorldRotation.Right.WithZ( 0f );
		}

		var back = !string.IsNullOrWhiteSpace( RollBackAction ) && Input.Down( RollBackAction );

		Vector3 combined;
		if ( left )
			combined = -rightDir;
		else if ( right )
			combined = rightDir;
		else if ( back )
			combined = -forwardDir; // Crouch + S → back roll (Block already defaults to back).
		else
			combined = block ? -forwardDir : forwardDir;

		if ( combined.LengthSquared < 1e-6f )
			return false;

		dir = combined.Normal;
		return true;
	}

	/// <summary>Client roll notification: the host only stamps authoritative i-frames + the rate limit — motion is owner velocity, synced normally.</summary>
	[Rpc.Host]
	void RpcHostDodgeRoll()
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
		// (latency) — a small vertical speed still means "not jumping or falling".
		if ( !_controller.IsOnGround )
		{
			var body = _controller.Body ?? Components.Get<Rigidbody>();
			if ( body is not null && body.IsValid() && Math.Abs( body.Velocity.z ) >= RollGraceMaxVerticalSpeed )
				return;
		}

		_rollRecoveryUntil = Time.NowDouble + Math.Max( 0f, RollRecoverySeconds );
		_rollInvulnUntil = Time.NowDouble + Math.Max( 0f, RollInvulnerabilitySeconds );
	}
}
