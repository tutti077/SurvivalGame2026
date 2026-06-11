using Sandbox;

namespace Survival;

static class BuildPreviewFactory
{
	public static GameObject CreatePlaceholder( Scene scene, BuildPieceData data, Transform worldTransform, string name )
	{
		var pieceId = data?.Id ?? string.Empty;
		var go = new GameObject( true, name );
		if ( scene.IsValid() )
			go.Parent = scene;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( "models/dev/box.vmdl" );
		renderer.Tint = BuildPieceCatalog.ParseFallbackColor( data?.FallbackColor ).WithAlpha( 0.55f );

		BuildPrefabUtility.ApplyStandardPieceTransform( go, pieceId, worldTransform );

		var piece = go.Components.Create<BuildPiece>();
		piece.Configure( pieceId, blueprint: false, previewGhost: true );
		return go;
	}
}
