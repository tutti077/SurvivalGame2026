using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads augment definitions from <c>data/augment_definitions.json</c>.</summary>
public static class AugmentCatalog
{
	const string FilePath = "data/augment_definitions.json";

	static readonly List<AugmentDefinition> Definitions = new();
	static readonly Dictionary<string, AugmentDefinition> ById =
		new( StringComparer.OrdinalIgnoreCase );

	static bool _loaded;
	static string _sourceJson = string.Empty;
	static int _contentVersion;

	public static IReadOnlyList<AugmentDefinition> All
	{
		get
		{
			EnsureLoaded();
			return Definitions;
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
		ReloadFromDisk();
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

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

		if ( !TryParse( json, out var parsed ) || parsed.Count == 0 )
			return false;

		Definitions.Clear();
		Definitions.AddRange( parsed );
		RebuildLookup();
		_sourceJson = json;
		_loaded = true;
		_contentVersion++;
		Log.Info( $"[AugmentCatalog] Applied host catalog ({Definitions.Count} augments)." );
		return true;
	}

	public static bool TryGet( string id, out AugmentDefinition definition )
	{
		EnsureLoaded();
		definition = null;
		if ( string.IsNullOrWhiteSpace( id ) )
			return false;

		return ById.TryGetValue( ResourceCatalog.NormalizeResourceId( id ), out definition );
	}

	public static AugmentDefinition Get( string id ) =>
		TryGet( id, out var def ) ? def : null;

	public static bool IsAugment( string resourceId ) => TryGet( resourceId, out _ );

	public static bool IsSlotAllowed( AugmentDefinition definition, AugmentSlot slot ) =>
		definition is not null && definition.TryGetSlot( out var required ) && required == slot;

	public static string GetIconPath( string id ) =>
		TryGet( id, out var def ) && !string.IsNullOrWhiteSpace( def.Icon ) ? def.Icon : null;

	public static int GetMaxStack( string id ) =>
		TryGet( id, out var def ) ? def.ResolvedMaxStack : 0;

	public static ResourceCatalog.ResourceDefinition ResolveCatalogEntry( string id )
	{
		if ( !TryGet( id, out var def ) )
			return default;

		return new ResourceCatalog.ResourceDefinition(
			def.DisplayName,
			MenuUiTextures.TryLoad( def.Icon ),
			new Color( 0.55f, 0.72f, 0.88f ),
			def.ResolvedMaxStack );
	}

	static void ReloadFromDisk()
	{
		Definitions.Clear();
		ById.Clear();
		_sourceJson = string.Empty;

		if ( !TryLoadFromFile() )
		{
			Definitions.AddRange( CreateFallback() );
			Log.Warning( "[AugmentCatalog] Using built-in fallback augment definitions." );
		}

		RebuildLookup();
		_loaded = true;
		_contentVersion++;
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

				if ( !TryParse( json, out var parsed ) || parsed.Count == 0 )
					continue;

				Definitions.Clear();
				Definitions.AddRange( parsed );
				_sourceJson = json;
				return true;
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[AugmentCatalog] Failed to load '{path}': {ex.Message}" );
			}
		}

		return false;
	}

	static bool TryParse( string json, out List<AugmentDefinition> parsed )
	{
		parsed = null;
		try
		{
			var file = JsonSerializer.Deserialize<AugmentDefinitionFile>( json, JsonOptions );
			if ( file?.Augments is null || file.Augments.Count == 0 )
				return false;

			parsed = file.Augments;
			return true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[AugmentCatalog] JSON parse failed: {ex.Message}" );
			return false;
		}
	}

	static void RebuildLookup()
	{
		ById.Clear();
		for ( var i = 0; i < Definitions.Count; i++ )
		{
			var def = Definitions[i];
			if ( def is null || string.IsNullOrWhiteSpace( def.Id ) )
				continue;

			ById[ResourceCatalog.NormalizeResourceId( def.Id )] = def;
		}
	}

	static IEnumerable<string> GetPathCandidates()
	{
		yield return FilePath;
		yield return "assets/data/augment_definitions.json";
		yield return "/data/augment_definitions.json";
	}

	static List<AugmentDefinition> CreateFallback() => new()
	{
		new()
		{
			Id = "augment_jump_legs",
			DisplayName = "Jump Legs",
			Icon = "ui/items/resource_woodBasic.png",
			Description = "Passive: every grounded jump launches at 3× normal height.",
			Slot = "LegQuads",
			Ability = "JumpHeight",
			JumpHeightMultiplier = 3f,
			MaxStack = 1,
			Ingredients = { new CraftingIngredient { ResourceId = "resource_woodBasic", Amount = 1 } },
		},
		new()
		{
			Id = "augment_lateral_dash_legs",
			DisplayName = "Lateral Dash Legs",
			Icon = "ui/items/resource_woodBasic.png",
			Description = "Grounded A/D + Jump dashes sideways instead of jumping.",
			Slot = "LegQuads",
			Ability = "LateralDash",
			DashMeters = 3f,
			MaxStack = 1,
			Ingredients = { new CraftingIngredient { ResourceId = "resource_woodBasic", Amount = 1 } },
		},
		new()
		{
			Id = "augment_double_jump_legs",
			DisplayName = "Double Jump Legs",
			Icon = "ui/items/resource_woodBasic.png",
			Description = "One mid-air jump at normal height; resets on landing.",
			Slot = "LegQuads",
			Ability = "DoubleJump",
			MaxStack = 1,
			Ingredients = { new CraftingIngredient { ResourceId = "resource_woodBasic", Amount = 1 } },
		},
	};

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};
}
