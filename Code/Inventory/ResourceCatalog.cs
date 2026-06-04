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
			["sword"] = "ui/items/item_sword.png",
			["rock"] = "ui/items/rock.jpg",
			["plant_fiber"] = "ui/items/plant_fiber.png",
			["wood"] = "ui/items/wood.png",
			["building_hammer"] = "ui/items/item_build_hammer.png",
		};

	public static void Register( ResourceItemDefinition definition )
	{
		if ( definition is null || !definition.IsValid() || string.IsNullOrWhiteSpace( definition.ResourceId ) )
			return;

		Definitions[definition.ResourceId] = definition;
		IconCache.Remove( definition.ResourceId );
	}

	public static void Unregister( ResourceItemDefinition definition )
	{
		if ( definition is null || string.IsNullOrWhiteSpace( definition.ResourceId ) )
			return;

		if ( Definitions.TryGetValue( definition.ResourceId, out var existing ) && existing == definition )
			Definitions.Remove( definition.ResourceId );

		IconCache.Remove( definition.ResourceId );
	}

	public static ResourceDefinition Resolve( string resourceId )
	{
		if ( !string.IsNullOrWhiteSpace( resourceId ) && Definitions.TryGetValue( resourceId, out var def ) && def.IsValid() )
			return def.ToCatalogEntry();

		var icon = GetCachedIcon( resourceId );
		return new ResourceDefinition(
			FormatDisplayName( resourceId ),
			icon,
			new Color( 0.45f, 0.48f, 0.52f ),
			64 );
	}

	public static int GetMaxStack( string resourceId )
	{
		if ( !string.IsNullOrWhiteSpace( resourceId ) && Definitions.TryGetValue( resourceId, out var def ) && def.IsValid() )
			return Math.Max( 1, def.MaxStack );

		return 64;
	}

	public static string GetIconPath( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return null;

		if ( Definitions.TryGetValue( resourceId, out var def ) && def.IsValid() && !string.IsNullOrWhiteSpace( def.Icon ) )
			return def.Icon;

		if ( KnownIconPaths.TryGetValue( resourceId, out var knownPath ) )
			return knownPath;

		if ( MountedIconExists( $"{resourceId}.png" ) )
			return $"ui/items/{resourceId}.png";

		if ( MountedIconExists( $"{resourceId}.jpg" ) )
			return $"ui/items/{resourceId}.jpg";

		return $"ui/items/{resourceId}.png";
	}

	static bool MountedIconExists( string fileName )
	{
		try
		{
			return FileSystem.Mounted.FileExists( $"ui/items/{fileName}" );
		}
		catch
		{
			return false;
		}
	}

	public static Texture GetCachedIcon( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return null;

		if ( IconCache.TryGetValue( resourceId, out var cached ) )
			return cached;

		if ( Definitions.TryGetValue( resourceId, out var def ) && def.IsValid() )
			cached = def.ResolveIcon();
		else
			cached = MenuUiTextures.TryLoadForResourceId( resourceId );

		IconCache[resourceId] = cached;
		return cached;
	}

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

		if ( countLabel is not null )
		{
			countLabel.Text = string.Empty;
			countLabel.Style.Set( "display", "none" );
		}
	}
}
