using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Short-lived map pings this machine should draw (own pings + crew mates', see the
/// <see cref="PlayerCrew"/> MapPing partial). Never saved: pings are pure session state, timed on
/// the wall clock (game time restarts at zero on every play session, which used to leave stale
/// pings "alive" for minutes) and tied to the scene they were made in.
/// </summary>
public static class MapPingFeed
{
	public const float LifetimeSeconds = 10f;

	public sealed class Ping
	{
		public Vector2 WorldMeters;
		public string SenderName = "";
		public double ExpiresAt;
		public double StartedAt;
		public Guid SceneId;
	}

	/// <summary>Wall-clock seconds; immune to the game clock resetting between play sessions.</summary>
	public static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

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
		var now = Now;
		Active.Add( new Ping
		{
			WorldMeters = worldMeters,
			SenderName = senderName ?? "",
			StartedAt = now,
			ExpiresAt = now + LifetimeSeconds,
			SceneId = Sandbox.Game.ActiveScene?.Id ?? Guid.Empty,
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
		var now = Now;
		var sceneId = Sandbox.Game.ActiveScene?.Id ?? Guid.Empty;
		for ( var i = Active.Count - 1; i >= 0; i-- )
		{
			var ping = Active[i];
			if ( ping.ExpiresAt > now && ping.SceneId == sceneId )
				continue;

			Active.RemoveAt( i );
			Version++;
		}
	}
}
