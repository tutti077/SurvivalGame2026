using System;
using Sandbox;

namespace Survival;

/// <summary>
/// No standing on creatures (Mark 2026-11-05: "when i jump on a pile of them they just kinda stay there, we should push
/// them over so i fall to the ground"). While the pawn's ground is a scav or animal body (<c>enemy</c> / <c>animal</c>
/// tag, inherited by its collider children) it loses its footing and is shoved off sideways, away from the body it
/// landed on, until it drops to real ground. Large enemies that players may ride come later.
/// </summary>
public sealed partial class PlayerMovement
{
	/// <summary>Sideways speed the pawn slips off a creature's head / shoulders at (m/s).</summary>
	[Property, Group( "Movement" ), Title( "Slide off creatures speed (m/s)" ), Range( 1f, 8f ), Step( 0.25f )]
	public float CreatureSlideOffMetersPerSecond { get; set; } = 3.5f;

	/// <summary>Local driver, physics step: standing on a creature → no grounding, pushed off it.</summary>
	void TickSlideOffCreatures()
	{
		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null || !_controller.IsValid() )
			return;

		var ground = _controller.GroundObject;
		if ( ground is null || !ground.IsValid() || !(ground.Tags.Has( "enemy" ) || ground.Tags.Has( "animal" )) )
			return;

		var body = _controller.Body;
		if ( body is null || !body.IsValid() )
			return;

		// Away from the creature it stands on; dead centre → the way the camera faces.
		var away = (GameObject.WorldPosition - ground.Root.WorldPosition).WithZ( 0f );
		if ( away.LengthSquared < 4f )
			away = _controller.EyeAngles.WithPitch( 0f ).ToRotation().Forward.WithZ( 0f );
		away = away.Normal;

		var unitsPerMeter = Math.Max( 24f, _controller.BodyHeight ) / 1.8f;
		var push = away * (CreatureSlideOffMetersPerSecond * unitsPerMeter);
		var v = body.Velocity;
		// Keep any faster sideways motion the player already has that way; never hold the pawn up.
		var along = Vector3.Dot( v.WithZ( 0f ), away );
		if ( along < push.Length )
			v += away * (push.Length - along);
		body.Velocity = v.WithZ( MathF.Min( v.z, 0f ) );
		_controller.PreventGrounding( 0.1f );
	}
}
