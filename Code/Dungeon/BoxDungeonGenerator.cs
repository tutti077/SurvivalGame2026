using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Procedural box dungeon (dungeonBoxTest scene). The host picks a seed (or uses <see cref="Seed"/>), every peer
/// builds the identical tinted-box geometry from it locally, and the host alone places the dead-end chests
/// through the normal build path so they network like any placed chest. The object's transform is the entrance:
/// the entrance room's centre sits on it and the doorway faces the object's -X.
/// <para>Console: <c>dungeon_regen [seed]</c> (host), <c>dungeon_floors &lt;n|all&gt;</c> (local view: hide floors above n),
/// <c>dungeon_info</c>.</para>
/// </summary>
[Title( "Box Dungeon Generator" )]
public sealed class BoxDungeonGenerator : Component
{
	// ---- layout ----------------------------------------------------------------------------------

	[Property, Group( "Layout" ), Title( "Seed (0 = random each start)" )]
	public int Seed { get; set; } = 1;

	[Property, Group( "Layout" ), Title( "Grid width (cells)" ), Range( 2, 20 )]
	public int Width { get; set; } = 10;

	[Property, Group( "Layout" ), Title( "Grid depth (cells)" ), Range( 2, 20 )]
	public int Depth { get; set; } = 10;

	[Property, Group( "Layout" ), Title( "Floors" ), Range( 1, 6 )]
	public int Floors { get; set; } = 3;

	[Property, Group( "Layout" ), Title( "Rooms per floor" ), Range( 2, 64 )]
	public int RoomsPerFloor { get; set; } = 12;

	[Property, Group( "Layout" ), Title( "Stairs per floor" ), Range( 1, 4 )]
	public int StairsPerFloor { get; set; } = 2;

	[Property, Group( "Layout" ), Title( "Winding bias (0 bushy – 1 long paths)" ), Range( 0f, 1f )]
	public float WindingBias { get; set; } = 0.6f;

	[Property, Group( "Layout" ), Title( "Corridor chance per step (0–1)" ), Range( 0f, 1f )]
	public float CorridorChance { get; set; } = 0.5f;

	[Property, Group( "Layout" ), Title( "Longest corridor run (cells)" ), Range( 1, 6 )]
	public int MaxCorridorRun { get; set; } = 3;

	[Property, Group( "Layout" ), Title( "Big room chance (0–1)" ), Range( 0f, 1f )]
	public float BigRoomChance { get; set; } = 0.4f;

	[Property, Group( "Layout" ), Title( "Round room chance (0–1)" ), Range( 0f, 1f )]
	public float RoundRoomChance { get; set; } = 0.2f;

	[Property, Group( "Layout" ), Title( "Chest chance per dead end (0–1)" ), Range( 0f, 1f )]
	public float ChestDeadEndFraction { get; set; } = 0.5f;

	// ---- sizes (meters) --------------------------------------------------------------------------

	[Property, Group( "Sizes (m)" ), Title( "Cell pitch (m)" )]
	public float PitchMeters { get; set; } = 16f;

	[Property, Group( "Sizes (m)" ), Title( "Medium room (m, single cell)" )]
	public float MediumRoomMeters { get; set; } = 9f;

	[Property, Group( "Sizes (m)" ), Title( "Large room (m, single cell; big rooms add a pitch per extra cell)" )]
	public float LargeRoomMeters { get; set; } = 12f;

	[Property, Group( "Sizes (m)" ), Title( "Hallway width (m)" )]
	public float HallWidthMeters { get; set; } = 2.5f;

	[Property, Group( "Sizes (m)" ), Title( "Floor-to-floor height (m)" )]
	public float FloorHeightMeters { get; set; } = 4f;

	[Property, Group( "Sizes (m)" ), Title( "Wall thickness (m)" )]
	public float WallThicknessMeters { get; set; } = 0.4f;

	[Property, Group( "Sizes (m)" ), Title( "Floor plate thickness (m)" )]
	public float PlateThicknessMeters { get; set; } = 0.4f;

	[Property, Group( "Sizes (m)" ), Title( "Doorway height (m)" )]
	public float DoorHeightMeters { get; set; } = 2.8f;

	[Property, Group( "Sizes (m)" ), Title( "Stair step rise (m)" )]
	public float StepRiseMeters { get; set; } = 0.4f;

	[Property, Group( "Sizes (m)" ), Title( "Stair step run (m)" )]
	public float StepRunMeters { get; set; } = 0.7f;

	[Property, Group( "Sizes (m)" ), Title( "Pillar size (m)" )]
	public float PillarMeters { get; set; } = 1f;

	// ---- debug -----------------------------------------------------------------------------------

	[Property, Group( "Debug" ), Title( "Cycle colours by depth (off = by room order)" )]
	public bool ColorByDepth { get; set; } = true;

	[Property, Group( "Debug" ), Title( "Sign scale" )]
	public float SignScale { get; set; } = 1.5f;

	[Property, Group( "Debug" ), Title( "Visible floors (-1 = all)" )]
	public int VisibleFloorsMax { get; set; } = -1;

	/// <summary>The seed every peer builds from. Host writes it; remotes rebuild when it changes.</summary>
	[Sync( SyncFlags.FromHost )]
	public int ActiveSeed { get; set; }

	public DungeonLayout Layout { get; private set; }
	public DungeonGeometryResult Geometry { get; private set; }
	/// <summary>Rooms / halls / doors / stairs in world meters for the map page; rebuilt with the geometry.</summary>
	public DungeonMapModel Map { get; private set; }
	/// <summary>What the local player has found so far (client-local; the map draws only this).</summary>
	public DungeonExploration Exploration { get; } = new();

	/// <summary>The generator in the active scene, for the map face and console commands.</summary>
	public static BoxDungeonGenerator Active { get; private set; }

	/// <summary>Real rooms (corridor cells excluded), for the map page's "rooms found" readout.</summary>
	public int RoomCount { get; private set; }

	/// <summary>Real rooms the local player has stood in (corridor cells excluded).</summary>
	public int VisitedRoomCount =>
		Layout is null ? 0 : Layout.Rooms.Count( r => !r.Has( DungeonRoomFlags.Corridor ) && Exploration.IsRoomVisited( r.Index ) );

	/// <summary>How often the viewer's position is sampled for room discovery.</summary>
	const float TrackIntervalSeconds = 0.25f;

	/// <summary>World-space point just outside the entrance doorway.</summary>
	public Vector3 EntranceSpawnPosition
	{
		get
		{
			if ( Geometry is null )
				return WorldPosition;
			var local = Geometry.EntranceDoorLocal + Geometry.EntranceOutwardLocal * TerrainWorldUnits.MetersToEngine( 3f ) + Vector3.Up * 8f;
			return WorldTransform.PointToWorld( local );
		}
	}

	static readonly (string id, int min, int max)[] ChestLoot =
	{
		("resource_scrap", 3, 8),
		("resource_flint", 2, 6),
		("resource_obsidian", 1, 3),
		("resource_lithium", 1, 3),
		("resource_silica", 2, 5),
		("resource_leather", 2, 4),
		("food_hunter_stew", 1, 2),
	};

	GameObject _geometryRoot;
	GameObject _sign;
	readonly List<GameObject> _chests = new();
	int _builtSeed;
	int _appliedVisibleFloors = int.MinValue;
	float _nextTrackAt;

	bool IsAuthority => !Networking.IsActive || Networking.IsHost;

	protected override void OnStart()
	{
		base.OnStart();
		if ( !IsAuthority )
			return;

		ActiveSeed = Seed != 0 ? Seed : Random.Shared.Next( 1, int.MaxValue );
		Rebuild();
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();
		Active = this;
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();
		if ( Active == this )
			Active = null;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( ActiveSeed != 0 && ActiveSeed != _builtSeed )
			Rebuild();

		if ( _appliedVisibleFloors != VisibleFloorsMax )
			ApplyFloorVisibility();

		TrackExploration();
	}

	/// <summary>Every quarter second: which floor / room / hall the local viewer is in, for the map reveal.</summary>
	void TrackExploration()
	{
		if ( Layout is null || Map is null || Time.Now < _nextTrackAt )
			return;

		_nextTrackAt = Time.Now + TrackIntervalSeconds;

		var viewer = ResolveLocalViewerPosition();
		if ( viewer is null )
			return;

		var world = viewer.Value;
		var local = WorldTransform.PointToLocal( world );
		var meters = new Vector2( TerrainWorldUnits.EngineToMeters( world.x ), TerrainWorldUnits.EngineToMeters( world.y ) );
		Exploration.Track( Layout, Map, local, meters );
	}

	/// <summary>The local input-owned pawn's feet; the scene camera when no pawn exists (fly cam in the test scene).</summary>
	Vector3? ResolveLocalViewerPosition()
	{
		if ( !Scene.IsValid() )
			return null;

		foreach ( var vitals in Scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is null || !vitals.IsValid() || !vitals.IsLocalInputOwnedPawn() )
				continue;
			return vitals.WorldPosition;
		}

		var camera = Scene.Camera;
		if ( camera is null || !camera.IsValid() )
			return null;

		return camera.WorldPosition;
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();
		Clear();
	}

	// ---- build -----------------------------------------------------------------------------------

	/// <summary>Tear down and rebuild from <see cref="ActiveSeed"/> (geometry on every peer, chests on the host).</summary>
	public void Rebuild()
	{
		Clear();
		if ( ActiveSeed == 0 )
			return;

		_builtSeed = ActiveSeed;

		var settings = new DungeonLayoutSettings
		{
			Width = Width,
			Depth = Depth,
			Floors = Floors,
			RoomsPerFloor = RoomsPerFloor,
			StairsPerFloor = StairsPerFloor,
			PitchMeters = PitchMeters,
			MediumRoomMeters = MediumRoomMeters,
			LargeRoomMeters = LargeRoomMeters,
			HallWidthMeters = HallWidthMeters,
			ChestDeadEndFraction = ChestDeadEndFraction,
			WindingBias = WindingBias,
			CorridorChance = CorridorChance,
			MaxCorridorRun = MaxCorridorRun,
			BigRoomChance = BigRoomChance,
			RoundRoomChance = RoundRoomChance,
		};
		Layout = DungeonLayout.Generate( settings, ActiveSeed );

		// Meters → engine units, once, here.
		var p = new DungeonGeometryParams
		{
			Pitch = TerrainWorldUnits.MetersToEngine( Layout.PitchMeters ),
			FloorHeight = TerrainWorldUnits.MetersToEngine( FloorHeightMeters ),
			PlateThickness = TerrainWorldUnits.MetersToEngine( PlateThicknessMeters ),
			WallThickness = TerrainWorldUnits.MetersToEngine( WallThicknessMeters ),
			HallWidth = TerrainWorldUnits.MetersToEngine( HallWidthMeters ),
			DoorHeight = TerrainWorldUnits.MetersToEngine( DoorHeightMeters ),
			StepRise = TerrainWorldUnits.MetersToEngine( StepRiseMeters ),
			StepRun = TerrainWorldUnits.MetersToEngine( StepRunMeters ),
			PillarSize = TerrainWorldUnits.MetersToEngine( PillarMeters ),
			ColorByDepth = ColorByDepth,
		};

		_geometryRoot = new GameObject( true, "DungeonGeometry" );
		_geometryRoot.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_geometryRoot.Parent = GameObject;
		_geometryRoot.LocalPosition = Vector3.Zero;
		_geometryRoot.LocalRotation = Rotation.Identity;
		_geometryRoot.LocalScale = Vector3.One;

		Geometry = DungeonGeometry.Build( _geometryRoot, Layout, p );
		Map = DungeonMapModel.Build( Layout, p, WorldTransform );
		RoomCount = Layout.Rooms.Count( r => !r.Has( DungeonRoomFlags.Corridor ) );
		Exploration.Reset( Layout );
		_appliedVisibleFloors = int.MinValue;

		SpawnSign();

		if ( IsAuthority )
			SpawnChests();

		var treasure = Layout.Rooms.Count( r => r.Has( DungeonRoomFlags.Treasure ) );
		var stairs = Layout.Rooms.Count( r => r.Has( DungeonRoomFlags.StairUp ) );
		var corridors = Layout.Rooms.Count( r => r.Has( DungeonRoomFlags.Corridor ) );
		var round = Layout.Rooms.Count( r => r.Shape == DungeonRoomShape.Round );
		var big = Layout.Rooms.Count( r => !r.IsSingleCell );
		Log.Info( $"[BoxDungeon] seed {ActiveSeed}: {Layout.Rooms.Count - corridors} rooms ({big} multi-cell, {round} round) + {corridors} corridor cells on {Layout.Floors} floors, {Layout.Halls.Count} halls, {stairs} stairs, {treasure} chest rooms, {Geometry.BoxCount} boxes." );
	}

	void Clear()
	{
		if ( _sign is { IsValid: true } )
			_sign.Destroy();
		_sign = null;

		if ( _geometryRoot is { IsValid: true } )
			_geometryRoot.Destroy();
		_geometryRoot = null;

		foreach ( var chest in _chests )
		{
			if ( chest is { IsValid: true } )
				chest.Destroy();
		}
		_chests.Clear();

		Layout = null;
		Geometry = null;
		Map = null;
		RoomCount = 0;
		_builtSeed = 0;
	}

	void SpawnSign()
	{
		if ( Geometry is null )
			return;

		var local = Geometry.EntranceDoorLocal + Vector3.Up * (Geometry.EntranceDoorHeight + TerrainWorldUnits.MetersToEngine( 0.6f ));
		var world = WorldTransform.PointToWorld( local );
		var facing = WorldTransform.NormalToWorld( Geometry.EntranceOutwardLocal );
		_sign = DungeonSignPanel.Spawn( GameObject, "EntranceSign", world, facing, "ENTRANCE", SignScale );
	}

	/// <summary>Host: one chest per treasure room, through the shared build placement so it networks like a placed chest.</summary>
	void SpawnChests()
	{
		if ( Geometry is null || !Scene.IsValid() )
			return;

		var rng = new Random( ActiveSeed ^ 0x5eed );
		foreach ( var slot in Geometry.ChestSlots )
		{
			var rotation = WorldRotation * Rotation.FromYaw( slot.LocalYaw );
			var position = WorldTransform.PointToWorld( slot.LocalPosition );
			position += Vector3.Up * BuildModuleDimensions.GetGroundSitHalfExtent( "chest", rotation );

			if ( !BuildAuthority.HostPlacePiece( Scene, "chest", new Transform( position, rotation ), blueprint: false, out var chest ) )
			{
				Log.Warning( $"[BoxDungeon] Failed to place chest for room {slot.RoomIndex}." );
				continue;
			}

			chest.Name = $"dungeon_chest_{slot.RoomIndex}";
			_chests.Add( chest );

			var container = chest.Components.Get<ContainerInventory>( FindMode.EverythingInSelfAndDescendants );
			if ( container is null )
				continue;

			var stacks = rng.Next( 2, 5 );
			for ( var i = 0; i < stacks; i++ )
			{
				var entry = ChestLoot[rng.Next( ChestLoot.Length )];
				container.HostDepositStack( entry.id, rng.Next( entry.min, entry.max + 1 ) );
			}
		}
	}

	void ApplyFloorVisibility()
	{
		_appliedVisibleFloors = VisibleFloorsMax;
		if ( Geometry is null )
			return;

		for ( var f = 0; f < Geometry.FloorRoots.Count; f++ )
		{
			var root = Geometry.FloorRoots[f];
			if ( root is not { IsValid: true } )
				continue;

			root.Enabled = VisibleFloorsMax < 0 || f <= VisibleFloorsMax;
		}
	}

	// ---- console ---------------------------------------------------------------------------------

	static BoxDungeonGenerator Find()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return null;
		return scene.GetAllComponents<BoxDungeonGenerator>().FirstOrDefault();
	}

	[ConCmd( "dungeon_regen" )]
	public static void ConCmdRegen( string seed = "" )
	{
		var gen = Find();
		if ( gen is null )
		{
			Log.Warning( "[BoxDungeon] No BoxDungeonGenerator in the active scene." );
			return;
		}

		if ( !gen.IsAuthority )
		{
			Log.Warning( "[BoxDungeon] dungeon_regen is host-only — the host's seed is what every peer builds." );
			return;
		}

		var next = int.TryParse( seed, out var parsed ) && parsed != 0 ? parsed : Random.Shared.Next( 1, int.MaxValue );
		gen.ActiveSeed = next;
		gen.Rebuild();
	}

	[ConCmd( "dungeon_floors" )]
	public static void ConCmdFloors( string maxFloor = "all" )
	{
		var gen = Find();
		if ( gen is null )
		{
			Log.Warning( "[BoxDungeon] No BoxDungeonGenerator in the active scene." );
			return;
		}

		gen.VisibleFloorsMax = int.TryParse( maxFloor, out var n ) ? n : -1;
		gen.ApplyFloorVisibility();
		Log.Info( gen.VisibleFloorsMax < 0
			? "[BoxDungeon] Showing every floor."
			: $"[BoxDungeon] Showing floors 0..{gen.VisibleFloorsMax} (local view only)." );
	}

	[ConCmd( "dungeon_info" )]
	public static void ConCmdInfo()
	{
		var gen = Find();
		if ( gen?.Layout is null )
		{
			Log.Warning( "[BoxDungeon] Nothing built." );
			return;
		}

		var layout = gen.Layout;
		Log.Info( $"[BoxDungeon] seed {layout.Seed}, {layout.Rooms.Count} rooms, {layout.Halls.Count} halls, {layout.Floors} floors" );
		for ( var f = 0; f < layout.Floors; f++ )
		{
			var rooms = layout.Rooms.Where( r => r.Floor == f && !r.Has( DungeonRoomFlags.Corridor ) ).ToList();
			var corridors = layout.Rooms.Count( r => r.Floor == f && r.Has( DungeonRoomFlags.Corridor ) );
			var chests = rooms.Count( r => r.Has( DungeonRoomFlags.Treasure ) );
			var stairs = rooms.Count( r => r.Has( DungeonRoomFlags.StairUp ) );
			var big = rooms.Count( r => !r.IsSingleCell );
			var round = rooms.Count( r => r.Shape == DungeonRoomShape.Round );
			var maxDoors = rooms.Count > 0 ? rooms.Max( r => r.DoorCount ) : 0;
			var maxDepth = rooms.Count > 0 ? rooms.Max( r => r.Depth ) : 0;
			Log.Info( $"  floor {f}: {rooms.Count} rooms ({big} multi-cell, {round} round, most doors {maxDoors}) + {corridors} corridor cells, {stairs} stairs up, {chests} chests, deepest room {maxDepth} steps from the entrance" );
		}
	}
}
