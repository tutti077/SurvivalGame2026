using Sandbox;

namespace Survival;

/// <summary>
/// Scene-only debug: after a pad teleport, press J to snap the local pawn back to that arrival
/// (a waypoint). Enable this component on the scene's Teleporters object — leave it off elsewhere.
/// Uses the existing <c>SpawnPlayer</c> action (J).
/// </summary>
[Title( "Teleport Waypoint Debug" )]
public sealed class TeleportWaypointDebug : Component
{
	[Property, Title( "Recall Action" )]
	public string RecallAction { get; set; } = "SpawnPlayer";

	[Property, Group( "Debug" )]
	public bool LogRecall { get; set; }

	protected override void OnUpdate()
	{
		if ( !Enabled )
			return;

		if ( !WasRecallPressed() )
			return;

		var pawn = ResolveLocalPawn();
		if ( pawn is null )
			return;

		if ( pawn.Components.Get<PlayerGameMenuController>() is { IsMenuOpen: true } )
			return;

		var movement = pawn.Components.Get<PlayerMovement>();
		if ( movement is { TimeTrialInputLocked: true } )
			return;

		if ( !TeleportPad.TryGetLastArrival( pawn, out var pos, out var rot, out var pad ) )
		{
			if ( LogRecall )
				Log.Info( "[TeleportWaypointDebug] J pressed — no pad teleport recorded yet." );
			return;
		}

		movement?.DetachGrappleForWorldTeleport();

		pawn.WorldPosition = pos;
		pawn.WorldRotation = rot;
		pawn.Transform.ClearInterpolation();
		if ( pawn.Network is { Active: true } )
			pawn.Network.ClearInterpolation();

		var rb = pawn.Components.Get<Rigidbody>();
		if ( rb is not null )
		{
			rb.Velocity = Vector3.Zero;
			rb.AngularVelocity = Vector3.Zero;
		}

		if ( pad is not null && pad.IsValid() )
		{
			pad.HoldUntilExit( pawn.Id );
			TeleportPad.MarkCooldown( pawn.Id, pad.CooldownSeconds );
		}

		if ( LogRecall )
			Log.Info( $"[TeleportWaypointDebug] Recalled {pawn.Name} → {pos}" );
	}

	bool WasRecallPressed()
	{
		if ( !string.IsNullOrWhiteSpace( RecallAction ) && Input.Pressed( RecallAction ) )
			return true;

		return Input.Keyboard.Pressed( "J" );
	}

	GameObject ResolveLocalPawn()
	{
		if ( !Scene.IsValid() )
			return null;

		foreach ( var movement in Scene.GetAllComponents<PlayerMovement>() )
		{
			if ( movement is null || !movement.GameObject.IsValid() )
				continue;
			if ( movement.GameObject.IsProxy )
				continue;
			return movement.GameObject;
		}

		return null;
	}
}
