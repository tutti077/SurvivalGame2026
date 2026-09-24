using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Short-lived map pings this machine should draw (own pings + crew mates', see the <see cref="PlayerCrew"/> MapPing partial).</summary>
public static class MapPingFeed
{
	public const float LifetimeSeconds = 15f;

	public sealed class Ping
	{
		public Vector2 WorldMeters;
		public string SenderName = "";
		public double ExpiresAt;
		public double StartedAt;
	}

	static readonly List<Ping> Active = new();

	/// <summary>Bumps whenever a ping is added or expires so map faces rebuild only then.</summary>
	public static int Version { get; private set; }

	public static IReadOnlyList<Ping> Pings
	{
		get
		{
			Prune();
			return Active;
		}
	}

	public static void Add( Vector2 worldMeters, string senderName )
	{
		Active.Add( new Ping
		{
			WorldMeters = worldMeters,
			SenderName = senderName ?? "",
			StartedAt = Time.NowDouble,
			ExpiresAt = Time.NowDouble + LifetimeSeconds,
		} );
		Version++;

		// The world sign is a bonus on top of the map ping: if it fails, the map ping must still show.
		try
		{
			MapPingBillboard.Spawn( Sandbox.Game.ActiveScene, worldMeters, senderName );
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[MapPing] world sign failed: {e}" );
		}
	}

	/// <summary>Diagnostic: ping your own position without the map or the middle mouse — separates input from rendering.</summary>
	[ConCmd( "map_ping_test" )]
	public static void ConCmdPingTest()
	{
		var scene = Sandbox.Game.ActiveScene;
		var crew = CrewMapShare.FindLocalCrew( scene );
		var position = crew is not null && crew.IsValid()
			? crew.GameObject.WorldPosition
			: scene?.Camera?.WorldPosition ?? Vector3.Zero;

		var meters = TerrainWorldUnits.EngineToMeters( position );
		Add( new Vector2( meters.x, meters.y ), "Test" );
		Log.Info( $"[MapPing] test ping at {meters.x:0}, {meters.y:0} m — {Active.Count} active" );
	}

	static void Prune()
	{
		var now = Time.NowDouble;
		for ( var i = Active.Count - 1; i >= 0; i-- )
		{
			if ( Active[i].ExpiresAt > now )
				continue;

			Active.RemoveAt( i );
			Version++;
		}
	}
}
