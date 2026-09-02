using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Player-vs-player grapple: hook other pawns, break their rope, and apply a short re-grapple lockout.
/// Attacker rope state is independent — if the victim hooks a tree, the attacker's hook on them stays.
/// The attacker owns the rope: the victim is reeled toward the attacker (never the reverse), so the
/// pendulum/swing constraint is skipped entirely for player attaches.
/// </summary>
partial class PlayerMovement
{
	/// <summary>Seconds after being hooked before this pawn may attach their own grapple again.</summary>
	[Property, Group( "Grapple PvP" ), Title( "Victim Re-grapple Cooldown (s)" ), Range( 0f, 10f ), Step( 0.25f )]
	public float GrappleVictimCooldownSeconds { get; set; } = 3f;

	/// <summary>Host-synced player anchor when <see cref="GrappleAttached"/> targets a pawn; empty = static world point.</summary>
	[Sync( SyncFlags.FromHost )] public Guid GrappleAttachPlayerId { get; private set; }

	/// <summary>Attach point in the target pawn's local space — follows body motion.</summary>
	[Sync( SyncFlags.FromHost )] public Vector3 GrappleAttachLocalOffset { get; private set; }

	/// <summary>Host-synced sandbox time after which this pawn may grapple again after being hooked.</summary>
	[Sync( SyncFlags.FromHost )] double GrappleVictimCooldownEndsAt { get; set; }

	/// <summary>Host-synced attacker pawn id while this pawn is on someone else's hook; empty = free.</summary>
	[Sync( SyncFlags.FromHost )] public Guid GrappledByPlayerId { get; private set; }

	/// <summary>Rope is hooked to another pawn — the swing constraint must not run on the attacker.</summary>
	public bool IsPlayerGrappleAttach => GrappleAttached && GrappleAttachPlayerId != Guid.Empty;

	/// <summary>On someone's hook right now. Blocks firing our own rope — the holder has control.</summary>
	public bool IsHeldByPlayerGrapple => GrappledByPlayerId != Guid.Empty;

	public bool IsGrappleVictimCooldownActive() => Time.NowDouble < GrappleVictimCooldownEndsAt;

	public float GrappleVictimCooldownRemainingSeconds =>
		Math.Max( 0f, (float)( GrappleVictimCooldownEndsAt - Time.NowDouble ) );

	/// <summary>World attach point — static from sync, or recomputed from a hooked player each frame.</summary>
	Vector3 ResolveGrappleAttachWorldPoint()
	{
		if ( !GrappleAttached )
			return GrappleAttachWorldPoint;

		if ( GrappleAttachPlayerId == Guid.Empty )
			return GrappleAttachWorldPoint;

		if ( !TryResolveGrapplePlayerTarget( GrappleAttachPlayerId, out var target ) )
			return GrappleAttachWorldPoint;

		return target.WorldTransform.PointToWorld( GrappleAttachLocalOffset );
	}

	static bool TryGetGrapplePlayerRoot( GameObject go, out GameObject playerRoot )
	{
		playerRoot = null;
		for ( var cur = go; cur.IsValid(); cur = cur.Parent )
		{
			if ( cur.Components.Get<PlayerMovement>() is not null )
			{
				playerRoot = cur;
				return true;
			}
		}

		return false;
	}

	bool IsGrappleablePlayer( GameObject go )
	{
		if ( !TryGetGrapplePlayerRoot( go, out var root ) )
			return false;

		if ( root == GameObject )
			return false;

		var vitals = root.Components.Get<PlayerVitals>();
		if ( vitals is not null && vitals.CurrentHealth <= 0.001f )
			return false;

		return true;
	}

	bool IsGrappleTarget( SceneTraceResult tr )
	{
		if ( HasGrappleTag( tr ) )
			return true;

		return tr.GameObject.IsValid() && IsGrappleablePlayer( tr.GameObject );
	}

	GameObject ResolveGrappleTargetRoot( GameObject go )
	{
		if ( TryGetGrapplePlayerRoot( go, out var playerRoot ) && playerRoot != GameObject )
			return playerRoot;

		return ResolveGrappleRoot( go );
	}

	bool IsSameGrappleTarget( GameObject a, GameObject b )
	{
		if ( !a.IsValid() || !b.IsValid() )
			return false;

		return ResolveGrappleTargetRoot( a ) == ResolveGrappleTargetRoot( b );
	}

	bool TryResolveGrapplePlayerTarget( Guid id, out GameObject target )
	{
		target = null;
		if ( id == Guid.Empty )
			return false;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		foreach ( var move in scene.GetAllComponents<PlayerMovement>() )
		{
			if ( move is null || !move.GameObject.IsValid() || move.GameObject.Id != id )
				continue;

			target = move.GameObject;
			return true;
		}

		return false;
	}

	/// <summary>
	/// Host check for a player attach: only the pawn the client actually aimed at is considered —
	/// never a bystander who happens to stand near the hit point. Slack covers pawn motion in flight.
	/// </summary>
	bool TryValidatePlayerAttach( Guid intendedPlayerId, Vector3 clientHitPoint, out GameObject playerRoot, out Vector3 attachPoint )
	{
		playerRoot = null;
		attachPoint = default;

		if ( intendedPlayerId == Guid.Empty )
			return false;

		if ( !TryResolveGrapplePlayerTarget( intendedPlayerId, out var root ) || !IsGrappleablePlayer( root ) )
			return false;

		if ( !TryResolveClosestPointOnObject( root, clientHitPoint, out var closest ) )
			return false;

		// The pawn keeps moving between the client's aim frame and the host validating it.
		var latencySlack = TerrainWorldUnits.MetersToEngine( 2.5f );
		if ( Vector3.DistanceBetween( closest, clientHitPoint ) > latencySlack )
			return false;

		if ( !IsWithinGrappleRange( closest ) )
			return false;

		playerRoot = root;
		attachPoint = closest;
		return true;
	}

	void ApplyGrappledByPlayerCooldown( float seconds )
	{
		if ( seconds <= 0f )
			return;

		var end = Time.NowDouble + seconds;
		if ( end > GrappleVictimCooldownEndsAt )
			GrappleVictimCooldownEndsAt = end;
	}

	/// <summary>Host-only: victim loses their rope, gets the re-grapple lockout, and learns who holds them.</summary>
	internal void HostNotifyGrappledByPlayer( Guid attackerId )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( GrappleAttached )
			ServerDetach( "grappled by player" );

		GrappledByPlayerId = attackerId;
		ApplyGrappledByPlayerCooldown( GrappleVictimCooldownSeconds );
	}

	/// <summary>Host-only: attacker's rope on this pawn is gone — stop the pull (lockout keeps ticking).</summary>
	internal void HostClearGrappledBy( Guid attackerId )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( GrappledByPlayerId == attackerId )
			GrappledByPlayerId = Guid.Empty;
	}

	/// <summary>
	/// Host tick while this pawn's rope holds another player. Runs from <c>OnFixedUpdate</c> before the
	/// local-driver gate, so it also covers client attackers whose pawns are proxies on the host.
	/// </summary>
	void TickGrapplePlayerTargetValidity()
	{
		if ( !GrappleAttached || GrappleAttachPlayerId == Guid.Empty )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( !TryResolveGrapplePlayerTarget( GrappleAttachPlayerId, out var target ) )
		{
			ServerDetach( "player target lost" );
			return;
		}

		var vitals = target.Components.Get<PlayerVitals>();
		if ( vitals is not null && vitals.CurrentHealth <= 0.001f )
		{
			ServerDetach( "player target down" );
			return;
		}

		// Way past max range means a desync or teleport, not gameplay — break the rope.
		var attachWorld = target.WorldTransform.PointToWorld( GrappleAttachLocalOffset );
		if ( Vector3.DistanceBetween( GameObject.WorldPosition, attachWorld ) > GetMaxRangeEngine() * 1.25f )
			ServerDetach( "player target out of range" );
	}

	/// <summary>
	/// Host tick while this pawn is held: if the attacker despawned (disconnect) or is no longer
	/// hooked to us, drop the held flag — otherwise a stale id blocks this pawn's grapple forever.
	/// </summary>
	void TickGrappledByValidity()
	{
		if ( GrappledByPlayerId == Guid.Empty )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( !TryResolveGrapplePlayerTarget( GrappledByPlayerId, out var attackerGo )
		     || attackerGo.Components.Get<PlayerMovement>() is not { } attacker
		     || !attacker.GrappleAttached
		     || attacker.GrappleAttachPlayerId != GameObject.Id )
			GrappledByPlayerId = Guid.Empty;
	}

	/// <summary>
	/// Victim-side pull, on the victim's owning machine (position is owner-authored). The attacker's
	/// hand is the anchor: walking away or winching E drags this pawn in; nothing here ever moves the
	/// attacker, which is what gives the rope holder ultimate control.
	/// </summary>
	void TickGrappledByPlayerPull()
	{
		if ( GrappledByPlayerId == Guid.Empty || IsGrappleLedgePulling )
			return;

		if ( !TryResolveGrapplePlayerTarget( GrappledByPlayerId, out var attackerGo ) )
			return;

		var attacker = attackerGo.Components.Get<PlayerMovement>();
		if ( attacker is null || !attacker.GrappleAttached || attacker.GrappleAttachPlayerId != GameObject.Id )
			return; // Host clear is still in flight — do not pull on stale state.

		var maxLen = Math.Max( 1f, attacker.GrappleRopeLengthEngine );
		var anchor = attacker.ResolveLeftArmWorldPoint();
		var attachWorld = GameObject.WorldTransform.PointToWorld( attacker.GrappleAttachLocalOffset );
		var toAttach = attachWorld - anchor;
		var dist = toAttach.Length;
		if ( dist <= maxLen + 1f || dist < 1e-4f )
			return;

		var radial = toAttach / dist;
		GameObject.WorldPosition -= radial * ( dist - maxLen );
		Transform.ClearInterpolation();

		var body = ResolveGrappleBody();
		if ( body is not null && body.IsValid() )
		{
			var vRad = Vector3.Dot( body.Velocity, radial );
			if ( vRad > 0f )
				body.Velocity -= radial * vRad;
		}
	}

	void ClearGrapplePlayerAttachState()
	{
		GrappleAttachPlayerId = Guid.Empty;
		GrappleAttachLocalOffset = default;
	}

}
