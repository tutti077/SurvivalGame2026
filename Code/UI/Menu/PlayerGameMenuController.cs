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

	/// <summary>
	/// Receives mouse wheel while the menu is open. Prefer values captured in <see cref="PreInput"/>
	/// before UI clears <see cref="Input.MouseWheel"/>.
	/// </summary>
	public Action<Vector2> MenuMouseWheelSink { get; set; }

	Vector2 _preInputMouseWheel;

	/// <summary>
	/// Apply wheel for the open menu. Reads PreInput capture, live <see cref="Input.MouseWheel"/>,
	/// and optional MouseWheelUp/Down action bindings (nothing should WantsMouseInput while menu is open).
	/// </summary>
	public void FlushCapturedMenuMouseWheel()
	{
		if ( !IsMenuOpen || MenuMouseWheelSink is null )
		{
			_preInputMouseWheel = default;
			return;
		}

		var scroll = _preInputMouseWheel;
		_preInputMouseWheel = default;

		if ( MathF.Abs( scroll.y ) < 0.01f && MathF.Abs( scroll.x ) < 0.01f )
			scroll = Input.MouseWheel;

		var fromMenuScrollAction = false;
		if ( MathF.Abs( scroll.y ) < 0.01f && MathF.Abs( scroll.x ) < 0.01f )
		{
			if ( Input.Pressed( "MenuScrollUp" ) )
			{
				scroll = new Vector2( 0f, -1f ); // panel convention: negative = up
				fromMenuScrollAction = true;
			}
			else if ( Input.Pressed( "MenuScrollDown" ) )
			{
				scroll = new Vector2( 0f, 1f );
				fromMenuScrollAction = true;
			}
		}

		if ( MathF.Abs( scroll.y ) < 0.01f && MathF.Abs( scroll.x ) < 0.01f )
			return;

		// Gameplay Input.MouseWheel: positive Y = wheel up (camera zoom). Panel: positive = down.
		if ( !fromMenuScrollAction )
			scroll = new Vector2( scroll.x, -scroll.y );

		MenuMouseWheelSink.Invoke( scroll );
	}

	PlayerVitals _vitals;

	PlayerController _controller;

	bool _savedUseLookControls = true;

	MouseVisibility _savedMouseVisibility = MouseVisibility.Hidden;



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

		// Capture before UI PanelInput consumes/clears Input.MouseWheel (visible cursor mode).
		_preInputMouseWheel = Input.MouseWheel;

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

		if ( IsMenuOpen )
			FlushCapturedMenuMouseWheel();

		if ( IsMenuOpen && Input.EscapePressed )
		{
			SetMenuOpen( false );
			return;
		}



		if ( WasActionPressed( CraftingMenuAction ) )
		{
			// Build snap next is also C while placing — don't open crafting over snap cycle.
			if ( IsLocalBuildHammerPlacing() )
				return;

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
			// Hidden so Input.MouseWheel works (Visible cursor mode swallows the wheel). Soft cursor is drawn by the overlay.
			Mouse.Visibility = MouseVisibility.Hidden;
			InventoryScreenPointer.ClampMouseToView( GameObject );
		}
		else
			Mouse.Visibility = MouseVisibility.Hidden;



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
		_controller.UseCameraControls = true;

		_savedUseLookControls = true;

		if ( Mouse.Visibility != MouseVisibility.Hidden )
			Mouse.Visibility = MouseVisibility.Hidden;

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

	bool IsLocalBuildHammerPlacing()
	{
		var equipment = Components.Get<PlayerEquipment>();
		if ( equipment is null || !equipment.MainHandHasAction( EquippedItemActions.BuildHammer ) )
			return false;

		foreach ( var hammer in Scene.GetAllComponents<ToolBuildHammer>() )
		{
			if ( hammer is null || !hammer.IsValid() )
				continue;

			if ( hammer.GameObject.Root != GameObject.Root )
				continue;

			return hammer.IsPlacingPiece;
		}

		return false;
	}

}


