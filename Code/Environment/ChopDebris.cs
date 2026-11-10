using Sandbox;

namespace Survival;

/// <summary>
/// A burst tree piece or rock debris in flight: cosmetic, local to every peer, never networked or saved.
/// <see cref="ChopableTree"/> (crown pieces) and <see cref="MineableRock"/> (rock pieces, stone bits) set it up;
/// the launch waits for the physics body (it exists only after the first fixed step), then the piece is destroyed
/// once <see cref="LifetimeSeconds"/> has passed - <see cref="OnExpired"/> gets where it ended up (the host drops
/// a rock's stone there).
/// </summary>
[Title( "Chop Debris" )]
public sealed class ChopDebris : Component
{
	public Vector3 Velocity { get; set; }
	public Vector3 AngularVelocity { get; set; }
	public float LifetimeSeconds { get; set; } = 2.5f;
	public System.Action<Vector3> OnExpired { get; set; }

	bool _launched;
	TimeUntil _expires;

	protected override void OnStart()
	{
		base.OnStart();
		_expires = LifetimeSeconds;
	}

	protected override void OnFixedUpdate()
	{
		if ( !_launched )
		{
			var body = Components.Get<Rigidbody>();
			if ( body is null || body.PhysicsBody is null )
				return;

			body.MotionEnabled = true;
			body.Sleeping = false;
			body.Velocity = Velocity;
			body.AngularVelocity = AngularVelocity;
			_launched = true;
		}

		if ( !_expires )
			return;
		var expired = OnExpired;
		OnExpired = null;
		expired?.Invoke( WorldPosition );
		GameObject.Destroy();
	}
}
