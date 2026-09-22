using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Look at a placed door + E swings it (open away from you / close). Owner traces and sends
/// intent; the host re-checks reach once and commits through <see cref="BuildDoor.HostToggle"/>.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Door" ), Title( "Use Action" )]
	public string DoorUseAction { get; set; } = "Use";

	[Property, Group( "Door" ), Title( "Focus Scan Interval (seconds)" )]
	public float DoorFocusScanIntervalSeconds { get; set; } = 0.15f;

	/// <summary>Door under the crosshair (drives the "E — Open Door / Close Door" HUD prompt).</summary>
	public BuildDoor FocusedDoor { get; private set; }

	public event Action FocusedDoorChanged;

	double _nextDoorFocusScanAt;

	void TickDoorAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		var pressed = !menuOpen && Input.Pressed( DoorUseAction );
		TickDoorFocusPrompt( menuOpen, force: pressed );

		if ( !pressed )
			return;

		if ( IsBuildHammerPreviewing() || IsGrappleRetractActive() )
			return;

		if ( FocusedDoor is null || !FocusedDoor.IsValid() )
			return;

		OwnerToggleDoor( FocusedDoor );
	}

	void TickDoorFocusPrompt( bool menuOpen, bool force = false )
	{
		if ( FocusedDoor is not null && !FocusedDoor.IsValid() )
			SetFocusedDoor( null );

		if ( menuOpen || IsBuildHammerPreviewing() )
		{
			SetFocusedDoor( null );
			return;
		}

		if ( !force && Time.NowDouble < _nextDoorFocusScanAt )
			return;

		_nextDoorFocusScanAt = Time.NowDouble + Math.Max( 0.05, DoorFocusScanIntervalSeconds );

		// Chest / station / workbench under the reticule keep their prompt.
		if ( FocusedContainer is not null || FocusedAugmentStation is not null || FocusedWorkbench is not null )
		{
			SetFocusedDoor( null );
			return;
		}

		var reach = FocusedDoor is { IsValid: true } ? FocusedDoor.UseReachMeters : 3f;
		if ( BuildDoor.TryFindFocusedDoor( GameObject, reach, out var door ) )
			SetFocusedDoor( door );
		else
			SetFocusedDoor( null );
	}

	void SetFocusedDoor( BuildDoor door )
	{
		if ( ReferenceEquals( FocusedDoor, door ) )
			return;

		FocusedDoor = door;
		FocusedDoorChanged?.Invoke();
	}

	void OwnerToggleDoor( BuildDoor door )
	{
		if ( door is null || !door.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			door.HostToggle( GameObject );
			return;
		}

		RpcHostToggleDoor( door.GameObject.Id );
	}

	[Rpc.Host]
	void RpcHostToggleDoor( Guid doorRootId )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		var door = Scene.Directory.FindByGuid( doorRootId )?.Components.Get<BuildDoor>();
		if ( door is null || !door.IsValid() )
			return;

		// Client sent intent; the host validates reach once — no toggling doors across the map.
		if ( !door.IsWithinUseReach( GameObject ) )
			return;

		door.HostToggle( GameObject );
	}
}
