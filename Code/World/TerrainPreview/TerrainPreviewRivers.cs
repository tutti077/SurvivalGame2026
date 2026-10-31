namespace Survival;

using System.Collections.Generic;

/// <summary>
/// River network (Mark 2026-10-07: "river systems throughout the map in addition to those massive lakes",
/// true sloped rivers so a boat can ride them later). Built once per world on the land-disk raster, after
/// lakes: sources sit on high ground, each river walks downhill on a smoothed coarse height field
/// (<see cref="RoutingResolution"/> cells of ~80 m) until it meets a lake, the sea or another river, with a
/// water level that only ever falls along the way (<see cref="TerrainPreviewSettings.RiverMinGradePercent"/>)
/// and never rises above the ground it crosses. A river that would have to cut deeper than
/// <see cref="TerrainPreviewSettings.RiverMaxCutMeters"/> through a ridge, or never finds water, is dropped
/// and another source is tried. Routes are Chaikin-smoothed and resampled; the width grows from source to
/// mouth. A fine raster (the land-disk resolution) holds the nearest segment per cell so the per-sample
/// cost is one lookup plus a few point-segment distances. The carve itself happens in
/// <see cref="TerrainPreviewPipeline"/>: inside the half width the ground IS the water surface (sloped
/// along the river, flat across it, like the lakes are flat terrain at sea level), the bank blends up to
/// the surrounding ground over <see cref="TerrainPreviewSettings.RiverBankWidthScale"/> × width.
/// </summary>
public static class TerrainPreviewRivers
{
	/// <summary>What a point knows about the nearest river (default = no river near).</summary>
	public readonly struct Hit
	{
		public bool Near { get; init; }
		public float Distance { get; init; }
		public float Level { get; init; }
		public float HalfWidth { get; init; }
		public float BankWidth { get; init; }
		public float BedDepthMeters { get; init; }
		public int River { get; init; }
		/// <summary>Index of the segment the hit lies on (Network.Segments).</summary>
		public int Segment { get; init; }
	}

	public struct Segment
	{
		public float Ax, Ay, Bx, By;
		public float LevelA, LevelB;
		public float HalfA, HalfB;
		public float BankA, BankB;
		public float DepthA, DepthB;   // bed depth under the surface (deeper in pools, scaled per river; the pipeline caps it per biome)
		public float ArcA, ArcB;       // metres along the river from its source — the water shader's flow coordinate
		public int River;
	}

	public sealed class Network
	{
		public int FineRes;
		public int[] FineSegment = Array.Empty<int>();
		public Segment[] Segments = Array.Empty<Segment>();
		public int[] RiverFirstSegment = Array.Empty<int>();
		public int[] RiverSegmentCount = Array.Empty<int>();
		public int RiverCount;
		/// <summary>Per river: the river it flows into (its index), or -1 when it ends in a lake or the sea.</summary>
		public int[] RiverJoins = Array.Empty<int>();
		/// <summary>
		/// Per segment: the other river's segments whose carve reaches overlap this one's at a confluence (first, last;
		/// -1 = none) — <see cref="SamplePartner"/> evaluates them, so both channels carve wherever both reach.
		/// </summary>
		public int[] PartnerFrom = Array.Empty<int>();
		public int[] PartnerTo = Array.Empty<int>();
		/// <summary>Width of the curved bank wall outside the channel (River Bank Slide Width): part of every segment's reach.</summary>
		public float SlideWidthMeters;
		public int Attempted;
		public int Failed;
		public int DesertDropped;   // routes dropped because they entered Amber Dunes
		public float TotalLengthMeters;
		/// <summary>Segments falling at 35° or steeper (a waterfall over a mountain wall) and the rivers that have one.</summary>
		public int WaterfallSegments;
		public int WaterfallRivers;
		/// <summary>Mountain-foot lakes (TerrainPreviewMountainLakes) reached by a river of their own, and how many of those rivers have a fall segment.</summary>
		public int FootLakeRivers;
		public int FootLakeWaterfalls;
		/// <summary>One line per mountain-foot lake for the log: where it is and what it got.</summary>
		public List<string> FootLakeNotes = new();
	}

	/// <summary>A segment whose water drops at least this much per metre of run is a fall (0.7 ≈ 35°).</summary>
	public const float WaterfallGrade = 0.7f;

	/// <summary>
	/// The Bank Max Slope rule (a deep cut spreads its bank so the side never exceeds the slope) applies in full in the
	/// lowland — a river slicing a ridge or cutting down to a lake through its shore bank gets a wide valley, not a trench
	/// (Mark 2026-10-30, two rivers "going uphill" into a lake: with the bank capped the 30 m mouth approach was a slot).
	/// On a mountain (rise from 0 to <see cref="GorgeMountainRise01"/>) the cut that may spread is capped at this: a fall
	/// down a mountain face sits 100 m+ under the ground beside it, and spreading that over cut / tan(25°) gave a 257 m
	/// bank that pulled the whole face onto the cascade's stepped level profile — horizontal sawtooth bands up every
	/// waterfall mountain ("when the terrain gets very sloped it gets all zigzaggy"). A gorge keeps a channel-width bank.
	/// </summary>
	public const float GorgeCutMeters = 15f;
	/// <summary>Mountain rise at which the bank rule is fully the gorge rule (0 = lowland, full spreading).</summary>
	public const float GorgeMountainRise01 = 0.3f;

	/// <summary>
	/// Routing raster: 512 on the 40 km world = 78 m cells (was 256 = 156 m: a cell straddled a whole valley and its
	/// shoulder, so routes "flowing downhill" on the smoothed field crossed 20 m rises in the real ground between cell
	/// centres — Mark 2026-10-30, "it goes uphill").
	/// </summary>
	const int RoutingResolution = 512;

	public static Network Build(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		bool[] openWater,
		bool[] desertField,
		float[] waterLevelMeters,
		IReadOnlyList<int[]> footLakeCells,
		int fieldRes,
		Func<float, float, float> preRiverHeightMeters )
	{
		var net = new Network
		{
			FineRes = fieldRes,
			FineSegment = new int[fieldRes * fieldRes],
			SlideWidthMeters = Math.Clamp( settings.RiverBankSlideWidthMeters, 0.5f, 30f ),
		};
		Array.Fill( net.FineSegment, -1 );
		if ( settings.RiverCount <= 0 || landDisk is null || openWater is null || landDisk.Length != fieldRes * fieldRes )
			return net;

		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		var res = Math.Clamp( Math.Min( RoutingResolution, fieldRes ), 64, 512 );
		var cell = diameter / res;
		var sea = settings.SeaLevelMeters;

		// ---- coarse routing raster: land, water, height (pre-river, then smoothed)
		var count = res * res;
		var land = new bool[count];
		var water = new bool[count];
		var desert = new bool[count];   // Amber Dunes: no sources, no routes (Mark 2026-10-08)
		var cellLevel = new float[count];   // the water level of a water cell: sea, or a tarn's level on a mountain top
		var h = new float[count];
		var haveLevels = waterLevelMeters is not null && waterLevelMeters.Length == fieldRes * fieldRes;
		float WaterLevelAt( float x, float y )
			=> haveLevels && TryFieldIndex( x, y, fieldRes, radius, diameter, out var fi ) ? waterLevelMeters[fi] : sea;
		var wxs = new float[count];
		var wys = new float[count];
		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var i = (py * res) + px;
				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters( px, py, res, radius, diameter, out var wx, out var wy );
				wxs[i] = wx;
				wys[i] = wy;
				if ( !TryFieldIndex( wx, wy, fieldRes, radius, diameter, out var fi ) || !landDisk[fi] )
				{
					land[i] = false;
					water[i] = true;   // outside the land disk = the sea
					h[i] = sea;
					cellLevel[i] = sea;
					continue;
				}

				land[i] = true;
				water[i] = openWater[fi];
				desert[i] = desertField is not null && fi < desertField.Length && desertField[fi];
				cellLevel[i] = haveLevels ? waterLevelMeters[fi] : sea;
				h[i] = water[i] ? cellLevel[i] : preRiverHeightMeters( wx, wy );
			}
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				return net;
		}

		// Mountain-foot lakes (TerrainPreviewMountainLakes) on the routing raster: every cell holding one of a lake's water
		// cells becomes a water sink at the lake's level, so a pond smaller than a routing cell (the ~100 m one under the
		// mountain north of spawn on seed 1337) is still a mouth the router can reach. The fine water points are kept so a
		// river ending in such a cell can be led on to the real water.
		var lakeCells = new List<HashSet<int>>();
		var lakeCentres = new List<(float X, float Y)>();
		var lakeWaterPts = new List<List<Vector2>>();
		if ( footLakeCells is not null )
		{
			foreach ( var fine in footLakeCells )
			{
				var set = new HashSet<int>();
				var pts = new List<Vector2>( fine.Length );
				var sx = 0f;
				var sy = 0f;
				foreach ( var fi in fine )
				{
					TerrainBiomeMapCoordinates.RasterPixelToWorldMeters( fi % fieldRes, fi / fieldRes, fieldRes, radius, diameter, out var wx, out var wy );
					sx += wx;
					sy += wy;
					pts.Add( new Vector2( wx, wy ) );
					if ( !TryFieldIndex( wx, wy, res, radius, diameter, out var ci ) || !land[ci] )
						continue;
					set.Add( ci );
					if ( !water[ci] )
					{
						var level = haveLevels && fi < waterLevelMeters.Length ? waterLevelMeters[fi] : sea;
						water[ci] = true;
						cellLevel[ci] = level;
						h[ci] = level;
					}
				}
				if ( fine.Length == 0 || set.Count == 0 )
					continue;
				lakeCells.Add( set );
				lakeCentres.Add( (sx / fine.Length, sy / fine.Length) );
				lakeWaterPts.Add( pts );
			}
		}

		// Keep the unsmoothed ground for the level profile: the smoothed field only steers the route.
		var rawH = new float[count];
		Array.Copy( h, rawH, count );
		SmoothLand( h, land, res, passes: 2 );

		// ---- sources: high land away from water, spaced out
		var heights = new List<float>( count );
		for ( var i = 0; i < count; i++ )
		{
			if ( land[i] && !water[i] )
				heights.Add( h[i] );
		}
		if ( heights.Count == 0 )
			return net;
		heights.Sort();
		// Sources used to be the highest 45 % of the land, highest first: on a world whose high ground is a
		// ring of ranges that put every river in the ring, flowing outward to the rim, and none near the centre
		// (Mark 2026-10-08). Now anything above the lowest 30 % qualifies and the order is a seeded shuffle with
		// a mild preference for height, so sources spread over the whole disc.
		var threshold = heights[(int)Math.Clamp( heights.Count * 0.30f, 0, heights.Count - 1 )];
		var topHeight = heights[^1];
		var heightSpan = Math.Max( 1f, topHeight - threshold );

		var candidates = new List<int>();
		var mountainSourceChance = Math.Clamp( settings.RiverMountainSourceChance01, 0f, 1f );
		for ( var i = 0; i < count; i++ )
		{
			if ( !land[i] || water[i] || desert[i] || h[i] < threshold )
				continue;
			if ( HasWaterWithin( water, res, i, 1 ) )
				continue;
			// Mountain-top sources (Mark 2026-10-30, prompt 5: "the possibility of some waterfalls flowing off of cliffs"):
			// kept with Rivers - Mountain Source Chance, so some mountains shed a river over their wall and some do not.
			if ( mountainSourceChance < 1f
				&& TerrainPreviewPipeline.SampleMountainRise01( settings, wxs[i], wys[i] ) >= 0.5f
				&& TerrainPreviewNoise.Hash01( settings.WorldSeed + 991, i % res, i / res ) >= mountainSourceChance )
				continue;
			candidates.Add( i );
		}
		var seed = settings.WorldSeed;
		float SourceKey( int i ) => TerrainPreviewNoise.Hash01( seed + 990, i, 3 ) + (0.5f * (1f - ((h[i] - threshold) / heightSpan)));
		candidates.Sort( ( a, b ) => SourceKey( a ).CompareTo( SourceKey( b ) ) );

		// Mountain-foot lakes first (Mark 2026-10-30: "throw a waterfall in here … I want them for sure"): every lake the
		// mountain stands over gets a river of its own down the face into it, with Rivers - Mountain Lake Falls. Up to six
		// spots 3-9 routing cells out from the water and at least 120 m above it are queued per lake, highest-and-nearest
		// first; the first route that ends in the lake (or in a river that does) settles it and the rest are skipped.
		var sources = new List<int>();
		var sourceLake = new List<int>();   // per source: the foot lake it is for, or -1
		var lakeFallChance = Math.Clamp( settings.RiverLakeWaterfallChance01, 0f, 1f );
		if ( lakeFallChance > 0f )
		{
			for ( var lakeId = 0; lakeId < lakeCells.Count; lakeId++ )
			{
				var set = lakeCells[lakeId];
				if ( TerrainPreviewNoise.Hash01( seed + 992, lakeId, 7 ) >= lakeFallChance )
					continue;

				var level = sea;
				var minX = res;
				var maxX = -1;
				var minY = res;
				var maxY = -1;
				foreach ( var ci in set )
				{
					level = cellLevel[ci];
					minX = Math.Min( minX, ci % res );
					maxX = Math.Max( maxX, ci % res );
					minY = Math.Min( minY, ci / res );
					maxY = Math.Max( maxY, ci / res );
				}
				// Spots 470–1400 m out from the water (in cells of whatever size the raster has), ideally ~780 m
				var nearMinCells = 470f / cell;
				var nearMaxCells = 1400f / cell;
				var pad = (int)MathF.Ceiling( nearMaxCells );
				var spots = new List<(float Score, int Cell)>();
				for ( var py = Math.Max( 0, minY - pad ); py <= Math.Min( res - 1, maxY + pad ); py++ )
				{
					for ( var px = Math.Max( 0, minX - pad ); px <= Math.Min( res - 1, maxX + pad ); px++ )
					{
						var i = (py * res) + px;
						if ( !land[i] || water[i] || desert[i] || HasWaterWithin( water, res, i, 1 ) )
							continue;
						var drop = h[i] - level;
						if ( drop < 120f )
							continue;
						var near = float.MaxValue;
						foreach ( var ci in set )
						{
							var dx = (ci % res) - px;
							var dy = (ci / res) - py;
							near = Math.Min( near, MathF.Sqrt( (dx * dx) + (dy * dy) ) );
						}
						if ( near < nearMinCells || near > nearMaxCells )
							continue;
						spots.Add( ((MathF.Abs( (near * cell) - 780f ) / 780f) + Math.Max( 0f, 1f - (drop / 400f) ), i) );
					}
				}
				spots.Sort( ( a, b ) => a.Score.CompareTo( b.Score ) );
				for ( var k = 0; k < spots.Count && k < 6; k++ )
				{
					sources.Add( spots[k].Cell );
					sourceLake.Add( lakeId );
				}
			}
		}
		var lakeReached = new bool[lakeCells.Count];
		var forcedCount = sources.Count;

		var spacing = Math.Max( cell * 2f, settings.RiverSourceSpacingMeters );
		var wanted = Math.Clamp( settings.RiverCount, 1, 80 );
		foreach ( var c in candidates )
		{
			if ( sources.Count >= forcedCount + (wanted * 12) )   // 5x was enough at Max Cut 25; strict descent drops more routes
				break;
			var ok = true;
			foreach ( var s in sources )
			{
				var dx = wxs[c] - wxs[s];
				var dy = wys[c] - wys[s];
				if ( (dx * dx) + (dy * dy) < spacing * spacing )
				{
					ok = false;
					break;
				}
			}
			if ( ok )
			{
				sources.Add( c );
				sourceLake.Add( -1 );
			}
		}

		// ---- descent per source
		var riverCell = new int[count];
		Array.Fill( riverCell, -1 );
		var riverLevel = new float[count];
		var stamp = new int[count];
		var minDrop = Math.Max( 0.0005f, settings.RiverMinGradePercent * 0.01f );
		var maxCut = Math.Max( 1f, settings.RiverMaxCutMeters );
		var maxSteps = res * 3;
		var depth = Math.Clamp( settings.RiverDepthMeters, 0.25f, 12f );
		var bankTan = MathF.Tan( Math.Clamp( settings.RiverBankMaxSlopeDegrees, 8f, 60f ) * MathF.PI / 180f );
		var halfMin = Math.Max( 1f, settings.RiverWidthMinMeters * 0.5f );
		var halfMax = Math.Max( halfMin, settings.RiverWidthMaxMeters * 0.5f );
		var bankScale = Math.Clamp( settings.RiverBankWidthScale, 0.25f, 6f );
		var smoothing = Math.Clamp( settings.RiverSmoothingMeters, 0f, 400f );
		var bedDepth = Math.Max( 0f, settings.RiverBedDepthMeters );

		var segments = new List<Segment>();
		var firstSegment = new List<int>();
		var segmentCount = new List<int>();
		var joins = new List<int>();
		var endCell = new List<int>();   // per built river: the routing cell it ended on (a water cell, or the other river's cell)
		var builtOf = new Dictionary<int, int>();   // routing id -> built index
		var built = 0;
		var path = new List<int>( 1024 );
		var levels = new List<float>( 1024 );

		for ( var si = 0; si < sources.Count && built < wanted; si++ )
		{
			var lakeFor = sourceLake[si];
			if ( lakeFor >= 0 && lakeReached[lakeFor] )
				continue;
			net.Attempted++;
			var riverId = si + 1;
			path.Clear();
			levels.Clear();
			var c = sources[si];
			path.Add( c );
			levels.Add( h[c] - depth );
			stamp[c] = riverId;
			var dirX = 0;
			var dirY = 0;
			var reached = false;
			var endLevel = sea;
			var joinId = -1;

			for ( var step = 0; step < maxSteps; step++ )
			{
				var cx = c % res;
				var cy = c / res;
				var best = -1;
				var bestScore = float.MaxValue;
				var joined = false;
				for ( var oy = -1; oy <= 1 && !joined; oy++ )
				{
					for ( var ox = -1; ox <= 1; ox++ )
					{
						if ( ox == 0 && oy == 0 )
							continue;
						var nx = cx + ox;
						var ny = cy + oy;
						if ( nx < 0 || ny < 0 || nx >= res || ny >= res )
							continue;
						var n = (ny * res) + nx;

						// Another river: join it at its level and stop.
						if ( riverCell[n] >= 0 && riverCell[n] != riverId )
						{
							path.Add( n );
							levels.Add( Math.Min( levels[^1], riverLevel[n] ) );
							endLevel = riverLevel[n];
							joinId = riverCell[n];
							reached = true;
							joined = true;
							break;
						}

						// Lake or sea: the mouth — at that water's level. A tarn standing above the river (a lake on a mountain
						// top beside a valley river) is not a mouth: water cannot run up into it.
						if ( water[n] || !land[n] )
						{
							if ( cellLevel[n] > levels[^1] + 0.5f )
								continue;
							path.Add( n );
							levels.Add( cellLevel[n] );
							endLevel = cellLevel[n];
							reached = true;
							joined = true;
							break;
						}

						if ( stamp[n] == riverId )
							continue;

						// Lowest neighbour wins; a little reluctance to turn sharply keeps the route from zig-zagging.
						var turn = (dirX == 0 && dirY == 0) ? 0f : 1f - (((ox * dirX) + (oy * dirY)) / (MathF.Sqrt( (ox * ox) + (oy * oy) ) * MathF.Sqrt( (dirX * dirX) + (dirY * dirY) )));
						var score = h[n] + (turn * 0.25f);   // 0.6 made every river a straight run (Mark 2026-10-08)
						if ( score < bestScore )
						{
							bestScore = score;
							best = n;
						}
					}
				}

				if ( reached )
					break;
				if ( best < 0 )
					break;
				if ( desert[best] )
				{
					net.DesertDropped++;
					break;   // no rivers through Amber Dunes
				}

				var bx = best % res;
				var by = best / res;
				var stepLen = cell * MathF.Sqrt( ((bx - cx) * (bx - cx)) + ((by - cy) * (by - cy)) );
				// The water sits Depth below the smoothed ground of the cell and only ever falls. (A 3x3 minimum, raw or
				// smoothed, pulled the level down into every side valley and dropped most rivers at the max-cut check;
				// the cut-only carve already stops a dip beside the channel from becoming a levee.) Raw ground sizes the banks.
				// The cut is checked against the REAL ground of the cell as well as the smoothed field: the smoothed field
				// hid 10-20 m of the ground the route was actually climbing, and with Max Cut at 25 m rivers ran for
				// kilometres in trenches up to 38 m deep across flat clover — "it goes uphill" (Mark 2026-10-30; 37 % of all
				// river length sat in a trench over 8 m on seed 1337). A route that cannot get through under Max Cut stops
				// here and the river is dropped; the source pool is wide enough to replace it.
				// Checked against the real ground only when the route is CLIMBING real ground (the trench case); down a
				// mountain face the smoothed field lags the raw ground by 50 m+ and a raw check there dropped every
				// waterfall river (28 -> 5 on seed 1337).
				var nextLevel = Math.Min( levels[^1] - (minDrop * stepLen), h[best] - depth );
				var checkGround = rawH[best] > rawH[c] ? Math.Max( h[best], rawH[best] ) : h[best];
				if ( checkGround - nextLevel > maxCut )
					break;   // would need a canyon through a ridge: drop this river

				path.Add( best );
				levels.Add( nextLevel );
				stamp[best] = riverId;
				dirX = bx - cx;
				dirY = by - cy;
				c = best;
			}

			if ( !reached || path.Count < 4 )
			{
				net.Failed++;
				continue;
			}

			// Levels never below the sea, never rising downstream; the mouth sits at the water it joins.
			levels[^1] = Math.Max( sea, endLevel );
			for ( var i = levels.Count - 2; i >= 0; i-- )
				levels[i] = Math.Max( levels[i], levels[i + 1] );
			for ( var i = 0; i < levels.Count; i++ )
				levels[i] = Math.Max( sea, levels[i] );

			// Cell centres → smoothed, evenly spaced polyline with interpolated levels.
			var pts = new List<Vector2>( path.Count );
			foreach ( var p in path )
				pts.Add( new Vector2( wxs[p], wys[p] ) );
			var lv = new List<float>( levels );
			var cuts = new List<float>( path.Count );
			for ( var i = 0; i < path.Count; i++ )
				cuts.Add( Math.Max( 0f, rawH[path[i]] - levels[i] ) );
			// Into a mountain-foot lake: the routing cell was made water for the router's sake, the real water may be a
			// pond off to one side of it — lead the line on to the nearest water point so EndAtWater finds the contact.
			if ( joinId < 0 )
			{
				for ( var k = 0; k < lakeCells.Count; k++ )
				{
					if ( !lakeCells[k].Contains( path[^1] ) )
						continue;
					var last = pts[^1];
					var nearest = last;
					var nearestD = float.MaxValue;
					foreach ( var wp in lakeWaterPts[k] )
					{
						var d = Vector2.DistanceSquared( last, wp );
						if ( d < nearestD )
						{
							nearestD = d;
							nearest = wp;
						}
					}
					if ( nearestD > 10f * 10f )
					{
						pts.Add( nearest );
						lv.Add( lv[^1] );
						cuts.Add( 0f );
					}
					break;
				}
			}
			ResamplePolyline( pts, lv, cuts, smoothing, 20f );

			// Character (Mark 2026-10-08: "too straight… twists and bends, varying thickness, little pools"):
			// a seeded sinuous side-to-side offset along the arc, a slow width multiplier, and pool stretches
			// where the channel widens ~3x and the bed deepens. All before the fine-ground level pass, so the
			// carve follows the final line.
			var widthMul = new float[pts.Count];
			var depthMul = new float[pts.Count];
			ShapeRiver( pts, lv, widthMul, depthMul, seed + (riverId * 31), halfMin, halfMax );
			CutSelfCrossings( pts, lv, cuts, ref widthMul, ref depthMul, halfMax );
			// A tributary (Mark 2026-10-25: "where rivers converge needs major work"): the route stopped at the routing
			// cell of the river it reached, but that river has since been meandered — end this one on its FINAL centre
			// line instead (cut where it first reaches into the channel, or carried on to it)
			var target = joinId >= 0 && builtOf.TryGetValue( joinId, out var joinBuilt ) ? joinBuilt : -1;
			if ( target >= 0 )
				JoinRiver( pts, lv, cuts, ref widthMul, ref depthMul, segments, firstSegment[target], segmentCount[target] );
			EndAtWater( pts, lv, cuts, ref widthMul, ref depthMul, preRiverHeightMeters, WaterLevelAt, halfMin );
			BendAndMouthWidths( pts, widthMul, preRiverHeightMeters, WaterLevelAt );

			// The level profile is set from the REAL fine ground along the final route (centre line and both
			// channel edges), not the 80 m routing field: box-smoothing raises valley floors, and a river routed
			// down a valley at "smoothed minus depth" sat above the true floor as a raised strip of water with
			// green slopes climbing up to it (Mark 2026-10-07). Only ever falling, depth under the lowest of the
			// three samples, never below the sea.
			if ( !SetLevelsFromFineGround( pts, lv, cuts, preRiverHeightMeters, halfMin, halfMax, widthMul, depth, minDrop, sea, maxCut * 1.6f, levels[^1], joinId < 0 ) )
			{
				net.Failed++;   // the fine ground (ledges, ravines) would need a gorge deeper than 1.6 x Max Cut
				continue;
			}

			// …and its water meets the other river's at the join: the levels ease onto that river's level over the last stretch
			if ( target >= 0 )
				MeetRiverLevel( pts, lv, cuts, NearestOnRiver( segments, firstSegment[target], segmentCount[target], pts[^1] ).Level );

			for ( var i = 0; i < path.Count; i++ )
			{
				if ( riverCell[path[i]] < 0 )
				{
					riverCell[path[i]] = riverId;
					riverLevel[path[i]] = levels[i];
				}
			}

			var total = 0f;
			for ( var i = 1; i < pts.Count; i++ )
				total += Vector2.Distance( pts[i - 1], pts[i] );
			net.TotalLengthMeters += total;

			// Some rivers run shallower than others (Mark 2026-10-21): a seeded 0.6–1.2x on the bed depth per river.
			// Pools multiply on top; TerrainPreviewPipeline caps the result per biome (Clover Hills 4 m).
			var riverDepthMul = 0.6f + (0.6f * TerrainPreviewNoise.Hash01( seed + 775, riverId, 9 ));
			// Mountain rise under every vertex: the bank rule is the lowland's valley or the mountain's gorge by it
			var mountainAt = new float[pts.Count];
			for ( var i = 0; i < pts.Count; i++ )
				mountainAt[i] = TerrainPreviewPipeline.SampleMountainRise01( settings, pts[i].x, pts[i].y );

			firstSegment.Add( segments.Count );
			var run = 0f;
			for ( var i = 1; i < pts.Count; i++ )
			{
				var ta = total <= 0f ? 0f : run / total;
				var arcA = run;
				run += Vector2.Distance( pts[i - 1], pts[i] );
				var tb = total <= 0f ? 1f : run / total;
				var halfA = (halfMin + ((halfMax - halfMin) * MathF.Sqrt( ta ))) * widthMul[i - 1];
				var halfB = (halfMin + ((halfMax - halfMin) * MathF.Sqrt( tb ))) * widthMul[i];
				// A deep cut spreads its bank so the side never exceeds Bank Max Slope: no canyon walls in the lowland; on a
				// mountain the spreading cut is capped at GorgeCutMeters and the river is in a gorge (see the constants).
				var bankA = Math.Max( halfA * 2f * bankScale, Math.Min( cuts[i - 1], SpreadCutCap( mountainAt[i - 1] ) ) / bankTan );
				var bankB = Math.Max( halfB * 2f * bankScale, Math.Min( cuts[i], SpreadCutCap( mountainAt[i] ) ) / bankTan );
				segments.Add( new Segment
				{
					Ax = pts[i - 1].x,
					Ay = pts[i - 1].y,
					Bx = pts[i].x,
					By = pts[i].y,
					LevelA = lv[i - 1],
					LevelB = lv[i],
					HalfA = halfA,
					HalfB = halfB,
					BankA = bankA,
					BankB = bankB,
					DepthA = bedDepth * depthMul[i - 1] * riverDepthMul,
					DepthB = bedDepth * depthMul[i] * riverDepthMul,
					ArcA = arcA,
					ArcB = run,
					River = built,
				} );
			}
			// Waterfalls: any segment dropping at WaterfallGrade or steeper (the lip of a mountain wall)
			var fell = false;
			for ( var i = firstSegment[^1]; i < segments.Count; i++ )
			{
				var len = segments[i].ArcB - segments[i].ArcA;
				if ( len > 0.5f && (segments[i].LevelA - segments[i].LevelB) / len >= WaterfallGrade )
				{
					net.WaterfallSegments++;
					fell = true;
				}
			}
			if ( fell )
				net.WaterfallRivers++;
			segmentCount.Add( segments.Count - firstSegment[^1] );
			joins.Add( target );
			endCell.Add( path[^1] );
			builtOf[riverId] = built;
			built++;
			if ( lakeFor >= 0 && EndsIn( built - 1, lakeCells[lakeFor] ) )
			{
				lakeReached[lakeFor] = true;
				net.FootLakeRivers++;
				if ( fell )
					net.FootLakeWaterfalls++;
				var (lx, ly) = lakeCentres[lakeFor];
				net.FootLakeNotes.Add( $"foot lake at ({lx:0}, {ly:0}): river from ({wxs[sources[si]]:0}, {wys[sources[si]]:0}), {rawH[sources[si]] - lv[^1]:0} m down{(fell ? ", waterfall" : ", no fall segment")}" );
			}
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				break;
		}
		for ( var k = 0; k < lakeCells.Count; k++ )
		{
			if ( lakeReached[k] )
				continue;
			var (lx, ly) = lakeCentres[k];
			net.FootLakeNotes.Add( $"foot lake at ({lx:0}, {ly:0}): no river reached it" );
		}

		// Does built river b end in one of these water cells, following the rivers it flows into?
		bool EndsIn( int b, HashSet<int> cells )
		{
			for ( var hops = 0; hops < 64 && b >= 0; hops++ )
			{
				if ( joins[b] < 0 )
					return cells.Contains( endCell[b] );
				b = joins[b];
			}
			return false;
		}

		net.Segments = segments.ToArray();
		net.RiverFirstSegment = firstSegment.ToArray();
		net.RiverSegmentCount = segmentCount.ToArray();
		net.RiverCount = built;
		net.RiverJoins = joins.ToArray();
		LinkConfluences( net );

		RasterizeSegments( net, settings );
		return net;
	}

	/// <summary>Nearest point on a built river's centre line: the point, the water level and the channel half width there.</summary>
	static (Vector2 P, float Level, float Half, float Distance) NearestOnRiver( List<Segment> segs, int first, int count, Vector2 p )
	{
		var best = (P: p, Level: 0f, Half: 1f, Distance: float.MaxValue);
		for ( var k = first; k < first + count; k++ )
		{
			var s = segs[k];
			var a = new Vector2( s.Ax, s.Ay );
			var ab = new Vector2( s.Bx, s.By ) - a;
			var len2 = ab.LengthSquared;
			var t = len2 > 1e-6f ? Math.Clamp( Vector2.Dot( p - a, ab ) / len2, 0f, 1f ) : 0f;
			var q = a + (ab * t);
			var d = Vector2.Distance( p, q );
			if ( d < best.Distance )
				best = (q, s.LevelA + ((s.LevelB - s.LevelA) * t), s.HalfA + ((s.HalfB - s.HalfA) * t), d);
		}
		return best;
	}

	/// <summary>
	/// End a tributary on the centre line of the river it flows into: cut it at the first point that reaches well into that
	/// channel, or, if it stops short (that river meandered away from the routing cell it reached), carry it on in 20 m
	/// steps to the nearest point of that centre line. The last point lands exactly on the centre line.
	/// </summary>
	static void JoinRiver( List<Vector2> pts, List<float> lv, List<float> cuts, ref float[] widthMul, ref float[] depthMul, List<Segment> segs, int first, int count )
	{
		var n = pts.Count;
		if ( n < 2 || count <= 0 )
			return;
		var cutAt = -1;
		for ( var i = Math.Max( 1, n / 3 ); i < n; i++ )
		{
			var hit = NearestOnRiver( segs, first, count, pts[i] );
			if ( hit.Distance <= hit.Half * 0.6f )
			{
				cutAt = i;
				break;
			}
		}
		var keep = cutAt >= 0 ? cutAt + 1 : n;
		var endHit = NearestOnRiver( segs, first, count, pts[keep - 1] );
		var newPts = new List<Vector2>( pts.GetRange( 0, keep ) );
		var newLv = new List<float>( lv.GetRange( 0, keep ) );
		var newCuts = new List<float>( cuts.GetRange( 0, keep ) );
		var newW = new List<float>( widthMul.Take( keep ) );
		var newD = new List<float>( depthMul.Take( keep ) );
		if ( cutAt >= 0 )
			newPts[^1] = endHit.P;
		else
		{
			var from = newPts[^1];
			var steps = (int)MathF.Ceiling( endHit.Distance / 20f );
			for ( var k = 1; k <= steps; k++ )
			{
				newPts.Add( Vector2.Lerp( from, endHit.P, k / (float)steps ) );
				newLv.Add( newLv[^1] );
				newCuts.Add( newCuts[^1] );
				newW.Add( newW[^1] );
				newD.Add( newD[^1] );
			}
		}
		pts.Clear();
		pts.AddRange( newPts );
		lv.Clear();
		lv.AddRange( newLv );
		cuts.Clear();
		cuts.AddRange( newCuts );
		widthMul = newW.ToArray();
		depthMul = newD.ToArray();
	}

	/// <summary>
	/// A tributary's water meets the river it flows into: its level eases onto that river's level over the last ~120 m
	/// (at most 40 % of it), never below it, never rising downstream. The carve follows the level, so a tributary arriving
	/// higher cuts down to the junction instead of standing over it as a step.
	/// </summary>
	static void MeetRiverLevel( List<Vector2> pts, List<float> lv, List<float> cuts, float joinLevel )
	{
		var n = pts.Count;
		if ( n < 2 )
			return;
		var arc = new float[n];
		for ( var i = 1; i < n; i++ )
			arc[i] = arc[i - 1] + Vector2.Distance( pts[i - 1], pts[i] );
		var reach = Math.Min( 120f, arc[n - 1] * 0.4f );
		for ( var i = 0; i < n; i++ )
		{
			var old = lv[i];
			var fromEnd = arc[n - 1] - arc[i];
			if ( fromEnd < reach && old > joinLevel )
			{
				var t = fromEnd / Math.Max( 1f, reach );
				lv[i] = joinLevel + ((old - joinLevel) * t * t * (3f - (2f * t)));
			}
			lv[i] = Math.Max( lv[i], joinLevel );
			cuts[i] = Math.Max( 0f, cuts[i] + (old - lv[i]) );
		}
		for ( var i = n - 2; i >= 0; i-- )
			lv[i] = Math.Max( lv[i], lv[i + 1] );
	}

	/// <summary>
	/// Nearest river at a world point, through the fine raster then a few exact segment distances. The raster keeps ONE
	/// nearest segment per ~78 m cell (nearest to the cell's centre), so a cell between two rivers that are not a
	/// confluence pair belonged to one of them and the other's channel was simply not found inside it: an uncarved
	/// square with a wall at the river level along the cell's edge (Mark 2026-10-30, "still an issue with rivers and
	/// terrain"). The segments of the eight neighbouring cells are candidates too, so every river whose reach covers
	/// the point is tried and the nearest one wins; the confluence partner (<see cref="SamplePartner"/>) still adds the
	/// second channel where two rivers meet.
	/// </summary>
	public static Hit Sample( Network net, TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		if ( net is null || net.Segments.Length == 0 )
			return default;

		if ( !TryFieldIndex( worldXMeters, worldYMeters, net.FineRes, settings.WorldRadiusMeters, settings.WorldDiameterMeters, out var idx ) )
			return default;

		var res = net.FineRes;
		var px = idx % res;
		var py = idx / res;
		Span<int> tried = stackalloc int[9];
		var triedCount = 0;
		var best = default( Hit );
		// Centre cell first, then the ring — a candidate is one window of ±6 segments (±120 m, more than a 78 m cell's
		// diagonal) round a raster segment, so a river's own segments inside the cell are never outside the window: ±3
		// left the last three segments of a tributary at its mouth unfound.
		for ( var k = 0; k < 9; k++ )
		{
			var dx = k == 0 ? 0 : ((k - 1) % 3) - 1;
			var dy = k == 0 ? 0 : ((k - 1) / 3) - 1;
			if ( k > 0 && dx == 0 && dy == 0 )
				continue;
			var nx = px + dx;
			var ny = py + dy;
			if ( nx < 0 || ny < 0 || nx >= res || ny >= res )
				continue;
			var seg = net.FineSegment[(ny * res) + nx];
			if ( seg < 0 )
				continue;
			var seen = false;
			for ( var i = 0; i < triedCount; i++ )
			{
				if ( tried[i] == seg )
				{
					seen = true;
					break;
				}
			}
			if ( seen )
				continue;
			tried[triedCount++] = seg;

			var river = net.Segments[seg].River;
			var first = net.RiverFirstSegment[river];
			var last = first + net.RiverSegmentCount[river] - 1;
			var hit = HitOnRiver( net, river, Math.Max( first, seg - 6 ), Math.Min( last, seg + 6 ), worldXMeters, worldYMeters );
			if ( hit.Near && (!best.Near || hit.Distance < best.Distance) )
				best = hit;
		}
		return best;
	}

	/// <summary>
	/// The other river at a confluence (Mark 2026-10-25: "where rivers converge needs major work"): the fine raster keeps
	/// one nearest segment per cell, so the carve switched from one river's profile to the other's along a line through
	/// the junction — a wall standing out of the water. Where this segment's carve reach overlaps the other river's
	/// (<see cref="LinkConfluences"/>, precomputed), returns that river's hit so the carve can take both channels. No
	/// radius cut-off: a 200 m one left a step in the banks where it switched off.
	/// </summary>
	public static Hit SamplePartner( Network net, TerrainPreviewSettings settings, float worldXMeters, float worldYMeters, Hit primary )
	{
		if ( net is null || !primary.Near || primary.Segment < 0 || primary.Segment >= net.PartnerFrom.Length )
			return default;
		var from = net.PartnerFrom[primary.Segment];
		if ( from < 0 )
			return default;
		return HitOnRiver( net, net.Segments[from].River, from, net.PartnerTo[primary.Segment], worldXMeters, worldYMeters );
	}

	/// <summary>How deep a cut may still spread its bank to Bank Max Slope here: unlimited in the lowland, GorgeCutMeters on a mountain.</summary>
	static float SpreadCutCap( float mountainRise01 )
	{
		var t = Math.Clamp( mountainRise01 / GorgeMountainRise01, 0f, 1f );
		t = t * t * (3f - (2f * t));
		return float.MaxValue * (1f - t) + (GorgeCutMeters * t) > GorgeCutMeters * 100f ? float.MaxValue : GorgeCutMeters + ((GorgeCutMeters * 100f) - GorgeCutMeters) * (1f - t);
	}

	/// <summary>Carve reach of a segment: channel half width + slide wall + bank, the wider end.</summary>
	static float SegmentReach( Network net, in Segment s )
		=> Math.Max( s.HalfA + s.BankA, s.HalfB + s.BankB ) + net.SlideWidthMeters;

	/// <summary>
	/// For every tributary, pair each of its segments with the main river's segments whose carve reaches overlap it (and
	/// the other way round), padded by three segments either side so a point between pairs always finds its partner.
	/// </summary>
	static void LinkConfluences( Network net )
	{
		var n = net.Segments.Length;
		net.PartnerFrom = new int[n];
		net.PartnerTo = new int[n];
		Array.Fill( net.PartnerFrom, -1 );
		Array.Fill( net.PartnerTo, -1 );
		for ( var r = 0; r < net.RiverCount; r++ )
		{
			var t = net.RiverJoins[r];
			if ( t < 0 )
				continue;
			var rFirst = net.RiverFirstSegment[r];
			var rLast = rFirst + net.RiverSegmentCount[r] - 1;
			var tFirst = net.RiverFirstSegment[t];
			var tLast = tFirst + net.RiverSegmentCount[t] - 1;
			for ( var i = rFirst; i <= rLast; i++ )
			{
				ref var si = ref net.Segments[i];
				var reachI = SegmentReach( net, si );
				for ( var j = tFirst; j <= tLast; j++ )
				{
					ref var sj = ref net.Segments[j];
					var d = Math.Min(
						Math.Min( DistanceToSegment( sj, si.Ax, si.Ay, out _ ), DistanceToSegment( sj, si.Bx, si.By, out _ ) ),
						Math.Min( DistanceToSegment( si, sj.Ax, sj.Ay, out _ ), DistanceToSegment( si, sj.Bx, sj.By, out _ ) ) );
					if ( d > reachI + SegmentReach( net, sj ) )
						continue;
					Extend( i, Math.Max( tFirst, j - 3 ), Math.Min( tLast, j + 3 ) );
					Extend( j, Math.Max( rFirst, i - 3 ), Math.Min( rLast, i + 3 ) );
				}
			}
		}

		void Extend( int segment, int from, int to )
		{
			net.PartnerFrom[segment] = net.PartnerFrom[segment] < 0 ? from : Math.Min( net.PartnerFrom[segment], from );
			net.PartnerTo[segment] = Math.Max( net.PartnerTo[segment], to );
		}
	}

	static Hit HitOnRiver( Network net, int river, int from, int to, float x, float y )
	{
		var bestDist = float.MaxValue;
		var bestT = 0f;
		var bestIndex = -1;
		for ( var s = from; s <= to; s++ )
		{
			var d = DistanceToSegment( net.Segments[s], x, y, out var t );
			if ( d < bestDist )
			{
				bestDist = d;
				bestT = t;
				bestIndex = s;
			}
		}
		if ( bestIndex < 0 )
			return default;
		ref var b = ref net.Segments[bestIndex];
		var half = b.HalfA + ((b.HalfB - b.HalfA) * bestT);
		var bank = b.BankA + ((b.BankB - b.BankA) * bestT);
		if ( bestDist > half + net.SlideWidthMeters + bank )
			return default;
		return new Hit
		{
			Near = true,
			Distance = bestDist,
			Level = b.LevelA + ((b.LevelB - b.LevelA) * bestT),
			HalfWidth = half,
			BankWidth = bank,
			BedDepthMeters = b.DepthA + ((b.DepthB - b.DepthA) * bestT),
			River = river,
			Segment = bestIndex,
		};
	}

	public enum RiverPoint { Mid, Source, Mouth }

	/// <summary>World point on the middle of river <paramref name="river"/> (dev teleport). False when no such river.</summary>
	public static bool TryGetRiverMidpoint( Network net, int river, out Vector2 point, out Vector2 forward, out float level )
		=> TryGetRiverPoint( net, river, RiverPoint.Mid, out point, out forward, out level );

	/// <summary>World point on river <paramref name="river"/>: its middle segment, its source (first vertex) or its mouth (last vertex). False when no such river.</summary>
	public static bool TryGetRiverPoint( Network net, int river, RiverPoint at, out Vector2 point, out Vector2 forward, out float level )
	{
		point = default;
		forward = Vector2.Zero;
		level = 0f;
		if ( net is null || river < 0 || river >= net.RiverCount )
			return false;

		var first = net.RiverFirstSegment[river];
		var count = net.RiverSegmentCount[river];
		switch ( at )
		{
			case RiverPoint.Source:
			{
				ref var s = ref net.Segments[first];
				point = new Vector2( s.Ax, s.Ay );
				forward = new Vector2( s.Bx - s.Ax, s.By - s.Ay ).Normal;
				level = s.LevelA;
				return true;
			}
			case RiverPoint.Mouth:
			{
				ref var s = ref net.Segments[first + count - 1];
				point = new Vector2( s.Bx, s.By );
				forward = new Vector2( s.Bx - s.Ax, s.By - s.Ay ).Normal;
				level = s.LevelB;
				return true;
			}
			default:
			{
				ref var s = ref net.Segments[first + (count / 2)];
				point = new Vector2( (s.Ax + s.Bx) * 0.5f, (s.Ay + s.By) * 0.5f );
				forward = new Vector2( s.Bx - s.Ax, s.By - s.Ay ).Normal;
				level = (s.LevelA + s.LevelB) * 0.5f;
				return true;
			}
		}
	}

	static void RasterizeSegments( Network net, TerrainPreviewSettings settings )
	{
		var res = net.FineRes;
		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		var cellSize = diameter / res;
		var best = new float[res * res];
		Array.Fill( best, float.MaxValue );

		for ( var si = 0; si < net.Segments.Length; si++ )
		{
			ref var s = ref net.Segments[si];
			var reach = Math.Max( s.HalfA + s.BankA, s.HalfB + s.BankB ) + net.SlideWidthMeters + (cellSize * 1.5f);
			var minX = Math.Min( s.Ax, s.Bx ) - reach;
			var maxX = Math.Max( s.Ax, s.Bx ) + reach;
			var minY = Math.Min( s.Ay, s.By ) - reach;
			var maxY = Math.Max( s.Ay, s.By ) + reach;

			var py0 = Math.Clamp( (int)MathF.Floor( ((minY + radius) / diameter) * res ), 0, res - 1 );
			var py1 = Math.Clamp( (int)MathF.Floor( ((maxY + radius) / diameter) * res ), 0, res - 1 );
			var pm0 = Math.Clamp( (int)MathF.Floor( ((minX + radius) / diameter) * res ), 0, res - 1 );
			var pm1 = Math.Clamp( (int)MathF.Floor( ((maxX + radius) / diameter) * res ), 0, res - 1 );
			for ( var py = py0; py <= py1; py++ )
			{
				for ( var pm = pm0; pm <= pm1; pm++ )
				{
					var px = (res - 1) - pm;
					var idx = (py * res) + px;
					TerrainBiomeMapCoordinates.RasterPixelToWorldMeters( px, py, res, radius, diameter, out var wx, out var wy );
					var d = DistanceToSegment( s, wx, wy, out _ );
					if ( d > reach || d >= best[idx] )
						continue;
					best[idx] = d;
					net.FineSegment[idx] = si;
				}
			}
		}
	}

	static float DistanceToSegment( in Segment s, float x, float y, out float t )
	{
		var vx = s.Bx - s.Ax;
		var vy = s.By - s.Ay;
		var wx = x - s.Ax;
		var wy = y - s.Ay;
		var len2 = (vx * vx) + (vy * vy);
		t = len2 <= 1e-6f ? 0f : Math.Clamp( ((wx * vx) + (wy * vy)) / len2, 0f, 1f );
		var px = s.Ax + (vx * t);
		var py = s.Ay + (vy * t);
		var dx = x - px;
		var dy = y - py;
		return MathF.Sqrt( (dx * dx) + (dy * dy) );
	}

	static bool TryFieldIndex( float wx, float wy, int res, float radius, float diameter, out int index )
	{
		index = 0;
		if ( diameter <= 0f )
			return false;
		var py = (int)MathF.Floor( ((wy + radius) / diameter) * res );
		var pm = (int)MathF.Floor( ((wx + radius) / diameter) * res );
		var px = (res - 1) - pm;
		if ( px < 0 || py < 0 || px >= res || py >= res )
			return false;
		index = (py * res) + px;
		return true;
	}

	static bool SetLevelsFromFineGround(
		List<Vector2> pts,
		List<float> levels,
		List<float> cuts,
		Func<float, float, float> groundMeters,
		float halfMin,
		float halfMax,
		float[] widthMul,
		float depth,
		float minDrop,
		float sea,
		float maxFineCut,
		float mouthLevel,
		bool endsInStillWater )
	{
		var n = pts.Count;
		if ( n == 0 )
			return false;

		var total = 0f;
		for ( var i = 1; i < n; i++ )
			total += Vector2.Distance( pts[i - 1], pts[i] );

		var run = 0f;
		var prevLevel = float.MaxValue;
		for ( var i = 0; i < n; i++ )
		{
			if ( i > 0 )
				run += Vector2.Distance( pts[i - 1], pts[i] );
			var t = total <= 0f ? 0f : run / total;
			var half = (halfMin + ((halfMax - halfMin) * MathF.Sqrt( t ))) * (widthMul is not null && i < widthMul.Length ? widthMul[i] : 1f);

			var dir = i + 1 < n ? pts[i + 1] - pts[i] : pts[i] - pts[Math.Max( 0, i - 1 )];
			if ( dir.LengthSquared < 1e-6f )
				dir = Vector2.Right;
			var side = new Vector2( -dir.y, dir.x ).Normal;

			var centre = groundMeters( pts[i].x, pts[i].y );
			var g = centre;
			g = Math.Min( g, groundMeters( pts[i].x + (side.x * half), pts[i].y + (side.y * half) ) );
			g = Math.Min( g, groundMeters( pts[i].x - (side.x * half), pts[i].y - (side.y * half) ) );
			var steepAhead = false;
			if ( i + 1 < n )
			{
				// Half way to the next vertex too: a dip between two vertices used to leave water above ground. Only a
				// real dip counts — ground lower than BOTH vertices. On a steep descent (a fall over a mountain wall, Mark
				// 2026-10-30) the half-way ground lies between the two and the next vertex's own level takes the drop;
				// reading it here pulled the water at the lip tens of metres under the ground and dropped the river.
				var next = groundMeters( pts[i + 1].x, pts[i + 1].y );
				steepAhead = next < centre - 8f;
				var mid = (pts[i] + pts[i + 1]) * 0.5f;
				var gMid = groundMeters( mid.x, mid.y );
				gMid = Math.Min( gMid, groundMeters( mid.x + (side.x * half), mid.y + (side.y * half) ) );
				gMid = Math.Min( gMid, groundMeters( mid.x - (side.x * half), mid.y - (side.y * half) ) );
				if ( gMid < Math.Min( centre, next ) - 0.5f )
					g = Math.Min( g, gMid );
			}

			var level = g - depth;
			if ( i > 0 )
				level = Math.Min( level, prevLevel - (minDrop * Vector2.Distance( pts[i - 1], pts[i] )) );
			level = Math.Max( sea, level );
			if ( centre - level > maxFineCut )
			{
				// A gorge through a ridge drops the river — but at the lip of a fall the "cut" is the wall beside the
				// channel, not a canyon to dig: cap it and let the water go over the edge.
				if ( !steepAhead )
					return false;
				level = Math.Max( sea, centre - maxFineCut );
			}
			levels[i] = level;
			cuts[i] = Math.Max( 0f, centre - level );
			prevLevel = level;
		}

		if ( endsInStillWater )
			ApproachMouth( pts, levels, cuts, groundMeters, Math.Max( sea, mouthLevel ), maxFineCut, total );
		return true;
	}

	/// <summary>Longest stretch (m) over which a river eases down to the lake / sea it runs into.</summary>
	const float MouthApproachMeters = 500f;
	/// <summary>The river is at the lake's level over this last stretch (the water shaders' hand-over is at most 60 m).</summary>
	const float MouthHandOverMeters = 60f;

	/// <summary>
	/// River mouths (Mark 2026-10-25: rivers did not blend into the lakes): the level followed the ground, and the ground
	/// rises steeply from every shore, so a river ran its last tens of metres down a ramp — 14 m in 65 m at one mouth, a
	/// sloped slab of water meeting the flat lake. Over the last stretch (up to <see cref="MouthApproachMeters"/>, at most
	/// 45 % of the river) the level eases to the mouth's level on (d / W)^1.6 — flat where it meets the lake — and the
	/// carve cuts a valley through the shore bank (deep cuts already spread their bank to Bank Max Slope). Cuts up to 3×
	/// the fine-cut limit; only for rivers that end at the sea level (not ones joining another river).
	/// </summary>
	static void ApproachMouth( List<Vector2> pts, List<float> levels, List<float> cuts, Func<float, float, float> groundMeters, float sea, float maxFineCut, float total )
	{
		// "sea" here is the level of the still water the river ends in: the sea, a lake, or a tarn on a mountain top.
		var n = pts.Count;
		if ( n < 3 || levels[n - 1] > sea + 0.01f )
			return;
		// The mouth is where the level first reaches the lake / sea — the route itself runs on out into the water, and a
		// window measured from its end could lie entirely in open water
		var mouth = n - 1;
		for ( var i = 0; i < n; i++ )
		{
			if ( levels[i] <= sea + 0.01f )
			{
				mouth = i;
				break;
			}
		}
		var window = Math.Min( MouthApproachMeters, total * 0.45f );
		if ( window < 20f || mouth < 2 )
			return;
		// Arc distance from the mouth for each point upstream of it, and the level where the window starts
		var fromMouth = new float[n];
		for ( var i = mouth - 1; i >= 0; i-- )
			fromMouth[i] = fromMouth[i + 1] + Vector2.Distance( pts[i], pts[i + 1] );
		var top = levels[0];
		for ( var i = 0; i < n; i++ )
		{
			if ( fromMouth[i] <= window )
			{
				top = i > 0 ? levels[i - 1] : levels[i];
				break;
			}
		}
		var rise = top - sea;
		if ( rise <= 0.05f )
			return;
		for ( var i = 0; i < mouth; i++ )
		{
			if ( fromMouth[i] > window )
				continue;
			// At the lake's level over the last HandOver metres: that stretch is where the lake / sea surface takes over
			// from the river (river_mouths.hlsl), and a river still above the lake there faded out over a lake surface
			// lying under its bed — a see-through hole in the river (Mark 2026-10-25)
			var handOver = Math.Min( MouthHandOverMeters, window * 0.5f );
			var d = Math.Max( 0f, fromMouth[i] - handOver ) / Math.Max( 1f, window - handOver );
			var cap = sea + (rise * MathF.Pow( d, 1.6f ));
			var centre = groundMeters( pts[i].x, pts[i].y );
			// The shore bluff is what has to be cut through: up to 3× the usual fine-cut limit here (the usual limit held
			// the water 9.6 m under a 17 m bluff and left the ramp in place)
			cap = Math.Max( cap, centre - (maxFineCut * 3f) );
			if ( cap >= levels[i] )
				continue;
			levels[i] = Math.Max( sea, cap );
			cuts[i] = Math.Max( 0f, centre - levels[i] );
		}
		// Never rising downstream
		for ( var i = n - 2; i >= 0; i-- )
			levels[i] = Math.Max( levels[i], levels[i + 1] );
	}

	/// <summary>Lowest unsmoothed ground in the 3x3 around a routing cell: the water must sit under all of it.</summary>
	static float GroundFloor3x3( float[] rawH, bool[] land, int res, int idx )
	{
		var cx = idx % res;
		var cy = idx / res;
		var lo = rawH[idx];
		for ( var oy = -1; oy <= 1; oy++ )
		{
			for ( var ox = -1; ox <= 1; ox++ )
			{
				var nx = cx + ox;
				var ny = cy + oy;
				if ( nx < 0 || ny < 0 || nx >= res || ny >= res )
					continue;
				var n = (ny * res) + nx;
				if ( land[n] )
					lo = Math.Min( lo, rawH[n] );
			}
		}
		return lo;
	}

	static bool HasWaterWithin( bool[] water, int res, int idx, int cells )
	{
		var cx = idx % res;
		var cy = idx / res;
		for ( var oy = -cells; oy <= cells; oy++ )
		{
			for ( var ox = -cells; ox <= cells; ox++ )
			{
				var nx = cx + ox;
				var ny = cy + oy;
				if ( nx < 0 || ny < 0 || nx >= res || ny >= res )
					return true;
				if ( water[(ny * res) + nx] )
					return true;
			}
		}
		return false;
	}

	static void SmoothLand( float[] h, bool[] land, int res, int passes )
	{
		var tmp = new float[h.Length];
		for ( var pass = 0; pass < passes; pass++ )
		{
			for ( var py = 0; py < res; py++ )
			{
				for ( var px = 0; px < res; px++ )
				{
					var i = (py * res) + px;
					if ( !land[i] )
					{
						tmp[i] = h[i];
						continue;
					}
					var sum = 0f;
					var n = 0;
					for ( var oy = -1; oy <= 1; oy++ )
					{
						for ( var ox = -1; ox <= 1; ox++ )
						{
							var nx = px + ox;
							var ny = py + oy;
							if ( nx < 0 || ny < 0 || nx >= res || ny >= res )
								continue;
							var j = (ny * res) + nx;
							sum += h[j];
							n++;
						}
					}
					tmp[i] = n > 0 ? sum / n : h[i];
				}
			}
			Array.Copy( tmp, h, h.Length );
		}
	}

	/// <summary>
	/// Width from the river's shape (Mark 2026-10-25: "short when it goes straight and wider when it turns, also the
	/// mouth should be kinda flared out like it would be in real life through erosion"), replacing a slow random width:
	/// the turn rate of the final line (smoothed over ±3 points) sets 0.75× on straights up to 1.6× in tight bends (≈ 50 m
	/// radius), and a river that ends in a lake / the sea widens over its last 180 m to 2.4× at the shoreline and beyond
	/// (an estuary). Pools stay on top.
	/// </summary>
	static void BendAndMouthWidths( List<Vector2> pts, float[] widthMul, Func<float, float, float> ground, Func<float, float, float> waterLevelAt )
	{
		var n = pts.Count;
		if ( n < 3 )
			return;
		var turn = new float[n];
		for ( var i = 1; i < n - 1; i++ )
		{
			var a = pts[i] - pts[i - 1];
			var b = pts[i + 1] - pts[i];
			var la = a.Length;
			var lb = b.Length;
			if ( la < 1e-3f || lb < 1e-3f )
				continue;
			var cos = Math.Clamp( Vector2.Dot( a, b ) / (la * lb), -1f, 1f );
			turn[i] = MathF.Acos( cos ) / ((la + lb) * 0.5f);   // rad per metre
		}
		for ( var i = 0; i < n; i++ )
		{
			var sum = 0f;
			var cnt = 0;
			for ( var j = Math.Max( 0, i - 3 ); j <= Math.Min( n - 1, i + 3 ); j++ )
			{
				sum += turn[j];
				cnt++;
			}
			var k = sum / Math.Max( 1, cnt );
			var t = Math.Clamp( (k - 0.002f) / (0.02f - 0.002f), 0f, 1f );
			widthMul[i] *= 0.75f + (0.85f * t * t * (3f - (2f * t)));
		}

		// Estuary: only for a river whose end lies in open water (ground under the local water level)
		var end = pts[n - 1];
		if ( ground( end.x, end.y ) >= waterLevelAt( end.x, end.y ) - 0.05f )
			return;
		var contact = n - 1;
		for ( var i = 0; i < n; i++ )
		{
			if ( ground( pts[i].x, pts[i].y ) < waterLevelAt( pts[i].x, pts[i].y ) - 0.05f )
			{
				contact = i;
				break;
			}
		}
		const float flare = 180f;
		var fromContact = 0f;
		for ( var i = contact; i >= 0 && fromContact < flare; i-- )
		{
			if ( i < contact )
				fromContact += Vector2.Distance( pts[i], pts[i + 1] );
			var f = Math.Max( 0f, 1f - (fromContact / flare) );
			widthMul[i] *= 1f + (1.4f * f * f);
		}
		for ( var i = contact + 1; i < n; i++ )
			widthMul[i] *= 2.4f;
	}

	/// <summary>
	/// A river meets the lake / sea head-on (Mark 2026-10-25, river mouths): the route ends at the first water cell of
	/// the 80 m routing grid and the meander then shifts it, so a river could reach the shore at a glancing angle and run
	/// along it — half its channel in the water, cut lengthwise by the sea's straight edge. The line now stops where its
	/// channel (centre or either edge) first touches open water, and carries on straight out into deeper water (down the
	/// ground's slope) for 60 m, the bend into that direction rounded over the last points.
	/// </summary>
	static void EndAtWater( List<Vector2> pts, List<float> lv, List<float> cuts, ref float[] widthMul, ref float[] depthMul, Func<float, float, float> ground, Func<float, float, float> waterLevelAt, float halfMin )
	{
		var n = pts.Count;
		if ( n < 4 )
			return;
		var k = -1;
		for ( var i = 1; i < n; i++ )
		{
			var dir = (pts[i] - pts[i - 1]).Normal;
			var side = new Vector2( -dir.y, dir.x ) * (halfMin * (i < widthMul.Length ? widthMul[i] : 1f));
			var p = pts[i];
			var level = waterLevelAt( p.x, p.y ) - 0.05f;
			if ( ground( p.x, p.y ) < level || ground( p.x + side.x, p.y + side.y ) < level || ground( p.x - side.x, p.y - side.y ) < level )
			{
				k = i;
				break;
			}
		}
		if ( k < 2 )
			return;

		// Out into the water: down the ground's slope at the contact point (falls back to the incoming direction)
		var c = pts[k];
		const float h = 10f;
		var grad = new Vector2( ground( c.x + h, c.y ) - ground( c.x - h, c.y ), ground( c.x, c.y + h ) - ground( c.x, c.y - h ) );
		var incoming = (pts[k] - pts[k - 1]).Normal;
		var outDir = grad.LengthSquared > 1e-6f ? (-grad).Normal : incoming;
		if ( Vector2.Dot( outDir, incoming ) < -0.2f )
			outDir = incoming;   // never turn back on itself

		var keep = k + 1;
		var newPts = pts.GetRange( 0, keep );
		var newLv = lv.GetRange( 0, keep );
		var newCuts = cuts.GetRange( 0, keep );
		for ( var j = 1; j <= 3; j++ )
		{
			newPts.Add( c + (outDir * (20f * j)) );
			newLv.Add( lv[k] );
			newCuts.Add( 0f );
		}
		var m = newPts.Count;
		var newWidth = new float[m];
		var newDepth = new float[m];
		for ( var i = 0; i < m; i++ )
		{
			newWidth[i] = widthMul[Math.Min( i, k )];
			newDepth[i] = depthMul[Math.Min( i, k )];
		}
		// Round the turn into the water (the end point stays put)
		for ( var pass = 0; pass < 4; pass++ )
		{
			for ( var i = Math.Max( 1, k - 4 ); i < m - 1; i++ )
				newPts[i] = (newPts[i - 1] * 0.25f) + (newPts[i] * 0.5f) + (newPts[i + 1] * 0.25f);
		}
		pts.Clear();
		pts.AddRange( newPts );
		lv.Clear();
		lv.AddRange( newLv );
		cuts.Clear();
		cuts.AddRange( newCuts );
		widthMul = newWidth;
		depthMul = newDepth;
	}

	/// <summary>
	/// A river never crosses itself (Mark 2026-10-24: a river whose water stopped in a point and carried on as a dry
	/// channel): the meander offset can swing a bend back onto the route it left, and where the two passes meet at
	/// different levels the carve follows only the nearer one — the other pass's water ends up under the ground. Where
	/// a later point comes back within <c>3 × halfMax</c> of an earlier one (the two channels and their banks would
	/// touch), the loop between them is cut out and the river takes the neck.
	/// </summary>
	static void CutSelfCrossings( List<Vector2> pts, List<float> lv, List<float> cuts, ref float[] widthMul, ref float[] depthMul, float halfMax )
	{
		var clearance = 3f * halfMax;
		var c2 = clearance * clearance;
		var keep = new List<int>( pts.Count );
		var joints = new List<int>();
		var i = 0;
		while ( i < pts.Count )
		{
			keep.Add( i );
			var jump = -1;
			for ( var j = pts.Count - 1; j > i + 3; j-- )
			{
				if ( Vector2.DistanceSquared( pts[i], pts[j] ) < c2 )
				{
					jump = j;
					break;
				}
			}
			if ( jump > 0 )
				joints.Add( keep.Count - 1 );   // the kept point before the neck
			i = jump > 0 ? jump : i + 1;
		}
		if ( keep.Count == pts.Count )
			return;

		var newPts = new List<Vector2>( keep.Count );
		var newLv = new List<float>( keep.Count );
		var newCuts = new List<float>( keep.Count );
		var newWidth = new float[keep.Count];
		var newDepth = new float[keep.Count];
		for ( var k = 0; k < keep.Count; k++ )
		{
			var src = keep[k];
			newPts.Add( pts[src] );
			newLv.Add( lv[src] );
			newCuts.Add( cuts[src] );
			newWidth[k] = widthMul[src];
			newDepth[k] = depthMul[src];
		}
		pts.Clear();
		pts.AddRange( newPts );
		lv.Clear();
		lv.AddRange( newLv );
		cuts.Clear();
		cuts.AddRange( newCuts );
		widthMul = newWidth;
		depthMul = newDepth;

		// Round the neck: the cut leaves a corner where the loop was taken out — a few smoothing passes over the points
		// either side of it (the ends of the river never move)
		foreach ( var joint in joints )
		{
			var from = Math.Max( 1, joint - 4 );
			var to = Math.Min( pts.Count - 2, joint + 5 );
			for ( var pass = 0; pass < 6; pass++ )
			{
				for ( var k = from; k <= to; k++ )
					pts[k] = (pts[k - 1] * 0.25f) + (pts[k] * 0.5f) + (pts[k + 1] * 0.25f);
			}
		}
	}

	/// <summary>
	/// Bends and pools along an evenly spaced route. Offset is perpendicular to the local direction, zero at both ends
	/// so the mouth and source stay put: a 2-octave FBM wander over the arc (~450 m swings, 1.5–3 channel widths) plus
	/// MEANDERS (Mark 2026-10-25: "should NOT be so straight, needs to be twisty and more bendy") — a snaking bend whose
	/// wavelength is ~12 channel widths (as real rivers: 9–14) with its phase running faster and slower along the arc,
	/// swinging up to ~2.4 widths, full on flat ground and fading where the routed level falls steeply (mountain rivers
	/// run straighter, and a meander swung across a slope would have to cut a canyon). Pools where a very slow FBM
	/// peaks (roughly every 1–2 km): width up to 3.2x, bed depth up to 2.5x.
	/// </summary>
	/// <summary>Meander wavelength (m): ~12 channel widths as real rivers, never under 8 route points (160 m at 20 m spacing).</summary>
	static float MeanderWavelength( float half ) => Math.Max( 160f, 12f * half * 2f );

	static void ShapeRiver( List<Vector2> pts, List<float> levels, float[] widthMul, float[] depthMul, int seed, float halfMin, float halfMax )
	{
		var n = pts.Count;
		if ( n < 3 )
		{
			Array.Fill( widthMul, 1f );
			Array.Fill( depthMul, 1f );
			return;
		}

		var arc = new float[n];
		for ( var i = 1; i < n; i++ )
			arc[i] = arc[i - 1] + Vector2.Distance( pts[i - 1], pts[i] );
		var total = Math.Max( 1f, arc[n - 1] );

		var swing = 1.5f + (TerrainPreviewNoise.Hash01( seed, 11, 7 ) * 1.5f);
		var src = new List<Vector2>( pts );
		var phase = TerrainPreviewNoise.Hash01( seed, 13, 5 ) * MathF.Tau;
		for ( var i = 0; i < n; i++ )
		{
			var t = arc[i] / total;
			var prev = src[Math.Max( 0, i - 1 )];
			var next = src[Math.Min( n - 1, i + 1 )];
			var dir = next - prev;
			if ( dir.LengthSquared < 1e-6f )
				dir = Vector2.Right;
			var side = new Vector2( -dir.y, dir.x ).Normal;

			var half = halfMin + ((halfMax - halfMin) * MathF.Sqrt( t ));
			var ends = Math.Min( 1f, Math.Min( arc[i], total - arc[i] ) / 300f );   // pin the source and the mouth
			var bend = (TerrainPreviewNoise.Fbm( seed + 1, arc[i] / 450f, 0.37f, 2 ) - 0.5f) * 2f;
			// Meander: the phase advances by the arc over ~12 widths (0.75–1.35x, noise), so loops tighten and open
			if ( i > 0 )
			{
				var pace = 0.75f + (TerrainPreviewNoise.Fbm( seed + 5, arc[i] / 600f, 0.61f, 2 ) * 0.6f);
				phase += (arc[i] - arc[i - 1]) / MeanderWavelength( half ) * MathF.Tau * pace;
			}
			var a = Math.Max( 0, i - 3 );
			var b = Math.Min( n - 1, i + 3 );
			var grade = (levels[a] - levels[b]) / Math.Max( 1f, arc[b] - arc[a] );
			var flat = 1f - Math.Clamp( (grade - 0.01f) / 0.05f, 0f, 1f );   // full below 1 %, gone past 6 %
			// Loop depth: ~2.4 widths, but never more than a fifth of the wavelength — deeper on a short wavelength and
			// the 20 m points zig-zag (a narrow source stream folded its carve and strip into a star-shaped pit)
			var loopMeters = Math.Min( 2.4f * half * 2f, MeanderWavelength( half ) * 0.2f )
				* (0.6f + (0.4f * TerrainPreviewNoise.Fbm( seed + 7, arc[i] / 900f, 0.19f, 2 )));
			var meander = MathF.Sin( phase ) * loopMeters * flat;
			pts[i] = src[i] + (side * (((bend * swing * half * 2f) + meander) * ends));

			// The width itself comes from the final line's bends and the mouth (BendAndMouthWidths); pools on top
			widthMul[i] = 1f;

			var pool = TerrainPreviewNoise.Fbm( seed + 3, arc[i] / 1400f, 0.83f, 2 );
			var pool01 = Math.Clamp( (pool - 0.60f) / 0.12f, 0f, 1f );
			pool01 = pool01 * pool01 * (3f - (2f * pool01));
			widthMul[i] += pool01 * 2.2f;
			depthMul[i] = 1f + (pool01 * 1.5f);
		}
	}

	/// <summary>Chaikin corner cutting (strength from the smoothing distance) then even spacing; levels follow by arc length and stay non-increasing.</summary>
	static void ResamplePolyline( List<Vector2> pts, List<float> levels, List<float> cuts, float smoothingMeters, float spacingMeters )
	{
		var rounds = smoothingMeters <= 1f ? 0 : smoothingMeters < 80f ? 1 : smoothingMeters < 200f ? 2 : 3;
		for ( var r = 0; r < rounds && pts.Count > 3; r++ )
		{
			var np = new List<Vector2>( pts.Count * 2 );
			var nl = new List<float>( pts.Count * 2 );
			var nc = new List<float>( pts.Count * 2 );
			np.Add( pts[0] );
			nl.Add( levels[0] );
			nc.Add( cuts[0] );
			for ( var i = 0; i < pts.Count - 1; i++ )
			{
				np.Add( (pts[i] * 0.75f) + (pts[i + 1] * 0.25f) );
				nl.Add( (levels[i] * 0.75f) + (levels[i + 1] * 0.25f) );
				nc.Add( Math.Max( cuts[i], cuts[i + 1] ) );
				np.Add( (pts[i] * 0.25f) + (pts[i + 1] * 0.75f) );
				nl.Add( (levels[i] * 0.25f) + (levels[i + 1] * 0.75f) );
				nc.Add( Math.Max( cuts[i], cuts[i + 1] ) );
			}
			np.Add( pts[^1] );
			nl.Add( levels[^1] );
			nc.Add( cuts[^1] );
			pts.Clear();
			pts.AddRange( np );
			levels.Clear();
			levels.AddRange( nl );
			cuts.Clear();
			cuts.AddRange( nc );
		}

		// Even spacing along the arc.
		var outPts = new List<Vector2>( pts.Count );
		var outLv = new List<float>( pts.Count );
		var outCut = new List<float>( pts.Count );
		outPts.Add( pts[0] );
		outLv.Add( levels[0] );
		outCut.Add( cuts[0] );
		var carry = 0f;
		for ( var i = 1; i < pts.Count; i++ )
		{
			var a = pts[i - 1];
			var b = pts[i];
			var segLen = Vector2.Distance( a, b );
			if ( segLen <= 1e-4f )
				continue;
			var pos = spacingMeters - carry;
			while ( pos <= segLen )
			{
				var t = pos / segLen;
				outPts.Add( a + ((b - a) * t) );
				outLv.Add( levels[i - 1] + ((levels[i] - levels[i - 1]) * t) );
				outCut.Add( Math.Max( cuts[i - 1], cuts[i] ) );
				pos += spacingMeters;
			}
			carry = segLen - (pos - spacingMeters);
		}
		if ( Vector2.Distance( outPts[^1], pts[^1] ) > spacingMeters * 0.25f )
		{
			outPts.Add( pts[^1] );
			outLv.Add( levels[^1] );
			outCut.Add( cuts[^1] );
		}

		for ( var i = 1; i < outLv.Count; i++ )
			outLv[i] = Math.Min( outLv[i], outLv[i - 1] );

		pts.Clear();
		pts.AddRange( outPts );
		levels.Clear();
		levels.AddRange( outLv );
		cuts.Clear();
		cuts.AddRange( outCut );
	}
}
