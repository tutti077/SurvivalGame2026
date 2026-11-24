using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Death loot drop: when a pawn dies on the host, its droppable stacks move into the player's corpse bag - a burlap
/// sack tied with black string (<see cref="CorpseBag"/>: model, the owner's map pin and 30 m beacon) - spawned at the
/// death spot. Current death rule is "resources only" — anything with an
/// equipment profile (tools, wingsuit, grapple, armor) stays on the pawn. Augments stay unless
/// <see cref="PlayerAugments.DropAugmentsOnDeath"/> is enabled. The sack carries a take-only
/// <see cref="ContainerInventory"/> ("&lt;name&gt;'s Loot") that opens with the standard Use/E
/// container flow and destroys itself once emptied.
/// </summary>
public static class DeathLootBag
{
	/// <summary>Host/offline only: move droppable stacks off the pawn into a corpse bag at its position.</summary>
	public static void HostSpawnForDeath( GameObject pawn )
	{
		if ( pawn is null || !pawn.IsValid() )
			return;

		var inventory = pawn.Components.Get<PlayerInventory>();
		var hotbar = pawn.Components.Get<PlayerHotbar>();
		if ( inventory is null || !inventory.HasHostAuthority )
			return;

		// An item still on a remote owner's cursor is part of what they carried: back to the bag before the tally.
		inventory.HostSettleCursor();

		var augments = pawn.Components.Get<PlayerAugments>();
		var stacks = CollectDroppableStacks( inventory, hotbar, augments );
		if ( stacks.Count == 0 )
			return;

		// Spawn before removing anything so a failed spawn never destroys items.
		var bag = SpawnBagObject( pawn, stacks.Count );
		var container = bag?.Components.Get<ContainerInventory>();
		if ( container is null )
			return;

		RemoveDroppablesFromPawn( inventory, hotbar, augments );

		foreach ( var (resourceId, count) in stacks )
			container.HostDepositStack( resourceId, count );
	}

	/// <summary>
	/// Death drop rule: equipment stays; augments stay unless <see cref="PlayerAugments.DropAugmentsOnDeath"/>;
	/// other resources drop.
	/// </summary>
	static bool IsDroppableOnDeath( in InventorySlot slot, PlayerAugments augments )
	{
		if ( slot.IsEmpty )
			return false;

		if ( EquipmentCatalog.TryGet( slot.ResourceId, out _ ) )
			return false;

		if ( AugmentCatalog.IsAugment( slot.ResourceId ) )
			return augments is not null && augments.DropAugmentsOnDeath;

		return true;
	}

	/// <summary>Totals droppable resources across bag + hotbar (+ optional augment bank/installed).</summary>
	static List<(string ResourceId, int Count)> CollectDroppableStacks(
		PlayerInventory inventory,
		PlayerHotbar hotbar,
		PlayerAugments augments )
	{
		var totals = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		var order = new List<string>();

		for ( var i = 0; i < inventory.SlotCount; i++ )
			Accumulate( totals, order, inventory.GetSlot( i ), augments );

		if ( hotbar is not null )
		{
			for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
				Accumulate( totals, order, hotbar.GetSlot( i ), augments );
		}

		if ( augments is not null && augments.DropAugmentsOnDeath )
		{
			var extra = new List<(string ResourceId, int Count)>();
			augments.HostCollectDeathDrops( extra );
			for ( var i = 0; i < extra.Count; i++ )
			{
				var (id, count) = extra[i];
				if ( string.IsNullOrWhiteSpace( id ) || count <= 0 )
					continue;

				id = ResourceCatalog.NormalizeResourceId( id );
				if ( !totals.ContainsKey( id ) )
				{
					totals[id] = 0;
					order.Add( id );
				}

				totals[id] += count;
			}
		}

		var stacks = new List<(string, int)>();
		foreach ( var id in order )
		{
			var remaining = totals[id];
			var maxStack = Math.Max( 1, ResourceCatalog.GetMaxStack( id ) );
			while ( remaining > 0 )
			{
				var take = Math.Min( maxStack, remaining );
				stacks.Add( (id, take) );
				remaining -= take;
			}
		}

		return stacks;
	}

	static void Accumulate(
		Dictionary<string, int> totals,
		List<string> order,
		in InventorySlot slot,
		PlayerAugments augments )
	{
		if ( !IsDroppableOnDeath( slot, augments ) )
			return;

		var id = ResourceCatalog.NormalizeResourceId( slot.ResourceId );
		if ( !totals.ContainsKey( id ) )
		{
			totals[id] = 0;
			order.Add( id );
		}

		totals[id] += slot.Count;
	}

	static void RemoveDroppablesFromPawn( PlayerInventory inventory, PlayerHotbar hotbar, PlayerAugments augments )
	{
		for ( var i = 0; i < inventory.SlotCount; i++ )
		{
			if ( IsDroppableOnDeath( inventory.GetSlot( i ), augments ) )
				inventory.HostTryPickupAll( i, out _ );
		}

		if ( hotbar is not null )
		{
			// Consume (not pickup) so slot binding ghosts survive and refill on re-collection.
			var hotbarTotals = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
			for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
			{
				var slot = hotbar.GetSlot( i );
				if ( !IsDroppableOnDeath( slot, augments ) )
					continue;

				var id = ResourceCatalog.NormalizeResourceId( slot.ResourceId );
				hotbarTotals[id] = hotbarTotals.TryGetValue( id, out var total ) ? total + slot.Count : slot.Count;
			}

			foreach ( var (id, count) in hotbarTotals )
				hotbar.TryConsumeResource( id, count );
		}

		augments?.HostClearAllForDeathDrop();
	}

	/// <summary>The burlap sack (model + its physics hull as a static collider for the Use-key look trace), the loot container and the owner's <see cref="CorpseBag"/>.</summary>
	static GameObject SpawnBagObject( GameObject pawn, int slotCount )
	{
		var scene = pawn.Scene.IsValid() ? pawn.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return null;

		var owner = pawn.Network is { Active: true, Owner: { } netOwner } ? netOwner : Connection.Local;
		var ownerName = string.IsNullOrWhiteSpace( owner?.DisplayName ) ? "Player" : owner.DisplayName;

		var go = new GameObject( true, "death_loot_bag" );
		go.NetworkMode = NetworkMode.Object;
		go.Parent = scene;
		go.WorldPosition = SnapToGround( scene, pawn, pawn.WorldPosition );
		go.WorldRotation = Rotation.FromYaw( Random.Shared.Float( 0f, 360f ) );

		// The model is authored at world size (0.62 m across, 0.55 m tall, origin on the ground): no scale here.
		var model = Model.Load( CorpseBag.ModelPath );
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model;

		var collider = go.Components.Create<ModelCollider>();
		collider.Model = model;
		collider.Static = true;

		var container = go.Components.Create<ContainerInventory>();
		container.SlotCount = Math.Max( 1, slotCount );
		container.Columns = InventoryDefaults.DefaultColumns;
		container.DisplayName = $"{ownerName}'s Loot";
		container.TakeOnly = true;
		container.DestroyWhenEmpty = true;

		var bag = go.Components.Create<CorpseBag>();
		bag.OwnerName = ownerName;
		bag.OwnerConnectionId = owner?.Id ?? Guid.Empty;
		bag.OwnerSteamId = owner?.SteamId.ValueUnsigned ?? 0UL;

		HostNetworkSpawn.TrySpawn( go );
		return go;
	}

	static Vector3 SnapToGround( Scene scene, GameObject ignore, Vector3 desired )
	{
		var start = desired + Vector3.Up * TerrainWorldUnits.MetersToEngine( 1.5f );
		var end = desired - Vector3.Up * TerrainWorldUnits.MetersToEngine( 6f );
		var trace = scene.Trace.Ray( start, end ).IgnoreGameObjectHierarchy( ignore ).Run();
		if ( !trace.Hit )
			return desired;

		// The sack's origin is its underside: rest on the surface, sunk 3 cm so it doesn't look like it floats on slopes.
		return trace.HitPosition - Vector3.Up * TerrainWorldUnits.MetersToEngine( 0.03f );
	}
}
