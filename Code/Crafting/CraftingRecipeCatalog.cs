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
	static bool _isFallbackOnly;
	static string _sourceJson = string.Empty;
	static int _contentVersion;
	static float _lastFallbackRetryTime = -100f;

	public static IReadOnlyList<CraftingRecipe> All
	{
		get
		{
			EnsureLoaded();
			return Recipes;
		}
	}

	/// <summary>True when only the built-in sword recipe is present (JSON load failed).</summary>
	public static bool IsFallbackOnly
	{
		get
		{
			EnsureLoaded();
			return _isFallbackOnly;
		}
	}

	/// <summary>Bumps when the recipe list is replaced — UI rebuilds when this changes.</summary>
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

	/// <summary>Host-exported JSON for joining clients (empty if nothing loaded yet).</summary>
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

	/// <summary>Replace local catalog from host-provided JSON (joining clients).</summary>
	public static bool ReplaceFromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			return false;

		if ( !TryParseRecipes( json, out var parsed ) || parsed.Count == 0 )
			return false;

		Recipes.Clear();
		Recipes.AddRange( parsed );
		_sourceJson = json;
		_loadedJsonHash = StringComparer.Ordinal.GetHashCode( json );
		_isFallbackOnly = false;
		_loaded = true;
		_contentVersion++;
		Log.Info( $"[CraftingRecipeCatalog] Applied host recipe catalog ({Recipes.Count} recipes)." );
		return true;
	}

	/// <summary>Icon for crafting UI: recipe icon, then catalog path for <see cref="CraftingRecipe.Id"/>.</summary>
	public static string ResolveIconPath( CraftingRecipe recipe )
	{
		if ( recipe is null )
			return null;

		if ( !string.IsNullOrWhiteSpace( recipe.Icon ) )
			return recipe.Icon;

		if ( !string.IsNullOrWhiteSpace( recipe.Id ) )
		{
			var outputIcon = ResourceCatalog.GetIconPath( recipe.Id );
			if ( !string.IsNullOrWhiteSpace( outputIcon ) )
				return outputIcon;
		}

		return null;
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
		{
			// Joining clients often hit FileExists too early — retry while stuck on sword-only fallback.
			if ( _isFallbackOnly )
				TryReloadIfFallback();
			return;
		}

		ReloadFromDisk();
	}

	static void TryReloadIfFallback()
	{
		// Avoid hammering mounts every EnsureLoaded/TickMenu call.
		if ( Time.Now - _lastFallbackRetryTime < 1f )
			return;

		_lastFallbackRetryTime = Time.Now;
		if ( TryLoadFromFile() )
		{
			_isFallbackOnly = false;
			_contentVersion++;
			Log.Info( $"[CraftingRecipeCatalog] Recovered full recipe list ({Recipes.Count} recipes)." );
		}
	}

	static void ReloadFromDisk()
	{
		Recipes.Clear();
		_sourceJson = string.Empty;
		_isFallbackOnly = false;

		if ( TryLoadFromFile() )
		{
			_loaded = true;
			_contentVersion++;
			return;
		}

		Recipes.Add( CreateFallbackClub() );
		_isFallbackOnly = true;
		_loaded = true;
		_loadedJsonHash = 0;
		_contentVersion++;
		Log.Warning( "[CraftingRecipeCatalog] Using built-in fallback recipes (json missing or invalid)." );
	}

	static bool TryLoadFromFile()
	{
		foreach ( var path in GetPathCandidates() )
		{
			try
			{
				// Do NOT gate on FileExists — it returns false on joining clients while ReadAllText still works.
				var json = FileSystem.Mounted.ReadAllText( path );
				if ( string.IsNullOrWhiteSpace( json ) )
					continue;

				if ( !TryParseRecipes( json, out var parsed ) || parsed.Count == 0 )
					continue;

				Recipes.Clear();
				Recipes.AddRange( parsed );
				_sourceJson = json;
				_loadedJsonHash = StringComparer.Ordinal.GetHashCode( json );
				return true;
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[CraftingRecipeCatalog] Failed to load '{path}': {ex.Message}" );
			}
		}

		return false;
	}

	static bool TryParseRecipes( string json, out List<CraftingRecipe> parsed )
	{
		parsed = null;
		try
		{
			var file = JsonSerializer.Deserialize<CraftingRecipesFile>( json, JsonOptions );
			if ( file?.Recipes is null || file.Recipes.Count == 0 )
				return false;

			parsed = file.Recipes;
			return true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[CraftingRecipeCatalog] JSON parse failed: {ex.Message}" );
			return false;
		}
	}

	static IEnumerable<string> GetPathCandidates()
	{
		yield return RecipeFilePath;
		yield return "assets/data/crafting_recipes.json";
		yield return "/data/crafting_recipes.json";
	}

	static CraftingRecipe CreateFallbackClub() => new()
	{
		Id = "club_wood",
		DisplayName = "Wooden Club",
		Icon = "ui/items/club_wood.png",
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
