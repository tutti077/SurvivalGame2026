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
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	[JsonPropertyName( "icon" )]
	public string Icon { get; set; } = string.Empty;

	public string UnlockId { get; set; } = string.Empty;
	public List<CraftingIngredient> Ingredients { get; set; } = new();
	public string OutputResourceId { get; set; } = string.Empty;
	public int OutputAmount { get; set; } = 1;

	/// <summary>How many finished items one craft action produces (e.g. 5 bandages). Ingredients scale with this.</summary>
	[JsonPropertyName( "numberOfItemsCrafted" )]
	public int NumberOfItemsCrafted { get; set; } = 1;

	public List<CraftingStatLine> Stats { get; set; } = new();

	/// <summary>Inventory stack size for the crafted output. Defaults to 1 when unset.</summary>
	[JsonPropertyName( "maxStack" )]
	public int MaxStack { get; set; }

	public int CraftBatchCount => Math.Max( 1, NumberOfItemsCrafted );

	public int TotalOutputAmount => Math.Max( 1, OutputAmount ) * CraftBatchCount;

	/// <summary>How many output items fit in one inventory stack after crafting.</summary>
	public int ResolvedMaxStack => MaxStack > 0 ? MaxStack : 1;

	public bool IsUnlockedByDefault => string.IsNullOrWhiteSpace( UnlockId );
}
