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
	static bool _isFallbackOnly;
	static string _sourceJson = string.Empty;
	static int _contentVersion;

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

	public static bool IsFallbackOnly
	{
		get
		{
			EnsureLoaded();
			return _isFallbackOnly;
		}
	}

	public static int ContentVersion
	{
		get
		{
			EnsureLoaded();
			return _contentVersion;
		}
	}

	public static void ForceReload()
	{
		_loaded = false;
		_loadedJsonHash = 0;
		ReloadFromDisk();
	}

	public static string ExportSourceJson()
	{
		EnsureLoaded();
		if ( !string.IsNullOrWhiteSpace( _sourceJson ) )
			return _sourceJson;

		foreach ( var path in GetPathCandidates() )
		{
			try
			{
				var json = FileSystem.Mounted.ReadAllText( path );
				if ( !string.IsNullOrWhiteSpace( json ) )
					return json;
			}
			catch
			{
				// try next
			}
		}

		return string.Empty;
	}

	public static bool ReplaceFromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			return false;

		if ( !TryParseResources( json, out var parsed ) || parsed.Count == 0 )
			return false;

		Resources.Clear();
		Resources.AddRange( parsed );
		RebuildLookup();
		_sourceJson = json;
		_loadedJsonHash = StringComparer.Ordinal.GetHashCode( json );
		_isFallbackOnly = false;
		_loaded = true;
		_contentVersion++;
		Log.Info( $"[ResourceDefinitionCatalog] Applied host resource catalog ({Resources.Count} resources)." );
		return true;
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
		{
			if ( _isFallbackOnly )
				TryReloadIfFallback();
			return;
		}

		ReloadFromDisk();
	}

	static void TryReloadIfFallback()
	{
		if ( !TryLoadFromFile() )
			return;

		_isFallbackOnly = false;
		_contentVersion++;
		Log.Info( $"[ResourceDefinitionCatalog] Recovered full resource list ({Resources.Count} resources)." );
	}

	static void ReloadFromDisk()
	{
		Resources.Clear();
		ById.Clear();
		_sourceJson = string.Empty;
		_isFallbackOnly = false;

		if ( TryLoadFromFile() )
		{
			_loaded = true;
			_contentVersion++;
			return;
		}

		Resources.AddRange( CreateFallbackResources() );
		RebuildLookup();
		_isFallbackOnly = true;
		_loaded = true;
		_loadedJsonHash = 0;
		_contentVersion++;
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

	static bool TryLoadFromFile()
	{
		foreach ( var path in GetPathCandidates() )
		{
			try
			{
				var json = FileSystem.Mounted.ReadAllText( path );
				if ( string.IsNullOrWhiteSpace( json ) )
					continue;

				if ( !TryParseResources( json, out var parsed ) || parsed.Count == 0 )
					continue;

				Resources.Clear();
				Resources.AddRange( parsed );
				RebuildLookup();
				_sourceJson = json;
				_loadedJsonHash = StringComparer.Ordinal.GetHashCode( json );
				return true;
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[ResourceDefinitionCatalog] Failed to load '{path}': {ex.Message}" );
			}
		}

		return false;
	}

	static bool TryParseResources( string json, out List<ResourceDefinitionData> parsed )
	{
		parsed = null;
		try
		{
			var file = JsonSerializer.Deserialize<ResourceDefinitionsFile>( json, JsonOptions );
			if ( file?.Resources is null || file.Resources.Count == 0 )
				return false;

			parsed = new List<ResourceDefinitionData>();
			for ( var i = 0; i < file.Resources.Count; i++ )
			{
				var entry = file.Resources[i];
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Id ) )
					continue;

				parsed.Add( entry );
			}

			return parsed.Count > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[ResourceDefinitionCatalog] JSON parse failed: {ex.Message}" );
			return false;
		}
	}

	static IEnumerable<string> GetPathCandidates()
	{
		yield return ResourceFilePath;
		yield return "assets/data/resources.json";
		yield return "/data/resources.json";
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
