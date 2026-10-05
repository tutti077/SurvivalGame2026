using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Look at a <see cref="Vehicle"/>: the prompt says "Drive Dune Buggy" / "Ride in Dune Buggy" / "… is
/// full"; E gets in (first in drives). A rolled vehicle reads "Flip Dune Buggy" and E pushes it toward
/// upright (<see cref="Vehicle.HostTryFlip"/>). While seated, E gets out. Looking at the vehicle's storage box
/// is the chest, not the vehicle (see <see cref="Vehicle.StorageTag"/>). Owner traces and sends intent;
/// the host re-checks reach once and commits through <see cref="Vehicle.HostTryEnter"/> /
/// <see cref="Vehicle.HostExit"/>.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Vehicle" ), Title( "Use Action" )]
	public string VehicleUseAction { get; set; } = "Use";

	[Property, Group( "Vehicle" ), Title( "Focus Scan Interval (seconds)" )]
	public float VehicleFocusScanIntervalSeconds { get; set; } = 0.15f;

	/// <summary>Vehicle under the crosshair (drives the enter prompt). Null while seated.</summary>
	public Vehicle FocusedVehicle { get; private set; }

	public event Action FocusedVehicleChanged;

	double _nextVehicleFocusScanAt;
	PlayerMovement _vehicleMovement;

	/// <summary>The local pawn sits in a vehicle (E exits; chests, beds, doors are not reachable from a seat).</summary>
	public bool IsSeatedInVehicle
	{
		get
		{
			_vehicleMovement ??= Components.Get<PlayerMovement>();
			return _vehicleMovement is { IsSeated: true };
		}
	}

	void TickVehicleAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		var pressed = !menuOpen && Input.Pressed( VehicleUseAction );

		if ( IsSeatedInVehicle )
		{
			SetFocusedVehicle( null );
			if ( pressed )
				OwnerExitVehicle();
			return;
		}

		TickVehicleFocusPrompt( menuOpen, force: pressed );

		if ( !pressed )
			return;

		if ( IsBuildHammerPreviewing() || IsGrappleRetractActive() )
			return;

		if ( FocusedVehicle is null || !FocusedVehicle.IsValid() )
			return;

		if ( FocusedVehicle.CanFlip( GameObject ) )
		{
			OwnerFlipVehicle( FocusedVehicle );
			return;
		}

		if ( FocusedVehicle.CanEnter( GameObject ) )
			OwnerEnterVehicle( FocusedVehicle );
	}

	void TickVehicleFocusPrompt( bool menuOpen, bool force = false )
	{
		if ( FocusedVehicle is not null && !FocusedVehicle.IsValid() )
			SetFocusedVehicle( null );

		if ( menuOpen || IsBuildHammerPreviewing() )
		{
			SetFocusedVehicle( null );
			return;
		}

		if ( !force && Time.NowDouble < _nextVehicleFocusScanAt )
			return;

		_nextVehicleFocusScanAt = Time.NowDouble + Math.Max( 0.05, VehicleFocusScanIntervalSeconds );

		// The storage box on the back is a chest: that prompt wins, as do the other focused things.
		if ( FocusedContainer is not null || FocusedAugmentStation is not null || FocusedWorkbench is not null
		     || FocusedCampfire is not null || FocusedDoor is not null || FocusedTrap is not null )
		{
			SetFocusedVehicle( null );
			return;
		}

		var reach = FocusedVehicle is { IsValid: true } ? FocusedVehicle.UseReachMeters : 3f;
		if ( Vehicle.TryFindFocusedVehicle( GameObject, reach, out var vehicle ) )
			SetFocusedVehicle( vehicle );
		else
			SetFocusedVehicle( null );
	}

	void SetFocusedVehicle( Vehicle vehicle )
	{
		if ( ReferenceEquals( FocusedVehicle, vehicle ) )
			return;

		FocusedVehicle = vehicle;
		FocusedVehicleChanged?.Invoke();
	}

	void OwnerEnterVehicle( Vehicle vehicle )
	{
		if ( vehicle is null || !vehicle.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			vehicle.HostTryEnter( GameObject );
			return;
		}

		RpcHostEnterVehicle( vehicle.GameObject.Id );
	}

	void OwnerFlipVehicle( Vehicle vehicle )
	{
		if ( vehicle is null || !vehicle.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			vehicle.HostTryFlip( GameObject );
			return;
		}

		RpcHostFlipVehicle( vehicle.GameObject.Id );
	}

	void OwnerExitVehicle()
	{
		_vehicleMovement ??= Components.Get<PlayerMovement>();
		if ( _vehicleMovement is null || !_vehicleMovement.IsSeated )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			Vehicle.HostEjectIfSeated( GameObject );
			return;
		}

		RpcHostExitVehicle();
	}

	[Rpc.Host]
	void RpcHostEnterVehicle( Guid vehicleRootId )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		var vehicle = Scene.Directory.FindByGuid( vehicleRootId )?.Components.Get<Vehicle>();
		if ( vehicle is null || !vehicle.IsValid() )
			return;

		// Client sent intent; the host validates reach once — no boarding from across the map.
		if ( !vehicle.IsWithinUseReach( GameObject ) )
			return;

		vehicle.HostTryEnter( GameObject );
	}

	[Rpc.Host]
	void RpcHostFlipVehicle( Guid vehicleRootId )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		var vehicle = Scene.Directory.FindByGuid( vehicleRootId )?.Components.Get<Vehicle>();
		if ( vehicle is null || !vehicle.IsValid() || !vehicle.IsWithinUseReach( GameObject ) )
			return;

		vehicle.HostTryFlip( GameObject );
	}

	[Rpc.Host]
	void RpcHostExitVehicle()
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		Vehicle.HostEjectIfSeated( GameObject );
	}
}
