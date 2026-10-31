using System.Threading.Tasks;

namespace Survival;

public static partial class TerrainDevCommands
{
	/// <summary>
	/// Usage: <c>terrain_paint_probe [radius m]</c> — for lake-bed points around the camera (40 m grid), group by the
	/// biome the land resolver picks there and print each group's count, average splat inputs (land shares, mountain,
	/// wet, beach) and bed height. Finds why one side of a lake reads differently.
	/// </summary>
	[ConCmd( "terrain_paint_probe" )]
	public static void ConCmdPaintProbe( float radiusMeters = 1500f )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		var cam = scene.IsValid() ? scene.Camera : null;
		var settings = manager?.LoadSettings;
		if ( settings is null || cam is null )
			return;
		var c = TerrainWorldUnits.EngineToMeters( cam.WorldPosition );
		var groups = new Dictionary<string, (int N, float C, float R, float A, float M, float Wet, float Beach, float H)>();
		for ( var y = c.y - radiusMeters; y <= c.y + radiusMeters; y += 40f )
		{
			for ( var x = c.x - radiusMeters; x <= c.x + radiusMeters; x += 40f )
			{
				var sample = TerrainPreviewPipeline.Sample( settings, x, y );
				if ( !sample.IsInsideWorld || sample.HeightMeters >= settings.SeaLevelMeters - 0.05f )
					continue;
				var w = TerrainPreviewBiomeResolver.SampleLandBiomeWeights( settings, sample, x, y );
				var key = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, x, y ).BiomeId.ToString();
				groups.TryGetValue( key, out var g );
				groups[key] = (g.N + 1, g.C + w.Clover, g.R + w.Redwood, g.A + w.Amber, g.M + w.Mountain, g.Wet + sample.Wet01, g.Beach + sample.Beach01, g.H + sample.HeightMeters);
			}
		}
		foreach ( var (k, g) in groups )
			Log.Info( $"[TerrainDev] bed {k}: {g.N} pts, weights c{g.C / g.N:0.00}/r{g.R / g.N:0.00}/a{g.A / g.N:0.00}/m{g.M / g.N:0.00}, wet {g.Wet / g.N:0.00}, beach {g.Beach / g.N:0.00}, bed height {g.H / g.N:0.00} m" );
		if ( groups.Count == 0 )
			Log.Info( "[TerrainDev] terrain_paint_probe: no submerged ground in range" );
	}

	/// <summary>
	/// Usage: <c>terrain_paint_line x0 y0 x1 y1 [points]</c> — along a line (world metres), what the terrain splat is
	/// built from at each point (<see cref="TerrainGroundSplat"/>): height, bed or not, resolved biome, land weights
	/// (clover / redwood / amber / mountain), beach and wet. Finds which input makes a band or a step in the ground.
	/// </summary>
	[ConCmd( "terrain_bank_probe" )]
	public static void ConCmdBankProbe( float x, float y )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var settings = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault()?.LoadSettings : null;
		if ( settings is not null )
			Log.Info( $"[TerrainDev] bank {x:0},{y:0}: {TerrainPreviewPipeline.DebugShore( settings, x, y )}" );
	}

	/// <summary>
	/// Usage: <c>terrain_coast_scan [step m] [band m] [min step m]</c> — walks every coast in the world (the ocean rim and
	/// every lake, land within band metres of open water) on a step-metre grid and finds height DISCONTINUITIES: a rise
	/// between two grid points of at least min step that is 2.5× steeper than the stretch either side of it (a cut, not a
	/// hillside). Hits are grouped into ~80 m sites; prints the count, how many drop to water level and the 25 worst
	/// sites. Runs on worker threads (a full scan takes some seconds). Finds the "squares cut out" near water.
	/// </summary>
	[ConCmd( "terrain_coast_scan" )]
	public static void ConCmdCoastScan( float stepMeters = 4f, float bandMeters = 300f, float minStepMeters = 1f )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var settings = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault()?.LoadSettings : null;
		if ( settings is null )
			return;
		_ = CoastScanAsync( settings, Math.Clamp( stepMeters, 1f, 16f ), Math.Max( 20f, bandMeters ), Math.Max( 0.2f, minStepMeters ) );
	}

	static async Task CoastScanAsync( TerrainPreviewSettings settings, float step, float band, float minStep )
	{
		const float Tile = 64f;
		var sea = settings.SeaLevelMeters;
		var land = settings.LandRadiusMeters;
		var threshold = TerrainPreviewLandDiskFields.GetOpenWaterThreshold01( settings );
		var tiles = new List<Vector2>();
		int byOcean = 0, byRaster = 0, byMask = 0;
		for ( var ty = -land; ty < land; ty += Tile )
		{
			for ( var tx = -land; tx < land; tx += Tile )
			{
				var cx = tx + (Tile * 0.5f);
				var cy = ty + (Tile * 0.5f);
				var dist = MathF.Sqrt( (cx * cx) + (cy * cy) );
				if ( dist > land + Tile )
					continue;
				var nearOcean = land - dist < band + Tile;
				var rasterNear = TerrainPreviewLandDiskFields.SampleDistanceToOpenWaterMeters( settings, cx, cy ) < band + Tile;
				var maskNear = TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld( settings, cx, cy, out var m ) && m > threshold - 0.04f;
				if ( nearOcean ) byOcean++;
				if ( rasterNear ) byRaster++;
				if ( maskNear ) byMask++;
				if ( nearOcean || rasterNear || maskNear )
					tiles.Add( new Vector2( tx, ty ) );
			}
		}
		Log.Info( $"[TerrainDev] terrain_coast_scan: {tiles.Count} tiles of {Tile:0} m on a {step:0.#} m grid (ocean {byOcean}, raster {byRaster}, mask {byMask}, threshold {threshold:0.000})…" );

		var n = (int)MathF.Round( Tile / step );
		const int batches = 12;
		var tasks = new List<Task<List<(float Step, float X, float Y, float Lo, float Hi)>>>();
		for ( var b = 0; b < batches; b++ )
		{
			var bi = b;
			tasks.Add( GameTask.RunInThreadAsync( () =>
			{
				var found = new List<(float, float, float, float, float)>();
				var h = new float[n + 4, n + 4];
				for ( var t = bi; t < tiles.Count; t += batches )
				{
					var o = tiles[t];
					for ( var j = 0; j < n + 4; j++ )
					for ( var i = 0; i < n + 4; i++ )
						h[i, j] = TerrainPreviewPipeline.Sample( settings, o.x + ((i - 1) * step), o.y + ((j - 1) * step) ).HeightMeters;
					for ( var j = 1; j < n + 1; j++ )
					for ( var i = 1; i < n + 1; i++ )
					{
						Check( h[i - 1, j], h[i, j], h[i + 1, j], h[i + 2, j], o.x + ((i - 0.5f) * step), o.y + ((j - 1) * step) );
						Check( h[i, j - 1], h[i, j], h[i, j + 1], h[i, j + 2], o.x + ((i - 1) * step), o.y + ((j - 0.5f) * step) );
					}
				}
				return found;

				void Check( float a, float b0, float b1, float c, float x, float y )
				{
					var d = MathF.Abs( b1 - b0 );
					if ( d < minStep || d < (2.5f * Math.Max( MathF.Abs( b0 - a ), MathF.Abs( c - b1 ) )) + 0.3f )
						return;
					if ( Math.Max( b0, b1 ) < sea - 0.5f )
						return;   // under water: the bed may shelve
					found.Add( (d, x, y, Math.Min( b0, b1 ), Math.Max( b0, b1 )) );
				}
			} ) );
		}
		var all = new List<(float Step, float X, float Y, float Lo, float Hi)>();
		foreach ( var task in tasks )
			all.AddRange( await task );
		var sites = all.GroupBy( q => (MathF.Floor( q.X / 80f ), MathF.Floor( q.Y / 80f )) )
			.Select( g => (Worst: g.MaxBy( q => q.Step ), Count: g.Count()) )
			.OrderByDescending( s => s.Worst.Step ).ToList();
		var toWater = sites.Count( s => s.Worst.Lo <= sea + 0.15f );
		Log.Info( $"[TerrainDev] terrain_coast_scan: {all.Count} cuts ≥ {minStep:0.0} m in {sites.Count} sites, {toWater} sites drop to water level" );
		string Where( float x, float y )
		{
			var r = TerrainPreviewLandDiskFields.SampleRiver( settings, x, y );
			var lake = TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld( settings, x, y, out var lm ) ? lm : -1f;
			return $"{(r.Near ? $"river {r.Distance:0} m off" : "no river")}, lake mask {lake:0.000}, {land - MathF.Sqrt( (x * x) + (y * y) ):0} m in from the rim";
		}
		foreach ( var (w, count) in sites.Where( s => s.Worst.Lo <= sea + 0.15f ).Take( 25 ) )
			Log.Info( $"[TerrainDev] TO WATER cut {w.Step:0.0} m at {w.X:0},{w.Y:0}: {w.Lo:0.0} → {w.Hi:0.0} m ({count} hits) — {Where( w.X, w.Y )}" );
		foreach ( var (w, count) in sites.Take( 12 ) )
			Log.Info( $"[TerrainDev] cut {w.Step:0.0} m at {w.X:0},{w.Y:0}: {w.Lo:0.0} → {w.Hi:0.0} m ({count} hits) — {Where( w.X, w.Y )}" );
		var byRiver = sites.Count( s => TerrainPreviewLandDiskFields.SampleRiver( settings, s.Worst.X, s.Worst.Y ).Near );
		Log.Info( $"[TerrainDev] terrain_coast_scan: {byRiver} of {sites.Count} sites are on a river" );
	}

	/// <summary>
	/// Usage: <c>terrain_river_trees [river]</c> — re-plan the large trees (the planner the scatter and the far ring use)
	/// on every chunk river N runs through and count the ones standing in its channel, with the first few positions.
	/// </summary>
	[ConCmd( "terrain_river_trees" )]
	public static void ConCmdRiverTrees( int index = 0 )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		var settings = manager?.LoadSettings;
		var net = TerrainPreviewLandDiskFields.RiverNetwork;
		if ( settings is null || net is null )
			return;
		var chunkSize = Math.Max( 32f, manager.ChunkSizeMeters );
		var coords = new HashSet<TerrainChunkCoord>();
		foreach ( var seg in net.Segments )
		{
			if ( seg.River == index )
				coords.Add( TerrainChunkStreaming.WorldToChunkCoord( seg.Ax, seg.Ay, settings.TotalWorldRadiusMeters, chunkSize ) );
		}
		var trees = new List<TerrainVegetationScatter.FarTree>();
		foreach ( var c in coords )
			TerrainVegetationScatter.EnumerateTrees( settings, TerrainPreviewBackendRegistry.Active, c, chunkSize, manager.DevVegetationOptions, trees );
		var wet = trees.Where( t => TerrainPreviewLandDiskFields.SampleRiver( settings, t.X, t.Y ) is { Near: true } h && h.Distance <= h.HalfWidth ).ToList();
		Log.Info( $"[TerrainDev] river {index}: {coords.Count} chunks, {trees.Count} trees planned, {wet.Count} in the channel" );
		foreach ( var t in wet.Take( 5 ) )
		{
			var smp = TerrainPreviewPipeline.Sample( settings, t.X, t.Y );
			Log.Info( $"[TerrainDev]   tree at {t.X:0.0},{t.Y:0.0}: planned h {t.HeightMeters:0.00}, sample now h {smp.HeightMeters:0.00} onLand {smp.IsOnLand} riverWater {smp.RiverWater}" );
		}
	}

	[ConCmd( "terrain_paint_line" )]
	public static void ConCmdPaintLine( float x0, float y0, float x1, float y1, int points = 16 )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		var settings = manager?.LoadSettings;
		if ( settings is null )
			return;
		points = Math.Clamp( points, 2, 64 );
		for ( var i = 0; i < points; i++ )
		{
			var t = i / (float)(points - 1);
			var x = x0 + ((x1 - x0) * t);
			var y = y0 + ((y1 - y0) * t);
			var sample = TerrainPreviewPipeline.Sample( settings, x, y );
			var display = TerrainShorelineDisplay.IsDisplayWaterColor( settings, x, y );
			var resolved = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, x, y );
			var w = TerrainPreviewBiomeResolver.SampleLandBiomeWeights( settings, sample, x, y );
			Log.Info( $"[TerrainDev] {x:0},{y:0}: h {sample.HeightMeters:0.00} {(display ? "WATER " : "")}{resolved.BiomeId} w c{w.Clover:0.00}/r{w.Redwood:0.00}/a{w.Amber:0.00}/m{w.Mountain:0.00} beach {sample.Beach01:0.00} wet {sample.Wet01:0.00}" );
		}
	}
}
