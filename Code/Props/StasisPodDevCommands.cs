using System;
using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Dev console for the stasis pod: drop a standing pod, swing its door, crash-land a flight of empty pods.
/// </summary>
public static class StasisPodDevCommands
{
	/// <summary>Host: a standing pod this many metres ahead of the local pawn, door facing the pawn (no flight).</summary>
	[ConCmd( "pod_spawn" )]
	public static void ConCmdSpawn( float metersAhead = 4f )
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[StasisPod] pod_spawn: host only." );
			return;
		}

		var pawn = FindLocalPawn();
		var scene = pawn?.Scene ?? Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
		{
			Log.Warning( "[StasisPod] pod_spawn: no scene." );
			return;
		}

		var prefabFile = ResourceLibrary.Get<PrefabFile>( StasisPodSpawner.PrefabPath );
		if ( prefabFile is null )
		{
			Log.Warning( $"[StasisPod] pod_spawn: prefab '{StasisPodSpawner.PrefabPath}' not found." );
			return;
		}

		Vector3 origin;
		float yaw;
		if ( pawn is not null )
		{
			var forward = pawn.WorldRotation.Forward.WithZ( 0f ).Normal;
			if ( forward.LengthSquared < 0.01f )
				forward = Vector3.Forward;
			origin = pawn.WorldPosition + forward * TerrainWorldUnits.MetersToEngine( Math.Max( 2f, metersAhead ) );
			yaw = pawn.WorldRotation.Yaw() + 180f;   // door (+X) toward the pawn
		}
		else
		{
			var cam = scene.Camera;
			origin = cam is not null ? cam.WorldPosition + cam.WorldRotation.Forward.WithZ( 0f ).Normal * TerrainWorldUnits.MetersToEngine( metersAhead ) : Vector3.Zero;
			yaw = 0f;
		}

		var trace = scene.Trace.Ray( origin + Vector3.Up * 200f, origin - Vector3.Up * 400f ).WithoutTags( "player", "trigger" );
		if ( pawn is not null )
			trace = trace.IgnoreGameObjectHierarchy( pawn );
		var tr = trace.Run();
		var ground = tr.Hit ? tr.HitPosition : origin;

		var go = SceneUtility.GetPrefabScene( prefabFile ).Clone( new Transform( ground, Rotation.FromYaw( yaw ) ), name: "stasis_pod" );
		go.Components.Get<StasisPod>()?.HostPlaceStanding( ground, yaw );
		HostNetworkSpawn.TrySpawn( go );
		Log.Info( $"[StasisPod] standing pod #{go.Components.Get<StasisPod>()?.Serial} at {go.WorldPosition} yaw {yaw:0}." );
	}

	/// <summary>Host: open / close / toggle the nearest pod's door.</summary>
	[ConCmd( "pod_door" )]
	public static void ConCmdDoor( string how = "toggle" )
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[StasisPod] pod_door: host only." );
			return;
		}

		var pod = FindNearestPod();
		if ( pod is null )
		{
			Log.Warning( "[StasisPod] pod_door: no stasis pod in the scene (pod_spawn first)." );
			return;
		}

		var open = how.ToLowerInvariant() switch
		{
			"open" or "1" => true,
			"close" or "closed" or "0" => false,
			_ => !pod.IsOpen,
		};
		pod.HostSetOpen( open );
		Log.Info( $"[StasisPod] #{pod.Serial} door {(open ? "opening" : "closing")}." );
	}

	/// <summary>
	/// Host: <paramref name="count"/> empty pods crash-land around the spawn centre (Mark: "simulate 10 players crash
	/// landing all at once"). <c>pod_test 10 here</c> lands them around your pawn / camera instead.
	/// </summary>
	[ConCmd( "pod_test" )]
	public static void ConCmdTest( int count = 10, string where = "" )
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[StasisPod] pod_test: host only." );
			return;
		}

		var spawner = StasisPodSpawner.Current;
		if ( spawner is null || !spawner.IsValid() )
		{
			Log.Warning( "[StasisPod] pod_test: no StasisPodSpawner in the scene - author one (terrainTest has it)." );
			return;
		}

		Vector3? centre = null;
		if ( string.Equals( where, "here", StringComparison.OrdinalIgnoreCase ) )
		{
			var pawn = FindLocalPawn();
			var scene = spawner.Scene;
			var at = pawn is not null ? pawn.WorldPosition : scene.Camera?.WorldPosition ?? Vector3.Zero;
			var tr = scene.Trace.Ray( at + Vector3.Up * 200f, at - Vector3.Up * 20000f ).WithoutTags( "player", "trigger" ).Run();
			centre = tr.Hit ? tr.HitPosition : at;
		}

		var launched = spawner.LaunchTest( Math.Clamp( count, 1, 64 ), centre );
		Log.Info( $"[StasisPod] pod_test: {launched} pods inbound around {(centre ?? spawner.Centre())}; next serial {StasisPodSerials.Peek( spawner.Scene )}." );
	}

	static StasisPod FindNearestPod()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return null;

		var pawn = FindLocalPawn();
		var from = pawn?.WorldPosition ?? scene.Camera?.WorldPosition ?? Vector3.Zero;
		StasisPod best = null;
		var bestDist = float.MaxValue;
		foreach ( var pod in scene.GetAllComponents<StasisPod>() )
		{
			if ( pod is null || !pod.IsValid() )
				continue;

			var d = Vector3.DistanceBetweenSquared( from, pod.WorldPosition );
			if ( d < bestDist )
			{
				bestDist = d;
				best = pod;
			}
		}

		return best;
	}

	static GameObject FindLocalPawn()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return null;

		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is not null && vitals.IsLocalInputOwnedPawn() )
				return vitals.GameObject;
		}

		return null;
	}
}
