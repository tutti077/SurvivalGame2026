using Sandbox;

namespace Survival;

/// <summary>
/// One seat on a <see cref="Vehicle"/>: an empty child object whose transform is where the seated pawn's
/// root goes (feet on the floor of the footwell, facing the vehicle's forward). Seat 0 is the driver;
/// higher indices are passengers. Occupancy lives on the vehicle, not here.
/// </summary>
[Title( "Vehicle Seat" ), Category( "Vehicles" ), Icon( "event_seat" )]
public sealed class VehicleSeat : Component
{
	/// <summary>0 = driver (controls + ownership), 1+ = passenger (may use weapons).</summary>
	[Property, Title( "Seat Index" )]
	public int SeatIndex { get; set; }

	public bool IsDriverSeat => SeatIndex == 0;
}
