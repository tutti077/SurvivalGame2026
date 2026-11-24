using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Folding a vehicle into an inventory item and unfolding it again (the pod scooter, Mark 2026-11-19: "the
/// player can pick up and deploy whenever they want"). A vehicle whose prefab names a <see cref="StowItemId"/>
/// can be folded by anyone standing at it while nobody rides it: the host hands the pawn that item, gives
/// back whatever is in the fuel slot (inventory first, the ground if the bag is full) and removes the vehicle.
/// The item's <c>deployVehicle</c> field in <c>resources.json</c> names the build piece to unfold
/// (<see cref="HostTryDeployFromItem"/>): the owner aims a spot, the host re-checks it once and spawns the prefab
/// there the way the build hammer drops a vehicle. Hit points do not travel with the item: it unfolds whole.
/// </summary>
public sealed partial class Vehicle
{
	/// <summary>Inventory item this vehicle folds into (empty = cannot be folded).</summary>
	[Property, Group( "Stow" ), Title( "Stow Item Id" )]
	public string StowItemId { get; set; } = string.Empty;

	/// <summary>Furthest from the pawn a deploy spot may be (m): the owner aims it, the host holds it to this.</summary>
	public const float DeployReachMeters = 3f;

	public bool IsStowable => !string.IsNullOrWhiteSpace( StowItemId );

	bool HasAnyOccupant
	{
		get
		{
			for ( var i = 0; i < SeatOccupants.Count; i++ )
			{
				if ( SeatOccupants[i] != Guid.Empty )
					return true;
			}

			return false;
		}
	}

	/// <summary>Can this viewer fold the vehicle up right now? (Flipped is fine: folding a fallen scooter is allowed.)</summary>
	public bool CanStow( GameObject viewer )
		=> IsStowable && !IsBroken && !IsPreviewGhost && !HasAnyOccupant && viewer is { IsValid: true }
		   && viewer.Components.Get<PlayerMovement>() is { IsSeated: false };

	/// <summary>Host: fold the vehicle into <see cref="StowItemId"/> for this pawn. False when the bag has no room for it.</summary>
	public bool HostTryStow( GameObject pawn )
	{
		if ( !HasHostAuthority || !CanStow( pawn ) )
			return false;

		var inventory = pawn.Components.Get<PlayerInventory>();
		if ( inventory is null || !inventory.IsValid() )
			return false;

		if ( !inventory.HostTryAddResource( StowItemId, 1 ) )
			return false;

		// The tank empties into the bag; what does not fit lands at the pawn's feet.
		if ( _storage is { IsValid: true } )
		{
			for ( var i = 0; i < _storage.SlotCount; i++ )
			{
				var slot = _storage.GetSlot( i );
				if ( slot.IsEmpty )
					continue;

				if ( !inventory.HostTryAddResource( slot.ResourceId, slot.Count, slot.Wear, slot.CrafterName, slot.Tier ) )
				{
					HeldStackWorldDrop.TrySpawnWorldDrop( Scene, slot.ResourceId, slot.Count, WorldPosition + Vector3.Up * 20f, GameObject,
						wear: slot.Wear, crafterName: slot.CrafterName, tier: slot.Tier );
				}

				_storage.HostTryRemoveFromSlot( i, slot.Count );
			}
		}

		GameObject.Destroy();
		return true;
	}

	/// <summary>The build piece an item unfolds into (<c>deployVehicle</c> in resources.json), if it is a vehicle piece.</summary>
	public static bool TryGetDeployPiece( string itemId, out string pieceId, out BuildPieceData piece )
	{
		pieceId = null;
		piece = null;
		if ( string.IsNullOrWhiteSpace( itemId ) || !ResourceDefinitionCatalog.TryGet( itemId, out var item )
		     || string.IsNullOrWhiteSpace( item.DeployVehicle ) )
			return false;

		if ( !BuildPieceCatalog.TryGet( item.DeployVehicle, out piece ) || !piece.Vehicle || string.IsNullOrWhiteSpace( piece.Prefab ) )
			return false;

		pieceId = item.DeployVehicle;
		return true;
	}

	/// <summary>
	/// The ground spot a pawn would unfold a vehicle on: <see cref="DeployReachMeters"/> × 0.6 ahead along its
	/// view, flattened. Needs a walkable floor there and standing room above it. Used by the owner to aim and by
	/// the host to check the aimed spot once.
	/// </summary>
	public static bool TryFindDeployFloor( Scene scene, GameObject pawn, Vector3 wanted, out Vector3 floor )
	{
		floor = default;
		if ( !scene.IsValid() || pawn is null || !pawn.IsValid() )
			return false;

		var tr = scene.Trace.Ray( wanted + Vector3.Up * 60f, wanted - Vector3.Up * 120f )
			.IgnoreGameObjectHierarchy( pawn )
			.WithoutTags( "player", "trigger", "worlddrop", "water" )
			.Run();
		if ( !tr.Hit || tr.StartedSolid || tr.Normal.z < 0.5f )
			return false;

		// Room for the scooter itself: nothing solid in a ball above the floor.
		var centre = tr.HitPosition + Vector3.Up * 30f;
		var room = scene.Trace.Sphere( 18f, centre, centre )
			.IgnoreGameObjectHierarchy( pawn )
			.WithoutTags( "player", "trigger", "worlddrop", "water" )
			.Run();
		if ( room.Hit && room.StartedSolid )
			return false;

		floor = tr.HitPosition;
		return true;
	}

	/// <summary>Host: spend one <paramref name="itemId"/> from the pawn and unfold its vehicle at the aimed spot, facing <paramref name="yawDegrees"/>.</summary>
	public static bool HostTryDeployFromItem( GameObject pawn, string itemId, Vector3 wanted, float yawDegrees )
	{
		if ( pawn is null || !pawn.IsValid() || ( pawn.Network is { Active: true } && !Networking.IsHost ) )
			return false;

		if ( !TryGetDeployPiece( itemId, out var pieceId, out var piece ) )
			return false;

		var movement = pawn.Components.Get<PlayerMovement>();
		if ( movement is null || movement.IsSeated )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( DeployReachMeters );
		if ( Vector3.DistanceBetween( wanted, pawn.WorldPosition ) > reach + 40f )
			return false;

		var scene = pawn.Scene;
		if ( !TryFindDeployFloor( scene, pawn, wanted, out var floor ) )
			return false;

		var inventory = pawn.Components.Get<PlayerInventory>();
		if ( inventory is null || inventory.CountResource( itemId ) < 1 )
			return false;

		// Spawn first: a missing prefab must not eat the item.
		var transform = new Transform( floor + Vector3.Up * 20f, Rotation.FromYaw( yawDegrees ) );
		var spawned = BuildPrefabUtility.SpawnPiece( scene, piece.Prefab, pieceId, transform );
		if ( spawned is null || !spawned.IsValid() || spawned.Components.Get<Vehicle>() is null )
		{
			spawned?.Destroy();
			Log.Warning( $"[Vehicle] '{itemId}' unfolds into '{pieceId}' but its prefab '{piece.Prefab}' is missing or has no Vehicle." );
			return false;
		}

		if ( !inventory.HostTryConsumeResources( new[] { new CraftingIngredient { ResourceId = itemId, Amount = 1 } } ) )
		{
			spawned.Destroy();
			return false;
		}

		spawned.Name = pieceId;
		HostNetworkSpawn.TrySpawn( spawned );
		return true;
	}
}
