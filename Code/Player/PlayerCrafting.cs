using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Host-authoritative crafting requests from the crafting menu.</summary>
[Title( "Player Crafting" )]
public sealed class PlayerCrafting : Component
{
	[Property, Group( "Debug" )] public bool LogCrafting { get; set; }

	PlayerInventory _inventory;

	protected override void OnStart()
	{
		base.OnStart();
		_inventory = Components.Get<PlayerInventory>();
		CraftingRecipeCatalog.EnsureLoaded();
	}

	/// <summary>Request a craft from the local player. Returns true only when applied immediately (host/offline).</summary>
	public bool OwnerTryCraft( string recipeId )
	{
		if ( _inventory is null )
			_inventory = Components.Get<PlayerInventory>();

		return _inventory is not null && _inventory.OwnerTryCraftRecipe( recipeId );
	}

	public bool HostTryCraft( string recipeId )
	{
		if ( !_inventory.HasHostAuthority || _inventory is null )
			return false;

		var recipe = CraftingRecipeCatalog.Get( recipeId );
		if ( recipe is null )
		{
			if ( LogCrafting )
				Log.Warning( $"[PlayerCrafting] Unknown recipe '{recipeId}'." );
			return false;
		}

		if ( !recipe.IsUnlockedByDefault )
		{
			if ( LogCrafting )
				Log.Warning( $"[PlayerCrafting] Recipe '{recipeId}' is locked." );
			return false;
		}

		var scaledIngredients = BuildIngredients( recipe );
		var outputTotal = recipe.TotalOutputAmount;

		if ( !_inventory.HasResources( scaledIngredients ) )
		{
			if ( LogCrafting )
				Log.Info( $"[PlayerCrafting] {GameObject.Name}: missing materials for '{recipeId}'." );
			return false;
		}

		if ( !_inventory.HostCanFitResource( recipe.Id, outputTotal ) )
		{
			if ( LogCrafting )
				Log.Info( $"[PlayerCrafting] {GameObject.Name}: no room for craft output '{recipe.Id}' x{outputTotal}." );
			return false;
		}

		if ( !_inventory.HostTryConsumeResources( scaledIngredients ) )
			return false;

		if ( !_inventory.HostTryAddResource( recipe.Id, outputTotal ) )
		{
			if ( LogCrafting )
				Log.Warning( $"[PlayerCrafting] {GameObject.Name}: crafted '{recipeId}' but inventory could not fit output." );
			return false;
		}

		if ( LogCrafting )
			Log.Info( $"[PlayerCrafting] {GameObject.Name}: crafted {outputTotal} {recipe.Id}." );

		return true;
	}

	static List<CraftingIngredient> BuildIngredients( CraftingRecipe recipe )
	{
		var list = new List<CraftingIngredient>();
		if ( recipe.Ingredients is null )
			return list;

		for ( var i = 0; i < recipe.Ingredients.Count; i++ )
		{
			var ing = recipe.Ingredients[i];
			if ( ing is null )
				continue;

			list.Add( new CraftingIngredient
			{
				ResourceId = ing.ResourceId,
				Amount = Math.Max( 1, ing.Amount ),
			} );
		}

		return list;
	}

}
