using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Look at a placed <see cref="BearTrap"/> + E: arm it, disarm it, or pry it open to free whoever
/// it holds. Owner traces and sends intent; the host re-checks reach once and commits through
/// <see cref="BearTrap.HostUse"/>.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Trap" ), Title( "Use Action" )]
	public string TrapUseAction { get; set; } = "Use";

	[Property, Group( "Trap" ), Title( "Focus Scan Interval (seconds)" )]
	public float TrapFocusScanIntervalSeconds { get; set; } = 0.15f;

	/// <summary>Trap under the crosshair (drives the "E — Arm Trap / Disarm Trap / Open Trap" HUD prompt).</summary>
	public BearTrap FocusedTrap { get; private set; }

	public event Action FocusedTrapChanged;

	double _nextTrapFocusScanAt;

	void TickTrapAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		var pressed = !menuOpen && Input.Pressed( TrapUseAction );
		TickTrapFocusPrompt( menuOpen, force: pressed );

		if ( !pressed )
			return;

		if ( IsBuildHammerPreviewing() || IsGrappleRetractActive() )
			return;

		if ( FocusedTrap is null || !FocusedTrap.IsValid() )
			return;

		OwnerUseTrap( FocusedTrap );
	}

	void TickTrapFocusPrompt( bool menuOpen, bool force = false )
	{
		if ( FocusedTrap is not null && !FocusedTrap.IsValid() )
			SetFocusedTrap( null );

		if ( menuOpen || IsBuildHammerPreviewing() )
		{
			SetFocusedTrap( null );
			return;
		}

		if ( !force && Time.NowDouble < _nextTrapFocusScanAt )
			return;

		_nextTrapFocusScanAt = Time.NowDouble + Math.Max( 0.05, TrapFocusScanIntervalSeconds );

		// Chest / station / workbench / campfire / door under the reticule keep their prompt.
		if ( FocusedContainer is not null || FocusedAugmentStation is not null || FocusedWorkbench is not null
		     || FocusedCampfire is not null || FocusedDoor is not null )
		{
			SetFocusedTrap( null );
			return;
		}

		var reach = FocusedTrap is { IsValid: true } ? FocusedTrap.UseReachMeters : 3f;
		if ( BearTrap.TryFindFocusedTrap( GameObject, reach, out var trap ) )
			SetFocusedTrap( trap );
		else
			SetFocusedTrap( null );
	}

	void SetFocusedTrap( BearTrap trap )
	{
		if ( ReferenceEquals( FocusedTrap, trap ) )
			return;

		FocusedTrap = trap;
		FocusedTrapChanged?.Invoke();
	}

	void OwnerUseTrap( BearTrap trap )
	{
		if ( trap is null || !trap.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			trap.HostUse( GameObject );
			return;
		}

		RpcHostUseTrap( trap.GameObject.Id );
	}

	[Rpc.Host]
	void RpcHostUseTrap( Guid trapRootId )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		var trap = Scene.Directory.FindByGuid( trapRootId )?.Components.Get<BearTrap>();
		if ( trap is null || !trap.IsValid() )
			return;

		// Client sent intent; the host validates reach once — no arming traps across the map.
		if ( !trap.IsWithinUseReach( GameObject ) )
			return;

		trap.HostUse( GameObject );
	}
}
