namespace Survival;

/// <summary>
/// Dev console commands for the streamed terrain test scene.
/// <para><c>terrain_tp_biome &lt;clover|redwood|amber|mountain&gt; [cliff] [minStepMeters]</c> — spiral-scan the live generation
/// settings from the world centre for the nearest land of that biome (with <c>cliff</c>: the nearest spot where
/// the ground steps ≥ minStep (default 2.5) m within 4 m, i.e. a Redwood ledge) and park the <see cref="TerrainTestFlyCamera"/>
/// above it looking down at it. Scan only — nothing is spawned or changed.</para>
/// </summary>
public static partial class TerrainDevCommands
{
	const float ScanStepMeters = 40f;
	const float CliffProbeMeters = 4f;
	const float CliffMinStepMeters = 2.5f;

	/// <summary>Usage: <c>terrain_cam &lt;x&gt; &lt;y&gt; &lt;z&gt; [pitch] [yaw]</c> — park the fly camera at world metres looking along pitch / yaw (degrees, pitch + = down). For far-terrain and horizon checks from the editor, which has no mouse.</summary>
	[ConCmd( "terrain_cam" )]
	public static void ConCmdCamera( float xMeters = 0f, float yMeters = 0f, float zMeters = 300f, float pitchDegrees = 10f, float yawDegrees = 0f )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var camera = scene.IsValid() ? scene.GetAllComponents<TerrainTestFlyCamera>().FirstOrDefault() : null;
		if ( camera is null || !camera.IsValid() )
		{
			Log.Warning( "[TerrainDev] terrain_cam: no TerrainTestFlyCamera in the scene" );
			return;
		}

		var pos = TerrainWorldUnits.MetersToEngine( new Vector3( xMeters, yMeters, zMeters ) );
		var forward = new Angles( pitchDegrees, yawDegrees, 0f ).Forward;
		camera.TeleportTo( pos, pos + (forward * 1000f) );
		Log.Info( $"[TerrainDev] camera at {xMeters:0},{yMeters:0},{zMeters:0} m pitch {pitchDegrees:0} yaw {yawDegrees:0}" );
	}

	/// <summary>Usage: <c>terrain_far 0|1</c> — toggle the distant terrain tier (rebuilds it).</summary>
	[ConCmd( "terrain_far" )]
	public static void ConCmdFar( string on = "1" )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		if ( manager is null || !manager.IsValid() )
			return;

		manager.FarTerrainEnabled = on != "0";
		Log.Info( $"[TerrainDev] far terrain {( manager.FarTerrainEnabled ? "on" : "off" )}" );
	}

	// ---- stream-offset rig (flash probe): the stream origin travels while the camera stays still, so chunk
	// hand-overs happen in front of a parked camera where the flash probe can see them.
	static Vector3 _streamOffsetDirEngine;
	static float _streamOffsetMetersPerSecond;
	static float _streamOffsetSeconds;
	static float _streamOffsetStartMeters;
	static RealTimeSince _streamOffsetSince;

	/// <summary>Engine-unit offset added to the stream origin by <c>terrain_stream_offset</c> (zero when idle).</summary>
	internal static Vector3 CurrentStreamOffsetEngine()
	{
		if ( _streamOffsetMetersPerSecond <= 0f )
			return Vector3.Zero;
		var t = Math.Min( (float)_streamOffsetSince, _streamOffsetSeconds );
		return _streamOffsetDirEngine * TerrainWorldUnits.MetersToEngine( _streamOffsetStartMeters + (_streamOffsetMetersPerSecond * t) );
	}

	/// <summary>Usage: <c>terrain_stream_offset [m/s] [seconds] [startMeters]</c> — move the STREAM origin (not the camera) along the camera's heading, starting startMeters ahead (negative = behind, so the load edge sweeps through the view); 0 resets.</summary>
	[ConCmd( "terrain_stream_offset" )]
	public static void ConCmdStreamOffset( float metersPerSecond = 55f, float seconds = 20f, float startMeters = 0f )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var cam = scene.IsValid() ? scene.Camera : null;
		if ( metersPerSecond <= 0f || cam is null || !cam.IsValid() )
		{
			_streamOffsetMetersPerSecond = 0f;
			Log.Info( "[TerrainDev] stream offset reset" );
			return;
		}

		var forward = cam.WorldRotation.Forward.WithZ( 0f );
		_streamOffsetDirEngine = forward.LengthSquared > 1e-6f ? forward.Normal : Vector3.Forward;
		_streamOffsetMetersPerSecond = metersPerSecond;
		_streamOffsetSeconds = Math.Max( 1f, seconds );
		_streamOffsetStartMeters = startMeters;
		_streamOffsetSince = 0f;
		Log.Info( $"[TerrainDev] stream origin travels {metersPerSecond:0} m/s for {seconds:0} s along the camera heading from {startMeters:0} m (camera stays)" );
	}

	/// <summary>Usage: <c>terrain_stream_radius [chunks] [margin]</c> — set the live stream radius / unload margin (flash probe rigs: 5 puts the hand-over at 320 m, still past the elm's LOD3 switch).</summary>
	[ConCmd( "terrain_stream_radius" )]
	public static void ConCmdStreamRadius( int chunks = 8, int margin = 2 )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		if ( manager is null || !manager.IsValid() )
			return;

		manager.StreamRadiusChunks = Math.Clamp( chunks, 1, 16 );
		manager.StreamUnloadMarginChunks = Math.Clamp( margin, 0, 8 );
		Log.Info( $"[TerrainDev] stream radius {manager.StreamRadiusChunks} chunks, unload margin {manager.StreamUnloadMarginChunks} (applies at the next refresh)" );
	}

	/// <summary>Usage: <c>far_trees_mode [0|1|2]</c> — dev: 0 instanced far model, 1 one DrawModel per tree (first 400), 2 instanced dev box.</summary>
	[ConCmd( "far_trees_mode" )]
	public static void ConCmdFarTreesMode( int mode = 0 )
	{
		TerrainWorldManager.FarDrawMode = mode;
		Log.Info( $"[TerrainDev] far trees draw mode {mode}" );
	}

	/// <summary>Usage: <c>far_trees [0|1]</c> — the far-tree ring (each tree's lowest LOD past the loaded chunks) on / off, live.</summary>
	[ConCmd( "far_trees" )]
	public static void ConCmdFarTrees( string on = "1" )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		if ( manager is null || !manager.IsValid() )
			return;

		manager.VegetationFarCardsEnabled = on != "0";
		Log.Info( $"[TerrainDev] far trees {( manager.VegetationFarCardsEnabled ? "on" : "off" )}" );
	}

	/// <summary>Usage: <c>terrain_tp_river [index] [mid|source|mouth]</c> — park the fly camera over river N (its middle, its source or its mouth) looking downstream; no argument lists the rivers.</summary>
	[ConCmd( "terrain_tp_river" )]
	public static void ConCmdTeleportRiver( int index = -1, string where = "mid" )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		var camera = scene.IsValid() ? scene.GetAllComponents<TerrainTestFlyCamera>().FirstOrDefault() : null;
		if ( manager is null || !manager.IsValid() )
		{
			Log.Warning( "[TerrainDev] needs a TerrainWorldManager in the scene" );
			return;
		}

		var settings = manager.BuildGenerationSettings();
		TerrainPreviewLandDiskFields.EnsureReady( settings );
		var net = TerrainPreviewLandDiskFields.RiverNetwork;
		if ( net is null || net.RiverCount == 0 )
		{
			Log.Info( "[TerrainDev] no rivers in this world" );
			return;
		}

		if ( index < 0 )
		{
			for ( var i = 0; i < net.RiverCount; i++ )
			{
				TerrainPreviewRivers.TryGetRiverMidpoint( net, i, out var p, out _, out var lvl );
				Log.Info( $"[TerrainDev] river {i}: {net.RiverSegmentCount[i]} segments, mid {p.x:0},{p.y:0} m at {lvl:0.#} m" );
			}
			return;
		}

		var at = (where ?? "mid").ToLowerInvariant() switch
		{
			"source" or "start" => TerrainPreviewRivers.RiverPoint.Source,
			"mouth" or "end" => TerrainPreviewRivers.RiverPoint.Mouth,
			_ => TerrainPreviewRivers.RiverPoint.Mid,
		};
		if ( !TerrainPreviewRivers.TryGetRiverPoint( net, index, at, out var point, out var forward, out var level ) )
		{
			Log.Warning( $"[TerrainDev] river {index} does not exist ({net.RiverCount} rivers)" );
			return;
		}

		if ( camera is null || !camera.IsValid() )
			return;

		// The ends get a close look (a source is a few metres across); the middle the wide downstream view.
		var back = at == TerrainPreviewRivers.RiverPoint.Mid ? 120f : 30f;
		var up = at == TerrainPreviewRivers.RiverPoint.Mid ? 60f : 14f;
		var ahead = at == TerrainPreviewRivers.RiverPoint.Mid ? 150f : 20f;
		var eye = TerrainWorldUnits.MetersToEngine( new Vector3( point.x - (forward.x * back), point.y - (forward.y * back), level + up ) );
		var look = TerrainWorldUnits.MetersToEngine( new Vector3( point.x + (forward.x * ahead), point.y + (forward.y * ahead), level ) );
		camera.TeleportTo( eye, look );
		Log.Info( $"[TerrainDev] camera over river {index} {at} at {point.x:0},{point.y:0} m (water {level:0.#} m)" );
	}

	/// <summary>Usage: <c>terrain_river_check [index]</c> — measures river N (all when omitted): at every segment, water level against the pre-river ground at the centre line and both channel edges. Water above ground is the bug.</summary>
	[ConCmd( "terrain_river_check" )]
	public static void ConCmdRiverCheck( int index = -1 )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		if ( manager is null || !manager.IsValid() )
			return;

		var settings = manager.BuildGenerationSettings();
		TerrainPreviewLandDiskFields.EnsureReady( settings );
		var net = TerrainPreviewLandDiskFields.RiverNetwork;
		if ( net is null || net.RiverCount == 0 )
		{
			Log.Info( "[TerrainDev] no rivers" );
			return;
		}

		for ( var r = 0; r < net.RiverCount; r++ )
		{
			if ( index >= 0 && r != index )
				continue;
			var first = net.RiverFirstSegment[r];
			var count = net.RiverSegmentCount[r];
			var worstAbove = float.MinValue;
			var worstBelow = float.MaxValue;
			var above = 0;
			var worstX = 0f;
			var worstY = 0f;
			var bedMax = 0f;
			var rimMin = float.MaxValue;
			var rimMax = float.MinValue;
			var slide = Math.Clamp( settings.RiverBankSlideWidthMeters, 0.5f, 30f );
			for ( var s = first; s < first + count; s++ )
			{
				var seg = net.Segments[s];
				var mx = (seg.Ax + seg.Bx) * 0.5f;
				var my = (seg.Ay + seg.By) * 0.5f;
				var level = (seg.LevelA + seg.LevelB) * 0.5f;
				var half = (seg.HalfA + seg.HalfB) * 0.5f;
				var dir = new Vector2( seg.Bx - seg.Ax, seg.By - seg.Ay ).Normal;
				var side = new Vector2( -dir.y, dir.x );
				var g = TerrainPreviewPipeline.SampleDryLandHeightMetersNoRivers( settings, mx, my );
				g = Math.Min( g, TerrainPreviewPipeline.SampleDryLandHeightMetersNoRivers( settings, mx + (side.x * half), my + (side.y * half) ) );
				g = Math.Min( g, TerrainPreviewPipeline.SampleDryLandHeightMetersNoRivers( settings, mx - (side.x * half), my - (side.y * half) ) );
				var delta = level - g;   // + = water above the ground
				if ( delta > 0.05f )
					above++;
				if ( delta > worstAbove )
				{
					worstAbove = delta;
					worstX = mx;
					worstY = my;
				}
				worstBelow = Math.Min( worstBelow, delta );

				// The carved channel itself: bed depth under the water at the centre line, and how far the rim
				// (the top of the slide wall) stands above the water on each side.
				bedMax = Math.Max( bedMax, level - TerrainPreviewPipeline.Sample( settings, mx, my ).HeightMeters );
				var reach = half + slide;
				var rimL = TerrainPreviewPipeline.Sample( settings, mx + (side.x * reach), my + (side.y * reach) ).HeightMeters - level;
				var rimR = TerrainPreviewPipeline.Sample( settings, mx - (side.x * reach), my - (side.y * reach) ).HeightMeters - level;
				rimMin = Math.Min( rimMin, Math.Min( rimL, rimR ) );
				rimMax = Math.Max( rimMax, Math.Max( rimL, rimR ) );
			}
			Log.Info( $"[TerrainDev] river {r}: {count} segments, water above ground at {above}, worst +{worstAbove:0.00} m at {worstX:0},{worstY:0}, deepest cut {-worstBelow:0.0} m, bed up to {bedMax:0.0} m deep, rim {rimMin:+0.0;-0.0}..{rimMax:+0.0;-0.0} m above the water" );
		}
	}

	/// <summary>Usage: <c>terrain_probe &lt;x&gt; &lt;y&gt; [halfSpan] [step]</c> — prints pipeline heights on a cross through the point at fine spacing (default ±6 m every 0.5 m) to see how wide a bump or spike really is.</summary>
	[ConCmd( "terrain_probe" )]
	public static void ConCmdProbe( float xMeters = 0f, float yMeters = 0f, float halfSpan = 6f, float step = 0.5f )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		if ( manager is null || !manager.IsValid() )
			return;

		var settings = manager.BuildGenerationSettings();
		step = Math.Max( 0.1f, step );
		var sbx = new System.Text.StringBuilder();
		var sby = new System.Text.StringBuilder();
		for ( var o = -halfSpan; o <= halfSpan + 0.001f; o += step )
		{
			sbx.Append( TerrainPreviewPipeline.Sample( settings, xMeters + o, yMeters ).HeightMeters.ToString( "0.00" ) ).Append( ' ' );
			sby.Append( TerrainPreviewPipeline.Sample( settings, xMeters, yMeters + o ).HeightMeters.ToString( "0.00" ) ).Append( ' ' );
		}
		Log.Info( $"[TerrainDev] probe {xMeters:0},{yMeters:0} along X: {sbx}" );
		Log.Info( $"[TerrainDev] probe {xMeters:0},{yMeters:0} along Y: {sby}" );
		var sbn = new System.Text.StringBuilder();
		var sbw = new System.Text.StringBuilder();
		for ( var o = -halfSpan; o <= halfSpan + 0.001f; o += step )
		{
			sbn.Append( TerrainPreviewPipeline.SampleDryLandHeightMetersNoRivers( settings, xMeters + o, yMeters ).ToString( "0.00" ) ).Append( ' ' );
			var w = TerrainPreviewBiomeResolver.SamplePlacementWeights( settings, xMeters + o, yMeters, 0.5f );
			sbw.Append( $"c{w.Clover:0.00}/r{w.Redwood:0.00}/a{w.Amber:0.00}/m{w.Mountain:0.00} " );
		}
		Log.Info( $"[TerrainDev] probe no-rivers along X: {sbn}" );
		for ( var o = -halfSpan; o <= halfSpan + 0.001f; o += step * 4f )
			Log.Info( $"[TerrainDev] ledge @y{o:+0.0}: {TerrainPreviewBiomeTerrainShaper.DebugRedwoodLedge( settings, xMeters, yMeters + o )}" );
		Log.Info( $"[TerrainDev] probe weights along X: {sbw}" );
	}

	[ConCmd( "terrain_tp_biome" )]
	public static void ConCmdTeleportToBiome( string biome = "redwood", string mode = "", string minStepMeters = "2.5" )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
		{
			Log.Warning( "[TerrainDev] no active scene" );
			return;
		}

		var manager = scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		var camera = scene.GetAllComponents<TerrainTestFlyCamera>().FirstOrDefault();
		if ( manager is null || !manager.IsValid() || camera is null || !camera.IsValid() )
		{
			Log.Warning( "[TerrainDev] needs a TerrainWorldManager and a TerrainTestFlyCamera in the scene" );
			return;
		}

		var target = ParseBiome( biome );
		if ( target == TerrainPreviewBiomeId.None )
		{
			Log.Warning( $"[TerrainDev] unknown biome '{biome}' — use clover, redwood, amber or mountain" );
			return;
		}

		var wantCliff = string.Equals( mode, "cliff", StringComparison.OrdinalIgnoreCase );
		if ( !float.TryParse( minStepMeters, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minStep ) || minStep < 0.5f )
			minStep = CliffMinStepMeters;
		var settings = manager.BuildGenerationSettings();
		var backend = TerrainPreviewBackendRegistry.Active;
		var landRadius = Math.Max( ScanStepMeters, settings.LandRadiusMeters );

		var sw = System.Diagnostics.Stopwatch.StartNew();
		if ( !TrySpiralFind( settings, backend, target, wantCliff, minStep, landRadius, out var x, out var y, out var groundZ, out var samples ) )
		{
			Log.Warning( $"[TerrainDev] no {target}{(wantCliff ? " cliff" : "")} found inside {landRadius:0} m ({samples} samples, {sw.ElapsedMilliseconds} ms)" );
			return;
		}

		// Park 40 m south of the spot, 25 m up, looking down at it.
		var eye = new Vector3( x, y - 40f, groundZ + 25f );
		var eyeEngine = TerrainWorldUnits.MetersToEngine( eye );
		var lookEngine = TerrainWorldUnits.MetersToEngine( new Vector3( x, y, groundZ ) );
		camera.TeleportTo( eyeEngine, lookEngine );
		Log.Info( $"[TerrainDev] {target}{(wantCliff ? " cliff" : "")} at X {x:0} m Y {y:0} m Z {groundZ:0.#} m ({samples} samples, {sw.ElapsedMilliseconds} ms) — camera parked above it" );
	}

	static TerrainPreviewBiomeId ParseBiome( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return TerrainPreviewBiomeId.None;
		name = name.Trim().ToLowerInvariant();
		if ( name.StartsWith( "clover" ) ) return TerrainPreviewBiomeId.CloverHills;
		if ( name.StartsWith( "red" ) ) return TerrainPreviewBiomeId.RedwoodForest;
		if ( name.StartsWith( "amber" ) || name.StartsWith( "dune" ) ) return TerrainPreviewBiomeId.AmberDunes;
		if ( name.StartsWith( "mount" ) ) return TerrainPreviewBiomeId.Mountain;
		return TerrainPreviewBiomeId.None;
	}

	/// <summary>Square spiral from the centre outward so the first hit is the nearest one (to within a step).</summary>
	static bool TrySpiralFind(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		TerrainPreviewBiomeId target,
		bool wantCliff,
		float minStep,
		float landRadius,
		out float x,
		out float y,
		out float groundZ,
		out int samples )
	{
		x = y = groundZ = 0f;
		samples = 0;
		var maxRing = (int)MathF.Ceiling( landRadius / ScanStepMeters );
		for ( var ring = 0; ring <= maxRing; ring++ )
		{
			for ( var iy = -ring; iy <= ring; iy++ )
			{
				for ( var ix = -ring; ix <= ring; ix++ )
				{
					if ( Math.Max( Math.Abs( ix ), Math.Abs( iy ) ) != ring )
						continue;
					var px = ix * ScanStepMeters;
					var py = iy * ScanStepMeters;
					samples++;
					if ( !IsBiomeLand( settings, backend, target, px, py, out var h ) )
						continue;
					if ( !wantCliff )
					{
						x = px; y = py; groundZ = h;
						return true;
					}
					// Refine: look for a step within this scan cell on a 4 m lattice.
					for ( var oy = -ScanStepMeters * 0.5f; oy <= ScanStepMeters * 0.5f; oy += CliffProbeMeters )
					{
						for ( var ox = -ScanStepMeters * 0.5f; ox <= ScanStepMeters * 0.5f; ox += CliffProbeMeters )
						{
							samples++;
							if ( TryCliffAt( settings, backend, target, minStep, px + ox, py + oy, out var cz ) )
							{
								x = px + ox; y = py + oy; groundZ = cz;
								return true;
							}
						}
					}
				}
			}
		}
		return false;
	}

	static bool IsBiomeLand( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, TerrainPreviewBiomeId target, float x, float y, out float heightMeters )
	{
		var s = backend.Sample( settings, x, y );
		heightMeters = s.HeightMeters;
		if ( !s.IsInsideWorld || !s.IsOnLand || !s.HasLandWeights )
			return false;
		if ( TerrainPreviewBiomeResolver.PickDominantPlacementBiome( s.LandWeights ) != target )
			return false;
		// Deep inside the biome only — edges blend two sculpts and are not representative.
		var w = s.LandWeights;
		var share = target switch
		{
			TerrainPreviewBiomeId.CloverHills => w.Clover,
			TerrainPreviewBiomeId.RedwoodForest => w.Redwood,
			TerrainPreviewBiomeId.AmberDunes => w.Amber,
			TerrainPreviewBiomeId.Mountain => w.Mountain,
			_ => 0f,
		};
		return share >= 0.9f * Math.Max( 0.0001f, w.Total );
	}

	/// <summary>True when the ground steps at least <see cref="CliffMinStepMeters"/> between here and a neighbour 4 m away.</summary>
	static bool TryCliffAt( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, TerrainPreviewBiomeId target, float minStep, float x, float y, out float heightMeters )
	{
		if ( !IsBiomeLand( settings, backend, target, x, y, out heightMeters ) )
			return false;
		var hx = backend.Sample( settings, x + CliffProbeMeters, y ).HeightMeters;
		var hy = backend.Sample( settings, x, y + CliffProbeMeters ).HeightMeters;
		return Math.Abs( hx - heightMeters ) >= minStep || Math.Abs( hy - heightMeters ) >= minStep;
	}
}
