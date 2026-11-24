using System;
using Sandbox;

namespace Survival;

/// <summary>Dev: fold / unfold a foldable vehicle (the pod scooter) from the console, through the host paths the keys use.</summary>
public static partial class VehicleDevCommands
{
	[ConCmd( "vehicle_stow" )]
	public static void ConCmdStow()
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Vehicle] vehicle_stow: host only." );
			return;
		}

		var pawn = FindLocalPawn();
		var vehicle = FindNearestVehicle( pawn );
		if ( pawn is null || vehicle is null )
		{
			Log.Warning( "[Vehicle] vehicle_stow: needs a local pawn and a vehicle." );
			return;
		}

		var name = vehicle.DisplayName;
		var item = vehicle.StowItemId;
		var ok = vehicle.HostTryStow( pawn );
		Log.Info( ok
			? $"[Vehicle] folded {name} into {item}."
			: $"[Vehicle] vehicle_stow: {name} did not fold (stowable={vehicle.IsStowable} canStow={vehicle.CanStow( pawn )}; a full bag also refuses)." );
	}

	[ConCmd( "vehicle_deploy" )]
	public static void ConCmdDeploy( string itemId = "vehicle_pod_scooter_folded", float metersAhead = 1.8f )
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Vehicle] vehicle_deploy: host only." );
			return;
		}

		var pawn = FindLocalPawn();
		if ( pawn is null )
		{
			Log.Warning( "[Vehicle] vehicle_deploy: no local player pawn." );
			return;
		}

		// Along the view, like the Attack1 path (the pawn root does not turn with the camera).
		var forward = BuildViewCamera.TryGetViewRay( pawn, out _, out var view ) ? view.WithZ( 0f ) : pawn.WorldRotation.Forward.WithZ( 0f );
		forward = forward.LengthSquared < 0.01f ? Vector3.Forward : forward.Normal;
		var wanted = pawn.WorldPosition + forward * TerrainWorldUnits.MetersToEngine( Math.Clamp( metersAhead, 0.5f, Vehicle.DeployReachMeters ) );
		var ok = Vehicle.HostTryDeployFromItem( pawn, itemId, wanted, MathX.RadianToDegree( MathF.Atan2( forward.y, forward.x ) ) );
		Log.Info( ok
			? $"[Vehicle] unfolded {itemId}."
			: $"[Vehicle] vehicle_deploy: could not unfold '{itemId}' (in the bag: {pawn.Components.Get<PlayerInventory>()?.CountResource( itemId ) ?? 0}, unfolds into: {( Vehicle.TryGetDeployPiece( itemId, out var pieceId, out _ ) ? pieceId : "nothing - no deployVehicle on the item row" )}, floor ok: {Vehicle.TryFindDeployFloor( pawn.Scene, pawn, wanted, out _ )}, seated: {pawn.Components.Get<PlayerMovement>()?.IsSeated}; `give {itemId}` first)." );
	}
}
