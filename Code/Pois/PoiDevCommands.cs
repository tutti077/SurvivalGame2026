using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Dev: <c>poi_spawn &lt;layout id | landmark id&gt; [metersAhead] [yaw]</c> - the host stands one point of interest from
/// <c>data/pois.json</c> on the ground ahead of the camera, without waiting for the generator to roll one
/// (<c>world_find poi</c> / <c>world_find crashed_pod</c> list the generated ones).
/// </summary>
public static class PoiDevCommands
{
	[ConCmd( "poi_spawn" )]
	public static void ConCmdSpawn( string id = "abandoned_shack", float metersAhead = 12f, float yawDegrees = 0f )
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Poi] poi_spawn: host only." );
			return;
		}

		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() || !scene.Camera.IsValid() )
		{
			Log.Warning( "[Poi] poi_spawn: no scene camera." );
			return;
		}

		PoiCatalog.Reload();
		var forward = scene.Camera.WorldRotation.Forward.WithZ( 0f ).Normal;
		var origin = scene.Camera.WorldPosition + (forward * TerrainWorldUnits.MetersToEngine( Math.Clamp( metersAhead, 2f, 200f ) ));

		if ( PoiCatalog.TryGetLayout( id, out _ ) )
		{
			var pieces = PoiSpawner.TrySpawn( scene, id, origin, yawDegrees, null );
			Log.Info( $"[Poi] poi_spawn {id}: {pieces.Count} pieces." );
			return;
		}

		foreach ( var landmark in PoiCatalog.Landmarks )
		{
			if ( !string.Equals( landmark.Id, id, StringComparison.OrdinalIgnoreCase ) )
				continue;
			var prefab = ResourceLibrary.Get<PrefabFile>( landmark.Prefab );
			if ( prefab is null )
			{
				Log.Warning( $"[Poi] poi_spawn: prefab '{landmark.Prefab}' not found." );
				return;
			}
			var tr = scene.Trace.Ray( origin + Vector3.Up * 2000f, origin + Vector3.Down * 8000f ).WithoutTags( "player", "enemy" ).Run();
			var go = SceneUtility.GetPrefabScene( prefab ).Clone( new Transform( tr.Hit ? tr.HitPosition : origin, Rotation.FromYaw( yawDegrees ) ) );
			go.Flags |= GameObjectFlags.NotSaved;
			Log.Info( $"[Poi] poi_spawn {id}: {go.Name} at {go.WorldPosition}." );
			return;
		}

		Log.Warning( $"[Poi] poi_spawn: no layout or landmark '{id}' in data/pois.json." );
	}
}
