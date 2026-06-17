using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Terrain/world nav at load; build blockers use physics only.
/// Walkable-path pieces schedule batched local tile generation — never full-scene rebake.
/// </summary>
public static class BuildNavMeshSync
{
	const float BuildTraversalMaxSlope = 50f;
	const float BuildTraversalStepSize = 40f;
	const double LocalBakeBatchSeconds = 0.75;

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
		navMesh.IncludeKeyframedBodies = false;
		navMesh.DeferGeneration = false;

		if ( navMesh.AgentMaxSlope < BuildTraversalMaxSlope )
			navMesh.AgentMaxSlope = BuildTraversalMaxSlope;

		if ( navMesh.AgentStepSize < BuildTraversalStepSize )
			navMesh.AgentStepSize = BuildTraversalStepSize;
	}

	/// <summary>Hook for terrain streaming — request tiles for a loaded chunk bounds once.</summary>
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

		var piece = pieceRoot.Components.Get<BuildPiece>();
		var pieceId = piece?.PieceId ?? string.Empty;
		if ( BuildPieceNavPolicy.GetCategory( pieceId ) == BuildNavCategory.Blocking )
		{
			NotifyEnemiesStructureChanged( scene );
			return;
		}

		var bounds = pieceRoot.GetBounds();
		if ( bounds.Size.LengthSquared < 1f )
			bounds = BBox.FromPositionAndSize( pieceRoot.WorldPosition, 120f );

		ScheduleLocalBake( scene, BuildPieceNavPolicy.ExpandForLocalBake( bounds ) );
	}

	public static void OnBuildPieceBoundsChanged( Scene scene, BBox bounds )
	{
		if ( !scene.IsValid() )
			return;

		EnsureBuildTraversalSettings( scene );
		NotifyEnemiesStructureChanged( scene );
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
			pending.ExecuteAt = Time.NowDouble + LocalBakeBatchSeconds;
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
