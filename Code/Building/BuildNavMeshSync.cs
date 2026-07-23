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
	const double LocalBakeBatchSeconds = 0.55;
	const float ChaseCorridorPadding = 640f;

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
	/// Spawn / chunk load: ensure nav tiles exist around a point from the live PhysicsWorld
	/// so streamed terrain scavs can snap onto mesh instead of falling forever.
	/// </summary>
	public static void EnsureNavAroundPoint( Scene scene, Vector3 worldPos, float padding = 768f )
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

		MarkSolidCollidersStaticInBounds( scene, bounds );

		var physics = scene.PhysicsWorld;
		if ( physics is not null )
			navMesh.GenerateTiles( physics, bounds );
		else
			navMesh.RequestTilesGeneration( bounds );
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
		var bounds = new BBox( mins, maxs );

		var physics = scene.PhysicsWorld;
		if ( physics is not null )
		{
			// Synchronous tile rebuild from current colliders — this is what actually updates pathing.
			navMesh.GenerateTiles( physics, bounds );
			NotifyEnemiesNavUpdated( scene );
			return;
		}

		navMesh.RequestTilesGeneration( bounds );
	}

	/// <summary>
	/// Editor cubes default to Static=false (physics collision only). Build pieces force Static=true.
	/// Nav generation only includes Static/Keyframed bodies — so we promote solids before bake.
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

			var go = col.GameObject;
			if ( IsPawnOrEnemyHierarchy( go ) )
				continue;

			var goBounds = go.GetBounds();
			if ( goBounds.Size.LengthSquared < 1f )
				goBounds = BBox.FromPositionAndSize( go.WorldPosition, 80f );

			if ( !BoundsOverlap( bounds, goBounds ) )
				continue;

			// BoxCollider / ModelCollider expose Static — same flag BuildPiece sets.
			if ( col is BoxCollider box && !box.Static )
				box.Static = true;
			else if ( col is ModelCollider model && !model.Static )
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

		MarkSolidCollidersStaticInBounds( scene, pending.Bounds );

		var physics = scene.PhysicsWorld;
		if ( physics is not null )
			navMesh.GenerateTiles( physics, pending.Bounds );
		else
			navMesh.RequestTilesGeneration( pending.Bounds );

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
