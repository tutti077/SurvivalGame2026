using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Look-at drying rack: E collects what is ready and loads free slots straight from the inventory
/// (<see cref="DryingRack.HostUse"/>). No menu. Owner sends intent; the host checks range once.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	/// <summary>Drying rack under the crosshair (drives the "E — Load / Collect" HUD prompt).</summary>
	public DryingRack FocusedDryingRack { get; private set; }

	public event Action FocusedDryingRackChanged;

	double _nextDryingRackFocusScanAt;

	void TickDryingRackAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		TickDryingRackFocusPrompt( menuOpen );

		if ( menuOpen || !Input.Pressed( ContainerUseAction ) )
			return;

		if ( IsBuildHammerPreviewing() || IsGrappleRetractActive() )
			return;

		if ( FocusedDryingRack is null || !FocusedDryingRack.IsValid() )
			return;

		OwnerTryUseDryingRack( FocusedDryingRack );
	}

	void TickDryingRackFocusPrompt( bool menuOpen )
	{
		if ( FocusedDryingRack is not null && !FocusedDryingRack.IsValid() )
			SetFocusedDryingRack( null );

		if ( menuOpen || IsBuildHammerPreviewing() || IsGrappleRetractActive() )
		{
			SetFocusedDryingRack( null );
			return;
		}

		if ( Time.NowDouble < _nextDryingRackFocusScanAt )
			return;

		_nextDryingRackFocusScanAt = Time.NowDouble + Math.Max( 0.05, ContainerFocusScanIntervalSeconds );

		// Chest / augment station / workbench / campfire keep the prompt when they are what is under the reticule.
		if ( FocusedContainer is not null || FocusedAugmentStation is not null || FocusedWorkbench is not null || FocusedCampfire is not null )
		{
			SetFocusedDryingRack( null );
			return;
		}

		SetFocusedDryingRack( TryTraceDryingRack( out var rack ) ? rack : null );
	}

	void SetFocusedDryingRack( DryingRack rack )
	{
		if ( ReferenceEquals( FocusedDryingRack, rack ) )
			return;

		FocusedDryingRack = rack;
		FocusedDryingRackChanged?.Invoke();
	}

	bool TryTraceDryingRack( out DryingRack rack )
	{
		rack = null;

		var eye = ResolveContainerEyePosition();
		var direction = GameObject.WorldRotation.Forward;
		var cam = BuildViewCamera.Resolve( GameObject );
		if ( cam.IsValid() )
			direction = cam.WorldRotation.Forward.Normal;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, ContainerReachMeters ) );
		var tr = Scene.Trace.Ray( eye, eye + direction * reach )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.Run();

		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
			return false;

		return DryingRack.TryFindOnHierarchy( tr.GameObject, out rack );
	}

	void OwnerTryUseDryingRack( DryingRack rack )
	{
		if ( rack is null || !rack.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			if ( rack.IsWithinUseRange( GameObject ) )
				rack.HostUse( _inventory );
			return;
		}

		RpcHostUseDryingRack( rack.GameObject.Id );
	}

	[Rpc.Host]
	void RpcHostUseDryingRack( Guid rackRootId )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		foreach ( var rack in scene.GetAllComponents<DryingRack>() )
		{
			if ( rack is null || !rack.GameObject.IsValid() || rack.GameObject.Id != rackRootId )
				continue;

			if ( !rack.IsBuilt || !rack.IsWithinUseRange( GameObject ) )
				return;

			rack.HostUse( Components.Get<PlayerInventory>() );
			return;
		}
	}
}
