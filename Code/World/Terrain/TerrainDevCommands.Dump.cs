namespace Survival;

/// <summary>
/// <c>terrain_dump_chunk [x y]</c> — rebuild the full-detail chunk under the fly camera (or the given world metres)
/// through <see cref="TerrainMeshBuilder.BuildChunkGeometry"/> and write it as a tiny OBJ-style text file
/// (<c>v x y z</c> in world metres, <c>f a b c</c> 1-based) to the data folder, so the triangles can be inspected
/// offline. <c>terrain_ledge_diagonals 0|1</c> toggles the ledge-aware triangulation for A/B dumps.
/// </summary>
public static partial class TerrainDevCommands
{
	[ConCmd( "terrain_dump_chunk" )]
	public static void ConCmdDumpChunk( string xMeters = "", string yMeters = "" )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return;

		var manager = scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		var camera = scene.GetAllComponents<TerrainTestFlyCamera>().FirstOrDefault();
		if ( manager is null || !manager.IsValid() )
		{
			Log.Warning( "[TerrainDev] needs a TerrainWorldManager in the scene" );
			return;
		}

		float x, y;
		if ( !TryParseMeters( xMeters, out x ) || !TryParseMeters( yMeters, out y ) )
		{
			if ( camera is null || !camera.IsValid() )
			{
				Log.Warning( "[TerrainDev] give x y in metres, or have a TerrainTestFlyCamera in the scene" );
				return;
			}
			var pos = TerrainWorldUnits.EngineToMeters( camera.WorldPosition );
			x = pos.x;
			y = pos.y;
		}

		var settings = manager.BuildGenerationSettings();
		var chunkSize = Math.Max( 32f, manager.ChunkSizeMeters );
		var coord = TerrainChunkStreaming.WorldToChunkCoord( x, y, settings.TotalWorldRadiusMeters, chunkSize );
		var g = TerrainMeshBuilder.BuildChunkGeometry(
			settings,
			TerrainPreviewBackendRegistry.Active,
			coord,
			chunkSize,
			manager.ChunkVerticesPerSide,
			manager.ChunkHeightSmoothPasses,
			manager.ChunkHeightSmoothStrength01,
			0 );

		var sb = new System.Text.StringBuilder( 64 * 1024 );
		sb.Append( "# chunk " ).Append( coord.X ).Append( ' ' ).Append( coord.Y )
			.Append( " min " ).Append( g.ChunkMinX ).Append( ' ' ).Append( g.ChunkMinY )
			.Append( " step " ).Append( g.Step ).Append( " verts " ).Append( g.VerticesPerSide )
			.Append( " ledgeDiagonals " ).Append( TerrainMeshBuilder.LedgeAwareDiagonals ? 1 : 0 ).Append( '\n' );
		var n = g.VerticesPerSide;
		for ( var i = 0; i < g.Heights.Length; i++ )
		{
			var ix = i % n;
			var iy = i / n;
			var wx = g.ChunkMinX + (ix * g.Step) + g.OffsetX[i];
			var wy = g.ChunkMinY + (iy * g.Step) + g.OffsetY[i];
			var side = !g.Samples[i].Ledge.Present ? 0 : g.Samples[i].Ledge.DistanceMeters >= 0f ? 1 : -1;
			sb.Append( "v " ).Append( wx.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture ) ).Append( ' ' )
				.Append( wy.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture ) ).Append( ' ' )
				.Append( g.Heights[i].ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture ) ).Append( ' ' )
				.Append( side ).Append( '\n' );
		}
		for ( var i = 0; i + 2 < g.Indices.Length; i += 3 )
			sb.Append( "f " ).Append( g.Indices[i] + 1 ).Append( ' ' ).Append( g.Indices[i + 1] + 1 ).Append( ' ' ).Append( g.Indices[i + 2] + 1 ).Append( '\n' );

		FileSystem.Data.CreateDirectory( "terrain_dump" );
		var path = $"terrain_dump/chunk_{coord.X}_{coord.Y}_{(TerrainMeshBuilder.LedgeAwareDiagonals ? "ledge" : "plain")}.txt";
		FileSystem.Data.WriteAllText( path, sb.ToString() );
		Log.Info( $"[TerrainDev] wrote {path} ({g.Heights.Length} verts, {g.Indices.Length / 3} tris) for chunk {coord} at X {x:0} m Y {y:0} m" );
	}

	[ConCmd( "terrain_ledge_diagonals" )]
	public static void ConCmdLedgeDiagonals( string on = "1" )
	{
		TerrainMeshBuilder.LedgeAwareDiagonals = on != "0";
		Log.Info( $"[TerrainDev] ledge-aware diagonals {(TerrainMeshBuilder.LedgeAwareDiagonals ? "on" : "off")} (rebuilt chunks only)" );
	}

	static bool TryParseMeters( string text, out float value )
		=> float.TryParse( text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value );
}
