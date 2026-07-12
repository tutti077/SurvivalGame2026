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
		ReloadFromDisk();
	}

	/// <summary>Icon for crafting UI: recipe icon, then catalog path for <see cref="CraftingRecipe.Id"/>.</summary>
	public static string ResolveIconPath( CraftingRecipe recipe )
	{
		if ( recipe is null )
			return null;

		if ( !string.IsNullOrWhiteSpace( recipe.Icon ) && MenuUiTextures.MountedPathExists( recipe.Icon ) )
			return recipe.Icon;

		if ( !string.IsNullOrWhiteSpace( recipe.Id ) )
		{
			var outputIcon = ResourceCatalog.GetIconPath( recipe.Id );
			if ( MenuUiTextures.MountedPathExists( outputIcon ) )
				return outputIcon;
		}

		return recipe.Icon;
	}

	/// <summary>Recipe whose <see cref="CraftingRecipe.Id"/> matches (crafted-only items).</summary>
	public static bool TryGetRecipeByOutput( string outputResourceId, out CraftingRecipe recipe )
	{
		recipe = Get( outputResourceId );
		return recipe is not null;
	}

	public static string GetOutputIconPath( string outputResourceId )
	{
		if ( !TryGetRecipeByOutput( outputResourceId, out var recipe ) || string.IsNullOrWhiteSpace( recipe.Icon ) )
			return null;

		return recipe.Icon;
	}

	public static int GetOutputMaxStack( string outputResourceId )
	{
		return TryGetRecipeByOutput( outputResourceId, out var recipe ) ? recipe.ResolvedMaxStack : 0;
	}

	public static ResourceCatalog.ResourceDefinition ResolveOutputCatalogEntry( string outputResourceId )
	{
		if ( !TryGetRecipeByOutput( outputResourceId, out var recipe ) )
			return default;

		return new ResourceCatalog.ResourceDefinition(
			recipe.DisplayName,
			MenuUiTextures.TryLoad( recipe.Icon ),
			new Color( 0.72f, 0.74f, 0.78f ),
			recipe.ResolvedMaxStack );
	}

	public static CraftingRecipe Get( string recipeId )
	{
		EnsureLoaded();
		if ( string.IsNullOrWhiteSpace( recipeId ) )
			return null;

		recipeId = ResourceCatalog.NormalizeResourceId( recipeId );
		for ( var i = 0; i < Recipes.Count; i++ )
		{
			if ( string.Equals(
				    ResourceCatalog.NormalizeResourceId( Recipes[i].Id ),
				    recipeId,
				    StringComparison.OrdinalIgnoreCase ) )
				return Recipes[i];
		}

		return null;
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		ReloadFromDisk();
	}

	static void ReloadFromDisk()
	{
		var jsonHash = TryReadRecipeJsonHash();
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
		Id = "basic_sword",
		DisplayName = "Sword",
		Icon = "ui/items/basic_sword.png",
		Ingredients =
		{
			new CraftingIngredient { ResourceId = "resource_stone", Amount = 3 },
			new CraftingIngredient { ResourceId = "resource_woodBasic", Amount = 2 },
			new CraftingIngredient { ResourceId = "resource_plantFiber", Amount = 5 },
		},
		OutputAmount = 1,
		MaxStack = 1,
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
