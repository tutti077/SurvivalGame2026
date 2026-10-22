using Sandbox;
using Sandbox.Clutter;

namespace Survival;

/// <summary>
/// Grass on the streamed world: ONE engine Clutter component in infinite-streaming mode, so the engine keeps a
/// constant ring of tiles around the camera and regenerates them as it moves — no per-chunk volumes, no pop-in at
/// chunk edges (Mark 2026-10-13). The clumps are planted on the loaded chunks' own meshed height grids
/// (bilinear, no physics traces, nothing else can be hit); a tile over a chunk that is not loaded yet gets no
/// clumps and is invalidated when the chunk arrives (NOT on a LOD remesh — that regenerated the tiles at 128 m /
/// 256 m while travelling and blinked the grass, 2026-10-13). Clover Hills only — an 8×8 biome mask per chunk.
/// grass_blade.shader thins clumps by camera distance (Thin Full / Far / Cull on grass_blade.vmat).
/// </summary>
public sealed partial class TerrainWorldManager
{
	[Property, Group( "Vegetation" ), Title( "Grass — Scatter Grass" )]
	public bool VegetationGrassEnabled { get; set; } = true;

	/// <summary>The four blade fans from Blender/scripts/create_grass_clump.py — used when the scene lists none.</summary>
	static readonly string[] DefaultGrassClumpModels =
	[
		"models/environment/cloverHills/groundFoliage/grass/grass_clump1.vmdl",
		"models/environment/cloverHills/groundFoliage/grass/grass_clump2.vmdl",
		"models/environment/cloverHills/groundFoliage/grass/grass_clump3.vmdl",
		"models/environment/cloverHills/groundFoliage/grass/grass_clump4.vmdl",
	];

	[Property, Group( "Vegetation" ), Title( "Grass — Clump Models" ), Description( "Picked at random per clump. Empty = the built-in grass_clump1–4 (a scene saved before this property existed loads it as empty)." )]
	public List<string> VegetationGrassClumpModels { get; set; } = [.. DefaultGrassClumpModels];

	[Property, Group( "Vegetation" ), Title( "Grass — Range (m)" ), Range( 0f, 600f ), Step( 8f ), Description( "Radius of the grass ring the engine streams around the camera. The engine's infinite mode always keeps 12 tiles around the camera, so this picks the clutter tile size (snapped to the engine's options: 77 / 153 / 307 / 614 m). Every clump in the ring costs vertex work even when the shader has culled it, so the ring is the grass GPU cost." )]
	public float VegetationGrassRangeMeters { get; set; } = 150f;

	[Property, Group( "Vegetation" ), Title( "Grass — Clumps Per m²" ), Range( 0.1f, 8f ), Step( 0.1f )]
	public float VegetationGrassClumpsPerSquareMeter { get; set; } = 1.5f;

	[Property, Group( "Vegetation" ), Title( "Grass — Max Slope (degrees)" ), Range( 0f, 90f ), Step( 1f )]
	public float VegetationGrassMaxSlopeDegrees { get; set; } = 40f;

	[Property, Group( "Vegetation" ), Title( "Grass — Scale Min" ), Range( 0.25f, 3f ), Step( 0.05f )]
	public float VegetationGrassScaleMin { get; set; } = 0.8f;

	[Property, Group( "Vegetation" ), Title( "Grass — Scale Max" ), Range( 0.25f, 3f ), Step( 0.05f )]
	public float VegetationGrassScaleMax { get; set; } = 1.25f;

	/// <summary>Tag on every terrain chunk GameObject.</summary>
	public const string TerrainTag = "terrain";

	/// <summary>Dev: flip world grass on / off live (the Scatter Grass checkbox) — the quickest fps bisect.</summary>
	[ConCmd( "grass_toggle" )]
	static void GrassToggleCommand()
	{
		var manager = Sandbox.Game.ActiveScene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		if ( manager is null )
		{
			Log.Info( "[TerrainWorldManager] no terrain world in the active scene" );
			return;
		}

		manager.VegetationGrassEnabled = !manager.VegetationGrassEnabled;
		manager.EnsureWorldGrass();
		Log.Info( $"[TerrainWorldManager] grass {(manager.VegetationGrassEnabled ? "ON" : "OFF")}" );
	}

	const int GrassMaskCells = 8;
	/// <summary>Preferred streaming tile edge (engine units): 25.6 m — the engine's enum is snapped to the nearest option.</summary>
	// Measured 2026-10-20: ClutterComponent.Infinite ignores ClutterDefinition.TileRadius and streams 12 tiles around the
	// camera whatever we ask (565 generation jobs at 40 m, 150 m and 300 m alike), so the only ring-size lever is the tile size.
	const int InfiniteRingTiles = 12;

	List<Model> _grassModels;
	GameObject _worldGrass;
	ClutterComponent _worldGrassClutter;
	bool _loggedFirstGrass;

	/// <summary>Create (or drop) the world grass to match the Scatter Grass flag. Cheap when nothing changes — called every update after load.</summary>
	void EnsureWorldGrass()
	{
		var want = VegetationGrassEnabled && !_vegetationHidden && !_isWorldLoading && _loadSettings is not null;
		var has = _worldGrass is not null && _worldGrass.IsValid();
		if ( want == has )
			return;

		if ( !want )
		{
			DestroyWorldGrass();
			return;
		}

		var models = ResolveGrassModels();
		if ( models.Count == 0 )
		{
			LogFirstGrass( "no clump models loaded" );
			VegetationGrassEnabled = false;
			return;
		}

		var scatterer = new GrassScatterer
		{
			ClumpsPerSquareMeter = VegetationGrassClumpsPerSquareMeter,
			ScaleMin = VegetationGrassScaleMin,
			ScaleMax = VegetationGrassScaleMax,
			MaxSlopeDegrees = VegetationGrassMaxSlopeDegrees,
		};
		scatterer.SetHeightField( SampleGrassGround );
		scatterer.SetWorldFilter( IsGrassAllowedAt );

		var definition = new ClutterDefinition
		{
			Entries = new List<ClutterEntry>(),
			Scatterer = scatterer,
		};
		foreach ( var model in models )
		{
			definition.Entries.Add( new ClutterEntry
			{
				Model = model,
				Weight = 1f,
				LocalScale = 1f,
				CastShadows = false,
				EnablePhysics = false,
			} );
		}

		// The ring is InfiniteRingTiles tiles whatever TileRadius says, so the range picks the tile size: the engine option
		// nearest range / 12 (150 m → 512 u tiles → a 153 m ring; 300 m → 1024 u → 307 m).
		var wantedTile = TerrainWorldUnits.MetersToEngine( Math.Max( 8f, VegetationGrassRangeMeters ) ) / InfiniteRingTiles;
		var bestOption = definition.TileSizeEnum;
		var bestDelta = float.MaxValue;
		foreach ( ClutterDefinition.TileSizeOption option in Enum.GetValues( typeof( ClutterDefinition.TileSizeOption ) ) )
		{
			definition.TileSizeEnum = option;
			var delta = MathF.Abs( definition.TileSize - wantedTile );
			if ( delta < bestDelta )
			{
				bestDelta = delta;
				bestOption = option;
			}
		}
		definition.TileSizeEnum = bestOption;
		var tileSize = Math.Max( definition.TileSize, 64f );
		definition.TileRadius = InfiniteRingTiles;

		_worldGrass = new GameObject( true, "WorldGrass" );
		_worldGrass.Flags |= GameObjectFlags.NotSaved;
		_worldGrass.Parent = GameObject;
		_worldGrass.LocalPosition = Vector3.Zero;
		_worldGrass.LocalRotation = Rotation.Identity;
		_worldGrassClutter = _worldGrass.Components.Create<ClutterComponent>();
		_worldGrassClutter.Clutter = definition;
		_worldGrassClutter.Seed = WorldSeed;
		_worldGrassClutter.Mode = ClutterComponent.ClutterMode.Infinite;

		LogFirstGrass( $"infinite ring: tile {tileSize:0} u × {InfiniteRingTiles} = {InfiniteRingTiles * tileSize / 40f:0} m (asked {VegetationGrassRangeMeters:0} m), {models.Count} clump models, {VegetationGrassClumpsPerSquareMeter:0.0}/m²" );
	}

	void DestroyWorldGrass()
	{
		if ( _worldGrass is not null && _worldGrass.IsValid() )
			_worldGrass.Destroy();
		_worldGrass = null;
		_worldGrassClutter = null;
	}

	/// <summary>A chunk meshed or remeshed: cache its biome mask and have the grass tiles over it regenerate.</summary>
	void NotifyGrassChunkChanged( LoadedChunk entry )
	{
		if ( entry.GameObject is null || !entry.GameObject.IsValid() || _loadSettings is null )
			return;

		entry.GrassMask ??= BuildGrassBiomeMask( entry, _loadSettings, Math.Max( 32f, ChunkSizeMeters ) );

		if ( _worldGrassClutter is null || !_worldGrassClutter.IsValid() )
			return;

		var origin = entry.GameObject.WorldPosition;
		var size = TerrainWorldUnits.MetersToEngine( Math.Max( 32f, ChunkSizeMeters ) );
		_worldGrassClutter.InvalidateTilesInBounds( new BBox( origin + new Vector3( 0f, 0f, -4000f ), origin + new Vector3( size, size, 30000f ) ) );
	}

	List<Model> ResolveGrassModels()
	{
		if ( _grassModels is not null )
			return _grassModels;

		_grassModels = new List<Model>();
		IEnumerable<string> paths = VegetationGrassClumpModels is { Count: > 0 } ? VegetationGrassClumpModels : DefaultGrassClumpModels;
		foreach ( var path in paths )
		{
			if ( string.IsNullOrWhiteSpace( path ) )
				continue;
			var model = Model.Load( path );
			if ( model is null || model.IsError )
			{
				Log.Warning( $"[TerrainWorldManager] grass clump model '{path}' failed to load." );
				continue;
			}
			_grassModels.Add( model );
		}

		return _grassModels;
	}

	/// <summary>The loaded chunk under a world point (engine units), or null.</summary>
	LoadedChunk FindLoadedChunkAt( float wx, float wy )
	{
		if ( _loadSettings is null )
			return null;
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var worldRadius = _loadSettings.TotalWorldRadiusMeters;
		var cx = (int)MathF.Floor( (TerrainWorldUnits.EngineToMeters( wx ) + worldRadius) / chunkSize );
		var cy = (int)MathF.Floor( (TerrainWorldUnits.EngineToMeters( wy ) + worldRadius) / chunkSize );
		return _loaded.TryGetValue( new TerrainChunkCoord { X = cx, Y = cy }, out var entry ) ? entry : null;
	}

	/// <summary>Bilinear ground sample on the chunk's meshed height grid: world xy → (ground z, ground normal). Null off the loaded world.</summary>
	(float Z, Vector3 Normal)? SampleGrassGround( float wx, float wy )
	{
		var entry = FindLoadedChunkAt( wx, wy );
		if ( entry is null || entry.GrassHeightsZ is null || entry.GrassHeightsSide < 2 || entry.GameObject is null || !entry.GameObject.IsValid() )
			return null;

		var heights = entry.GrassHeightsZ;
		var side = entry.GrassHeightsSide;
		var step = Math.Max( entry.GrassStepUnits, 1f );
		var origin = entry.GameObject.WorldPosition;
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
		var gx = (((h10 - h00) * (1f - ty)) + ((h11 - h01) * ty)) / step;
		var gy = (((h01 - h00) * (1f - tx)) + ((h11 - h10) * tx)) / step;
		return (origin.z + z, new Vector3( -gx, -gy, 1f ).Normal);
	}

	bool IsGrassAllowedAt( Vector3 world )
	{
		var entry = FindLoadedChunkAt( world.x, world.y );
		if ( entry?.GrassMask is null || entry.GameObject is null || !entry.GameObject.IsValid() )
			return false;

		var cellUnits = TerrainWorldUnits.MetersToEngine( Math.Max( 32f, ChunkSizeMeters ) / GrassMaskCells );
		var origin = entry.GameObject.WorldPosition;
		var ix = Math.Clamp( (int)((world.x - origin.x) / cellUnits), 0, GrassMaskCells - 1 );
		var iy = Math.Clamp( (int)((world.y - origin.y) / cellUnits), 0, GrassMaskCells - 1 );
		return entry.GrassMask[(iy * GrassMaskCells) + ix];
	}

	/// <summary>Coarse Clover-Hills mask for one chunk (8×8 cells, sampled once on the main thread). Null when no cell qualifies.</summary>
	bool[] BuildGrassBiomeMask( LoadedChunk entry, TerrainPreviewSettings settings, float chunkSize )
	{
		var worldRadius = settings.TotalWorldRadiusMeters;
		var minX = -worldRadius + (entry.Coord.X * chunkSize);
		var minY = -worldRadius + (entry.Coord.Y * chunkSize);
		var cell = chunkSize / GrassMaskCells;
		var mask = new bool[GrassMaskCells * GrassMaskCells];
		var any = false;

		for ( var iy = 0; iy < GrassMaskCells; iy++ )
		{
			for ( var ix = 0; ix < GrassMaskCells; ix++ )
			{
				var wx = minX + ((ix + 0.5f) * cell);
				var wy = minY + ((iy + 0.5f) * cell);
				var sample = _backend.Sample( settings, wx, wy );
				if ( !sample.IsInsideWorld || !sample.IsOnLand || sample.OceanHeight01 > 0.5f )
					continue;

				var biome = TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, wx, wy );
				if ( biome.BiomeId != TerrainPreviewBiomeId.CloverHills )
					continue;

				mask[(iy * GrassMaskCells) + ix] = true;
				any = true;
			}
		}

		return any ? mask : null;
	}

	/// <summary>One line per session so the first play tells us what the grass did.</summary>
	void LogFirstGrass( string message )
	{
		if ( _loggedFirstGrass )
			return;
		_loggedFirstGrass = true;
		Log.Info( $"[TerrainWorldManager] grass: {message}" );
	}
}
