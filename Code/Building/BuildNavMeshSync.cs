using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// NavMesh comes from the PhysicsWorld (Recast). testscene1 also had a baked .navdata —
/// alert must <see cref="NavMesh.GenerateTiles"/> from live physics (same as stair placement),
/// not only queue RequestTilesGeneration against stale bake.
/// </summary>
public static class BuildNavMeshSync
{
	const float BuildTraversalMaxSlope = 50f;
	const float BuildTraversalStepSize = 40f;
	/// <summary>Coalesce chunk/spawn notifies — avoids GenerateTiles thrash while streaming.</summary>
	const double LocalBakeBatchSeconds = 0.85;
	const float ChaseCorridorPadding = 640f;
	/// <summary>Cap deferred bake size so streaming unions cannot Recast half the world at once.</summary>
	const float MaxLocalBakeHalfExtent = 512f;

	static readonly Dictionary<Scene, PendingLocalBake> _pendingLocalBakes = new();

	sealed class PendingLocalBake
	{
		public BBox Bounds;
		public double ExecuteAt;
	}

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
			navMesh.GenerateTiles( physics, bounds );
			NotifyEnemiesNavUpdated( scene );
			return;
		}

		navMesh.RequestTilesGeneration( bounds );
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

		_pendingLocalBakes.Remove( scene );

		var navMesh = scene.NavMesh;
		if ( navMesh is null || !navMesh.IsEnabled )
			return;

		var bounds = ClampBakeBounds( pending.Bounds );
		MarkSolidCollidersStaticInBounds( scene, bounds );

		var physics = scene.PhysicsWorld;
		if ( physics is not null )
			navMesh.GenerateTiles( physics, bounds );
		else
			navMesh.RequestTilesGeneration( bounds );

		NotifyEnemiesNavUpdated( scene );
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

		ScheduleLocalBake( scene, BuildPieceNavPolicy.ExpandForLocalBake( bounds ) );
		NotifyEnemiesStructureChanged( scene );
	}

	public static void OnBuildPieceBoundsChanged( Scene scene, BBox bounds )
	{
		if ( !scene.IsValid() )
			return;

		EnsureBuildTraversalSettings( scene );
		ScheduleLocalBake( scene, BuildPieceNavPolicy.ExpandForLocalBake( bounds ) );
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

		ScheduleLocalBake( scene, BuildPieceNavPolicy.ExpandForLocalBake( bounds ) );
	}

	static void ScheduleLocalBake( Scene scene, BBox bounds )
	{
		if ( !IsNavAuthority() )
			return;

		if ( _pendingLocalBakes.TryGetValue( scene, out var pending ) )
		{
			pending.Bounds = pending.Bounds.Size.LengthSquared < 1f
				? bounds
				: UnionBounds( pending.Bounds, bounds );
			// Keep the first deadline — resetting on every chunk load during streaming postpones bake forever.
			return;
		}

		_pendingLocalBakes[scene] = new PendingLocalBake
		{
			Bounds = bounds,
			ExecuteAt = Time.NowDouble + LocalBakeBatchSeconds
		};
	}

	static BBox ClampBakeBounds( BBox bounds )
	{
		var center = (bounds.Mins + bounds.Maxs) * 0.5f;
		var half = (bounds.Maxs - bounds.Mins) * 0.5f;
		var max = MaxLocalBakeHalfExtent;
		half = new Vector3(
			Math.Min( half.x, max ),
			Math.Min( half.y, max ),
			Math.Min( Math.Max( half.z, 128f ), max ) );
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
