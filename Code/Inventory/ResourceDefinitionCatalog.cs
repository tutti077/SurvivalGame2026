using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads harvestable materials and stackable goods from <c>data/resources.json</c>.</summary>
public static class ResourceDefinitionCatalog
{
	const string ResourceFilePath = "data/resources.json";

	static readonly List<ResourceDefinitionData> Resources = new();
	static readonly Dictionary<string, ResourceDefinitionData> ById =
		new( StringComparer.OrdinalIgnoreCase );

	static bool _loaded;
	static int _loadedJsonHash;

	public static IReadOnlyList<ResourceDefinitionData> All
	{
		get
		{
			EnsureLoaded();
			return Resources;
		}
	}

	public static int LoadedJsonHash
	{
		get
		{
			EnsureLoaded();
			return _loadedJsonHash;
		}
	}

	public static void ForceReload()
	{
		_loaded = false;
		_loadedJsonHash = 0;
		ReloadFromDisk();
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		ReloadFromDisk();
	}

	static void ReloadFromDisk()
	{
		var jsonHash = TryReadJsonHash();
		_loaded = true;
		_loadedJsonHash = jsonHash;
		Resources.Clear();
		ById.Clear();

		if ( TryLoadFromFile() )
			return;

		Resources.AddRange( CreateFallbackResources() );
		RebuildLookup();
		Log.Warning( "[ResourceDefinitionCatalog] Using built-in fallback resources (json missing or invalid)." );
	}

	public static bool TryGet( string resourceId, out ResourceDefinitionData data )
	{
		EnsureLoaded();
		data = null;
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		return ById.TryGetValue( resourceId, out data );
	}

	public static ResourceCatalog.ResourceDefinition ResolveCatalogEntry( string resourceId )
	{
		if ( !TryGet( resourceId, out var data ) )
			return default;

		return new ResourceCatalog.ResourceDefinition(
			data.DisplayName,
			MenuUiTextures.TryLoad( data.Icon ),
			ParseFallbackColor( data.FallbackColor ),
			Math.Max( 1, data.MaxStack ) );
	}

	public static string GetIconPath( string resourceId )
	{
		return TryGet( resourceId, out var data ) && !string.IsNullOrWhiteSpace( data.Icon )
			? data.Icon
			: null;
	}

	public static int GetMaxStack( string resourceId )
	{
		return TryGet( resourceId, out var data )
			? Math.Max( 1, data.MaxStack )
			: 64;
	}

	public static void ApplyTo( ResourceItemDefinition definition, ResourceDefinitionData data )
	{
		if ( definition is null || data is null || string.IsNullOrWhiteSpace( data.Id ) )
			return;

		definition.ApplyCatalogData( data );
	}

	/// <summary>When a scene prefab shares a catalog id, copy display fields from JSON.</summary>
	public static bool TryApplyIdentity( ResourceItemDefinition definition )
	{
		if ( definition is null || string.IsNullOrWhiteSpace( definition.ResourceId ) )
			return false;

		if ( !TryGet( definition.ResourceId, out var data ) )
			return false;

		definition.DisplayName = data.DisplayName;
		definition.Icon = data.Icon;
		definition.MaxStack = Math.Max( 1, data.MaxStack );
		definition.FallbackColor = ParseFallbackColor( data.FallbackColor );
		definition.InvalidateIconCache();
		return true;
	}

	public static Color ParseFallbackColor( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			return new Color( 0.45f, 0.48f, 0.52f );

		var parts = value.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if ( parts.Length < 3 )
			return new Color( 0.45f, 0.48f, 0.52f );

		if ( !float.TryParse( parts[0], out var r ) || !float.TryParse( parts[1], out var g ) || !float.TryParse( parts[2], out var b ) )
			return new Color( 0.45f, 0.48f, 0.52f );

		var a = parts.Length > 3 && float.TryParse( parts[3], out var alpha ) ? alpha : 1f;
		return new Color( r, g, b, a );
	}

	static int TryReadJsonHash()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( ResourceFilePath ) )
				return 0;

			return StringComparer.Ordinal.GetHashCode( FileSystem.Mounted.ReadAllText( ResourceFilePath ) );
		}
		catch
		{
			return 0;
		}
	}

	static bool TryLoadFromFile()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( ResourceFilePath ) )
				return false;

			var json = FileSystem.Mounted.ReadAllText( ResourceFilePath );
			var file = JsonSerializer.Deserialize<ResourceDefinitionsFile>( json, JsonOptions );
			if ( file?.Resources is null || file.Resources.Count == 0 )
				return false;

			for ( var i = 0; i < file.Resources.Count; i++ )
			{
				var entry = file.Resources[i];
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Id ) )
					continue;

				Resources.Add( entry );
			}

			if ( Resources.Count == 0 )
				return false;

			RebuildLookup();
			return true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[ResourceDefinitionCatalog] Failed to load {ResourceFilePath}: {ex.Message}" );
			return false;
		}
	}

	static void RebuildLookup()
	{
		ById.Clear();
		for ( var i = 0; i < Resources.Count; i++ )
		{
			var entry = Resources[i];
			if ( entry is null || string.IsNullOrWhiteSpace( entry.Id ) )
				continue;

			ById[ResourceCatalog.NormalizeResourceId( entry.Id )] = entry;
		}
	}

	static IEnumerable<ResourceDefinitionData> CreateFallbackResources()
	{
		yield return new ResourceDefinitionData
		{
			Id = "resource_stone",
			DisplayName = "Stone",
			Icon = "ui/items/resource_stone.jpg",
			MaxStack = 20,
			FallbackColor = "0.58,0.5,0.42,1",
		};
		yield return new ResourceDefinitionData
		{
			Id = "resource_plantFiber",
			DisplayName = "Plant Fiber",
			Icon = "ui/items/resource_plantFiber.png",
			MaxStack = 20,
			FallbackColor = "0.28,0.48,0.22,1",
		};
		yield return new ResourceDefinitionData
		{
			Id = "resource_woodBasic",
			DisplayName = "Wood",
			Icon = "ui/items/resource_woodBasic.png",
			MaxStack = 20,
			FallbackColor = "0.45,0.32,0.18,1",
		};
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class ResourceDefinitionsFile
	{
		public List<ResourceDefinitionData> Resources { get; set; } = new();
	}
}
