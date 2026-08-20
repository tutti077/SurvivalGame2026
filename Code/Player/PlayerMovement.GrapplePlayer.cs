using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Player-vs-player grapple: hook other pawns, break their rope, and apply a short re-grapple lockout.
/// Attacker rope state is independent — if the victim hooks a tree, the attacker's hook on them stays.
/// </summary>
partial class PlayerMovement
{
	/// <summary>Seconds after being hooked before this pawn may attach their own grapple again.</summary>
	[Property, Group( "Grapple PvP" ), Title( "Victim Re-grapple Cooldown (s)" ), Range( 0f, 10f ), Step( 0.25f )]
	public float GrappleVictimCooldownSeconds { get; set; } = 3f;

	/// <summary>Host-synced player anchor when <see cref="GrappleAttached"/> targets a pawn; empty = static world point.</summary>
	[Sync( SyncFlags.FromHost )] Guid GrappleAttachPlayerId { get; set; }

	/// <summary>Attach point in the target pawn's local space — follows body motion.</summary>
	[Sync( SyncFlags.FromHost )] Vector3 GrappleAttachLocalOffset { get; set; }

	/// <summary>Host-synced sandbox time after which this pawn may grapple again after being hooked.</summary>
	[Sync( SyncFlags.FromHost )] double GrappleVictimCooldownEndsAt { get; set; }

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

	bool TryFindGrapplePlayerNearPoint( Vector3 point, float radius, out GameObject playerRoot, out Vector3 attachPoint )
	{
		playerRoot = null;
		attachPoint = default;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		var bestDist = float.MaxValue;
		foreach ( var move in scene.GetAllComponents<PlayerMovement>() )
		{
			if ( move is null || !move.GameObject.IsValid() || move.GameObject == GameObject )
				continue;

			if ( !IsGrappleablePlayer( move.GameObject ) )
				continue;

			if ( !TryResolveClosestPointOnObject( move.GameObject, point, out var closest ) )
				continue;

			var dist = Vector3.DistanceBetween( closest, point );
			if ( dist > radius || dist >= bestDist )
				continue;

			bestDist = dist;
			playerRoot = move.GameObject;
			attachPoint = closest;
		}

		return playerRoot is not null;
	}

	void ApplyGrappledByPlayerCooldown( float seconds )
	{
		if ( seconds <= 0f )
			return;

		var end = Time.NowDouble + seconds;
		if ( end > GrappleVictimCooldownEndsAt )
			GrappleVictimCooldownEndsAt = end;
	}

	/// <summary>Host-only: victim loses their rope and cannot re-hook for <see cref="GrappleVictimCooldownSeconds"/>.</summary>
	internal void HostNotifyGrappledByPlayer()
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( GrappleAttached )
			ServerDetach( "grappled by player" );

		ApplyGrappledByPlayerCooldown( GrappleVictimCooldownSeconds );
	}

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
			ServerDetach( "player target down" );
	}

	void ClearGrapplePlayerAttachState()
	{
		GrappleAttachPlayerId = Guid.Empty;
		GrappleAttachLocalOffset = default;
	}

}
