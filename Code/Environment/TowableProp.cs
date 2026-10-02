using System;
using Sandbox;

namespace Survival;

/// <summary>
/// A heavy physics prop (felled log, log half) that is meant to be moved with the grapple, not by
/// walking into it. While nothing is towing it and it is resting on the ground, the host bleeds
/// off horizontal and spin velocity fast, so a body shove barely moves it (Mark: pushing with the
/// body was "the easiest and fastest method"). The tow tick keeps <see cref="TowedUntil"/> fresh
/// so the damping stays out of the way while a rope is pulling, and a log that is not lying flat
/// (standing on the stump, tipping) or not resting (falling, bouncing) is never touched - a fixed
/// grace timer grabbed the slow tip-over halfway down (Mark).
/// </summary>
public sealed class TowableProp : Component
{
	[Property, Group( "Push" ), Title( "Idle Horizontal Damping (1/s)" ), Range( 0f, 20f ), Step( 0.5f )]
	public float IdleHorizontalDamping { get; set; } = 15f;

	[Property, Group( "Push" ), Title( "Idle Spin Damping (1/s)" ), Range( 0f, 20f ), Step( 0.5f )]
	public float IdleSpinDamping { get; set; } = 8f;

	/// <summary>Vertical speed below which the prop counts as resting (u/s); a bouncing or falling log is left alone.</summary>
	[Property, Group( "Push" ), Title( "Resting Vertical Speed (u/s)" ), Range( 1f, 100f ), Step( 1f )]
	public float RestingVerticalSpeed { get; set; } = 20f;

	/// <summary>The prop's long axis (local up) must be within this of horizontal to count as lying down (0..1 = |axis . up|).</summary>
	[Property, Group( "Push" ), Title( "Lying Flat Tolerance" ), Range( 0.05f, 0.9f ), Step( 0.05f )]
	public float LyingFlatTolerance { get; set; } = 0.35f;

	/// <summary>Sandbox time until which a tow is driving this prop (set each fixed step by the tow tick).</summary>
	public double TowedUntil { get; set; }

	protected override void OnFixedUpdate()
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( Time.NowDouble < TowedUntil )
			return;

		var body = Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return;

		// Only a log lying flat and not falling / bouncing: a standing or tipping log is left alone.
		if ( MathF.Abs( Vector3.Dot( WorldRotation.Up, Vector3.Up ) ) > LyingFlatTolerance )
			return;

		var v = body.Velocity;
		if ( MathF.Abs( v.z ) > RestingVerticalSpeed )
			return;

		var dt = Time.Delta;
		var h = v.WithZ( 0f );
		if ( h.LengthSquared > 1e-4f && IdleHorizontalDamping > 0f )
			body.Velocity = h * MathF.Exp( -IdleHorizontalDamping * dt ) + Vector3.Up * v.z;

		if ( IdleSpinDamping > 0f )
			body.AngularVelocity *= MathF.Exp( -IdleSpinDamping * dt );
	}
}
