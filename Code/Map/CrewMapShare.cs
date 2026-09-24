using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;

namespace Survival;

/// <summary>
/// Coop map sharing between crew mates. Each pawn's <see cref="PlayerCrew"/> syncs its owner's
/// "show my location" flag and a blob of their pins; every viewer decides per crew mate whether
/// that player's pins are drawn on their own map (session choice, never saved — crews change).
/// Locations come for free from the pawn transform, so only the flag travels.
/// </summary>
public static class CrewMapShare
{
	public readonly struct RemotePin
	{
		public readonly string Icon;
		public readonly string Name;
		public readonly float XMeters;
		public readonly float YMeters;
		public readonly bool CrossedOff;

		public RemotePin( string icon, string name, float x, float y, bool crossedOff )
		{
			Icon = icon;
			Name = name;
			XMeters = x;
			YMeters = y;
			CrossedOff = crossedOff;
		}
	}

	static readonly HashSet<Guid> ShownPinOwners = new();

	/// <summary>Bumps when a viewer toggles whose pins they see.</summary>
	public static int Version { get; private set; }

	public static bool IsShowingPinsOf( Guid playerKey ) => ShownPinOwners.Contains( playerKey );

	public static void SetShowPinsOf( Guid playerKey, bool show )
	{
		var changed = show ? ShownPinOwners.Add( playerKey ) : ShownPinOwners.Remove( playerKey );
		if ( changed )
			Version++;
	}

	// ------------------------------------------------------------------
	// Pin blob (owner → everyone, one [Sync] string on PlayerCrew)
	// ------------------------------------------------------------------

	/// <summary>One line per pin: <c>icon|name|x|y|c</c>. Names lose the separators.</summary>
	public static string EncodePins( IReadOnlyList<MapPinData> pins )
	{
		if ( pins is null || pins.Count == 0 )
			return string.Empty;

		var sb = new StringBuilder( pins.Count * 48 );
		for ( var i = 0; i < pins.Count; i++ )
		{
			var pin = pins[i];
			if ( pin is null )
				continue;

			if ( sb.Length > 0 )
				sb.Append( '\n' );

			sb.Append( pin.Icon ?? MapPinCatalog.DefaultIconId ).Append( '|' )
				.Append( SanitizeName( pin.Name ) ).Append( '|' )
				.Append( pin.XMeters.ToString( "0.##", System.Globalization.CultureInfo.InvariantCulture ) ).Append( '|' )
				.Append( pin.YMeters.ToString( "0.##", System.Globalization.CultureInfo.InvariantCulture ) ).Append( '|' )
				.Append( pin.CrossedOff ? '1' : '0' );
		}

		return sb.ToString();
	}

	public static void DecodePins( string blob, List<RemotePin> into )
	{
		into.Clear();
		if ( string.IsNullOrEmpty( blob ) )
			return;

		var culture = System.Globalization.CultureInfo.InvariantCulture;
		foreach ( var line in blob.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var parts = line.Split( '|' );
			if ( parts.Length < 5 )
				continue;

			if ( !float.TryParse( parts[2], System.Globalization.NumberStyles.Float, culture, out var x ) )
				continue;
			if ( !float.TryParse( parts[3], System.Globalization.NumberStyles.Float, culture, out var y ) )
				continue;

			var icon = MapPinCatalog.IsKnown( parts[0] ) ? parts[0] : MapPinCatalog.DefaultIconId;
			into.Add( new RemotePin( icon, parts[1], x, y, parts[4] == "1" ) );
		}
	}

	static string SanitizeName( string name )
	{
		if ( string.IsNullOrEmpty( name ) )
			return string.Empty;

		return name.Replace( '|', ' ' ).Replace( '\n', ' ' ).Replace( '\r', ' ' );
	}

	// ------------------------------------------------------------------
	// Who is in my crew
	// ------------------------------------------------------------------

	/// <summary>The <see cref="PlayerCrew"/> on the pawn this machine drives, or null.</summary>
	public static PlayerCrew FindLocalCrew( Scene scene )
	{
		if ( scene is null || !scene.IsValid() )
			return null;

		foreach ( var crew in scene.GetAllComponents<PlayerCrew>() )
		{
			if ( crew is null || !crew.IsValid() )
				continue;

			var vitals = crew.Components.Get<PlayerVitals>();
			if ( vitals is not null && vitals.IsLocalInputOwnedPawn() )
				return crew;
		}

		return null;
	}

	/// <summary>Every other live pawn in the local player's crew (empty while solo).</summary>
	public static void CollectCrewMates( Scene scene, PlayerCrew local, List<PlayerCrew> into )
	{
		into.Clear();
		if ( local is null || !local.IsValid() || scene is null || !scene.IsValid() )
			return;

		var myCrew = local.GetMyCrew();
		if ( myCrew is null || myCrew.Members.Count <= 1 )
			return;

		foreach ( var crew in scene.GetAllComponents<PlayerCrew>() )
		{
			if ( crew is null || !crew.IsValid() || crew == local )
				continue;

			var key = crew.PlayerKey;
			foreach ( var member in myCrew.Members )
			{
				if ( member.PlayerId != key )
					continue;

				into.Add( crew );
				break;
			}
		}
	}

	static readonly Color[] MarkerPalette =
	{
		new( 0.25f, 0.85f, 1f ),
		new( 1f, 0.55f, 0.2f ),
		new( 0.55f, 1f, 0.35f ),
		new( 1f, 0.4f, 0.75f ),
		new( 0.75f, 0.55f, 1f ),
		new( 1f, 0.9f, 0.3f ),
		new( 0.3f, 1f, 0.85f ),
		new( 1f, 0.35f, 0.35f ),
	};

	/// <summary>Stable per-player marker colour so crew mates can be told apart at a glance.</summary>
	public static Color ColorFor( Guid playerKey )
	{
		var hash = playerKey.GetHashCode() & 0x7fffffff;
		return MarkerPalette[hash % MarkerPalette.Length];
	}
}
