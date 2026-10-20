using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>How a <see cref="Vehicle"/> moves: four raycast wheels (dune buggy) or a hover cushion (platform).</summary>
public enum VehicleDriveModel
{
	Wheels,
	Hover,
}

/// <summary>
/// A drivable vehicle (the dune buggy; the hover platform). Placed with the build hammer like a piece
/// (<c>"vehicle": true</c> in <c>build_pieces.json</c>) but it is NOT a <see cref="BuildPiece"/>: it is a
/// host-spawned networked <see cref="Rigidbody"/> with raycast suspension (<c>Vehicle.Physics.cs</c>),
/// seats (<see cref="VehicleSeat"/> children — first in is the driver, the rest are passengers, see
/// <c>Vehicle.Seats.cs</c>), a storage <see cref="ContainerInventory"/> whose restricted slot is the fuel
/// tank (<c>Vehicle.Fuel.cs</c>) and hit points (<c>Vehicle.Health.cs</c>).
///
/// Who simulates: the host, until a driver sits down — then the host hands network ownership of the
/// vehicle to the driver's connection so WASD drives it with no round trip, and takes it back when
/// they leave. Seated pawns are parented under their seat (see <c>PlayerMovement.Vehicle.cs</c>).
/// </summary>
[Title( "Vehicle" ), Category( "Vehicles" ), Icon( "directions_car" )]
public sealed partial class Vehicle : Component
{
	[Property, Group( "Vehicle" ), Title( "Display Name" )]
	public string DisplayName { get; set; } = "Dune Buggy";

	/// <summary>Wheels = the buggy's raycast suspension (<c>Vehicle.Physics.cs</c>); Hover = the platform's cushion (<c>Vehicle.Hover.cs</c>).</summary>
	[Property, Group( "Vehicle" ), Title( "Drive Model" )]
	public VehicleDriveModel DriveModel { get; set; } = VehicleDriveModel.Wheels;

	/// <summary>Build pieces placed on this hull are parented under it and ride along (the hover platform's deck). See <c>BuildAuthority.HostPlacePiece</c>.</summary>
	[Property, Group( "Vehicle" ), Title( "Buildable Deck" )]
	public bool BuildableDeck { get; set; }

	/// <summary>Map pin drawn live at the vehicle's position for every player (empty = none). "platform" or any <see cref="MapPinCatalog"/> icon id.</summary>
	[Property, Group( "Vehicle" ), Title( "Map Pin Icon" )]
	public string MapPinIcon { get; set; } = string.Empty;

	public bool IsHover => DriveModel == VehicleDriveModel.Hover;
	public bool ShowsMapPin => !string.IsNullOrWhiteSpace( MapPinIcon );

	/// <summary>How close a pawn must stand to press E (meters).</summary>
	[Property, Group( "Vehicle" ), Title( "Use Reach (m)" )]
	public float UseReachMeters { get; set; } = 3f;

	/// <summary>Where a pawn is put down when it leaves: this far out from its seat, to the seat's side (meters).</summary>
	[Property, Group( "Vehicle" ), Title( "Exit Side Distance (m)" )]
	public float ExitSideMeters { get; set; } = 1.6f;

	/// <summary>The tag on the storage box child: E on that part opens the chest instead of entering.</summary>
	public const string StorageTag = "vehiclestorage";
	/// <summary>Root tag: the nav baker leaves vehicles alone (they move).</summary>
	public const string VehicleTag = "vehicle";

	public bool HasHostAuthority => GameObject.Network is not { Active: true } || Networking.IsHost;

	/// <summary>Build-hammer ghost clone of the prefab: no physics, no seats, no fuel.</summary>
	public bool IsPreviewGhost => GameObject.Tags.Has( "buildpreview" );

	/// <summary>Every vehicle in the scene (OnEnabled/OnDisabled), for focus traces and host sweeps.</summary>
	public static readonly List<Vehicle> Registered = new();

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !Registered.Contains( this ) )
			Registered.Add( this );
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();
		Registered.Remove( this );
	}

	protected override void OnStart()
	{
		base.OnStart();
		if ( IsPreviewGhost )
			return;

		CollectSeats();
		CollectWheelVisuals();
		HostSeedHealth();
		HookFuelStorage();
		HostSettleOnGround();
	}

	/// <summary>
	/// Host, once at spawn: stand the chassis on its springs. The hammer drops the origin at the ghost
	/// box's half height, which is not where a sprung chassis rests, and a chassis that starts inside the
	/// ground never gets a suspension ray out of it.
	/// </summary>
	void HostSettleOnGround()
	{
		if ( !HasHostAuthority )
			return;

		var restHeight = TerrainWorldUnits.MetersToEngine( IsHover ? HoverHullBottomMeters + HoverMinHeightMeters : SuspensionLengthMeters + WheelRadiusMeters );
		var origin = WorldPosition;
		var tr = Scene.Trace.Ray( origin + Vector3.Up * 400f, origin - Vector3.Up * 400f )
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "player", "trigger", "worlddrop" )
			.Run();
		if ( !tr.Hit )
			return;

		WorldPosition = tr.HitPosition + Vector3.Up * restHeight;
		GameObject.Transform.ClearInterpolation();
		var body = Components.Get<Rigidbody>();
		if ( body is not null && body.IsValid() )
		{
			body.Velocity = Vector3.Zero;
			body.AngularVelocity = Vector3.Zero;
		}
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();
		UnhookFuelStorage();
	}

	public bool IsWithinUseReach( GameObject user )
	{
		if ( user is null || !user.IsValid() )
			return false;

		// Vehicles are big: measure from the closest point of the chassis box, not the origin.
		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, UseReachMeters ) ) + 48f;
		var bounds = GameObject.GetBounds();
		var closest = bounds.ClosestPoint( user.WorldPosition );
		return Vector3.DistanceBetween( closest, user.WorldPosition ) <= reach + TerrainWorldUnits.MetersToEngine( 1.5f );
	}

	/// <summary>
	/// Vehicle under the viewer's crosshair within <paramref name="reachMeters"/>. A hit on the storage box
	/// (<see cref="StorageTag"/>) is not a vehicle focus — that part is the chest.
	/// </summary>
	public static bool TryFindFocusedVehicle( GameObject viewer, float reachMeters, out Vehicle vehicle )
	{
		vehicle = null;
		if ( viewer is null || !viewer.IsValid() )
			return false;

		var scene = viewer.Scene;
		if ( !scene.IsValid() )
			return false;

		if ( !BuildViewCamera.TryGetViewRay( viewer, out var origin, out var dir ) )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, reachMeters ) );
		var tr = scene.Trace.Ray( origin, origin + dir * reach )
			.IgnoreGameObjectHierarchy( viewer.Root )
			.Run();

		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
			return false;

		if ( IsStorageHit( tr.GameObject, tr.HitPosition ) )
			return false;

		// A wall built on the platform's deck is that wall, not the platform: FindOnHierarchy stops at it.
		var v = FindOnHierarchy( tr.GameObject );
		if ( v is null || !v.IsValid() || v.IsPreviewGhost )
			return false;

		vehicle = v;
		return true;
	}

	/// <summary>Is this hit object the vehicle's storage box (or inside it)? Stops at the vehicle root.</summary>
	public static bool IsStorageHit( GameObject hit, Vector3 hitPosition )
	{
		Vehicle vehicle = null;
		for ( var go = hit; go is not null && go.IsValid(); go = go.Parent )
		{
			if ( go.Tags.Has( StorageTag ) )
				return true;
			vehicle = go.Components.Get<Vehicle>();
			if ( vehicle is not null )
				break;
		}

		// The storage box is a child collider of the chassis rigidbody and a trace against that compound
		// body reports the root object, so decide by where the ray landed instead.
		return vehicle is not null && vehicle.IsValid() && vehicle.IsInsideStorageBox( hitPosition );
	}

	GameObject _storageBox;

	/// <summary>The storage box child (<see cref="StorageTag"/>): what chest reach / keep-open distances measure to, not the root.</summary>
	public GameObject StorageBox
	{
		get
		{
			if ( _storageBox is { IsValid: true } )
				return _storageBox;

			foreach ( var child in GameObject.GetAllObjects( true ) )
			{
				if ( child.Tags.Has( StorageTag ) )
				{
					_storageBox = child;
					break;
				}
			}
			return _storageBox;
		}
	}

	/// <summary>Is this world point on / inside the storage box child, with 2 u of slack?</summary>
	public bool IsInsideStorageBox( Vector3 worldPosition )
	{
		if ( StorageBox is null )
			return false;

		var box = _storageBox.Components.Get<BoxCollider>();
		if ( box is null || !box.IsValid() )
			return false;

		var local = _storageBox.WorldTransform.PointToLocal( worldPosition ) - box.Center;
		var half = box.Scale * 0.5f + 2f;
		return MathF.Abs( local.x ) <= half.x && MathF.Abs( local.y ) <= half.y && MathF.Abs( local.z ) <= half.z;
	}

	/// <summary>
	/// The vehicle this object belongs to — its own hull, seats, storage box, wheels. A <see cref="BuildPiece"/>
	/// met on the way up is a boundary: pieces built on a deck (<see cref="BuildableDeck"/>) are parented under
	/// the vehicle so they ride with it, but they are their own thing for prompts, damage and containers.
	/// </summary>
	public static Vehicle FindOnHierarchy( GameObject hit )
	{
		for ( var go = hit; go is not null && go.IsValid(); go = go.Parent )
		{
			if ( go.Components.Get<BuildPiece>() is not null )
				return null;
			var v = go.Components.Get<Vehicle>();
			if ( v is not null )
				return v;
		}

		return null;
	}

	/// <summary>The buildable-deck vehicle this object (a build piece, or a part of the hull) is carried by, through any pieces between.</summary>
	public static Vehicle FindDeckOnHierarchy( GameObject go )
	{
		for ( var current = go; current is not null && current.IsValid(); current = current.Parent )
		{
			var v = current.Components.Get<Vehicle>();
			if ( v is not null )
				return v.BuildableDeck && !v.IsPreviewGhost ? v : null;
		}

		return null;
	}

	/// <summary>Is this build piece parented under a buildable deck (rides a vehicle)?</summary>
	public static bool IsDeckMounted( GameObject pieceRoot )
		=> pieceRoot is { IsValid: true } && pieceRoot.Parent is { } parent && FindDeckOnHierarchy( parent ) is not null;

	/// <summary>Probe reach below a freshly placed piece's lowest corners when looking for a deck under it (units, 0.4 m).</summary>
	const float DeckProbeUnits = 16f;

	/// <summary>
	/// Host, once per placement: the buildable deck a just-spawned piece stands on — a short ray down from
	/// each of its lowest corners (and a copy pulled toward the centre), the way the structural solver
	/// probes for ground. A piece that rests on a deck, or on a piece already riding one, finds it; a piece
	/// that only touches a deck piece sideways (a wall hung off the edge) is found through a box overlap.
	/// Null when the piece stands on the world.
	/// </summary>
	public static Vehicle FindBuildHostUnder( Scene scene, GameObject pieceRoot, string pieceId )
	{
		if ( !scene.IsValid() || pieceRoot is null || !pieceRoot.IsValid() || Registered.Count == 0 )
			return null;

		var anyDeck = false;
		for ( var i = 0; i < Registered.Count; i++ )
		{
			if ( Registered[i] is { IsValid: true, BuildableDeck: true } )
			{
				anyDeck = true;
				break;
			}
		}
		if ( !anyDeck )
			return null;

		var rotation = BuildColliderSnap.GetSnapWorldRotation( pieceRoot, pieceId );
		var origin = pieceRoot.WorldPosition;
		var half = BuildColliderSnap.GetColliderHalfForPiece( pieceId );

		var corners = new Vector3[8];
		var index = 0;
		var minZ = float.MaxValue;
		for ( var xi = -1; xi <= 1; xi += 2 )
		for ( var yi = -1; yi <= 1; yi += 2 )
		for ( var zi = -1; zi <= 1; zi += 2 )
		{
			var corner = origin + rotation * new Vector3( xi * half.x, yi * half.y, zi * half.z );
			corners[index++] = corner;
			minZ = MathF.Min( minZ, corner.z );
		}

		for ( var i = 0; i < 8; i++ )
		{
			var corner = corners[i];
			if ( corner.z > minZ + 6f )
				continue;

			var deck = ProbeDeckBelow( scene, pieceRoot, corner );
			if ( deck is not null )
				return deck;

			var pulled = new Vector3( corner.x + ( origin.x - corner.x ) * 0.25f, corner.y + ( origin.y - corner.y ) * 0.25f, corner.z );
			deck = ProbeDeckBelow( scene, pieceRoot, pulled );
			if ( deck is not null )
				return deck;
		}

		// Side-snapped to a piece that rides a deck: whatever this piece overlaps (with the solver's contact slack).
		var bounds = BBox.FromPositionAndSize( origin, 1f );
		for ( var i = 0; i < 8; i++ )
			bounds = bounds.AddPoint( corners[i] );
		bounds = bounds.Grow( 6f );
		foreach ( var go in scene.FindInPhysics( bounds ) )
		{
			if ( go is null || !go.IsValid() || go.Root == pieceRoot.Root )
				continue;
			if ( go.Components.Get<BuildPiece>( FindMode.EverythingInSelfAndAncestors ) is null )
				continue;
			var deck = FindDeckOnHierarchy( go );
			if ( deck is not null )
				return deck;
		}

		return null;
	}

	static Vehicle ProbeDeckBelow( Scene scene, GameObject pieceRoot, Vector3 point )
	{
		var tr = scene.Trace.Ray( point + Vector3.Up * 4f, point - Vector3.Up * DeckProbeUnits )
			.IgnoreGameObjectHierarchy( pieceRoot )
			.WithoutTags( "player", "trigger", "worlddrop", "buildpreview" )
			.Run();
		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
			return null;

		return FindDeckOnHierarchy( tr.GameObject );
	}
}
