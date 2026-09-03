using Sandbox;

namespace Survival;

public static class BuildAuthority
{
	public static bool TryPlacePiece(
		GameObject placer,
		string pieceId,
		Transform transform,
		bool blueprint,
		out GameObject spawned )
	{
		spawned = null;
		if ( !placer.IsValid() || string.IsNullOrWhiteSpace( pieceId ) )
			return false;

		// Broken build hammer cannot place — repair it at a workbench first.
		if ( ToolDurability.IsActiveToolBroken( placer ) )
			return false;

		if ( !BuildPieceCatalog.TryGet( pieceId, out var data ) || string.IsNullOrWhiteSpace( data.Prefab ) )
			return false;

		var scene = placer.Scene.IsValid() ? placer.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		spawned = BuildPrefabUtility.SpawnPiece( scene, data.Prefab, pieceId, transform );
		if ( spawned is null || !spawned.IsValid() )
		{
			Log.Warning( $"[BuildAuthority] Missing prefab '{data.Prefab}' for piece '{pieceId}'." );
			return false;
		}

		spawned.Name = $"build_{pieceId}";

		var piece = spawned.Components.Get<BuildPiece>() ?? spawned.Components.Create<BuildPiece>();
		piece.Configure( pieceId, blueprint, previewGhost: false );

		// Host NetworkSpawn so remotes see placed walls/chests (prefabs are already Object mode).
		HostNetworkSpawn.TrySpawn( spawned );

		BuildSnapPlacement.InvalidatePieceCache();
		BuildNavMeshSync.OnBuildPieceChanged( scene, spawned );

		// Valheim-style: placement always succeeds, then the solver may collapse the piece
		// (and anything that only stood because of intermediate state) immediately.
		BuildStructuralIntegrity.HostOnPlaced( piece );
		if ( !spawned.IsValid() )
		{
			// Nothing was actually built — the swing is free (durability only ticks on real effect).
			spawned = null;
			return true;
		}

		// Build hammer durability: 1 tick per placement that actually stood.
		ToolDurability.HostAddWearToActiveTool( placer );

		if ( !blueprint )
			placer.Components.Get<PlayerQuests>()?.HostReport( QuestEventIds.PieceBuilt, pieceId );

		return true;
	}

	public static bool TryRepairBuildPiece( GameObject placer, BuildPiece target )
	{
		if ( !placer.IsValid() || target is null || !target.IsValid() || target.IsPreviewGhost )
			return false;

		if ( !target.IsBlueprint )
			return false;

		if ( ToolDurability.IsActiveToolBroken( placer ) )
			return false;

		target.Configure( target.PieceId, blueprint: false, previewGhost: false );
		BuildNavMeshSync.OnBuildPieceChanged( target.Scene, target.GameObject );

		// Build hammer durability: 1 tick per successful structure repair.
		ToolDurability.HostAddWearToActiveTool( placer );
		return true;
	}

	public static bool TryDestroyBuildPiece( GameObject placer, BuildPiece target )
	{
		if ( !placer.IsValid() || target is null || !target.IsValid() || target.IsPreviewGhost )
			return false;

		var scene = target.Scene;
		var removedRoot = target.GameObject;
		var bounds = target.GameObject.GetBounds();
		if ( bounds.Size.LengthSquared < 1f )
			bounds = BBox.FromPositionAndSize( target.GameObject.WorldPosition, 120f );

		target.GameObject.Destroy();
		BuildSnapPlacement.InvalidatePieceCache();
		if ( scene.IsValid() )
			BuildNavMeshSync.OnBuildPieceBoundsChanged( scene, bounds );

		// Re-solve what the piece used to touch — collapses anything it solely held up. The root is
		// passed so the deferred-destroyed piece can't keep supporting the structure this frame.
		BuildStructuralIntegrity.HostOnRemoved( scene, bounds, removedRoot );

		return true;
	}
}
