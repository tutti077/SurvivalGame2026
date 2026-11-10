namespace Survival;

/// <summary>
/// <c>world_find &lt;kind&gt; [radius m] [park]</c> — where the generator put something, without flying around looking: any prop
/// cluster kind (<c>wheat</c>, <c>spire</c>, <c>boulder</c>, <c>rock</c>, <c>stick</c>, <c>daisy</c> …) from the scatter's own
/// planner, or <c>camp</c> from the biome population's camp sites (<see cref="EnemyCampSites"/>). Lists the nearest eight to the
/// camera with a ready <c>terrain_cam</c> line; <c>park</c> puts the fly camera 25 m up and 40 m back from the nearest one,
/// looking at it. Read-only: nothing is spawned.
/// </summary>
public static partial class TerrainDevCommands
{
	[ConCmd( "world_find" )]
	public static void ConCmdWorldFind( string kind = "spire", float radiusMeters = 1500f, string park = "" )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		if ( manager is null || !manager.IsValid() || manager.LoadSettings is null )
		{
			Log.Warning( "[TerrainDev] world_find: no loaded terrain world" );
			return;
		}

		var from = TerrainWorldUnits.EngineToMeters( scene.Camera.IsValid() ? scene.Camera.WorldPosition : Vector3.Zero );
		var radius = Math.Clamp( radiusMeters, 32f, 8000f );
		var found = manager.DevFindPlanned( kind, from.x, from.y, radius );
		found.Sort( ( a, b ) => (new Vector2( a.x, a.y ) - new Vector2( from.x, from.y )).Length.CompareTo( (new Vector2( b.x, b.y ) - new Vector2( from.x, from.y )).Length ) );
		Log.Info( $"[TerrainDev] world_find {kind}: {found.Count} within {radius:0} m of {from.x:0},{from.y:0} ({found.Count / (MathF.PI * radius * radius / 1e6f):0.0} per km² of that disc, any biome)" );
		for ( var i = 0; i < Math.Min( 8, found.Count ); i++ )
		{
			var p = found[i];
			var d = (new Vector2( p.x, p.y ) - new Vector2( from.x, from.y )).Length;
			Log.Info( $"  {d,6:0} m  at {p.x:0.0},{p.y:0.0}  ground {p.z:0.0} m   terrain_cam {p.x - 40f:0} {p.y:0} {p.z + 25f:0} 30 0" );
		}

		if ( found.Count > 0 && park is "park" or "1" )
		{
			var p = found[0];
			ConCmdCamera( p.x - 40f, p.y, p.z + 25f, 30f, 0f );
		}
	}
}
