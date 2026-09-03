using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Survival;

public sealed class CraftingIngredient
{
	public string ResourceId { get; set; } = string.Empty;
	public int Amount { get; set; } = 1;
}

public sealed class CraftingStatLine
{
	public string Label { get; set; } = string.Empty;
	public string Value { get; set; } = string.Empty;
}

public sealed class CraftingRecipe
{
	/// <summary>Canonical crafted item id (inventory / equipment / recipe lookup).</summary>
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	[JsonPropertyName( "icon" )]
	public string Icon { get; set; } = string.Empty;

	public string UnlockId { get; set; } = string.Empty;
	public List<CraftingIngredient> Ingredients { get; set; } = new();

	/// <summary>How many finished items one craft produces (inventory grant). Ingredient amounts are the cost for that craft.</summary>
	[JsonPropertyName( "outputAmount" )]
	public int OutputAmount { get; set; } = 1;

	public List<CraftingStatLine> Stats { get; set; } = new();

	/// <summary>Inventory stack size for the crafted output. Defaults to 1 when unset.</summary>
	[JsonPropertyName( "maxStack" )]
	public int MaxStack { get; set; }

	/// <summary>
	/// Ammo family key shared with weapons (e.g. <c>bow</c>). Empty = not ammo.
	/// </summary>
	[JsonPropertyName( "ammoType" )]
	public string AmmoType { get; set; } = string.Empty;

	/// <summary>Base damage contributed by this ammo when fired (weapons add their own scaled damage).</summary>
	[JsonPropertyName( "damage" )]
	public float Damage { get; set; }

	/// <summary>
	/// Crafting station required nearby (e.g. <c>campfire</c>). Empty = craft anywhere.
	/// </summary>
	[JsonPropertyName( "requiredStation" )]
	public string RequiredStation { get; set; } = string.Empty;

	/// <summary>
	/// Stations whose crafting menus list this recipe (a recipe can appear at several benches).
	/// Empty = default: <see cref="RequiredStation"/> when set (campfire food), else <c>workbench</c>.
	/// Listing here does not gate crafting — only <see cref="RequiredStation"/> does.
	/// </summary>
	[JsonPropertyName( "stations" )]
	public List<string> Stations { get; set; } = new();


	/// <summary>Items granted / space required for one craft.</summary>
	public int TotalOutputAmount => Math.Max( 1, OutputAmount );

	public bool RequiresStation => !string.IsNullOrWhiteSpace( RequiredStation );

	/// <summary>Whether this recipe shows in the crafting menu of the given station.</summary>
	public bool AppearsAtStation( string stationId )
	{
		if ( string.IsNullOrWhiteSpace( stationId ) )
			return false;

		if ( Stations is { Count: > 0 } )
		{
			for ( var i = 0; i < Stations.Count; i++ )
			{
				if ( string.Equals( Stations[i], stationId, StringComparison.OrdinalIgnoreCase ) )
					return true;
			}

			return false;
		}

		if ( RequiresStation )
			return string.Equals( RequiredStation, stationId, StringComparison.OrdinalIgnoreCase );

		return string.Equals( stationId, Workbench.StationId, StringComparison.OrdinalIgnoreCase );
	}


	/// <summary>How many output items fit in one inventory stack after crafting.</summary>
	public int ResolvedMaxStack => MaxStack > 0 ? MaxStack : 1;

	public bool IsUnlockedByDefault => string.IsNullOrWhiteSpace( UnlockId );
}
