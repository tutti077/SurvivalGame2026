using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// A drivable vehicle (first one: the dune buggy). Placed with the build hammer like a piece
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

		var restHeight = TerrainWorldUnits.MetersToEngine( SuspensionLengthMeters + WheelRadiusMeters );
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

		for ( var go = tr.GameObject; go is not null && go.IsValid(); go = go.Parent )
		{
			var v = go.Components.Get<Vehicle>();
			if ( v is not null && v.IsValid() && !v.IsPreviewGhost )
			{
				vehicle = v;
				return true;
			}
		}

		return false;
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

	public static Vehicle FindOnHierarchy( GameObject hit )
	{
		for ( var go = hit; go is not null && go.IsValid(); go = go.Parent )
		{
			var v = go.Components.Get<Vehicle>();
			if ( v is not null )
				return v;
		}

		return null;
	}
}
