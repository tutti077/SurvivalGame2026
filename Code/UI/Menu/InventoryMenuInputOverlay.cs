using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Full-screen in-game UI layer while the inventory is open. Requests the cursor and keeps it inside the game window.
/// Does not take keyboard focus so Tab / Escape still reach <see cref="PlayerGameMenuController"/>.
/// </summary>
public sealed class InventoryMenuInputOverlay : Panel
{
	PlayerGameMenuController _menuController;
	PlayerInventoryInteraction _inventoryInteraction;
	bool _isOpen;

	public void BindMenuController( PlayerGameMenuController controller ) => _menuController = controller;

	public void BindInventoryInteraction( PlayerInventoryInteraction interaction ) => _inventoryInteraction = interaction;

	public override bool WantsMouseInput() => _isOpen;

	public void SetOpen( bool open )
	{
		if ( _isOpen == open )
			return;

		_isOpen = open;
		Style.Set( "display", open ? "flex" : "none" );
		Style.Set( "pointer-events", open ? "all" : "none" );

		AcceptsFocus = false;

	}

	public override void Tick()
	{
		base.Tick();

		if ( !_isOpen )
			return;

		if ( _menuController is not null && _menuController.GameObject.IsValid() )
			InventoryScreenPointer.ClampMouseToView( _menuController.GameObject );
	}

	protected override void OnEscape( PanelEvent e )
	{
		base.OnEscape( e );
		_menuController?.SetMenuOpen( false );
	}

	public override void OnButtonEvent( ButtonEvent e )
	{
		base.OnButtonEvent( e );

		if ( !_isOpen || !e.Pressed )
			return;

		if ( e.Button == "escape" )
			_menuController?.SetMenuOpen( false );
	}

	protected override void OnMouseDown( MousePanelEvent e )
	{
		if ( _isOpen && IsSecondaryMouseButton( e.Button ) )
		{
			e.StopPropagation();
			return;
		}

		if ( _isOpen && TryForwardInventoryPointer( e, pressed: true ) )
			return;

		if ( _isOpen && TryForwardHotbarPointer( e ) )
			return;

		base.OnMouseDown( e );
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		if ( _isOpen && IsSecondaryMouseButton( e.Button ) )
		{
			e.StopPropagation();
			return;
		}

		if ( _isOpen && TryForwardPlayerDropZone( e ) )
			return;

		if ( _isOpen && TryForwardInventoryPointer( e, pressed: false ) )
			return;

		if ( _isOpen && e.Button is "mouseleft" or "mouse1" or "Attack1" && TryForwardHotbarPointer( e, pressed: false ) )
			return;

		base.OnMouseUp( e );

		if ( !_isOpen || e.Button is not ( "mouseleft" or "mouse1" or "Attack1" ) )
			return;

		_inventoryInteraction?.OnGlobalMouseUp();
	}

	protected override void OnRightClick( MousePanelEvent e )
	{
		if ( _isOpen && TryForwardPlayerDropZoneRightClick() )
		{
			e.StopPropagation();
			return;
		}

		if ( _isOpen && TryForwardInventoryRightClick() )
		{
			e.StopPropagation();
			return;
		}

		if ( _isOpen && TryForwardHotbarRightClick() )
		{
			e.StopPropagation();
			return;
		}

		base.OnRightClick( e );
	}

	static bool IsSecondaryMouseButton( string button ) =>
		string.Equals( button, "mouseright", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( button, "mouse2", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( button, "Attack2", StringComparison.OrdinalIgnoreCase );

	bool TryForwardPlayerDropZoneRightClick()
	{
		if ( _inventoryInteraction is null )
			return false;

		if ( !_inventoryInteraction.IsOverPlayerDropZone( Mouse.Position ) )
			return false;

		_inventoryInteraction.TryReleaseOneOnPlayerDropZone();
		return true;
	}

	bool TryForwardPlayerDropZone( MousePanelEvent e )
	{
		if ( _inventoryInteraction is null )
			return false;

		if ( e.Button is not ( "mouseleft" or "mouse1" or "Attack1" ) )
			return false;

		if ( !_inventoryInteraction.IsOverPlayerDropZone( Mouse.Position ) )
			return false;

		_inventoryInteraction.TryReleaseOnPlayerDropZone();
		e.StopPropagation();
		return true;
	}

	bool TryForwardInventoryPointer( MousePanelEvent e, bool pressed )
	{
		if ( _inventoryInteraction is null )
			return false;

		var slot = _inventoryInteraction.FindPlayerBagSlotAtScreenPosition( Mouse.Position );
		if ( slot is null )
			return false;

		_inventoryInteraction.ProcessSlotPress( slot, e.Button, pressed );
		e.StopPropagation();
		return true;
	}

	bool TryForwardInventoryRightClick()
	{
		if ( _inventoryInteraction is null )
			return false;

		var slot = _inventoryInteraction.FindPlayerBagSlotAtScreenPosition( Mouse.Position );
		if ( slot is null )
			return false;

		_inventoryInteraction.ProcessSlotRightClick( slot );
		return true;
	}

	bool TryForwardHotbarRightClick()
	{
		if ( _inventoryInteraction is null )
			return false;

		var slot = _inventoryInteraction.FindHotbarSlotAtScreenPosition( Mouse.Position );
		if ( slot is null )
			return false;

		_inventoryInteraction.ProcessSlotRightClick( slot );
		return true;
	}

	bool TryForwardHotbarPointer( MousePanelEvent e, bool pressed = true )
	{
		if ( _inventoryInteraction is null )
			return false;

		if ( !pressed && _inventoryInteraction.IsDragging )
		{
			_inventoryInteraction.ProcessSlotPress( null, e.Button, pressed: false );
			e.StopPropagation();
			return true;
		}

		var slot = _inventoryInteraction.FindHotbarSlotAtScreenPosition( Mouse.Position );
		if ( slot is null )
			return false;

		_inventoryInteraction.ProcessSlotPress( slot, e.Button, pressed );
		e.StopPropagation();
		return true;
	}

}

