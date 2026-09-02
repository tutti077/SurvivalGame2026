using System;
using Sandbox;

namespace Survival;

/// <summary>Look + E on the Arena button opens the arena queue menu; queue intents go host-ward here.</summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Arena" ), Title( "Focus Scan Interval (seconds)" )]
	public float ArenaFocusScanIntervalSeconds { get; set; } = 0.15f;

	[Property, Group( "Arena" ), Title( "Focus Reach (m)" ), Range( 1f, 12f )]
	public float ArenaFocusReachMeters { get; set; } = 8f;

	public ArenaMenuButton FocusedArenaMenuButton { get; private set; }

	public bool IsArenaMenuOpen { get; private set; }

	public event Action FocusedArenaButtonChanged;
	public event Action ArenaMenuOpenChanged;

	double _nextArenaFocusScanAt;
	PlayerController _arenaMenuController;
	bool _arenaMenuSavedLook = true;

	void TickArenaAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		var pressed = !menuOpen && !IsTimeTrialMenuOpen
		              && (Input.Pressed( TimeTrialUseAction ) || Input.Pressed( "HandHarvest" ));

		TickArenaFocusPrompt( menuOpen, force: pressed );

		if ( IsArenaMenuOpen )
		{
			TickArenaMenuPointer();
			if ( Input.EscapePressed )
			{
				Input.EscapePressed = false;
				SetArenaMenuOpen( false );
			}

			return;
		}

		if ( !pressed )
			return;

		if ( IsBuildHammerPreviewing() || IsGrappleRetractActive() )
			return;

		if ( FocusedArenaMenuButton is null || !FocusedArenaMenuButton.IsValid() )
			return;

		if ( ResolveArenaSession() is null )
		{
			Log.Warning( "[Arena] E pressed but no ArenaSession in scene." );
			return;
		}

		SetArenaMenuOpen( true );
	}

	void TickArenaFocusPrompt( bool inventoryMenuOpen, bool force = false )
	{
		if ( FocusedArenaMenuButton is not null && !FocusedArenaMenuButton.IsValid() )
			SetFocusedArenaMenu( null );

		if ( inventoryMenuOpen || IsBuildHammerPreviewing() || IsArenaMenuOpen || IsTimeTrialMenuOpen )
		{
			if ( !IsArenaMenuOpen )
				SetFocusedArenaMenu( null );
			return;
		}

		if ( !force && Time.NowDouble < _nextArenaFocusScanAt )
			return;

		_nextArenaFocusScanAt = Time.NowDouble + Math.Max( 0.05, ArenaFocusScanIntervalSeconds );

		if ( FocusedContainer is not null || FocusedAugmentStation is not null )
		{
			SetFocusedArenaMenu( null );
			return;
		}

		if ( ArenaMenuButton.TryFindFocused( GameObject, ArenaFocusReachMeters, out var button ) )
			SetFocusedArenaMenu( button );
		else
			SetFocusedArenaMenu( null );
	}

	void SetFocusedArenaMenu( ArenaMenuButton button )
	{
		if ( ReferenceEquals( FocusedArenaMenuButton, button ) )
			return;

		FocusedArenaMenuButton = button;
		FocusedArenaButtonChanged?.Invoke();
	}

	public void SetArenaMenuOpen( bool open )
	{
		if ( IsArenaMenuOpen == open )
			return;

		IsArenaMenuOpen = open;
		if ( open )
			ApplyArenaMenuCapture();
		else
			RestoreArenaMenuCapture();

		ArenaMenuOpenChanged?.Invoke();
	}

	void TickArenaMenuPointer()
	{
		_arenaMenuController ??= Components.Get<PlayerController>();
		if ( _arenaMenuController is not null )
			_arenaMenuController.UseLookControls = false;

		if ( Mouse.Visibility != MouseVisibility.Auto )
			Mouse.Visibility = MouseVisibility.Auto;

		InventoryScreenPointer.ClampMouseToView( GameObject );

		if ( Input.Pressed( "Attack1" ) || Input.Down( "Attack1" ) )
			Input.SetAction( "Attack1", false );
	}

	void ApplyArenaMenuCapture()
	{
		_arenaMenuController ??= Components.Get<PlayerController>();
		if ( _arenaMenuController is not null )
		{
			_arenaMenuSavedLook = _arenaMenuController.UseLookControls;
			_arenaMenuController.UseLookControls = false;
		}

		Mouse.Visibility = MouseVisibility.Auto;
		InventoryScreenPointer.ClampMouseToView( GameObject );
	}

	void RestoreArenaMenuCapture()
	{
		if ( _menu is not null && _menu.IsMenuOpen )
			return;

		if ( _arenaMenuController is not null && _arenaMenuController.IsValid() )
			_arenaMenuController.UseLookControls = _arenaMenuSavedLook;

		Mouse.Visibility = MouseVisibility.Hidden;
	}

	public ArenaSession ResolveArenaSession()
	{
		if ( FocusedArenaMenuButton is { IsValid: true } button && button.Session is { IsValid: true } fromButton )
			return fromButton;
		return ArenaSession.Instance is { IsValid: true } instance ? instance : null;
	}

	public void OwnerArenaQueue( ArenaMode mode )
	{
		var session = ResolveArenaSession();
		if ( session is null )
		{
			Log.Warning( "[Arena] Queue failed — no ArenaSession." );
			return;
		}

		SendArenaIntent( session, (int)mode, cancel: false );
	}

	public void OwnerArenaCancelQueue()
	{
		var session = ResolveArenaSession();
		if ( session is null )
			return;

		SendArenaIntent( session, 0, cancel: true );
	}

	void SendArenaIntent( ArenaSession session, int modeInt, bool cancel )
	{
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			ApplyArenaIntentLocal( session, modeInt, cancel );
			return;
		}

		RpcHostArenaIntent( modeInt, cancel );
	}

	void ApplyArenaIntentLocal( ArenaSession session, int modeInt, bool cancel )
	{
		if ( cancel )
			session.HostTryCancelQueue( GameObject );
		else
			session.HostTryQueue( GameObject, (ArenaMode)modeInt );
	}

	[Rpc.Host]
	void RpcHostArenaIntent( int modeInt, bool cancel )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
		{
			Log.Warning( "[Arena] Host rejected intent — caller is not pawn owner." );
			return;
		}

		var session = ArenaSession.Instance;
		if ( session is null || !session.IsValid() )
		{
			Log.Warning( "[Arena] Host intent: no ArenaSession in scene." );
			return;
		}

		ApplyArenaIntentLocal( session, modeInt, cancel );
	}
}
