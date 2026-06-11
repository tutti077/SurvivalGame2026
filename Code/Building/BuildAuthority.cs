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
		BuildSnapPlacement.InvalidatePieceCache();

		return true;
	}

	public static bool TryRepairBuildPiece( GameObject placer, BuildPiece target )
	{
		if ( !placer.IsValid() || target is null || !target.IsValid() || target.IsPreviewGhost )
			return false;

		if ( !target.IsBlueprint )
			return false;

		target.Configure( target.PieceId, blueprint: false, previewGhost: false );
		return true;
	}

	public static bool TryDestroyBuildPiece( GameObject placer, BuildPiece target )
	{
		if ( !placer.IsValid() || target is null || !target.IsValid() || target.IsPreviewGhost )
			return false;

		target.GameObject.Destroy();
		BuildSnapPlacement.InvalidatePieceCache();
		return true;
	}
}
