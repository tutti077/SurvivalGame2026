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

	/// <summary>One river segment of the merged surface: its network index and the carved water depth under each end.</summary>
	readonly struct RiverSurfaceSegment
	{
		public int Index { get; init; }
		public float DepthA { get; init; }
		public float DepthB { get; init; }
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

		// The water depth under each segment end, from the carved ground itself (bed cap per biome included): the
		// shader darkens by the path through the water from this alone — it reads no depth buffer (the engine's
		// depth chain only exists while a ScreenSpaceReflections pass is on the camera, 2026-10-21).
		var owned = new RiverSurfaceSegment[segments.Count];
		for ( var k = 0; k < segments.Count; k++ )
		{
			ref var s = ref net.Segments[segments[k]];
			owned[k] = new RiverSurfaceSegment
			{
				Index = segments[k],
				DepthA = Math.Max( 0.2f, s.LevelA - TerrainPreviewPipeline.Sample( settings, s.Ax, s.Ay ).HeightMeters ),
				DepthB = Math.Max( 0.2f, s.LevelB - TerrainPreviewPipeline.Sample( settings, s.Bx, s.By ).HeightMeters ),
			};
		}
		_riverSegmentsByChunk[coord] = owned;
		_riverSurfaceDirty = true;

		var root = new GameObject( true, "RiverWater" );
		root.Flags |= GameObjectFlags.NotSaved;
		root.Parent = chunkRoot;
		root.LocalPosition = Vector3.Zero;

		var flow = Math.Clamp( settings.RiverFlowSpeedMetersPerSecond, 0f, 10f );
		foreach ( var v in segments )
			BuildRiverVolume( root, net, v, chunkMinX, chunkMinY, flow );
	}

	/// <summary>Chunk unload: its segments leave the merged surface (the swim volumes die with the chunk object).</summary>
	void ReleaseRiverWater( TerrainChunkCoord coord )
	{
		if ( _riverSegmentsByChunk.Remove( coord ) )
			_riverSurfaceDirty = true;
	}

	void DestroyRiverSurface()
	{
		_riverSegmentsByChunk.Clear();
		_riverSurfaceDirty = false;
		if ( _riverSurfaceObject is not null && _riverSurfaceObject.IsValid() )
			_riverSurfaceObject.Destroy();
		_riverSurfaceObject = null;
		_riverSurfaceRenderer = null;
	}

	/// <summary>Per frame: rebuild the merged surface when chunks with river changed (throttled). Nothing otherwise.</summary>
	void TickRiverSurface()
	{
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
			foreach ( var list in _riverSegmentsByChunk.Values )
				segments.AddRange( list );
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
		}
		_riverSurfaceRenderer.Model = model;
		_riverSurfaceRenderer.MaterialOverride = material;
		_riverSurfaceRenderer.Enabled = true;
	}

	/// <summary>
	/// The shader's ripple pattern repeats every this many metres along the river, so the along-river UV is stored
	/// modulo it: vertex UVs are half precision, and raw arc metres (thousands) stepped by whole metres.
	/// </summary>
	const float RiverRippleTileMeters = 64f;
	/// <summary>Vertex colour R carries the water depth in metres over this range (8-bit: ~3 cm steps).</summary>
	const float RiverDepthColorRangeMeters = 8f;

	/// <summary>
	/// One mesh for the given segments, in absolute engine units. Three vertices across each end (bank, centre line,
	/// bank) so the water depth — 0 at the edges, the carved depth on the centre line — interpolates across the channel;
	/// UV = (metres downstream modulo the ripple tile, signed metres across); vertex colour R = depth / 8 m.
	/// </summary>
	static Model BuildRiverSurfaceModel( TerrainPreviewRivers.Network net, List<RiverSurfaceSegment> segments, Material material )
	{
		var mesh = new Mesh( material, MeshPrimitiveType.Triangles );
		mesh.CreateVertexBuffer<Vertex>( segments.Count * 6 );
		mesh.CreateIndexBuffer( segments.Count * 12 );
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
				var sideB = Perpendicular( hasNext ? Bisector( dir, SegmentDirection( net, si + 1 ) ) : dir );
				var ha = s.HalfA + RiverSurfaceOverlapMeters;
				var hb = s.HalfB + RiverSurfaceOverlapMeters;
				var za = s.LevelA + RiverSurfaceLiftMeters;
				var zb = s.LevelB + RiverSurfaceLiftMeters;
				// The carve rounds each river end with a half-width cap (distance to the end point), but the strip used to
				// stop dead at the end vertex: the source showed a dry carved bowl behind a straight water edge (Mark
				// 2026-10-21). Push the open ends out by the half width; what pokes past the channel is under the bank.
				var ax = s.Ax - (hasPrev ? 0f : dir.x * ha);
				var ay = s.Ay - (hasPrev ? 0f : dir.y * ha);
				var bx = s.Bx + (hasNext ? 0f : dir.x * hb);
				var by = s.By + (hasNext ? 0f : dir.y * hb);
				// Continuous within the segment, and equal modulo the tile to the next segment's start.
				var uA = s.ArcA % RiverRippleTileMeters;
				var uB = uA + (s.ArcB - s.ArcA);
				var dA = Math.Clamp( segments[q].DepthA / RiverDepthColorRangeMeters, 0f, 1f );
				var dB = Math.Clamp( segments[q].DepthB / RiverDepthColorRangeMeters, 0f, 1f );
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
				for ( var c = 0; c < 6; c++ )
				{
					var p = TerrainWorldUnits.MetersToEngine( metres[c] );
					lo = Vector3.Min( lo, p );
					hi = Vector3.Max( hi, p );
					vertices[v++] = new Vertex
					{
						Position = p,
						Normal = Vector3.Up,
						Tangent = tangent,
						TexCoord0 = uvs[c],
						Color = new Color( depths[c], 0f, 0f, 1f ),
					};
				}
			}
		} );

		mesh.LockIndexBuffer( indices =>
		{
			var n = 0;
			for ( var q = 0; q < segments.Count; q++ )
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
		trigger.Scale = TerrainWorldUnits.MetersToEngine( new Vector3( length + 1f, width, depth ) );

		var volume = go.Components.Create<WaterVolume>();
		volume.FluidVelocity = new Vector3( dir.x, dir.y, 0f ) * TerrainWorldUnits.MetersToEngine( flowMetersPerSecond );
		volume.WaveAmplitude = 0f;
	}
}
