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

	public VehicleSeat GetSeat( int seatIndex )
	{
		if ( _seats.Count == 0 )
			CollectSeats();
		return seatIndex >= 0 && seatIndex < _seats.Count ? _seats[seatIndex] : null;
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

	/// <summary>Dev readout for <c>vehicle_info</c>: which spot the last exit chose.</summary>
	public string LastExitPlacement { get; private set; }

	/// <summary>
	/// Where the feet go on exit (Mark 2026-10-06: seat side first, the other side if that spot is under
	/// the ground or inside something, else the roof — he kept falling through the earth). A side spot
	/// counts only when the line from inside the hull to it at mid-hull height is clear (a hillside, wall
	/// or tree in the way means the spot is inside or behind it), a floor lies under it no more than
	/// <see cref="ExitMaxDropUnits"/> below the hull and is not a steep bank, and a pawn fits standing
	/// there. The roof is the one spot never under the ground, because the vehicle itself stands there.
	/// </summary>
	Vector3 ComputeExitPosition( int seatIndex )
	{
		var seatGo = GetSeatObject( seatIndex );

		// A standing seat (hover platform console) lets go onto the deck itself: a step to the seat's side
		// or behind it, wherever the deck is under foot and nothing stands there.
		if ( GetSeat( seatIndex ) is { Standing: true } && seatGo is { IsValid: true } && TryFindStandingExit( seatGo, out var deckFeet ) )
		{
			LastExitPlacement = "deck";
			return deckFeet;
		}
		var sideSign = seatGo is { IsValid: true } && seatGo.LocalPosition.y < 0f ? -1f : 1f;
		var bounds = GameObject.GetBounds();
		var centre = bounds.Center;

		// The hull's left, flat — a buggy on its side still puts you down beside it, not above or below.
		var side = WorldRotation.Left.WithZ( 0f );
		if ( side.LengthSquared < 0.09f )
			side = Vector3.Cross( Vector3.Up, WorldRotation.Forward.WithZ( 0f ) );
		if ( side.LengthSquared < 1e-4f )
			side = Vector3.Left;
		side = side.Normal;

		var ext = bounds.Extents;
		var halfWidth = MathF.Abs( side.x ) * ext.x + MathF.Abs( side.y ) * ext.y;
		var reach = halfWidth + TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, ExitSideMeters ) ) * 0.5f;

		if ( TryFindExitFootingBeside( centre, side * sideSign, reach, bounds.Mins.z, out var feet ) )
		{
			LastExitPlacement = sideSign > 0f ? "left" : "right";
			return feet;
		}

		if ( TryFindExitFootingBeside( centre, side * -sideSign, reach, bounds.Mins.z, out feet ) )
		{
			LastExitPlacement = sideSign > 0f ? "right (left blocked)" : "left (right blocked)";
			return feet;
		}

		var roof = centre.WithZ( bounds.Maxs.z + 2f );
		LastExitPlacement = IsExitStandingRoomClear( roof ) ? "roof (both sides blocked)" : "roof (both sides blocked, roof tight)";
		return roof;
	}

	/// <summary>Furthest a side spot may lie below the hull's lowest point before it counts as a cliff (3 m).</summary>
	const float ExitMaxDropUnits = 120f;

	/// <summary>Standing seat: left, right, then behind the console; a floor (the deck, or a piece built on it) must lie within 0.75 m under the spot.</summary>
	bool TryFindStandingExit( GameObject seatGo, out Vector3 feet )
	{
		feet = default;
		var step = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, ExitSideMeters ) ) * 0.6f;
		Span<Vector3> tries = stackalloc Vector3[3];
		tries[0] = seatGo.WorldPosition + seatGo.WorldRotation.Left * step;
		tries[1] = seatGo.WorldPosition + seatGo.WorldRotation.Right * step;
		tries[2] = seatGo.WorldPosition + seatGo.WorldRotation.Backward * step;

		for ( var i = 0; i < tries.Length; i++ )
		{
			var spot = tries[i];
			var floor = Scene.Trace.Ray( spot + Vector3.Up * 20f, spot - Vector3.Up * 30f )
				.WithoutTags( "player", "trigger", "worlddrop" )
				.Run();
			if ( !floor.Hit || floor.StartedSolid || floor.Normal.z < 0.5f )
				continue;

			var candidate = floor.HitPosition + Vector3.Up * 2f;
			if ( !IsExitStandingRoomClear( candidate ) )
				continue;

			feet = candidate;
			return true;
		}

		return false;
	}

	bool TryFindExitFootingBeside( Vector3 from, Vector3 direction, float reach, float hullBottomZ, out Vector3 feet )
	{
		feet = default;
		var hand = from + direction * reach;

		var line = Scene.Trace.Ray( from, hand )
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "player", "trigger", "worlddrop" )
			.Run();
		if ( line.Hit )
			return false;

		var floor = Scene.Trace.Ray( hand, hand - Vector3.Up * ( hand.z - hullBottomZ + ExitMaxDropUnits ) )
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "player", "trigger", "worlddrop" )
			.Run();
		if ( !floor.Hit || floor.StartedSolid || floor.Normal.z < 0.5f )
			return false;

		feet = floor.HitPosition + Vector3.Up * 2f;
		return IsExitStandingRoomClear( feet );
	}

	/// <summary>Two sphere overlaps where a standing pawn's legs and chest would be.</summary>
	bool IsExitStandingRoomClear( Vector3 feet )
	{
		const float radius = 14f;
		for ( var h = 18f; h <= 52f; h += 34f )
		{
			var at = feet + Vector3.Up * h;
			var overlap = Scene.Trace.Sphere( radius, at, at )
				.IgnoreGameObjectHierarchy( GameObject )
				.WithoutTags( "player", "trigger", "worlddrop" )
				.Run();
			if ( overlap.Hit && overlap.StartedSolid )
				return false;
		}

		return true;
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
