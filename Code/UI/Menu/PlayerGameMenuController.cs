using System;

using Sandbox;



namespace Survival;



/// <summary>

/// Local pawn: menu open state, active page, and which panels (inventory / crafting) are visible.

/// Each page hotkey opens that page; pressing the same hotkey again while already on that page closes the menu.

/// </summary>

[Title( "Player Game Menu Controller" )]

public sealed class PlayerGameMenuController : Component, PlayerController.IEvents

{

	[Property, Group( "Input" )]

	public string InventoryMenuAction { get; set; } = "InventoryMenu";



	[Property, Group( "Input" )]

	public string CraftingMenuAction { get; set; } = "CraftingMenu";

	[Property, Group( "Input" )]
	public string MapMenuAction { get; set; } = "MapMenu";

	[Property, Group( "Input" )]
	public string SettingsMenuAction { get; set; } = "SettingsMenu";

	public bool IsMenuOpen { get; private set; }



	public string ActivePageId { get; private set; } = MenuPageIds.Inventory;



	public MenuPanelFlags VisiblePanels { get; private set; } = MenuPanelFlags.Inventory;



	public event Action<bool> MenuOpenChanged;



	public event Action MenuLayoutChanged;



	PlayerVitals _vitals;

	PlayerController _controller;

	bool _savedUseLookControls = true;

	MouseVisibility _savedMouseVisibility = MouseVisibility.Auto;



	protected override void OnStart()

	{

		base.OnStart();

		_vitals = Components.Get<PlayerVitals>();

		ResolveController();

		EnsureGameplayLookControls();

	}



	protected override void OnDisabled()

	{

		if ( IsMenuOpen )

			SetMenuOpen( false );

		else

			EnsureGameplayLookControls();

		base.OnDisabled();

	}



	public void PreInput()

	{

		if ( !IsMenuOpen || !IsLocalInputOwnedPawn() )

			return;



		ResolveController();

		if ( _controller is not null )

			_controller.UseLookControls = false;

	}



	protected override void OnUpdate()

	{

		base.OnUpdate();



		if ( _vitals is null )

			_vitals = Components.Get<PlayerVitals>();



		if ( !IsLocalInputOwnedPawn() )

			return;



		if ( IsMenuOpen && Input.EscapePressed )

		{

			SetMenuOpen( false );

			return;

		}



		if ( WasActionPressed( CraftingMenuAction ) )
		{
			HandlePageHotkey( MenuPageIds.Crafting );
			return;
		}

		if ( WasActionPressed( MapMenuAction ) )
		{
			HandlePageHotkey( MenuPageIds.Map );
			return;
		}

		if ( WasActionPressed( SettingsMenuAction ) )
		{
			HandlePageHotkey( MenuPageIds.Settings );
			return;
		}

		if ( WasActionPressed( InventoryMenuAction ) )
			HandlePageHotkey( MenuPageIds.Inventory );



		if ( !IsMenuOpen )

			EnsureGameplayLookControls();

	}



	public void SetMenuOpen( bool open )

	{

		if ( IsMenuOpen == open )

			return;



		ResolveController();



		if ( _controller is not null )

		{

			if ( open )

			{

				_savedUseLookControls = _controller.UseLookControls;

				_controller.UseLookControls = false;

			}

			else

				_controller.UseLookControls = _savedUseLookControls;

		}



		if ( open )

		{

			_savedMouseVisibility = Mouse.Visibility;

			Mouse.Visibility = MouseVisibility.Auto;

			InventoryScreenPointer.ClampMouseToView( GameObject );

		}

		else

			Mouse.Visibility = _savedMouseVisibility;



		IsMenuOpen = open;

		MenuOpenChanged?.Invoke( IsMenuOpen );



		if ( open )

			MenuLayoutChanged?.Invoke();

		else

			EnsureGameplayLookControls();

	}



	public void OpenInventoryPage() => OpenPage( MenuPageIds.Inventory );



	public void OpenCraftingPage() => OpenPage( MenuPageIds.Crafting );



	public void SetActivePage( string pageId )

	{

		if ( !IsMenuOpen )

		{

			OpenPage( pageId );

			return;

		}



		if ( IsActivePage( pageId ) )

		{

			SetMenuOpen( false );

			return;

		}



		SwitchToPage( pageId );

	}



	void HandlePageHotkey( string pageId )

	{

		if ( !IsMenuOpen )

		{

			OpenPage( pageId );

			return;

		}



		if ( IsActivePage( pageId ) )

		{

			SetMenuOpen( false );

			return;

		}



		SwitchToPage( pageId );

	}



	void OpenPage( string pageId )

	{

		ApplyPage( pageId );

		if ( !IsMenuOpen )

			SetMenuOpen( true );

		else

			MenuLayoutChanged?.Invoke();

	}



	void SwitchToPage( string pageId )

	{

		ApplyPage( pageId );

		MenuLayoutChanged?.Invoke();

	}



	bool IsActivePage( string pageId ) =>

		!string.IsNullOrWhiteSpace( pageId )

		&& string.Equals( ActivePageId, pageId, StringComparison.OrdinalIgnoreCase );



	void ApplyPage( string pageId )

	{

		var def = MenuPageRegistry.Get( pageId );

		ActivePageId = def.PageId;

		VisiblePanels = def.Panels;

	}



	bool WasActionPressed( string actionName )

	{

		if ( !string.IsNullOrWhiteSpace( actionName ) && Input.Pressed( actionName ) )

			return true;



		if ( string.Equals( actionName, "InventoryMenu", StringComparison.OrdinalIgnoreCase ) )

		{

			if ( !string.Equals( InventoryMenuAction, "Score", StringComparison.OrdinalIgnoreCase ) && Input.Pressed( "Score" ) )

				return true;

		}



		return false;

	}



	void EnsureGameplayLookControls()

	{

		if ( IsMenuOpen )

			return;



		ResolveController();

		if ( _controller is null )

			return;



		_controller.UseLookControls = true;

		_savedUseLookControls = true;

	}



	void ResolveController()

	{

		if ( _controller is not null && _controller.IsValid() )

			return;



		for ( var go = GameObject; go.IsValid(); go = go.Parent )

		{

			var pc = go.Components.Get<PlayerController>();

			if ( pc is not null )

			{

				_controller = pc;

				return;

			}

		}

	}



	bool IsLocalInputOwnedPawn()

	{

		if ( _vitals is null )

			_vitals = Components.Get<PlayerVitals>();



		return _vitals is not null && _vitals.IsLocalInputOwnedPawn();

	}

}


