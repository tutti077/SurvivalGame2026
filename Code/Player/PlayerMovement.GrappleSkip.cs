using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Grapple landing skip (Mark 2026-11-16: "the players ability to travel horizontally along the ground after we grapple
/// ... I want it so players kinda skip along the ground"). A pawn that lets go of the rope in the air and touches down
/// fast keeps going: each touchdown above <see cref="GrappleSkipMinSpeedMeters"/> bounces it back up a little and keeps
/// <see cref="GrappleSkipKeep"/> of its ground speed, like a stone skipping on water, until it is slow enough to just run.
/// Before this, <see cref="OnLanded"/> capped the speed to the run speed on the first touchdown (the roof-jump guard),
/// which is what killed the momentum. Owner-side, like all pawn motion.
/// </summary>
partial class PlayerMovement
{
	/// <summary>Ground speed a post-grapple touchdown needs to skip; slower lands normally.</summary>
	[Property, Group( "Grapple Landing Skip" ), Title( "Min Speed (m/s)" ), Range( 0f, 40f ), Step( 0.5f )]
	public float GrappleSkipMinSpeedMeters { get; set; } = 8f;

	/// <summary>Share of the ground speed kept on every skip (the rest is lost to the ground).</summary>
	[Property, Group( "Grapple Landing Skip" ), Title( "Speed Kept Per Skip (0-1)" ), Range( 0.5f, 1f ), Step( 0.01f )]
	public float GrappleSkipKeep { get; set; } = 0.85f;

	/// <summary>Share of the touchdown fall speed that comes back as the skip's upward speed.</summary>
	[Property, Group( "Grapple Landing Skip" ), Title( "Bounce (x fall speed)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float GrappleSkipBounce { get; set; } = 0.35f;

	/// <summary>Smallest skip hop, so a shallow touchdown still leaves the ground.</summary>
	[Property, Group( "Grapple Landing Skip" ), Title( "Min Hop (m/s up)" ), Range( 0f, 10f ), Step( 0.1f )]
	public float GrappleSkipMinHopMeters { get; set; } = 1.5f;

	/// <summary>Largest skip hop, so a hard touchdown does not bounce sky-high.</summary>
	[Property, Group( "Grapple Landing Skip" ), Title( "Max Hop (m/s up)" ), Range( 0f, 20f ), Step( 0.1f )]
	public float GrappleSkipMaxHopMeters { get; set; } = 5f;

	/// <summary>Owner: let go of the rope in the air; the next fast touchdowns skip.</summary>
	bool _grappleSkipArmed;

	/// <summary>Owner, tests / debug: skips since the last rope release.</summary>
	internal int GrappleSkipCount { get; private set; }

	/// <summary><c>grapple_test</c>: the next fast touchdowns will skip.</summary>
	internal bool IsGrappleSkipArmed => _grappleSkipArmed;

	/// <summary><c>grapple_test</c>: the ground speed a normal landing is scrubbed down to.</summary>
	internal float DesignRunSpeedForTests => DesignGroundMaxSpeed();

	/// <summary><c>grapple_test</c> only: arm (or disarm) the skip as a rope released in the air would.</summary>
	internal void TestSetGrappleSkipArmed( bool armed )
	{
		GrappleSkipCount = 0;
		_grappleSkipArmed = armed;
	}

	/// <summary>Owner: the rope attached or came off — arm the skip only for a release in the air.</summary>
	void ArmGrappleSkipOnRopeEdge( bool attached )
	{
		GrappleSkipCount = 0;
		_controller ??= Components.Get<PlayerController>();
		_grappleSkipArmed = !attached && _controller is not null && _controller.IsValid() && !_controller.IsOnGround;
	}

	/// <summary>
	/// Owner, from <see cref="OnLanded"/>: skip instead of landing when armed and fast enough. True = skipped (the
	/// caller leaves the speed alone); false = a normal landing, and the skip is over.
	/// </summary>
	bool TryGrappleLandingSkip( Vector3 impactVelocity )
	{
		if ( !_grappleSkipArmed )
			return false;

		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		if ( WingsuitDeployed || GrappleAttached || TrapLocked || IsSeated || IsLedgePulling
		     || _controller is null || body is null || !body.IsValid() )
		{
			_grappleSkipArmed = false;
			return false;
		}

		var unitsPerMeter = Math.Max( 1f, _controller.BodyHeight ) / 1.8f;
		var v = body.Velocity;
		var flat = new Vector3( v.x, v.y, 0f );
		if ( flat.Length < GrappleSkipMinSpeedMeters * unitsPerMeter )
		{
			_grappleSkipArmed = false;
			return false;
		}

		var fall = Math.Max( Math.Abs( impactVelocity.z ), Math.Max( 0f, -v.z ) );
		var hop = Math.Clamp( fall * GrappleSkipBounce,
			GrappleSkipMinHopMeters * unitsPerMeter,
			Math.Max( GrappleSkipMinHopMeters, GrappleSkipMaxHopMeters ) * unitsPerMeter );

		_controller.PreventGrounding( 0.1f );
		body.Velocity = flat * Math.Clamp( GrappleSkipKeep, 0f, 1f ) + Vector3.Up * hop;
		GrappleSkipCount++;
		return true;
	}
}
