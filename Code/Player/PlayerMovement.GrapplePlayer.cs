using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Player-vs-player grapple: a short tether (Mark 2026-11-16: "it should only stay connected for 1s at most, and slow
/// the players down 50% for that 1s"). The host breaks the rope after <see cref="PlayerTetherSeconds"/>; while it holds,
/// the hooked pawn moves at <see cref="HookedPlayerSpeedScale"/> of its speed and loses its own rope (plus the
/// re-grapple lockout). The shooter keeps full walk / run speed, jump and sprint (playtest 2026-11-18: "grappling other
/// players slows you down as well. not just the person you grappled" was a bug report - Mark: "Full speed"; the shooter
/// used to be planted for the tether).
/// Nobody is pulled: the rope just stretches between the two. The swing constraint is skipped for player attaches.
/// Hosts and clients run the same path — the client sends whom it aimed at, the host validates and sets the synced
/// ids, and the hooked pawn's owner applies its own slow from them.
/// </summary>
partial class PlayerMovement
{
	/// <summary>Seconds after being hooked before this pawn may attach their own grapple again.</summary>
	[Property, Group( "Grapple PvP" ), Title( "Victim Re-grapple Cooldown (s)" ), Range( 0f, 10f ), Step( 0.25f )]
	public float GrappleVictimCooldownSeconds { get; set; } = 3f;

	/// <summary>Longest a hook on another player holds before the host breaks it.</summary>
	[Property, Group( "Grapple PvP" ), Title( "Player Tether (s)" ), Range( 0.1f, 5f ), Step( 0.1f )]
	public float PlayerTetherSeconds { get; set; } = 1f;

	/// <summary>Walk / run multiplier for a pawn on someone's hook (0.5 = half speed).</summary>
	[Property, Group( "Grapple PvP" ), Title( "Hooked Player Speed (x)" ), Range( 0.05f, 1f ), Step( 0.05f )]
	public float HookedPlayerSpeedScale { get; set; } = 0.5f;

	/// <summary>Host: when this pawn's hook on a player breaks by itself.</summary>
	double _playerTetherEndsAt;

	/// <summary>Host-synced player anchor when <see cref="GrappleAttached"/> targets a pawn; empty = static world point.</summary>
	[Sync( SyncFlags.FromHost )] public Guid GrappleAttachPlayerId { get; private set; }

	/// <summary>Attach point in the target's local space (hooked pawn or towed body) — follows its motion.</summary>
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

	/// <summary>World attach point — static from sync, or recomputed each frame from a hooked player, towed log or ridden body.</summary>
	Vector3 ResolveGrappleAttachWorldPoint()
	{
		if ( !GrappleAttached )
			return GrappleAttachWorldPoint;

		if ( GrappleAttachRideId != Guid.Empty )
			return TryResolveGrappleRideTarget( GrappleAttachRideId, out var ridden )
				? ridden.WorldTransform.PointToWorld( GrappleAttachLocalOffset )
				: GrappleAttachWorldPoint;

		if ( GrappleAttachTowId != Guid.Empty )
			return TryResolveGrappleTowTarget( GrappleAttachTowId, out var towed )
				? ResolveTowRopeEnd( towed )
				: GrappleAttachWorldPoint;

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

		if ( Time.NowDouble >= _playerTetherEndsAt )
		{
			ServerDetach( "player tether expired" );
			return;
		}

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

	/// <summary>Host: a hook just landed on a player — start the tether clock.</summary>
	void HostStartPlayerTether() =>
		_playerTetherEndsAt = Time.NowDouble + Math.Max( 0.05f, PlayerTetherSeconds );

	/// <summary>Walk / run multiplier while on someone's hook; 1 when free. Read by the plain-locomotion speed pass.</summary>
	float HookedSpeedScale() => IsHeldByPlayerGrapple ? Math.Clamp( HookedPlayerSpeedScale, 0.05f, 1f ) : 1f;

	/// <summary>
	/// <c>grapple_test</c> only: lend this pawn a grapple (without the control-scheme prompt a real equip raises) and run
	/// the host attach a client's <c>RpcRequestAttach</c> runs, aimed at <paramref name="target"/>. True = hooked.
	/// </summary>
	internal bool TestHostHookPlayer( GameObject target )
	{
		if ( !target.IsValid() )
			return false;

		_grappleWasEquippedForControlPrompt = true;
		HostSetEventGrappleLoan( true );
		ServerTryAttach( target.WorldPosition + Vector3.Up * 36f, target.Id );
		return IsPlayerGrappleAttach && GrappleAttachPlayerId == target.Id;
	}

	void ClearGrapplePlayerAttachState()
	{
		GrappleAttachPlayerId = Guid.Empty;
		GrappleAttachLocalOffset = default;
	}

}
