using System;
using Sandbox;

namespace Survival;

/// <summary>
/// World augment station: Use-key look trace opens the full-screen augment menu
/// (same cadence as chest open).
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	/// <summary>Augment bench under the crosshair (drives the "E — Open" HUD prompt).</summary>
	public AugmentStation FocusedAugmentStation { get; private set; }

	public event Action FocusedAugmentStationChanged;

	/// <summary>Currently opened station; null when none.</summary>
	public AugmentStation OpenAugmentStation { get; private set; }

	public event Action AugmentStationChanged;

	double _nextAugmentFocusScanAt;

	void TickAugmentStationAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		TickAugmentStationFocusPrompt( menuOpen );

		if ( OpenAugmentStation is not null )
		{
			if ( !OpenAugmentStation.IsValid() )
			{
				CloseAugmentStation();
				return;
			}

			if ( !menuOpen )
			{
				CloseAugmentStation();
				return;
			}

			if ( !IsAugmentStationWithinKeepOpenRange() || Input.Pressed( ContainerUseAction ) )
				_menu.SetMenuOpen( false );

			return;
		}

		if ( menuOpen || !Input.Pressed( ContainerUseAction ) )
			return;

		if ( IsGrappleRetractActive() )
			return;

		// Chest wins if both somehow share a look hit — prefer container when traced first.
		if ( TryTraceOpenableContainer( out _ ) )
			return;

		if ( !TryTraceAugmentStation( out var station ) )
			return;

		OpenAugmentStationView( station );
	}

	void TickAugmentStationFocusPrompt( bool menuOpen )
	{
		if ( FocusedAugmentStation is not null && !FocusedAugmentStation.IsValid() )
			SetFocusedAugmentStation( null );

		if ( menuOpen || OpenAugmentStation is not null || OpenContainer is not null || IsGrappleRetractActive() )
		{
			SetFocusedAugmentStation( null );
			return;
		}

		if ( Time.NowDouble < _nextAugmentFocusScanAt )
			return;

		_nextAugmentFocusScanAt = Time.NowDouble + Math.Max( 0.05, ContainerFocusScanIntervalSeconds );

		// Don't steal the chest prompt when a container is under the crosshair.
		if ( TryTraceOpenableContainer( out _ ) )
		{
			SetFocusedAugmentStation( null );
			return;
		}

		SetFocusedAugmentStation( TryTraceAugmentStation( out var station ) ? station : null );
	}

	void SetFocusedAugmentStation( AugmentStation station )
	{
		if ( ReferenceEquals( FocusedAugmentStation, station ) )
			return;

		FocusedAugmentStation = station;
		FocusedAugmentStationChanged?.Invoke();
	}

	public void OpenAugmentStationView( AugmentStation station )
	{
		if ( station is null || !station.IsValid() )
			return;

		if ( ReferenceEquals( OpenAugmentStation, station ) )
			return;

		CloseContainer();
		OpenAugmentStation = station;
		AugmentStationChanged?.Invoke();

		if ( _menu is null )
			_menu = Components.Get<PlayerGameMenuController>();

		_menu?.OpenAugmentStationPage();
	}

	public void CloseAugmentStation()
	{
		if ( OpenAugmentStation is null )
			return;

		OpenAugmentStation = null;
		AugmentStationChanged?.Invoke();
	}

	bool IsAugmentStationWithinKeepOpenRange()
	{
		var station = OpenAugmentStation;
		if ( station is null || !station.IsValid() )
			return false;

		var maxRange = TerrainWorldUnits.MetersToEngine( Math.Max( 1f, ContainerKeepOpenMeters ) );
		return Vector3.DistanceBetween( GameObject.WorldPosition, station.GameObject.WorldPosition ) <= maxRange;
	}

	bool TryTraceAugmentStation( out AugmentStation station )
	{
		station = null;

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

		return AugmentStation.TryFindOnHierarchy( tr.GameObject, out station );
	}
}
