using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Towing: hook a "tow"-tagged physics object (felled logs and log halves) and drag it by walking.
/// Third attach kind beside surface and player: the anchor rides the object (id + local offset),
/// the holder keeps normal ground locomotion (no swing), and the rope only ever pulls the OBJECT.
/// It is meant to feel heavy (Mark): the log creeps up to a low top speed, the holder is slowed
/// while the rope is taut and is held back when the log cannot keep up; sprinting tows faster
/// (and costs the usual sprint stamina). A tip hook
/// steers the log into line behind the pull; a middle hook pulled broadside rolls it. Winching (E)
/// reels the HOLDER to the log, stopped by whatever is in the way; the log never comes to them.
/// </summary>
partial class PlayerMovement
{
	/// <summary>Prefab tag on objects that can be towed (the felled log + halves, written by the elm generator).</summary>
	public const string GrappleTowTag = "tow";

	[Property, Group( "Grapple Tow" ), Title( "Walk Tow Speed (m/s)" ), Range( 0.2f, 5f ), Step( 0.1f )]
	public float TowWalkSpeedMeters { get; set; } = 2.6f;

	[Property, Group( "Grapple Tow" ), Title( "Sprint Tow Speed (m/s)" ), Range( 0.2f, 6f ), Step( 0.1f )]
	public float TowSprintSpeedMeters { get; set; } = 4.0f;

	/// <summary>How fast the log picks up speed — low values are what make it read as heavy.</summary>
	[Property, Group( "Grapple Tow" ), Title( "Tow Acceleration (m/s²)" ), Range( 0.2f, 10f ), Step( 0.1f )]
	public float TowAccelerationMeters { get; set; } = 6f;

	[Property, Group( "Grapple Tow" ), Title( "Holder Speed Scale (taut)" ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float TowHolderSpeedScale { get; set; } = 0.8f;

	/// <summary>Mass at which the tow numbers above apply as written; lighter loads tow proportionally faster (a 1500 kg half at 2x), heavier slower.</summary>
	[Property, Group( "Grapple Tow" ), Title( "Reference Mass (kg)" ), Range( 100f, 5000f ), Step( 50f )]
	public float TowReferenceMassKg { get; set; } = 3000f;

	/// <summary>Extra ease when the pull is broadside to the log and it rolls instead of sliding (1 = rolling tows at double speed).</summary>
	[Property, Group( "Grapple Tow" ), Title( "Roll Ease" ), Range( 0f, 3f ), Step( 0.1f )]
	public float TowRollEase { get; set; } = 1f;

	/// <summary>How fast a log hooked by an end swings into line behind the pull (rad/s at full misalignment); 0 = off.</summary>
	[Property, Group( "Grapple Tow" ), Title( "Align Yaw Rate (rad/s)" ), Range( 0f, 4f ), Step( 0.1f )]
	public float TowAlignYawRate { get; set; } = 1.2f;

	/// <summary>Winch (E) speed at which the holder is pulled toward the log along the rope.</summary>
	[Property, Group( "Grapple Tow" ), Title( "Winch Pull Speed (m/s)" ), Range( 0.5f, 8f ), Step( 0.1f )]
	public float TowWinchPullSpeedMeters { get; set; } = 3f;

	/// <summary>Rope stretch the holder may add before being held back by the log.</summary>
	[Property, Group( "Grapple Tow" ), Title( "Holder Slack (m)" ), Range( 0f, 3f ), Step( 0.1f )]
	public float TowHolderSlackMeters { get; set; } = 1.5f;

	/// <summary>Host-synced towed object when <see cref="GrappleAttached"/> targets a tow-tagged body; empty = not towing.</summary>
	[Sync( SyncFlags.FromHost )] public Guid GrappleAttachTowId { get; private set; }

	/// <summary>Rope is hooked to a towable physics object — no swing, the holder walks and pulls.</summary>
	public bool IsTowGrappleAttach => GrappleAttached && GrappleAttachTowId != Guid.Empty;

	/// <summary>Owner / host readout: the tow rope is stretched and pulling this frame.</summary>
	public bool IsTowRopeTaut { get; private set; }

	/// <summary>Owner-authored: the holder is winching (E) on a tow rope — the rope pulls the HOLDER to the log, so the host leaves the log alone.</summary>
	[Sync] public bool TowWinchHeld { get; private set; }

	/// <summary>Any attach whose anchor moves with its target and must never run the pendulum swing.</summary>
	bool IsMovingTargetAttach => IsPlayerGrappleAttach || IsTowGrappleAttach;

	float _towSpeedMeters;
	/// <summary>Last computed tow ease (mass x roll); the holder slowdown uses it too.</summary>
	float _towEase = 1f;

	/// <summary>
	/// How easy this pull is: lighter than the reference mass tows faster, and a broadside pull lets
	/// the log ROLL (0 = pull along the log, slides; 1 = pull square across it, rolls), which is
	/// easier than dragging it end-on. Returns (ease, perp).
	/// </summary>
	(float ease, float roll, float endness) ComputeTowEase( GameObject target, Rigidbody body, Vector3 pullFlat )
	{
		var massScale = Math.Clamp( TowReferenceMassKg / Math.Max( 1f, body.Mass ), 0.5f, 3f );
		var axis = target.WorldRotation.Up.WithZ( 0f );
		var perp = axis.Length > 0.1f ? 1f - MathF.Abs( Vector3.Dot( axis.Normal, pullFlat ) ) : 0f;
		// How far out along the log the rope is tied: 0 at the middle, 1 at a tip. A tip hook steers
		// (the log swings into line), a middle hook broadside rolls.
		var ( anchor, centre, _ ) = ResolveTowAnchor( target );
		var halfLen = Math.Max( 1f, ( target.Components.Get<ModelRenderer>()?.Model?.Bounds.Size.z ?? 2f ) * 0.5f * target.WorldScale.z );
		var endness = Math.Clamp( MathF.Abs( Vector3.Dot( anchor - centre, target.WorldRotation.Up ) ) / halfLen, 0f, 1f );
		var roll = Math.Clamp( perp, 0f, 1f ) * ( 1f - endness );
		return ( massScale * ( 1f + Math.Max( 0f, TowRollEase ) * roll ), roll, endness );
	}

	static bool ObjectHasTowTag( GameObject go ) => go.IsValid() && go.Tags.Has( GrappleTowTag );

	/// <summary>
	/// The rope is tied AROUND the log at the hook's station, like a choker chain: the physics
	/// anchor is the point on the log's long axis (local Z through the bounds centre) nearest the
	/// hooked surface point. A surface anchor went round underneath as the log rolled and the rope
	/// fought the roll (Mark). Returns the axis point, the axis centre and the log's radius there.
	/// </summary>
	(Vector3 anchor, Vector3 centre, float radius) ResolveTowAnchor( GameObject target )
	{
		var surface = target.WorldTransform.PointToWorld( GrappleAttachLocalOffset );
		var centre = target.GetBounds().Center;
		var axis = target.WorldRotation.Up;
		var anchor = centre + axis * Vector3.Dot( surface - centre, axis );
		return ( anchor, centre, Vector3.DistanceBetween( surface, anchor ) );
	}

	/// <summary>Where the drawn rope ends: on the log's surface, on the holder's side, at the hook's station.</summary>
	Vector3 ResolveTowRopeEnd( GameObject target )
	{
		var ( anchor, _, radius ) = ResolveTowAnchor( target );
		var toHand = ( ResolveLeftArmWorldPoint() - anchor ).WithZ( 0f );
		return toHand.Length > 1e-3f ? anchor + toHand.Normal * radius : anchor;
	}

	bool TryResolveGrappleTowTarget( Guid id, out GameObject target )
	{
		target = null;
		if ( id == Guid.Empty )
			return false;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		target = scene.Directory.FindByGuid( id );
		return target.IsValid() && target.Components.Get<Rigidbody>() is not null;
	}

	/// <summary>Host tick: the towed object was destroyed (split, broken) or is impossibly far — drop the rope.</summary>
	void TickGrappleTowValidity()
	{
		if ( !GrappleAttached || GrappleAttachTowId == Guid.Empty )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( !TryResolveGrappleTowTarget( GrappleAttachTowId, out var target ) )
		{
			ServerDetach( "tow target lost" );
			return;
		}

		if ( Vector3.DistanceBetween( GameObject.WorldPosition, ResolveTowAnchor( target ).anchor ) > GetMaxRangeEngine() * 1.25f )
			ServerDetach( "tow target out of range" );
	}

	bool IsSprintHeldForTow()
	{
		if ( string.IsNullOrWhiteSpace( SprintInputAction ) )
			return false;

		_vitals ??= Components.Get<PlayerVitals>();
		if ( _vitals is not null && _vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon ) )
			return false;

		return IsLocalMovementDriver() ? Input.Down( SprintInputAction ) : _sprintHeldReportedOnHost;
	}

	/// <summary>
	/// Host, fixed step: when the rope is taut, drive the towed body toward the holder's hand at a
	/// capped, slowly reached speed. No slope gating: gravity on the log is the only hill penalty.
	/// </summary>
	void TickGrappleTowPull( float dt )
	{
		if ( !IsTowGrappleAttach )
		{
			_towSpeedMeters = 0f;
			if ( !IsLocalMovementDriver() )
				IsTowRopeTaut = false;
			return;
		}

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( !TryResolveGrappleTowTarget( GrappleAttachTowId, out var target ) )
			return;

		var body = target.Components.Get<Rigidbody>();
		if ( body is null || !body.IsValid() )
			return;

		// A towed prop's idle push-damping stays off while the rope is on it.
		if ( target.Components.Get<TowableProp>() is { } prop )
			prop.TowedUntil = Time.NowDouble + 0.25;

		var ( anchor, centre, _ ) = ResolveTowAnchor( target );
		var hand = ResolveLeftArmWorldPoint();
		var toHand = hand - anchor;
		var dist = toHand.Length;

		// Winching (E) pulls the holder to the log, never the log to the holder; the rope length
		// follows the holder in (never shorter than where they actually are).
		if ( TowWinchHeld )
		{
			_towSpeedMeters = 0f;
			GrappleRopeLengthEngine = Math.Max( GetMinLengthEngine(), Math.Min( GrappleRopeLengthEngine, dist ) );
			return;
		}

		var maxLen = Math.Max( 1f, GrappleRopeLengthEngine );
		var taut = dist > maxLen && dist > 1e-3f;
		IsTowRopeTaut = taut;

		dt = Math.Max( 1e-4f, dt );
		var accel = Math.Max( 0.05f, TowAccelerationMeters );
		if ( !taut )
		{
			_towSpeedMeters = Math.Max( 0f, _towSpeedMeters - accel * 2f * dt );
			return;
		}

		var dir = toHand / dist;
		var flat = dir.WithZ( 0f );
		var run = flat.Length;
		var pull = run > 1e-3f ? flat / run : dir;
		var (ease, roll, endness) = ComputeTowEase( target, body, pull );
		_towEase = ease;
		var sprint = IsSprintHeldForTow();
		// No slope gating (Mark): the pull is the same on a hill as on the flat; gravity on the log
		// is the only thing that makes uphill slower. (The old grade test measured slope along the
		// rope - from the log's axis up to the hand - so flat ground already read as uphill.)
		var target_ = (sprint ? TowSprintSpeedMeters : TowWalkSpeedMeters) * ease;
		accel *= ease;

		_towSpeedMeters = _towSpeedMeters < target_
			? Math.Min( target_, _towSpeedMeters + accel * dt )
			: Math.Max( target_, _towSpeedMeters - accel * 2f * dt );

		if ( _towSpeedMeters <= 1e-3f )
			return;

		// Where the pull goes in: pulling along the log it goes in AT THE HOOK POINT, so a log hooked
		// by one end pivots to trail in line behind the holder (threads between trees). Pulling
		// broadside it goes in at the centre of mass, so the log does not yaw into line but ROLLS,
		// and the roll ease above makes that the easy way to move it (Mark). The impulse closes
		// part of the gap each step, so it comes up to speed over a few steps rather than snapping.
		var at = Vector3.Lerp( anchor, centre, roll );
		var speed = TerrainWorldUnits.MetersToEngine( _towSpeedMeters );
		var pointVel = body.PhysicsBody is { } pb ? pb.GetVelocityAtPoint( at ) : body.Velocity;
		var along = Vector3.Dot( pointVel, pull );
		if ( along < speed )
			body.ApplyImpulseAt( at, pull * ( speed - along ) * body.Mass * 0.7f );

		// Spin about the log's own long axis: damped when dragged end-on (it slides), left alone
		// when pulled broadside (it rolls on the ground).
		var axisW = target.WorldRotation.Up;
		var w = body.AngularVelocity;
		w -= axisW * Vector3.Dot( w, axisW ) * 0.9f * ( 1f - roll );

		// Steering: a log hooked by an end swings so that end leads and the far end trails directly
		// behind the pull - so you can turn a log by walking round with its tip (Mark). Yaw toward
		// alignment, strength by how far out the hook is.
		var lead = ( anchor - centre ).WithZ( 0f );
		if ( TowAlignYawRate > 0f && endness > 0.05f && lead.Length > 1e-3f )
		{
			lead = lead.Normal;
			var ang = MathF.Atan2( Vector3.Cross( lead, pull ).z, Vector3.Dot( lead, pull ) );
			var wantYaw = Math.Clamp( ang * 2f, -TowAlignYawRate, TowAlignYawRate ) * endness;
			w = w.WithZ( wantYaw );
		}
		body.AngularVelocity = w;
	}

	/// <summary>
	/// Owner, fixed step: the holder is the one with feet — the rope cannot pull them, but when the
	/// log is not keeping up the stretched rope holds them back, which is the "tough to pull" feel.
	/// </summary>
	void TickGrappleTowHoldback()
	{
		if ( !IsLocalMovementDriver() )
			return;

		if ( !IsTowGrappleAttach )
		{
			TowWinchHeld = false;
			return;
		}

		if ( !TryResolveGrappleTowTarget( GrappleAttachTowId, out var target ) )
			return;

		TowWinchHeld = IsRetractingRope;
		var ( anchor, _, _ ) = ResolveTowAnchor( target );
		var hand = ResolveLeftArmWorldPoint();
		var toHand = hand - anchor;
		var dist = toHand.Length;
		var maxLen = Math.Max( 1f, GrappleRopeLengthEngine );
		var slack = TowWinchHeld ? 0f : TerrainWorldUnits.MetersToEngine( Math.Max( 0f, TowHolderSlackMeters ) );
		IsTowRopeTaut = dist > maxLen;
		if ( target.Components.Get<Rigidbody>() is { } towBody && dist > 1e-4f )
		{
			var flat = ( toHand / dist ).WithZ( 0f );
			_towEase = ComputeTowEase( target, towBody, flat.Length > 1e-3f ? flat.Normal : Vector3.Forward ).ease;
		}
		if ( dist <= maxLen + slack || dist < 1e-4f )
			return;

		// The rope holds the holder back (or, winching, reels them in) - as a swept move, so a wall
		// or a tree between holder and log stops them instead of being teleported through.
		// Never more than one step's worth per tick, so nothing can bank up and pay out at once.
		var radial = toHand / dist;
		var step = TerrainWorldUnits.MetersToEngine( TowWinchHeld ? TowWinchPullSpeedMeters : 8f ) * Time.Delta;
		var want = Math.Min( dist - maxLen - slack, step );
		var flatRadial = radial.WithZ( 0f );
		radial = flatRadial.Length > 1e-3f ? flatRadial.Normal : radial;   // walk, do not climb the rope
		var from = GameObject.WorldPosition;
		var to = from - radial * want;
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		_controller ??= Components.Get<PlayerController>();
		var radius = _controller is not null ? Math.Max( 4f, _controller.BodyRadius * 0.6f ) : 10f;
		var height = _controller is not null ? _controller.BodyHeight * 0.6f : 40f;
		var probe = from + Vector3.Up * height;   // chest height: ground bumps never block, a wall or trunk does
		var tr = scene.Trace.Ray( probe, probe - radial * want ).Radius( radius )
			.IgnoreGameObjectHierarchy( GameObject ).IgnoreGameObjectHierarchy( target ).Run();
		if ( tr.Hit )
		{
			var allowed = Math.Max( 0f, Vector3.DistanceBetween( probe, tr.EndPosition ) - 1f );
			if ( allowed < 0.5f )
				return;
			to = from - radial * allowed;
		}

		GameObject.WorldPosition = to;
		Transform.ClearInterpolation();

		var body = ResolveGrappleBody();
		if ( body is not null && body.IsValid() )
		{
			var vRad = Vector3.Dot( body.Velocity, radial );
			if ( vRad > 0f )
				body.Velocity -= radial * vRad;
		}
	}

	/// <summary>Walk / run scale while the tow rope is taut (1 = no change); an easy load (half log, rolling) slows the holder less.</summary>
	float ComputeTowSpeedScale()
	{
		if ( !IsTowGrappleAttach || !IsTowRopeTaut )
			return 1f;

		var slow = 1f - Math.Clamp( TowHolderSpeedScale, 0.1f, 1f );
		return 1f - slow / Math.Max( 1f, _towEase );
	}

	void ClearGrappleTowAttachState()
	{
		GrappleAttachTowId = Guid.Empty;
		_towSpeedMeters = 0f;
		_towEase = 1f;
		if ( IsLocalMovementDriver() )
			TowWinchHeld = false;
		IsTowRopeTaut = false;
	}
}
