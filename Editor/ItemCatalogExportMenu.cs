using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game;

namespace Editor;

/// <summary>Writes the selected <see cref="ItemCatalog"/> definitions to <see cref="ItemCatalog.CatalogJsonPath"/> under the project Assets folder.</summary>
public static class ItemCatalogExportMenu
{
	[Menu( "Editor", "SurvivalGameBasics/Export Item Catalog to JSON" )]
	public static void ExportItemCatalogToJson()
	{
		using var scope = SceneEditorSession.Scope();

		var catalogs = new List<ItemCatalog>();
		foreach ( var go in EditorScene.Selection.OfType<GameObject>().Where( static x => x is not Scene ) )
		{
			var c = FindItemCatalogRecursive( go );
			if ( c is not null && !catalogs.Contains( c ) )
				catalogs.Add( c );
		}

		if ( catalogs.Count == 0 )
		{
			EditorUtility.DisplayDialog(
				"Export Item Catalog",
				"Select a GameObject that has an ItemCatalog on itself or a child, then run this again.\n\n" +
				"This writes the current inspector rows to the JSON file set on Catalog Json Path (under your project Assets folder).",
				"OK" );
			return;
		}

		if ( catalogs.Count > 1 )
		{
			EditorUtility.DisplayDialog(
				"Export Item Catalog",
				"Multiple ItemCatalog components were found in the selection. Select one object hierarchy at a time.",
				"OK" );
			return;
		}

		var catalog = catalogs[0];
		var project = Project.Current;
		if ( project is null )
		{
			EditorUtility.DisplayDialog( "Export Item Catalog", "No active project.", "OK" );
			return;
		}

		var rel = catalog.CatalogJsonPath?.Trim().Replace( '\\', '/' ) ?? "";
		if ( string.IsNullOrEmpty( rel ) )
		{
			EditorUtility.DisplayDialog( "Export Item Catalog", "Catalog Json Path is empty on the ItemCatalog component.", "OK" );
			return;
		}

		if ( rel.Contains( "..", System.StringComparison.Ordinal ) || Path.IsPathRooted( rel ) )
		{
			EditorUtility.DisplayDialog( "Export Item Catalog", "Catalog Json Path must be relative to Assets (no '..', no drive/root paths).", "OK" );
			return;
		}

		var assetsDir = project.GetAssetsPath();
		var fullPath = Path.GetFullPath( Path.Combine( assetsDir, rel.Replace( '/', Path.DirectorySeparatorChar ) ) );

		var dir = Path.GetDirectoryName( fullPath );
		if ( !string.IsNullOrEmpty( dir ) && !Directory.Exists( dir ) )
			Directory.CreateDirectory( dir );

		var json = ItemCatalogJsonSerializer.SerializeDefinitions( catalog.Definitions );
		File.WriteAllText( fullPath, json );

		AssetSystem.RegisterFile( fullPath );

		EditorUtility.DisplayDialog(
			"Export Item Catalog",
			$"Wrote {catalog.Definitions?.Count ?? 0} item(s) to:\n{fullPath}",
			"OK" );
	}

	private static ItemCatalog FindItemCatalogRecursive( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			return null;

		var self = root.Components.Get<ItemCatalog>();
		if ( self is not null )
			return self;

		foreach ( var child in root.Children )
		{
			var found = FindItemCatalogRecursive( child );
			if ( found is not null )
				return found;
		}

		return null;
	}
}
