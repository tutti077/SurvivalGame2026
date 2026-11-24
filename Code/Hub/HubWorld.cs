using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Root of the hub prefab (<c>prefabs/hub/hub.prefab</c>, built by <c>Tools/hub/generate_hub.py</c>). For now the hub
/// lives inside the host's own world, far above it (Mark 2026-11-10: the world keeps running and guests can stay),
/// spawned by the first <see cref="HubPortal"/> anyone walks through. Later the same prefab moves onto company servers.
/// Host-side it remembers, per player, the portal each one came through so the fountain sends them back there.
/// </summary>
[Title( "Hub World" )]
public sealed class HubWorld : Component
{
	public static HubWorld Instance { get; private set; }

	/// <summary>Where players land coming through a portal (in front of the fountain).</summary>
	[Property] public GameObject ArrivalPoint { get; set; }

	readonly Dictionary<Guid, Transform> _returns = new();

	protected override void OnEnabled()
	{
		base.OnEnabled();
		Instance = this;
	}

	protected override void OnDisabled()
	{
		if ( Instance == this )
			Instance = null;
		base.OnDisabled();
	}

	double _nextFallCheckAt;

	/// <summary>
	/// Host, once a second: the hub floats 3 km up, so anyone who ends up under its floor (over a wall, through a
	/// gap) is put back at the fountain instead of falling to their death in the world below.
	/// </summary>
	protected override void OnFixedUpdate()
	{
		if ( (GameObject.Network is { Active: true } && !Networking.IsHost) || Time.NowDouble < _nextFallCheckAt )
			return;
		_nextFallCheckAt = Time.NowDouble + 1.0;

		var half = TerrainWorldUnits.MetersToEngine( 700f );
		var below = TerrainWorldUnits.MetersToEngine( 20f );
		foreach ( var movement in Scene.GetAllComponents<PlayerMovement>() )
		{
			var local = WorldTransform.PointToLocal( movement.WorldPosition );
			if ( MathF.Abs( local.x ) > half || MathF.Abs( local.y ) > half || local.z > -below || local.z < -below * 10f )
				continue;

			var arrive = ArrivalTransform;
			movement.HostApplyEventSpawn( arrive.Position + Vector3.Up * 8f, arrive.Rotation );
		}
	}

	EnvironmentDayNightCycle _cycle;
	bool _cycleLookedUp;

	/// <summary>
	/// Every peer, before rendering: the hub is one enclosed hall, but the sun's shadow cascades do not take its 1000 m
	/// roof, so the sun lit a hard-edged disc of floor round the camera (2026-11-10). While the local camera is inside
	/// the hub, the sun and moon give no direct light or shadows (their ambient fill stays). The day/night cycle sets
	/// them again in its update every frame, so this only ever holds for the frame being drawn.
	/// </summary>
	protected override void OnPreRender()
	{
		var cam = Scene.Camera;
		if ( cam is null || !IsInside( cam.WorldPosition ) )
			return;

		if ( !_cycleLookedUp )
		{
			_cycleLookedUp = true;
			_cycle = Scene.GetAllComponents<EnvironmentDayNightCycle>().FirstOrDefault();
		}

		Darken( _cycle?.SunLight );
		Darken( _cycle?.MoonLight );
	}

	static void Darken( DirectionalLight light )
	{
		if ( light is null || !light.IsValid() )
			return;
		light.LightColor = Color.Black;
		light.Shadows = false;
	}

	bool IsInside( Vector3 worldPosition )
	{
		var local = WorldTransform.PointToLocal( worldPosition );
		var half = TerrainWorldUnits.MetersToEngine( 700f );
		return MathF.Abs( local.x ) < half && MathF.Abs( local.y ) < half
		       && local.z > -TerrainWorldUnits.MetersToEngine( 20f ) && local.z < TerrainWorldUnits.MetersToEngine( 200f );
	}

	/// <summary>Host: the hub in this scene, spawning it at <paramref name="worldPosition"/> the first time.</summary>
	public static HubWorld HostEnsureSpawned( Scene scene, GameObject prefab, Vector3 worldPosition )
	{
		if ( Instance is { IsValid: true } existing )
			return existing;

		if ( prefab is null || scene is null || !scene.IsValid() )
		{
			Log.Warning( "[Hub] No hub prefab wired on the portal." );
			return null;
		}

		var root = prefab.Clone( new Transform( worldPosition ) );
		root.Name = "Hub";
		HostNetworkSpawn.TrySpawn( root );
		var hub = root.Components.Get<HubWorld>();
		if ( hub is null )
			Log.Warning( "[Hub] Hub prefab has no HubWorld on its root." );
		return hub;
	}

	public Transform ArrivalTransform =>
		ArrivalPoint is { IsValid: true } a ? a.WorldTransform : WorldTransform;

	/// <summary>Host: remember where this player goes back to.</summary>
	public void HostRememberReturn( GameObject pawn, Transform where )
	{
		var key = TimeTrialSession.ResolvePlayerKey( pawn );
		if ( key != default )
			_returns[key] = where;
	}

	/// <summary>Host: where this player came from (consumed). False when they never came through a portal.</summary>
	public bool HostTakeReturn( GameObject pawn, out Transform where )
	{
		var key = TimeTrialSession.ResolvePlayerKey( pawn );
		if ( _returns.Remove( key, out where ) )
			return true;
		where = default;
		return false;
	}
}
