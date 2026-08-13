using System;
using Sandbox;

namespace Survival;

/// <summary>Look-at campfire: E adds wood fuel from inventory.</summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Campfire" ), Title( "Add Fuel Action" )]
	public string CampfireUseAction { get; set; } = "Use";

	[Property, Group( "Campfire" ), Title( "Focus Scan Interval (seconds)" )]
	public float CampfireFocusScanIntervalSeconds { get; set; } = 0.2f;

	public Campfire FocusedCampfire { get; private set; }

	public event Action FocusedCampfireChanged;

	double _nextCampfireFocusScanAt;

	void TickCampfireAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		TickCampfireFocusPrompt( menuOpen );

		if ( menuOpen || !Input.Pressed( CampfireUseAction ) )
			return;

		if ( IsBuildHammerPreviewing() || IsGrappleRetractActive() )
			return;

		if ( FocusedCampfire is null || !FocusedCampfire.IsValid() )
			return;

		OwnerTryAddCampfireFuel( FocusedCampfire );
	}

	void TickCampfireFocusPrompt( bool menuOpen )
	{
		if ( FocusedCampfire is not null && !FocusedCampfire.IsValid() )
			SetFocusedCampfire( null );

		if ( menuOpen || IsBuildHammerPreviewing() )
		{
			SetFocusedCampfire( null );
			return;
		}

		if ( Time.NowDouble < _nextCampfireFocusScanAt )
			return;

		_nextCampfireFocusScanAt = Time.NowDouble + Math.Max( 0.05, CampfireFocusScanIntervalSeconds );

		// Don't steal the prompt from an openable chest / augment station under the reticule.
		if ( FocusedContainer is not null || FocusedAugmentStation is not null )
		{
			SetFocusedCampfire( null );
			return;
		}

		if ( Campfire.TryFindFocusedCampfire( GameObject, 3f, out var fire ) )
			SetFocusedCampfire( fire );
		else
			SetFocusedCampfire( null );
	}

	void SetFocusedCampfire( Campfire fire )
	{
		if ( ReferenceEquals( FocusedCampfire, fire ) )
			return;

		FocusedCampfire = fire;
		FocusedCampfireChanged?.Invoke();
	}

	void OwnerTryAddCampfireFuel( Campfire fire )
	{
		if ( fire is null || !fire.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } )
		{
			fire.HostTryAddFuelFrom( _inventory );
			return;
		}

		if ( Networking.IsHost )
			fire.HostTryAddFuelFrom( _inventory );
		else
			RpcHostAddCampfireFuel( fire.GameObject.Id );
	}

	[Rpc.Host]
	void RpcHostAddCampfireFuel( Guid campfireRootId )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		foreach ( var fire in Campfire.All )
		{
			if ( fire is null || !fire.GameObject.IsValid() || fire.GameObject.Id != campfireRootId )
				continue;

			fire.HostTryAddFuelFrom( Components.Get<PlayerInventory>() );
			return;
		}
	}
}
