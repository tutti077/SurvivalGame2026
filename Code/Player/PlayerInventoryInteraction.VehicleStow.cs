using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Folding vehicles (the pod scooter): three owner intents, each re-checked once on the host.
/// <list type="bullet">
/// <item><b>Take</b> — E on the folded scooter docked in a stasis pod puts its item in the bag
/// (<see cref="StasisPod.HostTryTakeScooter"/>).</item>
/// <item><b>Unfold</b> — Attack1 with a folded-vehicle item (<c>deployVehicle</c> in resources.json) on the active
/// hotbar slot stands the vehicle on the ground ahead (<see cref="Vehicle.HostTryDeployFromItem"/>).</item>
/// <item><b>Fold</b> — the stow key (R) while looking at an empty foldable vehicle packs it back into its item
/// (<see cref="Vehicle.HostTryStow"/>).</item>
/// </list>
/// Ticked from <c>TickVehicleAccess</c>, so none of it runs from a seat or with a menu open.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Vehicle" ), Title( "Stow Action" )]
	public string VehicleStowAction { get; set; } = "Reload";

	[Property, Group( "Vehicle" ), Title( "Deploy Action" )]
	public string VehicleDeployAction { get; set; } = "Attack1";

	/// <summary>Stasis pod whose docked scooter is under the crosshair (drives the take prompt; shares <see cref="FocusedVehicleChanged"/>).</summary>
	public StasisPod FocusedPodScooter { get; private set; }

	PlayerEquippedItem _stowEquipped;

	void SetFocusedPodScooter( StasisPod pod )
	{
		if ( ReferenceEquals( FocusedPodScooter, pod ) )
			return;

		FocusedPodScooter = pod;
		FocusedVehicleChanged?.Invoke();
	}

	void TickVehicleStowAndDeploy( bool menuOpen )
	{
		if ( menuOpen || IsBuildHammerPreviewing() )
			return;

		if ( Input.Pressed( VehicleStowAction ) && FocusedVehicle is { IsValid: true } focused && focused.CanStow( GameObject ) )
		{
			OwnerStowVehicle( focused );
			return;
		}

		if ( Input.Pressed( VehicleDeployAction ) )
			OwnerTryDeployActiveHotbarVehicle();
	}

	void OwnerStowVehicle( Vehicle vehicle )
	{
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			vehicle.HostTryStow( GameObject );
			return;
		}

		RpcHostStowVehicle( vehicle.GameObject.Id );
	}

	void OwnerTryDeployActiveHotbarVehicle()
	{
		_stowEquipped ??= Components.Get<PlayerEquippedItem>();
		if ( _stowEquipped is null )
			return;

		var itemId = _stowEquipped.ActiveHotbarResourceId;
		if ( string.IsNullOrWhiteSpace( itemId ) || !Vehicle.TryGetDeployPiece( itemId, out _, out _ ) )
			return;

		// Ahead of the pawn along the view, flattened; the scooter faces the way the player looks.
		if ( !BuildViewCamera.TryGetViewRay( GameObject, out _, out var direction ) )
			return;

		var flat = direction.WithZ( 0f );
		if ( flat.LengthSquared < 1e-4f )
			return;
		flat = flat.Normal;

		var wanted = WorldPosition + flat * TerrainWorldUnits.MetersToEngine( Vehicle.DeployReachMeters * 0.6f );
		if ( !Vehicle.TryFindDeployFloor( Scene, GameObject, wanted, out _ ) )
			return;

		var yaw = MathX.RadianToDegree( MathF.Atan2( flat.y, flat.x ) );
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			Vehicle.HostTryDeployFromItem( GameObject, itemId, wanted, yaw );
			return;
		}

		RpcHostDeployVehicle( itemId, wanted, yaw );
	}

	void OwnerTakePodScooter( StasisPod pod )
	{
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			pod.HostTryTakeScooter( GameObject );
			return;
		}

		RpcHostTakePodScooter( pod.GameObject.Id );
	}

	bool IsRpcFromOwner()
		=> !( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		      && !ConnectionIdentity.SameClient( caller, owner ) );

	[Rpc.Host]
	void RpcHostStowVehicle( Guid vehicleRootId )
	{
		if ( !Networking.IsHost || !IsRpcFromOwner() )
			return;

		var vehicle = Scene.Directory.FindByGuid( vehicleRootId )?.Components.Get<Vehicle>();
		if ( vehicle is null || !vehicle.IsValid() || !vehicle.IsWithinUseReach( GameObject ) )
			return;

		vehicle.HostTryStow( GameObject );
	}

	[Rpc.Host]
	void RpcHostDeployVehicle( string itemId, Vector3 wanted, float yawDegrees )
	{
		if ( !Networking.IsHost || !IsRpcFromOwner() )
			return;

		Vehicle.HostTryDeployFromItem( GameObject, itemId, wanted, yawDegrees );
	}

	[Rpc.Host]
	void RpcHostTakePodScooter( Guid podRootId )
	{
		if ( !Networking.IsHost || !IsRpcFromOwner() )
			return;

		var pod = Scene.Directory.FindByGuid( podRootId )?.Components.Get<StasisPod>();
		if ( pod is null || !pod.IsValid() )
			return;

		pod.HostTryTakeScooter( GameObject );
	}
}
