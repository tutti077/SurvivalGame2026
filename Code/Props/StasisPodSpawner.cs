using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-side arrivals: every player's first pawn of the session comes down in a <see cref="StasisPod"/> that lands
/// within <see cref="LandingRadiusMeters"/> of the spawn centre (Mark 2026-11-09: "within a 25m range of the central
/// spawn point"). Author one in a scene; the centre is <see cref="CentreOverride"/>, else the scene's
/// <see cref="SpawnPoint"/>, else the world origin, dropped to the ground. <see cref="GameHacks.PodArrivals"/>
/// (<c>pod_arrivals 0</c>) switches the whole thing off for plain dev spawns; <c>pod_test N</c> drops N empty pods
/// through <see cref="LaunchTest"/>.
/// </summary>
[Title( "Stasis Pod Spawner" )]
public sealed class StasisPodSpawner : Component
{
	public const string PrefabPath = "prefabs/props/stasis_pod.prefab";

	[Property, Title( "Landing radius (m)" ), Range( 5f, 200f ), Step( 1f )]
	public float LandingRadiusMeters { get; set; } = 25f;

	[Property, Title( "Min pod spacing (m)" ), Range( 1f, 20f ), Step( 0.5f ), Description( "Landing points keep at least this far from every pod already down or on its way." )]
	public float MinSpacingMeters { get; set; } = 4f;

	[Property, Title( "Spawn centre override" ), Description( "Lands around this object instead of the scene's SpawnPoint / origin." )]
	public GameObject CentreOverride { get; set; }

	[Property, Title( "First spawn only" ), Description( "Each player's first pawn of the session arrives by pod; respawns and later dev pawns spawn normally." )]
	public bool FirstSpawnOnly { get; set; } = true;

	public static StasisPodSpawner Current { get; private set; }

	readonly HashSet<Guid> _handledConnections = new();
	readonly HashSet<Guid> _handledPawns = new();
	static bool _localPawnHandledThisSession;
	readonly List<Vector3> _landings = new();
	float _scanClock;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		Current = this;
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();
		if ( Current == this )
			Current = null;
	}

	bool IsHost => GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !IsHost || !GameHacks.PodArrivals )
			return;

		_scanClock -= Time.Delta;
		if ( _scanClock > 0f )
			return;
		_scanClock = 0.5f;

		foreach ( var vitals in Scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is null || !vitals.IsValid() || !vitals.GameObject.Active )
				continue;

			var pawn = vitals.GameObject;
			if ( _handledPawns.Contains( pawn.Id ) )
				continue;

			// Only real players arrive by pod: a connection's own pawn, or the one the L key spawned in an offline
			// terrainTest. Scripted clones (combat_test / anim_test pawns, camp dummies) spawn where they are put.
			var connection = pawn.Network?.Owner?.Id ?? Guid.Empty;
			if ( connection == Guid.Empty && pawn != TerrainTestPlayerSpawn.ActivePlayable )
				continue;

			var movement = pawn.Components.Get<PlayerMovement>();
			if ( movement is null || movement.PodLocked )
				continue;

			var firstForThisPlayer = connection != Guid.Empty ? _handledConnections.Add( connection ) : !_localPawnHandledThisSession;
			if ( connection == Guid.Empty )
				_localPawnHandledThisSession = true;
			_handledPawns.Add( pawn.Id );
			if ( FirstSpawnOnly && !firstForThisPlayer )
				continue;

			Launch( pawn );
		}
	}

	/// <summary>The spawn centre on the ground.</summary>
	public Vector3 Centre()
	{
		Vector3 xy;
		if ( CentreOverride is { IsValid: true } )
			xy = CentreOverride.WorldPosition;
		else
		{
			var point = Scene.GetAllComponents<SpawnPoint>().FirstOrDefault( p => p is { IsValid: true } );
			xy = point is not null ? point.WorldPosition : Vector3.Zero;
		}
		return new Vector3( xy.x, xy.y, GroundZ( xy.x, xy.y, xy.z ) );
	}

	/// <summary>Host: fly a pod in for this pawn (null = empty), landing near the centre. Returns the pod.</summary>
	public StasisPod Launch( GameObject occupant, Vector3? centreOverride = null, float delaySeconds = 0f )
	{
		if ( !IsHost )
			return null;

		var prefabFile = ResourceLibrary.Get<PrefabFile>( PrefabPath );
		if ( prefabFile is null )
		{
			Log.Warning( $"[StasisPodSpawner] prefab '{PrefabPath}' not found." );
			return null;
		}

		var centre = centreOverride ?? Centre();
		var land = PickLanding( centre );
		var yaw = Random.Shared.Float( 0f, 360f );
		var go = SceneUtility.GetPrefabScene( prefabFile ).Clone( new Transform( land + Vector3.Up * 4000f, Rotation.FromYaw( yaw ) ), name: "stasis_pod" );
		var pod = go.Components.Get<StasisPod>();
		if ( pod is null )
		{
			Log.Warning( "[StasisPodSpawner] the pod prefab has no StasisPod component." );
			go.Destroy();
			return null;
		}

		pod.HostLaunch( land, yaw, centre, occupant, delaySeconds );
		HostNetworkSpawn.TrySpawn( go );
		_landings.Add( land );
		return pod;
	}

	/// <summary>Host: <paramref name="count"/> empty pods crash-landing around the centre within a couple of seconds of each other.</summary>
	public int LaunchTest( int count, Vector3? centreOverride = null )
	{
		var launched = 0;
		for ( var i = 0; i < count; i++ )
		{
			if ( Launch( null, centreOverride, Random.Shared.Float( 0f, 2.5f ) ) is not null )
				launched++;
		}
		return launched;
	}

	Vector3 PickLanding( Vector3 centre )
	{
		var radius = TerrainWorldUnits.MetersToEngine( LandingRadiusMeters );
		var spacing = TerrainWorldUnits.MetersToEngine( MinSpacingMeters );
		Vector3 best = centre;
		var bestClearance = -1f;
		for ( var attempt = 0; attempt < 16; attempt++ )
		{
			var r = radius * MathF.Sqrt( Random.Shared.Float( 0.04f, 1f ) );
			var ang = Random.Shared.Float( 0f, MathF.Tau );
			var candidate = new Vector3( centre.x + MathF.Cos( ang ) * r, centre.y + MathF.Sin( ang ) * r, centre.z );
			var clearance = float.MaxValue;
			foreach ( var other in _landings )
				clearance = MathF.Min( clearance, Vector3.DistanceBetween( other.WithZ( 0f ), candidate.WithZ( 0f ) ) );
			if ( clearance >= spacing )
			{
				best = candidate;
				bestClearance = clearance;
				break;
			}
			if ( clearance > bestClearance )
			{
				best = candidate;
				bestClearance = clearance;
			}
		}

		return new Vector3( best.x, best.y, GroundZ( best.x, best.y, centre.z ) );
	}

	/// <summary>Ground height at an XY: the terrain world's own height field when there is one, else a scene trace.</summary>
	float GroundZ( float x, float y, float fallbackZ )
	{
		var manager = Scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault( m => m is { IsValid: true } );
		if ( manager is not null )
		{
			var meters = TerrainWorldUnits.EngineToMeters( new Vector3( x, y, 0f ) );
			if ( manager.TrySampleGroundMeters( meters.x, meters.y, out var groundMeters ) )
				return TerrainWorldUnits.MetersToEngine( groundMeters );
		}

		var tr = Scene.Trace.Ray( new Vector3( x, y, fallbackZ + 12000f ), new Vector3( x, y, fallbackZ - 12000f ) )
			.WithoutTags( "player", "trigger", "doorleaf" )
			.Run();
		return tr.Hit ? tr.HitPosition.z : fallbackZ;
	}
}
