using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Game;

/// <summary>Serializes <see cref="InventoryItemDefinitionEntry"/> rows to the same JSON shape as <see cref="ItemCatalogJsonFile"/> (for editor export to disk).</summary>
public static class ItemCatalogJsonSerializer
{
	public static string SerializeDefinitions( IReadOnlyList<InventoryItemDefinitionEntry> definitions )
	{
		var items = new List<ItemCatalogJsonItem>();
		if ( definitions is not null )
		{
			foreach ( var d in definitions )
			{
				if ( d is null || string.IsNullOrWhiteSpace( d.Id ) )
					continue;

				var id = d.Id.Trim();
				items.Add( new ItemCatalogJsonItem
				{
					Id = id,
					DisplayName = string.IsNullOrWhiteSpace( d.DisplayName ) ? id : d.DisplayName.Trim(),
					IconTexturePath = d.IconTexturePath?.Trim() ?? "",
					Stackable = d.Stackable,
					MaxStackSize = System.Math.Max( 1, d.MaxStackSize ),
					WorldDropPrefabPath = d.WorldDropPrefabPath?.Trim() ?? ""
				} );
			}
		}

		var root = new ItemCatalogJsonFile { Items = items };
		var opts = new JsonSerializerOptions
		{
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};

		return JsonSerializer.Serialize( root, opts );
	}
}
