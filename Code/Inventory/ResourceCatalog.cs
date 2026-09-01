using System.Collections.Generic;
using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Runtime lookup for <see cref="ResourceItemDefinition"/> instances (registered while enabled in the scene).
/// </summary>
public static class ResourceCatalog
{
	public readonly record struct ResourceDefinition( string DisplayName, Texture Icon, Color FallbackColor, int MaxStack );

	static readonly Dictionary<string, ResourceItemDefinition> Definitions =
		new( System.StringComparer.OrdinalIgnoreCase );

	static readonly Dictionary<string, Texture> IconCache =
		new( System.StringComparer.OrdinalIgnoreCase );

	static readonly Dictionary<string, string> KnownIconPaths =
		new( System.StringComparer.OrdinalIgnoreCase )
		{
			["basic_hook"] = "ui/items/basic_hook.png",
			["basic_sword"] = "ui/items/basic_sword.png",
			["build_hammer"] = "ui/items/build_hammer.png",
			["light_torch"] = "ui/items/light_torch.png",
			["light_lantern"] = "ui/items/light_lantern.png",
			["axe_stone"] = "ui/items/stone_axe.png",
			["basic_wingsuit"] = "ui/items/basic_wingsuit.png",
			["basic_bow"] = "ui/items/basic_bow.png",
			["arrow_wood"] = "ui/items/arrow_wood.png",
			["fishing_rod"] = "ui/items/tool_fishingRod.png",
		};

	/// <summary>Maps old/sample resource ids to current catalog ids.</summary>
	static readonly Dictionary<string, string> LegacyResourceAliases =
		new( System.StringComparer.OrdinalIgnoreCase )
		{
			["rock"] = "resource_stone",
			["stone"] = "resource_stone",
			["sample_rock"] = "resource_stone",
			["plant_fiber"] = "resource_plantFiber",
			["sample_bush"] = "resource_plantFiber",
			["wood"] = "resource_woodBasic",
			["resource_wood"] = "resource_woodBasic",
			["sample_stick"] = "resource_woodBasic",
			["sand"] = "resource_sand",
			["clay"] = "resource_clay",
			["sap"] = "resource_resin",
			["flint"] = "resource_flint",
			["femur"] = "resource_femur",
			["hide"] = "resource_hide",
			["animal_fat"] = "resource_animalFat",
			["resource_animal_fat"] = "resource_animalFat",
			["leather"] = "resource_leather",
			["feathers"] = "resource_feathers",
			["vines"] = "resource_vines",
			["sword"] = "basic_sword",
			["sample_sword"] = "basic_sword",
			["item_sword"] = "basic_sword",
			["building_hammer"] = "build_hammer",
			["item_build_hammer"] = "build_hammer",
			["item_building_hammer"] = "build_hammer",
			["buildhammer"] = "build_hammer",
			["item_hook"] = "basic_hook",
			["hook"] = "basic_hook",
			["torch"] = "light_torch",
			["item_torch"] = "light_torch",
		};

	/// <summary>Returns the canonical resource id used by crafting and the item library.</summary>
	public static string NormalizeResourceId( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return resourceId;

		return LegacyResourceAliases.TryGetValue( resourceId, out var canonical )
			? canonical
			: resourceId;
	}

	public static bool ResourceIdsMatch( string a, string b )
	{
		if ( string.IsNullOrWhiteSpace( a ) || string.IsNullOrWhiteSpace( b ) )
			return false;

		return string.Equals( NormalizeResourceId( a ), NormalizeResourceId( b ), StringComparison.OrdinalIgnoreCase );
	}

	public static void Register( ResourceItemDefinition definition )
	{
		if ( definition is null || !definition.IsValid() || string.IsNullOrWhiteSpace( definition.ResourceId ) )
			return;

		var id = NormalizeResourceId( definition.ResourceId );
		Definitions[id] = definition;
		IconCache.Remove( id );
	}

	public static void Unregister( ResourceItemDefinition definition )
	{
		if ( definition is null || string.IsNullOrWhiteSpace( definition.ResourceId ) )
			return;

		var id = NormalizeResourceId( definition.ResourceId );
		if ( Definitions.TryGetValue( id, out var existing ) && existing == definition )
			Definitions.Remove( id );

		IconCache.Remove( id );
	}

	public static ResourceDefinition Resolve( string resourceId )
	{
		resourceId = NormalizeResourceId( resourceId );

		if ( !string.IsNullOrWhiteSpace( resourceId ) && Definitions.TryGetValue( resourceId, out var def ) && def.IsValid() )
			return def.ToCatalogEntry();

		if ( ResourceDefinitionCatalog.TryGet( resourceId, out _ ) )
			return ResourceDefinitionCatalog.ResolveCatalogEntry( resourceId );

		if ( AugmentCatalog.TryGet( resourceId, out _ ) )
			return AugmentCatalog.ResolveCatalogEntry( resourceId );

		if ( CraftingRecipeCatalog.TryGetRecipeByOutput( resourceId, out _ ) )
			return CraftingRecipeCatalog.ResolveOutputCatalogEntry( resourceId );

		var icon = GetCachedIcon( resourceId );
		return new ResourceDefinition(
			FormatDisplayName( resourceId ),
			icon,
			new Color( 0.45f, 0.48f, 0.52f ),
			64 );
	}

	public static int GetMaxStack( string resourceId )
	{
		resourceId = NormalizeResourceId( resourceId );

		if ( !string.IsNullOrWhiteSpace( resourceId ) && Definitions.TryGetValue( resourceId, out var def ) && def.IsValid() )
			return Math.Max( 1, def.MaxStack );

		if ( ResourceDefinitionCatalog.TryGet( resourceId, out _ ) )
			return ResourceDefinitionCatalog.GetMaxStack( resourceId );

		var augmentMax = AugmentCatalog.GetMaxStack( resourceId );
		if ( augmentMax > 0 )
			return augmentMax;

		var craftedMax = CraftingRecipeCatalog.GetOutputMaxStack( resourceId );
		if ( craftedMax > 0 )
			return craftedMax;

		return 64;
	}

	/// <summary>How many of <paramref name="desiredAdd"/> can merge into a stack that already has <paramref name="currentCount"/>.</summary>
	public static int ClampAddToStack( string resourceId, int currentCount, int desiredAdd )
	{
		if ( desiredAdd <= 0 )
			return 0;

		var room = GetMaxStack( resourceId ) - Math.Max( 0, currentCount );
		return room <= 0 ? 0 : Math.Min( desiredAdd, room );
	}

	public static string GetIconPath( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return null;

		resourceId = NormalizeResourceId( resourceId );

		// Prefer catalog paths without FileExists — existence checks are flaky on joining clients.
		if ( Definitions.TryGetValue( resourceId, out var def ) && def.IsValid() && !string.IsNullOrWhiteSpace( def.Icon ) )
			return def.Icon;

		var jsonIcon = ResourceDefinitionCatalog.GetIconPath( resourceId );
		if ( !string.IsNullOrWhiteSpace( jsonIcon ) )
			return jsonIcon;

		var augmentIcon = AugmentCatalog.GetIconPath( resourceId );
		if ( !string.IsNullOrWhiteSpace( augmentIcon ) )
			return augmentIcon;

		var craftedIcon = CraftingRecipeCatalog.GetOutputIconPath( resourceId );
		if ( !string.IsNullOrWhiteSpace( craftedIcon ) )
			return craftedIcon;

		if ( KnownIconPaths.TryGetValue( resourceId, out var knownPath ) )
			return knownPath;

		if ( LegacyIconPaths.TryGetValue( resourceId, out var legacyPath ) )
			return legacyPath;

		return $"ui/items/{resourceId}.png";
	}

	/// <summary>Pre-rename filenames still present on disk — used when JSON/catalog paths miss on clients.</summary>
	static readonly Dictionary<string, string> LegacyIconPaths =
		new( StringComparer.OrdinalIgnoreCase )
		{
			["resource_stone"] = "ui/items/rock.jpg",
			["resource_plantFiber"] = "ui/items/plant_fiber.png",
			["resource_woodBasic"] = "ui/items/wood.png",
			["resource_sand"] = "ui/items/sand.png",
			["resource_clay"] = "ui/items/clay.png",
			["resource_resin"] = "ui/items/sap.png",
			["resource_flint"] = "ui/items/flint.png",
			["resource_femur"] = "ui/items/femur.png",
			["resource_hide"] = "ui/items/hide.png",
			["resource_animalFat"] = "ui/items/animal_fat.png",
			["resource_leather"] = "ui/items/leather.png",
			["resource_feathers"] = "ui/items/feathers.png",
			["resource_vines"] = "ui/items/vines.png",
			["basic_sword"] = "ui/items/item_sword.png",
			["build_hammer"] = "ui/items/item_build_hammer.png",
			["basic_hook"] = "ui/items/item_hook.png",
		};

	public static Texture GetCachedIcon( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return null;

		resourceId = NormalizeResourceId( resourceId );

		if ( IconCache.TryGetValue( resourceId, out var cached ) )
			return cached;

		if ( Definitions.TryGetValue( resourceId, out var def ) && def.IsValid() )
			cached = def.ResolveIcon();
		else
			cached = MenuUiTextures.TryLoadForResourceId( resourceId );

		// Don't cache misses until host UI sync finishes — joiners resolve icons late.
		if ( cached is not null || SyncedUiContent.IsReady )
			IconCache[resourceId] = cached;

		return cached;
	}

	public static void ClearIconCache() => IconCache.Clear();

	static string FormatDisplayName( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return "Unknown";

		var parts = resourceId.Split( '_', StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length == 0 )
			return resourceId;

		for ( var i = 0; i < parts.Length; i++ )
		{
			var part = parts[i];
			if ( part.Length == 0 )
				continue;
			parts[i] = char.ToUpperInvariant( part[0] ) + ( part.Length > 1 ? part[1..] : string.Empty );
		}

		return string.Join( ' ', parts );
	}

	public static void ApplyStackVisual( Panel iconPanel, Label countLabel, in InventorySlot slot )
	{
		if ( iconPanel is null )
			return;

		var count = slot.IsEmpty ? 0 : slot.Count;

		if ( slot.IsEmpty )
		{
			iconPanel.Style.Set( "display", "none" );
			iconPanel.Style.BackgroundImage = null;
			iconPanel.Style.BackgroundColor = Color.Transparent;
			if ( countLabel is not null )
			{
				countLabel.Text = string.Empty;
				countLabel.Style.Set( "display", "none" );
			}

			return;
		}

		var def = Resolve( slot.ResourceId );
		var iconPath = GetIconPath( slot.ResourceId );
		if ( !MenuUiTextures.ApplyBackground( iconPanel, iconPath ) )
		{
			iconPanel.Style.BackgroundImage = null;
			iconPanel.Style.Set( "background-image", "none" );
			iconPanel.Style.BackgroundColor = def.FallbackColor.WithAlpha( 0.95f );
			iconPanel.Style.Set( "display", "flex" );
		}
		if ( countLabel is not null )
		{
			if ( def.MaxStack <= 1 )
			{
				countLabel.Text = string.Empty;
				countLabel.Style.Set( "display", "none" );
			}
			else
			{
				countLabel.Text = count.ToString();
				countLabel.Style.Set( "display", "flex" );
			}
		}

		// Reset any ghost fade; broken state reads from the grey bar + red X, not opacity.
		iconPanel.Style.Set( "opacity", "1" );
		ApplyDurabilityBar( iconPanel, slot );
		ApplyBrokenOverlay( iconPanel, ToolDurability.IsBroken( slot ) );
	}

	static readonly Color DurabilityBarColor = new( 0.87f, 0.76f, 0.38f );
	static readonly Color DurabilityBrokenBarColor = new( 0.5f, 0.5f, 0.5f );

	/// <summary>
	/// Health-bar style durability readout along the icon's bottom edge: every durability item
	/// shows it — full tan/yellow when untouched, shrinking with wear, entirely grey when broken.
	/// </summary>
	static void ApplyDurabilityBar( Panel iconPanel, in InventorySlot slot )
	{
		var track = FindChildWithClass( iconPanel, "durability-track" );

		if ( slot.IsEmpty || !ToolDurability.HasDurability( slot.ResourceId ) )
		{
			track?.Style.Set( "display", "none" );
			return;
		}

		Panel fill = null;
		if ( track is null )
		{
			track = new Panel { Parent = iconPanel };
			track.AddClass( "durability-track" );
			track.Style.Set( "position", "absolute" );
			track.Style.Set( "left", "6%" );
			track.Style.Set( "right", "6%" );
			track.Style.Set( "bottom", "5%" );
			track.Style.Set( "height", "9%" );
			track.Style.Set( "border-radius", "2px" );
			track.Style.BackgroundColor = new Color( 0f, 0f, 0f, 0.65f );
			track.Style.Set( "pointer-events", "none" );

			fill = new Panel { Parent = track };
			fill.AddClass( "durability-fill" );
			fill.Style.Set( "position", "absolute" );
			fill.Style.Set( "left", "0" );
			fill.Style.Set( "top", "0" );
			fill.Style.Set( "bottom", "0" );
			fill.Style.Set( "border-radius", "2px" );
		}
		else
		{
			fill = FindChildWithClass( track, "durability-fill" );
		}

		track.Style.Set( "display", "flex" );
		if ( fill is null )
			return;

		var broken = ToolDurability.IsBroken( slot );
		var frac = ToolDurability.GetRemainingFraction( slot );
		fill.Style.Width = Length.Percent( broken ? 100f : Math.Max( frac * 100f, 3f ) );
		fill.Style.BackgroundColor = broken ? DurabilityBrokenBarColor : DurabilityBarColor;
	}

	/// <summary>Semi-transparent red X over the icon while the item is broken.</summary>
	static void ApplyBrokenOverlay( Panel iconPanel, bool broken )
	{
		var overlay = FindChildWithClass( iconPanel, "broken-x" );

		if ( !broken )
		{
			overlay?.Style.Set( "display", "none" );
			return;
		}

		if ( overlay is null )
		{
			overlay = new Panel { Parent = iconPanel };
			overlay.AddClass( "broken-x" );
			overlay.Style.Set( "position", "absolute" );
			overlay.Style.Set( "left", "0" );
			overlay.Style.Set( "top", "0" );
			overlay.Style.Set( "right", "0" );
			overlay.Style.Set( "bottom", "0" );
			overlay.Style.Set( "pointer-events", "none" );

			for ( var i = 0; i < 2; i++ )
			{
				var stroke = new Panel { Parent = overlay };
				stroke.Style.Set( "position", "absolute" );
				stroke.Style.Set( "left", "10%" );
				stroke.Style.Set( "right", "10%" );
				stroke.Style.Set( "top", "44%" );
				stroke.Style.Set( "height", "12%" );
				stroke.Style.Set( "border-radius", "3px" );
				stroke.Style.BackgroundColor = new Color( 0.85f, 0.12f, 0.1f, 0.55f );
				stroke.Style.Set( "transform", i == 0 ? "rotate(45deg)" : "rotate(-45deg)" );
			}
		}

		overlay.Style.Set( "display", "flex" );
	}

	static Panel FindChildWithClass( Panel parent, string className )
	{
		if ( parent is null )
			return null;

		foreach ( var child in parent.Children )
		{
			if ( child is not null && child.HasClass( className ) )
				return child;
		}

		return null;
	}

	/// <summary>Faded item icon for an empty hotbar slot that still has a remembered binding.</summary>
	public static void ApplyBindingGhostVisual( Panel iconPanel, Label countLabel, string resourceId )
	{
		if ( iconPanel is null )
			return;

		if ( string.IsNullOrWhiteSpace( resourceId ) )
		{
			ApplyStackVisual( iconPanel, countLabel, InventorySlot.Empty );
			return;
		}

		var def = Resolve( resourceId );
		var iconPath = GetIconPath( resourceId );
		iconPanel.Style.Set( "opacity", "0.42" );
		if ( !MenuUiTextures.ApplyBackground( iconPanel, iconPath ) )
		{
			iconPanel.Style.BackgroundImage = null;
			iconPanel.Style.Set( "background-image", "none" );
			iconPanel.Style.BackgroundColor = def.FallbackColor.WithAlpha( 0.38f );
		}

		iconPanel.Style.Set( "display", "flex" );
		ApplyDurabilityBar( iconPanel, InventorySlot.Empty );
		ApplyBrokenOverlay( iconPanel, broken: false );

		if ( countLabel is not null )
		{
			countLabel.Text = string.Empty;
			countLabel.Style.Set( "display", "none" );
		}
	}
}
