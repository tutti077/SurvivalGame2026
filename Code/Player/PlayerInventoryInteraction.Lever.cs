using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Look at a placed <see cref="CircuitLever"/> + E flips it. Owner traces and sends intent; the
/// host re-checks reach once and commits through <see cref="CircuitLever.HostToggle"/>.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Lever" ), Title( "Use Action" )]
	public string LeverUseAction { get; set; } = "Use";

	[Property, Group( "Lever" ), Title( "Focus Scan Interval (seconds)" )]
	public float LeverFocusScanIntervalSeconds { get; set; } = 0.15f;

	/// <summary>Lever under the crosshair (drives the "E — Switch On / Switch Off" HUD prompt).</summary>
	public CircuitLever FocusedLever { get; private set; }

	public event Action FocusedLeverChanged;

	double _nextLeverFocusScanAt;

	void TickLeverAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		var pressed = !menuOpen && Input.Pressed( LeverUseAction );
		TickLeverFocusPrompt( menuOpen, force: pressed );

		if ( !pressed )
			return;

		if ( IsBuildHammerPreviewing() || IsGrappleRetractActive() )
			return;

		if ( FocusedLever is null || !FocusedLever.IsValid() )
			return;

		OwnerToggleLever( FocusedLever );
	}

	void TickLeverFocusPrompt( bool menuOpen, bool force = false )
	{
		if ( FocusedLever is not null && !FocusedLever.IsValid() )
			SetFocusedLever( null );

		if ( menuOpen || IsBuildHammerPreviewing() )
		{
			SetFocusedLever( null );
			return;
		}

		if ( !force && Time.NowDouble < _nextLeverFocusScanAt )
			return;

		_nextLeverFocusScanAt = Time.NowDouble + Math.Max( 0.05, LeverFocusScanIntervalSeconds );

		// Chest / station / workbench / campfire / door / trap under the reticule keep their prompt.
		if ( FocusedContainer is not null || FocusedAugmentStation is not null || FocusedWorkbench is not null
		     || FocusedCampfire is not null || FocusedDoor is not null || FocusedTrap is not null )
		{
			SetFocusedLever( null );
			return;
		}

		var reach = FocusedLever is { IsValid: true } ? FocusedLever.UseReachMeters : 3f;
		if ( CircuitLever.TryFindFocusedLever( GameObject, reach, out var lever ) )
			SetFocusedLever( lever );
		else
			SetFocusedLever( null );
	}

	void SetFocusedLever( CircuitLever lever )
	{
		if ( ReferenceEquals( FocusedLever, lever ) )
			return;

		FocusedLever = lever;
		FocusedLeverChanged?.Invoke();
	}

	void OwnerToggleLever( CircuitLever lever )
	{
		if ( lever is null || !lever.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			lever.HostToggle( GameObject );
			return;
		}

		RpcHostToggleLever( lever.GameObject.Id );
	}

	[Rpc.Host]
	void RpcHostToggleLever( Guid leverRootId )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		var lever = Scene.Directory.FindByGuid( leverRootId )?.Components.Get<CircuitLever>();
		if ( lever is null || !lever.IsValid() )
			return;

		// Client sent intent; the host validates reach once — no flipping levers across the map.
		if ( !lever.IsWithinUseReach( GameObject ) )
			return;

		lever.HostToggle( GameObject );
	}
}
