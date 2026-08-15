using System;
using Sandbox;

namespace Survival;

/// <summary>Look + E on the Time Trials button opens the race menu.</summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Time Trial" ), Title( "Menu Use Action" )]
	public string TimeTrialUseAction { get; set; } = "Use";

	[Property, Group( "Time Trial" ), Title( "Focus Scan Interval (seconds)" )]
	public float TimeTrialFocusScanIntervalSeconds { get; set; } = 0.15f;

	[Property, Group( "Time Trial" ), Title( "Focus Reach (m)" ), Range( 1f, 12f )]
	public float TimeTrialFocusReachMeters { get; set; } = 8f;

	[Property, Group( "Time Trial" ), Title( "Log Ready" )]
	public bool LogTimeTrialReady { get; set; }

	/// <summary>Session for the focused Time Trials menu button (via <see cref="TimeTrialSession.Instance"/>).</summary>
	public TimeTrialSession FocusedTimeTrialStand { get; private set; }

	public TimeTrialMenuButton FocusedTimeTrialMenuButton { get; private set; }

	public bool IsTimeTrialMenuOpen { get; private set; }

	public event Action FocusedTimeTrialStandChanged;
	public event Action TimeTrialMenuOpenChanged;

	double _nextTimeTrialFocusScanAt;
	PlayerController _timeTrialController;
	bool _timeTrialSavedLook = true;

	void TickTimeTrialAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		var pressed = !menuOpen
		              && (Input.Pressed( TimeTrialUseAction ) || Input.Pressed( "HandHarvest" ));

		TickTimeTrialFocusPrompt( menuOpen, force: pressed );

		if ( IsTimeTrialMenuOpen )
		{
			var session = ResolveTimeTrialSession();
			if ( session is null || !session.IsValid() || !session.CanOpenMenu )
			{
				SetTimeTrialMenuOpen( false );
				return;
			}

			TickTimeTrialMenuPointer();
			// Prefer EscapePressed (same as inventory menu) — "Escape" action is unreliable.
			if ( Input.EscapePressed )
			{
				Input.EscapePressed = false;
				SetTimeTrialMenuOpen( false );
			}

			return;
		}

		if ( !pressed )
			return;

		if ( IsBuildHammerPreviewing() || IsGrappleRetractActive() )
			return;

		if ( FocusedTimeTrialMenuButton is null || !FocusedTimeTrialMenuButton.IsValid() )
		{
			if ( LogTimeTrialReady )
				Log.Info( "[TimeTrial] E pressed but Time Trials button not in look/reach." );
			return;
		}

		var focusedSession = ResolveTimeTrialSession();
		if ( focusedSession is null || !focusedSession.IsValid() )
		{
			if ( LogTimeTrialReady )
				Log.Info( "[TimeTrial] E pressed but no TimeTrialSession in scene." );
			return;
		}

		FocusedTimeTrialStand = focusedSession;

		if ( !focusedSession.CanOpenMenu )
		{
			if ( LogTimeTrialReady )
				Log.Info( $"[TimeTrial] Busy — phase={focusedSession.Phase}." );
			return;
		}

		SetTimeTrialMenuOpen( true );
	}

	void TickTimeTrialFocusPrompt( bool inventoryMenuOpen, bool force = false )
	{
		if ( FocusedTimeTrialMenuButton is not null && !FocusedTimeTrialMenuButton.IsValid() )
			SetFocusedTimeTrialMenu( null, null );

		if ( inventoryMenuOpen || IsBuildHammerPreviewing() || IsTimeTrialMenuOpen )
		{
			if ( !IsTimeTrialMenuOpen )
				SetFocusedTimeTrialMenu( null, null );
			return;
		}

		if ( !force && Time.NowDouble < _nextTimeTrialFocusScanAt )
			return;

		_nextTimeTrialFocusScanAt = Time.NowDouble + Math.Max( 0.05, TimeTrialFocusScanIntervalSeconds );

		if ( FocusedContainer is not null || FocusedAugmentStation is not null )
		{
			SetFocusedTimeTrialMenu( null, null );
			return;
		}

		if ( TimeTrialMenuButton.TryFindFocused( GameObject, TimeTrialFocusReachMeters, out var button ) )
			SetFocusedTimeTrialMenu( button, ResolveTimeTrialSession( button ) );
		else
			SetFocusedTimeTrialMenu( null, null );
	}

	void SetFocusedTimeTrialMenu( TimeTrialMenuButton button, TimeTrialSession session )
	{
		if ( ReferenceEquals( FocusedTimeTrialMenuButton, button )
		     && ReferenceEquals( FocusedTimeTrialStand, session ) )
			return;

		FocusedTimeTrialMenuButton = button;
		FocusedTimeTrialStand = session;
		FocusedTimeTrialStandChanged?.Invoke();
	}

	public void SetTimeTrialMenuOpen( bool open )
	{
		if ( IsTimeTrialMenuOpen == open )
			return;

		IsTimeTrialMenuOpen = open;
		if ( open )
			ApplyTimeTrialMenuCapture();
		else
			RestoreTimeTrialMenuCapture();

		TimeTrialMenuOpenChanged?.Invoke();
	}

	void TickTimeTrialMenuPointer()
	{
		_timeTrialController ??= Components.Get<PlayerController>();
		if ( _timeTrialController is not null )
			_timeTrialController.UseLookControls = false;

		if ( Mouse.Visibility != MouseVisibility.Auto )
			Mouse.Visibility = MouseVisibility.Auto;

		InventoryScreenPointer.ClampMouseToView( GameObject );

		// Don't let Attack1 fire combat/build while clicking menu chips.
		if ( Input.Pressed( "Attack1" ) || Input.Down( "Attack1" ) )
			Input.SetAction( "Attack1", false );
	}

	void ApplyTimeTrialMenuCapture()
	{
		_timeTrialController ??= Components.Get<PlayerController>();
		if ( _timeTrialController is not null )
		{
			_timeTrialSavedLook = _timeTrialController.UseLookControls;
			_timeTrialController.UseLookControls = false;
		}

		Mouse.Visibility = MouseVisibility.Auto;
		InventoryScreenPointer.ClampMouseToView( GameObject );
	}

	void RestoreTimeTrialMenuCapture()
	{
		if ( _menu is not null && _menu.IsMenuOpen )
			return;

		if ( _timeTrialController is not null && _timeTrialController.IsValid() )
			_timeTrialController.UseLookControls = _timeTrialSavedLook;

		Mouse.Visibility = MouseVisibility.Hidden;
	}

	TimeTrialSession ResolveTimeTrialSession( TimeTrialMenuButton button = null )
	{
		if ( button?.Session is { IsValid: true } fromButton )
			return fromButton;
		if ( FocusedTimeTrialStand is { IsValid: true } focused )
			return focused;
		if ( TimeTrialSession.Instance is { IsValid: true } instance )
			return instance;

		foreach ( var session in Scene.GetAllComponents<TimeTrialSession>() )
		{
			if ( session is not null && session.IsValid() )
				return session;
		}

		return null;
	}

	public void OwnerMenuStart( TimeTrialMode mode, string variationId )
	{
		var session = ResolveTimeTrialSession();
		if ( session is null || !session.IsValid() )
		{
			Log.Warning( "[TimeTrial] Start failed — no TimeTrialSession." );
			return;
		}

		// Keep menu open for 1v1 queue so host/client can Leave; solo closes into countdown.
		if ( mode == TimeTrialMode.Solo )
			SetTimeTrialMenuOpen( false );

		SendTimeTrialIntent( session, mode, variationId, join: false, leave: false );
	}

	public void OwnerMenuJoin()
	{
		var session = ResolveTimeTrialSession();
		if ( session is null || !session.IsValid() )
		{
			Log.Warning( "[TimeTrial] Join failed — no TimeTrialSession." );
			return;
		}

		SetTimeTrialMenuOpen( false );
		SendTimeTrialIntent( session, TimeTrialMode.TwoPlayer, session.ActiveVariationId, join: true, leave: false );
	}

	public void OwnerMenuLeave()
	{
		var session = ResolveTimeTrialSession();
		if ( session is null || !session.IsValid() )
			return;

		SendTimeTrialIntent( session, TimeTrialMode.TwoPlayer, "", join: false, leave: true );
	}

	void SendTimeTrialIntent( TimeTrialSession session, TimeTrialMode mode, string variationId, bool join, bool leave )
	{
		if ( GameObject.Network is not { Active: true } )
		{
			ApplyTimeTrialIntentLocal( session, mode, variationId, join, leave );
			return;
		}

		if ( Networking.IsHost )
			ApplyTimeTrialIntentLocal( session, mode, variationId, join, leave );
		else
			RpcHostTimeTrialIntent( session.GameObject.Id, (int)mode, variationId ?? "", join, leave );
	}

	void ApplyTimeTrialIntentLocal( TimeTrialSession session, TimeTrialMode mode, string variationId, bool join, bool leave )
	{
		if ( leave )
			session.HostTryLeave( GameObject );
		else if ( join )
			session.HostTryJoin( GameObject );
		else
			session.HostTryStart( GameObject, mode, variationId );
	}

	[Rpc.Host]
	void RpcHostTimeTrialIntent( Guid sessionRootId, int modeInt, string variationId, bool join, bool leave )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
		{
			Log.Warning( "[TimeTrial] Host rejected intent — caller is not pawn owner." );
			return;
		}

		var session = FindSession( sessionRootId );
		if ( session is null || !session.IsValid() )
		{
			Log.Warning( $"[TimeTrial] Host intent: session {sessionRootId} not found." );
			return;
		}

		if ( LogTimeTrialReady )
			Log.Info( $"[TimeTrial] Host intent from {GameObject.Name}: join={join} leave={leave} mode={(TimeTrialMode)modeInt} var={variationId}" );

		ApplyTimeTrialIntentLocal( session, (TimeTrialMode)modeInt, variationId, join, leave );
	}

	TimeTrialSession FindSession( Guid sessionRootId )
	{
		foreach ( var session in Scene.GetAllComponents<TimeTrialSession>() )
		{
			if ( session is null || !session.GameObject.IsValid() )
				continue;
			if ( session.GameObject.Id == sessionRootId )
				return session;
		}

		// Fallback — scene guids can disagree; there should only be one session.
		return ResolveTimeTrialSession();
	}
}
