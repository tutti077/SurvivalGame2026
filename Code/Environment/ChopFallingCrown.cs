using System;
using Sandbox;

namespace Survival;

/// <summary>
/// The felled crown of a type 2 tree while it is one body: tipped at the top once its physics
/// body exists, then watched until it has leaned past <see cref="SplitTiltDegrees"/> and touched
/// something — that is the landing, and <see cref="OnLanded"/> fires once. Cosmetic and local;
/// <see cref="ChopableTree"/> creates it on the detached crown and splits the crown in the callback.
/// </summary>
[Title( "Chop Falling Crown" )]
public sealed class ChopFallingCrown : Component, Component.ICollisionListener
{
	/// <summary>Flat direction the crown is pushed toward (away from the chopper).</summary>
	public Vector3 PushDirection { get; set; } = Vector3.Forward;

	/// <summary>Speed given to the push point, in engine units per second; gravity does the rest.</summary>
	public float PushSpeedUnits { get; set; }

	/// <summary>Where the push lands, in engine units above the crown's origin.</summary>
	public float PushHeightUnits { get; set; }

	/// <summary>Lean past this (from vertical) before a touch counts as landing.</summary>
	public float SplitTiltDegrees { get; set; } = 60f;

	/// <summary>Landing is forced after this long, so a crown wedged on something still splits.</summary>
	public float MaxFallSeconds { get; set; } = 8f;

	public Action<ChopFallingCrown> OnLanded { get; set; }

	bool _launched;
	bool _landed;
	bool _touched;
	TimeUntil _deadline;

	Rigidbody Body => Components.Get<Rigidbody>();

	bool IsLeaning => WorldRotation.Up.z < MathF.Cos( SplitTiltDegrees.DegreeToRadian() );

	protected override void OnStart()
	{
		base.OnStart();
		_deadline = MaxFallSeconds;
	}

	protected override void OnFixedUpdate()
	{
		if ( _landed )
			return;

		var body = Body;
		if ( body is null || body.PhysicsBody is null )
			return;

		if ( !_launched )
		{
			_launched = true;
			body.MotionEnabled = true;
			body.Sleeping = false;
			var point = WorldPosition + Vector3.Up * PushHeightUnits;
			body.ApplyImpulseAt( point, PushDirection.WithZ( 0f ).Normal * body.Mass * PushSpeedUnits );
			return;
		}

		// Leaning over and in contact, or leaning over and come to rest, or out of time.
		if ( (IsLeaning && (_touched || body.Velocity.Length < 2f)) || _deadline )
			Land();

		_touched = false;
	}

	public void OnCollisionStart( Collision collision ) => _touched = true;

	public void OnCollisionUpdate( Collision collision ) => _touched = true;

	public void OnCollisionStop( CollisionStop _ ) { }

	void Land()
	{
		if ( _landed )
			return;

		_landed = true;
		OnLanded?.Invoke( this );
	}
}
