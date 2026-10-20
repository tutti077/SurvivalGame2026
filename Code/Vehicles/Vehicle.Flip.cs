using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Righting a rolled buggy (Mark 2026-10-04; physics shove 2026-10-06 — "not physics based at all" was
/// the verdict on the old velocity write). With fewer than two wheels on the ground the prompt reads
/// "Flip Dune Buggy" and E is a shove: for <see cref="FlipPushSeconds"/> the simulating machine applies
/// a real force with <see cref="Rigidbody.ApplyForceAt"/> at the hull's top edge nearest the pusher,
/// pointed away from them and upward. Nothing writes velocities or angles: whether the chassis rocks
/// back, slides or goes over its contact edge falls out of the force, its lever arm, the mass centre and
/// the ground — so the side you push from decides which way it rolls (push from the roof side and it
/// lands on its wheels; push from the wheel side and it goes onto its roof, press again from the other
/// side). The owner traces and sends intent; the host checks reach / state / cooldown once and the
/// body's owner runs the shove.
/// </summary>
public sealed partial class Vehicle
{
	[Property, Group( "Flip" ), Title( "Flip Push (× weight)" ), Description( "Force of one E shove as a multiple of the chassis weight, applied at the hull's top edge nearest the pusher for Flip Push Time. Too low only rocks it; too high throws it." )]
	public float FlipPushWeightMultiple { get; set; } = 0.6f;

	[Property, Group( "Flip" ), Title( "Flip Push Time (s)" ), Description( "How long the shove force lasts." )]
	public float FlipPushSeconds { get; set; } = 0.3f;

	[Property, Group( "Flip" ), Title( "Flip Push Lift (0–1)" ), Description( "Share of the shove pointing up rather than away from the pusher: 0 = pure sideways push, 1 = pure lift." )]
	public float FlipPushLiftFraction { get; set; } = 0.5f;

	[Property, Group( "Flip" ), Title( "Flip Cooldown (s)" )]
	public float FlipCooldownSeconds { get; set; } = 1f;

	/// <summary>Fewer than two wheels on the ground with the chassis tipped past ~45° (a level buggy in the air — a
	/// jump, a fresh spawn — is not flipped). The simulating machine writes on change; prompts read it anywhere.</summary>
	[Sync]
	public bool IsFlipped { get; private set; }

	double _nextFlipAllowedAt;
	Vector3 _flipPushLocalPoint;
	Vector3 _flipPushDirection;
	float _flipPushRemaining;

	/// <summary>Dev readout for <c>vehicle_info</c>: where the last shove took hold.</summary>
	public string LastFlipPushDebug { get; private set; }

	void UpdateFlippedState()
	{
		var flipped = _groundedWheels < 2 && WorldRotation.Up.z < 0.7f;
		if ( flipped != IsFlipped )
			IsFlipped = flipped;
	}

	/// <summary>Can this viewer press E to flip the vehicle right now?</summary>
	public bool CanFlip( GameObject viewer )
		=> IsFlipped && !IsBroken && viewer is { IsValid: true }
		   && viewer.Components.Get<PlayerMovement>() is { IsSeated: false };

	/// <summary>Is there anything for this viewer's E on the vehicle (flip or get in)?</summary>
	public bool CanUse( GameObject viewer ) => CanFlip( viewer ) || CanEnter( viewer );

	/// <summary>Host: validate once, then the body's owner shoves from where the pawn stands.</summary>
	public bool HostTryFlip( GameObject pawn )
	{
		if ( !HasHostAuthority || IsPreviewGhost || !CanFlip( pawn ) )
			return false;
		if ( Time.NowDouble < _nextFlipAllowedAt )
			return false;

		_nextFlipAllowedAt = Time.NowDouble + Math.Max( 0f, FlipCooldownSeconds );
		RpcOwnerApplyFlip( pawn.WorldPosition );
		return true;
	}

	[Rpc.Owner]
	void RpcOwnerApplyFlip( Vector3 pusherPosition )
	{
		if ( !IsSimulatingMachine )
			return;

		_body ??= Components.Get<Rigidbody>();
		if ( _body is null || !_body.IsValid() )
			return;

		var bounds = GameObject.GetBounds();
		var centre = bounds.Center;

		// Away from the pusher, flat: the direction they heave the hull.
		var away = ( centre - pusherPosition ).WithZ( 0f );
		if ( away.LengthSquared < 1f )
			away = WorldRotation.Right.WithZ( 0f );
		if ( away.LengthSquared < 1e-4f )
			away = Vector3.Forward;
		away = away.Normal;

		// Hands on the hull itself, as high up the pusher's side as a ray at that height still meets it — the
		// longest lever the hull offers about its contact edge on the far side. The world bounds are a loose
		// box around a tilted hull, so a bounds corner can sit well off the chassis and twist it the wrong way
		// (first try, 2026-10-06). Kept in chassis space so the point rides the hull as it turns.
		var hands = FindFlipHandsOnHull( bounds, pusherPosition, centre );
		_flipPushLocalPoint = WorldTransform.PointToLocal( hands );

		var lift = Math.Clamp( FlipPushLiftFraction, 0f, 1f );
		_flipPushDirection = ( away * ( 1f - lift ) + Vector3.Up * lift ).Normal;
		_flipPushRemaining = Math.Max( 0.05f, FlipPushSeconds );
		_body.Sleeping = false;
		LastFlipPushDebug = $"at={hands} dir={_flipPushDirection} up.z={WorldRotation.Up.z:0.00}";
	}

	Vector3 FindFlipHandsOnHull( BBox bounds, Vector3 pusherPosition, Vector3 centre )
	{
		// The world bounds top is the highest wheel tip, not the cage: walk the ray down from there in
		// 12 u steps until it meets the hull, never below the pusher's chest (a real push is chest-high).
		var chest = pusherPosition.z + 40f;
		var top = bounds.Maxs.z - 6f;
		var steps = Math.Clamp( (int)MathF.Ceiling( ( top - chest ) / 12f ), 0, 10 );
		for ( var i = 0; i <= steps; i++ )
		{
			var z = i < steps ? top - i * 12f : chest;
			var from = pusherPosition.WithZ( z );
			var tr = Scene.Trace.Ray( from, centre.WithZ( z ) )
				.WithoutTags( "player", "trigger", "worlddrop" )
				.Run();
			if ( tr.Hit && tr.GameObject is { } hitGo && hitGo.Root == GameObject )
				return tr.HitPosition;
		}

		var fallback = bounds.ClosestPoint( pusherPosition.WithZ( chest ) );
		fallback.z = Math.Min( bounds.Maxs.z - 6f, Math.Max( fallback.z, chest ) );
		return fallback;
	}

	/// <summary>Simulating machine, every physics step: the shove in flight, a force like the springs and tyres.</summary>
	void SimulateFlipPush( float dt )
	{
		if ( _flipPushRemaining <= 0f )
			return;

		_flipPushRemaining -= dt;
		var gravity = Math.Max( 1f, Scene.PhysicsWorld.Gravity.Length );
		var force = Math.Max( 0f, FlipPushWeightMultiple ) * Math.Max( 1f, _body.Mass ) * gravity;
		_body.ApplyForceAt( WorldTransform.PointToWorld( _flipPushLocalPoint ), _flipPushDirection * force );
	}
}
