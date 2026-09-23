using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Hammer-placed bed (<c>bed</c> in <c>data/build_pieces.json</c>). Look at it + E to claim it as
/// your respawn point; looking at anyone's bed shows whose it is. One claimed bed per player — a new
/// claim releases the old one. A bed whose owner has left the game can be claimed by someone else.
/// Beds are also what base raids go for (<see cref="BaseRaidSession"/>). The bed is a wood piece, so
/// raiders tear it down like a wall; once it is gone its owner respawns at the scene spawn point again.
/// </summary>
[Title( "Build Bed" )]
public sealed class BuildBed : Component
{
	static readonly List<BuildBed> Registered = new();

	/// <summary>Every bed component on this machine — filter with <see cref="IsStanding"/>.</summary>
	public static IReadOnlyList<BuildBed> All => Registered;

	[Property, Group( "Bed" ), Title( "Use reach (m)" ), Range( 1f, 8f )]
	public float UseReachMeters { get; set; } = 3f;

	/// <summary>
	/// Per Mark: the bed model is too small for a scav swing to connect, so enemies treat the bed as
	/// this cube (build-kit meters — 2 m = one wall module), standing on the bed's floor and centred
	/// on it. A raider in swing reach of the cube is in reach of the bed, and a swing at the bed from
	/// there counts even when the sweep misses the mattress. The model and its collider are unchanged.
	/// </summary>
	[Property, Group( "Bed" ), Title( "Enemy hit zone (m, cube)" ), Range( 0.5f, 4f )]
	public float EnemyHitZoneMeters { get; set; } = 2f;

	/// <summary>Host → everyone: whose respawn point this is (<see cref="TimeTrialSession.ResolvePlayerKey"/>; empty = unclaimed).</summary>
	[Sync( SyncFlags.FromHost )] public Guid OwnerPlayerId { get; private set; }

	/// <summary>Host → everyone: the owner's name at claim time, for the look-at prompt.</summary>
	[Sync( SyncFlags.FromHost )] public string OwnerName { get; private set; } = string.Empty;

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	BuildPiece _piece;

	public BuildPiece Piece => _piece ??= Components.Get<BuildPiece>();

	/// <summary>A real, placed bed that has not been broken — not a hammer ghost.</summary>
	public bool IsStanding =>
		Enabled && GameObject.IsValid() && Piece is { } piece && piece.IsValid()
		&& !piece.IsPreviewGhost && !piece.IsBroken;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !Registered.Contains( this ) )
			Registered.Add( this );
	}

	protected override void OnDisabled()
	{
		Registered.Remove( this );
		base.OnDisabled();
	}

	protected override void OnDestroy()
	{
		Registered.Remove( this );
		base.OnDestroy();
	}

	/// <summary>Look-at prompt for <paramref name="viewer"/>: claim, own bed, or whose it is.</summary>
	public string PromptTextFor( GameObject viewer )
	{
		if ( OwnerPlayerId == default )
			return "Claim Bed (respawn here)";

		if ( OwnerPlayerId == TimeTrialSession.ResolvePlayerKey( viewer ) )
			return "Your Bed";

		var name = string.IsNullOrWhiteSpace( OwnerName ) ? "Someone" : OwnerName;
		return IsOwnerInGame() ? $"{name}'s Bed" : $"Claim {name}'s Bed (respawn here)";
	}

	/// <summary>Would pressing E claim this bed for <paramref name="viewer"/>? (Drives the E key cap too.)</summary>
	public bool CanClaim( GameObject viewer )
	{
		var key = TimeTrialSession.ResolvePlayerKey( viewer );
		if ( key == default || OwnerPlayerId == key )
			return false;

		return OwnerPlayerId == default || !IsOwnerInGame();
	}

	/// <summary>Host: <paramref name="user"/> claims this bed; their previous bed is released.</summary>
	public void HostClaim( GameObject user )
	{
		if ( !HasHostAuthority || !IsStanding || !CanClaim( user ) )
			return;

		var key = TimeTrialSession.ResolvePlayerKey( user );
		foreach ( var bed in Registered )
		{
			if ( bed != this && bed.OwnerPlayerId == key )
				bed.HostRelease();
		}

		OwnerPlayerId = key;
		OwnerName = ResolveDisplayName( user );
		Log.Info( $"[Bed] {OwnerName} claimed {GameObject.Name} as their respawn point." );
	}

	void HostRelease()
	{
		OwnerPlayerId = default;
		OwnerName = string.Empty;
	}

	public bool IsWithinUseReach( GameObject user )
	{
		if ( user is null || !user.IsValid() )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, UseReachMeters ) ) + 48f;
		return (user.WorldPosition - GameObject.WorldPosition).LengthSquared <= reach * reach;
	}

	/// <summary>Distance (units) from <paramref name="worldPoint"/> to the enemy hit zone cube — 0 inside it.</summary>
	public float DistanceToEnemyHitZone( Vector3 worldPoint )
	{
		var unitsPerMeter = BuildColliderSnap.PrefabColliderSize.x;
		var half = Math.Max( 0.5f, EnemyHitZoneMeters ) * unitsPerMeter * 0.5f;
		var floorLocal = Piece is { } piece && piece.IsValid() ? -piece.HalfExtents.z : 0f;
		var local = GameObject.WorldTransform.PointToLocal( worldPoint );
		var clamped = new Vector3(
			Math.Clamp( local.x, -half, half ),
			Math.Clamp( local.y, -half, half ),
			Math.Clamp( local.z, floorLocal, floorLocal + half * 2f ) );
		return (local - clamped).Length;
	}

	/// <summary>Where a player who dies wakes up: on top of the mattress, facing along the bed.</summary>
	public void GetRespawnTransform( out Vector3 position, out Rotation rotation )
	{
		var top = Piece is { } piece && piece.IsValid()
			? BuildPieceGeometry.WorldBounds( piece ).Maxs.z
			: GameObject.WorldPosition.z;
		position = GameObject.WorldPosition.WithZ( top + 4f );
		rotation = Rotation.FromYaw( GameObject.WorldRotation.Angles().yaw );
	}

	/// <summary>Host: the standing bed <paramref name="pawn"/> has claimed, if any.</summary>
	public static bool TryFindClaimedBy( GameObject pawn, out BuildBed bed )
	{
		bed = null;
		var key = TimeTrialSession.ResolvePlayerKey( pawn );
		if ( key == default )
			return false;

		foreach ( var candidate in Registered )
		{
			if ( candidate.OwnerPlayerId == key && candidate.IsStanding )
			{
				bed = candidate;
				return true;
			}
		}

		return false;
	}

	/// <summary>The standing bed nearest to <paramref name="position"/> (any owner), if any.</summary>
	public static BuildBed FindNearest( Vector3 position )
	{
		BuildBed best = null;
		var bestDistSq = float.MaxValue;
		foreach ( var bed in Registered )
		{
			if ( !bed.IsStanding )
				continue;

			var distSq = (bed.GameObject.WorldPosition - position).LengthSquared;
			if ( distSq >= bestDistSq )
				continue;

			bestDistSq = distSq;
			best = bed;
		}

		return best;
	}

	/// <summary>Bed under the crosshair within reach (same look trace as traps and doors).</summary>
	public static bool TryFindFocusedBed( GameObject viewer, float reachMeters, out BuildBed bed )
	{
		bed = null;
		if ( viewer is null || !viewer.IsValid() )
			return false;

		if ( !BuildViewCamera.TryGetViewRay( viewer, out var origin, out var direction ) )
			return false;

		var scene = viewer.Scene.IsValid() ? viewer.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, reachMeters ) );
		var tr = scene.Trace.Ray( origin, origin + direction * reach )
			.IgnoreGameObjectHierarchy( viewer )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return false;

		for ( var go = tr.GameObject; go.IsValid(); go = go.Parent )
		{
			var b = go.Components.Get<BuildBed>();
			if ( b is not null && b.IsStanding )
			{
				bed = b;
				return true;
			}
		}

		return false;
	}

	/// <summary>Is the owner's pawn still in the game? (Connection-keyed — a reconnect is a new player.)</summary>
	bool IsOwnerInGame()
	{
		if ( OwnerPlayerId == default )
			return false;

		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;

		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals?.GameObject is { IsValid: true } root
			     && TimeTrialSession.ResolvePlayerKey( root ) == OwnerPlayerId )
				return true;
		}

		return false;
	}

	static string ResolveDisplayName( GameObject user )
	{
		var connection = user?.Network is { Active: true, Owner: { } owner } ? owner : Connection.Local;
		var name = connection?.DisplayName;
		if ( string.IsNullOrWhiteSpace( name ) )
			name = connection?.Name;
		return string.IsNullOrWhiteSpace( name ) ? "Someone" : name;
	}
}
