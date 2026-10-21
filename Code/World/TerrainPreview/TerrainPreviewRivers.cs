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
		/// <summary>Width of the curved bank wall outside the channel (River Bank Slide Width): part of every segment's reach.</summary>
		public float SlideWidthMeters;
		public int Attempted;
		public int Failed;
		public int DesertDropped;   // routes dropped because they entered Amber Dunes
		public float TotalLengthMeters;
	}

	const int RoutingResolution = 256;

	public static Network Build(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		bool[] openWater,
		bool[] desertField,
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
		var h = new float[count];
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
					continue;
				}

				land[i] = true;
				water[i] = openWater[fi];
				desert[i] = desertField is not null && fi < desertField.Length && desertField[fi];
				h[i] = water[i] ? sea : preRiverHeightMeters( wx, wy );
			}
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				return net;
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
		for ( var i = 0; i < count; i++ )
		{
			if ( !land[i] || water[i] || desert[i] || h[i] < threshold )
				continue;
			if ( HasWaterWithin( water, res, i, 1 ) )
				continue;
			candidates.Add( i );
		}
		var seed = settings.WorldSeed;
		float SourceKey( int i ) => TerrainPreviewNoise.Hash01( seed + 990, i, 3 ) + (0.5f * (1f - ((h[i] - threshold) / heightSpan)));
		candidates.Sort( ( a, b ) => SourceKey( a ).CompareTo( SourceKey( b ) ) );

		var spacing = Math.Max( cell * 2f, settings.RiverSourceSpacingMeters );
		var sources = new List<int>();
		var wanted = Math.Clamp( settings.RiverCount, 1, 64 );
		foreach ( var c in candidates )
		{
			if ( sources.Count >= wanted * 5 )
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
				sources.Add( c );
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
		var built = 0;
		var path = new List<int>( 1024 );
		var levels = new List<float>( 1024 );

		for ( var si = 0; si < sources.Count && built < wanted; si++ )
		{
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
							reached = true;
							joined = true;
							break;
						}

						// Lake or sea: the mouth.
						if ( water[n] || !land[n] )
						{
							path.Add( n );
							levels.Add( sea );
							endLevel = sea;
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
				// and only ever falls; the cut is what that costs through the ground at the cell itself.
				var nextLevel = Math.Min( levels[^1] - (minDrop * stepLen), h[best] - depth );
				if ( h[best] - nextLevel > maxCut )
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
			ResamplePolyline( pts, lv, cuts, smoothing, 20f );

			// Character (Mark 2026-10-08: "too straight… twists and bends, varying thickness, little pools"):
			// a seeded sinuous side-to-side offset along the arc, a slow width multiplier, and pool stretches
			// where the channel widens ~3x and the bed deepens. All before the fine-ground level pass, so the
			// carve follows the final line.
			var widthMul = new float[pts.Count];
			var depthMul = new float[pts.Count];
			ShapeRiver( pts, widthMul, depthMul, seed + (riverId * 31), halfMin, halfMax );

			// The level profile is set from the REAL fine ground along the final route (centre line and both
			// channel edges), not the 80 m routing field: box-smoothing raises valley floors, and a river routed
			// down a valley at "smoothed minus depth" sat above the true floor as a raised strip of water with
			// green slopes climbing up to it (Mark 2026-10-07). Only ever falling, depth under the lowest of the
			// three samples, never below the sea.
			if ( !SetLevelsFromFineGround( pts, lv, cuts, preRiverHeightMeters, halfMin, halfMax, widthMul, depth, minDrop, sea, maxCut * 1.6f ) )
			{
				net.Failed++;   // the fine ground (ledges, ravines) would need a gorge deeper than 1.6 x Max Cut
				continue;
			}

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
				// A deep cut spreads its bank so the side never exceeds Bank Max Slope: no canyon walls.
				var bankA = Math.Max( halfA * 2f * bankScale, cuts[i - 1] / bankTan );
				var bankB = Math.Max( halfB * 2f * bankScale, cuts[i] / bankTan );
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
			segmentCount.Add( segments.Count - firstSegment[^1] );
			built++;
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				break;
		}

		net.Segments = segments.ToArray();
		net.RiverFirstSegment = firstSegment.ToArray();
		net.RiverSegmentCount = segmentCount.ToArray();
		net.RiverCount = built;

		RasterizeSegments( net, settings );
		return net;
	}

	/// <summary>Nearest river at a world point, through the fine raster then a few exact segment distances.</summary>
	public static Hit Sample( Network net, TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		if ( net is null || net.Segments.Length == 0 )
			return default;

		if ( !TryFieldIndex( worldXMeters, worldYMeters, net.FineRes, settings.WorldRadiusMeters, settings.WorldDiameterMeters, out var idx ) )
			return default;

		var seg = net.FineSegment[idx];
		if ( seg < 0 )
			return default;

		var river = net.Segments[seg].River;
		var first = net.RiverFirstSegment[river];
		var last = first + net.RiverSegmentCount[river] - 1;
		var from = Math.Max( first, seg - 3 );
		var to = Math.Min( last, seg + 3 );

		var bestDist = float.MaxValue;
		var bestT = 0f;
		var bestIndex = seg;
		for ( var s = from; s <= to; s++ )
		{
			var d = DistanceToSegment( net.Segments[s], worldXMeters, worldYMeters, out var t );
			if ( d < bestDist )
			{
				bestDist = d;
				bestT = t;
				bestIndex = s;
			}
		}

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
		float maxFineCut )
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
			if ( i + 1 < n )
			{
				// Half way to the next vertex too: a dip between two vertices used to leave water above ground.
				var mid = (pts[i] + pts[i + 1]) * 0.5f;
				g = Math.Min( g, groundMeters( mid.x, mid.y ) );
				g = Math.Min( g, groundMeters( mid.x + (side.x * half), mid.y + (side.y * half) ) );
				g = Math.Min( g, groundMeters( mid.x - (side.x * half), mid.y - (side.y * half) ) );
			}

			var level = g - depth;
			if ( i > 0 )
				level = Math.Min( level, prevLevel - (minDrop * Vector2.Distance( pts[i - 1], pts[i] )) );
			level = Math.Max( sea, level );
			if ( centre - level > maxFineCut )
				return false;
			levels[i] = level;
			cuts[i] = Math.Max( 0f, centre - level );
			prevLevel = level;
		}

		return true;
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
	/// Bends, width variation and pools along an evenly spaced route. Offset is perpendicular to the local
	/// direction: a 2-octave FBM over the arc (~450 m swings, amplitude 1.5–3 channel widths, zero at both
	/// ends so the mouth and source stay put). Width multiplier 0.7–1.35 from a slower FBM; pools where a very
	/// slow FBM peaks (roughly every 1–2 km): width up to 3.2x, bed depth up to 2.5x.
	/// </summary>
	static void ShapeRiver( List<Vector2> pts, float[] widthMul, float[] depthMul, int seed, float halfMin, float halfMax )
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
			pts[i] = src[i] + (side * (bend * swing * half * 2f * ends));

			var w = TerrainPreviewNoise.Fbm( seed + 2, arc[i] / 520f, 0.61f, 2 );
			widthMul[i] = 0.7f + (w * 0.65f);

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
