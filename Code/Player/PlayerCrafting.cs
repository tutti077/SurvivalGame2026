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

		if ( recipe.RequiresStation
		     && !Campfire.IsPlayerNearLitOrFueledStation( GameObject, recipe.RequiredStation ) )
		{
			if ( LogCrafting )
				Log.Info( $"[PlayerCrafting] {GameObject.Name}: need station '{recipe.RequiredStation}' for '{recipeId}'." );
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

		// Equipment remembers its maker (shown in the item tooltip); bulk resources stay untagged.
		var crafterName = EquipmentCatalog.TryGet( recipe.Id, out _ ) ? ResolveCrafterName() : null;

		if ( !_inventory.HostTryAddResource( recipe.Id, outputTotal, wear: 0, crafterName: crafterName ) )
		{
			if ( LogCrafting )
				Log.Warning( $"[PlayerCrafting] {GameObject.Name}: crafted '{recipeId}' but inventory could not fit output." );
			return false;
		}

		if ( LogCrafting )
			Log.Info( $"[PlayerCrafting] {GameObject.Name}: crafted {outputTotal} {recipe.Id}." );

		Components.Get<PlayerQuests>()?.HostReport( QuestEventIds.ItemCrafted, recipe.Id, outputTotal );

		return true;
	}

	/// <summary>Display name of the player who owns this pawn (host resolves at craft time).</summary>
	string ResolveCrafterName()
	{
		if ( GameObject.Network is { Active: true, Owner: { } owner } )
			return owner.DisplayName ?? string.Empty;

		return Connection.Local?.DisplayName ?? string.Empty;
	}

	/// <summary>Local UI check: any tool in hotbar or bag with wear (drives the workbench repair button).</summary>
	public bool HasDamagedTool()
	{
		var hotbar = Components.Get<PlayerHotbar>();
		if ( hotbar is not null )
		{
			for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
			{
				if ( ToolDurability.IsDamaged( hotbar.GetSlot( i ) ) )
					return true;
			}
		}

		if ( _inventory is null )
			_inventory = Components.Get<PlayerInventory>();

		if ( _inventory is not null )
		{
			for ( var i = 0; i < _inventory.SlotCount; i++ )
			{
				if ( ToolDurability.IsDamaged( _inventory.GetSlot( i ) ) )
					return true;
			}
		}

		return false;
	}

	/// <summary>Workbench repair click: restore the most-damaged tool (hotbar + bag) to full, for free.</summary>
	public bool OwnerTryRepairDamagedTool()
	{
		if ( _inventory is null )
			_inventory = Components.Get<PlayerInventory>();

		if ( _inventory is null || !_inventory.IsLocalManagingClient() )
			return false;

		if ( _inventory.HasHostAuthority )
			return HostTryRepairMostDamagedTool();

		RpcHostRepairDamagedTool();
		return true;
	}

	[Rpc.Host]
	void RpcHostRepairDamagedTool()
	{
		if ( !Networking.IsHost )
			return;

		if ( Rpc.Caller is { } caller
		     && GameObject.Network is { Active: true, Owner: { } owner }
		     && caller.Id != owner.Id )
			return;

		HostTryRepairMostDamagedTool();
	}

	public bool HostTryRepairMostDamagedTool()
	{
		if ( _inventory is null )
			_inventory = Components.Get<PlayerInventory>();

		if ( _inventory is null || !_inventory.HasHostAuthority )
			return false;

		var hotbar = Components.Get<PlayerHotbar>();
		var bestWear = 0;
		var bestHotbarIndex = -1;
		var bestBagIndex = -1;

		if ( hotbar is not null )
		{
			for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
			{
				var slot = hotbar.GetSlot( i );
				if ( ToolDurability.IsDamaged( slot ) && slot.Wear > bestWear )
				{
					bestWear = slot.Wear;
					bestHotbarIndex = i;
				}
			}
		}

		for ( var i = 0; i < _inventory.SlotCount; i++ )
		{
			var slot = _inventory.GetSlot( i );
			if ( ToolDurability.IsDamaged( slot ) && slot.Wear > bestWear )
			{
				bestWear = slot.Wear;
				bestHotbarIndex = -1;
				bestBagIndex = i;
			}
		}

		if ( bestHotbarIndex >= 0 )
			return hotbar.HostClearWear( bestHotbarIndex );

		if ( bestBagIndex >= 0 )
			return _inventory.HostClearWear( bestBagIndex );

		return false;
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
