namespace Survival;

/// <summary>
/// True distance to the lake shoreline (the contour where the bilinear lake mask crosses the open-water threshold), as a
/// signed field: metres from the shore, positive on dry land, negative inside a lake. Built once with the lake mask.
/// <para>
/// Before this (Mark 2026-10-25: "I still have the elevation water square bugs"), distance to the shore was the mask's
/// shortfall over its gradient. Where the mask lies almost flat right at the threshold (wide borderline areas, several
/// near the ocean rim) that ratio collapsed to 0 over whole patches and jumped to tens of metres a few metres away:
/// land drained to water level in flat patches walled by 7 m cuts. A distance to the traced contour is continuous
/// everywhere, so nothing it shapes can step.
/// </para>
/// The contour is traced by marching squares on every mask cell that straddles the threshold, subdivided 8× so the
/// segments follow the bilinear mask; distances are exact point-to-segment on a 16 m grid (seeded beside the
/// segments, then swept outward carrying the nearest segment), read back bilinearly.
/// <para>
/// Only REAL open water counts — mask cells next to the speck-filtered open-water raster. A speck where the mask just
/// pokes over the threshold is not a lake: traced, it drained a dry pit round itself and its sliver became lake floor
/// with land standing over it (Mark 2026-10-25: "a massive hole in the earth", a V-shaped cut in a meadow). The sign
/// follows the same rule, and the pipeline asks the sign whether a point is under water, so the carve, the shore bank
/// and the water surface all agree on where a lake is.
/// </para>
/// </summary>
public static class TerrainPreviewLakeShoreDistance
{
	const float GridMeters = 16f;
	const int Subdivide = 8;
	/// <summary>Distances are stored up to this; anything farther reads as this.</summary>
	public const float CapMeters = 2000f;

	static float[] _signed = Array.Empty<float>();
	static int _nodes;
	static float _radius;

	/// <summary>Signed metres to the lake shore (+ dry, − in a lake); <see cref="CapMeters"/> when there is no field.</summary>
	public static float SignedMeters( float worldXMeters, float worldYMeters )
	{
		var field = _signed;
		var n = _nodes;
		if ( n < 2 || field.Length != n * n )
			return CapMeters;
		var fx = (worldXMeters + _radius) / GridMeters;
		var fy = (worldYMeters + _radius) / GridMeters;
		if ( fx < 0f || fy < 0f || fx >= n - 1 || fy >= n - 1 )
			return CapMeters;
		var x0 = (int)fx;
		var y0 = (int)fy;
		var tx = fx - x0;
		var ty = fy - y0;
		var i = (y0 * n) + x0;
		var a = field[i] + ((field[i + 1] - field[i]) * tx);
		var b = field[i + n] + ((field[i + n + 1] - field[i + n]) * tx);
		return a + ((b - a) * ty);
	}

	/// <summary>The field (nodes², 16 m spacing from (−radius, −radius), +x not mirrored) for the sea surface, which reads the same distance on the GPU.</summary>
	public static bool TryGetField( out float[] field, out int nodes )
	{
		field = _signed;
		nodes = _nodes;
		return nodes > 1 && field.Length == nodes * nodes;
	}

	/// <summary>Trace the shoreline in <paramref name="mask"/> (res², x mirrored like the land disk rasters) and build the field.</summary>
	internal static void Build( float[] mask, bool[] openWater, int res, float radius, float diameter, float threshold )
	{
		var segs = TraceContour( mask, openWater, res, radius, diameter, threshold );
		var n = (int)MathF.Ceiling( diameter / GridMeters ) + 1;
		var dist = new float[n * n];
		var nearest = new int[n * n];
		Array.Fill( dist, CapMeters );
		Array.Fill( nearest, -1 );

		// Seed: every node within a grid step of a segment gets its exact distance
		for ( var s = 0; s < segs.Count; s++ )
		{
			var (ax, ay, bx, by) = segs[s];
			var i0 = Math.Max( 0, (int)MathF.Floor( (Math.Min( ax, bx ) + radius) / GridMeters ) - 1 );
			var i1 = Math.Min( n - 1, (int)MathF.Ceiling( (Math.Max( ax, bx ) + radius) / GridMeters ) + 1 );
			var j0 = Math.Max( 0, (int)MathF.Floor( (Math.Min( ay, by ) + radius) / GridMeters ) - 1 );
			var j1 = Math.Min( n - 1, (int)MathF.Ceiling( (Math.Max( ay, by ) + radius) / GridMeters ) + 1 );
			for ( var j = j0; j <= j1; j++ )
			for ( var i = i0; i <= i1; i++ )
			{
				var d = SegmentDistance( (i * GridMeters) - radius, (j * GridMeters) - radius, ax, ay, bx, by );
				var k = (j * n) + i;
				if ( d < dist[k] )
				{
					dist[k] = d;
					nearest[k] = s;
				}
			}
		}

		// Sweep: each node tries its already-visited neighbours' nearest segments (forward, then backward, twice)
		for ( var pass = 0; pass < 4; pass++ )
		{
			var forward = (pass & 1) == 0;
			for ( var jj = 0; jj < n; jj++ )
			{
				var j = forward ? jj : n - 1 - jj;
				for ( var ii = 0; ii < n; ii++ )
				{
					var i = forward ? ii : n - 1 - ii;
					var k = (j * n) + i;
					var sx = forward ? -1 : 1;
					Try( i + sx, j );
					Try( i + sx, j + sx );
					Try( i, j + sx );
					Try( i - sx, j + sx );

					void Try( int ni, int nj )
					{
						if ( ni < 0 || nj < 0 || ni >= n || nj >= n )
							return;
						var s = nearest[(nj * n) + ni];
						if ( s < 0 || s == nearest[k] )
							return;
						var (ax, ay, bx, by) = segs[s];
						var d = SegmentDistance( (i * GridMeters) - radius, (j * GridMeters) - radius, ax, ay, bx, by );
						if ( d < dist[k] )
						{
							dist[k] = Math.Min( d, CapMeters );
							nearest[k] = s;
						}
					}
				}
			}
		}

		// Sign: inside a lake where the mask is over the threshold at the node
		for ( var j = 0; j < n; j++ )
		for ( var i = 0; i < n; i++ )
		{
			var x = (i * GridMeters) - radius;
			var y = (j * GridMeters) - radius;
			if ( MaskAt( mask, res, radius, diameter, x, y ) > threshold && NearOpenWater( openWater, res, radius, diameter, x, y ) )
				dist[(j * n) + i] = -dist[(j * n) + i];
		}

		_radius = radius;
		_nodes = n;
		_signed = dist;
		Log.Info( $"[Terrain] lake shore distance: {segs.Count} shoreline segments, {n}×{n} field at {GridMeters:0} m" );
	}

	static List<(float Ax, float Ay, float Bx, float By)> TraceContour( float[] mask, bool[] openWater, int res, float radius, float diameter, float threshold )
	{
		var segs = new List<(float, float, float, float)>();
		var sub = new float[Subdivide + 1, Subdivide + 1];
		for ( var py = 0; py < res - 1; py++ )
		for ( var px = 0; px < res - 1; px++ )
		{
			var m00 = mask[(py * res) + px] - threshold;
			var m10 = mask[(py * res) + px + 1] - threshold;
			var m01 = mask[((py + 1) * res) + px] - threshold;
			var m11 = mask[((py + 1) * res) + px + 1] - threshold;
			var lo = Math.Min( Math.Min( m00, m10 ), Math.Min( m01, m11 ) );
			var hi = Math.Max( Math.Max( m00, m10 ), Math.Max( m01, m11 ) );
			if ( lo > 0f || hi <= 0f || !OpenWaterAround( openWater, res, px, py ) )
				continue;
			for ( var j = 0; j <= Subdivide; j++ )
			for ( var i = 0; i <= Subdivide; i++ )
			{
				var tx = i / (float)Subdivide;
				var ty = j / (float)Subdivide;
				var a = m00 + ((m10 - m00) * tx);
				var b = m01 + ((m11 - m01) * tx);
				sub[i, j] = a + ((b - a) * ty);
			}
			for ( var j = 0; j < Subdivide; j++ )
			for ( var i = 0; i < Subdivide; i++ )
				MarchCell( i, j );

			void MarchCell( int i, int j )
			{
				var v0 = sub[i, j];
				var v1 = sub[i + 1, j];
				var v2 = sub[i + 1, j + 1];
				var v3 = sub[i, j + 1];
				var code = (v0 > 0f ? 1 : 0) | (v1 > 0f ? 2 : 0) | (v2 > 0f ? 4 : 0) | (v3 > 0f ? 8 : 0);
				if ( code == 0 || code == 15 )
					return;
				// Edge crossings in raster units: e0 bottom (v0-v1), e1 right (v1-v2), e2 top (v3-v2), e3 left (v0-v3)
				Vector2 E( int e )
				{
					float fa, fb;
					Vector2 pa, pb;
					switch ( e )
					{
						case 0: fa = v0; fb = v1; pa = new Vector2( i, j ); pb = new Vector2( i + 1, j ); break;
						case 1: fa = v1; fb = v2; pa = new Vector2( i + 1, j ); pb = new Vector2( i + 1, j + 1 ); break;
						case 2: fa = v3; fb = v2; pa = new Vector2( i, j + 1 ); pb = new Vector2( i + 1, j + 1 ); break;
						default: fa = v0; fb = v3; pa = new Vector2( i, j ); pb = new Vector2( i, j + 1 ); break;
					}
					var t = Math.Clamp( fa / (fa - fb), 0f, 1f );
					var p = pa + ((pb - pa) * t);
					return new Vector2( px + (p.x / Subdivide), py + (p.y / Subdivide) );
				}
				void Add( int ea, int eb )
				{
					var a = RasterToWorld( E( ea ), res, radius, diameter );
					var b = RasterToWorld( E( eb ), res, radius, diameter );
					segs.Add( (a.x, a.y, b.x, b.y) );
				}
				var centreIn = (v0 + v1 + v2 + v3) > 0f;
				switch ( code )
				{
					case 1: case 14: Add( 0, 3 ); break;
					case 2: case 13: Add( 0, 1 ); break;
					case 3: case 12: Add( 1, 3 ); break;
					case 4: case 11: Add( 1, 2 ); break;
					case 6: case 9: Add( 0, 2 ); break;
					case 7: case 8: Add( 2, 3 ); break;
					case 5:
						if ( centreIn ) { Add( 0, 1 ); Add( 2, 3 ); } else { Add( 0, 3 ); Add( 1, 2 ); }
						break;
					case 10:
						if ( centreIn ) { Add( 0, 3 ); Add( 1, 2 ); } else { Add( 0, 1 ); Add( 2, 3 ); }
						break;
				}
			}
		}
		return segs;
	}

	/// <summary>Any speck-filtered open water in the raster cells around cell (px, py) — 4×4 nodes, the cell and its ring.</summary>
	static bool OpenWaterAround( bool[] openWater, int res, int px, int py )
	{
		for ( var y = Math.Max( 0, py - 1 ); y <= Math.Min( res - 1, py + 2 ); y++ )
		for ( var x = Math.Max( 0, px - 1 ); x <= Math.Min( res - 1, px + 2 ); x++ )
		{
			if ( openWater[(y * res) + x] )
				return true;
		}
		return false;
	}

	static bool NearOpenWater( bool[] openWater, int res, float radius, float diameter, float x, float y )
	{
		var fx = (res - 1) - ((((x + radius) / diameter) * res) - 0.5f);
		var fy = (((y + radius) / diameter) * res) - 0.5f;
		return OpenWaterAround( openWater, res, (int)MathF.Floor( fx ), (int)MathF.Floor( fy ) );
	}

	/// <summary>Raster float coordinates (x mirrored, see TerrainPreviewLandDiskFields.WorldMetersToRasterFloat) to world metres.</summary>
	static Vector2 RasterToWorld( Vector2 r, int res, float radius, float diameter )
		=> new( (((res - 0.5f - r.x) / res) * diameter) - radius, (((r.y + 0.5f) / res) * diameter) - radius );

	static float MaskAt( float[] mask, int res, float radius, float diameter, float x, float y )
	{
		var fx = (res - 1) - ((((x + radius) / diameter) * res) - 0.5f);
		var fy = (((y + radius) / diameter) * res) - 0.5f;
		var x0 = (int)MathF.Floor( fx );
		var y0 = (int)MathF.Floor( fy );
		float At( int px, int py ) => px < 0 || py < 0 || px >= res || py >= res ? 0f : mask[(py * res) + px];
		var tx = fx - x0;
		var ty = fy - y0;
		var a = At( x0, y0 ) + ((At( x0 + 1, y0 ) - At( x0, y0 )) * tx);
		var b = At( x0, y0 + 1 ) + ((At( x0 + 1, y0 + 1 ) - At( x0, y0 + 1 )) * tx);
		return a + ((b - a) * ty);
	}

	static float SegmentDistance( float x, float y, float ax, float ay, float bx, float by )
	{
		var dx = bx - ax;
		var dy = by - ay;
		var len2 = (dx * dx) + (dy * dy);
		var t = len2 > 1e-9f ? Math.Clamp( (((x - ax) * dx) + ((y - ay) * dy)) / len2, 0f, 1f ) : 0f;
		var ex = x - (ax + (dx * t));
		var ey = y - (ay + (dy * t));
		return MathF.Sqrt( (ex * ex) + (ey * ey) );
	}
}
