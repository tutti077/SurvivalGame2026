namespace Survival;

/// <summary>
/// River water (Mark 2026-10-07: the channel is carved as a bed below the water and the water fills it;
/// Mark 2026-10-21: "carve out the riverbed and add flowing water", clear, see-through at any angle). Two parts:
/// <list type="bullet">
/// <item>The surface: ONE mesh for every river segment of every loaded chunk, on a single <c>RiverWater</c>
/// object under the manager, rebuilt (throttled) whenever a chunk with river in it loads or unloads. One draw
/// matters: the surface refracts through a frame-buffer copy, and with a strip per chunk a far strip drawn
/// before a near one (translucents sort back to front) was already in the near strip's copy wherever they
/// overlapped on screen — looking along a river every pixel was water-behind-water and the river went flat grey.
/// Quads sit at the water level (sloped along the river, flat across) in <c>materials/water/riverwater.vmat</c>;
/// the UVs are metres along the river from its source (<see cref="TerrainPreviewRivers.Segment.ArcA"/>) and
/// metres across the channel, so the shader's flow always runs source → mouth.</item>
/// <item>The swim volume: for every segment whose midpoint lies in a chunk, a "water"-tagged box trigger under that
/// chunk, along the segment from the surface down past the bed, with a <see cref="WaterVolume"/> whose fluid
/// velocity is the river's current (River Flow Speed). The pawn's swim mode keys off the tag; the velocity is for
/// floating things (boats later). Destroyed with the chunk.</item>
/// </list>
/// Far-tier chunks carry neither.
/// </summary>
public sealed partial class TerrainWorldManager
{
	const string RiverWaterMaterialPath = "materials/water/riverwater.vmat";
	const string RiverWaterTag = "water";
	/// <summary>The surface quad reaches this far past the channel edge so the bank never shows a gap.</summary>
	const float RiverSurfaceOverlapMeters = 0.6f;
	/// <summary>Surface height above the river level: a hair, so it never z-fights the bed at the water's edge.</summary>
	const float RiverSurfaceLiftMeters = 0.08f;
	/// <summary>Chunk loads come in bursts: the merged surface rebuilds at most this often.</summary>
	const float RiverSurfaceRebuildIntervalSeconds = 0.25f;

	static Material _riverWaterMaterial;
	static bool _riverWaterMaterialWarned;

	/// <summary>
	/// One river segment of the merged surface: its network index and, per end, what the shader needs to know about the
	/// water there — carved depth, current (from the slope), biome water tint, and how high the bank plus treeline stand
	/// on each side (the reflection mirrors that silhouette instead of sky).
	/// </summary>
	readonly struct RiverSurfaceSegment
	{
		public int Index { get; init; }
		public RiverSurfaceEnd A { get; init; }
		public RiverSurfaceEnd B { get; init; }
		/// <summary>Fraction of the segment (from A) the surface reaches: below 1 where B lies out in a lake / the sea.</summary>
		public float ReachB { get; init; }
	}

	/// <summary>The river strip stops this far past the shoreline: the lake / sea surface owns the water from there (it has taken over up the channel already).</summary>
	const float RiverMouthOverlapMeters = 1f;

	readonly struct RiverSurfaceEnd
	{
		public float Depth { get; init; }
		/// <summary>Current at this end as a multiple of River Flow Speed: 1 at the network's median slope.</summary>
		public float SpeedMul { get; init; }
		/// <summary>0 = Redwood tannin, 0.5 = clear (Clover / Amber), 1 = Mountain glacial — see riverwater.shader.</summary>
		public float Tint01 { get; init; }
		/// <summary>Bank + treeline height above the water on the -side / +side, metres.</summary>
		public float OccluderMinus { get; init; }
		public float OccluderPlus { get; init; }
	}

	/// <summary>Current multiple range: calm pools on the flats, three times the median on the steepest stretches.</summary>
	const float RiverSpeedMulMin = 0.35f;
	const float RiverSpeedMulMax = 3f;
	/// <summary>The mirrored treeline stands this far back from the rim (trunks are not on the slide wall).</summary>
	const float RiverTreelineSetbackMeters = 6f;

	/// <summary>
	/// Typical crown height per biome for the mirrored treeline, metres (Clover elms / clover trees, Redwood giants,
	/// mountain conifers). Scaled by the scatter's forest cover at that spot, so open meadow mirrors only the bank.
	/// </summary>
	static float RiverTreelineHeightMeters( TerrainPreviewBiomeId biome ) => biome switch
	{
		TerrainPreviewBiomeId.CloverHills => 12f,
		TerrainPreviewBiomeId.RedwoodForest => 35f,
		TerrainPreviewBiomeId.Mountain => 16f,
		_ => 0f,
	};

	static TerrainPreviewRivers.Network _riverSpeedNetwork;
	static float _riverMedianSlope;

	/// <summary>Median water-level slope of the network (cached per network): that slope flows at River Flow Speed.</summary>
	static float RiverMedianSlope( TerrainPreviewRivers.Network net )
	{
		if ( ReferenceEquals( net, _riverSpeedNetwork ) )
			return _riverMedianSlope;
		var slopes = new List<float>( net.Segments.Length );
		for ( var i = 0; i < net.Segments.Length; i++ )
		{
			if ( net.Segments[i].ArcB - net.Segments[i].ArcA > 0.5f )
				slopes.Add( SegmentSlope( net, i ) );
		}
		slopes.Sort();
		_riverMedianSlope = slopes.Count == 0 ? 1e-5f : Math.Max( 1e-5f, slopes[slopes.Count / 2] );
		_riverSpeedNetwork = net;
		return _riverMedianSlope;
	}

	static float SegmentSlope( TerrainPreviewRivers.Network net, int index )
	{
		ref var seg = ref net.Segments[index];
		var len = seg.ArcB - seg.ArcA;
		return len > 0.5f ? Math.Max( 0f, seg.LevelA - seg.LevelB ) / len : 0f;
	}

	static TerrainPreviewRivers.Network _riverTravelNetwork;
	static float[] _riverTravel;

	/// <summary>
	/// The river surface's flow coordinate (Mark 2026-10-25: ripples "moving back and forth … speed up and slow down"):
	/// TRAVEL from the source, metres ÷ the local current multiple. riverwater.shader scrolls it at one constant
	/// FlowSpeed, which moves each ripple at its own local speed without ever stretching the pattern — the old two-phase
	/// scroll restarted every 3 s and surged. [0..n) = travel at each segment's A end, [n..2n) = each segment's span.
	/// Cached per network.
	/// </summary>
	static float[] RiverTravel( TerrainPreviewRivers.Network net )
	{
		if ( ReferenceEquals( net, _riverTravelNetwork ) && _riverTravel is not null )
			return _riverTravel;
		var n = net.Segments.Length;
		var travel = new float[n * 2];
		var t = 0f;
		for ( var i = 0; i < n; i++ )
		{
			if ( i == 0 || net.Segments[i].River != net.Segments[i - 1].River )
				t = 0f;
			ref var s = ref net.Segments[i];
			var speed = (RiverSpeedMulAtEnd( net, i, false ) + RiverSpeedMulAtEnd( net, i, true )) * 0.5f;
			var span = Math.Max( 0f, s.ArcB - s.ArcA ) / Math.Max( RiverSpeedMulMin, speed );
			travel[i] = t;
			travel[n + i] = span;
			t += span;
		}
		_riverTravel = travel;
		_riverTravelNetwork = net;
		return travel;
	}

	/// <summary>
	/// Current multiple at one end of a segment: the slope averaged over the five segments around that end (same river),
	/// so it is continuous and never jumps within one 20 m segment (a slope tripling across one segment switched the
	/// white water on along a straight line across the river, 2026-10-25).
	/// </summary>
	static float RiverSpeedMulAtEnd( TerrainPreviewRivers.Network net, int index, bool endB )
	{
		var centre = endB ? index + 0.5f : index - 0.5f;
		var sum = 0f;
		var weight = 0f;
		for ( var k = (int)MathF.Floor( centre - 2f ); k <= (int)MathF.Ceiling( centre + 2f ); k++ )
		{
			if ( k < 0 || k >= net.Segments.Length || net.Segments[k].River != net.Segments[index].River )
				continue;
			var w = Math.Max( 0f, 2.5f - MathF.Abs( k - centre ) );
			sum += SegmentSlope( net, k ) * w;
			weight += w;
		}
		var slope = weight > 0f ? sum / weight : SegmentSlope( net, index );
		return Math.Clamp( slope / RiverMedianSlope( net ), RiverSpeedMulMin, RiverSpeedMulMax );
	}

	/// <summary>
	/// Confluence fade at a point of river <paramref name="river"/>'s strip: 1, except on the last segments of a tributary,
	/// where it falls from 1 just outside the other river's water to 0 just inside it (riverwater.shader multiplies the
	/// surface's alpha by it).
	/// </summary>
	static float RiverJoinFade( TerrainPreviewRivers.Network net, int river, int segment, float x, float y )
	{
		if ( river < 0 || river >= net.RiverJoins.Length || net.RiverJoins[river] < 0 )
			return 1f;
		if ( segment < net.RiverFirstSegment[river] + net.RiverSegmentCount[river] - 10 )
			return 1f;
		var target = net.RiverJoins[river];
		var first = net.RiverFirstSegment[target];
		var count = net.RiverSegmentCount[target];
		var best = float.MaxValue;
		var half = 1f;
		for ( var k = first; k < first + count; k++ )
		{
			ref var t = ref net.Segments[k];
			var abx = t.Bx - t.Ax;
			var aby = t.By - t.Ay;
			var len2 = (abx * abx) + (aby * aby);
			var u = len2 > 1e-6f ? Math.Clamp( (((x - t.Ax) * abx) + ((y - t.Ay) * aby)) / len2, 0f, 1f ) : 0f;
			var dx = x - (t.Ax + (abx * u));
			var dy = y - (t.Ay + (aby * u));
			var d = MathF.Sqrt( (dx * dx) + (dy * dy) );
			if ( d < best )
			{
				best = d;
				half = t.HalfA + ((t.HalfB - t.HalfA) * u);
			}
		}
		// Gone just inside the other channel's edge (the strip there is split into rows, IsJoinTail): any of it left over
		// the other river's water showed its straight side edges and its own colour
		var f = Math.Clamp( (best - (half * 0.9f)) / Math.Max( 0.5f, half * 0.2f ), 0f, 1f );
		return f * f * (3f - (2f * f));
	}

	/// <summary>One of the last two segments of a river that flows into another (its strip is split into rows there).</summary>
	static bool IsJoinTail( TerrainPreviewRivers.Network net, int segment )
	{
		var river = net.Segments[segment].River;
		return river >= 0 && river < net.RiverJoins.Length && net.RiverJoins[river] >= 0
			&& segment >= net.RiverFirstSegment[river] + net.RiverSegmentCount[river] - 2;
	}

	/// <summary>Usage: <c>river_join_probe</c> — every confluence: which river flows into which, and the strip's confluence fade at the tributary's last segment ends.</summary>
	[ConCmd( "river_join_probe" )]
	public static void ConCmdRiverJoinProbe()
	{
		var net = TerrainPreviewLandDiskFields.RiverNetwork;
		if ( net is null )
			return;
		for ( var r = 0; r < net.RiverCount && r < net.RiverJoins.Length; r++ )
		{
			if ( net.RiverJoins[r] < 0 )
				continue;
			var first = net.RiverFirstSegment[r];
			var count = net.RiverSegmentCount[r];
			var line = new System.Text.StringBuilder();
			for ( var k = Math.Max( first, first + count - 6 ); k < first + count; k++ )
			{
				ref var sg = ref net.Segments[k];
				line.Append( $" [{k - first}] {RiverJoinFade( net, r, k, sg.Bx, sg.By ):0.00}" );
			}
			ref var end = ref net.Segments[first + count - 1];
			Log.Info( $"[RiverWater] river {r} flows into {net.RiverJoins[r]}, ends at {end.Bx:0},{end.By:0} level {end.LevelB:0.00}; fade at segment ends:{line}" );
		}
	}

	/// <summary>Segments owned by each loaded chunk — the merged surface is their union.</summary>
	readonly Dictionary<TerrainChunkCoord, RiverSurfaceSegment[]> _riverSegmentsByChunk = new();
	bool _riverSurfaceDirty;
	float _riverSurfaceLastRebuild = float.MinValue;
	GameObject _riverSurfaceObject;
	ModelRenderer _riverSurfaceRenderer;

	static Material GetRiverWaterMaterial()
	{
		if ( _riverWaterMaterial is { IsValid: true } )
			return _riverWaterMaterial;
		_riverWaterMaterial = Material.Load( RiverWaterMaterialPath );
		if ( _riverWaterMaterial is not { IsValid: true } && !_riverWaterMaterialWarned )
		{
			_riverWaterMaterialWarned = true;
			Log.Warning( $"[TerrainWorldManager] river water material '{RiverWaterMaterialPath}' did not load — rivers have no surface." );
		}
		return _riverWaterMaterial;
	}

	/// <summary>Chunk load: register its river segments for the merged surface and build its swim volumes.</summary>
	void TryBuildRiverWater( GameObject chunkRoot, TerrainChunkCoord coord, TerrainPreviewSettings settings, float chunkMinX, float chunkMinY, float chunkSize )
	{
		if ( !settings.EnableRivers )
			return;

		var net = TerrainPreviewLandDiskFields.RiverNetwork;
		if ( net is null || net.Segments.Length == 0 )
			return;

		var maxX = chunkMinX + chunkSize;
		var maxY = chunkMinY + chunkSize;
		// Every segment belongs to exactly one chunk — the one holding its midpoint. The quad and the box are
		// world-space and simply cross into the neighbour chunk.
		var segments = new List<int>();
		for ( var i = 0; i < net.Segments.Length; i++ )
		{
			ref var s = ref net.Segments[i];
			var mx = (s.Ax + s.Bx) * 0.5f;
			var my = (s.Ay + s.By) * 0.5f;
			if ( mx >= chunkMinX && mx < maxX && my >= chunkMinY && my < maxY )
				segments.Add( i );
		}

		if ( segments.Count == 0 )
			return;

		var ownedList = new List<RiverSurfaceSegment>( segments.Count );
		foreach ( var si in segments )
		{
			if ( RiverSurfaceSegmentFor( settings, net, si ) is { } seg )
				ownedList.Add( seg );
		}
		if ( ownedList.Count == 0 )
			return;
		var owned = ownedList.ToArray();
		_riverSegmentsByChunk[coord] = owned;
		_riverSurfaceDirty = true;

		var root = new GameObject( true, "RiverWater" );
		root.Flags |= GameObjectFlags.NotSaved;
		root.Parent = chunkRoot;
		root.LocalPosition = Vector3.Zero;

		var flow = Math.Clamp( settings.RiverFlowSpeedMetersPerSecond, 0f, 10f );
		foreach ( var seg in owned )
			BuildRiverVolume( root, net, seg.Index, chunkMinX, chunkMinY, flow * ((seg.A.SpeedMul + seg.B.SpeedMul) * 0.5f) );
	}

	/// <summary>Every segment's surface data, computed once per world (null = the segment lies in open water: no surface).</summary>
	readonly Dictionary<int, RiverSurfaceSegment?> _riverSurfaceCache = new();

	/// <summary>
	/// One segment's surface data, cached: per-end depth / current / tint / bank heights, and the mouth cut — the network
	/// runs on into the lake / sea it feeds, but the surface must stop at the shore (Mark 2026-10-22: a river strip lay
	/// across the lake). A segment starting in open water gets none; one crossing the shoreline is cut there
	/// (+ RiverMouthOverlapMeters, which the shader fades into the sea surface).
	/// </summary>
	RiverSurfaceSegment? RiverSurfaceSegmentFor( TerrainPreviewSettings settings, TerrainPreviewRivers.Network net, int si )
	{
		if ( _riverSurfaceCache.TryGetValue( si, out var cached ) )
			return cached;
		ref var s = ref net.Segments[si];
		var tuning = SeaTuning;
		RiverSurfaceSegment? result = null;
		if ( SeaWaves.DepthMeters( settings, tuning, s.Ax, s.Ay, out _ ) <= 0f )
		{
			var reachB = 1f;
			if ( SeaWaves.DepthMeters( settings, tuning, s.Bx, s.By, out _ ) > 0f )
			{
				var lo = 0f;
				var hi = 1f;
				for ( var it = 0; it < 12; it++ )
				{
					var mid = (lo + hi) * 0.5f;
					var wet = SeaWaves.DepthMeters( settings, tuning, s.Ax + ((s.Bx - s.Ax) * mid), s.Ay + ((s.By - s.Ay) * mid), out _ ) > 0f;
					if ( wet ) hi = mid; else lo = mid;
				}
				var length = Math.Max( 0.5f, MathF.Sqrt( ((s.Bx - s.Ax) * (s.Bx - s.Ax)) + ((s.By - s.Ay) * (s.By - s.Ay)) ) );
				reachB = Math.Clamp( lo + (RiverMouthOverlapMeters / length), 0.02f, 1f );
			}
			var side = Perpendicular( SegmentDirection( net, si ) );
			result = new RiverSurfaceSegment
			{
				Index = si,
				A = SampleRiverSurfaceEnd( settings, net, si, false, s.Ax, s.Ay, s.LevelA, s.HalfA, side ),
				// At a mouth the strip's end vertex is the cut, not B: B lies out in the lake, and its deep bed shaded the
				// whole last piece of river as deep water — a dark slab with a straight edge (2026-10-25)
				B = SampleRiverSurfaceEnd( settings, net, si, true,
					s.Ax + ((s.Bx - s.Ax) * reachB), s.Ay + ((s.By - s.Ay) * reachB),
					s.LevelA + ((s.LevelB - s.LevelA) * reachB), s.HalfA + ((s.HalfB - s.HalfA) * reachB), side ),
				ReachB = reachB,
			};
		}
		_riverSurfaceCache[si] = result;
		return result;
	}

	/// <summary>
	/// What the water needs at one segment end (once per segment — a handful of pipeline samples per segment): the carved
	/// depth, the current from the slope, the biome tint axis, and the bank + treeline silhouette height on each side,
	/// sampled past the rim where the trees actually stand.
	/// </summary>
	static RiverSurfaceEnd SampleRiverSurfaceEnd( TerrainPreviewSettings settings, TerrainPreviewRivers.Network net, int index, bool endB, float x, float y, float level, float half, Vector2 side )
	{
		var centre = TerrainPreviewPipeline.Sample( settings, x, y );
		var tint = 0.5f;
		if ( centre.HasLandWeights && centre.LandWeights.Total > 1e-4f )
			tint = Math.Clamp( 0.5f + (0.5f * (centre.LandWeights.Mountain - centre.LandWeights.Redwood) / centre.LandWeights.Total), 0f, 1f );

		var reach = half + Math.Clamp( settings.RiverBankSlideWidthMeters, 0.5f, 30f ) + RiverTreelineSetbackMeters;
		return new RiverSurfaceEnd
		{
			Depth = Math.Max( 0.2f, level - centre.HeightMeters ),
			SpeedMul = RiverSpeedMulAtEnd( net, index, endB ),
			Tint01 = tint,
			OccluderMinus = RiverOccluderHeight( settings, x - (side.x * reach), y - (side.y * reach), level ),
			OccluderPlus = RiverOccluderHeight( settings, x + (side.x * reach), y + (side.y * reach), level ),
		};
	}

	/// <summary>
	/// Height of the proxy wall the reflection is projected onto beside the river: the ground above the water there plus
	/// the biome's full treeline. Deliberately generous — the shader reads the wall's COLOUR from the frame buffer at
	/// that point, so where the real view above the bank is open sky the reflection still shows sky; a wall that is too
	/// low, on the other hand, mirrored sky over every bank (the grey sheet, 2026-10-22).
	/// </summary>
	static float RiverOccluderHeight( TerrainPreviewSettings settings, float x, float y, float level )
	{
		var sample = TerrainPreviewPipeline.Sample( settings, x, y );
		var ground = Math.Max( 0.3f, sample.HeightMeters - level );
		if ( !sample.HasLandWeights )
			return ground;
		return ground + RiverTreelineHeightMeters( TerrainPreviewBiomeResolver.PickDominantPlacementBiome( sample.LandWeights ) );
	}

	/// <summary>
	/// Usage: <c>river_surface_probe x y [radius m]</c> — every river segment whose midpoint is within the radius: its
	/// river, levels and half widths, and whether the merged water surface has it (chunk-owned, in the far set, cached
	/// with a surface, or cached as open water). Finds why a carved channel shows no water.
	/// </summary>
	[ConCmd( "river_surface_probe" )]
	public static void ConCmdRiverSurfaceProbe( float x, float y, float radius = 80f )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		var net = TerrainPreviewLandDiskFields.RiverNetwork;
		if ( manager is null || net is null )
			return;
		var owned = new HashSet<int>();
		foreach ( var list in manager._riverSegmentsByChunk.Values )
			foreach ( var seg in list )
				owned.Add( seg.Index );
		var found = 0;
		for ( var i = 0; i < net.Segments.Length; i++ )
		{
			ref var s = ref net.Segments[i];
			var dx = ((s.Ax + s.Bx) * 0.5f) - x;
			var dy = ((s.Ay + s.By) * 0.5f) - y;
			if ( (dx * dx) + (dy * dy) > radius * radius )
				continue;
			found++;
			var cache = manager._riverSurfaceCache.TryGetValue( i, out var c ) ? (c is null ? "OPEN WATER (no surface)" : $"surface reach {c.Value.ReachB:0.00}") : "not computed";
			Log.Info( $"[RiverProbe] seg {i} river {s.River}: {s.Ax:0},{s.Ay:0} → {s.Bx:0},{s.By:0}, level {s.LevelA:0.0}→{s.LevelB:0.0} m, half {s.HalfA:0.0}/{s.HalfB:0.0} m, chunk-owned {owned.Contains( i )}, far set {manager._riverFarSegments.Contains( i )}, {cache}" );
		}
		Log.Info( $"[RiverProbe] {found} segments within {radius:0} m of {x:0},{y:0}" );
	}

	/// <summary>Chunk unload: its segments leave the merged surface (the swim volumes die with the chunk object).</summary>
	void ReleaseRiverWater( TerrainChunkCoord coord )
	{
		if ( _riverSegmentsByChunk.Remove( coord ) )
			_riverSurfaceDirty = true;
	}

	void DestroyRiverSurface()
	{
		_waterTrails.Clear();
		_waterTrailBuffer?.Dispose();
		_waterTrailBuffer = null;
		_riverSegmentsByChunk.Clear();
		_riverSurfaceCache.Clear();
		_riverMouthNet = null;
		_riverMouthBuffer?.Dispose();
		_riverMouthBuffer = null;
		_riverFarSegments.Clear();
		_riverFarPending.Clear();
		_riverFarCentre = new Vector3( float.MaxValue );
		_riverSurfaceDirty = false;
		if ( _riverSurfaceObject is not null && _riverSurfaceObject.IsValid() )
			_riverSurfaceObject.Destroy();
		_riverSurfaceObject = null;
		_riverSurfaceRenderer = null;
	}

	/// <summary>
	/// Rivers beyond the near chunks (Mark 2026-10-22: far rivers were blue paint with no water, so the water "changed"
	/// at the edge of the near bubble). Every segment within Far View Distance of the camera joins the surface; the set is
	/// re-picked when the camera has moved <see cref="RiverFarRepickMeters"/>, and segments new to the cache are computed
	/// under <see cref="RiverFarBudgetMs"/> a frame. No swim volumes out there.
	/// </summary>
	readonly HashSet<int> _riverFarSegments = new();
	readonly List<int> _riverFarPending = new();
	Vector3 _riverFarCentre = new( float.MaxValue );
	const float RiverFarRepickMeters = 200f;
	const float RiverFarBudgetMs = 1.5f;

	void TickRiverFarSet()
	{
		var settings = _loadSettings;
		var net = TerrainPreviewLandDiskFields.RiverNetwork;
		var cam = ResolveStreamCamera();
		if ( settings is null || net is null || !settings.EnableRivers || cam is null || !cam.IsValid() )
			return;

		var camPos = cam.WorldPosition;
		if ( camPos.Distance( _riverFarCentre ) > TerrainWorldUnits.MetersToEngine( RiverFarRepickMeters ) )
		{
			_riverFarCentre = camPos;
			var cx = TerrainWorldUnits.EngineToMeters( camPos.x );
			var cy = TerrainWorldUnits.EngineToMeters( camPos.y );
			var radius = FarTerrainEnabled ? Math.Max( 1000f, FarViewDistanceMeters ) : 2000f;
			var r2 = radius * radius;
			_riverFarSegments.Clear();
			_riverFarPending.Clear();
			for ( var i = 0; i < net.Segments.Length; i++ )
			{
				ref var seg = ref net.Segments[i];
				var dx = ((seg.Ax + seg.Bx) * 0.5f) - cx;
				var dy = ((seg.Ay + seg.By) * 0.5f) - cy;
				if ( (dx * dx) + (dy * dy) > r2 )
					continue;
				_riverFarSegments.Add( i );
				if ( !_riverSurfaceCache.ContainsKey( i ) )
					_riverFarPending.Add( i );
			}
			_riverSurfaceDirty = true;
		}

		if ( _riverFarPending.Count == 0 )
			return;
		var sw = System.Diagnostics.Stopwatch.StartNew();
		while ( _riverFarPending.Count > 0 && sw.Elapsed.TotalMilliseconds < RiverFarBudgetMs )
		{
			var i = _riverFarPending[^1];
			_riverFarPending.RemoveAt( _riverFarPending.Count - 1 );
			RiverSurfaceSegmentFor( settings, net, i );
		}
		_riverSurfaceDirty = true;
	}

	/// <summary>The hand-over stretch up a river channel where the lake / sea surface takes over (river_mouths.hlsl): this many river widths (half widths × 2 × 0.75), within the limits.</summary>
	const float RiverMouthHandOverWidths = 1.5f;
	const float RiverMouthHandOverMinMeters = 20f;
	const float RiverMouthHandOverMaxMeters = 60f;
	const int RiverMouthCapacity = 64;   // water_common.hlsl RIVER_MOUTH_CAPACITY
	GpuBuffer<Vector4> _riverMouthBuffer;
	Vector4[] _riverMouthData;
	TerrainPreviewRivers.Network _riverMouthNet;
	int _riverMouthCount;

	/// <summary>
	/// River mouths for the water shaders' cross-fade (water_common.hlsl RiverMouthBlend): where each river's line first
	/// crosses from dry land into open water — the crossing point (binary search on the open-water depth), the flow
	/// direction there and the half width. Computed once per river network; the attributes are set every frame
	/// (Scene.RenderAttributes, like the swimmer trail).
	/// </summary>
	void PublishRiverMouths()
	{
		var settings = _loadSettings;
		var net = TerrainPreviewLandDiskFields.RiverNetwork;
		if ( settings is null || net is null || !settings.EnableRivers )
		{
			Scene.RenderAttributes.Set( "RiverMouthInfo", Vector4.Zero );
			return;
		}
		if ( !ReferenceEquals( net, _riverMouthNet ) )
		{
			_riverMouthNet = net;
			var tuning = SeaTuning;
			var data = new Vector4[RiverMouthCapacity * 2];
			var n = 0;
			for ( var r = 0; r < net.RiverCount && n < RiverMouthCapacity; r++ )
			{
				var first = net.RiverFirstSegment[r];
				var count = net.RiverSegmentCount[r];
				for ( var si = first; si < first + count; si++ )
				{
					ref var s = ref net.Segments[si];
					if ( SeaWaves.DepthMeters( settings, tuning, s.Ax, s.Ay, out _ ) > 0f || SeaWaves.DepthMeters( settings, tuning, s.Bx, s.By, out _ ) <= 0f )
						continue;
					var lo = 0f;
					var hi = 1f;
					for ( var it = 0; it < 14; it++ )
					{
						var mid = (lo + hi) * 0.5f;
						if ( SeaWaves.DepthMeters( settings, tuning, s.Ax + ((s.Bx - s.Ax) * mid), s.Ay + ((s.By - s.Ay) * mid), out _ ) > 0f )
							hi = mid;
						else
							lo = mid;
					}
					var dir = new Vector2( s.Bx - s.Ax, s.By - s.Ay ).Normal;
					var cx = s.Ax + ((s.Bx - s.Ax) * lo);
					var cy = s.Ay + ((s.By - s.Ay) * lo);
					var halfW = s.HalfA + ((s.HalfB - s.HalfA) * lo);
					// The channel's carved depth just upstream of the shoreline (the sea draws the hand-over with it)
					var level = s.LevelA + ((s.LevelB - s.LevelA) * lo);
					var bed = TerrainPreviewPipeline.Sample( settings, cx - (dir.x * 3f), cy - (dir.y * 3f) ).HeightMeters;
					var handOver = Math.Clamp( halfW * RiverMouthHandOverWidths, RiverMouthHandOverMinMeters, RiverMouthHandOverMaxMeters );
					data[n * 2] = new Vector4( cx, cy, dir.x, dir.y );
					data[(n * 2) + 1] = new Vector4( halfW, Math.Max( 0.3f, level - bed ), handOver, 0f );
					n++;
					break;
				}
			}
			_riverMouthCount = n;
			_riverMouthData = data;
			_riverMouthBuffer ??= new GpuBuffer<Vector4>( RiverMouthCapacity * 2 );
			_riverMouthBuffer.SetData( data.AsSpan() );
			Log.Info( $"[TerrainWorldManager] river mouths: {n} of {net.RiverCount} rivers end in a lake or the sea" );
		}
		Scene.RenderAttributes.Set( "RiverMouths", _riverMouthBuffer );
		Scene.RenderAttributes.Set( "RiverMouthInfo", new Vector4( _riverMouthCount, 0f, 0f, 0f ) );
	}

	/// <summary>
	/// Usage: <c>river_mouth [n] [back m] [height m] [side m]</c> — no argument lists every river mouth (where a river
	/// crosses into a lake or the sea) with its open-water kind; with n, parks the fly camera upstream of mouth n looking
	/// down the river across the mouth (default 70 m back, 25 m up), shifted sideways by side metres.
	/// </summary>
	[ConCmd( "river_mouth" )]
	public static void ConCmdRiverMouth( int n = -1, float back = 70f, float height = 25f, float side = 0f )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		var data = manager?._riverMouthData;
		if ( data is null || manager._riverMouthCount == 0 )
		{
			Log.Info( "[RiverMouth] no river mouths published (play mode, rivers on?)" );
			return;
		}
		if ( n < 0 || n >= manager._riverMouthCount )
		{
			var land = manager._loadSettings.LandRadiusMeters;
			for ( var i = 0; i < manager._riverMouthCount; i++ )
			{
				var m = data[i * 2];
				var kind = MathF.Sqrt( (m.x * m.x) + (m.y * m.y) ) >= land - 30f ? "ocean" : "lake";
				Log.Info( $"[RiverMouth] {i}: {kind} at {m.x:0},{m.y:0}, flowing {m.z:0.00},{m.w:0.00}, half width {data[(i * 2) + 1].x:0.0} m" );
			}
			return;
		}
		var mouth = data[n * 2];
		var dir = new Vector2( mouth.z, mouth.w );
		var perp = new Vector2( -dir.y, dir.x );
		var at = new Vector2( mouth.x, mouth.y ) - (dir * back) + (perp * side);
		var cam = scene.GetAllComponents<TerrainTestFlyCamera>().FirstOrDefault();
		if ( cam is null )
			return;
		var yaw = MathF.Atan2( dir.y, dir.x ) * 180f / MathF.PI;
		var pitch = MathF.Atan2( height, back ) * 180f / MathF.PI;
		cam.SetPoseMeters( new Vector3( at.x, at.y, height ), pitch, yaw );
		Log.Info( $"[RiverMouth] camera {back:0} m upstream of mouth {n} ({mouth.x:0},{mouth.y:0}), {height:0} m up" );
	}

	/// <summary>Per frame: rebuild the merged surface when its segment set changed (throttled). Nothing otherwise.</summary>
	void TickRiverSurface()
	{
		PublishRiverMouths();
		TickRiverFarSet();
		if ( !_riverSurfaceDirty )
			return;
		if ( Time.Now - _riverSurfaceLastRebuild < RiverSurfaceRebuildIntervalSeconds )
			return;
		_riverSurfaceDirty = false;
		_riverSurfaceLastRebuild = Time.Now;

		var net = TerrainPreviewLandDiskFields.RiverNetwork;
		var segments = new List<RiverSurfaceSegment>();
		if ( net is not null )
		{
			var taken = new HashSet<int>();
			foreach ( var list in _riverSegmentsByChunk.Values )
			{
				foreach ( var seg in list )
				{
					if ( taken.Add( seg.Index ) )
						segments.Add( seg );
				}
			}
			foreach ( var i in _riverFarSegments )
			{
				if ( !taken.Contains( i ) && _riverSurfaceCache.TryGetValue( i, out var cached ) && cached is { } far )
				{
					taken.Add( i );
					segments.Add( far );
				}
			}
		}

		if ( segments.Count == 0 || net is null )
		{
			if ( _riverSurfaceRenderer is not null && _riverSurfaceRenderer.IsValid() )
				_riverSurfaceRenderer.Enabled = false;
			return;
		}

		var material = GetRiverWaterMaterial();
		if ( material is not { IsValid: true } )
			return;

		var model = BuildRiverSurfaceModel( net, segments, material );
		if ( model is null || !model.IsValid )
			return;

		if ( _riverSurfaceObject is null || !_riverSurfaceObject.IsValid() )
		{
			_riverSurfaceObject = new GameObject( true, "RiverWater" );
			_riverSurfaceObject.Flags |= GameObjectFlags.NotSaved;
			_riverSurfaceObject.Parent = GameObject;
			_riverSurfaceObject.WorldPosition = Vector3.Zero;
			_riverSurfaceRenderer = _riverSurfaceObject.Components.Create<ModelRenderer>();
			_riverSurfaceRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			_riverSurfaceRenderer.ExecuteBefore = RiverGrabCommands();
		}
		_riverSurfaceRenderer.Model = model;
		_riverSurfaceRenderer.MaterialOverride = material;
		_riverSurfaceRenderer.Enabled = true;
	}

	/// <summary>Ring sources the water shaders read (water_common.hlsl WATER_TRAIL_CAPACITY): 4 pawns × 12.</summary>
	const int WaterTrailCapacity = 48;
	const int WaterTrailPerPawn = 12;
	/// <summary>A pawn drops a ring source after moving this far, or after this long standing still.</summary>
	const float WaterTrailSpacingMeters = 0.8f;
	const float WaterTrailIdleSeconds = 0.6f;
	/// <summary>A ring lives this long (water_common.hlsl WATER_RING_LIFE).</summary>
	const float WaterTrailLifeSeconds = 3.5f;
	/// <summary>Pawns further than this from the camera make no visible rings: not published.</summary>
	const float WaterRipplerRangeMeters = 80f;

	readonly Dictionary<PlayerMovement, List<Vector4>> _waterTrails = new();
	readonly List<(float Dist, PlayerMovement Pawn)> _waterRipplerPick = new();
	readonly Vector4[] _waterTrailData = new Vector4[WaterTrailCapacity];
	GpuBuffer<Vector4> _waterTrailBuffer;

	/// <summary>
	/// Per frame while the river or sea surface is up: every pawn drops ring sources along the path it actually took
	/// (every <see cref="WaterTrailSpacingMeters"/> moved, or every <see cref="WaterTrailIdleSeconds"/> standing still) and
	/// the sources of the four pawns nearest the camera go to the shaders. Each source is a ring expanding from where the
	/// pawn WAS; a swimmer's overlapping rings make the wake behind it. (Mark 2026-10-22: the first wake was drawn from
	/// the current velocity, so walking back and forth flipped it to where the pawn had never been.) The shader decides
	/// from the water height whether the body cut the surface. A handful of registered pawns, no scene query.
	/// </summary>
	void TickWaterRipples()
	{
		var riverUp = _riverSurfaceRenderer is not null && _riverSurfaceRenderer.IsValid() && _riverSurfaceRenderer.Enabled;
		var seaUp = _seaSurfaceRenderer is not null && _seaSurfaceRenderer.IsValid() && _seaSurfaceRenderer.Enabled;
		if ( !riverUp && !seaUp )
			return;
		var cam = ResolveStreamCamera();
		if ( cam is null || !cam.IsValid() )
			return;

		var now = Time.Now;
		var camPos = cam.WorldPosition;
		var range = TerrainWorldUnits.MetersToEngine( WaterRipplerRangeMeters );
		var spacing = TerrainWorldUnits.MetersToEngine( WaterTrailSpacingMeters );
		_waterRipplerPick.Clear();
		foreach ( var pawn in PlayerMovement.WaterRippleMakers )
		{
			if ( pawn is null || !pawn.IsValid() )
				continue;
			var pos = pawn.WorldPosition;
			if ( !_waterTrails.TryGetValue( pawn, out var trail ) )
				_waterTrails[pawn] = trail = new List<Vector4>( WaterTrailPerPawn );
			// Age out, then drop a new source when the pawn has moved on or stood long enough
			trail.RemoveAll( q => now - q.w > WaterTrailLifeSeconds );
			var newest = trail.Count > 0 ? trail[^1] : default;
			if ( trail.Count == 0 || new Vector3( newest.x, newest.y, newest.z ).Distance( pos ) >= spacing || now - newest.w >= WaterTrailIdleSeconds )
			{
				if ( trail.Count >= WaterTrailPerPawn )
					trail.RemoveAt( 0 );
				trail.Add( new Vector4( pos.x, pos.y, pos.z, now ) );
			}
			var d = pos.Distance( camPos );
			if ( d <= range )
				_waterRipplerPick.Add( (d, pawn) );
		}
		_waterRipplerPick.Sort( ( a, b ) => a.Dist.CompareTo( b.Dist ) );

		var n = 0;
		for ( var k = 0; k < _waterRipplerPick.Count && n < WaterTrailCapacity; k++ )
		{
			foreach ( var q in _waterTrails[_waterRipplerPick[k].Pawn] )
			{
				if ( n >= WaterTrailCapacity )
					break;
				_waterTrailData[n++] = q;
			}
		}
		for ( var i = n; i < WaterTrailCapacity; i++ )
			_waterTrailData[i] = new Vector4( 0f, 0f, 0f, -1000f );   // long dead

		_waterTrailBuffer ??= new GpuBuffer<Vector4>( WaterTrailCapacity );
		_waterTrailBuffer.SetData( _waterTrailData.AsSpan() );
		var attrs = Scene.RenderAttributes;
		attrs.Set( "WaterTrail", _waterTrailBuffer );
		attrs.Set( "WaterTrailInfo", new Vector4( n, now, 0f, 0f ) );

		// Pawns that left (destroyed / disabled) drop out
		if ( _waterTrails.Count > PlayerMovement.WaterRippleMakers.Count )
		{
			foreach ( var gone in _waterTrails.Keys.Where( p => !PlayerMovement.WaterRippleMakers.Contains( p ) ).ToList() )
				_waterTrails.Remove( gone );
		}
	}

	/// <summary>
	/// The shader's ripple pattern repeats every this many metres along the river, so the along-river UV is stored
	/// modulo it: vertex UVs are half precision, and raw arc metres (thousands) stepped by whole metres.
	/// </summary>
	const float RiverRippleTileMeters = 64f;
	/// <summary>Vertex colour R carries the water depth in metres over this range (8-bit: ~3 cm steps).</summary>
	const float RiverDepthColorRangeMeters = 8f;
	/// <summary>Vertex colour A carries the channel half width in metres over this range (8-bit: 25 cm steps).</summary>
	const float RiverHalfWidthColorRangeMeters = 64f;

	/// <summary>
	/// One mesh for the given segments, in absolute engine units. Three vertices across each end (bank, centre line,
	/// bank) so the water depth — 0 at the edges, the carved depth on the centre line — interpolates across the channel;
	/// UV = (metres downstream modulo the ripple tile, signed metres across); vertex colour R = depth / 8 m, G = current,
	/// B = biome tint axis, A = half width; UV1 = bank + treeline height per side.
	/// </summary>
	static Model BuildRiverSurfaceModel( TerrainPreviewRivers.Network net, List<RiverSurfaceSegment> segments, Material material )
	{
		// A tributary's last two segments are split into rows so its confluence fade can switch right at the other
		// river's bank: per segment the fade only changed from one end to the other, and the strip's straight side edges
		// lay half-faded inside the other river's water (2026-10-25)
		var rows = new int[segments.Count];
		var quads = 0;
		for ( var q = 0; q < segments.Count; q++ )
		{
			rows[q] = IsJoinTail( net, segments[q].Index ) ? 8 : 1;
			quads += rows[q];
		}
		var mesh = new Mesh( material, MeshPrimitiveType.Triangles );
		mesh.CreateVertexBuffer<Vertex>( quads * 6 );
		mesh.CreateIndexBuffer( quads * 12 );
		var lo = new Vector3( float.MaxValue );
		var hi = new Vector3( float.MinValue );

		mesh.LockVertexBuffer<Vertex>( vertices =>
		{
			var v = 0;
			for ( var q = 0; q < segments.Count; q++ )
			{
				var si = segments[q].Index;
				ref var s = ref net.Segments[si];
				var dir = SegmentDirection( net, si );
				// Mitred ends: the side at each end is the bisector of this segment and its neighbour on the same
				// river, so consecutive quads share their edge and a bend never opens a wedge of bed between them.
				var hasPrev = si > 0 && net.Segments[si - 1].River == s.River;
				var hasNext = si + 1 < net.Segments.Length && net.Segments[si + 1].River == s.River;
				var sideA = Perpendicular( hasPrev ? Bisector( SegmentDirection( net, si - 1 ), dir ) : dir );
				var cut = segments[q].ReachB < 1f;
				var sideB = Perpendicular( hasNext && !cut ? Bisector( dir, SegmentDirection( net, si + 1 ) ) : dir );
				var ha = s.HalfA + RiverSurfaceOverlapMeters;
				var hb = s.HalfA + ((s.HalfB - s.HalfA) * segments[q].ReachB) + RiverSurfaceOverlapMeters;
				var za = s.LevelA + RiverSurfaceLiftMeters + WaterStencilLiftMeters;
				var zb = s.LevelA + ((s.LevelB - s.LevelA) * segments[q].ReachB) + RiverSurfaceLiftMeters + WaterStencilLiftMeters;
				// The carve rounds each river end with a half-width cap (distance to the end point), but the strip used to
				// stop dead at the end vertex: the source showed a dry carved bowl behind a straight water edge (Mark
				// 2026-10-21). Push the SOURCE end out by the half width; what pokes past the channel is under the bank. The
				// mouth stays put — it ends in a lake, the sea or another river, and a strip pushed out over open water
				// read as a river rectangle lying on the lake (Mark 2026-10-22); the shader fades it into the sea there.
				var ax = s.Ax - (hasPrev ? 0f : dir.x * ha);
				var ay = s.Ay - (hasPrev ? 0f : dir.y * ha);
				// Cut at the shore where the river runs out into open water (ReachB < 1)
				var reach = segments[q].ReachB;
				var bx = s.Ax + ((s.Bx - s.Ax) * reach);
				var by = s.Ay + ((s.By - s.Ay) * reach);
				// Travel along the river (metres ÷ the local current, see RiverTravel), continuous within the segment and
				// equal modulo the tile to the next segment's start
				var travel = RiverTravel( net );
				var uA = travel[si] % RiverRippleTileMeters;
				var uB = uA + ((travel[net.Segments.Length + si]) * reach);
				var endA = segments[q].A;
				var endB = segments[q].B;
				var dA = Math.Clamp( endA.Depth / RiverDepthColorRangeMeters, 0f, 1f );
				var dB = Math.Clamp( endB.Depth / RiverDepthColorRangeMeters, 0f, 1f );
				var tangent = new Vector4( dir.x, dir.y, 0f, 1f );

				// A at -side, centre, +side; B likewise. (The vertex span is ref-like, so no local function may touch it.)
				var metres = new[]
				{
					new Vector3( ax - (sideA.x * ha), ay - (sideA.y * ha), za ),
					new Vector3( ax, ay, za ),
					new Vector3( ax + (sideA.x * ha), ay + (sideA.y * ha), za ),
					new Vector3( bx - (sideB.x * hb), by - (sideB.y * hb), zb ),
					new Vector3( bx, by, zb ),
					new Vector3( bx + (sideB.x * hb), by + (sideB.y * hb), zb ),
				};
				var uvs = new[] { new Vector2( uA, -ha ), new Vector2( uA, 0f ), new Vector2( uA, ha ), new Vector2( uB, -hb ), new Vector2( uB, 0f ), new Vector2( uB, hb ) };
				var depths = new[] { 0f, dA, 0f, 0f, dB, 0f };
				// Colour G = current / RiverSpeedMulMax, B = biome tint axis, A = channel half width / range;
				// TexCoord1 = bank + treeline height on the -side / +side, metres
				var hA = Math.Clamp( s.HalfA / RiverHalfWidthColorRangeMeters, 0f, 1f );
				var hB = Math.Clamp( s.HalfB / RiverHalfWidthColorRangeMeters, 0f, 1f );
				var occA = new Vector2( endA.OccluderMinus, endA.OccluderPlus );
				var occB = new Vector2( endB.OccluderMinus, endB.OccluderPlus );
				for ( var k = 0; k < rows[q]; k++ )
				{
					for ( var c = 0; c < 6; c++ )
					{
						// Row t along the segment: A row (c < 3) at k / rows, B row at (k + 1) / rows
						var col = c % 3;
						var t = (c < 3 ? k : k + 1) / (float)rows[q];
						var m = Vector3.Lerp( metres[col], metres[col + 3], t );
						// TexCoord1.y carries the confluence fade in its hundreds (bank heights stay under 100 m), unpacked
						// per vertex in riverwater.shader's VS: a tributary fades out where it enters the channel it flows
						// into, so the two surfaces meet instead of the tributary's straight end lying on the other river
						// (Mark 2026-10-25). (Packed into the colour, the engine's sRGB → linear conversion scrambled it.)
						var join = RiverJoinFade( net, s.River, si, m.x, m.y );
						var occ = Vector2.Lerp( occA, occB, t );
						occ.y = Math.Clamp( occ.y, 0f, 99f ) + (100f * MathF.Round( join * 15f ));
						var p = TerrainWorldUnits.MetersToEngine( m );
						lo = Vector3.Min( lo, p );
						hi = Vector3.Max( hi, p );
						vertices[v++] = new Vertex
						{
							Position = p,
							Normal = Vector3.Up,
							Tangent = tangent,
							TexCoord0 = Vector2.Lerp( uvs[col], uvs[col + 3], t ),
							TexCoord1 = occ,
							Color = new Color(
								depths[col] + ((depths[col + 3] - depths[col]) * t ),
								(endA.SpeedMul + ((endB.SpeedMul - endA.SpeedMul) * t)) / RiverSpeedMulMax,
								endA.Tint01 + ((endB.Tint01 - endA.Tint01) * t),
								hA + ((hB - hA) * t) ),
						};
					}
				}
			}
		} );

		mesh.LockIndexBuffer( indices =>
		{
			var n = 0;
			for ( var q = 0; q < quads; q++ )
			{
				// Counter-clockwise seen from above (the first strip version was wound the other way and culled):
				// vertices 0,1,2 = A at -side, centre, +side; 3,4,5 = B likewise.
				var b = q * 6;
				indices[n++] = b; indices[n++] = b + 4; indices[n++] = b + 1;
				indices[n++] = b; indices[n++] = b + 3; indices[n++] = b + 4;
				indices[n++] = b + 1; indices[n++] = b + 5; indices[n++] = b + 2;
				indices[n++] = b + 1; indices[n++] = b + 4; indices[n++] = b + 5;
			}
		} );

		mesh.Bounds = new BBox( lo - new Vector3( 1f ), hi + new Vector3( 1f ) );
		return new ModelBuilder().AddMesh( mesh ).Create();
	}

	static Vector2 SegmentDirection( TerrainPreviewRivers.Network net, int index )
	{
		ref var s = ref net.Segments[index];
		var dir = new Vector2( s.Bx - s.Ax, s.By - s.Ay );
		return dir.LengthSquared < 1e-6f ? Vector2.Right : dir.Normal;
	}

	/// <summary>Unit bisector of two unit directions; falls back to the second when they are opposed.</summary>
	static Vector2 Bisector( Vector2 a, Vector2 b )
	{
		var sum = a + b;
		return sum.LengthSquared < 1e-4f ? b : sum.Normal;
	}

	static Vector2 Perpendicular( Vector2 dir ) => new( -dir.y, dir.x );

	static void BuildRiverVolume( GameObject root, TerrainPreviewRivers.Network net, int segment, float chunkMinX, float chunkMinY, float flowMetersPerSecond )
	{
		ref var s = ref net.Segments[segment];
		var dir = new Vector2( s.Bx - s.Ax, s.By - s.Ay );
		var length = dir.Length;
		if ( length < 0.5f )
			return;
		dir /= length;

		// From a hair above the surface down past the deepest bed along this stretch: the part below the bed is
		// inside the ground and costs nothing. Width covers the wider end plus the surface overlap.
		var depth = Math.Max( 1f, Math.Max( s.DepthA, s.DepthB ) + 0.5f );
		var width = (Math.Max( s.HalfA, s.HalfB ) + RiverSurfaceOverlapMeters) * 2f;
		var levelMid = (s.LevelA + s.LevelB) * 0.5f;
		var centre = new Vector3(
			((s.Ax + s.Bx) * 0.5f) - chunkMinX,
			((s.Ay + s.By) * 0.5f) - chunkMinY,
			levelMid + RiverSurfaceLiftMeters - (depth * 0.5f) );

		var go = new GameObject( true, $"flow {segment}" );
		go.Flags |= GameObjectFlags.NotSaved;
		go.Parent = root;
		go.LocalPosition = TerrainWorldUnits.MetersToEngine( centre );
		// X along the segment and pitched with the water level, so the box top is the sloped surface.
		go.LocalRotation = Rotation.LookAt( new Vector3( dir.x * length, dir.y * length, s.LevelB - s.LevelA ).Normal, Vector3.Up );
		go.Tags.Add( RiverWaterTag );

		var trigger = go.Components.Create<BoxCollider>();
		trigger.IsTrigger = true;
		trigger.Static = true; // never moves; keyframed trigger boxes on the terrain cost Physics every frame (see the sea volumes)
		trigger.Scale = TerrainWorldUnits.MetersToEngine( new Vector3( length + 1f, width, depth ) );

		var volume = go.Components.Create<WaterVolume>();
		volume.FluidVelocity = new Vector3( dir.x, dir.y, 0f ) * TerrainWorldUnits.MetersToEngine( flowMetersPerSecond );
		volume.WaveAmplitude = 0f;
	}
}
