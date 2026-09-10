using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Navigation;

namespace Survival;

/// <summary>
/// NavMesh comes from the PhysicsWorld (Recast). testscene1 also had a baked .navdata —
/// alert must <see cref="NavMesh.GenerateTiles"/> from live physics (same as stair placement),
/// not only queue RequestTilesGeneration against stale bake.
/// <para>
/// Baked scenes: tiles cache a heightfield, and in a scene that loads baked navdata that cache
/// "originates from baked data rather than live geometry" (<see cref="NavMeshTile.IsBakedHeightField"/>),
/// so incremental GenerateTiles rebuilt tiles from the bake — new walls never carved, new floors
/// never got a mesh (nav_probe on an upper floor: "nav at feet NO", while a full Generate from
/// physics gave "YES"). <see cref="EnsureLiveNavOnce"/> therefore does ONE full physics generate
/// per non-streamed scene on the host at load, after which every tile is live and incremental
/// rebakes rasterise real geometry. Never UnloadTiles before GenerateTiles: unloaded tiles do not
/// come back from an incremental call, which left holes agents got re-projected out of (10 m
/// teleports).
/// </para>
/// </summary>
public static class BuildNavMeshSync
{
	/// <summary>Roofs are exactly 45° — keep a margin so voxelised roof normals still count as walkable.</summary>
	const float BuildTraversalMaxSlope = 55f;
	const float BuildTraversalStepSize = 40f;
	/// <summary>Coalesce chunk/spawn notifies — avoids GenerateTiles thrash while streaming.</summary>
	const double LocalBakeBatchSeconds = 0.85;
	/// <summary>
	/// Hand-built scenes: a structure regenerate waits this long after the LAST placement. Each
	/// regenerate costs a visible hitch (per Mark: "super laggy when they renav"), so a building
	/// session is batched — entities keep tracking on foot meanwhile.
	/// </summary>
	const double StructuralDebounceSeconds = 1.5;
	/// <summary>Tile / request modes rebuild only a few tiles, so a placement lands in nav almost at once (per Mark: keep the mesh current).</summary>
	const double TileDebounceSeconds = 0.25;
	const double TileUrgentSeconds = 0.15;
	const double TileBatchMaxSeconds = 1.0;

	/// <summary>Whole-plane regenerates need the long batch; tile rebakes are cheap enough to run near-immediately.</summary>
	static bool CheapRebake => RebakeMode != StructureRebakeMode.Full;
	/// <summary>
	/// A removed piece opens a way in — entities wait on this bake to resume the chase, so it lands
	/// on the next frames (the destroy is deferred to end of frame; never bake the same frame).
	/// </summary>
	const double UrgentBakeSeconds = 0.5;
	const float ChaseCorridorPadding = 640f;
	/// <summary>Cap deferred bake size so streaming unions cannot Recast half the world at once.</summary>
	const float MaxLocalBakeHalfExtent = 512f;
	/// <summary>
	/// Headroom kept above the tallest build bake in the nav bounds. Nav is only generated inside
	/// <see cref="NavMesh.Bounds"/>; a baked scene computes those from the bare world, so an upper
	/// floor sits above the ceiling and never gets a mesh (nav_probe: "nav at feet NO" on a floor
	/// at z≈102 while the ground below has nav).
	/// </summary>
	const float NavBoundsHeadroom = 512f;

	static readonly Dictionary<Scene, PendingLocalBake> _pendingLocalBakes = new();

	sealed class PendingLocalBake
	{
		public BBox Bounds;
		public double ExecuteAt;
		public double FirstRequestedAt;
		/// <summary>A build piece was placed / removed. Spawn-settle and chunk-load requests are not structural.</summary>
		public bool Structural;
	}

	/// <summary>Hand-built scenes: a burst of placements becomes one full regenerate at most this long after the first.</summary>
	const double StructuralBatchMaxSeconds = 6.0;

	public static void EnsureBuildTraversalSettings( Scene scene )
	{
		if ( !scene.IsValid() )
			return;

		var navMesh = scene.NavMesh;
		if ( navMesh is null || !navMesh.IsEnabled )
			return;

		navMesh.EditorAutoUpdate = false;
		navMesh.IncludeStaticBodies = true;
		navMesh.IncludeKeyframedBodies = true;
		navMesh.DeferGeneration = false;

		if ( navMesh.AgentMaxSlope < BuildTraversalMaxSlope )
			navMesh.AgentMaxSlope = BuildTraversalMaxSlope;

		if ( navMesh.AgentStepSize < BuildTraversalStepSize )
			navMesh.AgentStepSize = BuildTraversalStepSize;
	}

	/// <summary>
	/// Spawn / chunk load: queue a local tile bake (coalesced). Agents retry via
	/// <see cref="EntityBrain.OnNavBakeComplete"/> — do not sync-GenerateTiles per scav
	/// (that was hitching terrainTest ~once/sec while streaming + populating).
	/// </summary>
	public static void EnsureNavAroundPoint( Scene scene, Vector3 worldPos, float padding = 384f )
	{
		if ( !scene.IsValid() || !IsNavAuthority() )
			return;

		EnsureBuildTraversalSettings( scene );

		var navMesh = scene.NavMesh;
		if ( navMesh is null || !navMesh.IsEnabled )
			return;

		var pad = Math.Max( 128f, padding );
		var bounds = new BBox(
			worldPos - new Vector3( pad, pad, pad ),
			worldPos + new Vector3( pad, pad, pad ) );

		ScheduleLocalBake( scene, bounds );
	}

	/// <summary>
	/// On alert: rebuild chase-corridor tiles from the live PhysicsWorld so static cubes/walls
	/// carve holes. Uses <see cref="NavMesh.GenerateTiles"/> (same path as walkable build pieces),
	/// not a fire-and-forget request against stale baked.navdata.
	/// </summary>
	public static void RefreshChaseCorridor( Scene scene, Vector3 from, Vector3 to )
	{
		if ( !scene.IsValid() || !IsNavAuthority() )
			return;

		EnsureBuildTraversalSettings( scene );

		var navMesh = scene.NavMesh;
		if ( navMesh is null || !navMesh.IsEnabled )
			return;

		var mins = Vector3.Min( from, to ) - new Vector3( ChaseCorridorPadding, ChaseCorridorPadding, ChaseCorridorPadding );
		var maxs = Vector3.Max( from, to ) + new Vector3( ChaseCorridorPadding, ChaseCorridorPadding, ChaseCorridorPadding );
		var bounds = ClampBakeBounds( new BBox( mins, maxs ) );

		// Alert path still needs live carve — only promote non-static solids (trees already Static).
		MarkSolidCollidersStaticInBounds( scene, bounds );

		var physics = scene.PhysicsWorld;
		if ( physics is not null )
		{
			RegenerateTilesFromPhysics( navMesh, physics, bounds );
			NotifyEnemiesNavUpdated( scene );
			return;
		}

		navMesh.RequestTilesGeneration( bounds );
	}

	/// <summary>Incremental rebuild of the tiles under <paramref name="bounds"/> from the live physics world.</summary>
	static void RegenerateTilesFromPhysics( NavMesh navMesh, PhysicsWorld physics, BBox bounds )
	{
		navMesh.GenerateTiles( physics, bounds );
	}

	/// <summary>How a hand-built scene rebuilds nav after a structure change — runtime A/B via `nav_mode`.</summary>
	public enum StructureRebakeMode
	{
		/// <summary>Whole-mesh Generate after a local unload. Known correct; one hitch per bake.</summary>
		Full,
		/// <summary>Unload + GenerateTiles for just the tiles around the change. Cheap if the engine rebuilds them from physics.</summary>
		Tiles,
		/// <summary>Unload + RequestTilesGeneration: the engine rebuilds those tiles incrementally over frames — no single hitch.</summary>
		Request
	}

	/// <summary>Default is tiles (per Mark: regenerating the whole plane for one wall is a waste); a tile bake that leaves its area empty drops the session back to Full.</summary>
	public static StructureRebakeMode RebakeMode = StructureRebakeMode.Tiles;

	/// <summary>Pending verification of a tile rebake: the area must contain nav again once generation settles.</summary>
	static BBox _tileVerifyBounds;
	static double _tileVerifyAt = -1d;
	static Scene _tileVerifyScene;
	const double TileVerifyDelaySeconds = 1.5;

	static readonly HashSet<Scene> _liveNavScenes = new();
	static double _liveNavFirstTryAt = -1d;
	/// <summary>Hand-built scenes mesh only this far (units) around the player at load — 150 m — so a structure regenerate stays quick.</summary>
	const float HandBuiltNavBubbleUnits = 6000f;
	/// <summary>How long the load-time pass waits for a player pawn to centre the bubble on before using the scene origin.</summary>
	const double LiveNavPawnWaitSeconds = 15d;
	static readonly Dictionary<Scene, bool> _streamedScene = new();
	static double _fullRegenStartedAt = -1d;

	/// <summary>Streamed world (chunks bake their own tiles as they load) vs a hand-built scene.</summary>
	static bool IsStreamedScene( Scene scene )
	{
		if ( _streamedScene.TryGetValue( scene, out var streamed ) )
			return streamed;

		streamed = false;
		foreach ( var manager in scene.GetAllComponents<TerrainWorldManager>() )
		{
			if ( manager is not null && manager.IsValid() )
			{
				streamed = true;
				break;
			}
		}

		_streamedScene[scene] = streamed;
		return streamed;
	}

	/// <summary>
	/// Hand-built scenes: the only regenerate ever observed to carve placed walls or mesh an upper
	/// floor is a full <see cref="NavMesh.Generate"/> from physics — incremental GenerateTiles kept
	/// reporting paths through fresh walls even after every tile was live. So structure changes in
	/// those scenes regenerate the whole mesh (coalesced by the pending-bake batch). Streamed worlds
	/// keep tile bakes: a full generate there would rebuild every loaded chunk.
	/// Returns false when a generate is already running (the caller keeps its request pending).
	/// <paramref name="dropCaches"/> unloads every tile first. This is NOT optional for structure
	/// changes: Generate without it reuses each tile's cached heightfield, which predates the new
	/// pieces — nav_show then draws nothing on any build piece, not even a floor slab on the ground.
	/// The mesh is absent for the ~0.7 s the rebuild takes; settle requests are dropped in hand-built
	/// scenes so that gap cannot re-trigger a regenerate.
	/// </summary>
	/// <summary>
	/// <paramref name="unloadBounds"/>: which tiles to drop before generating. Generate rebuilds
	/// only MISSING tiles from physics and reuses every cached one (no unload: 0.7 s and stale — no
	/// mesh on any new piece; unload everything: correct but 6 s over this scene's 1 km² bounds).
	/// So a structure change unloads just the tiles around it and generates: correct and quick.
	/// Null = all tiles (load-time pass, nav_regen).
	/// </summary>
	static bool RegenerateFullFromPhysics( Scene scene, NavMesh navMesh, PhysicsWorld physics, string why, BBox? unloadBounds )
	{
		if ( navMesh.IsGenerating )
			return false;

		var bounds = navMesh.Bounds;
		if ( bounds.Size.LengthSquared < 1f )
		{
			Log.Warning( "[BuildNav] nav bounds are empty — mesh not initialised yet, skipping full regenerate" );
			return true;
		}

		var unload = unloadBounds ?? bounds;
		MarkSolidCollidersStaticInBounds( scene, unload );
		_fullRegenStartedAt = Time.NowDouble;
		Log.Info( $"[BuildNav] regenerate from physics ({why}) — dropping tiles around {unload.Center} size {unload.Size}" );
		navMesh.UnloadTiles( unload );
		navMesh.Generate( physics );
		return true;
	}

	/// <summary>Logs how long a full regenerate took once <see cref="NavMesh.IsGenerating"/> clears. Ticked by <see cref="BuildNavBakeSystem"/>.</summary>
	/// <summary>
	/// After a tile rebake: the unloaded area must hold nav again. If GenerateTiles did not bring the
	/// tiles back (observed once while the mesh was still baked), fall back to a full regenerate and
	/// stay on Full for the session — a hole under an entity is worse than one hitch.
	/// </summary>
	public static void TickTileVerify( Scene scene )
	{
		if ( _tileVerifyAt < 0d || _tileVerifyScene != scene || !scene.IsValid() )
			return;

		var navMesh = scene.NavMesh;
		if ( navMesh is null || navMesh.IsGenerating || Time.NowDouble < _tileVerifyAt )
			return;

		_tileVerifyAt = -1d;
		// Sample a flattened slice of the bake area around ground level where the tiles were.
		var probe = new BBox(
			new Vector3( _tileVerifyBounds.Mins.x, _tileVerifyBounds.Mins.y, _tileVerifyBounds.Center.z - 300f ),
			new Vector3( _tileVerifyBounds.Maxs.x, _tileVerifyBounds.Maxs.y, _tileVerifyBounds.Center.z + 300f ) );
		var found = false;
		for ( var i = 0; i < 6 && !found; i++ )
			found = navMesh.GetRandomPoint( probe ).HasValue;

		if ( found )
			return;

		var physics = scene.PhysicsWorld;
		Log.Warning( "[BuildNav] tile rebake left its area without nav — falling back to full regenerate for this session (nav_mode full)" );
		RebakeMode = StructureRebakeMode.Full;
		if ( physics is not null )
			RegenerateFullFromPhysics( scene, navMesh, physics, "tile rebake verify failed", unloadBounds: _tileVerifyBounds );
	}

	public static void TickFullRegenTiming( Scene scene )
	{
		if ( _fullRegenStartedAt < 0d || !scene.IsValid() )
			return;

		var navMesh = scene.NavMesh;
		if ( navMesh is not null && navMesh.IsGenerating )
			return;

		Log.Info( $"[BuildNav] full regenerate finished in {Time.NowDouble - _fullRegenStartedAt:0.00}s" );
		_fullRegenStartedAt = -1d;
		NotifyEnemiesNavUpdated( scene );
	}

	/// <summary>
	/// Host, once per scene: if this scene is not a streamed world (no <see cref="TerrainWorldManager"/>),
	/// regenerate the whole nav mesh from physics so no tile keeps a baked heightfield. Small
	/// hand-built scenes only — a streamed world builds its tiles live as chunks load anyway.
	/// </summary>
	public static bool EnsureLiveNavOnce( Scene scene )
	{
		if ( !scene.IsValid() || !IsNavAuthority() )
			return true;

		if ( _liveNavScenes.Contains( scene ) )
			return true;

		var navMesh = scene.NavMesh;
		var physics = scene.PhysicsWorld;
		if ( navMesh is null || !navMesh.IsEnabled || physics is null )
			return true;

		if ( IsStreamedScene( scene ) )
		{
			_liveNavScenes.Add( scene );
			return true;
		}

		// Nav data is still loading right after SceneLoaded — bounds read as empty. Retry later.
		if ( navMesh.Bounds.Size.LengthSquared < 1f )
			return false;

		// Bubble (per Mark): a test scene's whole-world bounds made every structure regenerate ~5 s.
		// Centre 150 m of nav on the first player; wait a little for one to spawn.
		if ( _liveNavFirstTryAt < 0d )
			_liveNavFirstTryAt = Time.NowDouble;

		var centre = Vector3.Zero;
		var havePawn = false;
		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is null || !vitals.GameObject.IsValid() )
				continue;

			centre = vitals.GameObject.WorldPosition;
			havePawn = true;
			break;
		}

		if ( !havePawn && Time.NowDouble - _liveNavFirstTryAt < LiveNavPawnWaitSeconds )
			return false;

		var current = navMesh.Bounds;
		navMesh.CustomBounds = true;
		navMesh.Bounds = new BBox(
			new Vector3( centre.x - HandBuiltNavBubbleUnits, centre.y - HandBuiltNavBubbleUnits, current.Mins.z ),
			new Vector3( centre.x + HandBuiltNavBubbleUnits, centre.y + HandBuiltNavBubbleUnits, current.Maxs.z ) );
		Log.Info( $"[BuildNav] hand-built scene: nav bubble ±{HandBuiltNavBubbleUnits:0}u around {(havePawn ? "the player" : "the origin")} {centre}" );

		_liveNavScenes.Add( scene );
		EnsureBuildTraversalSettings( scene );
		RegenerateFullFromPhysics( scene, navMesh, physics, "scene load — baked tiles become live", unloadBounds: null );
		return true;
	}

	/// <summary>
	/// Editor cubes default to Static=false (physics collision only). Build pieces force Static=true.
	/// Nav generation only includes Static/Keyframed bodies — so we promote solids before bake.
	/// Already-static colliders (terrain + vegetation) are skipped before GetBounds — critical with
	/// thousands of trees, or every bake hitch becomes a full-scene scan.
	/// </summary>
	public static void MarkSolidCollidersStaticInBounds( Scene scene, BBox bounds )
	{
		if ( !scene.IsValid() )
			return;

		foreach ( var col in scene.GetAllComponents<Collider>() )
		{
			if ( col is null || !col.Enabled || !col.GameObject.IsValid() )
				continue;

			if ( col.IsTrigger )
				continue;

			// Fast path: vegetation / terrain chunks are already Static — never GetBounds them.
			if ( col is BoxCollider alreadyBox && alreadyBox.Static )
				continue;
			if ( col is ModelCollider alreadyModel && alreadyModel.Static )
				continue;
			if ( col is not BoxCollider and not ModelCollider )
				continue;

			var go = col.GameObject;
			if ( IsPawnOrEnemyHierarchy( go ) )
				continue;

			var goBounds = go.GetBounds();
			if ( goBounds.Size.LengthSquared < 1f )
				goBounds = BBox.FromPositionAndSize( go.WorldPosition, 80f );

			if ( !BoundsOverlap( bounds, goBounds ) )
				continue;

			if ( col is BoxCollider box )
				box.Static = true;
			else if ( col is ModelCollider model )
				model.Static = true;
		}
	}

	static bool BoundsOverlap( BBox a, BBox b ) =>
		a.Mins.x <= b.Maxs.x && a.Maxs.x >= b.Mins.x
		&& a.Mins.y <= b.Maxs.y && a.Maxs.y >= b.Mins.y
		&& a.Mins.z <= b.Maxs.z && a.Maxs.z >= b.Mins.z;

	static bool IsPawnOrEnemyHierarchy( GameObject go )
	{
		for ( var current = go; current.IsValid(); current = current.Parent )
		{
			if ( current.Components.Get<PlayerVitals>() is not null )
				return true;
			if ( current.Components.Get<PlayerController>() is not null )
				return true;
			if ( current.Components.Get<EntityBrain>() is not null )
				return true;
			if ( current.Components.Get<EntityVitals>() is not null )
				return true;
			if ( current.Tags.Has( "enemy" ) || current.Tags.Has( "player" ) )
				return true;
		}

		return false;
	}

	public static void NotifyTerrainChunkLoaded( Scene scene, BBox chunkBounds )
	{
		if ( !scene.IsValid() || !IsNavAuthority() )
			return;

		EnsureBuildTraversalSettings( scene );
		ScheduleLocalBake( scene, chunkBounds );
	}

	static Scene _bakeTickScene;
	static double _bakeTickAt;

	/// <summary>Executes the coalesced local bake once its deadline passes. Ticked every frame by <see cref="BuildNavBakeSystem"/>.</summary>
	public static void TickPendingLocalBakes( Scene scene )
	{
		if ( !scene.IsValid() || !IsNavAuthority() )
			return;

		var now = Time.NowDouble;
		if ( _bakeTickScene == scene && now - _bakeTickAt < 1.0 / 30.0 )
			return;

		_bakeTickScene = scene;
		_bakeTickAt = now;

		if ( !_pendingLocalBakes.TryGetValue( scene, out var pending ) )
			return;

		if ( Time.NowDouble < pending.ExecuteAt )
			return;

		var navMesh = scene.NavMesh;
		if ( navMesh is null || !navMesh.IsEnabled )
		{
			_pendingLocalBakes.Remove( scene );
			return;
		}

		var physics = scene.PhysicsWorld;
		if ( physics is not null && !IsStreamedScene( scene ) )
		{
			if ( !pending.Structural )
			{
				_pendingLocalBakes.Remove( scene );
				return;
			}

			// One regenerate at a time — a request that lands mid-generate simply waits its turn.
			var local = ClampBakeBounds( pending.Bounds );
			EnsureNavBoundsCover( navMesh, local );
			switch ( RebakeMode )
			{
				case StructureRebakeMode.Tiles:
					if ( navMesh.IsGenerating )
						return;
					MarkSolidCollidersStaticInBounds( scene, local );
					_fullRegenStartedAt = Time.NowDouble;
					Log.Info( $"[BuildNav] tile regenerate (unload + GenerateTiles) around {local.Center} size {local.Size}" );
					navMesh.UnloadTiles( local );
					navMesh.GenerateTiles( physics, local );
					_pendingLocalBakes.Remove( scene );
					_tileVerifyBounds = local;
					_tileVerifyScene = scene;
					_tileVerifyAt = Time.NowDouble + TileVerifyDelaySeconds;
					return;
				case StructureRebakeMode.Request:
					MarkSolidCollidersStaticInBounds( scene, local );
					_fullRegenStartedAt = Time.NowDouble;
					Log.Info( $"[BuildNav] tile request (unload + RequestTilesGeneration) around {local.Center} size {local.Size}" );
					navMesh.UnloadTiles( local );
					navMesh.RequestTilesGeneration( local );
					_pendingLocalBakes.Remove( scene );
					return;
			}

			if ( RegenerateFullFromPhysics( scene, navMesh, physics, "structure changed", unloadBounds: local ) )
				_pendingLocalBakes.Remove( scene );
			return;
		}

		_pendingLocalBakes.Remove( scene );
		var bounds = ClampBakeBounds( pending.Bounds );
		MarkSolidCollidersStaticInBounds( scene, bounds );
		EnsureNavBoundsCover( navMesh, bounds );

		Log.Info( $"[BuildNav] rebake tiles from physics around {bounds.Center} size {bounds.Size} (physics={(physics is not null)} slope={navMesh.AgentMaxSlope} step={navMesh.AgentStepSize} height={navMesh.AgentHeight})" );
		if ( physics is not null )
			RegenerateTilesFromPhysics( navMesh, physics, bounds );
		else
			navMesh.RequestTilesGeneration( bounds );

		NotifyEnemiesNavUpdated( scene );
	}

	/// <summary>
	/// Raise the nav ceiling so tiles regenerated around a tall structure include it. Only the
	/// vertical extent grows: the tile grid is laid out from the XY bounds, and moving those would
	/// orphan every existing tile. Structures outside the XY bounds are logged, not fixed here.
	/// </summary>
	static void EnsureNavBoundsCover( NavMesh navMesh, BBox bake )
	{
		var current = navMesh.Bounds;
		if ( current.Size.LengthSquared < 1f )
			return;

		var neededTop = bake.Maxs.z + NavBoundsHeadroom;
		var neededBottom = bake.Mins.z - 64f;
		var grow = neededTop > current.Maxs.z || neededBottom < current.Mins.z;
		if ( !grow )
			return;

		var mins = current.Mins.WithZ( Math.Min( current.Mins.z, neededBottom ) );
		var maxs = current.Maxs.WithZ( Math.Max( current.Maxs.z, neededTop ) );
		navMesh.CustomBounds = true;
		navMesh.Bounds = new BBox( mins, maxs );
		Log.Info( $"[BuildNav] nav bounds raised to z {mins.z:0}..{maxs.z:0} (was {current.Mins.z:0}..{current.Maxs.z:0}) so tiles above the baked ceiling can generate" );

		if ( bake.Mins.x < current.Mins.x || bake.Mins.y < current.Mins.y
		     || bake.Maxs.x > current.Maxs.x || bake.Maxs.y > current.Maxs.y )
			Log.Warning( $"[BuildNav] structure at {bake.Center} lies outside the nav XY bounds {current.Mins}..{current.Maxs} — no nav will generate there (needs a full regenerate: nav_regen)." );
	}

	/// <summary>Full regenerate with bounds grown by <paramref name="extraHeight"/> — console `nav_regen`; small scenes only.</summary>
	public static void RegenerateWithHeadroom( Scene scene, float extraHeight )
	{
		if ( !scene.IsValid() )
			return;

		var navMesh = scene.NavMesh;
		var physics = scene.PhysicsWorld;
		if ( navMesh is null || !navMesh.IsEnabled || physics is null )
			return;

		EnsureBuildTraversalSettings( scene );
		if ( !navMesh.CustomBounds )
		{
			var current = navMesh.Bounds;
			navMesh.CustomBounds = true;
			navMesh.Bounds = new BBox( current.Mins - Vector3.Up * 64f, current.Maxs + Vector3.Up * extraHeight );
		}

		RegenerateFullFromPhysics( scene, navMesh, physics, "nav_regen", unloadBounds: null );
	}

	/// <summary>
	/// True while the mesh is stale: a structural rebake is queued (debounce window) or a generate is
	/// running. Path results in this window are not trusted — a path through a wall placed a moment
	/// ago is not a leak to detour around, it is a mesh that has not caught up yet.
	/// </summary>
	public static bool IsNavStale( Scene scene )
	{
		if ( !scene.IsValid() )
			return false;

		if ( IsNavGenerating( scene ) )
			return true;

		return _pendingLocalBakes.TryGetValue( scene, out var pending ) && pending.Structural;
	}

	/// <summary>Console A/B: unload + incremental GenerateTiles around <paramref name="center"/> instead of a full Generate.</summary>
	public static void RegenerateLocalTiles( Scene scene, Vector3 center, float halfExtent )
	{
		if ( !scene.IsValid() )
			return;

		var navMesh = scene.NavMesh;
		var physics = scene.PhysicsWorld;
		if ( navMesh is null || !navMesh.IsEnabled || physics is null )
			return;

		var bounds = new BBox( center - new Vector3( halfExtent ), center + new Vector3( halfExtent ) );
		MarkSolidCollidersStaticInBounds( scene, bounds );
		_fullRegenStartedAt = Time.NowDouble;
		Log.Info( $"[BuildNav] local tile regenerate (unload + GenerateTiles) around {center} ±{halfExtent:0}" );
		navMesh.UnloadTiles( bounds );
		navMesh.GenerateTiles( physics, bounds );
	}

	public static bool IsNavGenerating( Scene scene )
	{
		if ( !scene.IsValid() )
			return false;

		var navMesh = scene.NavMesh;
		return navMesh is not null && navMesh.IsEnabled && navMesh.IsGenerating;
	}

	public static void OnBuildPieceChanged( Scene scene, GameObject pieceRoot )
	{
		if ( !scene.IsValid() || pieceRoot is null || !pieceRoot.IsValid() )
			return;

		EnsureBuildTraversalSettings( scene );

		var bounds = pieceRoot.GetBounds();
		if ( bounds.Size.LengthSquared < 1f )
			bounds = BBox.FromPositionAndSize( pieceRoot.WorldPosition, 120f );

		ScheduleLocalBake( scene, BuildPieceNavPolicy.ExpandForLocalBake( bounds ), structural: true );
		NotifyEnemiesStructureChanged( scene );
	}

	/// <summary>A piece was removed (hammer, collapse, entity damage) — rebake soon: this may open a route.</summary>
	public static void OnBuildPieceBoundsChanged( Scene scene, BBox bounds )
	{
		if ( !scene.IsValid() )
			return;

		EnsureBuildTraversalSettings( scene );
		ScheduleLocalBake( scene, BuildPieceNavPolicy.ExpandForLocalBake( bounds ), urgent: true, structural: true );
		NotifyEnemiesStructureChanged( scene );
	}

	public static void ScheduleObstacleBake( Scene scene, GameObject obstacle )
	{
		if ( !scene.IsValid() || obstacle is null || !obstacle.IsValid() )
			return;

		EnsureBuildTraversalSettings( scene );

		var bounds = obstacle.GetBounds();
		if ( bounds.Size.LengthSquared < 1f && obstacle.Parent.IsValid() )
			bounds = obstacle.Parent.GetBounds();

		if ( bounds.Size.LengthSquared < 1f )
			bounds = BBox.FromPositionAndSize( obstacle.WorldPosition, 160f );

		ScheduleLocalBake( scene, BuildPieceNavPolicy.ExpandForLocalBake( bounds ), structural: true );
	}

	static void ScheduleLocalBake( Scene scene, BBox bounds, bool urgent = false, bool structural = false )
	{
		if ( !IsNavAuthority() )
			return;

		// Hand-built scenes regenerate the whole mesh, and only when a piece actually changed. A
		// spawn-settle or chunk request there is noise — after the load-time generate the mesh
		// already covers the scene. (Treating those as regenerates put an off-nav entity into a
		// loop: settle → regenerate → tiles rebuilding → still off-nav → settle …)
		if ( !structural && scene.IsValid() && !IsStreamedScene( scene ) )
			return;

		var now = Time.NowDouble;
		var handBuilt = structural && !IsStreamedScene( scene );
		var batch = handBuilt ? (CheapRebake ? TileDebounceSeconds : StructuralDebounceSeconds) : LocalBakeBatchSeconds;
		var urgentDelay = handBuilt && CheapRebake ? TileUrgentSeconds : UrgentBakeSeconds;
		var deadline = now + (urgent ? urgentDelay : batch);
		if ( _pendingLocalBakes.TryGetValue( scene, out var pending ) )
		{
			pending.Bounds = pending.Bounds.Size.LengthSquared < 1f
				? bounds
				: UnionBounds( pending.Bounds, bounds );
			pending.Structural |= structural;

			if ( structural && !IsStreamedScene( scene ) )
			{
				// Debounce a building session into one regenerate, but never past the batch cap.
				pending.ExecuteAt = Math.Min( pending.FirstRequestedAt + (CheapRebake ? TileBatchMaxSeconds : StructuralBatchMaxSeconds), deadline );
				return;
			}

			// Keep the first deadline — resetting on every chunk load during streaming postpones bake
			// forever. An urgent request may only pull it earlier.
			if ( deadline < pending.ExecuteAt )
				pending.ExecuteAt = deadline;
			return;
		}

		_pendingLocalBakes[scene] = new PendingLocalBake
		{
			Bounds = bounds,
			ExecuteAt = deadline,
			FirstRequestedAt = now,
			Structural = structural
		};
	}

	static BBox ClampBakeBounds( BBox bounds )
	{
		var center = (bounds.Mins + bounds.Maxs) * 0.5f;
		var half = (bounds.Maxs - bounds.Mins) * 0.5f;
		var max = MaxLocalBakeHalfExtent;
		// Full-height columns: a terrain-chunk rebake whose bounds stopped 2.5 m up regenerated the
		// tiles under a base without the stairs / roof geometry above that line in range, and the
		// polys entities had been climbing vanished until the next piece edit baked them back.
		half = new Vector3(
			Math.Min( half.x, max ),
			Math.Min( half.y, max ),
			max );
		return new BBox( center - half, center + half );
	}

	static BBox UnionBounds( BBox a, BBox b )
	{
		if ( a.Size.LengthSquared < 1f )
			return b;

		if ( b.Size.LengthSquared < 1f )
			return a;

		return new BBox(
			Vector3.Min( a.Mins, b.Mins ),
			Vector3.Max( a.Maxs, b.Maxs ) );
	}

	static void NotifyEnemiesStructureChanged( Scene scene )
	{
		foreach ( var brain in scene.GetAllComponents<EntityBrain>() )
		{
			if ( brain is null || !brain.Enabled || !brain.GameObject.IsValid() )
				continue;

			brain.OnStructureBlockerChanged();
		}
	}

	static void NotifyEnemiesNavUpdated( Scene scene )
	{
		foreach ( var brain in scene.GetAllComponents<EntityBrain>() )
		{
			if ( brain is null || !brain.Enabled || !brain.GameObject.IsValid() )
				continue;

			brain.OnNavBakeComplete();
		}
	}

	static bool IsNavAuthority()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return true;

		return scene.Network is not { Active: true } || Networking.IsHost;
	}
}
