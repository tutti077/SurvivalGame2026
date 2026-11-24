namespace Survival;

using System.Collections.Generic;

/// <summary>
/// Redwood ledge cliffs as real ground (Mark 2026-11-16: "I'd want them at more 90 degree drops or hell even sharper, so
/// there's like a little overhang" — then, of a rock mesh laid over the slope: "It needs to ACTUALLY be the terrain ... What
/// happens when a player digs that wall?? Should be normal terrain."). The heightfield is the first pass: it draws each
/// ledge as a scarp slope, which is what the far chunks show. Near the player a chunk with a cliff becomes a voxel column
/// (<see cref="TerrainWorldManager"/>, Voxel.cs) seeded from that heightfield, and <see cref="Sculpt"/> then reshapes the
/// field across every ledge line: the scarp slope is cut away and a rock section stands in its place — a vertical face,
/// a lip overhanging the drop, the cap flush with the upper bench. The result is the same voxel ground digging uses, so a
/// pickaxe chips the cliff like any other ground, the strokes replay on top of the sculpt, and collision is the ground's own.
/// <para>
/// Everything is a pure function of world position (the ledge field on a fixed 2 m world lattice, world-space noise), so
/// two neighbouring columns carve the same rock into their shared margin and meet without a seam; a column whose cliff
/// runs over a chunk border pulls that neighbour in too (<see cref="Scan"/>'s border bits).
/// </para>
/// Units: metres throughout (the field is in metres); nothing here touches engine units.
/// </summary>
public static class TerrainLedgeCliffs
{
	/// <summary><see cref="Scan"/>: the chunk holds a cliff (plus <see cref="TerrainMeshBuilder.BorderMinY"/>… bits where it runs over a border).</summary>
	public const int HasCliff = 16;

	/// <summary>Drop (m) at which the rock section starts to take over from the scarp; full at <see cref="FullDropMeters"/>.</summary>
	const float MinDropMeters = 1.5f;
	const float FullDropMeters = 3f;
	const float OverhangMinMeters = 0.4f;
	const float OverhangMaxMeters = 1.3f;
	/// <summary>How far past the scarp the cap's back edge sinks into the upper bench (m).</summary>
	const float BackRunMeters = 2.2f;
	/// <summary>The ledge lattice: world-aligned, so every column reads identical values where they overlap.</summary>
	const float LatticeMeters = 2f;
	/// <summary>How far from the line the sculpt reaches (a 6 m face's half width + the cap's back run + slack).</summary>
	const float BandReachMeters = 8f;
	/// <summary>The sculpt fades in over this distance past a river's bank (m): none in the channel or on the bank itself.</summary>
	const float RiverClearMeters = 6f;
	/// <summary>Lattice apron past the field: the bench heights are read up to ~hw + 2.2 m either side of the line.</summary>
	const float ApronMeters = 12f;

	/// <summary>
	/// Does this chunk hold a cliff, and over which borders does it run? From the chunk's own sampled grid (any LOD): a cell
	/// whose four corners are near a ledge, with the line crossing it and its corners spanning a metre or more of height. A
	/// border bit is set when such a cell lies within <see cref="BandReachMeters"/> of that border: the sculpt reaches that far
	/// from the line, so the neighbour holds part of the band and must be voxel ground too.
	/// </summary>
	public static int Scan( TerrainPreviewSample[] samples, int n, float step )
	{
		var mask = 0;
		for ( var iy = 0; iy < n - 1; iy++ )
		{
			for ( var ix = 0; ix < n - 1; ix++ )
			{
				var i0 = (iy * n) + ix;
				var a = samples[i0];
				var b = samples[i0 + 1];
				var c = samples[i0 + n];
				var d = samples[i0 + n + 1];
				if ( !a.Ledge.Present || !b.Ledge.Present || !c.Ledge.Present || !d.Ledge.Present )
					continue;
				var pos = (a.Ledge.DistanceMeters >= 0f ? 1 : 0) + (b.Ledge.DistanceMeters >= 0f ? 1 : 0)
					+ (c.Ledge.DistanceMeters >= 0f ? 1 : 0) + (d.Ledge.DistanceMeters >= 0f ? 1 : 0);
				if ( pos == 0 || pos == 4 )
					continue;
				var lo = Math.Min( Math.Min( a.HeightMeters, b.HeightMeters ), Math.Min( c.HeightMeters, d.HeightMeters ) );
				var hi = Math.Max( Math.Max( a.HeightMeters, b.HeightMeters ), Math.Max( c.HeightMeters, d.HeightMeters ) );
				if ( hi - lo < 1f )
					continue;
				mask |= HasCliff;
				var span = (n - 1) * step;
				if ( iy * step < BandReachMeters ) mask |= TerrainMeshBuilder.BorderMinY;
				if ( span - ((iy + 1) * step) < BandReachMeters ) mask |= TerrainMeshBuilder.BorderMaxY;
				if ( ix * step < BandReachMeters ) mask |= TerrainMeshBuilder.BorderMinX;
				if ( span - ((ix + 1) * step) < BandReachMeters ) mask |= TerrainMeshBuilder.BorderMaxX;
			}
		}
		return mask;
	}

	struct Node
	{
		public bool Present;
		public float Height, D, Ux, Uy, Hw;
	}

	/// <summary>
	/// Reshapes a freshly seeded field across every ledge line it holds. Worker-safe (pipeline samples only, ~2 000 of them).
	/// Per sample column within reach of a line: s = the signed distance to the line (+ uphill), the bench heights read where
	/// the line point's section needs them, then for each z: ground = max( min( seeded, −carve ), rock ), faded in by the drop
	/// and faded out toward each border in <paramref name="fadeSides"/> (a neighbour still drawn as heightfield, whose scarp
	/// the column has to meet at the shared edge).
	/// </summary>
	public static void Sculpt( VoxelField field, TerrainPreviewSettings settings, ITerrainPreviewBackend backend,
		float chunkMinX, float chunkMinY, float chunkSize, int fadeSides )
	{
		var o = field.OriginMeters;
		var maxX = o.x + (field.CellsX * field.CellMeters);
		var maxY = o.y + (field.CellsY * field.CellMeters);
		var lx0 = MathF.Floor( (o.x - ApronMeters) / LatticeMeters ) * LatticeMeters;
		var ly0 = MathF.Floor( (o.y - ApronMeters) / LatticeMeters ) * LatticeMeters;
		var nx = (int)MathF.Ceiling( (maxX + ApronMeters - lx0) / LatticeMeters ) + 1;
		var ny = (int)MathF.Ceiling( (maxY + ApronMeters - ly0) / LatticeMeters ) + 1;
		var lattice = new Node[nx * ny];
		var any = false;
		for ( var j = 0; j < ny; j++ )
		{
			for ( var i = 0; i < nx; i++ )
			{
				var s = backend.Sample( settings, lx0 + (i * LatticeMeters), ly0 + (j * LatticeMeters) );
				var info = s.Ledge;
				lattice[(j * nx) + i] = new Node
				{
					Present = info.Present,
					Height = s.IsInsideWorld ? s.HeightMeters : 0f,
					D = info.DistanceMeters,
					Ux = info.DirectionX,
					Uy = info.DirectionY,
					Hw = info.HalfScarpWidthMeters,
				};
				any |= info.Present;
			}
		}
		if ( !any )
			return;

		float Height( Vector2 q )
		{
			var fx = Math.Clamp( (q.x - lx0) / LatticeMeters, 0f, nx - 1.001f );
			var fy = Math.Clamp( (q.y - ly0) / LatticeMeters, 0f, ny - 1.001f );
			var i = (int)fx;
			var j = (int)fy;
			var tx = fx - i;
			var ty = fy - j;
			var h00 = lattice[(j * nx) + i].Height;
			var h10 = lattice[(j * nx) + i + 1].Height;
			var h01 = lattice[((j + 1) * nx) + i].Height;
			var h11 = lattice[((j + 1) * nx) + i + 1].Height;
			return Lerp( Lerp( h00, h10, tx ), Lerp( h01, h11, tx ), ty );
		}

		field.RewriteColumns( ( x, y ) =>
		{
			// The ledge at this column: bilinear over the lattice cell, all four corners near the same line
			var fx = (x - lx0) / LatticeMeters;
			var fy = (y - ly0) / LatticeMeters;
			var i = (int)MathF.Floor( fx );
			var j = (int)MathF.Floor( fy );
			if ( i < 0 || j < 0 || i >= nx - 1 || j >= ny - 1 )
				return null;
			var n00 = lattice[(j * nx) + i];
			var n10 = lattice[(j * nx) + i + 1];
			var n01 = lattice[((j + 1) * nx) + i];
			var n11 = lattice[((j + 1) * nx) + i + 1];
			if ( !n00.Present || !n10.Present || !n01.Present || !n11.Present )
				return null;
			var dmin = Math.Min( Math.Min( n00.D, n10.D ), Math.Min( n01.D, n11.D ) );
			var dmax = Math.Max( Math.Max( n00.D, n10.D ), Math.Max( n01.D, n11.D ) );
			if ( dmax - dmin > 3f * LatticeMeters )
				return null;   // the terrace wrap between two lines, not a line
			var tx = fx - i;
			var ty = fy - j;
			float Bi( float a, float b, float c, float d ) => Lerp( Lerp( a, b, tx ), Lerp( c, d, tx ), ty );
			var s = Bi( n00.D, n10.D, n01.D, n11.D );
			var hw = Math.Max( 0.5f, Bi( n00.Hw, n10.Hw, n01.Hw, n11.Hw ) );
			if ( s < -hw - 3f || s > hw + BackRunMeters + 0.5f )
				return null;
			var up = new Vector2( Bi( n00.Ux, n10.Ux, n01.Ux, n11.Ux ), Bi( n00.Uy, n10.Uy, n01.Uy, n11.Uy ) );
			if ( up.LengthSquared < 0.25f )
				return null;
			up = up.Normal;

			// The section belongs to the line point, so every column across it shares one set of bench heights
			var line = new Vector2( x, y ) - (up * s);
			var top = Height( line + (up * (hw + 1f)) );
			var back = Height( line + (up * (hw + BackRunMeters)) );
			var foot = Height( line - (up * (hw + 0.8f)) );
			var drop = top - foot;
			var weight = SmoothStep01( Math.Clamp( (drop - MinDropMeters) / (FullDropMeters - MinDropMeters), 0f, 1f ) )
				* BorderFade( x, y, chunkMinX, chunkMinY, chunkSize, fadeSides );
			if ( weight <= 0f )
				return null;
			// No cliff in a river: the channel, its banks and RiverClearMeters beyond stay the ground the river carve made.
			// A ledge line that ran across a river stood its rock section in the water - teeth of cliff cutting into the
			// channel from the bank (Mark 2026-11-18: "redwood cliffs cutting into rivers").
			var river = TerrainPreviewLandDiskFields.SampleRiver( settings, x, y );
			if ( river.Near )
				weight *= SmoothStep01( Math.Clamp( (river.Distance - river.HalfWidth - river.BankWidth) / RiverClearMeters, 0f, 1f ) );
			var partner = TerrainPreviewLandDiskFields.SampleRiverPartner( settings, x, y, river );
			if ( partner.Near )
				weight *= SmoothStep01( Math.Clamp( (partner.Distance - partner.HalfWidth - partner.BankWidth) / RiverClearMeters, 0f, 1f ) );
			if ( weight <= 0f )
				return null;

			var section = new Vector2[11];   // its own copy: the rewrite runs after this returns (Array.Clone is not whitelisted)
			Section( section, line, hw, back, top, foot, drop );
			var carveLo = -hw - 0.9f;
			var carveHi = hw + 0.6f;
			// The cliff's body is rock up to just under the cap (no soil on a cut into it, stone to the pickaxe), from the lip's
			// front edge back over the old scarp. Faded zones keep less of it.
			var rockFloor = s >= -hw - OverhangMaxMeters - 0.2f && s <= carveHi ? Lerp( foot, top - 0.6f, weight ) : float.NegativeInfinity;
			return new VoxelField.ColumnRewrite( ( f, z ) =>
			{
				var p = new Vector2( s, z );
				var rock = -SignedDistance( section, p );                    // + inside the rock
				var carve = CarveDistance( s, z, carveLo, carveHi, foot );   // + inside the cut-away scarp
				var shaped = Math.Max( Math.Min( f, -carve ), rock );
				return Lerp( f, shaped, weight );
			}, rockFloor );
		} );
	}

	/// <summary>
	/// The rock's cross-section, (s, z): s the ground distance uphill from the line, z absolute metres. From the cap's buried
	/// back over the lip, down the face to the toe and the buried foot, closed 30 m down. The scarp the heightfield drew runs
	/// from (−hw, foot) to (+hw, top); it is cut away (<see cref="CarveDistance"/>) and this stands in front of where it was.
	/// </summary>
	static void Section( Vector2[] poly, Vector2 line, float hw, float back, float top, float foot, float drop )
	{
		// Per line point variation from world-space noise (the same in every column that reads this point)
		var lean = TerrainPreviewNoise.Fbm( 7411, line.x / 9f, line.y / 9f, 2 );
		var j1 = (TerrainPreviewNoise.Fbm( 7412, line.x / 5f, line.y / 5f, 1 ) - 0.5f) * 2f * 0.5f;
		var j2 = (TerrainPreviewNoise.Fbm( 7413, line.x / 5f, line.y / 5f, 1 ) - 0.5f) * 2f * 0.5f;
		var overhang = Lerp( OverhangMinMeters, OverhangMaxMeters, Math.Clamp( (lean - 0.3f) / 0.4f, 0f, 1f ) )
			* Math.Clamp( drop / 8f, 0.5f, 1f );
		var footS = -hw;
		poly[0] = new( hw + BackRunMeters, back - 0.7f );           // cap back, buried in the upper bench
		poly[1] = new( hw + 0.6f, top + 0.15f );                    // over the old scarp's lip
		poly[2] = new( footS + 1.2f, top + 0.3f );                  // cap top
		poly[3] = new( footS - overhang, top + 0.12f );             // lip edge
		poly[4] = new( footS - overhang + 0.15f, top - 0.5f );      // lip underside (its thickness)
		poly[5] = new( footS + 0.1f + j1, foot + (0.7f * drop) );  // face tucked back under the lip
		poly[6] = new( footS - 0.1f + j2, foot + (0.3f * drop) );  // lower face, plumb
		poly[7] = new( footS - 0.4f, foot + 0.3f );                 // scree toe
		poly[8] = new( footS - 0.9f, foot - 1.3f );                 // foot, buried in the lower bench
		poly[9] = new( footS - 0.9f, foot - 30f );
		poly[10] = new( hw + BackRunMeters, back - 30f );
	}

	/// <summary>Over <see cref="BorderFadeMeters"/> before a faded border the carve gives way to the seeded scarp (none within 1.5 m of the edge, nor in the margin past it).</summary>
	static float BorderFade( float x, float y, float minX, float minY, float size, int sides )
	{
		var w = 1f;
		if ( (sides & TerrainMeshBuilder.BorderMinY) != 0 ) w *= Ramp( y - minY );
		if ( (sides & TerrainMeshBuilder.BorderMaxY) != 0 ) w *= Ramp( minY + size - y );
		if ( (sides & TerrainMeshBuilder.BorderMinX) != 0 ) w *= Ramp( x - minX );
		if ( (sides & TerrainMeshBuilder.BorderMaxX) != 0 ) w *= Ramp( minX + size - x );
		return w;

		// Untouched for the last 1.5 m, so the column's edge is the seeded heightfield exactly where it meets the real one
		static float Ramp( float inside ) => SmoothStep01( Math.Clamp( (inside - 1.5f) / BorderFadeMeters, 0f, 1f ) );
	}

	const float BorderFadeMeters = 6f;

	/// <summary>Signed distance to a closed polygon, negative inside (Quilez).</summary>
	static float SignedDistance( Vector2[] v, Vector2 p )
	{
		var d = Vector2.Dot( p - v[0], p - v[0] );
		var sign = 1f;
		for ( int i = 0, j = v.Length - 1; i < v.Length; j = i, i++ )
		{
			var e = v[j] - v[i];
			var w = p - v[i];
			var b = w - (e * Math.Clamp( Vector2.Dot( w, e ) / Vector2.Dot( e, e ), 0f, 1f ));
			d = Math.Min( d, Vector2.Dot( b, b ) );
			var c1 = p.y >= v[i].y;
			var c2 = p.y < v[j].y;
			var c3 = (e.x * w.y) > (e.y * w.x);
			if ( (c1 && c2 && c3) || (!c1 && !c2 && !c3) )
				sign = -sign;
		}
		return sign * MathF.Sqrt( d );
	}

	/// <summary>Signed distance to the cut-away band (s in [lo, hi], z above the foot), positive inside.</summary>
	static float CarveDistance( float s, float z, float lo, float hi, float foot )
	{
		var inside = Math.Min( Math.Min( s - lo, hi - s ), z - foot );
		if ( inside >= 0f )
			return inside;
		var dx = Math.Max( Math.Max( lo - s, s - hi ), 0f );
		var dz = Math.Max( foot - z, 0f );
		return -MathF.Sqrt( (dx * dx) + (dz * dz) );
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t);
	static float SmoothStep01( float t ) => t * t * (3f - (2f * t));
}
