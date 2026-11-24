using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Hammer-placed bed (<c>bed</c> in <c>data/build_pieces.json</c>). Look at it + E: an unclaimed bed becomes
/// yours (your respawn point) and you climb in; your own bed you climb into; someone else's only says whose it
/// is. One claimed bed per player — a new claim releases the old one. A bed whose owner has left the game can be
/// claimed by someone else. Lying in it is the pawn's bed lock (<c>PlayerMovement.Bed.cs</c>); once every player
/// in the world has lain for <see cref="SleepDelaySeconds"/> the day cycle runs fast until 06:00
/// (<see cref="EnvironmentDayNightCycle"/>). A dead player wakes standing beside the bed, on the mattress when
/// there is no room (<see cref="GetRespawnTransform"/>).
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

	[Property, Group( "Bed" ), Title( "Sleep delay (s)" ), Range( 0f, 30f ), Description( "How long a player lies in the bed before they count as asleep." )]
	public float SleepDelaySeconds { get; set; } = 5f;

	[Property, Group( "Bed" ), Title( "Fade to black (s)" ), Range( 0.1f, 10f ), Description( "How long the screen takes to go black once everyone is asleep (and to clear again on waking)." )]
	public float SleepFadeSeconds { get; set; } = 3f;

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
		// A bed that is broken or picked up puts its sleeper back on their feet where they lie.
		if ( HasHostAuthority )
			PlayerMovement.HostEjectFromBed( GameObject.Id );
		Registered.Remove( this );
		base.OnDisabled();
	}

	protected override void OnDestroy()
	{
		Registered.Remove( this );
		base.OnDestroy();
	}

	/// <summary>Look-at prompt for <paramref name="viewer"/>: claim and sleep, sleep in your own bed, or whose it is.</summary>
	public string PromptTextFor( GameObject viewer )
	{
		if ( OwnerPlayerId == default )
			return "Claim Bed and Sleep";

		if ( OwnerPlayerId == TimeTrialSession.ResolvePlayerKey( viewer ) )
			return "Sleep in Your Bed";

		var name = string.IsNullOrWhiteSpace( OwnerName ) ? "Someone" : OwnerName;
		return IsOwnerInGame() ? $"{name}'s Bed" : $"Claim {name}'s Bed and Sleep";
	}

	/// <summary>Would pressing E claim this bed for <paramref name="viewer"/>? (Unclaimed, or its owner has left.)</summary>
	public bool CanClaim( GameObject viewer )
	{
		var key = TimeTrialSession.ResolvePlayerKey( viewer );
		if ( key == default || OwnerPlayerId == key )
			return false;

		return OwnerPlayerId == default || !IsOwnerInGame();
	}

	/// <summary>Would pressing E do something for <paramref name="viewer"/>: their own bed, or one they can claim? (Drives the E key cap too.)</summary>
	public bool CanUse( GameObject viewer )
	{
		var key = TimeTrialSession.ResolvePlayerKey( viewer );
		if ( key == default )
			return false;

		return OwnerPlayerId == key || CanClaim( viewer );
	}

	/// <summary>
	/// Host: <paramref name="user"/> pressed E on this bed. A bed they can claim becomes theirs, then they climb
	/// into it; a bed that belongs to a player still in the game is refused.
	/// </summary>
	public void HostUse( GameObject user )
	{
		if ( !HasHostAuthority || !IsStanding || user is null || !user.IsValid() )
			return;

		if ( CanClaim( user ) )
			HostClaim( user );

		if ( OwnerPlayerId == default || OwnerPlayerId != TimeTrialSession.ResolvePlayerKey( user ) )
			return;

		user.Components.Get<PlayerMovement>()?.HostEnterBed( this );
	}

	/// <summary>Host: <paramref name="user"/> claims this bed; their previous bed is released.</summary>
	void HostClaim( GameObject user )
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
		var half = Math.Max( 0.5f, EnemyHitZoneMeters ) * BuildModuleDimensions.UnitsPerMeter * 0.5f;
		var floorLocal = Piece is { } piece && piece.IsValid() ? -piece.HalfExtents.z : 0f;
		var local = GameObject.WorldTransform.PointToLocal( worldPoint );
		var clamped = new Vector3(
			Math.Clamp( local.x, -half, half ),
			Math.Clamp( local.y, -half, half ),
			Math.Clamp( local.z, floorLocal, floorLocal + half * 2f ) );
		return (local - clamped).Length;
	}

	// ---- lying frame (the root box collider is the bed's real solid; read once, it never changes) ----

	bool _lieFrameReady;
	Vector3 _lieLocalTopCentre;
	/// <summary>Local unit axis from the foot of the bed to the pillow.</summary>
	Vector3 _lieLocalHead = Vector3.Left;
	float _lieHalfLength = 40f;
	float _lieHalfWidth = 20f;
	float _lieHeight = 20f;

	void EnsureLieFrame()
	{
		if ( _lieFrameReady )
			return;

		_lieFrameReady = true;
		var box = Components.Get<BoxCollider>( FindMode.EverythingInSelf );
		if ( box is null || !box.IsValid() )
			return;

		var size = box.Scale;
		var alongY = size.y >= size.x;
		_lieHalfLength = (alongY ? size.y : size.x) * 0.5f;
		_lieHalfWidth = (alongY ? size.x : size.y) * 0.5f;
		_lieHeight = size.z;
		_lieLocalTopCentre = box.Center + Vector3.Up * (size.z * 0.5f);
		_lieLocalHead = alongY ? Vector3.Left : Vector3.Forward;

		// The pillow says which end the head goes.
		foreach ( var child in GameObject.Children )
		{
			if ( child is null || !child.IsValid() || !child.Name.Contains( "pillow", StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( Vector3.Dot( child.LocalPosition - box.Center, _lieLocalHead ) < 0f )
				_lieLocalHead = -_lieLocalHead;
			break;
		}
	}

	/// <summary>Flat world direction from the foot of the bed to the pillow.</summary>
	Vector3 HeadDirection()
	{
		EnsureLieFrame();
		var head = (GameObject.WorldRotation * _lieLocalHead).WithZ( 0f );
		return head.LengthSquared < 1e-4f ? Vector3.Forward : head.Normal;
	}

	/// <summary>World point at the middle of the mattress top — where the pawn's root rests while it lies in the bed.</summary>
	public Vector3 GetMattressTopCentre()
	{
		EnsureLieFrame();
		return GameObject.WorldTransform.PointToWorld( _lieLocalTopCentre );
	}

	/// <summary>The lying body's spine sits this far above the mattress (half a torso's depth, world units).</summary>
	const float LieBackLiftUnits = 5f;

	/// <summary>How far past the bed's edge a standing spot is tried (pawn radius plus a little air, world units).</summary>
	const float StandClearanceUnits = 26f;

	/// <summary>
	/// World pose of a body lying on its back on the mattress, head on the pillow: the body's feet-to-head axis runs
	/// along the bed and its front faces up. <paramref name="bodyHeight"/> is the standing height in world units.
	/// </summary>
	public void GetLieBodyTransform( float bodyHeight, out Vector3 feetPosition, out Rotation rotation )
	{
		var head = HeadDirection();
		rotation = Rotation.LookAt( Vector3.Up, head );
		feetPosition = GetMattressTopCentre() - head * (bodyHeight * 0.5f) + Vector3.Up * LieBackLiftUnits;
	}

	/// <summary>
	/// A free standing spot beside the bed: each long side, then the foot, then the head. A spot counts when nothing
	/// stands between the bed and it (a wall the bed is pushed against), a walkable floor lies under it no more than
	/// 1.5 m below the bed, and a pawn fits standing there.
	/// </summary>
	public bool TryFindStandSpot( out Vector3 feet )
	{
		feet = default;

		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;

		var head = HeadDirection();
		var side = Vector3.Cross( Vector3.Up, head ).Normal;
		var from = GetMattressTopCentre() + Vector3.Up * 24f;

		Span<Vector3> tries = stackalloc Vector3[4];
		tries[0] = side * (_lieHalfWidth + StandClearanceUnits);
		tries[1] = -side * (_lieHalfWidth + StandClearanceUnits);
		tries[2] = -head * (_lieHalfLength + StandClearanceUnits);
		tries[3] = head * (_lieHalfLength + StandClearanceUnits);

		for ( var i = 0; i < tries.Length; i++ )
		{
			var hand = from + tries[i];
			var line = scene.Trace.Ray( from, hand )
				.IgnoreGameObjectHierarchy( GameObject )
				.WithoutTags( "player", "trigger", "worlddrop" )
				.Run();
			if ( line.Hit )
				continue;

			var floor = scene.Trace.Ray( hand, hand - Vector3.Up * (24f + _lieHeight + 60f) )
				.IgnoreGameObjectHierarchy( GameObject )
				.WithoutTags( "player", "trigger", "worlddrop" )
				.Run();
			if ( !floor.Hit || floor.StartedSolid || floor.Normal.z < 0.5f )
				continue;

			var candidate = floor.HitPosition + Vector3.Up * 2f;
			if ( !IsStandingRoomClear( scene, candidate ) )
				continue;

			feet = candidate;
			return true;
		}

		return false;
	}

	/// <summary>Two sphere overlaps where a standing pawn's legs and chest would be (the vehicle exit's test).</summary>
	static bool IsStandingRoomClear( Scene scene, Vector3 feet )
	{
		const float radius = 14f;
		for ( var h = 18f; h <= 52f; h += 34f )
		{
			var at = feet + Vector3.Up * h;
			var overlap = scene.Trace.Sphere( radius, at, at )
				.WithoutTags( "player", "trigger", "worlddrop" )
				.Run();
			if ( overlap.Hit && overlap.StartedSolid )
				return false;
		}

		return true;
	}

	/// <summary>
	/// Where a player gets out of the bed, and where one who dies wakes up: standing beside it, facing along the
	/// bed; on top of the mattress when no side has room.
	/// </summary>
	public void GetRespawnTransform( out Vector3 position, out Rotation rotation )
	{
		rotation = Rotation.FromYaw( GameObject.WorldRotation.Angles().yaw );
		if ( TryFindStandSpot( out position ) )
			return;

		position = GetMattressTopCentre() + Vector3.Up * 4f;
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
