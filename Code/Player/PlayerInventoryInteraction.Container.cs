using System;
using Sandbox;

namespace Survival;

/// <summary>
/// World-container access (chest, etc.): Use-key look trace opens the container view in the
/// game menu; walking away or closing the menu closes it. The opened container is exposed to
/// the menu grids through a single reusable <see cref="ContainerInventoryGridHost"/>.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Container" ), Title( "Open Container Action" )]
	public string ContainerUseAction { get; set; } = "Use";

	[Property, Group( "Container" ), Title( "Open Reach (meters)" )]
	public float ContainerReachMeters { get; set; } = 3f;

	[Property, Group( "Container" ), Title( "Keep-Open Range (meters)" )]
	public float ContainerKeepOpenMeters { get; set; } = 4f;

	[Property, Group( "Container" ), Title( "Focus Scan Interval (seconds)" )]
	public float ContainerFocusScanIntervalSeconds { get; set; } = 0.2f;

	/// <summary>Fired on the local client when a container is opened or closed.</summary>
	public event Action ContainerChanged;

	/// <summary>Openable container under the crosshair (drives the "E — Open" HUD prompt).</summary>
	public ContainerInventory FocusedContainer { get; private set; }

	/// <summary>Fires when <see cref="FocusedContainer"/> reference changes (including to null).</summary>
	public event Action FocusedContainerChanged;

	/// <summary>Currently opened world container; null when none.</summary>
	public ContainerInventory OpenContainer =>
		_containerGrid is { IsActive: true } ? _containerGrid.Container : null;

	/// <summary>Shared grid host the container menu section renders from.</summary>
	public ContainerInventoryGridHost ContainerGrid => _containerGrid;

	ContainerInventoryGridHost _containerGrid;
	PlayerController _containerController;
	PlayerMovement _containerMovement;
	double _nextContainerFocusScanAt;

	void InitializeContainerGrid()
	{
		_containerGrid = new ContainerInventoryGridHost();
		_grids.Add( _containerGrid );
	}

	void TickContainerAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		TickContainerFocusPrompt( menuOpen );

		if ( _containerGrid?.Container is not null )
		{
			if ( !_containerGrid.IsActive )
			{
				CloseContainer();
				return;
			}

			if ( !menuOpen )
			{
				CloseContainer();
				return;
			}

			// Use toggles: pressing E again (or walking away) closes the chest with the menu.
			if ( !IsContainerWithinKeepOpenRange() || Input.Pressed( ContainerUseAction ) )
				_menu.SetMenuOpen( false ); // cascades to CloseContainer via OnMenuOpenChanged

			return;
		}

		if ( menuOpen || !Input.Pressed( ContainerUseAction ) )
			return;

		// E doubles as grapple retract while attached — don't pop chests mid-swing.
		if ( IsGrappleRetractActive() )
			return;

		if ( !TryTraceOpenableContainer( out var container ) )
			return;

		OpenContainerView( container );
	}

	/// <summary>Look-trace scan (same cadence pattern as the harvest focus) feeding the HUD prompt.</summary>
	void TickContainerFocusPrompt( bool menuOpen )
	{
		if ( FocusedContainer is not null && !FocusedContainer.IsValid() )
			SetFocusedContainer( null );

		// No prompt while a menu/container is open, or while E means "retract grapple".
		if ( menuOpen || _containerGrid?.Container is not null || IsGrappleRetractActive() )
		{
			SetFocusedContainer( null );
			return;
		}

		if ( Time.NowDouble < _nextContainerFocusScanAt )
			return;

		_nextContainerFocusScanAt = Time.NowDouble + Math.Max( 0.05, ContainerFocusScanIntervalSeconds );
		SetFocusedContainer( TryTraceOpenableContainer( out var container ) ? container : null );
	}

	void SetFocusedContainer( ContainerInventory container )
	{
		if ( ReferenceEquals( FocusedContainer, container ) )
			return;

		FocusedContainer = container;
		FocusedContainerChanged?.Invoke();
	}

	public void OpenContainerView( ContainerInventory container )
	{
		if ( container is null || !container.IsValid() || _containerGrid is null )
			return;

		if ( ReferenceEquals( _containerGrid.Container, container ) )
			return;

		_containerGrid.Container = container;
		ContainerChanged?.Invoke();

		if ( _menu is null )
			_menu = Components.Get<PlayerGameMenuController>();

		_menu?.OpenInventoryPage();
	}

	public void CloseContainer()
	{
		if ( _containerGrid?.Container is null )
			return;

		_containerGrid.Container = null;
		ContainerChanged?.Invoke();
	}

	bool IsContainerWithinKeepOpenRange()
	{
		var container = _containerGrid?.Container;
		if ( container is null || !container.IsValid() )
			return false;

		var maxRange = TerrainWorldUnits.MetersToEngine( Math.Max( 1f, ContainerKeepOpenMeters ) );
		return Vector3.DistanceBetween( GameObject.WorldPosition, container.GameObject.WorldPosition ) <= maxRange;
	}

	bool TryTraceOpenableContainer( out ContainerInventory container )
	{
		container = null;

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

		return ContainerInventory.TryFindOnHierarchy( tr.GameObject, out container );
	}

	Vector3 ResolveContainerEyePosition()
	{
		_containerController ??= Components.Get<PlayerController>();
		if ( _containerController is not null )
		{
			return GameObject.WorldPosition
			       + Vector3.Up * Math.Max( 8f, _containerController.BodyHeight - _containerController.EyeDistanceFromTop );
		}

		return GameObject.WorldPosition + Vector3.Up * 64f;
	}

	bool IsGrappleRetractActive()
	{
		_containerMovement ??= Components.Get<PlayerMovement>();
		return _containerMovement is not null && _containerMovement.GrappleAttached;
	}
}
