using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Death loot drop: when a pawn dies on the host, its droppable stacks move into a pitch-black
/// sphere spawned at the death spot. Current death rule is "resources only" — anything with an
/// equipment profile (tools, wingsuit, grapple, armor) stays on the pawn; future all/nothing
/// switches slot into <see cref="IsDroppableOnDeath"/>. The sphere carries a take-only
/// <see cref="ContainerInventory"/> ("&lt;name&gt;'s Loot") that opens with the standard Use/E
/// container flow and destroys itself once emptied.
/// </summary>
public static class DeathLootBag
{
	/// <summary>Comfortably larger than a dropped-item pickup (~0.2 m) so it reads from a distance.</summary>
	const float SphereDiameterMeters = 0.5f;

	/// <summary>Host/offline only: move droppable stacks off the pawn into a loot disk at its position.</summary>
	public static void HostSpawnForDeath( GameObject pawn )
	{
		if ( pawn is null || !pawn.IsValid() )
			return;

		var inventory = pawn.Components.Get<PlayerInventory>();
		var hotbar = pawn.Components.Get<PlayerHotbar>();
		if ( inventory is null || !inventory.HasHostAuthority )
			return;

		var stacks = CollectDroppableStacks( inventory, hotbar );
		if ( stacks.Count == 0 )
			return;

		// Spawn before removing anything so a failed spawn never destroys items.
		var bag = SpawnBagObject( pawn, stacks.Count );
		var container = bag?.Components.Get<ContainerInventory>();
		if ( container is null )
			return;

		RemoveDroppablesFromPawn( inventory, hotbar );

		foreach ( var (resourceId, count) in stacks )
			container.HostDepositStack( resourceId, count );
	}

	/// <summary>Death drop rule (currently "resources only"): equipment-profile items stay with the pawn.</summary>
	static bool IsDroppableOnDeath( in InventorySlot slot ) =>
		!slot.IsEmpty && !EquipmentCatalog.TryGet( slot.ResourceId, out _ );

	/// <summary>Totals droppable resources across bag + hotbar, packed into max-size stacks.</summary>
	static List<(string ResourceId, int Count)> CollectDroppableStacks( PlayerInventory inventory, PlayerHotbar hotbar )
	{
		var totals = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		var order = new List<string>();

		for ( var i = 0; i < inventory.SlotCount; i++ )
			Accumulate( totals, order, inventory.GetSlot( i ) );

		if ( hotbar is not null )
		{
			for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
				Accumulate( totals, order, hotbar.GetSlot( i ) );
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

	static void Accumulate( Dictionary<string, int> totals, List<string> order, in InventorySlot slot )
	{
		if ( !IsDroppableOnDeath( slot ) )
			return;

		var id = ResourceCatalog.NormalizeResourceId( slot.ResourceId );
		if ( !totals.ContainsKey( id ) )
		{
			totals[id] = 0;
			order.Add( id );
		}

		totals[id] += slot.Count;
	}

	static void RemoveDroppablesFromPawn( PlayerInventory inventory, PlayerHotbar hotbar )
	{
		for ( var i = 0; i < inventory.SlotCount; i++ )
		{
			if ( IsDroppableOnDeath( inventory.GetSlot( i ) ) )
				inventory.HostTryPickupAll( i, out _ );
		}

		if ( hotbar is null )
			return;

		// Consume (not pickup) so slot binding ghosts survive and refill on re-collection.
		var hotbarTotals = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
		{
			var slot = hotbar.GetSlot( i );
			if ( !IsDroppableOnDeath( slot ) )
				continue;

			var id = ResourceCatalog.NormalizeResourceId( slot.ResourceId );
			hotbarTotals[id] = hotbarTotals.TryGetValue( id, out var total ) ? total + slot.Count : slot.Count;
		}

		foreach ( var (id, count) in hotbarTotals )
			hotbar.TryConsumeResource( id, count );
	}

	/// <summary>Pitch-black sphere with a static collider (Use-key look trace) and the loot container.</summary>
	static GameObject SpawnBagObject( GameObject pawn, int slotCount )
	{
		var scene = pawn.Scene.IsValid() ? pawn.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return null;

		var ownerName = ResolveOwnerName( pawn );

		var go = new GameObject( true, "death_loot_bag" );
		go.NetworkMode = NetworkMode.Never;
		go.Parent = scene;
		go.WorldPosition = SnapToGround( scene, pawn, pawn.WorldPosition );

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( "models/dev/sphere.vmdl" );
		renderer.Tint = Color.Black;

		// Uniform-scale the dev sphere to the target diameter regardless of model bounds.
		var bounds = renderer.Model?.Bounds.Size ?? new Vector3( 32f );
		var modelDiameter = Math.Max( 1f, Math.Max( bounds.x, Math.Max( bounds.y, bounds.z ) ) );
		var diameter = TerrainWorldUnits.MetersToEngine( SphereDiameterMeters );
		go.LocalScale = new Vector3( diameter / modelDiameter );

		// Collider radius is pre-LocalScale, so half the model diameter matches the visual.
		var collider = go.Components.Create<SphereCollider>();
		collider.Static = true;
		collider.Radius = modelDiameter * 0.5f;

		var container = go.Components.Create<ContainerInventory>();
		container.SlotCount = Math.Max( 1, slotCount );
		container.Columns = InventoryDefaults.DefaultColumns;
		container.DisplayName = $"{ownerName}'s Loot";
		container.TakeOnly = true;
		container.DestroyWhenEmpty = true;

		return go;
	}

	/// <summary>Steam display name of the owning connection ("Tutti" → "Tutti's Loot"); offline uses the local connection.</summary>
	static string ResolveOwnerName( GameObject pawn )
	{
		var name = pawn.Network is { Active: true, Owner: { } owner }
			? owner.DisplayName
			: Connection.Local?.DisplayName;

		return string.IsNullOrWhiteSpace( name ) ? "Player" : name;
	}

	static Vector3 SnapToGround( Scene scene, GameObject ignore, Vector3 desired )
	{
		var start = desired + Vector3.Up * TerrainWorldUnits.MetersToEngine( 1.5f );
		var end = desired - Vector3.Up * TerrainWorldUnits.MetersToEngine( 6f );
		var trace = scene.Trace.Ray( start, end ).IgnoreGameObjectHierarchy( ignore ).Run();
		if ( !trace.Hit )
			return desired;

		// Rest on the surface, slightly sunk so it doesn't look like it floats on slopes.
		return trace.HitPosition + Vector3.Up * TerrainWorldUnits.MetersToEngine( SphereDiameterMeters * 0.4f );
	}
}
