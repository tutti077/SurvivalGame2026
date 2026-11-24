namespace Survival;

/// <summary>
/// Pipe systems in the live world (Mark 2026-11-19; rules in <see cref="PipeRoutePlanner"/>, data in <c>data/pipe_systems.json</c>).
/// A chunk that finishes loading clones the kit prefabs of every piece whose start joint lies inside it, as children of the
/// chunk object - so they stream and die with the chunk, on every peer alike (nothing is networked or saved: the route is a
/// pure function of the seed). Buried pieces are just props inside the ground; the pickaxe only chips <c>terrain</c> /
/// <c>voxel</c> ground, so digging uncovers them and never cuts them.
/// </summary>
public sealed partial class TerrainWorldManager
{
	void SpawnPipesOnChunk( LoadedChunk entry, TerrainPreviewSettings settings, float chunkMinX, float chunkMinY )
	{
		var config = PipeSystemCatalog.Config;
		if ( !config.Enabled || _backend is null )
			return;

		var size = ChunkSizeMeters;
		var maxX = chunkMinX + size;
		var maxY = chunkMinY + size;
		var (loX, loY) = PipeRoutePlanner.CellAt( chunkMinX, chunkMinY );
		var (hiX, hiY) = PipeRoutePlanner.CellAt( maxX, maxY );
		for ( var cy = loY; cy <= hiY; cy++ )
		{
			for ( var cx = loX; cx <= hiX; cx++ )
			{
				var route = PipeRoutePlanner.RouteForCell( settings, _backend, cx, cy );
				if ( route is null || !route.Overlaps( chunkMinX, chunkMinY, maxX, maxY ) )
					continue;
				foreach ( var piece in route.Pieces )
				{
					var p = piece.PositionMeters;
					if ( p.x < chunkMinX || p.x >= maxX || p.y < chunkMinY || p.y >= maxY )
						continue;   // another chunk owns it
					SpawnPipePiece( entry.GameObject, config, piece );
				}
			}
		}
	}

	static void SpawnPipePiece( GameObject chunkRoot, PipeSystemConfig config, in PipePiece piece )
	{
		var path = config.PrefabFor( piece.Kind );
		var template = string.IsNullOrWhiteSpace( path ) ? null : GameObject.GetPrefab( path );
		if ( template is not { IsValid: true } )
		{
			Log.Warning( $"[Pipes] no prefab for {piece.Kind} ('{path}') - check data/pipe_systems.json" );
			return;
		}

		var go = template.Clone();
		go.NetworkMode = NetworkMode.Never;
		go.Flags |= GameObjectFlags.NotSaved;
		go.Name = $"pipe_{piece.Kind}";
		go.Parent = chunkRoot;
		go.WorldPosition = TerrainWorldUnits.MetersToEngine( piece.PositionMeters );
		go.WorldRotation = piece.Rotation;
	}

	/// <summary>Dev (<c>world_find pipe</c>): the middle of every pipe system within a radius - (x, y, ground) metres.</summary>
	void DevFindPipes( float xMeters, float yMeters, float radiusMeters, List<Vector3> result )
	{
		foreach ( var route in PipeRoutePlanner.RoutesNear( _loadSettings, _backend, xMeters, yMeters, radiusMeters ) )
		{
			var m = route.Middle;
			if ( (new Vector2( m.x, m.y ) - new Vector2( xMeters, yMeters )).Length <= radiusMeters )
				result.Add( m );
		}
	}

	/// <summary>
	/// Usage: <c>pipe_info [radius m]</c> - the pipe systems round the camera, nearest first: where each meets the ground, where
	/// its crushed tip and its cement cube are buried, and a <c>terrain_cam</c> line for each. <c>pipe_info reload</c> re-reads
	/// data/pipe_systems.json (chunks already loaded keep their pieces until they stream out).
	/// </summary>
	[ConCmd( "pipe_info" )]
	public static void ConCmdPipeInfo( string arg = "" )
	{
		if ( string.Equals( arg, "reload", StringComparison.OrdinalIgnoreCase ) )
		{
			PipeSystemCatalog.Reload();
			arg = "";
		}

		var config = PipeSystemCatalog.Config;
		Log.Info( $"[Pipes] {(config.Enabled ? "on" : "OFF")}: one roll of {config.Chance:0.##} per {config.CellMeters:0} m cell, run {config.LengthMinMeters:0}-{config.LengthMaxMeters:0} m, axis {config.ClearanceMinMeters:0.#}-{config.ClearanceMaxMeters:0.#} m over the ground, deep end {config.DeepEndDepthMeters:0} m down" );
		var scene = Sandbox.Game.ActiveScene;
		var world = scene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		if ( world is null || world._loadSettings is null || world._backend is null )
			return;

		var radius = float.TryParse( arg, out var r ) ? Math.Clamp( r, 500f, 20000f ) : 4000f;
		var from = TerrainWorldUnits.EngineToMeters( scene.Camera.IsValid() ? scene.Camera.WorldPosition : Vector3.Zero );
		var routes = PipeRoutePlanner.RoutesNear( world._loadSettings, world._backend, from.x, from.y, radius );
		float Distance( PipeRoute route ) => (new Vector2( route.Middle.x, route.Middle.y ) - new Vector2( from.x, from.y )).Length;
		routes.Sort( ( a, b ) => Distance( a ).CompareTo( Distance( b ) ) );
		Log.Info( $"[Pipes] {routes.Count} systems in the cells within {radius:0} m of {from.x:0},{from.y:0}" );
		for ( var i = 0; i < Math.Min( 6, routes.Count ); i++ )
		{
			var route = routes[i];
			var m = route.Middle;
			Log.Info( $"  {Distance( route ),6:0} m  cell {route.CellX},{route.CellY}: run {route.RunLengthMeters:0} m, {route.Pieces.Count} pieces   terrain_cam {m.x - 45f:0} {m.y:0} {m.z + 22f:0} 25 0" );
			Log.Info( $"          crushed side: enters the ground at {route.EntryCrushed.x:0.0},{route.EntryCrushed.y:0.0},{route.EntryCrushed.z:0.0}, tip at {route.CrushedTip.x:0.0},{route.CrushedTip.y:0.0},{route.CrushedTip.z:0.0}" );
			Log.Info( $"          deep side: enters the ground at {route.EntryDeep.x:0.0},{route.EntryDeep.y:0.0},{route.EntryDeep.z:0.0}, cube centre at {route.CubeCenter.x:0.0},{route.CubeCenter.y:0.0},{route.CubeCenter.z:0.0}" );
		}
	}
}
