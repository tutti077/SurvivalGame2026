using Sandbox.Rendering;

namespace Survival;

/// <summary>
/// Ocean and lake water (Mark 2026-10-22: the ocean "should do the same thing as the river", big waves out deep, small
/// laps at the shore; lakes too). Every lake and the rim ocean sit at Sea Level and every piece of dry land stands above
/// it, so ONE water surface at sea level serves the whole world — the terrain hides it wherever there is ground:
/// <list type="bullet">
/// <item>The surface: a radial grid (dense under the camera, coarse at the horizon, no LOD seams) built once and moved
/// with the camera every frame, drawn with <c>materials/water/seawater.vmat</c>. The shader recomputes the water depth
/// exactly as the terrain carve does — the lake mask raster uploaded once (SeaLakeMask) and the ocean shelf — and
/// displaces the grid with the Gerstner waves of <see cref="SeaWaves"/>; the parameters are published here as scene
/// attributes, one source for the GPU and the CPU.</item>
/// <item>The swim volume: per near chunk whose ground dips below sea level, a "water"-tagged trigger box from the lowest
/// ground up to the sea surface. Destroyed with the chunk.</item>
/// </list>
/// </summary>
public sealed partial class TerrainWorldManager
{
	[Property, Group( "Sea" ), Title( "Sea Water" ), Description( "The ocean and lake water surface (and its swim volumes)." )]
	public bool SeaWaterEnabled { get; set; } = true;

	[Property, Group( "Sea" ), Title( "Swell Amplitude (m)" ), Range( 0f, 10f ), Step( 0.1f ), Description( "Open-ocean swell at the default weather: the 14 swells (17–190 m, SeaWaves.Spectrum) together stand about twice this crest to trough. Grows to ~2.8× in a storm (sea energy 1 = WindSystem base + gusts, followed over Swell Energy Follow seconds), shoals and breaks at 0.39 × the depth on the way to the beach." )]
	public float SeaSwellAmplitudeMeters { get; set; } = 4.5f;

	[Property, Group( "Sea" ), Title( "Chop Amplitude (m)" ), Range( 0f, 2f ), Step( 0.05f ), Description( "Short wind waves on top of the swell, everywhere; follows wind and gusts." )]
	public float SeaChopAmplitudeMeters { get; set; } = 0.35f;

	[Property, Group( "Sea" ), Title( "Lake Chop Share (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Lakes get this share of the chop and no swell." )]
	public float SeaLakeChopShare { get; set; } = 0.35f;

	[Property, Group( "Sea" ), Title( "Shore Lap (m)" ), Range( 0f, 1f ), Step( 0.01f ), Description( "The little lap that still reaches the waterline once the waves are depth-limited (Mark 2026-10-22: 'at the shore just a little wake')." )]
	public float SeaShoreLapMeters { get; set; } = 0.12f;

	[Property, Group( "Sea" ), Title( "Swell Min Depth (m)" ), Range( 0f, 100f ), Step( 1f ), Description( "Lakes: swell starts building where a lake is this deep (Mark 2026-10-25: big waves only over deep water); lakes are at most Lake Max Depth deep, so above that they keep only their chop. The ocean ignores it — its swell runs all the way to the beach, depth-limited, and breaks there." )]
	public float SeaSwellMinDepthMeters { get; set; } = 10f;

	[Property, Group( "Sea" ), Title( "Swell Full Depth (m)" ), Range( 1f, 300f ), Step( 1f ), Description( "Lakes: water this deep carries the full swell (the ocean ignores it)." )]
	public float SeaSwellFullDepthMeters { get; set; } = 30f;

	[Property, Group( "Sea" ), Title( "Shore Surge (m)" ), Range( 0f, 1f ), Step( 0.01f ), Description( "How high each lap's sheet of water stands as it runs up the sand (the swash), at default shore energy." )]
	public float SeaShoreSurgeMeters { get; set; } = 0.45f;

	[Property, Group( "Sea" ), Title( "Run-Up Reach (m)" ), Range( 2f, 40f ), Step( 0.5f ), Description( "How far onto the land a lap may run at full shore energy; the terrain hides the sheet wherever the ground stands higher, so a lap runs far up a flat beach and a little way up a steep one." )]
	public float SeaRunUpMeters { get; set; } = 15f;

	[Property, Group( "Sea" ), Title( "Lake Lap Period (s)" ), Range( 2f, 20f ), Step( 0.5f ), Description( "Lakes: seconds between laps reaching the shore (the ocean's laps are timed from the arriving swell crests)." )]
	public float SeaLapPeriodSeconds { get; set; } = 7f;

	[Property, Group( "Sea" ), Title( "Shore Foam Width (m)" ), Range( 0.5f, 30f ), Step( 0.5f ), Description( "Within this many metres of the waterline the laps run in and break into bubbles." )]
	public float SeaShoreFoamWidthMeters { get; set; } = 4f;

	[Property, Group( "Sea" ), Title( "Lake Breaker Height (m)" ), Range( 0f, 3f ), Step( 0.05f ), Description( "Lakes: height of the small breakers rolling in over the shallows (the ocean's breakers are the swell itself)." )]
	public float SeaLakeBreakerMeters { get; set; } = 0.5f;

	[Property, Group( "Sea" ), Title( "Swell Energy Follow (s)" ), Range( 5f, 300f ), Step( 5f ), Description( "How long the swell takes to follow the weather (WindSystem base strength + gusts): a storm builds the sea over this long and it dies down as slowly." )]
	public float SeaEnergyFollowSeconds { get; set; } = 40f;

	const string SeaWaterMaterialPath = "materials/water/seawater.vmat";
	/// <summary>Radial grid: segments around (spacing ≈ 2π r / this — the shader's 0.0123) and its reach.</summary>
	const int SeaGridSegments = 512;
	const float SeaGridInnerRadiusMeters = 0.5f;
	const float SeaGridOuterRadiusMeters = 40000f;
	/// <summary>
	/// The water meshes sit this far ABOVE their surface and the shaders lower them back in the vertex stage. The engine's
	/// "Refraction Stencil Layer" pass draws the raw, undisplaced geometry to mark where the frame-buffer copy is made: a
	/// flat surface at the water level z-fought every shallow bed in that pass and the copy came back black in stripes
	/// across shallow lakes (2026-10-22, found with the raw-copy debug view). Lifted, the raw geometry is always in front
	/// of any bed. Must match WATER_STENCIL_LIFT_UNITS in seawater.shader / riverwater.shader (1 m = 40 u).
	/// </summary>
	const float WaterStencilLiftMeters = 1f;

	/// <summary>The swim box stops this far above sea level (waves above it are for boats, later).</summary>
	const float SeaVolumeTopMeters = 0.3f;

	GameObject _seaSurfaceObject;
	ModelRenderer _seaSurfaceRenderer;
	Texture _seaLakeMaskTexture;
	Texture _seaLakeShoreTexture;
	/// <summary>Metres the sheet is lifted above sea level per lake-mask cell: tarns on mountain tops (TerrainPreviewMountainLakes), 0 elsewhere.</summary>
	Texture _seaLakeLevelTexture;
	Texture _seaSwellFieldTexture;
	int _seaSwellFieldUploaded = int.MinValue;
	/// <summary>The sea's energy 0…1 (SeaWaves.Wind.Energy01): the weather followed over Swell Energy Follow seconds.</summary>
	float _seaEnergy01 = 0.25f;
	bool _seaEnergyStarted;
	/// <summary>The heading the swell travel field was marched for (latched; the chop follows the live wind).</summary>
	Vector2 _seaSwellHeading;
	/// <summary>The swim trigger boxes the loaded chunks made (TryBuildSeaVolume), so the one under the player can follow the waves.</summary>
	readonly List<(GameObject Root, BoxCollider Box, float MinZ)> _seaVolumes = new();
	WindSystem _seaWind;
	float _seaWindLookupAt = float.MinValue;

	/// <summary>
	/// Runs immediately before the river surface draws: grabs the frame behind it (with mips, for the bed blur) as
	/// "WaterSceneColor". The engine's own refraction copy (bWantsFBCopyTexture) is made only where its "Refraction
	/// Stencil Layer" pass marks the water, and over shallow beds and at a distance it left rows of pixels uncopied —
	/// black stripes across lakes (2026-10-22). The sea's own grab is the tail of its foam-memory list
	/// (TerrainWorldManager.SeaFoamMemory): one list shared by both water renderers ran once, before whichever of them
	/// drew first — the river, drawn after the sea, then composed onto a picture without the sea and pasted bare bed over
	/// the lake wherever it was see-through (2026-10-24, the river-mouth cross-fade showed it as sand bands).
	/// </summary>
	static CommandList RiverGrabCommands() => _riverGrab ??= NewWaterGrab( "RiverGrab" );
	static CommandList _riverGrab;

	static CommandList NewWaterGrab( string name )
	{
		var list = new CommandList( name );
		list.GlobalAttributes.GrabFrameTexture( "WaterSceneColor", true );
		return list;
	}

	internal SeaWaves.Tuning SeaTuning => new()
	{
		SwellAmplitudeMeters = SeaSwellAmplitudeMeters,
		ChopAmplitudeMeters = SeaChopAmplitudeMeters,
		LakeChopShare = SeaLakeChopShare,
		ShoreLapMeters = SeaShoreLapMeters,
		SwellMinDepthMeters = SeaSwellMinDepthMeters,
		SwellFullDepthMeters = SeaSwellFullDepthMeters,
	};

	/// <summary>
	/// Per frame: create the surface once the world fields are ready, keep it under the camera and publish the sea
	/// attributes (a handful of vectors). The mesh itself never rebuilds.
	/// </summary>
	void TickSeaSurface()
	{
		if ( !SeaWaterEnabled || _loadSettings is null )
		{
			if ( _seaSurfaceRenderer is not null && _seaSurfaceRenderer.IsValid() )
				_seaSurfaceRenderer.Enabled = false;
			return;
		}

		var cam = ResolveStreamCamera();
		if ( cam is null || !cam.IsValid() )
			return;

		if ( _seaLakeMaskTexture is null && TerrainPreviewLandDiskFields.TryGetLakeMaskRaster( out var grid, out var res ) )
		{
			_seaLakeMaskTexture = Texture.Create( res, res, ImageFormat.R32F )
				.WithData( new ReadOnlySpan<float>( grid, 0, res * res ) )
				.WithName( "sea_lake_mask" )
				.Finish();
		}
		if ( _seaLakeMaskTexture is null )
			return;
		if ( _seaLakeShoreTexture is null && TerrainPreviewLakeShoreDistance.TryGetField( out var shore, out var nodes ) )
		{
			_seaLakeShoreTexture = Texture.Create( nodes, nodes, ImageFormat.R32F )
				.WithData( new ReadOnlySpan<float>( shore, 0, nodes * nodes ) )
				.WithName( "sea_lake_shore" )
				.Finish();
		}
		if ( _seaLakeShoreTexture is null )
			return;
		if ( _seaLakeLevelTexture is null && TerrainPreviewLandDiskFields.TryGetWaterSurfaceLiftRaster( _loadSettings, out var lift, out var liftRes ) )
		{
			_seaLakeLevelTexture = Texture.Create( liftRes, liftRes, ImageFormat.R32F )
				.WithData( new ReadOnlySpan<float>( lift, 0, liftRes * liftRes ) )
				.WithName( "sea_lake_level" )
				.Finish();
		}
		if ( _seaLakeLevelTexture is null )
			return;

		// The weather the swell follows: WindSystem's base strength plus its gusts, eased over Swell Energy Follow seconds
		// (Mark 2026-10-25: "weather to affect them, bigger taller waves" — the gusts had reached the chop only)
		if ( (_seaWind is null || !_seaWind.IsValid()) && Time.Now - _seaWindLookupAt > 3f )
		{
			_seaWindLookupAt = Time.Now;
			_seaWind = Scene.GetAllComponents<WindSystem>().FirstOrDefault();
		}
		var windDir = new Vector2( 1f, 0f );
		var windBase = 0.25f;
		var windGust = 0f;
		if ( _seaWind is not null && _seaWind.IsValid() )
		{
			windDir = new Vector2( _seaWind.Direction.x, _seaWind.Direction.y );
			windBase = _seaWind.CurrentBaseStrength01;
			windGust = _seaWind.Gust01;
		}
		var energyTarget = Math.Clamp( windBase + (windGust * 0.6f), 0f, 1f );
		if ( !_seaEnergyStarted )
		{
			_seaEnergyStarted = true;
			_seaEnergy01 = Math.Clamp( windBase, 0f, 1f );
		}
		_seaEnergy01 += (energyTarget - _seaEnergy01) * (1f - MathF.Exp( -Time.Delta / Math.Max( 1f, SeaEnergyFollowSeconds ) ));

		// The swell travel field for this world and heading (built once on a worker thread; plane waves until it lands).
		// The swell keeps the heading it was marched for: the wind wanders a few degrees over minutes (WindSystem Wander)
		// and real swell is set by distant weather, not the local breeze — only a real change of heading (> 25°) rebuilds,
		// and the chop follows the live wind in between.
		if ( windDir.LengthSquared > 1e-6f && (_seaSwellHeading.LengthSquared < 1e-6f || Vector2.Dot( _seaSwellHeading, windDir.Normal ) < 0.906f) )
			_seaSwellHeading = windDir.Normal;
		SeaSwellTravelField.EnsureBuilding( _loadSettings, _seaSwellHeading );
		var field = SeaSwellTravelField.Current;
		if ( field is not null && field.Fingerprint != _seaSwellFieldUploaded )
		{
			_seaSwellFieldTexture?.Dispose();
			_seaSwellFieldTexture = Texture.Create( field.Nodes, field.Nodes, ImageFormat.RGBA32323232F )
				.WithData( new ReadOnlySpan<float>( field.Data, 0, field.Nodes * field.Nodes * 4 ) )
				.WithName( "sea_swell_field" )
				.Finish();
			_seaSwellFieldUploaded = field.Fingerprint;
		}

		if ( _seaSurfaceObject is null || !_seaSurfaceObject.IsValid() )
		{
			var material = Material.Load( SeaWaterMaterialPath );
			if ( material is not { IsValid: true } )
				return;
			_seaSurfaceObject = new GameObject( true, "SeaWater" );
			_seaSurfaceObject.Flags |= GameObjectFlags.NotSaved;
			_seaSurfaceObject.Parent = GameObject;
			_seaSurfaceRenderer = _seaSurfaceObject.Components.Create<ModelRenderer>();
			_seaSurfaceRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			_seaSurfaceRenderer.Model = BuildSeaGridModel( material );
			_seaSurfaceRenderer.MaterialOverride = material;
			_seaSurfaceRenderer.ExecuteBefore = SeaDrawCommands();
		}
		_seaSurfaceRenderer.Enabled = true;

		var settings = _loadSettings;
		var camPos = cam.WorldPosition;
		// Raised by WaterStencilLiftMeters; seawater.shader lowers it back (see WaterStencilLiftMeters)
		_seaSurfaceObject.WorldPosition = new Vector3( camPos.x, camPos.y, TerrainWorldUnits.MetersToEngine( settings.SeaLevelMeters + WaterStencilLiftMeters ) );

		var attrs = Scene.RenderAttributes;
		attrs.Set( "SeaLakeMask", _seaLakeMaskTexture );
		attrs.Set( "SeaLakeShore", _seaLakeShoreTexture );
		attrs.Set( "SeaLakeLevel", _seaLakeLevelTexture );
		if ( _seaSwellFieldTexture is not null && field is not null )
		{
			attrs.Set( "SeaSwellField", _seaSwellFieldTexture );
			attrs.Set( "SeaSwellFieldInfo", new Vector4( field.HalfSizeMeters, field.Nodes, field.WindDirection.x, field.WindDirection.y ) );
		}
		else
		{
			var d = _seaSwellHeading.LengthSquared > 1e-6f ? _seaSwellHeading : new Vector2( 1f, 0f );
			attrs.Set( "SeaSwellFieldInfo", new Vector4( 0f, 0f, d.x, d.y ) );
		}
		attrs.Set( "SeaShore", new Vector4( SeaShoreFoamWidthMeters, SeaLapPeriodSeconds, SeaShoreSurgeMeters, SeaRunUpMeters ) );
		attrs.Set( "SeaShore2", new Vector4( SeaLakeBreakerMeters, 0f, 0f, 0f ) );
		attrs.Set( "SeaShape", new Vector4( settings.WorldRadiusMeters, settings.LandRadiusMeters, Math.Max( 0f, settings.OceanMaxDepthMeters ), Math.Max( 1f, settings.OceanShelfWidthMeters ) ) );
		attrs.Set( "SeaLake", new Vector4( TerrainPreviewLandDiskFields.GetOpenWaterThreshold01( settings ), Math.Max( 0f, settings.LakeMaxDepthMeters ), Math.Max( 1f, settings.LakeShelfWidthMeters ), 0f ) );
		attrs.Set( "SeaSwellDepth", new Vector4( SeaSwellMinDepthMeters, Math.Max( SeaSwellMinDepthMeters + 0.1f, SeaSwellFullDepthMeters ), 0f, 0f ) );
		attrs.Set( "SeaShelf", new Vector4( Math.Max( 0f, settings.OceanShelfDepthMeters ), Math.Max( 1f, settings.OceanDropOffWidthMeters ), 0f, 0f ) );
		attrs.Set( "SeaWaves", new Vector4( SeaSwellAmplitudeMeters, SeaChopAmplitudeMeters, SeaLakeChopShare, SeaShoreLapMeters ) );
		attrs.Set( "SeaClock", new Vector4( Time.Now, FarTerrainEnabled ? Math.Max( 0f, FarSinkMeters ) : 0f, 0f, 1f ) );

		// Ripple drift, integrated (Mark 2026-10-25: "moving back and forth … speed up and slow down out of nowhere"): the
		// shader placed the ripples at time × speed with the speed following the gusts, so every change of gust jumped the
		// whole pattern forward or back by the full elapsed time. Here the distance travelled only ever adds up.
		var windAmt = Math.Clamp( windBase + (windGust * 0.5f), 0f, 1f );
		if ( windDir.LengthSquared > 1e-6f )
			_seaRippleTravel += windDir.Normal * ((0.3f + windAmt) * Time.Delta);
		attrs.Set( "SeaRippleTravel", new Vector4( _seaRippleTravel.x, _seaRippleTravel.y, 0f, 0f ) );

		// The swell for this weather (SeaWaves.Spectrum — the CPU's SurfaceHeightMeters reads the same)
		var sea = SeaWaves.Spectrum( SeaTuning, _seaEnergy01 );
		var a = sea.Amplitudes;
		attrs.Set( "SeaSwellA", new Vector4( a[0], a[1], a[2], a[3] ) );
		attrs.Set( "SeaSwellB", new Vector4( a[4], a[5], a[6], a[7] ) );
		attrs.Set( "SeaSwellC", new Vector4( a[8], a[9], a[10], a[11] ) );
		attrs.Set( "SeaSwellD", new Vector4( a[12], a[13], sea.Crest, sea.Total ) );
		attrs.Set( "SeaSwellE", new Vector4( sea.Peak, sea.Rms, 0f, 0f ) );

		TickSeaVolumeUnderCamera( camPos, settings );
		TickSeaFoamMemory( camPos );
	}

	/// <summary>
	/// The swim box under the camera follows the drawn surface: its top is the wave height there (the boxes are built flat
	/// at sea level, where a swimmer hung in the air over a trough and sat 3 m under a crest — 2026-10-26). One box, one
	/// CPU wave evaluation per frame; the others stay flat until the player reaches them.
	/// </summary>
	void TickSeaVolumeUnderCamera( Vector3 camPos, TerrainPreviewSettings settings )
	{
		for ( var i = _seaVolumes.Count - 1; i >= 0; i-- )
		{
			var (root, box, _) = _seaVolumes[i];
			if ( root is null || !root.IsValid() || box is null || !box.IsValid() )
				_seaVolumes.RemoveAt( i );
		}
		var size = TerrainWorldUnits.MetersToEngine( Math.Max( 32f, ChunkSizeMeters ) );
		foreach ( var (root, box, minZ) in _seaVolumes )
		{
			var origin = root.WorldPosition;
			if ( camPos.x < origin.x || camPos.y < origin.y || camPos.x >= origin.x + size || camPos.y >= origin.y + size )
				continue;
			var seaZ = TerrainWorldUnits.MetersToEngine( settings.SeaLevelMeters );
			var top = (SeaSurfaceHeightAt( camPos ) ?? seaZ) + TerrainWorldUnits.MetersToEngine( SeaVolumeTopMeters );
			var height = Math.Max( 8f, top - minZ );
			if ( MathF.Abs( box.Scale.z - height ) < 0.5f )
				return;
			box.Scale = new Vector3( size, size, height );
			box.GameObject.WorldPosition = new Vector3( origin.x + (size * 0.5f), origin.y + (size * 0.5f), (minZ + top) * 0.5f );
			return;
		}
	}

	/// <summary>How far the sea's ripples have drifted (m per unit of RippleDrift), added up frame by frame.</summary>
	Vector2 _seaRippleTravel;

	/// <summary>
	/// Per frame, sea or no sea: the far terrain's sink under the near chunks (pixel_lit F_FAR_SINK reads SeaClock.y and
	/// SeaNearRect). Without it the far surface would stand at full height inside the near square and poke through.
	/// </summary>
	void PublishFarSinkAttributes()
	{
		var attrs = Scene.RenderAttributes;
		attrs.Set( "SeaNearRect", NearChunkRect() );
		if ( !SeaWaterEnabled || _seaSurfaceObject is null )
			attrs.Set( "SeaClock", new Vector4( Time.Now, FarTerrainEnabled ? Math.Max( 0f, FarSinkMeters ) : 0f, 0f, 0f ) );
	}

	Vector4 _nearChunkRect;
	float _nearChunkRectAt = float.MinValue;

	/// <summary>
	/// The square the near chunks cover (engine units: min x, min y, max x, max y), refreshed four times a second. The far
	/// terrain sinks FarSink under it on the GPU (pixel_lit F_FAR_SINK) and stands at true height everywhere else — the
	/// old CPU sink of the whole far tier left a 2.5 m step round the near square ("square chunks taken out",
	/// 2026-10-22) and forced the water to sink with it.
	/// </summary>
	Vector4 NearChunkRect()
	{
		if ( Time.Now - _nearChunkRectAt < 0.25f || _loadSettings is null )
			return _nearChunkRect;
		_nearChunkRectAt = Time.Now;
		if ( _loaded.Count == 0 )
			return _nearChunkRect = new Vector4( 0f, 0f, -1f, -1f );
		int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
		foreach ( var c in _loaded.Keys )
		{
			minX = Math.Min( minX, c.X );
			minY = Math.Min( minY, c.Y );
			maxX = Math.Max( maxX, c.X );
			maxY = Math.Max( maxY, c.Y );
		}
		var size = Math.Max( 32f, ChunkSizeMeters );
		var r = _loadSettings.TotalWorldRadiusMeters;
		_nearChunkRect = new Vector4(
			TerrainWorldUnits.MetersToEngine( (minX * size) - r ),
			TerrainWorldUnits.MetersToEngine( (minY * size) - r ),
			TerrainWorldUnits.MetersToEngine( ((maxX + 1) * size) - r ),
			TerrainWorldUnits.MetersToEngine( ((maxY + 1) * size) - r ) );
		return _nearChunkRect;
	}

	void DestroySeaSurface()
	{
		if ( _seaSurfaceObject is not null && _seaSurfaceObject.IsValid() )
			_seaSurfaceObject.Destroy();
		_seaSurfaceObject = null;
		_seaSurfaceRenderer = null;
		_seaLakeMaskTexture?.Dispose();
		_seaLakeMaskTexture = null;
		_seaLakeShoreTexture?.Dispose();
		_seaLakeShoreTexture = null;
		_seaLakeLevelTexture?.Dispose();
		_seaLakeLevelTexture = null;
		_seaSwellFieldTexture?.Dispose();
		_seaSwellFieldTexture = null;
		_seaSwellFieldUploaded = int.MinValue;
		_seaVolumes.Clear();
		DestroySeaFoamMemory();
	}

	/// <summary>
	/// The radial grid, in metres around the origin (engine units): a centre vertex, then rings whose radius grows by
	/// one segment's arc each step, so every cell is about square — 0.5 m apart underfoot, ~650 m at 40 km.
	/// </summary>
	static Model BuildSeaGridModel( Material material )
	{
		var growth = 1f + (MathF.Tau / SeaGridSegments);
		var rings = (int)MathF.Ceiling( MathF.Log( SeaGridOuterRadiusMeters / SeaGridInnerRadiusMeters ) / MathF.Log( growth ) ) + 1;
		var vertexCount = 1 + (rings * SeaGridSegments);
		var indexCount = (SeaGridSegments * 3) + ((rings - 1) * SeaGridSegments * 6);

		var mesh = new Mesh( material, MeshPrimitiveType.Triangles );
		mesh.CreateVertexBuffer<Vertex>( vertexCount );
		mesh.CreateIndexBuffer( indexCount );

		var cos = new float[SeaGridSegments];
		var sin = new float[SeaGridSegments];
		for ( var s = 0; s < SeaGridSegments; s++ )
		{
			cos[s] = MathF.Cos( s * MathF.Tau / SeaGridSegments );
			sin[s] = MathF.Sin( s * MathF.Tau / SeaGridSegments );
		}

		mesh.LockVertexBuffer<Vertex>( vertices =>
		{
			var tangent = new Vector4( 1f, 0f, 0f, 1f );
			vertices[0] = new Vertex { Position = Vector3.Zero, Normal = Vector3.Up, Tangent = tangent };
			var v = 1;
			var radius = SeaGridInnerRadiusMeters;
			for ( var r = 0; r < rings; r++ )
			{
				var units = TerrainWorldUnits.MetersToEngine( radius );
				for ( var s = 0; s < SeaGridSegments; s++ )
					vertices[v++] = new Vertex { Position = new Vector3( cos[s] * units, sin[s] * units, 0f ), Normal = Vector3.Up, Tangent = tangent };
				radius *= growth;
			}
		} );

		mesh.LockIndexBuffer( indices =>
		{
			var n = 0;
			// Centre fan, counter-clockwise seen from above
			for ( var s = 0; s < SeaGridSegments; s++ )
			{
				indices[n++] = 0;
				indices[n++] = 1 + s;
				indices[n++] = 1 + ((s + 1) % SeaGridSegments);
			}
			for ( var r = 0; r < rings - 1; r++ )
			{
				var inner = 1 + (r * SeaGridSegments);
				var outer = inner + SeaGridSegments;
				for ( var s = 0; s < SeaGridSegments; s++ )
				{
					var s1 = (s + 1) % SeaGridSegments;
					indices[n++] = inner + s;
					indices[n++] = outer + s;
					indices[n++] = outer + s1;
					indices[n++] = inner + s;
					indices[n++] = outer + s1;
					indices[n++] = inner + s1;
				}
			}
		} );

		// Never culled: the grid rides the camera and the shader displaces it — up to the highest tarn (a lake on a
		// mountain top lifts the sheet hundreds of metres), so the bounds reach 1 km up
		var reach = TerrainWorldUnits.MetersToEngine( SeaGridOuterRadiusMeters );
		var up = TerrainWorldUnits.MetersToEngine( 1000f );
		mesh.Bounds = new BBox( new Vector3( -reach, -reach, -4000f ), new Vector3( reach, reach, up ) );
		return new ModelBuilder().AddMesh( mesh ).Create();
	}

	/// <summary>
	/// Chunk load: a swim volume where this chunk's ground dips below sea level — one "water" trigger box over the
	/// chunk from its lowest ground up to just above the sea surface (dry ground above sea level is never inside it).
	/// </summary>
	void TryBuildSeaVolume( GameObject chunkRoot, TerrainPreviewSettings settings, float[] heightsZ, float chunkSizeMeters )
	{
		if ( !SeaWaterEnabled || heightsZ is null || heightsZ.Length == 0 )
			return;
		// A tarn on a mountain top lifts this chunk's water to its level (the ring around a tarn is never lower than it,
		// so the box holds no air over dry ground within ~100 m; TerrainPreviewMountainLakes)
		var level = settings.SeaLevelMeters;
		var originX = TerrainWorldUnits.EngineToMeters( chunkRoot.WorldPosition.x );
		var originY = TerrainWorldUnits.EngineToMeters( chunkRoot.WorldPosition.y );
		for ( var sy = 0; sy <= 2; sy++ )
		{
			for ( var sx = 0; sx <= 2; sx++ )
				level = Math.Max( level, TerrainPreviewLandDiskFields.SampleWaterSurfaceMeters( settings, originX + (chunkSizeMeters * 0.5f * sx), originY + (chunkSizeMeters * 0.5f * sy) ) );
		}
		var seaZ = TerrainWorldUnits.MetersToEngine( level );
		var minZ = float.MaxValue;
		foreach ( var z in heightsZ )
			minZ = Math.Min( minZ, z );
		minZ += chunkRoot.WorldPosition.z;
		if ( minZ >= seaZ - TerrainWorldUnits.MetersToEngine( 0.2f ) )
			return;

		var top = seaZ + TerrainWorldUnits.MetersToEngine( SeaVolumeTopMeters );
		var size = TerrainWorldUnits.MetersToEngine( chunkSizeMeters );
		var go = new GameObject( true, "SeaWater" );
		go.Flags |= GameObjectFlags.NotSaved;
		go.Parent = chunkRoot;
		go.WorldPosition = new Vector3( chunkRoot.WorldPosition.x + (size * 0.5f), chunkRoot.WorldPosition.y + (size * 0.5f), (minZ + top) * 0.5f );
		go.Tags.Add( RiverWaterTag );

		var trigger = go.Components.Create<BoxCollider>();
		trigger.IsTrigger = true;
		trigger.Scale = new Vector3( size, size, Math.Max( 8f, top - minZ ) );

		var volume = go.Components.Create<WaterVolume>();
		volume.WaveAmplitude = 0f;
		_seaVolumes.Add( (chunkRoot, trigger, minZ) );
	}

	/// <summary>
	/// Height of the sea / lake surface at a world point (engine units), waves included — what a floating body should
	/// ride. Null where there is no open water. The wind is WindSystem's (looked up at most every few seconds).
	/// </summary>
	public float? SeaSurfaceHeightAt( Vector3 worldPosition )
	{
		var settings = _loadSettings;
		if ( !SeaWaterEnabled || settings is null )
			return null;
		var x = TerrainWorldUnits.EngineToMeters( worldPosition.x );
		var y = TerrainWorldUnits.EngineToMeters( worldPosition.y );
		var tuning = SeaTuning;
		if ( SeaWaves.DepthMeters( settings, tuning, x, y, out _ ) <= 0f )
			return null;

		if ( (_seaWind is null || !_seaWind.IsValid()) && Time.Now - _seaWindLookupAt > 3f )
		{
			_seaWindLookupAt = Time.Now;
			_seaWind = Scene.GetAllComponents<WindSystem>().FirstOrDefault();
		}
		var wind = _seaWind is not null && _seaWind.IsValid()
			? new SeaWaves.Wind { Direction = new Vector2( _seaWind.Direction.x, _seaWind.Direction.y ), BaseStrength01 = _seaWind.CurrentBaseStrength01, Gust01 = _seaWind.Gust01, Energy01 = _seaEnergy01 }
			: new SeaWaves.Wind { Direction = new Vector2( 1f, 0f ), BaseStrength01 = 0.25f, Gust01 = 0f, Energy01 = _seaEnergy01 };
		var meters = TerrainPreviewLandDiskFields.SampleWaterSurfaceMeters( settings, x, y ) + SeaWaves.SurfaceHeightMeters( settings, tuning, wind, x, y, Time.Now );
		return TerrainWorldUnits.MetersToEngine( meters );
	}

	/// <summary>Usage: <c>sea_probe</c> — the sea under the camera: depth, openness, wind, swell / chop settings and the surface height now.</summary>
	[ConCmd( "sea_probe" )]
	public static void ConCmdSeaProbe()
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		var cam = scene.IsValid() ? scene.Camera : null;
		if ( manager is null || !manager.IsValid() || cam is null || !cam.IsValid() || manager._loadSettings is null )
		{
			Log.Warning( "[Sea] sea_probe: no terrain world / camera" );
			return;
		}
		var p = cam.WorldPosition;
		var x = TerrainWorldUnits.EngineToMeters( p.x );
		var y = TerrainWorldUnits.EngineToMeters( p.y );
		var depth = SeaWaves.DepthMeters( manager._loadSettings, manager.SeaTuning, x, y, out var openness );
		var height = manager.SeaSurfaceHeightAt( p );
		var wind = manager._seaWind;
		Log.Info( $"[Sea] at {x:0},{y:0} m: depth {depth:0.0} m, openness {openness:0.00}, swell setting {manager.SeaSwellAmplitudeMeters:0.0} m, chop {manager.SeaChopAmplitudeMeters:0.00} m, "
			+ $"wind {(wind is { } w && w.IsValid() ? $"base {w.CurrentBaseStrength01:0.00} gust {w.Gust01:0.00}" : "none (defaults)")}, sea energy {manager._seaEnergy01:0.00}, "
			+ $"surface {(height is { } h ? $"{TerrainWorldUnits.EngineToMeters( h ) - manager._loadSettings.SeaLevelMeters:+0.00;-0.00} m vs sea level" : "no water")}" );
		var field = SeaSwellTravelField.Current;
		if ( field is null )
			Log.Info( "[Sea] swell travel field: not built yet (plane waves)" );
		else
		{
			var (detour, grad, exposure) = SeaSwellTravelField.Sample( field, field.WindDirection, x, y );
			var sea = SeaWaves.Spectrum( manager.SeaTuning, manager._seaEnergy01 );
			Log.Info( $"[Sea] swell travel field {field.Nodes}² @ {SeaSwellTravelField.NodeMeters} m, heading {MathF.Atan2( field.WindDirection.y, field.WindDirection.x ) * 180f / MathF.PI:0}°: detour {detour:0.0} m, travel {grad.x:0.00},{grad.y:0.00}, exposure {exposure:0.00}; "
				+ $"swell rms {sea.Rms:0.00} m, breaking height here {SeaWaves.BreakingRatio * depth:0.00} m" );
		}
		var st = manager._loadSettings;
		Log.Info( $"[Sea] settings: ocean max {st.OceanMaxDepthMeters} m, shelf {st.OceanShelfDepthMeters} m over {st.OceanShelfWidthMeters} m, drop-off {st.OceanDropOffWidthMeters} m; lake max {st.LakeMaxDepthMeters} m over {st.LakeShelfWidthMeters} m; beach {st.BeachWidthMeters} m, wet line {st.WetLineHeightMeters} m" );
	}

	/// <summary>Usage: <c>sea_tune &lt;swell m&gt; [chop m]</c> — set the live Swell / Chop Amplitude (the component values; not saved).</summary>
	[ConCmd( "sea_tune" )]
	public static void ConCmdSeaTune( float swellMeters, float chopMeters = -1f )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		if ( manager is null || !manager.IsValid() )
			return;
		manager.SeaSwellAmplitudeMeters = Math.Clamp( swellMeters, 0f, 10f );
		if ( chopMeters >= 0f )
			manager.SeaChopAmplitudeMeters = Math.Clamp( chopMeters, 0f, 2f );
		Log.Info( $"[Sea] swell {manager.SeaSwellAmplitudeMeters:0.00} m, chop {manager.SeaChopAmplitudeMeters:0.00} m" );
	}
}
