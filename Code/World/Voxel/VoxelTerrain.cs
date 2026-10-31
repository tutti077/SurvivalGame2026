using System.Threading.Tasks;

namespace Survival;

/// <summary>
/// The standalone voxel digging prototype (Mark 2026-10-26: "I want this game to be a digging game for sure" — true
/// voxels, caves and tunnels). One <see cref="VoxelField"/> seeded from a height function covers a square around
/// <see cref="CenterMeters"/>; its chunks are a <see cref="VoxelChunkSet"/>. Edits are host-authoritative: a client asks
/// (<see cref="RequestBrush"/>), the host checks the brush and broadcasts it, every peer applies the same brush to the same
/// samples. The edit log is saved under the world's save folder and replayed on load. Lives in Assets/scenes/voxelTest.scene;
/// console: voxel_dig / voxel_fill / voxel_flatten / voxel_slope / voxel_brush / voxel_info / voxel_reset (VoxelCommands.cs).
/// The live world does the same per touched chunk in TerrainWorldManager.Voxel.cs — see docs/VOXEL.md.
/// </summary>
[Title( "Voxel Terrain" )]
public sealed class VoxelTerrain : Component, IVoxelBrushTarget
{
	public enum SeedSource
	{
		/// <summary>Rolling hills from a fixed formula — self-contained, the same every run.</summary>
		ProceduralHills,
		/// <summary>The real world's heightfield sampler for World Name (the terrain the player digs will match terrainTest).</summary>
		WorldRecipe,
	}

	[Property, Group( "Field" ), Title( "Seed" )] public SeedSource Seed { get; set; } = SeedSource.ProceduralHills;
	[Property, Group( "Field" ), Title( "World Name" ), Description( "Save folder for the edit log, and the recipe sampled when Seed = World Recipe." )]
	public string WorldName { get; set; } = "VoxelTest";
	[Property, Group( "Field" ), Title( "Centre (m)" ), Description( "World x,y the field is centred on." )] public Vector2 CenterMeters { get; set; }
	[Property, Group( "Field" ), Title( "Cell (m)" ), Range( 0.25f, 2f ), Step( 0.25f )] public float CellMeters { get; set; } = 0.5f;
	[Property, Group( "Field" ), Title( "Chunks Across" ), Range( 1, 32 ), Description( "Chunks per side (32 cells each: 16 m at 0.5 m cells)." )] public int ChunksAcross { get; set; } = 8;
	[Property, Group( "Field" ), Title( "Depth Below Ground (m)" ), Range( 4f, 64f ), Step( 1f ), Description( "How far under the lowest seed height the field reaches — the deepest a pit can go." )] public float DepthBelowMeters { get; set; } = 24f;
	[Property, Group( "Field" ), Title( "Head Room (m)" ), Range( 2f, 64f ), Step( 1f ), Description( "Field above the highest seed height — how tall a fill can grow." )] public float HeadRoomMeters { get; set; } = 8f;
	[Property, Group( "Field" ), Title( "Material" )] public string MaterialPath { get; set; } = "materials/terrain/terrain_ground.vmat";
	[Property, Group( "Brush" ), Title( "Max Brush Radius (m)" ), Range( 0.5f, 8f ), Step( 0.5f ), Description( "The host refuses larger brushes (anti-cheat)." )] public float MaxBrushRadiusMeters { get; set; } = 4f;
	[Property, Group( "Brush" ), Title( "Brush Flatness (0.2–1)" ), Range( 0.2f, 1f ), Step( 0.05f ), Description( "Half height of the dig / fill puck as a share of its radius: 1 = as tall as wide, 0.55 = the flat scoop a shovel takes (Mark 2026-10-27: a perfect sphere feels weird and removes too much)." )] public float BrushFlatness { get; set; } = 0.55f;
	[Property, Group( "Brush" ), Title( "Save Edits" )] public bool SaveEdits { get; set; } = true;
	[Property, Group( "Debug" )] public bool LogEdits { get; set; }

	/// <summary>The seed, the field and the chunks: null until <see cref="OnStart"/>'s worker thread finishes.</summary>
	public VoxelField Field => _set?.Field;
	public bool IsReady => _set is not null && _seeded;

	VoxelChunkSet _set;
	bool _seeded;
	Material _material;
	readonly List<VoxelEdit> _edits = new();

	protected override void OnStart()
	{
		base.OnStart();
		_material = Material.Load( MaterialPath );
		var cell = Math.Clamp( CellMeters, 0.25f, 2f );
		var across = Math.Clamp( ChunksAcross, 1, 32 );
		var sideMeters = across * VoxelField.ChunkCells * cell;
		_ = SeedAsync( Seed, WorldName, CenterMeters, sideMeters, cell, across, DepthBelowMeters, HeadRoomMeters );
	}

	protected override void OnDestroy()
	{
		_set?.Destroy();
		_set = null;
		base.OnDestroy();
	}

	/// <summary>Seed the field on a worker thread, then (back on the main thread) replay the saved edits and make the chunks.</summary>
	async Task SeedAsync( SeedSource seed, string worldName, Vector2 center, float sideMeters, float cell, int across, float depthBelow, float headRoom )
	{
		(VoxelField field, long seedMs) result;
		try
		{
			result = await GameTask.RunInThreadAsync( () =>
			{
				var sw = System.Diagnostics.Stopwatch.StartNew();
				var height = ResolveHeightFunction( seed, worldName );
				// The vertical range follows the seed heights over the square (sampled coarsely first)
				var minZ = float.MaxValue;
				var maxZ = float.MinValue;
				for ( var y = 0; y <= 32; y++ )
				{
					for ( var x = 0; x <= 32; x++ )
					{
						var g = height( center.x - (sideMeters * 0.5f) + (sideMeters * x / 32f), center.y - (sideMeters * 0.5f) + (sideMeters * y / 32f) );
						minZ = Math.Min( minZ, g );
						maxZ = Math.Max( maxZ, g );
					}
				}
				var chunkMeters = VoxelField.ChunkCells * cell;
				var originZ = MathF.Floor( (minZ - depthBelow) / chunkMeters ) * chunkMeters;
				var chunksZ = Math.Max( 1, (int)MathF.Ceiling( (maxZ + headRoom - originZ) / chunkMeters ) );
				var origin = new Vector3( center.x - (sideMeters * 0.5f), center.y - (sideMeters * 0.5f), originZ );
				var cells = across * VoxelField.ChunkCells;
				var field = new VoxelField( cell, cells, cells, chunksZ * VoxelField.ChunkCells, origin );
				field.SeedFromHeights( height );
				return (field, sw.ElapsedMilliseconds);
			} );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Voxel] seed failed: {e.Message}" );
			return;
		}
		if ( !IsValid )
			return;
		var (field, seedMs) = result;
		VoxelEditLog.Load( WorldName, _edits );
		foreach ( var e in _edits )
			Replay( field, e );
		_set = new VoxelChunkSet( field, GameObject, _material, "voxel", "voxel", TerrainWorldManager.TerrainTag );
		_set.CreateAllChunks();
		_seeded = true;
		Log.Info( $"[Voxel] field {field.SamplesX}×{field.SamplesY}×{field.SamplesZ} samples ({field.CellMeters} m cells, {field.ChunksX}×{field.ChunksY}×{field.ChunksZ} chunks) seeded in {seedMs} ms from {Seed}; {_edits.Count} saved edits replayed; origin {field.OriginMeters.x:0},{field.OriginMeters.y:0},{field.OriginMeters.z:0} m" );
	}

	/// <summary>One logged stroke onto a field: a rake stroke lays its saved plane (older rake rows without one plan again).</summary>
	HashSet<(int X, int Y, int Z)> Replay( VoxelField field, VoxelEdit e )
	{
		if ( e.IsRake && e.HasPlane )
			return field.ApplyPlane( e.CenterMeters, e.R, e.Plane );
		return field.ApplyBrush( e.CenterMeters, e.R, e.BrushMode, BrushFlatness );
	}

	/// <summary>The height function the field is seeded from (worker thread).</summary>
	static Func<float, float, float> ResolveHeightFunction( SeedSource seed, string worldName )
	{
		if ( seed == SeedSource.WorldRecipe )
		{
			var recipe = WorldSaveIO.TryReadRecipe( worldName );
			if ( recipe is not null )
			{
				var settings = TerrainPreviewSettingsResolver.ResolveForWorldGeneration( new TerrainWorldGenerationRequest
				{
					WorldSeed = recipe.WorldSeed,
					WorldDiameterMeters = recipe.WorldDiameterMeters,
					MaxTerrainHeightMeters = recipe.MaxTerrainHeightMeters,
					OceanRingWidthMeters = recipe.OceanRingWidthMeters,
					WorldName = worldName,
					Source = TerrainWorldSettingsSource.WorldRecipeFirst,
				} );
				return ( x, y ) => TerrainHeightQuery.TrySampleGroundMeters( settings, x, y, out var g ) ? g : 0f;
			}
			Log.Warning( $"[Voxel] no world recipe '{worldName}': seeding procedural hills instead" );
		}
		return ProceduralHills;
	}

	/// <summary>Rolling hills with a ridge and a hollow: enough shape to dig into a slope, under a crest and through a bank.</summary>
	static float ProceduralHills( float x, float y )
	{
		var h = 20f;
		h += 6f * MathF.Sin( x / 23f ) * MathF.Cos( y / 31f );
		h += 3f * MathF.Sin( (x + y) / 11f );
		h += 1.2f * MathF.Sin( x / 5.3f + 1.7f ) * MathF.Sin( y / 6.1f );
		h += 9f * MathF.Exp( -((x * x) + (y * y)) / 900f );               // a knoll at the centre
		h -= 5f * MathF.Exp( -(((x - 40f) * (x - 40f)) + ((y + 30f) * (y + 30f))) / 400f );   // a hollow
		return h;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( _seeded && _set is not null )
			_set.Tick();
	}

	// ---- edits

	public bool TryPlanFlatten( Vector3 centerEngine, float radiusMeters, bool slope, out VoxelPlane plane )
	{
		plane = default;
		return IsReady && _set.Field.TryPlanFlatten( TerrainWorldUnits.EngineToMeters( centerEngine ), radiusMeters, slope, out plane );
	}

	public void RequestBrush( Vector3 centerEngine, float radiusMeters, VoxelBrushMode mode )
	{
		var c = TerrainWorldUnits.EngineToMeters( centerEngine );
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			HostBrush( c, radiusMeters, mode );
			return;
		}
		RpcRequestBrush( c, radiusMeters, (int)mode );
	}

	[Rpc.Host]
	void RpcRequestBrush( Vector3 centerMeters, float radiusMeters, int mode )
	{
		if ( !Networking.IsHost )
			return;
		HostBrush( centerMeters, radiusMeters, (VoxelBrushMode)Math.Clamp( mode, 0, 3 ) );
	}

	/// <summary>Host rules, once per edit: a live field, a sane radius, inside the field; a rake stroke is planned here once so every peer lays the same plane. Then everyone applies it.</summary>
	void HostBrush( Vector3 centerMeters, float radiusMeters, VoxelBrushMode mode )
	{
		if ( !IsReady )
			return;
		var r = Math.Clamp( radiusMeters, 0.25f, MaxBrushRadiusMeters );
		var b = _set.Field.BoundsMeters;
		if ( centerMeters.x < b.Mins.x - r || centerMeters.y < b.Mins.y - r || centerMeters.z < b.Mins.z - r
			|| centerMeters.x > b.Maxs.x + r || centerMeters.y > b.Maxs.y + r || centerMeters.z > b.Maxs.z + r )
			return;
		VoxelEdit edit;
		if ( mode is VoxelBrushMode.Flatten or VoxelBrushMode.Slope )
		{
			if ( !_set.Field.TryPlanFlatten( centerMeters, r, mode == VoxelBrushMode.Slope, out var plane ) )
				return;
			edit = VoxelEdit.Rake( centerMeters, r, mode, plane );
		}
		else
			edit = VoxelEdit.Brush( centerMeters, r, mode );
		if ( GameObject.Network is { Active: true } )
			RpcApplyEdit( centerMeters, r, (int)mode, edit.HasPlane, edit.PlaneZ, edit.PlaneGx, edit.PlaneGy );
		else
			ApplyEditLocal( edit );
		if ( SaveEdits )
		{
			_edits.Add( edit );
			VoxelEditLog.Save( WorldName, _edits );
		}
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	void RpcApplyEdit( Vector3 centerMeters, float radiusMeters, int mode, bool hasPlane, float planeZ, float planeGx, float planeGy )
	{
		var edit = VoxelEdit.Brush( centerMeters, radiusMeters, (VoxelBrushMode)Math.Clamp( mode, 0, 3 ) );
		edit.HasPlane = hasPlane;
		edit.PlaneZ = planeZ;
		edit.PlaneGx = planeGx;
		edit.PlaneGy = planeGy;
		ApplyEditLocal( edit );
	}

	void ApplyEditLocal( VoxelEdit edit )
	{
		if ( !IsReady )
			return;
		var dirty = Replay( _set.Field, edit );
		_set.MarkDirty( dirty );
		if ( LogEdits )
			Log.Info( $"[Voxel] {edit.BrushMode} r {edit.R:0.00} m at {edit.X:0.0},{edit.Y:0.0},{edit.Z:0.0}: {dirty.Count} chunks" );
	}

	public string DescribeVoxels()
	{
		if ( !IsReady )
			return "[Voxel] seeding…";
		var f = _set.Field;
		return $"[Voxel] {f.ChunksX}×{f.ChunksY}×{f.ChunksZ} chunks of {VoxelField.ChunkCells}³ cells at {f.CellMeters} m ({f.ChunkMeters} m), origin {f.OriginMeters.x:0},{f.OriginMeters.y:0},{f.OriginMeters.z:0} m; "
			+ $"{_set.DrawnChunks} chunks drawn, {_set.TotalVertices} vertices, {_edits.Count} edits saved; queue {_set.QueueCount}, building {_set.BuildingCount}; last build {_set.LastBuildMs:0.0} ms (worker), main thread {_set.LastMainThreadMs:0.00} ms";
	}

	public void ResetVoxels()
	{
		if ( !IsReady )
			return;
		_edits.Clear();
		VoxelEditLog.Save( WorldName, _edits );
		_set.Field.SeedFromHeights( ResolveHeightFunction( Seed, WorldName ) );
		_set.MarkAllDirty();
		Log.Info( "[Voxel] reset: edits cleared, field reseeded, every chunk remeshing" );
	}
}
