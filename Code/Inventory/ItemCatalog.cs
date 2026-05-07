using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Game;

/// <summary>
/// Item definitions: load from <see cref="CatalogJsonPath"/> (recommended) and optionally merge prefab references from the same component&apos;s inspector list.
/// </summary>
public sealed class ItemCatalog : Component
{
	[Property] public List<InventoryItemDefinitionEntry> Definitions { get; set; } = new();

	/// <summary>If true, <see cref="CatalogJsonPath"/> is read on awake (content mount). Scene list is used only to overlay <see cref="InventoryItemDefinitionEntry.WorldDroppedPrefab"/> by id.</summary>
	[Property] public bool LoadCatalogFromJson { get; set; } = true;

	/// <summary>Path under the project Assets folder and on the content mount (e.g. <c>item_catalog.json</c>). Use Editor → SurvivalGameBasics → Export Item Catalog to JSON to write inspector rows back to this file.</summary>
	[Property] public string CatalogJsonPath { get; set; } = "catalog/item_catalog.json";

	private Dictionary<string, InventoryItemDefinitionEntry> _map;

	protected override void OnAwake()
	{
		if ( LoadCatalogFromJson && !string.IsNullOrWhiteSpace( CatalogJsonPath ) )
			TryApplyJsonCatalog();

		RebuildMap();
	}

	protected override void OnValidate()
	{
		RebuildMap();
	}

	private void TryApplyJsonCatalog()
	{
		var scenePrefabById = new Dictionary<string, GameObject>( StringComparer.OrdinalIgnoreCase );
		var sceneIconById = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var sceneNameById = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		if ( Definitions is not null )
		{
			foreach ( var row in Definitions )
			{
				if ( row is null || string.IsNullOrWhiteSpace( row.Id ) )
					continue;

				var id = row.Id.Trim();
				if ( row.WorldDroppedPrefab is not null && row.WorldDroppedPrefab.IsValid() )
					scenePrefabById[id] = row.WorldDroppedPrefab;

				if ( !string.IsNullOrWhiteSpace( row.IconTexturePath ) )
					sceneIconById[id] = row.IconTexturePath.Trim();

				if ( !string.IsNullOrWhiteSpace( row.DisplayName ) )
					sceneNameById[id] = row.DisplayName.Trim();
			}
		}

		if ( !TryReadJsonText( CatalogJsonPath, out var json ) )
			return;

		try
		{
			var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
			var root = JsonSerializer.Deserialize<ItemCatalogJsonFile>( json, opts );
			if ( root?.Items is null || root.Items.Count == 0 )
				return;

			var next = new List<InventoryItemDefinitionEntry>( root.Items.Count );
			foreach ( var j in root.Items )
			{
				if ( j is null || string.IsNullOrWhiteSpace( j.Id ) )
					continue;

				var id = j.Id.Trim();
				var icon = NormalizeIconPath( j.IconTexturePath );
				if ( string.IsNullOrWhiteSpace( icon ) && sceneIconById.TryGetValue( id, out var sceneIcon ) )
					icon = NormalizeIconPath( sceneIcon );

				var disp = !string.IsNullOrWhiteSpace( j.DisplayName )
					? j.DisplayName.Trim()
					: (sceneNameById.TryGetValue( id, out var sceneDisp ) && !string.IsNullOrWhiteSpace( sceneDisp )
						? sceneDisp
						: id);

				var e = new InventoryItemDefinitionEntry
				{
					Id = id,
					DisplayName = disp,
					IconTexturePath = icon,
					Stackable = j.Stackable,
					MaxStackSize = Math.Max( 1, j.MaxStackSize ),
					WorldDropPrefabPath = j.WorldDropPrefabPath?.Trim() ?? ""
				};

				if ( scenePrefabById.TryGetValue( id, out var scenePrefab ) && scenePrefab is not null && scenePrefab.IsValid() )
					e.WorldDroppedPrefab = scenePrefab;

				next.Add( e );
			}

			if ( next.Count > 0 )
				Definitions = next;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[ItemCatalog] Failed to parse JSON '{CatalogJsonPath}': {ex.Message}" );
		}
	}

	private static string NormalizeIconPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return "";

		var p = path.Trim().Replace( '\\', '/' );
		if ( !p.EndsWith( ".png", StringComparison.OrdinalIgnoreCase ) )
			return p;

		if ( p.StartsWith( "Assets/", StringComparison.OrdinalIgnoreCase ) )
			p = p["Assets/".Length..];

		var lower = p.ToLowerInvariant();
		if ( lower.StartsWith( "assets/itempngs/", StringComparison.Ordinal ) )
			return lower;

		if ( lower.StartsWith( "itempngs/", StringComparison.Ordinal ) )
			return "assets/" + lower;

		var file = p;
		var slash = p.LastIndexOf( '/' );
		if ( slash >= 0 && slash + 1 < p.Length )
			file = p[(slash + 1)..];

		return $"assets/itempngs/{file.ToLowerInvariant()}";
	}

	private static bool TryReadJsonText( string relativePath, out string text )
	{
		text = null;
		if ( string.IsNullOrWhiteSpace( relativePath ) )
			return false;

		var path = relativePath.Trim().Replace( '\\', '/' );
		try
		{
			if ( FileSystem.Mounted is null || !FileSystem.Mounted.FileExists( path ) )
				return false;

			// Whitelist: use Sandbox FileSystem, not System.IO.File.
			text = FileSystem.Mounted.ReadAllText( path );
			return !string.IsNullOrWhiteSpace( text );
		}
		catch
		{
			return false;
		}
	}

	/// <summary>Editor-assigned drop template from the catalog row (if any).</summary>
	public static GameObject ResolveEditorDropPrefab( InventoryItemDefinitionEntry def )
	{
		if ( def is null )
			return null;

		if ( def.WorldDroppedPrefab is not null && def.WorldDroppedPrefab.IsValid() )
			return def.WorldDroppedPrefab;

		return null;
	}

	/// <summary>Load a prefab resource by path (e.g. <c>prefabs/sword1.prefab</c>) for <see cref="GameObject.Clone"/>.</summary>
	public static PrefabFile TryLoadPrefabFile( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return null;

		var p = path.Trim().Replace( '\\', '/' );
		try
		{
			var pf = ResourceLibrary.Get<PrefabFile>( p );
			return pf;
		}
		catch
		{
			return null;
		}
	}

	private void RebuildMap()
	{
		_map = new Dictionary<string, InventoryItemDefinitionEntry>( StringComparer.OrdinalIgnoreCase );
		if ( Definitions is null )
			return;

		foreach ( var d in Definitions )
		{
			if ( d is null || string.IsNullOrWhiteSpace( d.Id ) )
				continue;

			_map[d.Id.Trim()] = d;
		}
	}

	public bool TryGet( string id, out InventoryItemDefinitionEntry def )
	{
		def = null;
		if ( string.IsNullOrWhiteSpace( id ) || _map is null )
			return false;

		return _map.TryGetValue( id.Trim(), out def );
	}
}
