using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Spawns catalog-only <see cref="ResourceItemDefinition"/> children from <c>data/resources.json</c>.
/// </summary>
public static class ResourceItemLibraryHost
{
	static readonly string[] LibraryPrefabPaths =
	{
		"prefabs/items/resource_item_library.prefab",
		"assets/prefabs/items/resource_item_library.prefab",
	};

	static GameObject _instance;
	static int _spawnedJsonHash;

	public static void EnsureSpawned( Scene scene )
	{
		if ( scene is null )
			return;

		ResourceDefinitionCatalog.EnsureLoaded();
		var jsonHash = ResourceDefinitionCatalog.LoadedJsonHash;

		var existing = scene.Directory.FindByName( "resource_item_library" ).FirstOrDefault();
		if ( existing is { IsValid: true } )
			_instance = existing;

		if ( _instance is null || !_instance.IsValid() || _instance.Scene != scene )
			_instance = TryCloneLibraryPrefab( scene );

		if ( _instance is null )
		{
			Log.Warning( "[ResourceItemLibraryHost] Could not create resource_item_library root." );
			return;
		}

		_instance.Name = "resource_item_library";
		_instance.Parent = scene;
		_instance.WorldPosition = new Vector3( 0f, 0f, -4096f );

		if ( jsonHash == _spawnedJsonHash )
			return;

		RebuildFromJson();
		_spawnedJsonHash = jsonHash;
	}

	static GameObject TryCloneLibraryPrefab( Scene scene )
	{
		foreach ( var path in LibraryPrefabPaths )
		{
			var prefabFile = ResourceLibrary.Get<PrefabFile>( path );
			if ( prefabFile is not null )
			{
				var prefabScene = SceneUtility.GetPrefabScene( prefabFile );
				if ( prefabScene is not null )
					return prefabScene.Clone();
			}

			var template = GameObject.GetPrefab( path );
			if ( template is not null )
				return template.Clone();
		}

		return new GameObject( true, "resource_item_library" );
	}

	static void RebuildFromJson()
	{
		if ( _instance is null || !_instance.IsValid() )
			return;

		foreach ( var child in _instance.Children.ToArray() )
		{
			if ( child is { IsValid: true } )
				child.Destroy();
		}

		foreach ( var data in ResourceDefinitionCatalog.All )
		{
			if ( data is null || string.IsNullOrWhiteSpace( data.Id ) )
				continue;

			var child = new GameObject( false, data.Id );
			child.Parent = _instance;

			var definition = child.Components.Create<ResourceItemDefinition>();
			ResourceDefinitionCatalog.ApplyTo( definition, data );
			child.Enabled = true;
		}
	}

	public static void ForceReload()
	{
		ResourceDefinitionCatalog.ForceReload();
		_spawnedJsonHash = 0;
		if ( _instance is not { IsValid: true } )
			return;

		RebuildFromJson();
		_spawnedJsonHash = ResourceDefinitionCatalog.LoadedJsonHash;
	}
}
