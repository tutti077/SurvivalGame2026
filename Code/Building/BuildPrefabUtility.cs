using Sandbox;

namespace Survival;

public static class BuildPrefabUtility
{
	static readonly string[] PathVariants =
	{
		"{0}",
		"assets/{0}",
	};

	public static GameObject GetTemplate( string prefabPath )
	{
		if ( string.IsNullOrWhiteSpace( prefabPath ) )
			return null;

		for ( var i = 0; i < PathVariants.Length; i++ )
		{
			var path = string.Format( PathVariants[i], prefabPath );
			var template = GameObject.GetPrefab( path );
			if ( template is { IsValid: true } )
				return template;
		}

		return null;
	}

	public static GameObject SpawnPiece( Scene scene, string prefabPath, string pieceId, Transform worldTransform )
	{
		var instance = TryClonePrefab( scene, prefabPath );
		if ( instance is null || !instance.IsValid() )
		{
			if ( !BuildPieceCatalog.TryGet( pieceId, out var data ) )
				return null;

			instance = BuildPreviewFactory.CreatePlaceholder( scene, data, worldTransform, $"build_{pieceId}" );
			if ( instance is null || !instance.IsValid() )
				return null;
		}
		else if ( scene.IsValid() )
		{
			instance.Parent = scene;
		}

		ApplyStandardPieceTransform( instance, pieceId, worldTransform );
		return instance;
	}

	public static GameObject CreatePreviewClone( Scene scene, BuildPieceData data, GameObject prefabTemplate )
	{
		if ( data is null || string.IsNullOrWhiteSpace( data.Id ) )
			return null;

		GameObject instance;
		if ( prefabTemplate is { IsValid: true } )
			instance = prefabTemplate.Clone();
		else
			instance = BuildPreviewFactory.CreatePlaceholder( scene, data, Transform.Zero, "build_preview" );

		if ( instance is null || !instance.IsValid() )
			return null;

		if ( scene.IsValid() )
			instance.Parent = scene;

		instance.Name = "build_preview";
		instance.Tags.Add( "buildpreview" );
		ApplyStandardPieceTransform( instance, data.Id, Transform.Zero );
		return instance;
	}

	static GameObject TryClonePrefab( Scene scene, string prefabPath )
	{
		if ( string.IsNullOrWhiteSpace( prefabPath ) )
			return null;

		for ( var i = 0; i < PathVariants.Length; i++ )
		{
			var path = string.Format( PathVariants[i], prefabPath );
			var template = GameObject.GetPrefab( path );
			if ( template is { IsValid: true } )
				return template.Clone();

			var prefabFile = ResourceLibrary.Get<PrefabFile>( path );
			if ( prefabFile is null )
				continue;

			var prefabScene = SceneUtility.GetPrefabScene( prefabFile );
			if ( prefabScene is null )
				continue;

			var fromScene = prefabScene.Clone();
			if ( fromScene.IsValid() )
				return fromScene;
		}

		return null;
	}

	/// <summary>World yaw from placement. Pitch baked in mesh stays off the root transform.</summary>
	public static void ApplyStandardPieceTransform( GameObject instance, string pieceId, Transform worldTransform )
	{
		if ( instance is null || !instance.IsValid() )
			return;

		var yawOnly = Rotation.FromYaw( worldTransform.Rotation.Angles().yaw );
		var pitch = BuildPieceVisual.UsesBakedMeshRotation( pieceId )
			? Rotation.Identity
			: BuildModuleDimensions.GetPrefabLocalRotation( pieceId );

		instance.LocalScale = Vector3.One;
		instance.LocalRotation = Rotation.Identity;
		instance.WorldPosition = worldTransform.Position;
		instance.WorldRotation = yawOnly * pitch;

		BuildPieceVisual.Ensure( instance, pieceId );

		var preview = instance.Tags.Has( "buildpreview" );
		BuildPieceCollider.Ensure( instance, pieceId, preview );
	}
}
