using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Game;

/// <summary>Root object for <see cref="ItemCatalog.CatalogJsonPath"/> JSON files.</summary>
public sealed class ItemCatalogJsonFile
{
	[JsonPropertyName( "items" )]
	public List<ItemCatalogJsonItem> Items { get; set; } = new();
}

public sealed class ItemCatalogJsonItem
{
	[JsonPropertyName( "id" )]
	public string Id { get; set; }

	[JsonPropertyName( "displayName" )]
	public string DisplayName { get; set; }

	[JsonPropertyName( "iconTexturePath" )]
	public string IconTexturePath { get; set; }

	[JsonPropertyName( "stackable" )]
	public bool Stackable { get; set; } = true;

	[JsonPropertyName( "maxStackSize" )]
	public int MaxStackSize { get; set; } = 64;

	/// <summary>Resource path to a prefab (e.g. <c>prefabs/sword1.prefab</c>). Resolved at runtime; scene rows can still override with a direct prefab reference.</summary>
	[JsonPropertyName( "worldDropPrefabPath" )]
	public string WorldDropPrefabPath { get; set; }
}
