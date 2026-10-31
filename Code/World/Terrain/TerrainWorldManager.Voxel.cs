namespace Survival;

/// <summary>
/// Voxel digging in the live world (Mark 2026-10-29: "implement the voxel terrain in our terrain test"). The heightfield
/// sampler stays the authority for undug ground; a 64 m chunk the player digs, fills or rakes gets a <b>voxel column</b>:
/// a <see cref="VoxelField"/> (0.5 m cells) seeded from that chunk's own meshed heights, reaching Depth Below Ground under its
/// lowest point and Head Room above its highest, meshed by a <see cref="VoxelChunkSet"/> under its own root object. Once the
/// column's first meshes have landed the heightfield chunk's renderer and collider switch off (its trees, grass mask and
/// scatter stay). The field carries a two-cell margin past the square, so a brush at a chunk border writes the same samples
/// into both columns and <see cref="VoxelMesher.Clip"/> lands both meshes on the same edge vertices. Edits are
/// host-authoritative (<see cref="RequestBrush"/> → <see cref="RpcRequestVoxelBrush"/> → <see cref="RpcApplyVoxelEdit"/>), logged
/// to <c>voxel_edits.json</c> in the world save folder and replayed onto a column when its chunk loads. Columns go with
/// their chunk when it unloads; the log brings them back.
/// </summary>
public sealed partial class TerrainWorldManager : IVoxelBrushTarget
{
	sealed class VoxelColumn
	{
		public TerrainChunkCoord Coord;
		public LoadedChunk Chunk;
		public GameObject Root;
		public VoxelChunkSet Set;
		/// <summary>The heightfield chunk is hidden and the voxel meshes stand in for it.</summary>
		public bool Swapped;
	}

	[Property, Group( "Voxel" ), Title( "Voxel Digging" ), Description( "Digging, filling and raking swap the touched 64 m chunk for voxel ground (0.5 m cells) seeded from its own mesh. Off = the brushes do nothing here." )]
	public bool VoxelDigging { get; set; } = true;
	[Property, Group( "Voxel" ), Title( "Depth Below Ground (m)" ), Range( 4f, 64f ), Step( 1f ), Description( "How far under a chunk's lowest point its voxel field reaches — the deepest a pit can go." )]
	public float VoxelDepthBelowMeters { get; set; } = 24f;
	[Property, Group( "Voxel" ), Title( "Head Room (m)" ), Range( 2f, 64f ), Step( 1f ), Description( "Field above a chunk's highest point — how tall a fill can grow." )]
	public float VoxelHeadRoomMeters { get; set; } = 8f;
	[Property, Group( "Voxel" ), Title( "Max Brush Radius (m)" ), Range( 0.5f, 8f ), Step( 0.5f ), Description( "The host refuses larger brushes (anti-cheat)." )]
	public float VoxelMaxBrushRadiusMeters { get; set; } = 4f;
	[Property, Group( "Voxel" ), Title( "Brush Flatness (0.2–1)" ), Range( 0.2f, 1f ), Step( 0.05f ), Description( "Half height of the dig / fill puck as a share of its radius (0.55 = the flat scoop a shovel takes)." )]
	public float VoxelBrushFlatness { get; set; } = 0.55f;
	[Property, Group( "Voxel" ), Title( "Save Edits" )] public bool VoxelSaveEdits { get; set; } = true;
	[Property, Group( "Voxel" ), Title( "Log Edits" )] public bool VoxelLogEdits { get; set; }

	const float VoxelCellMeters = 0.5f;
	/// <summary>Cells the field reaches past its chunk on every side: the shared ground two neighbouring columns both hold.</summary>
	const int VoxelMarginCells = 2;
	const string VoxelTag = "voxel";

	readonly Dictionary<TerrainChunkCoord, VoxelColumn> _voxelColumns = new();
	readonly List<VoxelEdit> _voxelEdits = new();
	string _voxelEditsWorld;
	Material _voxelMaterial;

	bool IVoxelBrushTarget.IsReady => VoxelReady;
	bool VoxelReady => !_isWorldLoading && _loadSettings is not null && !_waitingForHostWorld;

	float VoxelChunkSize => Math.Max( 32f, ChunkSizeMeters );

	TerrainChunkCoord VoxelCoordAt( float xMeters, float yMeters )
		=> TerrainChunkStreaming.WorldToChunkCoord( xMeters, yMeters, _loadSettings.TotalWorldRadiusMeters, VoxelChunkSize );

	void EnsureVoxelEditsLoaded()
	{
		if ( _voxelEditsWorld == WorldName )
			return;
		_voxelEditsWorld = WorldName;
		VoxelEditLog.Load( WorldName, _voxelEdits );
		if ( _voxelEdits.Count > 0 )
			Log.Info( $"[Voxel] {_voxelEdits.Count} saved edits for world '{WorldName}'" );
	}

	// ---- the surface, for the rake's plan

	/// <summary>The original meshed ground (m) under a world xy from the loaded chunk's height grid. False when no chunk is loaded there.</summary>
	bool TryHeightfieldZMeters( float xMeters, float yMeters, out float zMeters )
	{
		zMeters = 0f;
		if ( _loadSettings is null || !_loaded.TryGetValue( VoxelCoordAt( xMeters, yMeters ), out var entry ) )
			return false;
		return TryChunkMeshZMeters( entry, xMeters, yMeters, out zMeters );
	}

	/// <summary>Bilinear on one chunk's meshed height grid (clamped to the chunk), metres.</summary>
	static bool TryChunkMeshZMeters( LoadedChunk entry, float xMeters, float yMeters, out float zMeters )
	{
		zMeters = 0f;
		if ( entry?.GrassHeightsZ is null || entry.GrassHeightsSide < 2 || entry.GameObject is null || !entry.GameObject.IsValid() )
			return false;
		var heights = entry.GrassHeightsZ;
		var side = entry.GrassHeightsSide;
		var step = Math.Max( entry.GrassStepUnits, 1f );
		var origin = entry.GameObject.WorldPosition;
		var wx = TerrainWorldUnits.MetersToEngine( xMeters );
		var wy = TerrainWorldUnits.MetersToEngine( yMeters );
		var last = side - 1;
		var fx = Math.Clamp( (wx - origin.x) / step, 0f, last - 0.001f );
		var fy = Math.Clamp( (wy - origin.y) / step, 0f, last - 0.001f );
		var ix = (int)fx;
		var iy = (int)fy;
		var tx = fx - ix;
		var ty = fy - iy;
		var h00 = heights[(iy * side) + ix];
		var h10 = heights[(iy * side) + ix + 1];
		var h01 = heights[((iy + 1) * side) + ix];
		var h11 = heights[((iy + 1) * side) + ix + 1];
		var z = ((h00 * (1f - tx)) + (h10 * tx)) * (1f - ty) + ((h01 * (1f - tx)) + (h11 * tx)) * ty;
		zMeters = TerrainWorldUnits.EngineToMeters( origin.z + z );
		return true;
	}

	/// <summary>Bilinear on one chunk's splat grid (what the ground is made of), clamped to the chunk.</summary>
	static bool TryChunkSplat( LoadedChunk entry, float xMeters, float yMeters, out Color weights, out Vector2 splat )
	{
		weights = new Color( 1f, 0f, 0f, 0f );
		splat = Vector2.Zero;
		if ( entry?.SplatWeights is null || entry.SplatExtras is null || entry.GrassHeightsSide < 2 || entry.GameObject is null || !entry.GameObject.IsValid() )
			return false;
		var side = entry.GrassHeightsSide;
		if ( entry.SplatWeights.Length != side * side )
			return false;
		var step = Math.Max( entry.GrassStepUnits, 1f );
		var origin = entry.GameObject.WorldPosition;
		var wx = TerrainWorldUnits.MetersToEngine( xMeters );
		var wy = TerrainWorldUnits.MetersToEngine( yMeters );
		var last = side - 1;
		var fx = Math.Clamp( (wx - origin.x) / step, 0f, last - 0.001f );
		var fy = Math.Clamp( (wy - origin.y) / step, 0f, last - 0.001f );
		var ix = (int)fx;
		var iy = (int)fy;
		var tx = fx - ix;
		var ty = fy - iy;
		var w = entry.SplatWeights;
		var s = entry.SplatExtras;
		int i00 = (iy * side) + ix, i10 = i00 + 1, i01 = i00 + side, i11 = i01 + 1;
		Color Mix( Color a, Color b, Color c, Color d ) => new(
			(((a.r * (1f - tx)) + (b.r * tx)) * (1f - ty)) + (((c.r * (1f - tx)) + (d.r * tx)) * ty),
			(((a.g * (1f - tx)) + (b.g * tx)) * (1f - ty)) + (((c.g * (1f - tx)) + (d.g * tx)) * ty),
			(((a.b * (1f - tx)) + (b.b * tx)) * (1f - ty)) + (((c.b * (1f - tx)) + (d.b * tx)) * ty),
			(((a.a * (1f - tx)) + (b.a * tx)) * (1f - ty)) + (((c.a * (1f - tx)) + (d.a * tx)) * ty) );
		weights = Mix( w[i00], w[i10], w[i01], w[i11] );
		var sx = (((s[i00].x * (1f - tx)) + (s[i10].x * tx)) * (1f - ty)) + (((s[i01].x * (1f - tx)) + (s[i11].x * tx)) * ty);
		var sy = (((s[i00].y * (1f - tx)) + (s[i10].y * tx)) * (1f - ty)) + (((s[i01].y * (1f - tx)) + (s[i11].y * tx)) * ty);
		splat = new Vector2( sx, sy );
		return true;
	}

	/// <summary>The top of the ground at a world xy between two heights: the voxel column's surface where one stands, else the original mesh. NaN = nothing there.</summary>
	float WorldSurfaceZMeters( float xMeters, float yMeters, float zLo, float zHi )
	{
		if ( _loadSettings is null )
			return float.NaN;
		if ( _voxelColumns.TryGetValue( VoxelCoordAt( xMeters, yMeters ), out var column ) )
			return column.Set.Field.SurfaceZ( xMeters, yMeters, zLo, zHi );
		return TryHeightfieldZMeters( xMeters, yMeters, out var z ) ? z : float.NaN;
	}

	public bool TryPlanFlatten( Vector3 centerEngine, float radiusMeters, bool slope, out VoxelPlane plane )
	{
		plane = default;
		if ( !VoxelReady || !VoxelDigging )
			return false;
		var c = TerrainWorldUnits.EngineToMeters( centerEngine );
		var r = Math.Max( radiusMeters, VoxelCellMeters * 0.5f );
		var window = VoxelRake.WindowMeters( r );
		return VoxelRake.Plan( ( x, y ) => WorldSurfaceZMeters( x, y, c.z - window, c.z + window ), c, r, slope, out plane );
	}

	// ---- edits

	public void RequestBrush( Vector3 centerEngine, float radiusMeters, VoxelBrushMode mode )
	{
		var c = TerrainWorldUnits.EngineToMeters( centerEngine );
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			HostVoxelBrush( c, radiusMeters, mode );
			return;
		}
		RpcRequestVoxelBrush( c, radiusMeters, (int)mode );
	}

	[Rpc.Host]
	void RpcRequestVoxelBrush( Vector3 centerMeters, float radiusMeters, int mode )
	{
		if ( !Networking.IsHost )
			return;
		HostVoxelBrush( centerMeters, radiusMeters, (VoxelBrushMode)Math.Clamp( mode, 0, 3 ) );
	}

	/// <summary>Host rules, once per edit: digging on, a loaded chunk under the brush, a sane radius; a rake stroke is planned here once so every peer lays the same plane. Then everyone applies it.</summary>
	void HostVoxelBrush( Vector3 centerMeters, float radiusMeters, VoxelBrushMode mode )
	{
		if ( !VoxelReady || !VoxelDigging )
			return;
		var r = Math.Clamp( radiusMeters, 0.25f, VoxelMaxBrushRadiusMeters );
		if ( !_loaded.ContainsKey( VoxelCoordAt( centerMeters.x, centerMeters.y ) ) )
			return;
		VoxelEdit edit;
		if ( mode is VoxelBrushMode.Flatten or VoxelBrushMode.Slope )
		{
			var window = VoxelRake.WindowMeters( r );
			if ( !VoxelRake.Plan( ( x, y ) => WorldSurfaceZMeters( x, y, centerMeters.z - window, centerMeters.z + window ), centerMeters, r, mode == VoxelBrushMode.Slope, out var plane ) )
				return;
			edit = VoxelEdit.Rake( centerMeters, r, mode, plane );
		}
		else
			edit = VoxelEdit.Brush( centerMeters, r, mode );
		EnsureVoxelEditsLoaded();
		if ( GameObject.Network is { Active: true } )
			RpcApplyVoxelEdit( centerMeters, r, (int)mode, edit.HasPlane, edit.PlaneZ, edit.PlaneGx, edit.PlaneGy );
		else
			ApplyVoxelEditLocal( edit );
		if ( VoxelSaveEdits )
		{
			_voxelEdits.Add( edit );
			VoxelEditLog.Save( WorldName, _voxelEdits );
		}
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	void RpcApplyVoxelEdit( Vector3 centerMeters, float radiusMeters, int mode, bool hasPlane, float planeZ, float planeGx, float planeGy )
	{
		var edit = VoxelEdit.Brush( centerMeters, radiusMeters, (VoxelBrushMode)Math.Clamp( mode, 0, 3 ) );
		edit.HasPlane = hasPlane;
		edit.PlaneZ = planeZ;
		edit.PlaneGx = planeGx;
		edit.PlaneGy = planeGy;
		ApplyVoxelEditLocal( edit );
	}

	/// <summary>One stroke onto every loaded chunk it reaches: each gets its voxel column (made now if it has none) and the same brush.</summary>
	void ApplyVoxelEditLocal( VoxelEdit edit )
	{
		if ( !VoxelReady )
			return;
		var touched = 0;
		foreach ( var coord in VoxelCoordsTouched( edit ) )
		{
			if ( !_loaded.TryGetValue( coord, out var entry ) )
				continue;
			var column = GetOrCreateVoxelColumn( entry );
			if ( column is null )
				continue;
			ApplyEditToColumn( column, edit );
			touched++;
		}
		if ( VoxelLogEdits )
			Log.Info( $"[Voxel] {edit.BrushMode} r {edit.R:0.00} m at {edit.X:0.0},{edit.Y:0.0},{edit.Z:0.0} m: {touched} columns" );
	}

	/// <summary>The chunk coords whose column (square + margin) the stroke's reach overlaps.</summary>
	IEnumerable<TerrainChunkCoord> VoxelCoordsTouched( VoxelEdit edit )
	{
		var reach = edit.ReachMeters + (VoxelMarginCells * VoxelCellMeters);
		var lo = VoxelCoordAt( edit.X - reach, edit.Y - reach );
		var hi = VoxelCoordAt( edit.X + reach, edit.Y + reach );
		for ( var y = lo.Y; y <= hi.Y; y++ )
		{
			for ( var x = lo.X; x <= hi.X; x++ )
				yield return new TerrainChunkCoord( x, y );
		}
	}

	bool VoxelEditTouches( VoxelEdit edit, TerrainChunkCoord coord )
	{
		var cs = VoxelChunkSize;
		var margin = VoxelMarginCells * VoxelCellMeters;
		var minX = -_loadSettings.TotalWorldRadiusMeters + (coord.X * cs) - margin;
		var minY = -_loadSettings.TotalWorldRadiusMeters + (coord.Y * cs) - margin;
		var reach = edit.ReachMeters;
		return edit.X + reach >= minX && edit.X - reach <= minX + cs + (2f * margin)
			&& edit.Y + reach >= minY && edit.Y - reach <= minY + cs + (2f * margin);
	}

	void ApplyEditToColumn( VoxelColumn column, VoxelEdit edit )
	{
		var field = column.Set.Field;
		HashSet<(int X, int Y, int Z)> dirty;
		if ( edit.IsRake )
		{
			if ( !edit.HasPlane )
				return;   // a rake row from before planes were logged: nothing deterministic to lay
			dirty = field.ApplyPlane( edit.CenterMeters, edit.R, edit.Plane );
		}
		else
			dirty = field.ApplyBrush( edit.CenterMeters, edit.R, edit.BrushMode, VoxelBrushFlatness );
		column.Set.MarkDirty( dirty );
	}

	// ---- columns

	/// <summary>A chunk just loaded: if the log has strokes on it, its column comes back now (seeded from the fresh mesh, strokes replayed).</summary>
	void TryRestoreVoxelColumn( LoadedChunk entry )
	{
		if ( !VoxelDigging || _loadSettings is null )
			return;
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;   // clients get their strokes from the host's broadcasts
		EnsureVoxelEditsLoaded();
		if ( _voxelEdits.Count == 0 )
			return;
		foreach ( var edit in _voxelEdits )
		{
			if ( VoxelEditTouches( edit, entry.Coord ) )
			{
				GetOrCreateVoxelColumn( entry );
				return;
			}
		}
	}

	/// <summary>
	/// The chunk's voxel column, made on first touch: the field is seeded from the chunk's own meshed heights (neighbours'
	/// meshes for the margin, the sampler where none is loaded yet), its splat from the same vertices, then every logged
	/// stroke that reaches it is replayed. Synchronous — a few milliseconds once per chunk; the meshes follow on workers.
	/// </summary>
	VoxelColumn GetOrCreateVoxelColumn( LoadedChunk entry )
	{
		if ( entry.Voxel is not null )
			return entry.Voxel;
		if ( _loadSettings is null || entry.GrassHeightsZ is null || entry.GameObject is null || !entry.GameObject.IsValid() )
			return null;
		_voxelMaterial ??= Material.Load( "materials/terrain/terrain_ground.vmat" );
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var cs = VoxelChunkSize;
		var cell = VoxelCellMeters;
		var cellsXy = (int)MathF.Round( cs / cell );
		var margin = VoxelMarginCells;
		var side = cellsXy + (2 * margin);
		var samples = side + 1;
		var minX = -_loadSettings.TotalWorldRadiusMeters + (entry.Coord.X * cs);
		var minY = -_loadSettings.TotalWorldRadiusMeters + (entry.Coord.Y * cs);
		var originX = minX - (margin * cell);
		var originY = minY - (margin * cell);

		// Bake the seed surface and the splat per sample column (main thread: reads the loaded chunks), so the field seed
		// and the worker-side mesher only ever read these arrays
		var heights = new float[samples * samples];
		var weights = new Color[samples * samples];
		var splats = new Vector2[samples * samples];
		var minZ = float.MaxValue;
		var maxZ = float.MinValue;
		var floor = _loadSettings.SeaLevelMeters - Math.Max( 0f, _loadSettings.OceanMaxDepthMeters );
		for ( var iy = 0; iy < samples; iy++ )
		{
			for ( var ix = 0; ix < samples; ix++ )
			{
				var x = originX + (ix * cell);
				var y = originY + (iy * cell);
				var i = (iy * samples) + ix;
				var inside = ix >= margin && iy >= margin && ix <= margin + cellsXy && iy <= margin + cellsXy;
				var src = inside ? entry : (_loaded.TryGetValue( VoxelCoordAt( x, y ), out var other ) ? other : null);
				float z;
				if ( src is not null && TryChunkMeshZMeters( src, x, y, out z ) )
				{
				}
				else if ( _backend is not null )
				{
					var sample = _backend.Sample( _loadSettings, x, y );
					z = sample.IsInsideWorld ? sample.HeightMeters : floor;
				}
				else
					TryChunkMeshZMeters( entry, x, y, out z );
				heights[i] = z;
				minZ = Math.Min( minZ, z );
				maxZ = Math.Max( maxZ, z );
				if ( !TryChunkSplat( src, x, y, out weights[i], out splats[i] ) )
					TryChunkSplat( entry, x, y, out weights[i], out splats[i] );
			}
		}
		var originZ = MathF.Floor( (minZ - VoxelDepthBelowMeters) / cell ) * cell;
		var cellsZ = Math.Max( 4, (int)MathF.Ceiling( (maxZ + VoxelHeadRoomMeters - originZ) / cell ) );
		var field = new VoxelField( cell, side, side, cellsZ, new Vector3( originX, originY, originZ ) );
		field.SeedFromHeights( ( x, y ) =>
		{
			var ix = Math.Clamp( (int)MathF.Round( (x - originX) / cell ), 0, samples - 1 );
			var iy = Math.Clamp( (int)MathF.Round( (y - originY) / cell ), 0, samples - 1 );
			return heights[(iy * samples) + ix];
		} );

		var root = new GameObject( true, $"VoxelColumn {entry.Coord}" );
		root.Flags |= GameObjectFlags.NotSaved;
		root.Parent = GameObject;
		var set = new VoxelChunkSet( field, root, _voxelMaterial, "voxel", VoxelTag, TerrainTag )
		{
			Clip = new VoxelMesher.Clip( margin, margin, margin + cellsXy, margin + cellsXy ),
			Style = ( float x, float y, out Color w, out Vector2 s ) =>
			{
				var ix = Math.Clamp( (int)MathF.Round( (x - originX) / cell ), 0, samples - 1 );
				var iy = Math.Clamp( (int)MathF.Round( (y - originY) / cell ), 0, samples - 1 );
				w = weights[(iy * samples) + ix];
				s = splats[(iy * samples) + ix];
			},
		};
		set.CreateAllChunks();
		var column = new VoxelColumn { Coord = entry.Coord, Chunk = entry, Root = root, Set = set };
		entry.Voxel = column;
		_voxelColumns[entry.Coord] = column;

		var replayed = 0;
		foreach ( var edit in _voxelEdits )
		{
			if ( !VoxelEditTouches( edit, entry.Coord ) )
				continue;
			ApplyEditToColumn( column, edit );
			replayed++;
		}
		Log.Info( $"[Voxel] column {entry.Coord}: {field.SamplesX}×{field.SamplesY}×{field.SamplesZ} samples, {field.ChunksX}×{field.ChunksY}×{field.ChunksZ} chunks, ground {minZ:0.#}…{maxZ:0.#} m, {replayed} strokes replayed, seeded in {sw.Elapsed.TotalMilliseconds:0.0} ms" );
		return column;
	}

	/// <summary>Every frame: land voxel meshes; a column whose first meshes are all in takes over from its heightfield chunk.</summary>
	void TickVoxelColumns()
	{
		if ( _voxelColumns.Count == 0 )
			return;
		foreach ( var column in _voxelColumns.Values )
		{
			column.Set.Tick();
			if ( !column.Swapped && column.Set.Settled )
				SwapChunkToVoxel( column );
		}
	}

	void SwapChunkToVoxel( VoxelColumn column )
	{
		column.Swapped = true;
		var entry = column.Chunk;
		if ( entry.GameObject is null || !entry.GameObject.IsValid() )
			return;
		var renderer = entry.GameObject.Components.Get<ModelRenderer>( true );   // includeDisabled: the swapped chunk's renderer is off
		if ( renderer is not null && renderer.IsValid() )
			renderer.Enabled = false;
		if ( entry.Collider is not null && entry.Collider.IsValid() )
			entry.Collider.Enabled = false;   // the voxel chunks' colliders stand in; tree colliders keep following the range
		NotifyGrassChunkChanged( entry );   // the grass tiles over it regenerate without clumps (SampleGrassGround skips swapped chunks)
	}

	/// <summary>Put the heightfield chunk back (reset): renderer on, collider by range on the next pass, grass back.</summary>
	void RestoreChunkFromVoxel( VoxelColumn column )
	{
		var entry = column.Chunk;
		if ( entry.GameObject is null || !entry.GameObject.IsValid() )
			return;
		var renderer = entry.GameObject.Components.Get<ModelRenderer>( true );   // includeDisabled: the swapped chunk's renderer is off
		if ( renderer is not null && renderer.IsValid() )
			renderer.Enabled = true;
		_colliderPassPending = true;
		NotifyGrassChunkChanged( entry );
	}

	void DestroyVoxelColumn( TerrainChunkCoord coord, bool restoreChunk )
	{
		if ( !_voxelColumns.TryGetValue( coord, out var column ) )
			return;
		_voxelColumns.Remove( coord );
		if ( column.Chunk is not null )
			column.Chunk.Voxel = null;
		if ( restoreChunk && column.Swapped )
			RestoreChunkFromVoxel( column );
		column.Set.Destroy();
		if ( column.Root is not null && column.Root.IsValid() )
			column.Root.Destroy();
	}

	void DestroyAllVoxelColumns()
	{
		foreach ( var coord in _voxelColumns.Keys.ToArray() )
			DestroyVoxelColumn( coord, restoreChunk: false );
	}

	public string DescribeVoxels()
	{
		if ( !VoxelReady )
			return "[Voxel] world still loading";
		EnsureVoxelEditsLoaded();
		var swapped = _voxelColumns.Values.Count( c => c.Swapped );
		var vertices = _voxelColumns.Values.Sum( c => c.Set.TotalVertices );
		var queue = _voxelColumns.Values.Sum( c => c.Set.QueueCount );
		var building = _voxelColumns.Values.Sum( c => c.Set.BuildingCount );
		var slowest = _voxelColumns.Values.Select( c => c.Set.LastBuildMs ).DefaultIfEmpty( 0f ).Max();
		return $"[Voxel] world '{WorldName}': {_voxelColumns.Count} voxel columns ({swapped} swapped in for their chunk), {vertices} vertices, {_voxelEdits.Count} edits saved; queue {queue}, building {building}; last build {slowest:0.0} ms (worker); digging {(VoxelDigging ? "on" : "off")}, cell {VoxelCellMeters} m, {VoxelDepthBelowMeters:0} m deep";
	}

	public void ResetVoxels()
	{
		if ( !VoxelReady )
			return;
		EnsureVoxelEditsLoaded();
		_voxelEdits.Clear();
		VoxelEditLog.Save( WorldName, _voxelEdits );
		var n = _voxelColumns.Count;
		foreach ( var coord in _voxelColumns.Keys.ToArray() )
			DestroyVoxelColumn( coord, restoreChunk: true );
		Log.Info( $"[Voxel] reset: edits cleared, {n} columns dropped, heightfield chunks back" );
	}
}
