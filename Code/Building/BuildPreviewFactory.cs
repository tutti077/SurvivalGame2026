using Sandbox;

namespace Survival;

/// <summary>
/// Fallback when a catalog prefab cannot be cloned. Uses the same visual path as real prefabs
/// (<see cref="BuildPieceVisual"/>) — not the dev box.
/// </summary>
static class BuildPreviewFactory
{
	public static GameObject CreatePlaceholder( Scene scene, BuildPieceData data, Transform worldTransform, string name )
	{
		var pieceId = data?.Id ?? string.Empty;
		var go = new GameObject( true, name );
		if ( scene.IsValid() )
			go.Parent = scene;

		go.Components.Create<BoxCollider>().Scale = BuildColliderSnap.PrefabColliderSize;

		BuildPrefabUtility.ApplyStandardPieceTransform( go, pieceId, worldTransform );

		var piece = go.Components.Create<BuildPiece>();
		piece.Configure( pieceId, blueprint: false, previewGhost: true );
		return go;
	}
}
