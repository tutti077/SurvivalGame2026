namespace Survival;

/// <summary>
/// Where the ocean swell has travelled to (Mark 2026-10-26: the shore bend was a lerp of two wave phases, which is not a
/// wave — it drew slow wide bands across the whole shelf). The swell's phase is now k × S(p) − ωt, where S is ONE
/// continuous travel distance from a wavefront far upwind, marched over the sea floor with the shallow-water refractive
/// index n(d) = c_deep / c(d): over the shelf the rays slow and turn toward the beach by themselves, crests stay one
/// unbroken line, no spurious wavenumber appears, and nothing is blended. In deep water S = wind · p exactly (plane waves).
/// <para>
/// Built once per world and swell heading on a worker thread (fast marching, 40 m nodes, the land radius + a margin
/// past the drop-off), uploaded as one RGBA32F texture (<c>SeaSwellField</c>) and sampled on the CPU by
/// <see cref="SeaWaves"/> with the same bilinear read. Channels: R = S − wind·p (m, the refraction detour),
/// GB = ∇S (the travel direction scaled by n), A = exposure 0…1 (1 where the swell arrives straight, falling with the
/// detour a ray made to reach the lee of the island). Lakes and inland cells read as plane waves (R 0, GB wind, A 1).
/// </para>
/// </summary>
public static class SeaSwellTravelField
{
	public const float NodeMeters = 40f;
	/// <summary>The field reaches this far past the land edge; beyond it the shader and the CPU use the plane wave.</summary>
	public const float MarginMeters = 1800f;
	/// <summary>The wavelength the refraction is marched for (m): the default-weather peak sits here. Every swell shares its rays.</summary>
	public const float ReferenceWavelengthMeters = 90f;
	/// <summary>Cells past the waterline that still get a marched value (the run-up strip reads them).</summary>
	const int LandReachCells = 4;
	/// <summary>Detour (m) that halves the exposure: around the back of the island the sea is calm, not dead.</summary>
	const float ShadowHalfMeters = 2500f;
	const float LandIndex = 8f;
	/// <summary>Bump when the march changes, so a cached field is rebuilt.</summary>
	const int FieldVersion = 2;

	/// <summary>One built field (immutable once published).</summary>
	public sealed class Field
	{
		public int Nodes { get; init; }
		public float HalfSizeMeters { get; init; }
		public Vector2 WindDirection { get; init; }
		/// <summary>Nodes² × 4 floats: detour, grad x, grad y, exposure.</summary>
		public float[] Data { get; init; }
		public int Fingerprint { get; init; }
	}

	static Field _field;
	static int _buildingFingerprint = int.MinValue;

	public static Field Current => _field;

	/// <summary>The travel data at a world point: detour (m), ∇S (n × unit travel direction), exposure 0…1. Plane values without a field.</summary>
	public static (float Detour, Vector2 Grad, float Exposure) Sample( Field field, Vector2 windDirection, float xMeters, float yMeters )
	{
		if ( field is null )
			return (0f, windDirection, 1f);
		var n = field.Nodes;
		var fx = (xMeters + field.HalfSizeMeters) / NodeMeters;
		var fy = (yMeters + field.HalfSizeMeters) / NodeMeters;
		if ( fx < 0f || fy < 0f || fx >= n - 1 || fy >= n - 1 )
			return (0f, field.WindDirection, 1f);
		var x0 = (int)fx;
		var y0 = (int)fy;
		var tx = fx - x0;
		var ty = fy - y0;
		var d = field.Data;
		var i00 = ((y0 * n) + x0) * 4;
		var i10 = i00 + 4;
		var i01 = i00 + (n * 4);
		var i11 = i01 + 4;
		float L( int c )
		{
			var a = d[i00 + c] + ((d[i10 + c] - d[i00 + c]) * tx);
			var b = d[i01 + c] + ((d[i11 + c] - d[i01 + c]) * tx);
			return a + ((b - a) * ty);
		}
		return (L( 0 ), new Vector2( L( 1 ), L( 2 ) ), L( 3 ));
	}

	/// <summary>Fingerprint of what the field depends on: the carve's ocean shape and the swell heading (to a degree).</summary>
	public static int FingerprintFor( TerrainPreviewSettings settings, Vector2 windDirection )
	{
		var yaw = MathF.Round( MathF.Atan2( windDirection.y, windDirection.x ) * 180f / MathF.PI );
		return HashCode.Combine( FieldVersion, settings.LandRadiusMeters, settings.OceanShelfDepthMeters, settings.OceanShelfWidthMeters, settings.OceanMaxDepthMeters, settings.OceanDropOffWidthMeters, yaw );
	}

	/// <summary>
	/// Start building the field for this world and heading on a worker thread if it is not built or being built already.
	/// Returns true once the published field matches.
	/// </summary>
	public static bool EnsureBuilding( TerrainPreviewSettings settings, Vector2 windDirection )
	{
		var fingerprint = FingerprintFor( settings, windDirection );
		if ( _field is { } f && f.Fingerprint == fingerprint )
			return true;
		if ( _buildingFingerprint == fingerprint )
			return false;
		_buildingFingerprint = fingerprint;
		var dir = windDirection.LengthSquared > 1e-6f ? windDirection.Normal : new Vector2( 1f, 0f );
		var land = settings.LandRadiusMeters;
		var shelfDepth = Math.Max( 0f, settings.OceanShelfDepthMeters );
		var shelfWidth = Math.Max( 1f, settings.OceanShelfWidthMeters );
		var maxDepth = Math.Max( shelfDepth, settings.OceanMaxDepthMeters );
		var dropWidth = Math.Max( 1f, settings.OceanDropOffWidthMeters );
		_ = GameTask.RunInThreadAsync( () =>
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var built = Build( land, shelfDepth, shelfWidth, maxDepth, dropWidth, dir, fingerprint );
			_field = built;
			Log.Info( $"[Sea] swell travel field {built.Nodes}² nodes ({NodeMeters} m) for heading {MathF.Atan2( dir.y, dir.x ) * 180f / MathF.PI:0}° built in {sw.ElapsedMilliseconds} ms" );
		} );
		return false;
	}

	public static void Reset()
	{
		_field = null;
		_buildingFingerprint = int.MinValue;
	}

	static float OceanDepth( float past, float shelfDepth, float shelfWidth, float maxDepth, float dropWidth )
	{
		if ( past <= 0f )
			return 0f;
		var t = Math.Clamp( past / shelfWidth, 0f, 1f );
		var d = Math.Clamp( (past - shelfWidth) / dropWidth, 0f, 1f );
		return (shelfDepth * t * (2f - t)) + ((maxDepth - shelfDepth) * d * d * (3f - (2f * d)));
	}

	/// <summary>Refractive index of the reference swell over this depth: c_deep / c(d), c = sqrt( g/k · tanh( k d ) ).</summary>
	static float IndexAt( float depth )
	{
		if ( depth <= 0f )
			return LandIndex;
		var k = MathF.Tau / ReferenceWavelengthMeters;
		var ratio = MathF.Tanh( k * Math.Max( depth, 0.05f ) );
		return Math.Min( LandIndex, 1f / MathF.Sqrt( Math.Max( ratio, 1e-4f ) ) );
	}

	/// <summary>First-order fast marching of |∇S| = n(d) from the upwind boundary (where S = wind · p) inward.</summary>
	static Field Build( float landRadius, float shelfDepth, float shelfWidth, float maxDepth, float dropWidth, Vector2 dir, int fingerprint )
	{
		var half = landRadius + MarginMeters;
		var n = (int)MathF.Ceiling( (half * 2f) / NodeMeters ) + 1;
		half = (n - 1) * NodeMeters * 0.5f;
		var count = n * n;
		var index = new float[count];
		var S = new float[count];
		var state = new byte[count];   // 0 far, 1 trial, 2 known
		var landDist = new int[count]; // cells from the water, land only
		var h = NodeMeters;

		for ( var y = 0; y < n; y++ )
		{
			for ( var x = 0; x < n; x++ )
			{
				var px = (x * h) - half;
				var py = (y * h) - half;
				var r = MathF.Sqrt( (px * px) + (py * py) );
				var depth = OceanDepth( r - landRadius, shelfDepth, shelfWidth, maxDepth, dropWidth );
				var i = (y * n) + x;
				index[i] = IndexAt( depth );
				S[i] = float.PositiveInfinity;
				landDist[i] = depth > 0f ? 0 : int.MaxValue;
			}
		}
		// How many cells inland each land cell is (BFS from the water), so the march stops a few cells past the beach
		var queue = new Queue<int>();
		for ( var i = 0; i < count; i++ )
		{
			if ( landDist[i] != 0 )
				continue;
			var x = i % n;
			var y = i / n;
			if ( (x > 0 && landDist[i - 1] == int.MaxValue) || (x < n - 1 && landDist[i + 1] == int.MaxValue) || (y > 0 && landDist[i - n] == int.MaxValue) || (y < n - 1 && landDist[i + n] == int.MaxValue) )
				queue.Enqueue( i );
		}
		while ( queue.Count > 0 )
		{
			var i = queue.Dequeue();
			var x = i % n;
			var y = i / n;
			var next = landDist[i] + 1;
			if ( next > LandReachCells )
				continue;
			void Visit( int j )
			{
				if ( landDist[j] > next )
				{
					landDist[j] = next;
					queue.Enqueue( j );
				}
			}
			if ( x > 0 ) Visit( i - 1 );
			if ( x < n - 1 ) Visit( i + 1 );
			if ( y > 0 ) Visit( i - n );
			if ( y < n - 1 ) Visit( i + n );
		}

		var heap = new MinHeap( count / 8 );
		// Seeds: the upwind half of the outer ring, where the wave is still the plane wave (deep water, n = 1)
		for ( var y = 0; y < n; y++ )
		{
			for ( var x = 0; x < n; x++ )
			{
				if ( x != 0 && y != 0 && x != n - 1 && y != n - 1 )
					continue;
				var px = (x * h) - half;
				var py = (y * h) - half;
				var outward = new Vector2( px, py ).Normal;
				if ( (outward.x * dir.x) + (outward.y * dir.y) > 0.05f )
					continue;
				var i = (y * n) + x;
				S[i] = (dir.x * px) + (dir.y * py);
				state[i] = 2;
				heap.Push( S[i], i );
			}
		}
		// Known seeds push their neighbours as trials
		var seeds = heap.Count;
		var seedList = new int[seeds];
		for ( var s = 0; s < seeds; s++ )
			seedList[s] = heap.Pop().Index;
		foreach ( var i in seedList )
			UpdateNeighbours( i, n, h, index, S, state, landDist, heap );

		while ( heap.Count > 0 )
		{
			var (value, i) = heap.Pop();
			if ( state[i] == 2 || value > S[i] )
				continue;
			state[i] = 2;
			UpdateNeighbours( i, n, h, index, S, state, landDist, heap );
		}

		var data = new float[count * 4];
		for ( var y = 0; y < n; y++ )
		{
			for ( var x = 0; x < n; x++ )
			{
				var i = (y * n) + x;
				var px = (x * h) - half;
				var py = (y * h) - half;
				var plane = (dir.x * px) + (dir.y * py);
				var o = i * 4;
				if ( float.IsInfinity( S[i] ) )
				{
					data[o] = 0f;
					data[o + 1] = dir.x;
					data[o + 2] = dir.y;
					data[o + 3] = 1f;
					continue;
				}
				float Sat( int j ) => float.IsInfinity( S[j] ) ? (dir.x * (((j % n) * h) - half)) + (dir.y * (((j / n) * h) - half)) : S[j];
				var gx = x > 0 && x < n - 1 ? (Sat( i + 1 ) - Sat( i - 1 )) / (2f * h) : dir.x;
				var gy = y > 0 && y < n - 1 ? (Sat( i + n ) - Sat( i - n )) / (2f * h) : dir.y;
				// The exposure is the water's: the march across the run-up strip (index 8) is not a detour the wave made
				var landMarch = landDist[i] > 0 && landDist[i] <= LandReachCells ? landDist[i] * LandIndex * h : 0f;
				var detour = Math.Max( 0f, S[i] - plane - landMarch );
				data[o] = S[i] - plane;
				data[o + 1] = gx;
				data[o + 2] = gy;
				data[o + 3] = MathF.Exp( -detour / ShadowHalfMeters * 0.6931f );
			}
		}
		return new Field { Nodes = n, HalfSizeMeters = half, WindDirection = dir, Data = data, Fingerprint = fingerprint };
	}

	static void UpdateNeighbours( int i, int n, float h, float[] index, float[] S, byte[] state, int[] landDist, MinHeap heap )
	{
		var x = i % n;
		var y = i / n;
		if ( x > 0 ) Update( i - 1, n, h, index, S, state, landDist, heap );
		if ( x < n - 1 ) Update( i + 1, n, h, index, S, state, landDist, heap );
		if ( y > 0 ) Update( i - n, n, h, index, S, state, landDist, heap );
		if ( y < n - 1 ) Update( i + n, n, h, index, S, state, landDist, heap );
	}

	static void Update( int j, int n, float h, float[] index, float[] S, byte[] state, int[] landDist, MinHeap heap )
	{
		if ( state[j] == 2 || landDist[j] > LandReachCells )
			return;
		var x = j % n;
		var y = j / n;
		var a = float.PositiveInfinity;
		var b = float.PositiveInfinity;
		if ( x > 0 && state[j - 1] == 2 ) a = Math.Min( a, S[j - 1] );
		if ( x < n - 1 && state[j + 1] == 2 ) a = Math.Min( a, S[j + 1] );
		if ( y > 0 && state[j - n] == 2 ) b = Math.Min( b, S[j - n] );
		if ( y < n - 1 && state[j + n] == 2 ) b = Math.Min( b, S[j + n] );
		var nh = index[j] * h;
		float t;
		if ( float.IsInfinity( a ) ) t = b + nh;
		else if ( float.IsInfinity( b ) ) t = a + nh;
		else if ( MathF.Abs( a - b ) >= nh ) t = Math.Min( a, b ) + nh;
		else
		{
			var diff = a - b;
			t = 0.5f * (a + b + MathF.Sqrt( (2f * nh * nh) - (diff * diff) ));
		}
		if ( t < S[j] )
		{
			S[j] = t;
			state[j] = 1;
			heap.Push( t, j );
		}
	}

	/// <summary>Binary min-heap of (value, index) with lazy deletion.</summary>
	sealed class MinHeap
	{
		float[] _values;
		int[] _indices;
		public int Count { get; private set; }

		public MinHeap( int capacity )
		{
			_values = new float[Math.Max( 16, capacity )];
			_indices = new int[_values.Length];
		}

		public void Push( float value, int index )
		{
			if ( Count == _values.Length )
			{
				Array.Resize( ref _values, Count * 2 );
				Array.Resize( ref _indices, Count * 2 );
			}
			var i = Count++;
			while ( i > 0 )
			{
				var parent = (i - 1) >> 1;
				if ( _values[parent] <= value )
					break;
				_values[i] = _values[parent];
				_indices[i] = _indices[parent];
				i = parent;
			}
			_values[i] = value;
			_indices[i] = index;
		}

		public (float Value, int Index) Pop()
		{
			var top = (_values[0], _indices[0]);
			Count--;
			if ( Count > 0 )
			{
				var value = _values[Count];
				var index = _indices[Count];
				var i = 0;
				while ( true )
				{
					var child = (i * 2) + 1;
					if ( child >= Count )
						break;
					if ( child + 1 < Count && _values[child + 1] < _values[child] )
						child++;
					if ( _values[child] >= value )
						break;
					_values[i] = _values[child];
					_indices[i] = _indices[child];
					i = child;
				}
				_values[i] = value;
				_indices[i] = index;
			}
			return top;
		}
	}
}
