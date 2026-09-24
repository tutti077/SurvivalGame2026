using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Procedural cave (caveVertTest scene): a chain of drops, junction halls, tunnels, chimneys and caverns swept as one
/// wandering tube — no central axis — with dead-end rooms and loops forking off the junctions and the drops, ponds as
/// oases, and a big chamber with a waterfall at the bottom of a 3 km net descent. The host picks a seed (or uses
/// <see cref="Seed"/>), every peer builds the identical rock + ledges from it locally, and the host alone places the
/// chests through the normal build path. The object's transform is the rim of the first drop; the square entrance
/// platform surrounds it and the first ledge / ENTRANCE sign sit on the object's -X side. Only the ledges carry the
/// <c>grapple</c> tag — the rock does not.
/// <para>Console: <c>cave_regen [seed]</c> (host), <c>cave_info</c>, <c>cave_tp &lt;routeIndex|top|bottom&gt;</c>,
/// <c>cave_tp room &lt;n&gt;</c>, <c>cave_tp junction &lt;n&gt;</c> (local pawn).</para>
/// </summary>
[Title( "Cave Dungeon Generator" )]
public sealed class CaveDungeonGenerator : Component
{
	// ---- layout ----------------------------------------------------------------------------------

	[Property, Group( "Layout" ), Title( "Seed (0 = random each start)" )]
	public int Seed { get; set; } = 1;

	[Property, Group( "Layout" ), Title( "Archetype (-1 = from seed; 0 Shaft, 1 Stairs, 2 Serpent, 3 Chimneys, 4 Caverns)" ), Range( -1, 4 )]
	public int Archetype { get; set; } = -1;

	[Property, Group( "Layout" ), Title( "Net depth (m)" )]
	public float DepthMeters { get; set; } = 3000f;

	[Property, Group( "Layout" ), Title( "Top radius (m)" )]
	public float TopRadiusMeters { get; set; } = 25f;

	[Property, Group( "Layout" ), Title( "Narrowest radius (m)" )]
	public float MinRadiusMeters { get; set; } = 3f;

	[Property, Group( "Layout" ), Title( "Widest radius (m)" )]
	public float MaxRadiusMeters { get; set; } = 45f;

	[Property, Group( "Layout" ), Title( "Wall roughness (m)" )]
	public float WallNoiseMeters { get; set; } = 1.2f;

	[Property, Group( "Layout" ), Title( "Entrance platform half size (m)" )]
	public float PlatformHalfMeters { get; set; } = 45f;

	// ---- puzzle ----------------------------------------------------------------------------------

	[Property, Group( "Puzzle" ), Title( "Dead-end rooms off the drops" ), Range( 0, 16 )]
	public int SideRoomCount { get; set; } = 2;

	[Property, Group( "Puzzle" ), Title( "Junction halls (drops that fork)" ), Range( 0, 6 )]
	public int JunctionCount { get; set; } = 1;

	[Property, Group( "Puzzle" ), Title( "Decoy rooms per junction (min)" ), Range( 0, 4 )]
	public int JunctionDecoysMin { get; set; } = 1;

	[Property, Group( "Puzzle" ), Title( "Decoy rooms per junction (max)" ), Range( 0, 4 )]
	public int JunctionDecoysMax { get; set; } = 2;

	[Property, Group( "Puzzle" ), Title( "Loops (leave a junction, rejoin further on)" ), Range( 0, 8 )]
	public int LoopCount { get; set; } = 1;

	[Property, Group( "Puzzle" ), Title( "Pond chance in a big room (0–1)" ), Range( 0f, 1f )]
	public float RoomPondChance { get; set; } = 0.5f;

	[Property, Group( "Puzzle" ), Title( "Pond chance in a junction hall (0–1)" ), Range( 0f, 1f )]
	public float JunctionPondChance { get; set; } = 0.4f;

	// ---- chamber ---------------------------------------------------------------------------------

	[Property, Group( "Chamber" ), Title( "Chamber radius (m)" )]
	public float ChamberRadiusMeters { get; set; } = 40f;

	[Property, Group( "Chamber" ), Title( "Build waterfall" )]
	public bool BuildWaterfall { get; set; } = true;

	// ---- route -----------------------------------------------------------------------------------

	[Property, Group( "Route" ), Title( "Jump hop gap (m, min)" )]
	public float JumpGapMinMeters { get; set; } = 3f;

	[Property, Group( "Route" ), Title( "Jump hop gap (m, max)" )]
	public float JumpGapMaxMeters { get; set; } = 5f;

	[Property, Group( "Route" ), Title( "Jump hop drop (m, min)" )]
	public float JumpDropMinMeters { get; set; } = 3f;

	[Property, Group( "Route" ), Title( "Jump hop drop (m, max)" )]
	public float JumpDropMaxMeters { get; set; } = 7f;

	[Property, Group( "Route" ), Title( "Grapple hop gap (m, min)" )]
	public float GrappleGapMinMeters { get; set; } = 8f;

	[Property, Group( "Route" ), Title( "Grapple hop gap (m, max)" )]
	public float GrappleGapMaxMeters { get; set; } = 15f;

	[Property, Group( "Route" ), Title( "Grapple hop drop (m, min)" )]
	public float GrappleDropMinMeters { get; set; } = 8f;

	[Property, Group( "Route" ), Title( "Grapple hop drop (m, max)" )]
	public float GrappleDropMaxMeters { get; set; } = 18f;

	[Property, Group( "Route" ), Title( "Grapple hop chance (0–1)" ), Range( 0f, 1f )]
	public float GrappleHopChance { get; set; } = 0.35f;

	[Property, Group( "Route" ), Title( "Climb hop rise (m, min)" )]
	public float ClimbRiseMinMeters { get; set; } = 5f;

	[Property, Group( "Route" ), Title( "Climb hop rise (m, max)" )]
	public float ClimbRiseMaxMeters { get; set; } = 10f;

	[Property, Group( "Route" ), Title( "Climb hop gap (m, min)" )]
	public float ClimbGapMinMeters { get; set; } = 2f;

	[Property, Group( "Route" ), Title( "Climb hop gap (m, max)" )]
	public float ClimbGapMaxMeters { get; set; } = 7f;

	[Property, Group( "Route" ), Title( "Filler ledges (fraction of route)" ), Range( 0f, 2f )]
	public float FillerFraction { get; set; } = 0.4f;

	[Property, Group( "Route" ), Title( "Light every N route ledges (0 = none)" ), Range( 0, 20 )]
	public int LightEveryLedges { get; set; } = 6;

	// ---- mesh / debug ----------------------------------------------------------------------------

	[Property, Group( "Mesh" ), Title( "Trunk ring spacing (m)" )]
	public float TrunkRingMeters { get; set; } = 4f;

	[Property, Group( "Mesh" ), Title( "Trunk segments around" ), Range( 16, 128 )]
	public int TrunkSegments { get; set; } = 48;

	[Property, Group( "Mesh" ), Title( "Passage ring spacing (m)" )]
	public float PassageRingMeters { get; set; } = 2.5f;

	[Property, Group( "Mesh" ), Title( "Passage segments around" ), Range( 12, 64 )]
	public int PassageSegments { get; set; } = 28;

	[Property, Group( "Debug" ), Title( "Tint route ledges by hop (gold jump / blue grapple / green climb)" )]
	public bool ColorByHop { get; set; } = true;

	[Property, Group( "Debug" ), Title( "Light radius (m)" )]
	public float LightRadiusMeters { get; set; } = 22f;

	[Property, Group( "Debug" ), Title( "Sign scale" )]
	public float SignScale { get; set; } = 1.5f;

	/// <summary>The seed every peer builds from. Host writes it; remotes rebuild when it changes.</summary>
	[Sync( SyncFlags.FromHost )]
	public int ActiveSeed { get; set; }

	public CaveLayout Layout { get; private set; }
	public CaveGeometryResult Geometry { get; private set; }

	/// <summary>World-space point on the platform just behind the first ledge.</summary>
	public Vector3 EntranceSpawnPosition =>
		Geometry is null ? WorldPosition : WorldTransform.PointToWorld( Geometry.EntranceSpawnLocal );

	static readonly (string id, int min, int max)[] ChestLoot =
	{
		("resource_scrap", 4, 10),
		("resource_obsidian", 2, 4),
		("resource_lithium", 2, 4),
		("resource_silica", 3, 6),
		("resource_flint", 3, 8),
		("food_hunter_stew", 1, 2),
	};

	GameObject _geometryRoot;
	GameObject _sign;
	readonly List<GameObject> _chests = new();
	int _builtSeed;

	bool IsAuthority => !Networking.IsActive || Networking.IsHost;

	protected override void OnStart()
	{
		base.OnStart();
		if ( !IsAuthority )
			return;

		ActiveSeed = Seed != 0 ? Seed : Random.Shared.Next( 1, int.MaxValue );
		Rebuild();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( ActiveSeed != 0 && ActiveSeed != _builtSeed )
			Rebuild();
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

		var settings = new CaveLayoutSettings
		{
			DepthMeters = DepthMeters,
			TopRadiusMeters = TopRadiusMeters,
			MinRadiusMeters = MinRadiusMeters,
			MaxRadiusMeters = MaxRadiusMeters,
			ChamberRadiusMeters = ChamberRadiusMeters,
			WallNoiseMeters = WallNoiseMeters,
			PlatformHalfMeters = PlatformHalfMeters,
			Archetype = Archetype,
			TrunkRingMeters = TrunkRingMeters,
			TrunkSegments = TrunkSegments,
			PassageRingMeters = PassageRingMeters,
			PassageSegments = PassageSegments,
			SideRoomCount = SideRoomCount,
			JunctionCount = JunctionCount,
			JunctionDecoysMin = Math.Min( JunctionDecoysMin, JunctionDecoysMax ),
			JunctionDecoysMax = JunctionDecoysMax,
			LoopCount = LoopCount,
			RoomPondChance = RoomPondChance,
			JunctionPondChance = JunctionPondChance,
			JumpGapMinMeters = JumpGapMinMeters,
			JumpGapMaxMeters = JumpGapMaxMeters,
			JumpDropMinMeters = JumpDropMinMeters,
			JumpDropMaxMeters = JumpDropMaxMeters,
			GrappleGapMinMeters = GrappleGapMinMeters,
			GrappleGapMaxMeters = GrappleGapMaxMeters,
			GrappleDropMinMeters = GrappleDropMinMeters,
			GrappleDropMaxMeters = GrappleDropMaxMeters,
			GrappleHopChance = GrappleHopChance,
			ClimbRiseMinMeters = ClimbRiseMinMeters,
			ClimbRiseMaxMeters = ClimbRiseMaxMeters,
			ClimbGapMinMeters = ClimbGapMinMeters,
			ClimbGapMaxMeters = ClimbGapMaxMeters,
			FillerFraction = FillerFraction,
			LightEveryLedges = LightEveryLedges,
		};
		Layout = CaveLayout.Generate( settings, ActiveSeed );

		var p = new CaveGeometryParams
		{
			ColorByHop = ColorByHop,
			LightRadiusMeters = LightRadiusMeters,
			BuildWaterfall = BuildWaterfall,
		};

		_geometryRoot = new GameObject( true, "CaveGeometry" );
		_geometryRoot.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_geometryRoot.Parent = GameObject;
		_geometryRoot.LocalPosition = Vector3.Zero;
		_geometryRoot.LocalRotation = Rotation.Identity;
		_geometryRoot.LocalScale = Vector3.One;

		Geometry = CaveGeometry.Build( _geometryRoot, Layout, p );

		SpawnSign();

		if ( IsAuthority )
			SpawnChests();

		var l = Layout;
		var trunk = l.Trunk;
		var routeLedges = l.Ledges.Where( x => x.IsRoute ).ToList();
		Log.Info( $"[CaveDungeon] seed {ActiveSeed}: {l.Archetype}, bottom {l.BottomZ:0} m, trunk {trunk.Length:0} m ({trunk.Legs.Count( x => x.IsJunction )} junctions, {trunk.Legs.Count( x => x.Kind == CaveLegKind.Climb )} chimneys), route {l.RoutePathMeters:0} m ({l.RouteDescentMeters:0} m down, {l.RouteClimbMeters:0} m up) over {l.RouteLedgeCount} ledges ({routeLedges.Count( x => x.Ascending )} climb hops), {l.Passages.Count - 1} branches ({l.Passages.Count( x => x.Kind == CavePassageKind.Loop )} loops), {l.Bowls.Count} ponds, {l.Ledges.Count - l.RouteLedgeCount} off-route ledges, {Geometry.TriangleCount} rock tris, {Geometry.BoxCount} boxes." );
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
		_builtSeed = 0;
	}

	void SpawnSign()
	{
		if ( Geometry is null )
			return;

		var world = WorldTransform.PointToWorld( Geometry.EntranceSignLocal );
		var facing = WorldTransform.NormalToWorld( Geometry.EntranceFacingLocal );
		_sign = DungeonSignPanel.Spawn( GameObject, "EntranceSign", world, facing, "ENTRANCE", SignScale );
	}

	/// <summary>Host: the chamber chest and one per room, through the shared build placement so they network like placed chests.</summary>
	void SpawnChests()
	{
		if ( Geometry is null || !Scene.IsValid() )
			return;

		var rng = new Random( ActiveSeed ^ 0xCA4E );
		foreach ( var slot in Geometry.ChestSlots )
		{
			var rotation = WorldRotation * Rotation.FromYaw( slot.LocalYaw );
			var position = WorldTransform.PointToWorld( slot.LocalPosition );
			position += Vector3.Up * BuildModuleDimensions.GetGroundSitHalfExtent( "chest", rotation );

			if ( !BuildAuthority.HostPlacePiece( Scene, "chest", new Transform( position, rotation ), blueprint: false, out var chest ) )
			{
				Log.Warning( $"[CaveDungeon] Failed to place chest {slot.RoomIndex}." );
				continue;
			}

			chest.Name = slot.RoomIndex < 0 ? "cave_chest_chamber" : $"cave_chest_passage_{slot.RoomIndex}";
			_chests.Add( chest );

			var container = chest.Components.Get<ContainerInventory>( FindMode.EverythingInSelfAndDescendants );
			if ( container is null )
				continue;

			var stacks = slot.RoomIndex < 0 ? rng.Next( 4, 7 ) : rng.Next( 2, 4 );
			for ( var i = 0; i < stacks; i++ )
			{
				var entry = ChestLoot[rng.Next( ChestLoot.Length )];
				container.HostDepositStack( entry.id, rng.Next( entry.min, entry.max + 1 ) );
			}
		}
	}

	// ---- console ---------------------------------------------------------------------------------

	static CaveDungeonGenerator Find()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return null;
		return scene.GetAllComponents<CaveDungeonGenerator>().FirstOrDefault();
	}

	[ConCmd( "cave_regen" )]
	public static void ConCmdRegen( string seed = "" )
	{
		var gen = Find();
		if ( gen is null )
		{
			Log.Warning( "[CaveDungeon] No CaveDungeonGenerator in the active scene." );
			return;
		}

		if ( !gen.IsAuthority )
		{
			Log.Warning( "[CaveDungeon] cave_regen is host-only — the host's seed is what every peer builds." );
			return;
		}

		var next = int.TryParse( seed, out var parsed ) && parsed != 0 ? parsed : Random.Shared.Next( 1, int.MaxValue );
		gen.ActiveSeed = next;
		gen.Rebuild();
	}

	[ConCmd( "cave_info" )]
	public static void ConCmdInfo()
	{
		var gen = Find();
		if ( gen?.Layout is null )
		{
			Log.Warning( "[CaveDungeon] Nothing built." );
			return;
		}

		var l = gen.Layout;
		var trunk = l.Trunk;
		Log.Info( $"[CaveDungeon] seed {l.Seed}: {l.Archetype}, net depth {l.DepthMeters:0} m (bottom {l.BottomZ:0} m), top radius {l.TopRadiusMeters:0.#} m, trunk {trunk.Length:0} m, route {l.RoutePathMeters:0} m ({l.RouteDescentMeters:0} m down / {l.RouteClimbMeters:0} m up)" );

		var junction = 0;
		var lastGroup = -1;
		for ( var i = 0; i < trunk.Legs.Count; i++ )
		{
			var leg = trunk.Legs[i];
			if ( leg.Kind == CaveLegKind.Drop && leg.Group == lastGroup )
				continue;
			lastGroup = leg.Group;
			var z0 = trunk.Samples[leg.Start].Position.z;
			var z1 = trunk.Samples[Math.Max( leg.Start, leg.End - 1 )].Position.z;
			var label = i == l.ChamberLeg ? "CHAMBER" : leg.IsJunction ? $"junction {junction++} (cave_tp junction {junction - 1})" : leg.HallRadius > 0f ? $"cavern r {leg.HallRadius:0}" : leg.Kind.ToString();
			var branches = leg.BranchPassages.Count > 0 ? $", branches [{string.Join( ",", leg.BranchPassages )}]" : "";
			Log.Info( $"  trunk leg {i}: {label} z {z0:0} → {z1:0} m{branches}" );
		}

		foreach ( var pk in l.Passages )
		{
			if ( pk.Kind == CavePassageKind.Trunk )
				continue;
			var mouth = l.Mouths[pk.MouthA];
			var legs = string.Join( " → ", pk.Legs.Select( g => g.Kind.ToString() ) );
			Log.Info( $"  passage {pk.Index} {pk.Kind}: mouth floor z {mouth.FloorZ:0} m ({(mouth.OnVerticalWall ? "drop wall" : "hall side")}), room r {pk.RoomRadius:0.#} m, {pk.Length:0} m long, {legs}{(pk.HasPond ? ", pond" : "")}, {l.Ledges.Count( x => x.Passage == pk.Index )} ledges (cave_tp room {pk.Index})" );
		}

		var prev = (CaveRoutePoint?)null;
		for ( var i = 0; i < l.Route.Count; i++ )
		{
			var pt = l.Route[i];
			var d = prev is null ? Vector3.Zero : pt.Position - prev.Value.Position;
			var gap = d.WithZ( 0f ).Length;
			var ledge = pt.Ledge >= 0 ? l.Ledges[pt.Ledge] : null;
			var what = ledge is null ? "walk" : ledge.Kind == CaveLedgeKind.Doorstep ? "DOORSTEP" : ledge.Ascending ? "CLIMB" : ledge.GrappleHop ? "GRAPPLE" : "jump";
			Log.Info( $"  route {i,3}: z {pt.Position.z,7:0.0} m {what,-8} {(ledge is null ? "" : ledge.Kind.ToString()),-8} gap {gap,4:0.0} m dz {d.z,6:0.0} m{(pt.Passage > 0 ? $" (passage {pt.Passage})" : "")}{(ledge is { HasLight: true } ? " (light)" : "")}" );
			prev = pt;
		}
	}

	[ConCmd( "cave_tp" )]
	public static void ConCmdTeleport( string target = "top", string index = "" )
	{
		var gen = Find();
		if ( gen?.Geometry is null )
		{
			Log.Warning( "[CaveDungeon] Nothing built." );
			return;
		}

		var pawn = gen.Scene.GetAllComponents<PlayerMovement>().FirstOrDefault( m => m.IsValid() && !m.GameObject.IsProxy )?.GameObject;
		if ( pawn is null )
		{
			Log.Warning( "[CaveDungeon] No local pawn — press L to spawn one first." );
			return;
		}

		var g = gen.Geometry;
		Vector3 local;
		if ( target.Equals( "bottom", StringComparison.OrdinalIgnoreCase ) )
			local = g.BottomLocal;
		else if ( target.Equals( "room", StringComparison.OrdinalIgnoreCase ) && int.TryParse( index, out var room ) && room > 0 && room < g.PassageFloorsLocal.Count )
			local = g.PassageFloorsLocal[room];
		else if ( target.Equals( "junction", StringComparison.OrdinalIgnoreCase ) && int.TryParse( index, out var junction ) && junction >= 0 && junction < g.JunctionFloorsLocal.Count )
			local = g.JunctionFloorsLocal[junction];
		else if ( int.TryParse( target, out var ledge ) && ledge >= 0 && ledge < g.RouteLandingsLocal.Count )
			local = g.RouteLandingsLocal[ledge];
		else
			local = g.EntranceSpawnLocal;

		pawn.WorldPosition = gen.WorldTransform.PointToWorld( local ) + Vector3.Up * TerrainWorldUnits.MetersToEngine( 0.6f );
		pawn.Transform.ClearInterpolation();
		if ( pawn.Network is { Active: true } )
			pawn.Network.ClearInterpolation();
		Log.Info( $"[CaveDungeon] Teleported to '{target} {index}'." );
	}
}
