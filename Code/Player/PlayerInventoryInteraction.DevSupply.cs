using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Dev supply (<see cref="GameHacks.DevSupply"/>): key X (or console <c>devSupplyOpen</c>) opens a
/// take-only grid beside the bag holding one full stack of every <c>resource_*</c> item and every
/// crafting-recipe ingredient. Takes refill at once (<see cref="ContainerInventory.InfiniteSupply"/>).
/// The grid is a client-local, never-networked <see cref="ContainerInventory"/> shown through the
/// normal container view, so every click / drag / shift-move works unchanged; what lands in the bag
/// goes through the usual bag RPCs.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Dev Supply" ), Title( "Open Action" )]
	public string DevSupplyAction { get; set; } = "DevSupply";

	[Property, Group( "Dev Supply" ), Title( "Columns" )]
	public int DevSupplyColumns { get; set; } = 10;

	GameObject _devSupplyObject;
	ContainerInventory _devSupply;

	bool IsDevSupply( ContainerInventory container ) =>
		container is not null && ReferenceEquals( container, _devSupply );

	void TickDevSupplyAccess()
	{
		if ( !GameHacks.DevSupply || !Input.Pressed( DevSupplyAction ) )
			return;

		ToggleDevSupply();
	}

	/// <summary>Opens the dev supply next to the bag, or closes the menu when it is already showing.</summary>
	public void ToggleDevSupply()
	{
		if ( !GameHacks.DevSupply )
			return;

		if ( IsDevSupply( OpenContainer ) )
		{
			_menu?.SetMenuOpen( false );
			return;
		}

		if ( IsBuildHammerPreviewing() || IsSeatedInVehicle )
			return;

		var supply = GetOrCreateDevSupply();
		if ( supply is null )
			return;

		// Restocked on every open, so catalog edits show up without a restart.
		supply.HostStockInfiniteSupply( CollectDevSupplyIds() );
		OpenContainerView( supply );
	}

	ContainerInventory GetOrCreateDevSupply()
	{
		if ( _devSupply is { IsValid: true } )
			return _devSupply;

		// Transient UI backing for this pawn's own feature: local only, never saved or networked.
		_devSupplyObject = new GameObject( true, "dev_supply" ) { NetworkMode = NetworkMode.Never };
		_devSupply = _devSupplyObject.Components.Create<ContainerInventory>();
		_devSupply.DisplayName = "Dev Supply";
		_devSupply.TakeOnly = true;
		_devSupply.InfiniteSupply = true;
		_devSupply.Columns = Math.Max( 1, DevSupplyColumns );
		return _devSupply;
	}

	/// <summary>Every <c>resource_*</c> in resources.json (catalog order), then every recipe ingredient not already listed.</summary>
	static List<string> CollectDevSupplyIds()
	{
		ResourceDefinitionCatalog.EnsureLoaded();
		CraftingRecipeCatalog.EnsureLoaded();

		var ids = new List<string>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var data in ResourceDefinitionCatalog.All )
		{
			var id = data?.Id;
			if ( !string.IsNullOrWhiteSpace( id ) && id.StartsWith( "resource_", StringComparison.OrdinalIgnoreCase ) && seen.Add( id ) )
				ids.Add( id );
		}

		foreach ( var recipe in CraftingRecipeCatalog.All )
		{
			if ( recipe?.Ingredients is null )
				continue;

			foreach ( var ingredient in recipe.Ingredients )
			{
				var id = ResourceCatalog.NormalizeResourceId( ingredient?.ResourceId ?? string.Empty );
				if ( !string.IsNullOrWhiteSpace( id ) && seen.Add( id ) )
					ids.Add( id );
			}
		}

		return ids;
	}

	void DestroyDevSupply()
	{
		if ( _devSupplyObject is { IsValid: true } )
			_devSupplyObject.Destroy();

		_devSupplyObject = null;
		_devSupply = null;
	}
}
