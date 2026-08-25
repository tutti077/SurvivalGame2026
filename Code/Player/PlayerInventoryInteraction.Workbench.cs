using System;
using Sandbox;

namespace Survival;

/// <summary>
/// World workbench: Use-key look trace opens the crafting page with the workbench recipe set
/// and the tool-repair button (same cadence as chest / augment station open).
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	/// <summary>Workbench under the crosshair (drives the "E — Open" HUD prompt).</summary>
	public Workbench FocusedWorkbench { get; private set; }

	public event Action FocusedWorkbenchChanged;

	/// <summary>Currently opened workbench; null when none.</summary>
	public Workbench OpenWorkbench { get; private set; }

	public event Action WorkbenchChanged;

	double _nextWorkbenchFocusScanAt;

	void TickWorkbenchAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		TickWorkbenchFocusPrompt( menuOpen );

		if ( OpenWorkbench is not null )
		{
			if ( !OpenWorkbench.IsValid() )
			{
				CloseWorkbench();
				return;
			}

			if ( !menuOpen )
			{
				CloseWorkbench();
				return;
			}

			if ( !IsWorkbenchWithinKeepOpenRange() || Input.Pressed( ContainerUseAction ) )
				_menu.SetMenuOpen( false );

			return;
		}

		if ( menuOpen || !Input.Pressed( ContainerUseAction ) )
			return;

		if ( IsBuildHammerPreviewing() )
			return;

		if ( IsGrappleRetractActive() )
			return;

		// Chest / augment station win when the look hit lands on one of those instead.
		if ( TryTraceOpenableContainer( out _ ) || TryTraceAugmentStation( out _ ) )
			return;

		if ( !TryTraceWorkbench( out var workbench ) )
			return;

		OpenWorkbenchView( workbench );
	}

	void TickWorkbenchFocusPrompt( bool menuOpen )
	{
		if ( FocusedWorkbench is not null && !FocusedWorkbench.IsValid() )
			SetFocusedWorkbench( null );

		if ( menuOpen || OpenWorkbench is not null || OpenContainer is not null || OpenAugmentStation is not null
		     || IsGrappleRetractActive() || IsBuildHammerPreviewing() )
		{
			SetFocusedWorkbench( null );
			return;
		}

		if ( Time.NowDouble < _nextWorkbenchFocusScanAt )
			return;

		_nextWorkbenchFocusScanAt = Time.NowDouble + Math.Max( 0.05, ContainerFocusScanIntervalSeconds );

		if ( TryTraceOpenableContainer( out _ ) || TryTraceAugmentStation( out _ ) )
		{
			SetFocusedWorkbench( null );
			return;
		}

		SetFocusedWorkbench( TryTraceWorkbench( out var workbench ) ? workbench : null );
	}

	void SetFocusedWorkbench( Workbench workbench )
	{
		if ( ReferenceEquals( FocusedWorkbench, workbench ) )
			return;

		FocusedWorkbench = workbench;
		FocusedWorkbenchChanged?.Invoke();
	}

	public void OpenWorkbenchView( Workbench workbench )
	{
		if ( workbench is null || !workbench.IsValid() )
			return;

		if ( ReferenceEquals( OpenWorkbench, workbench ) )
			return;

		CloseContainer();
		CloseAugmentStation();
		OpenWorkbench = workbench;
		WorkbenchChanged?.Invoke();

		if ( _menu is null )
			_menu = Components.Get<PlayerGameMenuController>();

		_menu?.OpenCraftingPage();
	}

	public void CloseWorkbench()
	{
		if ( OpenWorkbench is null )
			return;

		OpenWorkbench = null;
		WorkbenchChanged?.Invoke();
	}

	bool IsWorkbenchWithinKeepOpenRange()
	{
		var workbench = OpenWorkbench;
		if ( workbench is null || !workbench.IsValid() )
			return false;

		var maxRange = TerrainWorldUnits.MetersToEngine( Math.Max( 1f, ContainerKeepOpenMeters ) );
		return Vector3.DistanceBetween( GameObject.WorldPosition, workbench.GameObject.WorldPosition ) <= maxRange;
	}

	bool TryTraceWorkbench( out Workbench workbench )
	{
		workbench = null;

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

		return Workbench.TryFindOnHierarchy( tr.GameObject, out workbench );
	}
}
