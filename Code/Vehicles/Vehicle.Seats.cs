using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Seats: occupancy is a host-synced list of pawn ids, one per <see cref="VehicleSeat"/> child ordered by
/// <see cref="VehicleSeat.SeatIndex"/>. Entering takes the lowest free seat, so the first pawn in is the
/// driver (seat 0) and gets network ownership of the vehicle; everyone after is a passenger. The pawn
/// side of sitting (parenting, physics off, pose) is <c>PlayerMovement.Vehicle.cs</c>. Reused by any
/// later vehicle: author seats on the prefab and the rest follows.
/// </summary>
public sealed partial class Vehicle
{
	/// <summary>Pawn root id per seat index (<see cref="Guid.Empty"/> = free). Host writes.</summary>
	[Sync( SyncFlags.FromHost )]
	public NetList<Guid> SeatOccupants { get; set; } = new();

	readonly List<VehicleSeat> _seats = new();
	double _nextOccupancySweepAt;

	public IReadOnlyList<VehicleSeat> Seats => _seats;
	public int SeatCount => _seats.Count;

	void CollectSeats()
	{
		_seats.Clear();
		foreach ( var seat in Components.GetAll<VehicleSeat>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( seat is not null && seat.IsValid() )
				_seats.Add( seat );
		}

		_seats.Sort( ( a, b ) => a.SeatIndex.CompareTo( b.SeatIndex ) );

		if ( HasHostAuthority )
		{
			while ( SeatOccupants.Count < _seats.Count )
				SeatOccupants.Add( Guid.Empty );
		}
	}

	public GameObject GetSeatObject( int seatIndex )
	{
		if ( _seats.Count == 0 )
			CollectSeats();
		return seatIndex >= 0 && seatIndex < _seats.Count ? _seats[seatIndex].GameObject : null;
	}

	public Guid GetOccupantId( int seatIndex )
		=> seatIndex >= 0 && seatIndex < SeatOccupants.Count ? SeatOccupants[seatIndex] : Guid.Empty;

	public GameObject GetOccupant( int seatIndex )
	{
		var id = GetOccupantId( seatIndex );
		return id == Guid.Empty ? null : Scene.Directory.FindByGuid( id );
	}

	public int GetSeatIndexOf( Guid pawnId )
	{
		if ( pawnId == Guid.Empty )
			return -1;
		for ( var i = 0; i < SeatOccupants.Count; i++ )
		{
			if ( SeatOccupants[i] == pawnId )
				return i;
		}

		return -1;
	}

	public int FindFreeSeatIndex()
	{
		for ( var i = 0; i < SeatOccupants.Count; i++ )
		{
			if ( SeatOccupants[i] == Guid.Empty && i < _seats.Count )
				return i;
		}

		return -1;
	}

	public bool IsFull => FindFreeSeatIndex() < 0;
	public bool HasDriver => GetOccupantId( 0 ) != Guid.Empty;

	/// <summary>HUD prompt for a viewer looking at the vehicle.</summary>
	public string PromptTextFor( GameObject viewer )
	{
		var name = string.IsNullOrWhiteSpace( DisplayName ) ? "Vehicle" : DisplayName;
		if ( IsBroken )
			return $"{name} (wrecked)";
		if ( IsFlipped )
			return $"Flip {name}";
		if ( IsFull )
			return $"{name} is full";
		return HasDriver ? $"Ride in {name}" : $"Drive {name}";
	}

	/// <summary>Can this viewer press E to get in right now?</summary>
	public bool CanEnter( GameObject viewer )
		=> !IsBroken && !IsFlipped && !IsFull && viewer is { IsValid: true }
		   && viewer.Components.Get<PlayerMovement>() is { IsSeated: false };

	/// <summary>Host: seat the pawn in the lowest free seat. Seat 0 also takes the vehicle's network ownership.</summary>
	public bool HostTryEnter( GameObject pawn )
	{
		if ( !HasHostAuthority || IsPreviewGhost || IsBroken || IsFlipped )
			return false;
		if ( pawn is null || !pawn.IsValid() )
			return false;

		var movement = pawn.Components.Get<PlayerMovement>();
		if ( movement is null || movement.IsSeated )
			return false;

		if ( _seats.Count == 0 )
			CollectSeats();
		if ( GetSeatIndexOf( pawn.Id ) >= 0 )
			return false;

		var seat = FindFreeSeatIndex();
		if ( seat < 0 )
			return false;

		SeatOccupants[seat] = pawn.Id;
		movement.HostSetSeated( this, seat, default );

		if ( seat == 0 )
			HostAssignDriverOwnership( pawn );

		return true;
	}

	/// <summary>Host: put the pawn down beside its seat and free the seat. The driver leaving hands ownership back to the host.</summary>
	public bool HostExit( GameObject pawn )
	{
		if ( !HasHostAuthority || pawn is null || !pawn.IsValid() )
			return false;

		var seat = GetSeatIndexOf( pawn.Id );
		if ( seat < 0 )
			return false;

		SeatOccupants[seat] = Guid.Empty;
		var exitPos = ComputeExitPosition( seat );
		pawn.Components.Get<PlayerMovement>()?.HostSetSeated( null, -1, exitPos );

		if ( seat == 0 )
			HostReleaseDriverOwnership();

		return true;
	}

	/// <summary>Host: death / despawn safety — if this pawn sits in any vehicle, it gets out.</summary>
	public static void HostEjectIfSeated( GameObject pawn )
	{
		if ( pawn is null || !pawn.IsValid() )
			return;

		var movement = pawn.Components.Get<PlayerMovement>();
		if ( movement is null || !movement.IsSeated )
			return;

		var vehicle = movement.SeatedVehicle;
		if ( vehicle is not null && vehicle.IsValid() )
			vehicle.HostExit( pawn );
		else
			movement.HostSetSeated( null, -1, pawn.WorldPosition );
	}

	/// <summary>Host: everyone out (wreck, despawn).</summary>
	void HostEjectAll()
	{
		for ( var i = 0; i < SeatOccupants.Count; i++ )
		{
			var pawn = GetOccupant( i );
			if ( pawn is not null && pawn.IsValid() )
				HostExit( pawn );
			else
				SeatOccupants[i] = Guid.Empty;
		}
	}

	Vector3 ComputeExitPosition( int seatIndex )
	{
		var seatGo = GetSeatObject( seatIndex );
		var seatPos = seatGo is { IsValid: true } ? seatGo.WorldPosition : WorldPosition;
		var sideSign = seatGo is { IsValid: true } && seatGo.LocalPosition.y < 0f ? -1f : 1f;
		var side = WorldRotation.Left * sideSign;
		var half = GameObject.GetBounds().Size.y * 0.5f;
		var outPos = seatPos + side * ( half + TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, ExitSideMeters ) ) * 0.5f );

		// Feet on the ground beside the vehicle; fall back to the seat height if nothing is below.
		var tr = Scene.Trace.Ray( outPos + Vector3.Up * 60f, outPos - Vector3.Up * 200f )
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "player", "trigger" )
			.Run();
		return tr.Hit ? tr.HitPosition + Vector3.Up * 2f : outPos;
	}

	void HostAssignDriverOwnership( GameObject pawn )
	{
		if ( GameObject.Network is not { Active: true } net )
			return;

		var owner = pawn.Network?.Owner;
		if ( owner is null )
			return;

		net.AssignOwnership( owner );
	}

	void HostReleaseDriverOwnership()
	{
		if ( GameObject.Network is { Active: true } net && net.Owner is not null )
			net.DropOwnership();
	}

	/// <summary>Host, 2 Hz: a seat whose pawn vanished (disconnect, respawn elsewhere) is freed.</summary>
	void TickOccupancyValidity()
	{
		if ( !HasHostAuthority || Time.NowDouble < _nextOccupancySweepAt )
			return;

		_nextOccupancySweepAt = Time.NowDouble + 0.5;
		for ( var i = 0; i < SeatOccupants.Count; i++ )
		{
			var id = SeatOccupants[i];
			if ( id == Guid.Empty )
				continue;

			var pawn = Scene.Directory.FindByGuid( id );
			var movement = pawn?.Components.Get<PlayerMovement>();
			if ( pawn is null || !pawn.IsValid() || movement is null || movement.SeatedVehicleId != GameObject.Id )
			{
				SeatOccupants[i] = Guid.Empty;
				if ( i == 0 )
					HostReleaseDriverOwnership();
			}
		}
	}
}
