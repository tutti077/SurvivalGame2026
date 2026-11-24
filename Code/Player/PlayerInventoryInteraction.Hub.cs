using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Hub access for the local pawn: walk into a <see cref="HubPortal"/> and the host moves you; look + E on a
/// <see cref="HubKiosk"/> (bet NPC, stratagem cabinet) opens <see cref="HubMenuPanel"/>. Bets and stratagem scores are
/// intents to the host, which validates them once against <see cref="HubStore"/>.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	/// <summary>This player's hub token balance (host → all; refreshed when they enter the hub, bet or get paid out).</summary>
	[Sync( SyncFlags.FromHost )] public int HubTokens { get; private set; }

	/// <summary>This player's best stratagem score (host → all).</summary>
	[Sync( SyncFlags.FromHost )] public int HubStratagemBest { get; private set; }

	public HubKiosk FocusedHubKiosk { get; private set; }
	public bool IsHubMenuOpen { get; private set; }

	public event Action FocusedHubKioskChanged;
	public event Action HubMenuOpenChanged;

	double _nextHubScanAt;
	double _hubPortalCooldownUntil;
	PlayerController _hubController;
	bool _hubSavedLook = true;
	bool _hubSavedInput = true;

	void TickHubAccess()
	{
		var now = Time.NowDouble;
		var gameMenuOpen = _menu is not null && _menu.IsMenuOpen;

		if ( IsHubMenuOpen )
		{
			if ( FocusedHubKiosk is not { IsValid: true } || gameMenuOpen )
			{
				SetHubMenuOpen( false );
				return;
			}

			TickHubMenuCapture();
			if ( Input.EscapePressed )
			{
				Input.EscapePressed = false;
				SetHubMenuOpen( false );
			}

			return;
		}

		if ( now < _nextHubScanAt )
			return;
		_nextHubScanAt = now + 0.1;

		// Portals: a short static list, the local pawn only; the host re-checks the distance once.
		if ( now >= _hubPortalCooldownUntil )
		{
			foreach ( var portal in HubPortal.All )
			{
				if ( portal is null || !portal.IsValid() || !portal.Contains( GameObject ) )
					continue;

				_hubPortalCooldownUntil = now + 2.0;
				if ( GameObject.Network is { Active: true } && !Networking.IsHost )
					RpcHostUsePortal( portal.GameObject.Id );
				else
					HostUsePortal( portal );
				break;
			}
		}

		// Kiosks: only the hub has them, so the scan is a no-op everywhere else.
		HubKiosk focused = null;
		if ( !gameMenuOpen && !IsTimeTrialMenuOpen && !IsArenaMenuOpen && !IsBuildHammerPreviewing() )
		{
			var pc = Components.Get<PlayerController>();
			var eye = WorldPosition + Vector3.Up * (pc is not null ? Math.Max( 48f, pc.BodyHeight * 0.9f ) : 64f);
			var cam = BuildViewCamera.Resolve( GameObject );
			var dir = cam.IsValid() ? cam.WorldRotation.Forward : WorldRotation.Forward;
			focused = HubKiosk.FindFocused( eye, dir.Normal );
		}

		if ( !ReferenceEquals( focused, FocusedHubKiosk ) )
		{
			FocusedHubKiosk = focused;
			FocusedHubKioskChanged?.Invoke();
		}

		if ( focused is null || !(Input.Pressed( "Use" ) || Input.Pressed( "HandHarvest" )) )
			return;

		if ( focused.Kind == HubKioskKind.Commend )
			OwnerCommendCameraman( focused );
		else
			SetHubMenuOpen( true );
	}

	public void SetHubMenuOpen( bool open )
	{
		if ( IsHubMenuOpen == open )
			return;

		IsHubMenuOpen = open;
		_hubController ??= Components.Get<PlayerController>();
		if ( open )
		{
			if ( _hubController is not null )
			{
				_hubSavedLook = _hubController.UseLookControls;
				_hubSavedInput = _hubController.UseInputControls;
				_hubController.UseLookControls = false;
				// The stratagem game reads WASD as arrows; standing still while at a kiosk is fine for both kinds.
				_hubController.UseInputControls = false;
			}

			Mouse.Visibility = MouseVisibility.Auto;
			InventoryScreenPointer.ClampMouseToView( GameObject );
		}
		else
		{
			if ( _hubController is not null && _hubController.IsValid() )
			{
				_hubController.UseLookControls = _hubSavedLook;
				_hubController.UseInputControls = _hubSavedInput;
			}

			if ( _menu is null || !_menu.IsMenuOpen )
				Mouse.Visibility = MouseVisibility.Hidden;
		}

		HubMenuOpenChanged?.Invoke();
	}

	void TickHubMenuCapture()
	{
		if ( _hubController is not null )
		{
			_hubController.UseLookControls = false;
			_hubController.UseInputControls = false;
		}

		if ( Mouse.Visibility != MouseVisibility.Auto )
			Mouse.Visibility = MouseVisibility.Auto;
		InventoryScreenPointer.ClampMouseToView( GameObject );

		if ( Input.Pressed( "Attack1" ) || Input.Down( "Attack1" ) )
			Input.SetAction( "Attack1", false );
	}

	// ── Portals ─────────────────────────────────────────────────────────────────

	[Rpc.Host]
	void RpcHostUsePortal( Guid portalId )
	{
		if ( !IsHubRpcFromOwner() )
			return;
		foreach ( var portal in HubPortal.All )
		{
			if ( portal is not null && portal.IsValid() && portal.GameObject.Id == portalId )
			{
				HostUsePortal( portal );
				return;
			}
		}
	}

	void HostUsePortal( HubPortal portal )
	{
		// The client saw itself inside; allow a metre for the proxy lagging behind the owner.
		if ( !portal.Contains( GameObject, padUnits: TerrainWorldUnits.MetersToEngine( 1f ) ) )
			return;
		portal.HostTravel( GameObject );
	}

	// ── Records / tokens ────────────────────────────────────────────────────────

	/// <summary>Host: reload this player's hub record into the synced fields (and grant today's tokens on entry).</summary>
	public void HostRefreshHubRecord( bool grantDailyAllowance = false )
	{
		var record = HubStore.Get( HubIdentity.SteamKey( GameObject ) );
		record.Name = HubIdentity.DisplayName( GameObject );
		if ( grantDailyAllowance && HubStore.TryGrantDailyAllowance( record ) )
			Log.Info( $"[Hub] {record.Name} got today's {HubStore.DailyTokenAllowance} tokens." );

		HubTokens = record.Tokens;
		HubStratagemBest = record.StratagemBest;
	}

	/// <summary>Owner: ask the host for fresh numbers (menu open).</summary>
	public void OwnerRequestHubRefresh()
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			RpcHostRefreshHub();
		else
			HostRefreshHubRecord();
	}

	[Rpc.Host]
	void RpcHostRefreshHub()
	{
		if ( IsHubRpcFromOwner() )
			HostRefreshHubRecord();
	}

	public void OwnerPlaceBet( HubBetBoard board, int team, int amount )
	{
		if ( board is null || !board.IsValid() )
			return;
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			RpcHostPlaceBet( board.GameObject.Id, team, amount );
		else
			board.HostPlaceBet( GameObject, team, amount );
	}

	[Rpc.Host]
	void RpcHostPlaceBet( Guid boardId, int team, int amount )
	{
		if ( !IsHubRpcFromOwner() )
			return;
		foreach ( var board in Scene.GetAllComponents<HubBetBoard>() )
		{
			if ( board.GameObject.Id == boardId )
			{
				board.HostPlaceBet( GameObject, team, amount );
				return;
			}
		}
	}

	public void OwnerSubmitStratagemScore( StratagemMachine machine, int score, int codes, float seconds )
	{
		if ( machine is null || !machine.IsValid() )
			return;
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			RpcHostSubmitStratagem( machine.GameObject.Id, score, codes, seconds );
		else
			machine.HostSubmitScore( GameObject, score, codes, seconds );
	}

	[Rpc.Host]
	void RpcHostSubmitStratagem( Guid machineId, int score, int codes, float seconds )
	{
		if ( !IsHubRpcFromOwner() )
			return;
		foreach ( var machine in Scene.GetAllComponents<StratagemMachine>() )
		{
			if ( machine.GameObject.Id == machineId )
			{
				machine.HostSubmitScore( GameObject, score, codes, seconds );
				return;
			}
		}
	}

	void OwnerCommendCameraman( HubKiosk button )
	{
		var screen = button.Components.Get<HubArenaScreen>( FindMode.EverythingInSelfAndAncestors );
		if ( screen is null )
			return;
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			RpcHostCommend( screen.GameObject.Id );
		else
			screen.HostCommend( GameObject );
	}

	[Rpc.Host]
	void RpcHostCommend( Guid screenId )
	{
		if ( !IsHubRpcFromOwner() )
			return;
		foreach ( var screen in Scene.GetAllComponents<HubArenaScreen>() )
		{
			if ( screen.GameObject.Id == screenId )
			{
				screen.HostCommend( GameObject );
				return;
			}
		}
	}

	bool IsHubRpcFromOwner()
	{
		if ( !Networking.IsHost )
			return false;
		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller && caller.Id != owner.Id )
		{
			Log.Warning( "[Hub] Rejected intent — caller is not the pawn owner." );
			return false;
		}

		return true;
	}
}
