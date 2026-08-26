using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Grapple ledge grab: while hanging on the rope with a standable lip within arm's reach, Space
/// vaults the pawn up onto it (rope breaks that instant, short guided pull up and over the lip).
/// The lip is any solid geometry near the <b>pawn</b> — not the attach point — and does not need
/// the grapple tag. Pro scheme only — Training Wheels binds Space to winch retract, so no ledge
/// grab exists there. Owned by <see cref="PlayerMovement"/> (Commandment #1), like the swing.
/// </summary>
partial class PlayerMovement
{
	[Property, Group( "Grapple Ledge Grab" ), Title( "Enabled" )]
	public bool LedgeGrabEnabled { get; set; } = true;

	/// <summary>Highest lip above the pawn's feet that Space can still mantle (arm reach above the head).</summary>
	[Property, Group( "Grapple Ledge Grab" ), Title( "Reach Up (meters)" ), Range( 0.5f, 5f ), Step( 0.1f )]
	public float LedgeGrabReachMeters { get; set; } = 2.2f;

	/// <summary>How far in front of the pawn to look for the standable top.</summary>
	[Property, Group( "Grapple Ledge Grab" ), Title( "Forward Search (meters)" ), Range( 0.3f, 3f ), Step( 0.1f )]
	public float LedgeGrabForwardMeters { get; set; } = 1.2f;

	/// <summary>Pull speed along the up-and-over path.</summary>
	[Property, Group( "Grapple Ledge Grab" ), Title( "Pull Speed (m/s)" ), Range( 2f, 15f ), Step( 0.5f )]
	public float LedgeGrabPullMetersPerSecond { get; set; } = 6f;

	/// <summary>Local driver is being pulled onto a ledge right now (rope already released).</summary>
	public bool IsGrappleLedgePulling { get; private set; }

	/// <summary>Slopes steeper than this normal.z are a wall face, not a stand.</summary>
	const float LedgeStandNormalZ = 0.7f;

	/// <summary>Down-trace start heights as fractions of reach — lower retries catch a shelf under more wall.</summary>
	static readonly float[] LedgeSearchHeightFractions = { 1f, 0.66f, 0.4f };

	Vector3 _ledgePullCorner;
	Vector3 _ledgePullTarget;
	bool _ledgePullPastCorner;
	double _ledgePullDeadline;

	/// <summary>
	/// Space pressed while hanging on the rope — called from <see cref="PreInput"/> before the
	/// grapple jump-clear eats the action. All traces run once, on the press only.
	/// </summary>
	void TryStartGrappleLedgeGrabFromJumpPress()
	{
		if ( !LedgeGrabEnabled || IsGrappleLedgePulling )
			return;

		if ( string.IsNullOrWhiteSpace( JumpInputAction ) || !Input.Pressed( JumpInputAction ) )
			return;

		// Training Wheels: Space is winch retract — there is no applicable ledge grab in that scheme.
		if ( GrappleControlSchemeStore.NeedsChoice || GrappleControlSchemeStore.IsTrainingWheels )
		{
			LogLedgeGrabReject( "training wheels scheme" );
			return;
		}

		// Grounded Space stays a normal jump — the grab is for hanging on the rope.
		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null || _controller.IsOnGround )
		{
			LogLedgeGrabReject( "on ground" );
			return;
		}

		// Hooked players move around; only static surface attaches leave you beside a ledge.
		if ( GrappleAttachPlayerId != Guid.Empty )
		{
			LogLedgeGrabReject( "attached to a player" );
			return;
		}

		if ( IsHitReactionActive() )
		{
			LogLedgeGrabReject( "hit reaction" );
			return;
		}

		if ( !TryFindGrappleLedgeStand( out var stand ) )
		{
			LogLedgeGrabReject( "no standable lip in reach" );
			return;
		}

		StartGrappleLedgePull( stand );
	}

	void LogLedgeGrabReject( string reason )
	{
		if ( LogGrapple )
			Log.Info( $"[PlayerMovement.GrappleLedge] {GameObject.Name}: Space while grappled — no ledge grab ({reason})." );
	}

	/// <summary>
	/// Standable top near the pawn: search along camera look, then toward the rope, then pawn
	/// facing; at a few forward steps, trace down from reach height for a walkable hit above the
	/// feet, then confirm capsule clearance. Any solid geometry counts — the ledge itself does not
	/// need the grapple tag. Runs only on the Space press, never per frame.
	/// </summary>
	bool TryFindGrappleLedgeStand( out Vector3 stand )
	{
		stand = default;
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		var pos = GameObject.WorldPosition;
		var reachUp = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, LedgeGrabReachMeters ) );
		var minRise = TerrainWorldUnits.MetersToEngine( 0.1f );
		var maxForward = TerrainWorldUnits.MetersToEngine( Math.Max( 0.3f, LedgeGrabForwardMeters ) );

		Span<Vector3> dirs = stackalloc Vector3[3];
		var dirCount = 0;
		if ( TryGetAimRayFromPlayer( out _, out var look ) )
			AddLedgeSearchDir( dirs, ref dirCount, look );
		AddLedgeSearchDir( dirs, ref dirCount, ResolveGrappleAttachWorldPoint() - pos );
		AddLedgeSearchDir( dirs, ref dirCount, GameObject.WorldRotation.Forward );

		for ( var d = 0; d < dirCount; d++ )
		{
			for ( var step = 1; step <= 3; step++ )
			{
				var ahead = pos + dirs[d] * ( maxForward * ( step / 3f ) );

				// Highest start first (topmost floor wins); lower retries catch a shelf that still
				// has wall above it, where the high start would begin inside the face.
				foreach ( var fraction in LedgeSearchHeightFractions )
				{
					var height = reachUp * fraction;
					if ( height <= minRise + 1f )
						continue;

					var tr = scene.Trace.Ray( ahead + Vector3.Up * height, ahead + Vector3.Up * minRise )
						.IgnoreGameObjectHierarchy( GameObject )
						.Run();

					if ( !tr.Hit || tr.StartedSolid )
						continue;

					if ( tr.Normal.z < LedgeStandNormalZ )
						continue;

					if ( !HasLedgeStandingClearance( scene, tr.HitPosition ) )
						continue;

					stand = tr.HitPosition;
					return true;
				}
			}
		}

		return false;
	}

	/// <summary>Flatten, normalize, and dedup a candidate search direction.</summary>
	static void AddLedgeSearchDir( Span<Vector3> dirs, ref int count, Vector3 candidate )
	{
		var flat = candidate.WithZ( 0f );
		if ( flat.LengthSquared < 1e-4f || count >= dirs.Length )
			return;

		flat = flat.Normal;
		for ( var i = 0; i < count; i++ )
		{
			if ( Vector3.Dot( dirs[i], flat ) > 0.85f )
				return;
		}

		dirs[count++] = flat;
	}

	/// <summary>Pawn capsule fits standing at the landing point (sphere sweep feet→head).</summary>
	bool HasLedgeStandingClearance( Scene scene, Vector3 groundPoint )
	{
		_controller ??= Components.Get<PlayerController>();
		var radius = _controller is not null ? Math.Max( 8f, _controller.BodyRadius * 0.9f ) : 14f;
		var height = _controller is not null ? Math.Max( 32f, _controller.BodyHeight ) : 72f;

		var feet = groundPoint + Vector3.Up * ( radius + 2f );
		var head = groundPoint + Vector3.Up * Math.Max( radius + 4f, height - radius );
		var tr = scene.Trace.Sphere( radius, feet, head )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		return !tr.StartedSolid && !tr.Hit;
	}

	void StartGrappleLedgePull( Vector3 stand )
	{
		var pos = GameObject.WorldPosition;
		var lip = TerrainWorldUnits.MetersToEngine( 0.2f );
		_ledgePullCorner = pos.WithZ( stand.z + lip );
		_ledgePullTarget = stand + Vector3.Up * 1f;
		_ledgePullPastCorner = false;

		var speed = TerrainWorldUnits.MetersToEngine( Math.Max( 1f, LedgeGrabPullMetersPerSecond ) );
		var pathLen = Vector3.DistanceBetween( pos, _ledgePullCorner )
		              + Vector3.DistanceBetween( _ledgePullCorner, _ledgePullTarget );
		_ledgePullDeadline = Time.NowDouble + pathLen / speed + 0.75;

		IsGrappleLedgePulling = true;

		// Mantling breaks the grapple at the same instant: the host clears attach state, and the
		// local rope draw + constraint are gated on IsGrappleLedgePulling so the break doesn't wait
		// out the detach round trip. The pull itself is owner motion like the swing.
		RequestDetach();

		// Citizen ledgegrab pull-up on every peer, fitted to the expected pull time.
		Components.Get<PlayerAnimation>()?.BeginLedgeMantle( pathLen / speed + 0.2f );

		if ( LogGrapple )
			Log.Info( $"[PlayerMovement.GrappleLedge] {GameObject.Name}: ledge grab — pulling up {TerrainWorldUnits.EngineToMeters( pathLen ):0.##}m." );
	}

	/// <summary>Fixed-step guided pull: rise beside the face to lip height, then in onto the stand.</summary>
	void TickGrappleLedgePull( float dt )
	{
		if ( !IsGrappleLedgePulling )
			return;

		if ( !IsLocalMovementDriver() || IsHitReactionActive() || Time.NowDouble > _ledgePullDeadline )
		{
			EndGrappleLedgePull();
			return;
		}

		var body = ResolveGrappleBody();
		if ( body is null || !body.IsValid() )
		{
			EndGrappleLedgePull();
			return;
		}

		dt = Math.Max( 1e-4f, dt );
		var pos = GameObject.WorldPosition;
		var speed = TerrainWorldUnits.MetersToEngine( Math.Max( 1f, LedgeGrabPullMetersPerSecond ) );

		if ( !_ledgePullPastCorner && Vector3.DistanceBetween( pos, _ledgePullCorner ) <= speed * dt )
			_ledgePullPastCorner = true;

		var waypoint = _ledgePullPastCorner ? _ledgePullTarget : _ledgePullCorner;
		var to = waypoint - pos;
		var dist = to.Length;

		if ( _ledgePullPastCorner && dist <= speed * dt )
		{
			// Arrived over the stand — stop dead so the controller grounds cleanly.
			body.Velocity = Vector3.Zero;
			EndGrappleLedgePull();
			return;
		}

		if ( dist < 1e-3f )
		{
			body.Velocity = Vector3.Zero;
			EndGrappleLedgePull();
			return;
		}

		if ( body.Sleeping )
			body.Sleeping = false;

		body.Velocity = to / dist * Math.Min( speed, dist / dt );
	}

	void EndGrappleLedgePull()
	{
		IsGrappleLedgePulling = false;
		_ledgePullPastCorner = false;
		Components.Get<PlayerAnimation>()?.EndLedgeMantle();
	}
}
