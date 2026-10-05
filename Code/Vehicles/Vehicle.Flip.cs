using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Flipping a rolled buggy back over (Mark 2026-10-04). With fewer than two wheels on the ground the
/// prompt reads "Flip Dune Buggy" instead of "Drive", and E gives the chassis a hop plus a roll toward
/// upright — up to <see cref="FlipTurnDegrees"/> per press, so a buggy on its roof takes two presses and
/// physics finishes the job. The owner traces and sends intent; the host checks reach / state / cooldown
/// once and the simulating machine (whoever owns the body) applies the push.
/// </summary>
public sealed partial class Vehicle
{
	[Property, Group( "Flip" ), Title( "Flip Lift (m/s)" ), Description( "Upward hop one E press gives a rolled buggy." )]
	public float FlipLiftMetersPerSecond { get; set; } = 3f;

	[Property, Group( "Flip" ), Title( "Flip Turn (deg)" ), Description( "Most one press rolls it toward upright. Below 180 means a buggy on its roof needs two presses." )]
	public float FlipTurnDegrees { get; set; } = 100f;

	[Property, Group( "Flip" ), Title( "Flip Cooldown (s)" )]
	public float FlipCooldownSeconds { get; set; } = 1f;

	/// <summary>Fewer than two wheels on the ground with the chassis tipped past ~45° (a level buggy in the air — a
	/// jump, a fresh spawn — is not flipped). The simulating machine writes on change; prompts read it anywhere.</summary>
	[Sync]
	public bool IsFlipped { get; private set; }

	double _nextFlipAllowedAt;

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

	/// <summary>Host: validate once, then the body's owner applies the push.</summary>
	public bool HostTryFlip( GameObject pawn )
	{
		if ( !HasHostAuthority || IsPreviewGhost || !CanFlip( pawn ) )
			return false;
		if ( Time.NowDouble < _nextFlipAllowedAt )
			return false;

		_nextFlipAllowedAt = Time.NowDouble + Math.Max( 0f, FlipCooldownSeconds );
		RpcOwnerApplyFlip();
		return true;
	}

	[Rpc.Owner]
	void RpcOwnerApplyFlip()
	{
		if ( !IsSimulatingMachine )
			return;

		_body ??= Components.Get<Rigidbody>();
		if ( _body is null || !_body.IsValid() )
			return;

		var up = WorldRotation.Up;
		var angle = MathF.Acos( Math.Clamp( up.z, -1f, 1f ) );
		// Push it over sideways, about its own length (the narrow way, like rocking a car back over), turning
		// whichever way brings the roof down. Only a buggy standing on its nose or tail tips end-over-end.
		var forward = WorldRotation.Forward;
		Vector3 axis;
		if ( MathF.Abs( forward.z ) < 0.7f )
		{
			var sign = Vector3.Dot( Vector3.Cross( forward, up ), Vector3.Up ) >= 0f ? 1f : -1f;
			axis = forward * sign;
		}
		else
		{
			axis = Vector3.Cross( up, Vector3.Up );
			axis = axis.LengthSquared > 1e-4f ? axis.Normal : WorldRotation.Right;
		}

		var lift = TerrainWorldUnits.MetersToEngine( Math.Max( 0f, FlipLiftMetersPerSecond ) );
		var gravity = Math.Max( 1f, Scene.PhysicsWorld.Gravity.Length );
		var airtime = Math.Max( 0.2f, 2f * lift / gravity );
		var turn = Math.Min( angle, MathX.DegreeToRadian( Math.Max( 0f, FlipTurnDegrees ) ) );

		_body.Sleeping = false;
		_body.Velocity = _body.Velocity.WithZ( Math.Max( _body.Velocity.z, 0f ) + lift );
		_body.AngularVelocity = axis * ( turn / airtime );
	}
}
