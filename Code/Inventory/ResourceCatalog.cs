using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Runtime lookup for <see cref="ResourceItemDefinition"/> instances (registered while enabled in the scene).
/// </summary>
public static class ResourceCatalog
{
	public readonly record struct ResourceDefinition( string DisplayName, Texture Icon, Color FallbackColor );

	static readonly Dictionary<string, ResourceItemDefinition> Definitions =
		new( System.StringComparer.OrdinalIgnoreCase );

	static readonly Dictionary<string, Texture> IconCache =
		new( System.StringComparer.OrdinalIgnoreCase );

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

		return new ResourceDefinition(
			string.IsNullOrWhiteSpace( resourceId ) ? "Unknown" : resourceId,
			null,
			new Color( 0.45f, 0.48f, 0.52f ) );
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
			cached = null;

		IconCache[resourceId] = cached;
		return cached;
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
		var texture = GetCachedIcon( slot.ResourceId );
		if ( texture is not null )
		{
			iconPanel.Style.BackgroundImage = texture;
			iconPanel.Style.BackgroundColor = Color.Transparent;
		}
		else
		{
			iconPanel.Style.BackgroundImage = null;
			iconPanel.Style.BackgroundColor = def.FallbackColor.WithAlpha( 0.95f );
		}

		iconPanel.Style.Set( "display", "flex" );
		if ( countLabel is not null )
		{
			countLabel.Text = count.ToString();
			countLabel.Style.Set( "display", "flex" );
		}
	}
}
