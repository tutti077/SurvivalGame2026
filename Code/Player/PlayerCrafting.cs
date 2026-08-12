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
	bool _requestedHostCatalogs;

	protected override void OnStart()
	{
		base.OnStart();
		_inventory = Components.Get<PlayerInventory>();
		CraftingRecipeCatalog.EnsureLoaded();
		ResourceDefinitionCatalog.EnsureLoaded();
		BuildPieceCatalog.EnsureLoaded();
		TryRequestHostCatalogs();
	}

	protected override void OnUpdate()
	{
		// Owner may not be ready on the first OnStart frame after NetworkSpawn.
		if ( !_requestedHostCatalogs )
			TryRequestHostCatalogs();
	}

	void TryRequestHostCatalogs()
	{
		if ( _requestedHostCatalogs )
			return;

		if ( GameObject.Network is not { Active: true } net )
			return;

		if ( !net.IsOwner || Networking.IsHost )
		{
			// Host already has disk catalogs; mark done so we don't spam.
			if ( Networking.IsHost || !net.Active )
				_requestedHostCatalogs = true;
			return;
		}

		_requestedHostCatalogs = true;
		RpcHostRequestCatalogs();
	}

	[Rpc.Host]
	void RpcHostRequestCatalogs()
	{
		if ( !Networking.IsHost )
			return;

		if ( Rpc.Caller is { } caller
		     && GameObject.Network is { Active: true, Owner: { } owner }
		     && caller.Id != owner.Id )
		{
			Log.Warning( $"[PlayerCrafting] RpcHostRequestCatalogs ignored: caller ≠ owner." );
			return;
		}

		CraftingRecipeCatalog.EnsureLoaded();
		ResourceDefinitionCatalog.EnsureLoaded();
		BuildPieceCatalog.EnsureLoaded();
		AugmentCatalog.EnsureLoaded();

		var recipesJson = CraftingRecipeCatalog.ExportSourceJson();
		var resourcesJson = ResourceDefinitionCatalog.ExportSourceJson();
		var buildPiecesJson = BuildPieceCatalog.ExportSourceJson();
		var augmentsJson = AugmentCatalog.ExportSourceJson();

		if ( string.IsNullOrWhiteSpace( recipesJson ) && LogCrafting )
			Log.Warning( "[PlayerCrafting] Host has no recipe JSON to sync to client." );

		if ( string.IsNullOrWhiteSpace( buildPiecesJson ) && LogCrafting )
			Log.Warning( "[PlayerCrafting] Host has no build piece JSON to sync to client." );

		RpcOwnerReceiveCatalogs(
			recipesJson ?? string.Empty,
			resourcesJson ?? string.Empty,
			buildPiecesJson ?? string.Empty,
			augmentsJson ?? string.Empty );
	}

	[Rpc.Owner]
	void RpcOwnerReceiveCatalogs( string recipesJson, string resourcesJson, string buildPiecesJson, string augmentsJson )
	{
		var recipesOk = CraftingRecipeCatalog.ReplaceFromJson( recipesJson );
		var resourcesOk = ResourceDefinitionCatalog.ReplaceFromJson( resourcesJson );
		var buildOk = BuildPieceCatalog.ReplaceFromJson( buildPiecesJson );
		var augmentsOk = AugmentCatalog.ReplaceFromJson( augmentsJson );

		if ( LogCrafting || !recipesOk || !buildOk )
			Log.Info( $"[PlayerCrafting] Client catalog sync recipesOk={recipesOk} resourcesOk={resourcesOk} buildOk={buildOk} augmentsOk={augmentsOk} recipeCount={CraftingRecipeCatalog.All.Count} buildCount={BuildPieceCatalog.All.Count} augmentCount={AugmentCatalog.All.Count}" );
	}

	/// <summary>Request a craft from the local player. Returns true when applied on host, or when the RPC was sent.</summary>
	public bool OwnerTryCraft( string recipeId )
	{
		if ( _inventory is null )
			_inventory = Components.Get<PlayerInventory>();

		return _inventory is not null && _inventory.OwnerTryCraftRecipe( recipeId );
	}

	public bool HostTryCraft( string recipeId )
	{
		if ( _inventory is null )
			_inventory = Components.Get<PlayerInventory>();

		if ( _inventory is null || !_inventory.HasHostAuthority )
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
