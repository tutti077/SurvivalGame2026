using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Ensures <c>resource_item_library.prefab</c> is in the active scene so <see cref="ResourceCatalog"/> registers item definitions.
/// </summary>
public static class ResourceItemLibraryHost
{
	static readonly string[] LibraryPrefabPaths =
	{
		"prefabs/items/resource_item_library.prefab",
		"assets/prefabs/items/resource_item_library.prefab",
	};

	static GameObject _instance;

	public static void EnsureSpawned( Scene scene )
	{
		if ( scene is null )
			return;

		var existing = scene.Directory.FindByName( "resource_item_library" ).FirstOrDefault();
		if ( existing is { IsValid: true } )
		{
			_instance = existing;
			return;
		}

		if ( _instance is { IsValid: true } && _instance.Scene == scene )
			return;

		foreach ( var path in LibraryPrefabPaths )
		{
			var prefabFile = ResourceLibrary.Get<PrefabFile>( path );
			if ( prefabFile is not null )
			{
				var prefabScene = SceneUtility.GetPrefabScene( prefabFile );
				if ( prefabScene is not null )
				{
					_instance = prefabScene.Clone();
					break;
				}
			}

			var template = GameObject.GetPrefab( path );
			if ( template is null )
				continue;

			_instance = template.Clone();
			break;
		}

		if ( _instance is null )
		{
			Log.Warning( "[ResourceItemLibraryHost] Could not load resource_item_library prefab." );
			return;
		}

		_instance.Name = "resource_item_library";
		_instance.Parent = scene;
	}
}
