using System;
using Sandbox;

namespace Survival;

/// <summary>
/// The caldera wall (Mark 2026-11-03: "we live in a caldera … a MASSIVE mountain ring encircles the whole map, 5000 m+
/// tall, always visible"; then "VERY flat … some ridges lower, or more jutted out toward the player … should feel real
/// not like a wall of rock"): one render-only polar heightfield round the world origin. It rises out of the sea just past
/// the rim ocean as low foothill ridges that climb inward-to-outward to a main crest set back by Range Depth, where
/// massifs reach Peak Height and saddles dip to Saddle Height. Ridge arms run down from the crest toward the caldera —
/// some die early, some reach nearly to the foot — with valleys and sea inlets between them, so the range has depth and
/// layers instead of one face. Backdrop only — no collider, no streaming, no LOD, no shadows; drawn with
/// <c>shaders/environment/caldera_ring.shader</c>: lit rock only — the distance haze that pales it comes from the scene-wide
/// <see cref="AerialPerspective"/> pass like everything else (one atmosphere; the ring was never fogged out because that
/// haze is capped). Lit each frame from the scene's
/// <see cref="EnvironmentDayNightCycle"/> sun / moon lights. It is NOT a child of the cycle group: cycle_toggle switches
/// that group off and must not take the mountains with it. The <see cref="SkyDome"/> and the cycle's disk orbit must sit
/// beyond the ring's back edge (terrainTest: dome 80 km, disks 55 km) or they draw in front of it.
/// </summary>
[Title( "Caldera Ring" )]
public sealed class CalderaRing : Component
{
	public const string DefaultMaterialPath = "materials/environment/sky/caldera_ring.vmat";

	[Property, Title( "Material" )]
	public Material RingMaterial { get; set; }

	[Property, Group( "Shape" ), Title( "Foot Radius (m)" ), Description( "Distance from the world centre where the first foothills leave the sea. terrainTest: the 22.5 km world edge (land 20 km + 2.5 km rim ocean) + 500 m." )]
	public float FootRadiusMeters { get; set; } = 23000f;

	[Property, Group( "Shape" ), Title( "Range Depth (m)" ), Description( "Average horizontal distance from the foot to the main crest (±25 % round the ring). The ridge arms fill it." )]
	public float RangeDepthMeters { get; set; } = 11000f;

	[Property, Group( "Shape" ), Title( "Saddle Height (m)" ), Description( "Lowest the main crest dips between massifs." )]
	public float SaddleHeightMeters { get; set; } = 3200f;

	[Property, Group( "Shape" ), Title( "Peak Height (m)" ), Description( "Tallest summits along the main crest." )]
	public float PeakHeightMeters { get; set; } = 7000f;

	[Property, Group( "Shape" ), Title( "Ridge Spacing (m)" ), Description( "Typical distance between the ridge arms that run down from the crest toward the caldera." )]
	public float RidgeSpacingMeters { get; set; } = 3000f;

	[Property, Group( "Shape" ), Title( "Ridge Reach (0–1)" ), Range( 0f, 1f ), Description( "How far the longest arms run toward the foot, as a share of Range Depth. Each arm rolls its own reach under this." )]
	public float RidgeReach01 { get; set; } = 0.9f;

	[Property, Group( "Shape" ), Title( "Seed" )]
	public int Seed { get; set; } = 1337;

	/// <summary>Back edge of the mesh from the world centre (m) — what the dome and disks must clear.</summary>
	public float OuterRadiusMeters => FootRadiusMeters + RangeDepthMeters * 1.25f + BackSlopeRunMeters;

	const int Segments = 1536;            // ~95 m per column at the foot, ~150 m at the crest
	const float BackSlopeRunMeters = 6000f;
	const float ApronDepthMeters = 400f;  // under the ocean floor past the world edge
	const float ApronRunMeters = 900f;
	const float InletDepthMeters = 260f;  // valley floors near the foot dip under the sea: inlets between the arms

	// Rows: the apron, t = 0 foot … 1 main crest (dense toward the crest, where the rock is steepest), then the back slope.
	static readonly float[] RangeRows = BuildRangeRows( 40 );
	static readonly float[] BackRows = { 0.1f, 0.25f, 0.45f, 0.7f, 1f };
	static int RowCount => 1 + RangeRows.Length + BackRows.Length;

	ModelRenderer _renderer;
	EnvironmentDayNightCycle _cycle;
	int _builtKey;

	protected override void OnStart()
	{
		// Found once, enabled or not: the cycle group may be toggled off and on (cycle_toggle) while the ring stays.
		_cycle = Scene.Components.GetAll<EnvironmentDayNightCycle>( FindMode.EverythingInSelfAndDescendants ).FirstOrDefault();
		Rebuild();
	}

	protected override void OnUpdate()
	{
		if ( ShapeKey() != _builtKey )
			Rebuild();
	}

	protected override void OnPreRender()
	{
		if ( _renderer is null || !_renderer.IsValid() )
			return;

		var attrs = _renderer.Attributes;
		var sun = _cycle?.SunLight;
		var moon = _cycle?.MoonLight;
		var sunOn = sun is not null && sun.IsValid() && sun.Active;
		var moonOn = moon is not null && moon.IsValid() && moon.Active;
		attrs.Set( "RingSunDirection", sunOn ? -sun.WorldRotation.Forward : Vector3.Up );
		attrs.Set( "RingSunColor", sunOn ? Rgb( sun.LightColor ) : Vector3.Zero );
		attrs.Set( "RingAmbient", sunOn ? Rgb( sun.SkyColor ) : new Vector3( 0.18f, 0.23f, 0.33f ) );
		attrs.Set( "RingMoonDirection", moonOn ? -moon.WorldRotation.Forward : Vector3.Down );
		attrs.Set( "RingMoonColor", moonOn ? Rgb( moon.LightColor ) : Vector3.Zero );
	}

	static Vector3 Rgb( Color c ) => new( c.r, c.g, c.b );

	int ShapeKey() => HashCode.Combine( FootRadiusMeters, RangeDepthMeters, SaddleHeightMeters, PeakHeightMeters, RidgeSpacingMeters, RidgeReach01, Seed );

	void Rebuild()
	{
		_builtKey = ShapeKey();
		RingMaterial ??= Material.Load( DefaultMaterialPath );
		_renderer ??= Components.Get<ModelRenderer>();
		if ( _renderer is null )
		{
			Log.Warning( $"[CalderaRing] '{GameObject.Name}' needs a ModelRenderer on the same object — add it in the scene." );
			return;
		}

		_renderer.Model = BuildRing( RingMaterial );
		_renderer.RenderType = ModelRenderer.ShadowRenderType.Off;

		var dome = Scene.Components.GetAll<SkyDome>( FindMode.EverythingInSelfAndDescendants ).FirstOrDefault();
		if ( dome is not null && dome.RadiusMeters < OuterRadiusMeters + 20000f )
			Log.Warning( $"[CalderaRing] Sky Dome radius {dome.RadiusMeters:0} m is inside the ring's reach (back edge {OuterRadiusMeters:0} m + up to 20 km of camera offset) — the dome will draw in front of the mountains." );
	}

	Model BuildRing( Material material )
	{
		var rows = RowCount;
		var columns = Segments + 1;
		var radii = new float[columns * rows];
		var heights = new float[columns * rows];
		var ribs = new float[columns * rows];
		var crestOf = new float[columns];
		var midRadius = FootRadiusMeters + RangeDepthMeters * 0.6f;
		var ridgeCells = Math.Max( 8, (int)MathF.Round( MathF.Tau * midRadius / Math.Max( 200f, RidgeSpacingMeters ) ) );
		var span = Math.Max( 0f, PeakHeightMeters - SaddleHeightMeters );

		for ( var s = 0; s < columns; s++ )
		{
			var seg = s % Segments;   // the seam column repeats column 0 exactly
			var u = (seg + Jitter( seg, 0, 0.35f )) / Segments;

			// Main crest: smooth massifs carry the slopes (saddles at Saddle Height); the sharp summits only sit on top,
			// so a peak's cusp never runs down the whole face as a seam. Set back 75–125 % of Range Depth.
			var massif = SaddleHeightMeters + span * 0.62f * MassifShape01( u );
			var summit = span * 0.38f * PeakShape01( u );
			var crest = massif + summit;
			var depth = RangeDepthMeters * (0.75f + 0.5f * Noise( u, 21, 11 ));
			var steepness = 1.4f + 1.2f * Noise( u, 33, 27 );   // how late the climb steepens toward the crest
			// Each arm's reach: the share of the depth (from the crest) it carries height toward the foot.
			var reach = RidgeReach01 * (0.15f + 0.85f * MathF.Pow( Noise( u, ridgeCells, 41 ), 0.7f ));
			// Arms swing sideways on the way down (up to ~2 ridge spacings over the depth), so from the caldera they show
			// their length as diagonal ridgelines instead of being seen end-on as vertical pleats.
			var twist = (Noise( u, Math.Max( 4, ridgeCells / 3 ), 61 ) - 0.5f) * (4f / ridgeCells);
			crestOf[s] = crest;

			var i = s * rows;
			radii[i] = FootRadiusMeters - ApronRunMeters;
			heights[i] = -ApronDepthMeters;
			for ( var w = 0; w < RangeRows.Length; w++ )
			{
				var t = RangeRows[w];
				var tj = w is 0 || w == RangeRows.Length - 1 ? t : Math.Clamp( t + Jitter( seg, w, 0.008f ), 0f, 1f );
				var radiusMeters = FootRadiusMeters + tj * depth;
				// Arms curve and branch on their way down: the ridge field is warped by the distance from the crest.
				var warp = (Noise2( u, radiusMeters, 40, 2600f, 17 ) - 0.5f) * (1.0f / ridgeCells) + twist * (1f - tj);
				var rib = 0.8f * MathF.Pow( Ridged( u + warp, ridgeCells, 29 ), 1.6f )
					+ 0.2f * MathF.Pow( Ridged( u + warp * 1.3f, ridgeCells * 2, 33 ), 1.3f );
				var armStart = 1f - reach;
				var arm = rib * MathF.Pow( Math.Clamp( (tj - armStart) / Math.Max( 0.05f, reach ), 0f, 1f ), 0.75f );
				// On an arm the ground stands as if closer to the crest; between arms it stays low (valleys, inlets).
				var tEff = tj + (1f - tj) * arm * 0.85f;
				var height = massif * MathF.Pow( tEff, steepness ) + summit * MathF.Pow( tEff, 10f );
				height -= InletDepthMeters * MathF.Pow( 1f - tj, 3f ) * (1f - rib);
				// Knobs, false summits and a broken crest edge.
				height += massif * 0.09f * (Noise2( u, radiusMeters, 160, 900f, 51 ) - 0.5f) * 2f * tEff;
				radii[i + 1 + w] = radiusMeters;
				heights[i + 1 + w] = tj > 0f ? height : 0f;
				ribs[i + 1 + w] = rib;
			}
			for ( var b = 0; b < BackRows.Length; b++ )
			{
				var k = BackRows[b];
				var j = i + 1 + RangeRows.Length + b;
				radii[j] = FootRadiusMeters + depth + k * BackSlopeRunMeters;
				heights[j] = crest * (1f - 0.5f * MathF.Pow( k, 0.8f ));
				ribs[j] = 1f;
			}
		}

		var vertices = new Vertex[columns * rows];
		for ( var s = 0; s < columns; s++ )
		{
			var seg = s % Segments;
			var u = (seg + Jitter( seg, 0, 0.35f )) / Segments;
			var dir = new Vector2( MathF.Cos( u * MathF.Tau ), MathF.Sin( u * MathF.Tau ) );
			var strataWarp = (Noise( u, 61, 17 ) - 0.5f) * 500f;
			for ( var r = 0; r < rows; r++ )
			{
				var i = s * rows + r;
				// Radial grade to the next row decides cliff vs scree.
				var next = Math.Min( r + 1, rows - 1 );
				var prev = Math.Max( r - 1, 0 );
				var run = Math.Max( 1f, radii[s * rows + next] - radii[s * rows + prev] );
				var grade = MathF.Abs( heights[s * rows + next] - heights[s * rows + prev] ) / run;
				vertices[i] = new Vertex
				{
					Position = TerrainWorldUnits.MetersToEngine( new Vector3( dir.x * radii[i], dir.y * radii[i], heights[i] ) ),
					Normal = new Vector3( -dir.x, -dir.y, 0f ),
					Tangent = new Vector4( 0f, 0f, 1f, 1f ),
					TexCoord0 = Vector2.Zero,
					Color = ColourAt( seg, r, heights[i], grade, ribs[i], crestOf[s], strataWarp ),
				};
			}
		}

		var indices = new int[Segments * (rows - 1) * 6];
		var n = 0;
		for ( var s = 0; s < Segments; s++ )
		{
			for ( var r = 0; r < rows - 1; r++ )
			{
				var i0 = s * rows + r;
				var i1 = i0 + 1;
				var i2 = i0 + rows;
				var i3 = i2 + 1;
				// Alternate the diagonal by hash so the facets never read as a regular grid.
				if ( Hash( s, r, 5 ) < 0.5f )
				{
					indices[n++] = i0; indices[n++] = i2; indices[n++] = i1;
					indices[n++] = i1; indices[n++] = i2; indices[n++] = i3;
				}
				else
				{
					indices[n++] = i0; indices[n++] = i2; indices[n++] = i3;
					indices[n++] = i0; indices[n++] = i3; indices[n++] = i1;
				}
			}
		}

		var mesh = new Mesh( material, MeshPrimitiveType.Triangles );
		mesh.CreateVertexBuffer<Vertex>( vertices.Length );
		mesh.CreateIndexBuffer( indices.Length );
		mesh.LockVertexBuffer<Vertex>( span => vertices.AsSpan().CopyTo( span ) );
		mesh.LockIndexBuffer( span => indices.AsSpan().CopyTo( span ) );
		var extent = TerrainWorldUnits.MetersToEngine( OuterRadiusMeters + 100f );
		mesh.Bounds = new BBox(
			new Vector3( -extent, -extent, TerrainWorldUnits.MetersToEngine( -ApronDepthMeters - 10f ) ),
			new Vector3( extent, extent, TerrainWorldUnits.MetersToEngine( PeakHeightMeters * 1.1f + 300f ) ) );
		return new ModelBuilder().AddMesh( mesh ).Create();
	}

	/// <summary>Range rows from the foot (0) to the main crest (1), spaced closer toward the crest.</summary>
	static float[] BuildRangeRows( int count )
	{
		var rows = new float[count + 1];
		for ( var k = 0; k <= count; k++ )
		{
			var x = k / (float)count;
			rows[k] = 0.55f * x + 0.45f * (1f - (1f - x) * (1f - x));
		}
		return rows;
	}

	/// <summary>0–1 massif lift (~10 km): the smooth body of the range; its lows are the deep saddles.</summary>
	float MassifShape01( float u )
	{
		var massif = Noise( u, 16, 3 );
		return massif * massif * (3f - 2f * massif);
	}

	/// <summary>0–1 summit lift: sharp peaks (~2 km), taller on the big massifs, and a jagged edge (~500 m). Ridged noise
	/// peaks in a cusp, so summits are points.</summary>
	float PeakShape01( float u )
	{
		var peaks = MathF.Pow( Ridged( u, 80, 7 ), 2.5f );
		var jag = MathF.Pow( Ridged( u, 320, 19 ), 1.5f );
		return Math.Clamp( 0.75f * peaks * (0.4f + 0.6f * MassifShape01( u )) + 0.25f * jag, 0f, 1f );
	}

	/// <summary>Basalt cliffs with oxidised tuff strata where the rock is steep, grey-brown scree on gentle ground, ash on
	/// the high gentle ground, valleys darker than the ridge crests.</summary>
	Color ColourAt( int seg, int row, float heightMeters, float grade, float rib, float crest, float strataWarp )
	{
		var basalt = new Vector3( 0.040f, 0.036f, 0.034f );
		var tuff = new Vector3( 0.085f, 0.050f, 0.034f );
		var scree = new Vector3( 0.070f, 0.064f, 0.058f );
		var ash = new Vector3( 0.150f, 0.142f, 0.135f );

		var band = (int)MathF.Floor( (heightMeters + strataWarp) / 380f );
		var cliff = (band & 3) == 1 ? tuff : basalt;
		var c = Vector3.Lerp( scree, cliff, SmoothStep( 0.45f, 0.9f, grade ) );
		c = Vector3.Lerp( c, ash, 0.55f * SmoothStep( 0.55f * crest, 0.85f * crest, heightMeters ) * (1f - SmoothStep( 0.5f, 1f, grade )) );
		var shade = (0.8f + 0.4f * Hash( seg, row, 41 )) * (0.7f + 0.45f * rib);
		return new Color( c.x * shade, c.y * shade, c.z * shade, 1f );
	}

	float Jitter( int a, int b, float amplitude ) => (Hash( a, b, 3 ) - 0.5f) * 2f * amplitude;

	float Hash( int a, int b, int salt )
	{
		var h = (uint)(a * 374761393 + b * 668265263 + (Seed + salt) * 1274126177);
		h = (h ^ (h >> 13)) * 1274126177u;
		h ^= h >> 16;
		return (h & 0xFFFFFF) / (float)0x1000000;
	}

	/// <summary>Smooth value noise round the circle: <paramref name="cells"/> lattice points, wraps at u = 1.</summary>
	float Noise( float u, int cells, int salt )
	{
		var x = (u - MathF.Floor( u )) * cells;
		var i = (int)MathF.Floor( x );
		var f = x - i;
		var a = Hash( i % cells, 0, salt );
		var b = Hash( (i + 1) % cells, 0, salt );
		return a + (b - a) * (f * f * (3f - 2f * f));
	}

	/// <summary>2D value noise: round the circle (wraps at u = 1) × radius in metres (cell size <paramref name="cellMeters"/>).</summary>
	float Noise2( float u, float radiusMeters, int cells, float cellMeters, int salt )
	{
		var x = (u - MathF.Floor( u )) * cells;
		var y = radiusMeters / cellMeters;
		var i = (int)MathF.Floor( x );
		var j = (int)MathF.Floor( y );
		var fx = x - i;
		var fy = y - j;
		fx = fx * fx * (3f - 2f * fx);
		fy = fy * fy * (3f - 2f * fy);
		var i0 = i % cells;
		var i1 = (i + 1) % cells;
		var a = Hash( i0, j, salt ) + (Hash( i1, j, salt ) - Hash( i0, j, salt )) * fx;
		var b = Hash( i0, j + 1, salt ) + (Hash( i1, j + 1, salt ) - Hash( i0, j + 1, salt )) * fx;
		return a + (b - a) * fy;
	}

	float Ridged( float u, int cells, int salt ) => 1f - MathF.Abs( 2f * Noise( u, cells, salt ) - 1f );

	static float SmoothStep( float a, float b, float x )
	{
		var t = Math.Clamp( (x - a) / (b - a), 0f, 1f );
		return t * t * (3f - 2f * t);
	}
}
