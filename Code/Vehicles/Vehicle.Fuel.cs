using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Fuel: the vehicle's storage <see cref="ContainerInventory"/> has one restricted slot
/// (<see cref="ContainerInventory.RestrictedSlotIndex"/>) that only takes <see cref="FuelResourceId"/>; that
/// slot is the tank. The host mirrors its count into <see cref="FuelUnits"/> for everyone. The driver's
/// machine (the simulating owner) runs the burn timer while W or S is held and asks the host to take one
/// unit every <see cref="SecondsPerFuelUnit"/>; the host checks the caller is the driver and commits.
/// </summary>
public sealed partial class Vehicle
{
	[Property, Group( "Fuel" ), Title( "Fuel Resource Id" )]
	public string FuelResourceId { get; set; } = "crafted_gasoline";

	/// <summary>One fuel unit lasts this long of throttle (W or S held).</summary>
	[Property, Group( "Fuel" ), Title( "Seconds Per Fuel Unit" )]
	public float SecondsPerFuelUnit { get; set; } = 60f;

	/// <summary>Units in the tank slot (host mirror of the container).</summary>
	[Sync( SyncFlags.FromHost )]
	public int FuelUnits { get; private set; }

	public bool HasFuel => FuelUnits > 0;

	ContainerInventory _storage;
	float _burnAccumulatorSeconds;

	int FuelSlotIndex => _storage is { IsValid: true } ? _storage.RestrictedSlotIndex : -1;

	void HookFuelStorage()
	{
		_storage = Components.Get<ContainerInventory>( FindMode.EverythingInSelfAndDescendants );
		if ( _storage is null || !_storage.IsValid() )
		{
			Log.Warning( $"[Vehicle] {GameObject.Name}: no ContainerInventory on the prefab — no storage, no fuel." );
			return;
		}

		if ( !HasHostAuthority )
			return;

		_storage.ContentsChanged += RefreshFuelMirror;
		RefreshFuelMirror();
	}

	void UnhookFuelStorage()
	{
		if ( _storage is not null )
			_storage.ContentsChanged -= RefreshFuelMirror;
	}

	void RefreshFuelMirror()
	{
		if ( !HasHostAuthority || _storage is null || !_storage.IsValid() )
			return;

		var slot = _storage.GetSlot( FuelSlotIndex );
		FuelUnits = !slot.IsEmpty && ResourceCatalog.ResourceIdsMatch( slot.ResourceId, FuelResourceId ) ? slot.Count : 0;
	}

	/// <summary>Simulating machine: accumulate throttle time; each full unit is requested from the host.</summary>
	void TickFuelBurn( float dt, bool throttleHeld )
	{
		if ( !throttleHeld || !HasFuel )
			return;

		_burnAccumulatorSeconds += dt;
		if ( _burnAccumulatorSeconds < Math.Max( 1f, SecondsPerFuelUnit ) )
			return;

		_burnAccumulatorSeconds -= Math.Max( 1f, SecondsPerFuelUnit );
		if ( HasHostAuthority )
			HostBurnOneFuel();
		else
			RpcHostBurnFuel();
	}

	[Rpc.Host]
	void RpcHostBurnFuel()
	{
		if ( !Networking.IsHost )
			return;

		// Only the driver's connection may burn fuel.
		var driver = GetOccupant( 0 );
		var driverOwner = driver?.Network?.Owner;
		if ( driverOwner is null || Rpc.Caller is not { } caller || !ConnectionIdentity.SameClient( caller, driverOwner ) )
			return;

		HostBurnOneFuel();
	}

	void HostBurnOneFuel()
	{
		if ( !HasHostAuthority || _storage is null || !_storage.IsValid() )
			return;

		_storage.HostTryRemoveFromSlot( FuelSlotIndex, 1 );
		RefreshFuelMirror();
	}
}
