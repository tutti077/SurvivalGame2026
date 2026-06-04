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
		if ( _isOpen && TryForwardHotbarPointer( e ) )
			return;

		base.OnMouseDown( e );
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		if ( _isOpen && e.Button == "mouseleft" && TryForwardHotbarPointer( e, pressed: false ) )
			return;

		base.OnMouseUp( e );

		if ( !_isOpen || e.Button != "mouseleft" )
			return;

		_inventoryInteraction?.OnGlobalMouseUp();
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
