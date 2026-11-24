using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

public enum HubPortalKind
{
	/// <summary>The buildable purple portal in a survival world: walk in, arrive in the hub.</summary>
	ToHub,
	/// <summary>The hub fountain: back to the portal you came through (or your spawn when there is none).</summary>
	ToWorld,
	/// <summary>A pad inside the hub that moves you to <see cref="HubPortal.Target"/> (time trial exit, etc.).</summary>
	Teleport,
}

/// <summary>
/// Walk-in portal. The local pawn notices it is inside one (<see cref="PlayerInventoryInteraction"/>'s hub tick, a few
/// times a second against this small static list) and asks the host, which re-checks the distance once and moves it.
/// </summary>
[Title( "Hub Portal" )]
public sealed class HubPortal : Component
{
	static readonly List<HubPortal> Active = new();
	public static IReadOnlyList<HubPortal> All => Active;

	[Property] public HubPortalKind Kind { get; set; } = HubPortalKind.ToHub;

	/// <summary>Inside this distance of the portal's centre (pawn body centre) you go through.</summary>
	[Property, Title( "Radius (m)" ), Range( 0.5f, 10f )]
	public float RadiusMeters { get; set; } = 1.4f;

	/// <summary>Height of the portal's centre above its origin (the origin sits on the floor).</summary>
	[Property, Title( "Centre Height (m)" ), Range( 0f, 10f )]
	public float CentreHeightMeters { get; set; } = 1.4f;

	[Property, Group( "To Hub" )] public GameObject HubPrefab { get; set; }

	/// <summary>Where the hub is placed in the world, in metres (far above the playable map).</summary>
	[Property, Group( "To Hub" ), Title( "Hub Position (m)" )]
	public Vector3 HubPositionMeters { get; set; } = new( 0f, 0f, 3000f );

	[Property, Group( "Teleport" )] public GameObject Target { get; set; }

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !Active.Contains( this ) )
			Active.Add( this );
	}

	protected override void OnDisabled()
	{
		Active.Remove( this );
		base.OnDisabled();
	}

	public Vector3 CentreWorld => WorldPosition + WorldRotation.Up * TerrainWorldUnits.MetersToEngine( CentreHeightMeters );

	public float RadiusUnits => TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, RadiusMeters ) );

	/// <summary>Pawn body centre (feet + 0.9 m) inside the portal sphere.</summary>
	public bool Contains( GameObject pawn, float padUnits = 0f )
	{
		if ( pawn is null || !pawn.IsValid() )
			return false;
		var body = pawn.WorldPosition + Vector3.Up * 36f;
		var r = RadiusUnits + padUnits;
		return (body - CentreWorld).LengthSquared <= r * r;
	}

	/// <summary>
	/// Last resort for the fountain (no portal remembered — the hub was respawned — and no bed or SpawnPoint, as in
	/// terrainTest): the ground straight below the hub. The player is standing above it, so that terrain is streamed in.
	/// </summary>
	bool TryGroundBelowHub( out Transform where )
	{
		var start = WorldPosition - Vector3.Up * TerrainWorldUnits.MetersToEngine( 200f );
		var tr = Scene.Trace.Ray( start, start - Vector3.Up * TerrainWorldUnits.MetersToEngine( 5000f ) )
			.WithoutTags( "player", "trigger" ).Run();
		where = new Transform( tr.HitPosition + Vector3.Up * 16f, Rotation.Identity );
		return tr.Hit;
	}

	/// <summary>Host: move this pawn through the portal (distance already re-checked by the caller).</summary>
	public void HostTravel( GameObject pawn )
	{
		var movement = pawn?.Components.Get<PlayerMovement>();
		if ( movement is null )
			return;

		switch ( Kind )
		{
			case HubPortalKind.ToHub:
			{
				var hub = HubWorld.HostEnsureSpawned( Scene, HubPrefab,
					new Vector3( TerrainWorldUnits.MetersToEngine( HubPositionMeters.x ),
						TerrainWorldUnits.MetersToEngine( HubPositionMeters.y ),
						TerrainWorldUnits.MetersToEngine( HubPositionMeters.z ) ) );
				if ( hub is null )
					return;

				// Come back out 2 m in front of this portal, facing away from it.
				var back = new Transform(
					WorldPosition + WorldRotation.Forward.WithZ( 0f ).Normal * TerrainWorldUnits.MetersToEngine( 2f ) + Vector3.Up * 8f,
					Rotation.LookAt( WorldRotation.Forward.WithZ( 0f ).Normal, Vector3.Up ) );
				hub.HostRememberReturn( pawn, back );
				var arrive = hub.ArrivalTransform;
				movement.HostApplyEventSpawn( arrive.Position + Vector3.Up * 8f, arrive.Rotation );
				pawn.Components.Get<PlayerInventoryInteraction>()?.HostRefreshHubRecord( grantDailyAllowance: true );
				Log.Info( $"[Hub] {HubIdentity.DisplayName( pawn )} entered the hub." );
				break;
			}
			case HubPortalKind.ToWorld:
			{
				Transform back = default;
				var has = HubWorld.Instance is { IsValid: true } hub && hub.HostTakeReturn( pawn, out back );
				if ( !has )
				{
					var vitals = pawn.Components.Get<PlayerVitals>();
					if ( vitals is not null && vitals.TryResolveSpawnTransform( out var pos, out var rot ) )
						back = new Transform( pos, rot );
					else if ( !TryGroundBelowHub( out back ) )
					{
						Log.Warning( "[Hub] No return portal, spawn or ground below the hub for this player." );
						return;
					}
				}

				movement.HostApplyEventSpawn( back.Position, back.Rotation );
				Log.Info( $"[Hub] {HubIdentity.DisplayName( pawn )} went back to their world." );
				break;
			}
			case HubPortalKind.Teleport:
				if ( Target is { IsValid: true } t )
					movement.HostApplyEventSpawn( t.WorldPosition + Vector3.Up * 8f, t.WorldRotation );
				break;
		}
	}
}
