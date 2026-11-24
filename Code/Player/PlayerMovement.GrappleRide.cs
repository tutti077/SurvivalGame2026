using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Riding anchors (Mark 2026-10-08: "when I grapple to the platform then move the platform the grapple anchor
/// point stays floating in the air"). A surface hook that lands on something that moves — the hover platform,
/// a buggy, a piece built on the platform's deck, any loose physics prop — is stored in that body's local
/// space, like a player or tow hook, and every machine resolves it from the body's live transform. The rope
/// then rides the body and drags or carries the holder with it. Static world (terrain, trees, placed bases)
/// keeps the fixed world point it always had.
///
/// The rope on a ridden body is a tow rope (<see cref="ApplyGrappleRideRopeConstraint"/>): solved against the hook
/// point's own velocity, with tension instead of position snaps.
///
/// Decided once, on the host, at attach time: one small physics overlap at the validated point picks the
/// collider under the hook and walks up to the first enabled, simulated <see cref="Rigidbody"/>. Players and
/// tow-tagged logs keep their own attach kinds and never reach this.
/// </summary>
public sealed partial class PlayerMovement
{
	/// <summary>Host-synced: the moving body a surface hook rides (<see cref="Guid.Empty"/> = a fixed world point). Offset is <see cref="GrappleAttachLocalOffset"/>.</summary>
	[Sync( SyncFlags.FromHost )] public Guid GrappleAttachRideId { get; private set; }

	/// <summary>Probe half-size around the validated hook point when looking for the collider it sits on (units, 0.1 m).</summary>
	const float RideProbeUnits = 4f;

	/// <summary>
	/// Anchor speed at which a taut rope stops being a leash and becomes a tow: above it the holder cannot keep up on
	/// foot, so the pawn lets go of the ground and slides behind the body like a tube behind a boat.
	/// </summary>
	[Property, Group( "Grapple Vehicle Tow" ), Title( "Slide Above Speed (meters/s)" )]
	public float RideTowSlideSpeedMeters { get; set; } = 5f;

	/// <summary>Ground drag on a sliding, towed pawn (fraction of its ground speed lost per second). It keeps the rope taut
	/// and settles the pawn behind the body after a turn; lower = wider, longer whips.</summary>
	[Property, Group( "Grapple Vehicle Tow" ), Title( "Slide Drag (per second)" )]
	public float RideTowSlideDrag { get; set; } = 0.6f;

	/// <summary>How hard the rope takes stretch back up: the inward speed per unit of over-length (per second). The rope
	/// pulls, it never teleports.</summary>
	[Property, Group( "Grapple Vehicle Tow" ), Title( "Rope Stiffness (per second)" )]
	public float RideRopeStiffness { get; set; } = 12f;

	/// <summary>The pawn is being dragged behind a moving body right now (owner-side; frictionless slide, WASD leans like a swing).</summary>
	public bool IsGrappleTowSliding => _rideTowSliding;

	/// <summary>Anchor speed (units/s, 1 m/s) above which the ridden body counts as moving: no wall pin, no wall kill.</summary>
	const float RideMovingAnchorSpeed = 40f;

	Vector3 _rideAnchorPrev;
	bool _rideAnchorPrevValid;
	Vector3 _rideAnchorVelocity;
	bool _rideTowSliding;

	/// <summary>The hook rides a body that is moving right now (owner-side).</summary>
	bool IsGrappleRideAnchorMoving => GrappleAttached && GrappleAttachRideId != Guid.Empty
	                                  && _rideAnchorVelocity.Length > RideMovingAnchorSpeed;

	/// <summary>
	/// Velocity of the attach point. A body this machine simulates answers exactly (linear + spin at the point); a proxy
	/// body only has its interpolated transform, so the point is differenced step to step and smoothed.
	/// </summary>
	Vector3 SampleRideAnchorVelocity( Vector3 attach, float dt )
	{
		Vector3 measured;
		var exact = false;
		if ( TryResolveGrappleRideTarget( GrappleAttachRideId, out var target ) && !target.IsProxy
		     && target.Components.Get<Rigidbody>() is { } rb && rb.IsValid() && rb.Enabled && rb.MotionEnabled )
		{
			measured = rb.GetVelocityAtPoint( attach );
			exact = true;
		}
		else
			measured = _rideAnchorPrevValid ? (attach - _rideAnchorPrev) / dt : Vector3.Zero;

		_rideAnchorVelocity = exact || !_rideAnchorPrevValid
			? measured
			: Vector3.Lerp( _rideAnchorVelocity, measured, Math.Clamp( dt / 0.1f, 0f, 1f ) );
		_rideAnchorPrev = attach;
		_rideAnchorPrevValid = true;
		return _rideAnchorVelocity;
	}

	void ResetGrappleRideTowState()
	{
		_rideAnchorPrevValid = false;
		_rideAnchorVelocity = Vector3.Zero;
		_rideTowSliding = false;
	}

	/// <summary>
	/// Rope constraint for a hook on a moving body (Mark 2026-11-19: "their physics is still calculated as if they were
	/// attached ... that boat tube effect where he goes whipping to the side we just turned away from"). Everything is
	/// solved in the anchor's frame: the rope only removes the speed at which the pawn and the hook point separate, so
	/// the pawn inherits the body's speed along the rope and keeps its own sideways momentum - a turn slings it wide.
	/// Over-length is pulled back in as inward speed (<see cref="RideRopeStiffness"/>), never by moving the pawn; the
	/// one position write left is the safety net for a hitch that leaves the rope more than a quarter over length.
	/// A parked body gives the same swing a fixed anchor does.
	/// </summary>
	void ApplyGrappleRideRopeConstraint( Rigidbody body, float dt )
	{
		var attach = ResolveGrappleAttachWorldPoint();
		var anchorVel = SampleRideAnchorVelocity( attach, dt );
		var maxLen = Math.Max( 1f, GrappleRopeLengthEngine );
		var prevLen = _grapplePrevRopeLength > 1e-3f ? _grapplePrevRopeLength : maxLen;
		_grappleRopeShrinkRate = (prevLen - maxLen) / dt;
		_grapplePrevRopeLength = maxLen;

		var toPlayer = GameObject.WorldPosition - attach;
		var dist = toPlayer.Length;
		if ( dist < 1e-4f )
			return;

		var radial = toPlayer / dist;
		var stretch = dist - maxLen;
		var hardStretch = maxLen * 0.25f + TerrainWorldUnits.MetersToEngine( 2f );
		if ( stretch > hardStretch )
		{
			GameObject.WorldPosition = attach + radial * (maxLen + hardStretch * 0.5f);
			Transform.ClearInterpolation();
			stretch = hardStretch * 0.5f;
		}

		var wasTaut = _grappleRopeTaut;
		_grappleRopeTaut = stretch >= -Math.Max( 2f, maxLen * 0.02f );
		if ( _grappleRopeTaut )
		{
			var vel = body.Velocity;
			var rel = vel - anchorVel;
			var vRad = Vector3.Dot( rel, radial );
			// The separation speed the rope allows: none at length, inward while stretched.
			var allowed = stretch > 0f ? -Math.Min( stretch * Math.Max( 0f, RideRopeStiffness ), stretch / dt ) : 0f;
			if ( vRad > allowed )
			{
				var incoming = vel.Length;
				rel = allowed < 0f
					? rel - radial * (vRad - allowed)
					: StripOutwardRadial( rel, radial, vRad, maxLen, dt );
				body.Velocity = anchorVel + rel;
				if ( body.Sleeping )
					body.Sleeping = false;
				if ( !wasTaut )
					RecordGrappleRopeCatch( incoming, body.Velocity.Length );
			}

			// Winch: the ice-skater rule works on the speed around the anchor, so do it in the anchor's frame.
			if ( MathF.Abs( prevLen - maxLen ) >= 1e-3f )
			{
				body.Velocity -= anchorVel;
				ApplyWinchAngularMomentum( body, radial, prevLen, maxLen );
				body.Velocity += anchorVel;
			}

			_grappleFirstCatchDone = true;
		}

		TickGrappleTowSlide( body, anchorVel, dt );

		// A wall overlap pins a swing on a parked body; behind a moving one the rope wins.
		if ( anchorVel.Length > RideMovingAnchorSpeed )
			_grappleBlockedByWall = false;
		else
			TickGrappleBodyBlock();

		ApplyGrappleSwingPushAfterWalk( dt );

		if ( _grappleBlockedByWall )
			body.Velocity = Vector3.Zero;

		WriteGrappleSwingLogRow( dist, maxLen, radial, body.Velocity );
	}

	/// <summary>
	/// Tow slide. Feet on the ground brake the pawn to a stop every step, which is what left a towed pawn "stuck" beside
	/// a turning vehicle. While a taut rope drags the pawn faster than <see cref="RideTowSlideSpeedMeters"/> it lets go
	/// of the ground (<see cref="PlayerController.PreventGrounding"/>): the controller then treats it like a pawn on a
	/// swing - no walk, no brake, WASD leans against the rope - and it slides on the terrain with only
	/// <see cref="RideTowSlideDrag"/>. The slide lasts until the pawn itself is slow again, so a slack moment in the
	/// middle of a whip does not plant its feet.
	/// </summary>
	void TickGrappleTowSlide( Rigidbody body, Vector3 anchorVel, float dt )
	{
		var slideSpeed = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, RideTowSlideSpeedMeters ) );
		var flat = body.Velocity.WithZ( 0f ).Length;
		_rideTowSliding = _rideTowSliding
			? flat > slideSpeed * 0.6f
			: _grappleRopeTaut && anchorVel.WithZ( 0f ).Length > slideSpeed && flat > slideSpeed * 0.6f;
		if ( !_rideTowSliding )
			return;

		_controller ??= Components.Get<PlayerController>();
		_controller?.PreventGrounding( 0.1f );

		var drag = Math.Max( 0f, RideTowSlideDrag );
		if ( drag > 0f )
		{
			var keep = Math.Max( 0f, 1f - drag * dt );
			var v = body.Velocity;
			body.Velocity = new Vector3( v.x * keep, v.y * keep, v.z );
		}
	}

	/// <summary>Host, once at attach: if the hook point sits on a simulated rigidbody, ride it.</summary>
	void HostTryBindGrappleRide( Vector3 validatedPoint )
	{
		var carrier = FindGrappleRideCarrier( validatedPoint );
		if ( carrier is null )
			return;

		GrappleAttachRideId = carrier.Id;
		GrappleAttachLocalOffset = carrier.WorldTransform.PointToLocal( validatedPoint );
		if ( LogGrapple )
			Log.Info( $"[PlayerMovement.Grapple] {GameObject.Name}: hook rides '{carrier.Name}'." );
	}

	GameObject FindGrappleRideCarrier( Vector3 point )
	{
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return null;

		GameObject best = null;
		var bestDist = RideProbeUnits * 1.5f;
		foreach ( var go in scene.FindInPhysics( BBox.FromPositionAndSize( point, RideProbeUnits * 2f ) ) )
		{
			if ( go is null || !go.IsValid() || go.Root == GameObject.Root )
				continue;
			if ( TryGetGrapplePlayerRoot( go, out _ ) )
				continue;

			var carrier = FindSimulatedBodyOnHierarchy( go );
			if ( carrier is null || ObjectHasTowTag( carrier ) )
				continue;

			foreach ( var col in go.Components.GetAll<Collider>( FindMode.EverythingInSelf ) )
			{
				if ( col is null || !col.IsValid() || !col.Enabled || col.IsTrigger )
					continue;

				var d = Vector3.DistanceBetween( col.FindClosestPoint( point ), point );
				if ( d >= bestDist )
					continue;

				bestDist = d;
				best = carrier;
			}
		}

		return best;
	}

	/// <summary>The object that moves this one: the first ancestor (or itself) with an enabled, simulated rigidbody.</summary>
	static GameObject FindSimulatedBodyOnHierarchy( GameObject go )
	{
		for ( var cur = go; cur is not null && cur.IsValid(); cur = cur.Parent )
		{
			var body = cur.Components.Get<Rigidbody>();
			if ( body is not null && body.Enabled && body.MotionEnabled )
				return cur;
		}

		return null;
	}

	bool TryResolveGrappleRideTarget( Guid id, out GameObject target )
	{
		target = null;
		if ( id == Guid.Empty )
			return false;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		target = scene.Directory.FindByGuid( id );
		return target.IsValid();
	}

	/// <summary>Host tick: the ridden body was destroyed (wrecked platform) or flew impossibly far — drop the rope.</summary>
	void TickGrappleRideValidity()
	{
		if ( !GrappleAttached || GrappleAttachRideId == Guid.Empty )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( !TryResolveGrappleRideTarget( GrappleAttachRideId, out var target ) )
		{
			ServerDetach( "ridden body lost" );
			return;
		}

		if ( Vector3.DistanceBetween( GameObject.WorldPosition, target.WorldTransform.PointToWorld( GrappleAttachLocalOffset ) ) > GetMaxRangeEngine() * 1.25f )
			ServerDetach( "ridden body out of range" );
	}

	void ClearGrappleRideAttachState()
	{
		GrappleAttachRideId = Guid.Empty;
	}

	/// <summary>
	/// <c>grapple_test</c> only: lend this pawn a grapple and run the host attach a client's <c>RpcRequestAttach</c> runs
	/// at a point on a surface. True = the hook landed and rides <paramref name="body"/>.
	/// </summary>
	internal bool TestHostHookRide( GameObject body, Vector3 point )
	{
		if ( !body.IsValid() )
			return false;

		_grappleWasEquippedForControlPrompt = true;
		HostSetEventGrappleLoan( true );
		ServerTryAttach( point );
		return GrappleAttached && GrappleAttachRideId == body.Id;
	}

	/// <summary><c>grapple_test</c> only: drop the rope through the host detach.</summary>
	internal void TestHostDetach() => ServerDetach( "test" );

	/// <summary>
	/// Usage: <c>grapple_tow</c> hooks the local pawn to the nearest vehicle through the host attach (lending a grapple
	/// when the slot is empty; the loan lasts until play stops), <c>grapple_tow off</c> lets go. The editor cannot aim or press keys, so this is how a
	/// tow behind <c>vehicle_drive</c> gets filmed. Host / offline only.
	/// </summary>
	[ConCmd( "grapple_tow" )]
	public static void ConCmdGrappleTow( string mode = "on" )
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Grapple] grapple_tow: host only." );
			return;
		}

		var scene = Sandbox.Game.ActiveScene;
		PlayerMovement pawn = null;
		if ( scene.IsValid() )
		{
			foreach ( var m in scene.GetAllComponents<PlayerMovement>() )
			{
				if ( m.IsValid() && m.IsLocalMovementDriver() && !m.GameObject.Tags.Has( "test_pawn" ) )
				{
					pawn = m;
					break;
				}
			}
		}

		if ( pawn is null )
		{
			Log.Warning( "[Grapple] grapple_tow: no local player pawn." );
			return;
		}

		if ( string.Equals( mode, "off", StringComparison.OrdinalIgnoreCase ) )
		{
			pawn.ServerDetach( "dev" );
			Log.Info( "[Grapple] grapple_tow: released." );
			return;
		}

		Vehicle nearest = null;
		var best = float.MaxValue;
		foreach ( var v in scene.GetAllComponents<Vehicle>() )
		{
			var d = Vector3.DistanceBetween( v.WorldPosition, pawn.WorldPosition );
			if ( d < best )
			{
				best = d;
				nearest = v;
			}
		}

		if ( nearest is null )
		{
			Log.Warning( "[Grapple] grapple_tow: no vehicle in the scene (vehicle_spawn first)." );
			return;
		}

		if ( !pawn.HasGrappleEquipped() )
			pawn.HostSetEventGrappleLoan( true );

		pawn._grappleWasEquippedForControlPrompt = true;
		if ( pawn.GrappleAttached )
			pawn.ServerDetach( "dev" );
		var aim = pawn.ResolveEyePosition();
		var point = pawn.TryResolveClosestPointOnObject( nearest.GameObject, aim, out var closest ) ? closest : nearest.WorldPosition;
		pawn.ServerTryAttach( point );
		Log.Info( pawn.GrappleAttached
			? $"[Grapple] grapple_tow: hooked {nearest.GameObject.Name}, rope {TerrainWorldUnits.EngineToMeters( pawn.GrappleRopeLengthEngine ):0.#} m, rides {pawn.GrappleAttachRideId != Guid.Empty}."
			: $"[Grapple] grapple_tow: the host refused the hook on {nearest.GameObject.Name} ({TerrainWorldUnits.EngineToMeters( best ):0.#} m away)." );
	}
}
