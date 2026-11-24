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
/// <para>
/// Redwood cliffs (2026-11-16) are columns too: a chunk holding a ledge cliff within <see cref="CliffColumnRangeMeters"/> of
/// the camera becomes voxel ground with the cliff carved in (<see cref="TerrainLedgeCliffs.Sculpt"/>, between the seed and
/// the stroke replay), so the vertical face and its overhang are ground you can dig. Past the range (and with no strokes on
/// it) the column goes and the heightfield's scarp stands in. Where a column borders a chunk that is still heightfield the
/// carve fades back to the scarp over the last 6 m (<see cref="VoxelSeed.FadeSides"/>), so the two meet without a seam; the
/// column is rebuilt without that fade once the neighbour is voxel ground too.
/// </para>
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
		/// <summary>Stroke circles (x, y, reach in metres) applied before the swap: the grass regenerates there when it lands.</summary>
		public readonly List<Vector3> GrassAreas = new();
		/// <summary>The chunk LOD (vertices per side) the seed was read from: a cliff column is rebuilt when its chunk refines.</summary>
		public int SeedVerts;
		/// <summary>The column this one replaces (a rebuild): it stays drawn until this one's meshes have all landed.</summary>
		public VoxelColumn Old;
		/// <summary>Borders (<see cref="TerrainMeshBuilder.BorderMinY"/>…) where the cliff carve fades out: the neighbour was heightfield.</summary>
		public int FadeSides;
		/// <summary>The lowest seed ground of the column (m): how deep its field may grow is measured from here.</summary>
		public float MinGroundZ;
		public float ChunkMinX, ChunkMinY;
		/// <summary>The loot rooms carved into the field so far (TerrainWorldManager.Underground.cs).</summary>
		public List<CarvedRoom> Rooms = new();
	}

	/// <summary>Cliff chunks within this distance of the camera (chunk centre, metres) are voxel ground with the cliff carved in.</summary>
	const float CliffColumnRangeMeters = 128f;
	/// <summary>A cliff column with no strokes goes back to heightfield this far past the range.</summary>
	const float CliffColumnDropSlackMeters = 48f;
	/// <summary>Cliff columns building on workers at once.</summary>
	const int CliffColumnsInFlight = 2;
	float _cliffColumnTimer;
	/// <summary>The chunks the last <see cref="TickCliffColumns"/> wanted as cliff columns (a seed fades its carve toward any other neighbour).</summary>
	readonly HashSet<TerrainChunkCoord> _cliffWanted = new();

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

	/// <summary>
	/// A loaded chunk's meshed grids (heights, splat), taken by reference on the main thread so a worker can sample them: a
	/// remesh replaces the arrays, it never writes into them. Reading the chunk object's position for every one of a seed's
	/// 17 700 samples was most of the 6–17 ms a column's seed cost the main thread (2026-11-16).
	/// </summary>
	sealed class ChunkGrid
	{
		public float[] Heights;
		public Color[] Weights;
		public Vector4[] Extras;
		public int Side;
		public float StepUnits;
		public Vector3 Origin;
	}

	static ChunkGrid SnapshotGrid( LoadedChunk entry )
	{
		if ( entry?.GrassHeightsZ is null || entry.GrassHeightsSide < 2 || entry.GameObject is null || !entry.GameObject.IsValid() )
			return null;
		return new ChunkGrid
		{
			Heights = entry.GrassHeightsZ,
			Weights = entry.SplatWeights,
			Extras = entry.SplatExtras,
			Side = entry.GrassHeightsSide,
			StepUnits = Math.Max( entry.GrassStepUnits, 1f ),
			Origin = entry.GameObject.WorldPosition,
		};
	}

	/// <summary>Bilinear on one chunk's meshed height grid (clamped to the chunk), metres.</summary>
	static bool TryChunkMeshZMeters( LoadedChunk entry, float xMeters, float yMeters, out float zMeters )
		=> TryGridZMeters( SnapshotGrid( entry ), xMeters, yMeters, out zMeters );

	/// <summary>Bilinear on one chunk's splat grid (what the ground is made of), clamped to the chunk.</summary>
	static bool TryChunkSplat( LoadedChunk entry, float xMeters, float yMeters, out Color weights, out Vector2 splat )
		=> TryGridSplat( SnapshotGrid( entry ), xMeters, yMeters, out weights, out splat );

	static bool TryGridZMeters( ChunkGrid g, float xMeters, float yMeters, out float zMeters )
	{
		zMeters = 0f;
		if ( g is null )
			return false;
		var heights = g.Heights;
		var side = g.Side;
		var step = g.StepUnits;
		var origin = g.Origin;
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

	static bool TryGridSplat( ChunkGrid g, float xMeters, float yMeters, out Color weights, out Vector2 splat )
	{
		weights = new Color( 1f, 0f, 0f, 0f );
		splat = Vector2.Zero;
		if ( g?.Weights is null || g.Extras is null )
			return false;
		var side = g.Side;
		if ( g.Weights.Length != side * side )
			return false;
		var step = g.StepUnits;
		var origin = g.Origin;
		var wx = TerrainWorldUnits.MetersToEngine( xMeters );
		var wy = TerrainWorldUnits.MetersToEngine( yMeters );
		var last = side - 1;
		var fx = Math.Clamp( (wx - origin.x) / step, 0f, last - 0.001f );
		var fy = Math.Clamp( (wy - origin.y) / step, 0f, last - 0.001f );
		var ix = (int)fx;
		var iy = (int)fy;
		var tx = fx - ix;
		var ty = fy - iy;
		var w = g.Weights;
		var s = g.Extras;
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

	public bool RequestBrush( Vector3 centerEngine, float radiusMeters, VoxelBrushMode mode, bool square = false, float depth = 0f )
		=> Request( VoxelEdit.Brush( TerrainWorldUnits.EngineToMeters( centerEngine ), radiusMeters, mode, square, depth ) );

	public float TilledAt( Vector3 pointEngine )
	{
		if ( !VoxelReady || _loadSettings is null )
			return 0f;
		var m = TerrainWorldUnits.EngineToMeters( pointEngine );
		if ( _voxelColumns.TryGetValue( VoxelCoordAt( m.x, m.y ), out var column ) )
			return column.Set.Field.TilledAt( m.x, m.y );
		// Undug ground: the generated tilled beds (wild wheat) on the loaded heightfield chunk
		return _loaded.TryGetValue( VoxelCoordAt( m.x, m.y ), out var entry ) ? TerrainGroundPatch.TilledCoverageAt( entry.GroundPatches, m.x, m.y ) : 0f;
	}

	public float RockAt( Vector3 pointEngine )
	{
		if ( !VoxelReady || _loadSettings is null )
			return 0f;
		var m = TerrainWorldUnits.EngineToMeters( pointEngine );
		if ( _voxelColumns.TryGetValue( VoxelCoordAt( m.x, m.y ), out var column ) )
			return VoxelMesher.RockAt( column.Set.Field, column.Set.Style, m );
		// Undug heightfield: the chunk's own splat (mountain rock in A)
		return _loaded.TryGetValue( VoxelCoordAt( m.x, m.y ), out var entry ) && TryChunkSplat( entry, m.x, m.y, out var weights, out _ ) ? weights.a : 0f;
	}

	public bool RequestChip( Vector3 centerEngine, Vector3 normal, float radiusMeters, float depthMeters )
		=> Request( VoxelEdit.Chip( TerrainWorldUnits.EngineToMeters( centerEngine ), normal, radiusMeters, depthMeters ) );

	bool Request( VoxelEdit request )
	{
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
			return HostVoxelBrush( request );
		RpcRequestVoxelBrush( request.CenterMeters, request.R, request.Mode, request.Normal, request.D, request.Sq );
		return true;
	}

	[Rpc.Host]
	void RpcRequestVoxelBrush( Vector3 centerMeters, float radiusMeters, int mode, Vector3 normal, float depth, bool square )
	{
		if ( !Networking.IsHost )
			return;
		HostVoxelBrush( VoxelEdit.FromWire( centerMeters, radiusMeters, mode, normal, depth, square ) );
	}

	/// <summary>Host rules, once per edit: digging on, a loaded chunk under the brush, a sane radius (and chip depth); a rake stroke is planned here once so every peer lays the same plane. Then everyone applies it. True when the stroke was made.</summary>
	bool HostVoxelBrush( VoxelEdit request )
	{
		if ( !VoxelReady || !VoxelDigging )
			return false;
		var centerMeters = request.CenterMeters;
		var r = Math.Clamp( request.R, 0.25f, VoxelMaxBrushRadiusMeters );
		if ( !_loaded.ContainsKey( VoxelCoordAt( centerMeters.x, centerMeters.y ) ) )
			return false;
		VoxelEdit edit;
		if ( request.IsRake )
		{
			var window = VoxelRake.WindowMeters( r );
			if ( !VoxelRake.Plan( ( x, y ) => WorldSurfaceZMeters( x, y, centerMeters.z - window, centerMeters.z + window ), centerMeters, r, request.BrushMode == VoxelBrushMode.Slope, out var plane ) )
				return false;
			edit = VoxelEdit.Rake( centerMeters, r, request.BrushMode, plane );
		}
		else if ( request.IsChip )
			edit = VoxelEdit.Chip( centerMeters, request.Normal, r, Math.Clamp( request.D, 0.05f, r ) );
		else
			edit = VoxelEdit.Brush( centerMeters, r, request.BrushMode, request.Sq, Math.Clamp( request.D, 0.05f, 1f ) );
		EnsureVoxelEditsLoaded();
		if ( GameObject.Network is { Active: true } )
			RpcApplyVoxelEdit( centerMeters, r, edit.Mode, edit.HasPlane, edit.PlaneZ, edit.PlaneGx, edit.PlaneGy, edit.Normal, edit.D, edit.Sq );
		else
			ApplyVoxelEditLocal( edit );
		if ( VoxelSaveEdits )
		{
			_voxelEdits.Add( edit );
			VoxelEditLog.Save( WorldName, _voxelEdits );
		}
		return true;
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	void RpcApplyVoxelEdit( Vector3 centerMeters, float radiusMeters, int mode, bool hasPlane, float planeZ, float planeGx, float planeGy, Vector3 normal, float depth, bool square )
	{
		var edit = VoxelEdit.FromWire( centerMeters, radiusMeters, mode, normal, depth, square );
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
			_pendingVoxelColumns.Remove( coord );   // a rebuild on its worker was seeded without this stroke
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
		EnsureColumnDepthFor( column, edit );   // a stroke near the floor grows the field down first
		column.Set.MarkDirty( VoxelReplay.Apply( column.Set.Field, edit, VoxelBrushFlatness ) );
		if ( column.Swapped )
			NotifyGrassAreaChanged( edit.X, edit.Y, edit.ReachMeters );   // the clumps over the stroke follow the new ground / paint (bare = none)
		else
			column.GrassAreas.Add( new Vector3( edit.X, edit.Y, edit.ReachMeters ) );
	}

	// ---- columns

	/// <summary>The inputs of one column's field, baked on the main thread from the loaded chunks; NaN heights are sampled on the worker.</summary>
	sealed class VoxelSeed
	{
		public TerrainChunkCoord Coord;
		public int Side, Samples, CellsXy, Margin;
		public float Cell, OriginX, OriginY, Floor;
		public float[] Heights;
		public Color[] Weights;
		public Vector2[] Splats;
		public TerrainGroundPatch[] Patches;
		public VoxelEdit[] Edits;
		/// <summary>Carve the Redwood cliffs into the field (<see cref="TerrainLedgeCliffs.Sculpt"/>): this chunk, or a neighbour's cliff band, reaches it.</summary>
		public bool Cliff;
		public int Verts;
		/// <summary>Borders facing a chunk that is not voxel ground: the cliff carve fades to nothing within 6 m of them.</summary>
		public int FadeSides;
		public float ChunkMinX, ChunkMinY, ChunkSize;
		/// <summary>The chunk's and its eight neighbours' grids ([dx + 1 + 3 (dy + 1)], null = not loaded): sampled on the worker.</summary>
		public ChunkGrid[] Grids;
		public bool SampleMissing;
		public float Flatness, DepthBelow, HeadRoom;
		public float MainMs;
		public VoxelField Field;
		public float MinZ, MaxZ, WorkerMs;
		/// <summary>The world seed for the underground's pockets and rooms; null = the underground is off.</summary>
		public int? UndergroundSeed;
		/// <summary>The loot rooms the build carved (worker), handed to the column when it lands.</summary>
		public List<CarvedRoom> Rooms = new();
	}

	sealed class PendingVoxelColumn
	{
		public LoadedChunk Entry;
		public VoxelSeed Seed;
		public System.Threading.Tasks.Task Task;
		/// <summary>Replaces the chunk's standing column when it lands (a cliff column rebuilt at the chunk's new LOD).</summary>
		public bool Replace;
	}

	/// <summary>Columns coming back on a worker after their chunk loaded (field seeded, strokes replayed off the main thread).</summary>
	readonly Dictionary<TerrainChunkCoord, PendingVoxelColumn> _pendingVoxelColumns = new();

	/// <summary>
	/// A chunk just loaded: if the log has strokes on it, its column comes back — the field is seeded and the strokes replayed on
	/// a worker (a dug chunk replayed 222 strokes in 2.1 s on the main thread, 2026-11-03), then it lands in <see cref="TickVoxelColumns"/>.
	/// </summary>
	void TryRestoreVoxelColumn( LoadedChunk entry )
	{
		if ( !VoxelDigging || _loadSettings is null || entry.Voxel is not null || _pendingVoxelColumns.ContainsKey( entry.Coord ) )
			return;
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;   // clients get their strokes from the host's broadcasts
		EnsureVoxelEditsLoaded();
		if ( _voxelEdits.Count == 0 || !AnyVoxelEditTouches( entry.Coord ) )
			return;   // the strokes test first: building the seed (~4 ms of grid reads) for every streamed chunk was 0.7 ms/frame flying (2026-11-04)
		var seed = PrepareVoxelSeed( entry );
		if ( seed is null || seed.Edits.Length == 0 )
			return;
		var settings = _loadSettings;
		var backend = _backend;
		_pendingVoxelColumns[entry.Coord] = new PendingVoxelColumn
		{
			Entry = entry,
			Seed = seed,
			Task = GameTask.RunInThreadAsync( () => BuildVoxelField( seed, settings, backend ) ),
		};
	}

	/// <summary>
	/// The chunk's voxel column, made now (a stroke on a chunk that has none): the same seed and replay as a restore, inline.
	/// A restore still on its worker is dropped and redone here, so the stroke lands on a complete field.
	/// </summary>
	VoxelColumn GetOrCreateVoxelColumn( LoadedChunk entry )
	{
		if ( entry.Voxel is not null )
			return entry.Voxel;
		_pendingVoxelColumns.Remove( entry.Coord );
		var seed = PrepareVoxelSeed( entry );
		if ( seed is null )
			return null;
		BuildVoxelField( seed, _loadSettings, _backend );
		return LandVoxelColumn( entry, seed );
	}

	/// <summary>
	/// Main thread: the seed surface and the splat per sample column, read from the chunk's own meshed heights (neighbours'
	/// meshes for the margin; NaN where none is loaded, for the worker to sample), and the logged strokes that reach it.
	/// </summary>
	bool AnyVoxelEditTouches( TerrainChunkCoord coord )
	{
		foreach ( var e in _voxelEdits )
		{
			if ( VoxelEditTouches( e, coord ) )
				return true;
		}
		return false;
	}

	VoxelSeed PrepareVoxelSeed( LoadedChunk entry )
	{
		if ( _loadSettings is null || entry.GrassHeightsZ is null || entry.GameObject is null || !entry.GameObject.IsValid() )
			return null;
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var cs = VoxelChunkSize;
		var cell = VoxelCellMeters;
		var cellsXy = (int)MathF.Round( cs / cell );
		var margin = VoxelMarginCells;
		var side = cellsXy + (2 * margin);
		var samples = side + 1;
		var minX = -_loadSettings.TotalWorldRadiusMeters + (entry.Coord.X * cs);
		var minY = -_loadSettings.TotalWorldRadiusMeters + (entry.Coord.Y * cs);
		var seed = new VoxelSeed
		{
			Coord = entry.Coord,
			Side = side,
			Samples = samples,
			CellsXy = cellsXy,
			Margin = margin,
			Cell = cell,
			OriginX = minX - (margin * cell),
			OriginY = minY - (margin * cell),
			Floor = _loadSettings.SeaLevelMeters - Math.Max( 0f, _loadSettings.OceanMaxDepthMeters ),
			Heights = new float[samples * samples],
			Weights = new Color[samples * samples],
			Splats = new Vector2[samples * samples],
			Patches = entry.GroundPatches,
			Edits = _voxelEdits.Where( e => VoxelEditTouches( e, entry.Coord ) ).ToArray(),
			Flatness = VoxelBrushFlatness,
			DepthBelow = VoxelDepthBelowMeters,
			HeadRoom = VoxelHeadRoomMeters,
			Cliff = CliffReaches( entry ),
			Verts = entry.VerticesPerSide,
			FadeSides = HeightfieldSides( entry.Coord ),
			ChunkMinX = minX,
			ChunkMinY = minY,
			ChunkSize = cs,
			Grids = new ChunkGrid[9],
			SampleMissing = _backend is not null,
			UndergroundSeed = UndergroundSeedOrNull,
		};
		Underground.EnsureLoaded();   // main thread: the worker only reads it
		for ( var dy = -1; dy <= 1; dy++ )
		{
			for ( var dx = -1; dx <= 1; dx++ )
			{
				var c = new TerrainChunkCoord( entry.Coord.X + dx, entry.Coord.Y + dy );
				seed.Grids[(dx + 1) + (3 * (dy + 1))] = _loaded.TryGetValue( c, out var e ) ? SnapshotGrid( e ) : null;
			}
		}
		seed.MainMs = (float)sw.Elapsed.TotalMilliseconds;
		return seed;
	}

	/// <summary>Worker-safe: each sample column's seed height and splat from the chunk's own grid (inside) or the neighbour's (margin).</summary>
	static void FillSeedSamples( VoxelSeed seed )
	{
		var samples = seed.Samples;
		var own = seed.Grids[4];
		for ( var iy = 0; iy < samples; iy++ )
		{
			for ( var ix = 0; ix < samples; ix++ )
			{
				var x = seed.OriginX + (ix * seed.Cell);
				var y = seed.OriginY + (iy * seed.Cell);
				var i = (iy * samples) + ix;
				var inside = ix >= seed.Margin && iy >= seed.Margin && ix <= seed.Margin + seed.CellsXy && iy <= seed.Margin + seed.CellsXy;
				var src = own;
				if ( !inside )
				{
					var dx = Math.Clamp( (int)MathF.Floor( (x - seed.ChunkMinX) / seed.ChunkSize ), -1, 1 );
					var dy = Math.Clamp( (int)MathF.Floor( (y - seed.ChunkMinY) / seed.ChunkSize ), -1, 1 );
					src = seed.Grids[(dx + 1) + (3 * (dy + 1))];
				}
				if ( TryGridZMeters( src, x, y, out var z ) )
					seed.Heights[i] = z;
				else if ( seed.SampleMissing )
					seed.Heights[i] = float.NaN;
				else
				{
					TryGridZMeters( own, x, y, out z );
					seed.Heights[i] = z;
				}
				if ( !TryGridSplat( src, x, y, out seed.Weights[i], out seed.Splats[i] ) )
					TryGridSplat( own, x, y, out seed.Weights[i], out seed.Splats[i] );
			}
		}
	}

	/// <summary>Worker-safe: sample the missing margin heights, seed the field from the heights (and the wild-wheat beds), replay the strokes.</summary>
	static void BuildVoxelField( VoxelSeed seed, TerrainPreviewSettings settings, ITerrainPreviewBackend backend )
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		FillSeedSamples( seed );
		var heights = seed.Heights;
		var samples = seed.Samples;
		var minZ = float.MaxValue;
		var maxZ = float.MinValue;
		for ( var i = 0; i < heights.Length; i++ )
		{
			if ( float.IsNaN( heights[i] ) )
			{
				var sample = backend.Sample( settings, seed.OriginX + ((i % samples) * seed.Cell), seed.OriginY + ((i / samples) * seed.Cell) );
				heights[i] = sample.IsInsideWorld ? sample.HeightMeters : seed.Floor;
			}
			minZ = Math.Min( minZ, heights[i] );
			maxZ = Math.Max( maxZ, heights[i] );
		}
		var cell = seed.Cell;
		var originZ = MathF.Floor( (minZ - seed.DepthBelow) / cell ) * cell;
		// Strokes that went deep: the field starts as deep as the live column had grown (the same rule, so the same lattice)
		originZ = UndergroundOriginZ( originZ, minZ, seed.Edits, cell, seed.UndergroundSeed is not null );
		var cellsZ = Math.Max( 4, (int)MathF.Ceiling( (maxZ + seed.HeadRoom - originZ) / cell ) );
		var field = new VoxelField( cell, seed.Side, seed.Side, cellsZ, new Vector3( seed.OriginX, seed.OriginY, originZ ) )
		{
			UndergroundSeed = seed.UndergroundSeed,
			BedrockDepthMeters = seed.UndergroundSeed is not null ? Underground.BedrockDepthMeters : float.PositiveInfinity,
		};
		field.SeedFromHeights( ( x, y ) =>
		{
			var ix = Math.Clamp( (int)MathF.Round( (x - seed.OriginX) / cell ), 0, samples - 1 );
			var iy = Math.Clamp( (int)MathF.Round( (y - seed.OriginY) / cell ), 0, samples - 1 );
			return heights[(iy * samples) + ix];
		} );
		if ( seed.Patches is { } patches )
			field.SeedTilled( ( x, y ) => TerrainGroundPatch.TilledCoverageAt( patches, x, y ) );   // the wild-wheat beds become real tilled paint
		if ( seed.Cliff )
			TerrainLedgeCliffs.Sculpt( field, settings, backend, seed.ChunkMinX, seed.ChunkMinY, seed.ChunkSize, seed.FadeSides );   // the heightfield was the first pass: the cliffs are carved over it
		if ( seed.UndergroundSeed is { } undergroundSeed )
			CarveUndergroundRooms( field, undergroundSeed, seed.Coord, seed.ChunkMinX, seed.ChunkMinY, seed.ChunkSize, seed.Rooms );   // before the strokes: a digger breaks INTO a room
		foreach ( var edit in seed.Edits )
			VoxelReplay.Apply( field, edit, seed.Flatness );
		seed.Field = field;
		seed.MinZ = minZ;
		seed.MaxZ = maxZ;
		seed.WorkerMs = (float)sw.Elapsed.TotalMilliseconds;
	}

	/// <summary>Main thread: the column's objects over the finished field; every chunk mesh is queued (the replayed strokes are in the field already).</summary>
	VoxelColumn LandVoxelColumn( LoadedChunk entry, VoxelSeed seed )
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		_voxelMaterial ??= Material.Load( "materials/terrain/terrain_ground.vmat" );
		var field = seed.Field;
		var root = new GameObject( true, $"VoxelColumn {entry.Coord}" );
		root.Flags |= GameObjectFlags.NotSaved;
		root.Parent = GameObject;
		var samples = seed.Samples;
		var weights = seed.Weights;
		var splats = seed.Splats;
		var set = new VoxelChunkSet( field, root, _voxelMaterial, "voxel", VoxelTag, TerrainTag )
		{
			Clip = new VoxelMesher.Clip( seed.Margin, seed.Margin, seed.Margin + seed.CellsXy, seed.Margin + seed.CellsXy ),
			Style = ( float x, float y, out Color w, out Vector2 s ) =>
			{
				var ix = Math.Clamp( (int)MathF.Round( (x - seed.OriginX) / seed.Cell ), 0, samples - 1 );
				var iy = Math.Clamp( (int)MathF.Round( (y - seed.OriginY) / seed.Cell ), 0, samples - 1 );
				w = weights[(iy * samples) + ix];
				s = splats[(iy * samples) + ix];
			},
		};
		set.CreateAllChunks();
		var column = new VoxelColumn
		{
			Coord = entry.Coord, Chunk = entry, Root = root, Set = set, SeedVerts = seed.Verts, FadeSides = seed.Cliff ? seed.FadeSides : 0,
			MinGroundZ = seed.MinZ, ChunkMinX = seed.ChunkMinX, ChunkMinY = seed.ChunkMinY, Rooms = seed.Rooms,
		};
		HostSpawnRoomChests( column );
		foreach ( var edit in seed.Edits )
			column.GrassAreas.Add( new Vector3( edit.X, edit.Y, edit.ReachMeters ) );
		entry.Voxel = column;
		_voxelColumns[entry.Coord] = column;
		Log.Info( $"[Voxel] column {entry.Coord}{(seed.Cliff ? " (cliff)" : "")}: {field.SamplesX}×{field.SamplesY}×{field.SamplesZ} samples, {field.ChunksX}×{field.ChunksY}×{field.ChunksZ} chunks, ground {seed.MinZ:0.#}…{seed.MaxZ:0.#} m, {seed.Edits.Length} strokes replayed; main thread {seed.MainMs + (float)sw.Elapsed.TotalMilliseconds:0.0} ms, field {seed.WorkerMs:0.0} ms" );
		return column;
	}

	/// <summary>Every frame: columns whose worker finished land (if their chunk is still the one loaded and nothing made one meanwhile).</summary>
	void LandPendingVoxelColumns()
	{
		if ( _pendingVoxelColumns.Count == 0 )
			return;
		List<TerrainChunkCoord> done = null;
		foreach ( var (coord, pending) in _pendingVoxelColumns )
		{
			if ( !pending.Task.IsCompleted )
				continue;
			(done ??= new()).Add( coord );
			if ( pending.Task.IsFaulted )
			{
				Log.Warning( $"[Voxel] column {coord} restore failed: {pending.Task.Exception?.InnerException?.Message}" );
				continue;
			}
			if ( !_loaded.TryGetValue( coord, out var entry ) || entry != pending.Entry || pending.Seed.Field is null )
				continue;
			if ( entry.Voxel is null )
				LandVoxelColumn( entry, pending.Seed );
			else if ( pending.Replace && entry.Voxel.Old is null )
			{
				// A rebuild: the old column keeps drawing until the new one's meshes have all landed (TickVoxelColumns)
				var old = entry.Voxel;
				var fresh = LandVoxelColumn( entry, pending.Seed );
				fresh.Old = old;
			}
		}
		if ( done is null )
			return;
		foreach ( var coord in done )
			_pendingVoxelColumns.Remove( coord );
	}

	/// <summary>Every frame: land voxel meshes; a column whose first meshes are all in takes over from its heightfield chunk.</summary>
	void TickVoxelColumns()
	{
		TickCliffColumns();
		LandPendingVoxelColumns();
		TickUndergroundRooms();
		if ( _voxelColumns.Count == 0 )
			return;
		foreach ( var column in _voxelColumns.Values )
		{
			column.Set.Tick();
			if ( !column.Swapped && column.Set.Settled )
				SwapChunkToVoxel( column );
			if ( column.Old is not null && column.Set.Settled )
			{
				DestroyColumnObjects( column.Old );
				column.Old = null;
			}
		}
	}

	/// <summary>
	/// Four times a second: the chunks a cliff band reaches within <see cref="CliffColumnRangeMeters"/> get a column built on a
	/// worker, nearest first; a column is rebuilt (the old one drawing until the new one has landed) when its chunk refined
	/// past the LOD it was seeded from, or when a neighbour it faded toward is voxel ground now; a column out of range with no
	/// strokes on it goes and its heightfield comes back.
	/// </summary>
	void TickCliffColumns()
	{
		_cliffColumnTimer -= Time.Delta;
		if ( _cliffColumnTimer > 0f )
			return;
		_cliffColumnTimer = 0.25f;
		if ( !VoxelDigging || !VoxelReady || !TryGetStreamTransform( out var streamEngine, out _ ) )
			return;
		var stream = TerrainWorldUnits.EngineToMeters( streamEngine );
		var cs = VoxelChunkSize;
		var radius = _loadSettings.TotalWorldRadiusMeters;
		float Distance( TerrainChunkCoord c )
			=> new Vector2( -radius + ((c.X + 0.5f) * cs) - stream.x, -radius + ((c.Y + 0.5f) * cs) - stream.y ).Length;

		_cliffWanted.Clear();
		foreach ( var (coord, entry) in _loaded )
		{
			if ( Distance( coord ) <= CliffColumnRangeMeters && CliffReaches( entry ) )
				_cliffWanted.Add( coord );
		}

		// Build / rebuild: nearest first, a couple at a time. Rebuilt: a column seeded from a coarser LOD than its chunk has now,
		// or one whose fades no longer match its neighbours (faded toward one that is voxel now — or not faded toward one that
		// went back to heightfield) on a border its cliff band actually crosses.
		var candidates = new HashSet<TerrainChunkCoord>( _cliffWanted );
		foreach ( var (coord, column) in _voxelColumns )
		{
			if ( column.Chunk is not null && Distance( coord ) <= CliffColumnRangeMeters + CliffColumnDropSlackMeters && CliffReaches( column.Chunk ) )
				candidates.Add( coord );
		}
		var building = _pendingVoxelColumns.Count;
		foreach ( var coord in candidates.OrderBy( Distance ) )
		{
			if ( building >= CliffColumnsInFlight )
				break;
			if ( _pendingVoxelColumns.ContainsKey( coord ) || !_loaded.TryGetValue( coord, out var entry ) )
				continue;
			var rebuild = entry.Voxel is not null;
			if ( rebuild )
			{
				var v = entry.Voxel;
				var stale = v.SeedVerts < entry.VerticesPerSide || ((v.FadeSides ^ HeightfieldSides( coord )) & CliffBorders( entry )) != 0;
				if ( v.Old is not null || !stale )
					continue;
			}
			var seed = PrepareVoxelSeed( entry );
			if ( seed is null )
				continue;
			var settings = _loadSettings;
			var backend = _backend;
			_pendingVoxelColumns[coord] = new PendingVoxelColumn
			{
				Entry = entry,
				Seed = seed,
				Replace = rebuild,
				Task = GameTask.RunInThreadAsync( () => BuildVoxelField( seed, settings, backend ) ),
			};
			building++;
		}

		// Drop: out of range, nothing dug
		List<TerrainChunkCoord> drop = null;
		foreach ( var (coord, column) in _voxelColumns )
		{
			if ( column.Chunk is null || Distance( coord ) <= CliffColumnRangeMeters + CliffColumnDropSlackMeters || !CliffReaches( column.Chunk ) )
				continue;   // in range, or a plain dug column (those stay with their chunk)
			EnsureVoxelEditsLoaded();
			if ( !AnyVoxelEditTouches( coord ) )
				(drop ??= new()).Add( coord );
		}
		if ( drop is not null )
		{
			foreach ( var coord in drop )
				DestroyVoxelColumn( coord, restoreChunk: true );
		}
	}

	/// <summary>The borders of a chunk whose neighbour is plain heightfield (no column, none wanted): a cliff carve fades out toward them.</summary>
	int HeightfieldSides( TerrainChunkCoord c )
	{
		var mask = 0;
		if ( Heightfield( new TerrainChunkCoord( c.X, c.Y - 1 ) ) ) mask |= TerrainMeshBuilder.BorderMinY;
		if ( Heightfield( new TerrainChunkCoord( c.X, c.Y + 1 ) ) ) mask |= TerrainMeshBuilder.BorderMaxY;
		if ( Heightfield( new TerrainChunkCoord( c.X - 1, c.Y ) ) ) mask |= TerrainMeshBuilder.BorderMinX;
		if ( Heightfield( new TerrainChunkCoord( c.X + 1, c.Y ) ) ) mask |= TerrainMeshBuilder.BorderMaxX;
		return mask;

		bool Heightfield( TerrainChunkCoord n )
			=> !_cliffWanted.Contains( n ) && !(_loaded.TryGetValue( n, out var e ) && e.Voxel is not null) && !_pendingVoxelColumns.ContainsKey( n );
	}

	/// <summary>The borders of this chunk a cliff band crosses: its own (<see cref="TerrainLedgeCliffs.Scan"/>) and the neighbours' facing it.</summary>
	int CliffBorders( LoadedChunk entry )
	{
		var c = entry.Coord;
		var mask = entry.CliffMask & (TerrainMeshBuilder.BorderMinY | TerrainMeshBuilder.BorderMaxY | TerrainMeshBuilder.BorderMinX | TerrainMeshBuilder.BorderMaxX);
		if ( Faces( new TerrainChunkCoord( c.X, c.Y - 1 ), TerrainMeshBuilder.BorderMaxY ) ) mask |= TerrainMeshBuilder.BorderMinY;
		if ( Faces( new TerrainChunkCoord( c.X, c.Y + 1 ), TerrainMeshBuilder.BorderMinY ) ) mask |= TerrainMeshBuilder.BorderMaxY;
		if ( Faces( new TerrainChunkCoord( c.X - 1, c.Y ), TerrainMeshBuilder.BorderMaxX ) ) mask |= TerrainMeshBuilder.BorderMinX;
		if ( Faces( new TerrainChunkCoord( c.X + 1, c.Y ), TerrainMeshBuilder.BorderMinX ) ) mask |= TerrainMeshBuilder.BorderMaxX;
		return mask;

		bool Faces( TerrainChunkCoord n, int side ) => _loaded.TryGetValue( n, out var e ) && (e.CliffMask & side) != 0;
	}

	/// <summary>A cliff band reaches this chunk: its own cliff, or a neighbour's that runs up to their shared border.</summary>
	bool CliffReaches( LoadedChunk entry )
	{
		if ( (entry.CliffMask & TerrainLedgeCliffs.HasCliff) != 0 )
			return true;
		var c = entry.Coord;
		return Faces( new TerrainChunkCoord( c.X, c.Y - 1 ), TerrainMeshBuilder.BorderMaxY )
			|| Faces( new TerrainChunkCoord( c.X, c.Y + 1 ), TerrainMeshBuilder.BorderMinY )
			|| Faces( new TerrainChunkCoord( c.X - 1, c.Y ), TerrainMeshBuilder.BorderMaxX )
			|| Faces( new TerrainChunkCoord( c.X + 1, c.Y ), TerrainMeshBuilder.BorderMinX );

		bool Faces( TerrainChunkCoord n, int side ) => _loaded.TryGetValue( n, out var e ) && (e.CliffMask & side) != 0;
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
		// The seed copies the heightfield, so the clumps only change where a stroke landed: those tiles alone regenerate,
		// on the voxel surface (SampleGrassGround reads the field once the chunk is swapped)
		foreach ( var area in column.GrassAreas )
			NotifyGrassAreaChanged( area.x, area.y, area.z );
		column.GrassAreas.Clear();
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
		_pendingVoxelColumns.Remove( coord );
		if ( !_voxelColumns.TryGetValue( coord, out var column ) )
			return;
		_voxelColumns.Remove( coord );
		if ( column.Chunk is not null )
			column.Chunk.Voxel = null;
		if ( restoreChunk && column.Swapped )
			RestoreChunkFromVoxel( column );
		if ( column.Old is not null )
			DestroyColumnObjects( column.Old );
		DestroyColumnObjects( column );
	}

	static void DestroyColumnObjects( VoxelColumn column )
	{
		column.Set.Destroy();
		if ( column.Root is not null && column.Root.IsValid() )
			column.Root.Destroy();
	}

	void DestroyAllVoxelColumns()
	{
		DestroyUndergroundChests();
		_pendingVoxelColumns.Clear();
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
		_pendingVoxelColumns.Clear();
		foreach ( var coord in _voxelColumns.Keys.ToArray() )
			DestroyVoxelColumn( coord, restoreChunk: true );
		Log.Info( $"[Voxel] reset: edits cleared, {n} columns dropped, heightfield chunks back" );
	}
}
