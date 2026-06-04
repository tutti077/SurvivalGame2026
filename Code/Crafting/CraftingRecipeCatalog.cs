using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads crafting recipes from <c>data/crafting_recipes.json</c> with a built-in fallback.</summary>
public static class CraftingRecipeCatalog
{
	const string RecipeFilePath = "data/crafting_recipes.json";

	static readonly List<CraftingRecipe> Recipes = new();
	static bool _loaded;
	static int _loadedJsonHash;

	public static IReadOnlyList<CraftingRecipe> All
	{
		get
		{
			EnsureLoaded();
			return Recipes;
		}
	}

	public static void ForceReload()
	{
		_loaded = false;
		_loadedJsonHash = 0;
		EnsureLoaded();
	}

	/// <summary>Icon for crafting UI: prefer the crafted item's catalog icon over JSON (avoids stale recipe icon paths).</summary>
	public static string ResolveIconPath( CraftingRecipe recipe )
	{
		if ( recipe is null )
			return null;

		if ( !string.IsNullOrWhiteSpace( recipe.OutputResourceId ) )
		{
			var outputIcon = ResourceCatalog.GetIconPath( recipe.OutputResourceId );
			if ( MenuUiTextures.MountedPathExists( outputIcon ) )
				return outputIcon;
		}

		if ( !string.IsNullOrWhiteSpace( recipe.Icon ) && MenuUiTextures.MountedPathExists( recipe.Icon ) )
			return recipe.Icon;

		return !string.IsNullOrWhiteSpace( recipe.OutputResourceId )
			? ResourceCatalog.GetIconPath( recipe.OutputResourceId )
			: recipe.Icon;
	}

	public static CraftingRecipe Get( string recipeId )
	{
		EnsureLoaded();
		if ( string.IsNullOrWhiteSpace( recipeId ) )
			return null;

		for ( var i = 0; i < Recipes.Count; i++ )
		{
			if ( string.Equals( Recipes[i].Id, recipeId, StringComparison.OrdinalIgnoreCase ) )
				return Recipes[i];
		}

		return null;
	}

	public static void EnsureLoaded()
	{
		var jsonHash = TryReadRecipeJsonHash();
		if ( _loaded && jsonHash == _loadedJsonHash )
			return;

		_loaded = true;
		_loadedJsonHash = jsonHash;
		Recipes.Clear();

		if ( TryLoadFromFile() )
			return;

		Recipes.Add( CreateFallbackSword() );
		Log.Warning( "[CraftingRecipeCatalog] Using built-in fallback recipes (json missing or invalid)." );
	}

	static int TryReadRecipeJsonHash()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( RecipeFilePath ) )
				return 0;

			return StringComparer.Ordinal.GetHashCode( FileSystem.Mounted.ReadAllText( RecipeFilePath ) );
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
			if ( !FileSystem.Mounted.FileExists( RecipeFilePath ) )
				return false;

			var json = FileSystem.Mounted.ReadAllText( RecipeFilePath );
			var file = JsonSerializer.Deserialize<CraftingRecipesFile>( json, JsonOptions );
			if ( file?.Recipes is null || file.Recipes.Count == 0 )
				return false;

			Recipes.AddRange( file.Recipes );
			return true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[CraftingRecipeCatalog] Failed to load {RecipeFilePath}: {ex.Message}" );
			return false;
		}
	}

	static CraftingRecipe CreateFallbackSword() => new()
	{
		Id = "sword",
		DisplayName = "Sword",
		Icon = "ui/items/item_sword.png",
		Ingredients =
		{
			new CraftingIngredient { ResourceId = "rock", Amount = 3 },
			new CraftingIngredient { ResourceId = "wood", Amount = 2 },
			new CraftingIngredient { ResourceId = "plant_fiber", Amount = 5 },
		},
		OutputResourceId = "sword",
		OutputAmount = 1,
		NumberOfItemsCrafted = 1,
		Stats =
		{
			new CraftingStatLine { Label = "Damage", Value = "12" },
			new CraftingStatLine { Label = "Speed", Value = "1.0" },
		}
	};

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class CraftingRecipesFile
	{
		public List<CraftingRecipe> Recipes { get; set; } = new();
	}
}
