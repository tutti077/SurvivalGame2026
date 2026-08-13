using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Full-screen layer while the inventory/crafting menu is open.
/// Uses <see cref="MouseVisibility.Hidden"/> so <see cref="Input.MouseWheel"/> stays in gameplay
/// (Visible cursor mode never delivers wheel here). A software cursor + Attack1 hit-tests replace UI mouse.
/// </summary>
public sealed class InventoryMenuInputOverlay : Panel
{
	/// <summary>Converts <see cref="Input.AnalogLook"/> degrees into screen pixels when MouseDelta is empty.</summary>
	const float AnalogLookToPixels = 18f;

	PlayerGameMenuController _menuController;
	PlayerInventoryInteraction _inventoryInteraction;
	Action<Vector2> _craftingWheelSink;
	Func<Vector2, bool, bool> _craftingScrollbarPointer;
	bool _craftingScrollbarDragging;
	Func<Vector2, bool> _craftingRecipeSelect;
	Func<Vector2, bool, bool> _craftingCraftPointer;
	Func<Vector2, bool> _tabSelect;
	Func<Vector2, bool> _pageContentSelect;
	Action _menuGlobalMouseUp;
	bool _isOpen;

	Panel _softCursor;
	Vector2 _softCursorPos;
	bool _softCursorReady;

	public void BindMenuController( PlayerGameMenuController controller ) => _menuController = controller;

	public void BindInventoryInteraction( PlayerInventoryInteraction interaction ) => _inventoryInteraction = interaction;

	public void BindCraftingWheel( Action<Vector2> sink ) => _craftingWheelSink = sink;

	public void BindCraftingScrollbar( Func<Vector2, bool, bool> handler ) => _craftingScrollbarPointer = handler;

	public void BindCraftingRecipeSelect( Func<Vector2, bool> handler ) => _craftingRecipeSelect = handler;

	public void BindCraftingCraftPointer( Func<Vector2, bool, bool> handler ) => _craftingCraftPointer = handler;

	public void BindTabSelect( Func<Vector2, bool> handler ) => _tabSelect = handler;

	public void BindPageContentSelect( Func<Vector2, bool> handler ) => _pageContentSelect = handler;

	public void BindMenuGlobalMouseUp( Action handler ) => _menuGlobalMouseUp = handler;

	public override bool WantsMouseInput() => false;

	public void SetOpen( bool open )
	{
		if ( _isOpen == open )
			return;

		_isOpen = open;
		Style.Set( "display", open ? "flex" : "none" );
		Style.Set( "pointer-events", "none" );
		AcceptsFocus = false;
		_softCursorReady = false;
		_craftingScrollbarDragging = false;
		if ( !open )
			InventoryScreenPointer.SetSoftCursor( default, active: false );

		if ( _softCursor is not null && _softCursor.IsValid() )
			_softCursor.Style.Set( "display", open ? "flex" : "none" );

		Mouse.Visibility = MouseVisibility.Hidden;
		if ( open )
			InitSoftCursorFromView();
	}

	/// <summary>Call from HUD Component OnUpdate so MouseDelta/AnalogLook are fresh.</summary>
	public void PollMenuPointer()
	{
		if ( !_isOpen )
			return;

		if ( Mouse.Visibility != MouseVisibility.Hidden )
			Mouse.Visibility = MouseVisibility.Hidden;

		TickSoftCursor();
		PollAttack1MenuPointer();
		PollAttack2MenuPointer();
		PollEquipAmmoOnUse();
	}

	/// <summary>Inventory soft-cursor + E (HandHarvest): mark hovered ammo stack as preferred.</summary>
	void PollEquipAmmoOnUse()
	{
		if ( !Input.Pressed( "HandHarvest" ) )
			return;

		if ( _inventoryInteraction is null )
			return;

		var slot = _inventoryInteraction.FindPlayerBagSlotAtScreenPosition( _softCursorPos )
			?? _inventoryInteraction.FindHotbarSlotAtScreenPosition( _softCursorPos );
		if ( slot?.GridHost is null )
			return;

		var stack = slot.GridHost.GetSlot( slot.SlotIndex );
		if ( stack.IsEmpty || !AmmoCatalog.IsAmmo( stack.ResourceId ) )
			return;

		var pref = _inventoryInteraction.Components.Get<PlayerAmmoPreference>();
		pref?.OwnerTryEquipAmmoFromSlot( stack.ResourceId );
	}

	public override void Tick()
	{
		base.Tick();
		// Pointer is polled from PlayerScreenHud.OnUpdate (fresher input). Keep cursor drawn here.
		if ( !_isOpen )
			return;

		EnsureSoftCursorPanel();
		ApplySoftCursorVisual();
	}

	void InitSoftCursorFromView()
	{
		if ( _menuController is not null && _menuController.GameObject.IsValid() )
			_softCursorPos = InventoryScreenPointer.GetCrosshairScreenPosition( _menuController.GameObject );
		else
			_softCursorPos = Screen.Size * 0.5f;

		_softCursorReady = true;
		Mouse.Position = _softCursorPos;
		InventoryScreenPointer.SetSoftCursor( _softCursorPos, active: true );
	}

	void TickSoftCursor()
	{
		EnsureSoftCursorPanel();

		if ( !_softCursorReady )
			InitSoftCursorFromView();

		_softCursorPos += ReadSoftCursorDelta();

		// Full screen — camera ScreenRect clamping kept the soft cursor out of the top tab strip.
		var size = Screen.Size;
		_softCursorPos = new Vector2(
			_softCursorPos.x.Clamp( 0f, Math.Max( 0f, size.x - 1f ) ),
			_softCursorPos.y.Clamp( 0f, Math.Max( 0f, size.y - 1f ) ) );

		// Authoritative hit-test position for slots / scrollbar / tabs / drag ghost.
		Mouse.Position = _softCursorPos;
		InventoryScreenPointer.SetSoftCursor( _softCursorPos, active: true );
		ApplySoftCursorVisual();
	}

	static Vector2 ReadSoftCursorDelta()
	{
		// Prefer pixel deltas (same signal combat uses).
		var delta = Input.MouseDelta;
		if ( delta.LengthSquared > 1e-6f )
			return delta;

		delta = Mouse.Delta;
		if ( delta.LengthSquared > 1e-6f )
			return delta;

		// Fallback: look analog still updates while UseLookControls is false.
		// Negate pitch so mouse-down moves the soft cursor down (matches screen +Y).
		var look = Input.AnalogLook;
		return new Vector2( -look.yaw, -look.pitch ) * AnalogLookToPixels;
	}

	void EnsureSoftCursorPanel()
	{
		if ( _softCursor is not null && _softCursor.IsValid() )
			return;

		_softCursor = new Panel { Parent = this };
		_softCursor.Style.Set( "position", "absolute" );
		_softCursor.Style.Width = Length.Pixels( 14f );
		_softCursor.Style.Height = Length.Pixels( 14f );
		_softCursor.Style.BackgroundColor = new Color( 1f, 1f, 1f, 0.95f );
		_softCursor.Style.Set( "border-radius", "7px" );
		_softCursor.Style.Set( "pointer-events", "none" );
		_softCursor.Style.Set( "z-index", "10000" );
		_softCursor.Style.Set( "border-width", "2px" );
		_softCursor.Style.Set( "border-color", "#111111" );
	}

	void ApplySoftCursorVisual()
	{
		if ( _softCursor is null || !_softCursor.IsValid() )
			return;

		_softCursor.Style.Set( "display", "flex" );

		// Same screen→local mapping as inventory drag ghosts (PanelPositionToScreenPosition + ScaleToScreen).
		// ScreenPositionToPanelPosition was placing the dot above the real hit point → “clicks item far below”.
		var origin = PanelPositionToScreenPosition( Vector2.Zero );
		var scale = ScaleToScreen > 0.001f ? ScaleToScreen : 1f;
		var local = ( _softCursorPos - origin ) / scale;
		_softCursor.Style.Left = Length.Pixels( local.x - 7f );
		_softCursor.Style.Top = Length.Pixels( local.y - 7f );
	}

	void PollAttack1MenuPointer()
	{
		var pos = _softCursorPos;

		if ( Input.Pressed( "Attack1" ) )
		{
			if ( _tabSelect is not null && _tabSelect.Invoke( pos ) )
				return;

			if ( _pageContentSelect is not null && _pageContentSelect.Invoke( pos ) )
				return;

			if ( _craftingScrollbarPointer is not null && _craftingScrollbarPointer.Invoke( pos, true ) )
			{
				_craftingScrollbarDragging = true;
				return;
			}

			if ( _craftingRecipeSelect is not null && _craftingRecipeSelect.Invoke( pos ) )
				return;

			if ( _craftingCraftPointer is not null && _craftingCraftPointer.Invoke( pos, true ) )
				return;

			if ( TryInventoryPress( pressed: true ) )
				return;

			TryHotbarPress( pressed: true );
			return;
		}

		// While held: keep scrollbar drag alive even if cursor leaves the thin strip.
		if ( _craftingScrollbarDragging && Input.Down( "Attack1" ) )
			_craftingScrollbarPointer?.Invoke( pos, true );

		if ( Input.Released( "Attack1" ) )
		{
			if ( _craftingScrollbarDragging )
			{
				_craftingScrollbarPointer?.Invoke( pos, false );
				_craftingScrollbarDragging = false;
			}

			_craftingCraftPointer?.Invoke( pos, false );

			if ( _inventoryInteraction is not null && _inventoryInteraction.IsDragging )
			{
				var dropTarget = _inventoryInteraction.FindHotbarSlotAtScreenPosition( pos )
					?? _inventoryInteraction.FindPlayerBagSlotAtScreenPosition( pos );

				if ( dropTarget is not null )
				{
					_inventoryInteraction.ProcessSlotPress( dropTarget, "Attack1", pressed: false );
					_menuGlobalMouseUp?.Invoke();
					return;
				}

				if ( TryForwardPlayerDropZoneAttack1() )
				{
					_menuGlobalMouseUp?.Invoke();
					return;
				}

				// Released over empty space — return held to source.
				_inventoryInteraction.ProcessSlotPress( null, "Attack1", pressed: false );
				_menuGlobalMouseUp?.Invoke();
				return;
			}

			if ( TryForwardPlayerDropZoneAttack1() )
			{
				_menuGlobalMouseUp?.Invoke();
				return;
			}

			TryInventoryPress( pressed: false );
			TryHotbarPress( pressed: false );
			_inventoryInteraction?.OnGlobalMouseUp();
			_menuGlobalMouseUp?.Invoke();
		}
	}

	void PollAttack2MenuPointer()
	{
		if ( !Input.Pressed( "Attack2" ) )
			return;

		var pos = _softCursorPos;
		if ( _inventoryInteraction is null )
			return;

		if ( _inventoryInteraction.IsOverPlayerDropZone( pos ) )
		{
			_inventoryInteraction.TryReleaseOneOnPlayerDropZone();
			return;
		}

		var bag = _inventoryInteraction.FindPlayerBagSlotAtScreenPosition( pos );
		if ( bag is not null )
		{
			_inventoryInteraction.ProcessSlotRightClick( bag );
			return;
		}

		var hotbar = _inventoryInteraction.FindHotbarSlotAtScreenPosition( pos );
		if ( hotbar is not null )
			_inventoryInteraction.ProcessSlotRightClick( hotbar );
	}

	bool TryInventoryPress( bool pressed )
	{
		if ( _inventoryInteraction is null )
			return false;

		var slot = _inventoryInteraction.FindPlayerBagSlotAtScreenPosition( _softCursorPos );
		if ( slot is null )
			return false;

		_inventoryInteraction.ProcessSlotPress( slot, "Attack1", pressed );
		return true;
	}

	bool TryHotbarPress( bool pressed )
	{
		if ( _inventoryInteraction is null )
			return false;

		var slot = _inventoryInteraction.FindHotbarSlotAtScreenPosition( _softCursorPos );
		if ( slot is null )
			return false;

		_inventoryInteraction.ProcessSlotPress( slot, "Attack1", pressed );
		return true;
	}

	bool TryForwardPlayerDropZoneAttack1()
	{
		if ( _inventoryInteraction is null )
			return false;

		if ( !_inventoryInteraction.IsOverPlayerDropZone( _softCursorPos ) )
			return false;

		_inventoryInteraction.TryReleaseOnPlayerDropZone();
		return true;
	}

	public override void OnMouseWheel( Vector2 value )
	{
		if ( !_isOpen || _craftingWheelSink is null || _menuController is null )
		{
			base.OnMouseWheel( value );
			return;
		}

		if ( !string.Equals( _menuController.ActivePageId, MenuPageIds.Crafting, StringComparison.OrdinalIgnoreCase ) )
		{
			base.OnMouseWheel( value );
			return;
		}

		_craftingWheelSink.Invoke( value );
	}

	protected override void OnEscape( PanelEvent e )
	{
		if ( !_isOpen )
			return;

		// Consume Escape so closing inventory does not also open the engine pause menu.
		Input.EscapePressed = false;
		_menuController?.SetMenuOpen( false );
	}

	public override void OnButtonEvent( ButtonEvent e )
	{
		base.OnButtonEvent( e );

		if ( !_isOpen || !e.Pressed )
			return;

		if ( e.Button == "escape" )
		{
			Input.EscapePressed = false;
			_menuController?.SetMenuOpen( false );
		}
	}
}
