using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads edible / raw food metadata from <c>data/food_items.json</c>.</summary>
public static class FoodCatalog
{
	const string FoodFilePath = "data/food_items.json";

	static readonly List<FoodItemData> Foods = new();
	static readonly Dictionary<string, FoodItemData> ByResourceId =
		new( StringComparer.OrdinalIgnoreCase );

	static bool _loaded;

	public static IReadOnlyList<FoodItemData> All
	{
		get
		{
			EnsureLoaded();
			return Foods;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		Foods.Clear();
		ByResourceId.Clear();

		if ( !TryLoadFromFile() )
			Log.Warning( "[FoodCatalog] No food_items.json — food buffs unavailable." );
	}

	public static bool TryGet( string resourceId, out FoodItemData food )
	{
		EnsureLoaded();
		food = null;
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		return ByResourceId.TryGetValue( resourceId, out food );
	}

	public static bool IsEdible( string resourceId ) =>
		TryGet( resourceId, out var food ) && food.Edible;

	public static Color ResolveFallbackColor( FoodItemData food )
	{
		if ( food is null || string.IsNullOrWhiteSpace( food.FallbackColor ) )
			return new Color( 0.7f, 0.7f, 0.7f );

		var parts = food.FallbackColor.Split( ',' );
		if ( parts.Length < 3 )
			return new Color( 0.7f, 0.7f, 0.7f );

		if ( !float.TryParse( parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r )
		     || !float.TryParse( parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var g )
		     || !float.TryParse( parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b ) )
			return new Color( 0.7f, 0.7f, 0.7f );

		var a = 1f;
		if ( parts.Length >= 4 )
			float.TryParse( parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a );

		return new Color( r, g, b, a );
	}

	static bool TryLoadFromFile()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( FoodFilePath ) )
				return false;

			var json = FileSystem.Mounted.ReadAllText( FoodFilePath );
			var file = JsonSerializer.Deserialize<FoodItemsFile>( json, JsonOptions );
			if ( file?.Foods is null || file.Foods.Count == 0 )
				return false;

			for ( var i = 0; i < file.Foods.Count; i++ )
			{
				var entry = file.Foods[i];
				if ( entry is null || string.IsNullOrWhiteSpace( entry.ResourceId ) )
					continue;

				entry.ResourceId = ResourceCatalog.NormalizeResourceId( entry.ResourceId );
				Foods.Add( entry );
				ByResourceId[entry.ResourceId] = entry;
			}

			return Foods.Count > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[FoodCatalog] Failed to load {FoodFilePath}: {ex.Message}" );
			return false;
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class FoodItemsFile
	{
		public List<FoodItemData> Foods { get; set; } = new();
	}
}
