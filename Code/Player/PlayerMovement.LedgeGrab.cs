using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Ledge grab: Space vaults the pawn up onto a standable lip within arm's reach — short guided
/// pull up and over the lip, Citizen ledgegrab pull-up on every peer. Two ways in:
/// <list type="bullet">
/// <item><b>Grapple</b> — hanging on the rope, any solid geometry near the <b>pawn</b> (not the
/// attach point, no grapple tag needed); the rope breaks that instant. Pro scheme only — Training
/// Wheels binds Space to winch retract, so no grapple ledge grab exists there.</item>
/// <item><b>Jump</b> — airborne without a rope (jump, then Space again), onto the lip of a build
/// piece (Mark 2026-11-10) or any other static world object — rock spire stumps, boulders, props,
/// cave ledges (Mark 2026-11-11: "ledge grab and vault onto other square objects like the rock
/// spires"). Never terrain, never a moving body or a pawn (<see cref="IsJumpLedgeSurface"/>). Takes
/// Space before the augment air hop and the wingsuit deploy, and only when such a lip is in
/// reach.</item>
/// </list>
/// Owned by <see cref="PlayerMovement"/> (Commandment #1), like the swing.
/// </summary>
partial class PlayerMovement
{
	[Property, Group( "Ledge Grab" ), Title( "Enabled (grapple)" )]
	public bool LedgeGrabEnabled { get; set; } = true;

	/// <summary>Airborne Space (no rope) mantles onto lips of build pieces and static world objects (not terrain).</summary>
	[Property, Group( "Ledge Grab" ), Title( "Enabled (jump)" )]
	public bool JumpLedgeGrabEnabled { get; set; } = true;

	/// <summary>Highest lip above the pawn's feet that Space can still mantle (arm reach above the head).</summary>
	[Property, Group( "Ledge Grab" ), Title( "Reach Up (meters)" ), Range( 0.5f, 5f ), Step( 0.1f )]
	public float LedgeGrabReachMeters { get; set; } = 2.2f;

	/// <summary>How far in front of the pawn to look for the standable top.</summary>
	[Property, Group( "Ledge Grab" ), Title( "Forward Search (meters)" ), Range( 0.3f, 3f ), Step( 0.1f )]
	public float LedgeGrabForwardMeters { get; set; } = 1.2f;

	/// <summary>Pull speed along the up-and-over path.</summary>
	[Property, Group( "Ledge Grab" ), Title( "Pull Speed (m/s)" ), Range( 2f, 15f ), Step( 0.5f )]
	public float LedgeGrabPullMetersPerSecond { get; set; } = 6f;

	/// <summary>Local driver is being pulled onto a ledge right now (rope, if any, already released).</summary>
	public bool IsLedgePulling { get; private set; }

	/// <summary>Slopes steeper than this normal.z are a wall face, not a stand.</summary>
	const float LedgeStandNormalZ = 0.7f;

	/// <summary>Down-trace start heights as fractions of reach — lower retries catch a shelf under more wall.</summary>
	static readonly float[] LedgeSearchHeightFractions = { 1f, 0.66f, 0.4f };

	/// <summary>Face-probe heights as fractions of reach — where a forward ray looks for the face below a lip.</summary>
	static readonly float[] LedgeFaceProbeHeightFractions = { 0.15f, 0.5f, 0.85f };

	/// <summary>How far past a found face the lip down-trace lands (engine units) — inside a 0.25 m wall top.</summary>
	const float LedgeFaceInset = 4f;

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
		if ( !LedgeGrabEnabled || IsLedgePulling )
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

		if ( !TryFindLedgeStand( objectsOnly: false, out var stand ) )
		{
			LogLedgeGrabReject( "no standable lip in reach" );
			return;
		}

		StartLedgePull( stand, breakRope: true );
	}

	/// <summary>
	/// Space pressed in the air without a rope — called from <see cref="PreInput"/> before the
	/// augment air hop and the wingsuit gate see Jump. Mantles onto lips of build pieces and static
	/// world objects (<see cref="IsJumpLedgeSurface"/>); with none in reach the press falls through
	/// untouched. Traces run once, on the press only.
	/// </summary>
	void TryStartJumpLedgeGrabFromJumpPress()
	{
		if ( !JumpLedgeGrabEnabled || IsLedgePulling )
			return;

		if ( string.IsNullOrWhiteSpace( JumpInputAction ) || !Input.Pressed( JumpInputAction ) )
			return;

		// Rope: the grapple path owns Space. Wingsuit: Space stows the suit.
		if ( GrappleAttached || WingsuitDeployed || IsHeldByPlayerGrapple )
			return;

		_controller ??= Components.Get<PlayerController>();
		if ( _controller is null || _controller.IsOnGround || _controller.IsSwimming )
			return;

		if ( IsHitReactionActive() )
			return;

		if ( !TryFindLedgeStand( objectsOnly: true, out var stand ) )
			return;

		StartLedgePull( stand, breakRope: false );
		ClearActionIfPressed( JumpInputAction );
	}

	void LogLedgeGrabReject( string reason )
	{
		if ( LogGrapple )
			Log.Info( $"[PlayerMovement.LedgeGrab] {GameObject.Name}: Space while grappled — no ledge grab ({reason})." );
	}

	/// <summary>
	/// Standable top near the pawn: search along camera look, then toward the rope (grapple only),
	/// then pawn facing. Per direction, first a forward ray finds the face and the lip is traced just
	/// behind it (thin wall tops), then fixed forward steps trace down from reach height for a
	/// walkable hit above the feet; every candidate must pass capsule clearance.
	/// <paramref name="objectsOnly"/> (the jump path) accepts only hits on
	/// <see cref="IsJumpLedgeSurface"/> objects; anything else still blocks the ray. Runs only on the
	/// Space press, never per frame.
	/// </summary>
	bool TryFindLedgeStand( bool objectsOnly, out Vector3 stand )
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
		if ( GrappleAttached )
			AddLedgeSearchDir( dirs, ref dirCount, ResolveGrappleAttachWorldPoint() - pos );
		AddLedgeSearchDir( dirs, ref dirCount, GameObject.WorldRotation.Forward );

		for ( var d = 0; d < dirCount; d++ )
		{
			var dir = dirs[d];

			// Face probe: a 0.25 m wall top falls between the fixed steps below.
			foreach ( var fraction in LedgeFaceProbeHeightFractions )
			{
				var from = pos + Vector3.Up * ( reachUp * fraction );
				var face = LedgeTrace( scene, scene.Trace.Ray( from, from + dir * maxForward ), objectsOnly );
				if ( !face.Hit || face.StartedSolid || face.Normal.z >= LedgeStandNormalZ )
					continue;

				var ahead = face.HitPosition.WithZ( pos.z ) + dir * LedgeFaceInset;
				if ( TryLedgeStandAt( scene, ahead, reachUp, minRise, objectsOnly, out stand ) )
					return true;
			}

			for ( var step = 1; step <= 3; step++ )
			{
				var ahead = pos + dir * ( maxForward * ( step / 3f ) );
				if ( TryLedgeStandAt( scene, ahead, reachUp, minRise, objectsOnly, out stand ) )
					return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Down-traces at <paramref name="ahead"/> (feet height) from a few heights — highest first so
	/// the topmost floor wins; lower retries catch a shelf that still has wall above it, where the
	/// high start would begin inside the face.
	/// </summary>
	bool TryLedgeStandAt( Scene scene, Vector3 ahead, float reachUp, float minRise, bool objectsOnly, out Vector3 stand )
	{
		stand = default;
		foreach ( var fraction in LedgeSearchHeightFractions )
		{
			var height = reachUp * fraction;
			if ( height <= minRise + 1f )
				continue;

			var tr = LedgeTrace( scene, scene.Trace.Ray( ahead + Vector3.Up * height, ahead + Vector3.Up * minRise ), objectsOnly );
			if ( !tr.Hit || tr.StartedSolid )
				continue;

			if ( tr.Normal.z < LedgeStandNormalZ )
				continue;

			if ( !HasLedgeStandingClearance( scene, tr.HitPosition ) )
				continue;

			stand = tr.HitPosition;
			return true;
		}

		return false;
	}

	/// <summary>
	/// One ledge ray, pawn ignored. With <paramref name="objectsOnly"/> a hit on anything that is not
	/// a <see cref="IsJumpLedgeSurface"/> comes back as a miss — it still occludes, so a lip behind a
	/// hill or another pawn is never reached through it.
	/// </summary>
	SceneTraceResult LedgeTrace( Scene scene, SceneTrace trace, bool objectsOnly )
	{
		var tr = trace.IgnoreGameObjectHierarchy( GameObject ).Run();
		if ( objectsOnly && tr.Hit && !IsJumpLedgeSurface( tr ) )
			return default;
		return tr;
	}

	/// <summary>
	/// What the jump ledge grab may mantle onto: a build piece, or any other static collider that is
	/// not terrain (heightfield chunks, voxel columns), water or a pawn. A hillside is not a lip — it
	/// would steal Space from the air hop / wingsuit on every slope — and a moving body is not
	/// something to stand on.
	/// </summary>
	static bool IsJumpLedgeSurface( in SceneTraceResult tr )
	{
		var go = tr.GameObject;
		if ( go is null || !go.IsValid() )
			return false;

		var tags = go.Tags;
		if ( tags.Has( ShelterProbe.BuildPieceTag ) )
			return true;

		if ( tags.Has( TerrainWorldManager.TerrainTag ) || tags.Has( "water" ) || tags.Has( "player" ) )
			return false;

		return tr.Body is not null && tr.Body.BodyType == PhysicsBodyType.Static;
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

	void StartLedgePull( Vector3 stand, bool breakRope )
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

		IsLedgePulling = true;

		// Mantling breaks the grapple at the same instant: the host clears attach state, and the
		// local rope draw + constraint are gated on IsLedgePulling so the break doesn't wait out the
		// detach round trip. The pull itself is owner motion like the swing.
		if ( breakRope )
			RequestDetach();

		// Citizen ledgegrab pull-up on every peer, fitted to the expected pull time.
		Components.Get<PlayerAnimation>()?.BeginLedgeMantle( pathLen / speed + 0.2f );

		if ( LogGrapple )
			Log.Info( $"[PlayerMovement.LedgeGrab] {GameObject.Name}: ledge grab — pulling up {TerrainWorldUnits.EngineToMeters( pathLen ):0.##}m." );
	}

	/// <summary>Fixed-step guided pull: rise beside the face to lip height, then in onto the stand.</summary>
	void TickLedgePull( float dt )
	{
		if ( !IsLedgePulling )
			return;

		if ( !IsLocalMovementDriver() || IsHitReactionActive() || Time.NowDouble > _ledgePullDeadline )
		{
			EndLedgePull();
			return;
		}

		var body = ResolveGrappleBody();
		if ( body is null || !body.IsValid() )
		{
			EndLedgePull();
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
			EndLedgePull();
			return;
		}

		if ( dist < 1e-3f )
		{
			body.Velocity = Vector3.Zero;
			EndLedgePull();
			return;
		}

		if ( body.Sleeping )
			body.Sleeping = false;

		body.Velocity = to / dist * Math.Min( speed, dist / dt );
	}

	void EndLedgePull()
	{
		IsLedgePulling = false;
		_ledgePullPastCorner = false;
		Components.Get<PlayerAnimation>()?.EndLedgeMantle();
	}
}
