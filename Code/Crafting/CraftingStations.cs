using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Proximity stations for <c>requiredStation</c> recipes: the crafting menu lists such a recipe while
/// the player stands near the station, and the host re-checks the same test once per craft
/// (<c>PlayerCrafting.HostTryCraft</c>). The workbench is not one of these — it has its own menu and
/// shelter rule (<see cref="Workbench"/>). Add a station here when its recipes should appear in the
/// personal menu by proximity. The drying rack is not one either: it processes items pulled from the
/// inventory on its own clock (<see cref="DryingRack"/>), no recipe lists there.
/// </summary>
public static class CraftingStations
{
	public const int CampfireBit = 1;

	/// <summary>Is <paramref name="player"/> within use range of a working <paramref name="stationId"/>?</summary>
	public static bool IsPlayerNear( GameObject player, string stationId )
	{
		if ( string.IsNullOrWhiteSpace( stationId ) )
			return false;

		if ( Is( stationId, Campfire.StationId ) )
			return Campfire.IsPlayerNearLitOrFueledStation( player, stationId );

		return false;
	}

	/// <summary>Bit per station the player is near right now — the crafting list re-filters when it changes.</summary>
	public static int NearbyMask( GameObject player )
	{
		if ( player is null || !player.IsValid() )
			return 0;

		var mask = 0;
		if ( Campfire.IsPlayerNearLitOrFueledStation( player, Campfire.StationId ) )
			mask |= CampfireBit;
		return mask;
	}

	/// <summary>Lower-case words for the "Need … nearby" craft button.</summary>
	public static string DisplayName( string stationId )
	{
		if ( Is( stationId, Campfire.StationId ) )
			return "campfire";
		if ( Is( stationId, Workbench.StationId ) )
			return "workbench";
		return stationId ?? string.Empty;
	}

	static bool Is( string a, string b ) => string.Equals( a, b, StringComparison.OrdinalIgnoreCase );
}
