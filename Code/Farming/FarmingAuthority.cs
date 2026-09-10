using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host commit path for farming. Clients send intent through <see cref="PlayerFarming"/>; the host
/// re-checks the tool, the reach, the surface and the spacing exactly once, then spawns the
/// networked tile / plant or hands out the harvest. Nothing here runs per frame.
/// </summary>
public static class FarmingAuthority
{
	static readonly Random Rng = new();

	/// <summary>Host/offline: convert the dirt under <paramref name="tileCenter"/> into a tilled square.</summary>
	public static bool TryTill( GameObject pawn, Vector3 tileCenter, float reachMeters, out string reason )
	{
		reason = string.Empty;
		if ( pawn is null || !pawn.IsValid() )
		{
			reason = "no pawn";
			return false;
		}

		if ( !HasWorkingHoe( pawn, out reason ) )
			return false;

		if ( !WithinReach( pawn, tileCenter, reachMeters ) )
		{
			reason = "out of reach";
			return false;
		}

		var scene = pawn.Scene.IsValid() ? pawn.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
		{
			reason = "no scene";
			return false;
		}

		// Re-find the ground from above the requested centre so a client cannot till mid-air.
		var probe = TerrainWorldUnits.MetersToEngine( 2f );
		var tr = FarmingRules.TraceView( scene, pawn, tileCenter + Vector3.Up * probe, Vector3.Down, probe * 2f );
		if ( !FarmingRules.CanTillAt( tr, out var snapped, out reason ) )
			return false;

		var prefab = GameObject.GetPrefab( FarmingRules.TilledSoilPrefab );
		if ( prefab is null || !prefab.IsValid() )
		{
			reason = $"prefab missing: {FarmingRules.TilledSoilPrefab}";
			Log.Warning( $"[FarmingAuthority] {reason}" );
			return false;
		}

		var tile = prefab.Clone( new Transform( new Vector3( snapped.x, snapped.y, tr.HitPosition.z ), Rotation.Identity ) );
		tile.Name = "tilled_soil";
		HostNetworkSpawn.TrySpawn( tile );

		// Real effect only — a rejected till costs nothing.
		ToolDurability.HostAddWearToActiveTool( pawn );
		return true;
	}

	/// <summary>Host/offline: plant one <paramref name="seedId"/> from the pawn's hotbar / bag at <paramref name="point"/>.</summary>
	public static bool TrySow( GameObject pawn, string seedId, Vector3 point, float reachMeters, out string reason )
	{
		reason = string.Empty;
		if ( pawn is null || !pawn.IsValid() )
		{
			reason = "no pawn";
			return false;
		}

		seedId = ResourceCatalog.NormalizeResourceId( seedId );
		if ( !ResourceDefinitionCatalog.TryGetSeed( seedId, out _ ) )
		{
			reason = "not a seed";
			return false;
		}

		var inventory = pawn.Components.Get<PlayerInventory>();
		if ( inventory is null || !inventory.HasHostAuthority )
		{
			reason = "no inventory";
			return false;
		}

		if ( !WithinReach( pawn, point, reachMeters ) )
		{
			reason = "out of reach";
			return false;
		}

		if ( !FarmingRules.CanSowAt( point, out var tile, out reason ) )
			return false;

		var prefab = GameObject.GetPrefab( FarmingRules.FarmPlantPrefab );
		if ( prefab is null || !prefab.IsValid() )
		{
			reason = $"prefab missing: {FarmingRules.FarmPlantPrefab}";
			Log.Warning( $"[FarmingAuthority] {reason}" );
			return false;
		}

		var cost = new[] { new CraftingIngredient { ResourceId = seedId, Amount = 1 } };
		if ( !inventory.HostTryConsumeResources( cost ) )
		{
			reason = "no seed in inventory";
			return false;
		}

		var spawnAt = new Vector3( point.x, point.y, tile.SurfaceZ );
		var plantGo = prefab.Clone( new Transform( spawnAt, Rotation.Identity ) );
		plantGo.Name = $"plant_{seedId}";

		var plant = plantGo.Components.Get<FarmPlant>();
		if ( plant is null )
		{
			Log.Warning( $"[FarmingAuthority] {FarmingRules.FarmPlantPrefab} has no FarmPlant component — fix the prefab." );
			plantGo.Destroy();
			inventory.HostTryAddResource( seedId, 1 );
			reason = "bad plant prefab";
			return false;
		}

		plant.HostConfigure( seedId );
		HostNetworkSpawn.TrySpawn( plantGo );
		return true;
	}

	/// <summary>
	/// Host/offline: pick a matured plant (crop) or a bolted one (seeds). Single plants are removed
	/// afterwards; bushes wind back and regrow. Rejected when the bag cannot hold the worst-case yield,
	/// so a harvest never silently loses items.
	/// </summary>
	public static bool TryHarvest( GameObject pawn, FarmPlant plant, float reachMeters, out string reason )
	{
		reason = string.Empty;
		if ( pawn is null || !pawn.IsValid() || plant is null || !plant.IsValid() || !plant.GameObject.IsValid() )
		{
			reason = "no plant";
			return false;
		}

		if ( !plant.HasHostAuthority )
		{
			reason = "not host";
			return false;
		}

		if ( !plant.CanHarvest )
		{
			reason = "not ready";
			return false;
		}

		var data = plant.Data;
		if ( data is null )
		{
			reason = "unknown seed";
			return false;
		}

		var inventory = pawn.Components.Get<PlayerInventory>();
		if ( inventory is null || !inventory.HasHostAuthority )
		{
			reason = "no inventory";
			return false;
		}

		if ( !WithinReach( pawn, plant.GameObject.WorldPosition, reachMeters ) )
		{
			reason = "out of reach";
			return false;
		}

		if ( !TryGetHarvestYield( plant, data, out var resourceId, out var low, out var high ) )
		{
			reason = "nothing to harvest";
			return false;
		}

		if ( !inventory.CanAcceptResource( resourceId, high ) )
		{
			reason = "inventory full";
			return false;
		}

		var amount = low >= high ? low : Rng.Next( low, high + 1 );
		if ( amount > 0 && !inventory.HostTryAddResource( resourceId, amount ) )
		{
			reason = "inventory full";
			return false;
		}

		if ( data.IsBush && !plant.IsBolting )
			plant.HostOnBushHarvested( data );
		else
			plant.GameObject.Destroy();

		return true;
	}

	/// <summary>Host/offline: hoe a plant out of the ground — the plant is gone and one seed comes back.</summary>
	public static bool TryUproot( GameObject pawn, FarmPlant plant, float reachMeters, out string reason )
	{
		reason = string.Empty;
		if ( pawn is null || !pawn.IsValid() || plant is null || !plant.IsValid() || !plant.GameObject.IsValid() )
		{
			reason = "no plant";
			return false;
		}

		if ( !plant.HasHostAuthority )
		{
			reason = "not host";
			return false;
		}

		if ( !HasWorkingHoe( pawn, out reason ) )
			return false;

		var inventory = pawn.Components.Get<PlayerInventory>();
		if ( inventory is null || !inventory.HasHostAuthority )
		{
			reason = "no inventory";
			return false;
		}

		if ( !WithinReach( pawn, plant.GameObject.WorldPosition, reachMeters ) )
		{
			reason = "out of reach";
			return false;
		}

		var seedId = plant.SeedId;
		var seeds = FarmingRules.UprootSeedAmount;
		if ( seeds > 0 && !string.IsNullOrWhiteSpace( seedId ) )
		{
			if ( !inventory.CanAcceptResource( seedId, seeds ) )
			{
				reason = "inventory full";
				return false;
			}

			inventory.HostTryAddResource( seedId, seeds );
		}

		plant.GameObject.Destroy();
		ToolDurability.HostAddWearToActiveTool( pawn );
		return true;
	}

	/// <summary>What a harvest hands out right now: crop when matured, seeds when bolted.</summary>
	public static bool TryGetHarvestYield( FarmPlant plant, FarmPlantData data, out string resourceId, out int low, out int high )
	{
		resourceId = string.Empty;
		low = 0;
		high = 0;
		if ( plant is null || data is null )
			return false;

		if ( plant.IsBolting )
		{
			resourceId = plant.SeedId;
			low = high = Math.Max( 1, data.BoltSeedAmount );
			return !string.IsNullOrWhiteSpace( resourceId );
		}

		if ( !plant.IsMatured || string.IsNullOrWhiteSpace( data.HarvestResourceId ) )
			return false;

		resourceId = ResourceCatalog.NormalizeResourceId( data.HarvestResourceId );
		low = Math.Max( 1, data.HarvestAmountLow );
		high = Math.Max( low, data.HarvestAmountHigh );
		return true;
	}

	static bool HasWorkingHoe( GameObject pawn, out string reason )
	{
		reason = string.Empty;
		var equipment = pawn.Components.Get<PlayerEquipment>();
		if ( equipment is null || !equipment.MainHandHasAction( EquippedItemActions.Till ) )
		{
			reason = "no hoe equipped";
			return false;
		}

		if ( ToolDurability.IsActiveToolBroken( pawn ) )
		{
			reason = "hoe broken";
			return false;
		}

		return true;
	}

	static bool WithinReach( GameObject pawn, Vector3 point, float reachMeters )
	{
		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, reachMeters ) + FarmingRules.ReachSlackMeters );
		var eye = pawn.WorldPosition + Vector3.Up * 64f;
		return Vector3.DistanceBetween( eye, point ) <= reach
		       || Vector3.DistanceBetween( pawn.WorldPosition, point ) <= reach;
	}
}
