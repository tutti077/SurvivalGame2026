public static class MyEditorMenu
{
	[Menu( "Editor", "SurvivalGameBasics/Open Terrain Test Scene" )]
	public static void OpenTerrainTestScene()
	{
		var scene = ResourceLibrary.Get<SceneFile>( "scenes/terrainTest.scene" );
		if ( !scene.IsValid() )
		{
			EditorUtility.DisplayDialog(
				"Scene not found",
				"Could not find scenes/terrainTest.scene in this project.",
				"OK" );
			return;
		}

		var asset = AssetSystem.FindByPath( scene.ResourcePath );
		if ( asset is not null && asset.CanOpenInEditor )
		{
			asset.OpenInEditor();
			return;
		}

		EditorUtility.DisplayDialog(
			"Open terrain test scene",
			"Use File → Open Scene → Assets/scenes/terrainTest.scene.\n\nPlay startup is already set to terrainTest in survivalgamebasics.sbproj.",
			"OK" );
	}
}
