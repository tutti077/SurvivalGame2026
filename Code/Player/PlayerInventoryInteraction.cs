using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Local inventory cursor: drag ghost, click rules, and menu-open mouse unlock.
/// </summary>
[Title( "Player Inventory Interaction" )]
public sealed class PlayerInventoryInteraction : Component
{
	public InventoryCursorStack Held => _held;
	public bool IsDragging => _leftDragActive;

	readonly List<InventorySlotPanel> _slots = new();
	readonly List<IInventoryGridHost> _grids = new();

	PlayerVitals _vitals;
	PlayerInventory _inventory;
	PlayerGameMenuController _menu;

	Panel _dragLayer;
	Panel _dragGhost;
	Panel _dragIcon;
	Label _dragCount;

	InventoryCursorStack _held;
	Vector2 _grabOffset;
	bool _leftDragActive;
	int _dragSourceSlot = -1;
	int _dropHoverSlot = -1;
	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		_inventory = Components.Get<PlayerInventory>();
		_menu = Components.Get<PlayerGameMenuController>();

		if ( _inventory is not null )
			_grids.Add( new PlayerInventoryGridHost( "player", _inventory ) );
	}

	protected override void OnDestroy()
	{
		if ( _menu is not null )
			_menu.MenuOpenChanged -= OnMenuOpenChanged;
		base.OnDestroy();
	}

	public void BindMenu( PlayerGameMenuController menu )
	{
		if ( _menu == menu )
			return;

		if ( _menu is not null )
			_menu.MenuOpenChanged -= OnMenuOpenChanged;

		_menu = menu;
		if ( _menu is not null )
		{
			_menu.MenuOpenChanged += OnMenuOpenChanged;
		}
	}

	public void RegisterGrid( IInventoryGridHost grid )
	{
		if ( grid is null || _grids.Contains( grid ) )
			return;
		_grids.Add( grid );
	}

	public void RegisterSlot( InventorySlotPanel slot )
	{
		if ( slot is null || _slots.Contains( slot ) )
			return;
		_slots.Add( slot );
	}

	/// <summary>Attach the drag ghost to the inventory column panel (same coordinate space as slots).</summary>
	public void BindDragLayer( Panel menuColumn )
	{
		if ( _dragLayer is not null && _dragLayer.IsValid() )
			return;

		_dragLayer = new Panel { Parent = menuColumn };
		_dragLayer.Style.Set( "position", "absolute" );
		_dragLayer.Style.Set( "left", "0" );
		_dragLayer.Style.Set( "top", "0" );
		_dragLayer.Style.Set( "right", "0" );
		_dragLayer.Style.Set( "bottom", "0" );
		_dragLayer.Style.Set( "pointer-events", "none" );
		_dragLayer.Style.Set( "z-index", "2000" );

		_dragGhost = new Panel { Parent = _dragLayer };
		_dragGhost.Style.Set( "position", "absolute" );
		_dragGhost.Style.Width = Length.Pixels( InventoryMenuSection.SlotSize );
		_dragGhost.Style.Height = Length.Pixels( InventoryMenuSection.SlotSize );
		_dragGhost.Style.Set( "display", "none" );
		_dragGhost.Style.Set( "pointer-events", "none" );

		_dragIcon = new Panel { Parent = _dragGhost };
		_dragIcon.Style.Set( "position", "absolute" );
		_dragIcon.Style.Set( "left", "4px" );
		_dragIcon.Style.Set( "top", "4px" );
		_dragIcon.Style.Set( "right", "4px" );
		_dragIcon.Style.Set( "bottom", "4px" );
		_dragIcon.Style.Set( "background-size", "contain" );
		_dragIcon.Style.Set( "background-repeat", "no-repeat" );
		_dragIcon.Style.Set( "background-position", "center" );

		_dragCount = new Label { Parent = _dragGhost };
		_dragCount.Style.Set( "position", "absolute" );
		_dragCount.Style.Set( "right", "3px" );
		_dragCount.Style.Set( "bottom", "1px" );
		_dragCount.Style.Set( "padding-left", "3px" );
		_dragCount.Style.Set( "padding-right", "3px" );
		_dragCount.Style.Set( "padding-top", "1px" );
		_dragCount.Style.Set( "padding-bottom", "1px" );
		_dragCount.Style.Set( "background-color", "rgba(0,0,0,0.65)" );
		_dragCount.Style.Set( "border-radius", "3px" );
		_dragCount.Style.FontColor = Color.White;
		_dragCount.Style.FontSize = Length.Pixels( 13f );
		_dragCount.Style.Set( "text-shadow", "1px 1px 2px black" );

	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( _menu is null || !_menu.IsMenuOpen )
			return;

		if ( !_leftDragActive && _held.IsEmpty )
			return;

		UpdateDragGhostPosition();
		UpdateDropHoverSlot();

	}

	/// <summary>Called from slot panels while dragging so release hit-test has a fallback.</summary>
	public void NotifyDropHover( int slotIndex, InventorySlotPanel slot )
	{
		if ( !_leftDragActive || slot is null || !slot.IsValid() )
			return;

		if ( SlotContainsScreenPoint( slot, GetDropProbeScreenPosition() ) )
			_dropHoverSlot = slotIndex;
	}

	void OnMenuOpenChanged( bool open )
	{
		if ( open )
			return;

		ReturnHeldToInventory();
		_leftDragActive = false;
		_dragSourceSlot = -1;
		HideDragGhost();
	}

	public void OnSlotMouseDown( InventorySlotPanel slot, MousePanelEvent e )
	{
		if ( !CanInteract() || _inventory is null || slot is null )
			return;

		var shift = Input.Down( "Run" );
		var isLeft = e.Button == "mouseleft";
		var isRight = e.Button == "mouseright";
		if ( !isLeft && !isRight )
			return;

		if ( isLeft )
		{
			if ( shift )
			{
				TryQuickMove( slot.SlotIndex );
				return;
			}

			if ( _held.IsEmpty )
				BeginDragFromSlot( slot );
			else
				_leftDragActive = true;
			return;
		}

		if ( isRight && shift )
		{
			if ( _held.IsEmpty )
				TryTakeHalfFromSlot( slot.SlotIndex );
			else
				TryPlaceHalfIntoSlot( slot.SlotIndex );
			return;
		}

		if ( isRight )
		{
			if ( _held.IsEmpty )
				TryTakeOneFromSlot( slot.SlotIndex );
			else
				TryDropOneIntoSlot( slot.SlotIndex );
		}
	}

	public void OnSlotMouseUp( InventorySlotPanel slot, MousePanelEvent e )
	{
		if ( e.Button != "mouseleft" || !_leftDragActive )
			return;

		FinishLeftDrag( ResolveDropTargetSlotIndex() );
	}

	public void PollInventoryInput( MenuPanelFlags visiblePanels )
	{
	}

	public void OnGlobalMouseUp()
	{
		if ( !_leftDragActive )
			return;

		FinishLeftDrag( ResolveDropTargetSlotIndex() );
	}

	void BeginDragFromSlot( InventorySlotPanel slot )
	{
		if ( !_inventory.OwnerTryPickupAll( slot.SlotIndex, out var picked ) || picked.IsEmpty )
			return;

		_held.Set( picked.ResourceId, picked.Count );
		_dragSourceSlot = slot.SlotIndex;
		_dropHoverSlot = slot.SlotIndex;
		_leftDragActive = true;

		ShowDragGhost();
		UpdateDragGhostPosition();
	}

	void FinishLeftDrag( int targetSlotIndex )
	{
		if ( !_leftDragActive )
			return;

		UpdateDragGhostPosition();
		_leftDragActive = false;

		if ( _held.IsEmpty )
		{
			HideDragGhost();
			_dragSourceSlot = -1;
			_dropHoverSlot = -1;
			return;
		}

		var sourceSlot = _dragSourceSlot;
		_dragSourceSlot = -1;

		if ( targetSlotIndex < 0 )
			targetSlotIndex = ResolveDropTargetSlotIndex();

		_dropHoverSlot = -1;

		if ( targetSlotIndex < 0 )
		{
			ReturnHeldToSourceSlot( sourceSlot );
			UpdateDragGhostVisibility();
			return;
		}

		var heldCopy = _held;
		if ( !_inventory.OwnerTryFinishDragDrop( sourceSlot, targetSlotIndex, ref heldCopy ) )
		{
			ReturnHeldToSourceSlot( sourceSlot );
			UpdateDragGhostVisibility();
			return;
		}

		_held = heldCopy;
		UpdateDragGhostVisibility();
	}

	void ReturnHeldToSourceSlot( int sourceSlotIndex )
	{
		if ( _held.IsEmpty || _inventory is null )
			return;

		if ( sourceSlotIndex < 0 )
		{
			ReturnHeldToInventory();
			return;
		}

		var heldCopy = _held;
		_inventory.OwnerTryPlaceHeld( sourceSlotIndex, ref heldCopy );
		_held = heldCopy;

		if ( _held.IsEmpty )
			HideDragGhost();
	}

	void TryPlaceHeldOnSlot( int slotIndex )
	{
		if ( _held.IsEmpty )
			return;

		var heldCopy = _held;
		_inventory.OwnerTryPlaceHeld( slotIndex, ref heldCopy );
		_held = heldCopy;
		UpdateDragGhostVisibility();
	}

	void TryTakeOneFromSlot( int slotIndex )
	{
		var source = _inventory.GetSlot( slotIndex );
		if ( source.IsEmpty )
			return;

		if ( !_held.IsEmpty && !_held.CanStack( source.ResourceId ) )
			return;

		if ( !_inventory.OwnerTryTakeOne( slotIndex ) )
			return;

		if ( _held.IsEmpty )
			_held.Set( source.ResourceId, 1 );
		else
			_held.Count++;

		UpdateDragGhostVisibility();
	}

	void TryDropOneIntoSlot( int slotIndex )
	{
		if ( _held.IsEmpty )
			return;

		var heldCopy = _held;
		if ( !_inventory.OwnerTryDropOne( slotIndex, heldCopy ) )
			return;

		_held.Count--;
		if ( _held.Count <= 0 )
			_held.Clear();

		UpdateDragGhostVisibility();
	}

	void TryTakeHalfFromSlot( int slotIndex )
	{
		var source = _inventory.GetSlot( slotIndex );
		if ( source.IsEmpty )
			return;

		var half = source.Count / 2;
		if ( half <= 0 )
			return;

		if ( !_held.IsEmpty && !_held.CanStack( source.ResourceId ) )
			return;

		if ( !_inventory.OwnerTryTakeHalf( slotIndex ) )
			return;

		if ( _held.IsEmpty )
			_held.Set( source.ResourceId, half );
		else
			_held.Count += half;

		UpdateDragGhostVisibility();
	}

	void TryPlaceHalfIntoSlot( int slotIndex )
	{
		if ( _held.IsEmpty )
			return;

		var half = _held.Count / 2;
		if ( half <= 0 )
			return;

		var dest = _inventory.GetSlot( slotIndex );
		if ( !dest.IsEmpty && !string.Equals( dest.ResourceId, _held.ResourceId, System.StringComparison.OrdinalIgnoreCase ) )
			return;

		var heldCopy = _held;
		if ( !_inventory.OwnerTryPlaceHalf( slotIndex, ref heldCopy ) )
			return;

		_held.Count -= half;
		if ( _held.Count <= 0 )
			_held.Clear();

		UpdateDragGhostVisibility();
	}

	void TryQuickMove( int fromSlotIndex )
	{
		var source = _inventory.GetSlot( fromSlotIndex );
		if ( source.IsEmpty )
			return;

		foreach ( var grid in _grids )
		{
			if ( grid is null || grid.Inventory is null || grid.GridId == "player" )
				continue;

			if ( grid.Inventory.OwnerTryQuickMove( fromSlotIndex, grid ) )
				return;
		}

		foreach ( var grid in _grids )
		{
			if ( grid is null || grid.Inventory is null )
				continue;

			if ( grid.Inventory.OwnerTryQuickMove( fromSlotIndex, grid ) )
				return;
		}
	}

	void ReturnHeldToInventory()
	{
		if ( _held.IsEmpty || _inventory is null )
			return;

		var heldCopy = _held;
		if ( _inventory.HasHostAuthority )
			_inventory.HostTryReturnStack( heldCopy );
		else
			_inventory.OwnerTryPlaceHeld( FindFirstEmptyOrStackSlot(), ref heldCopy );

		_held = heldCopy;
		if ( _held.IsEmpty )
			HideDragGhost();
	}

	int FindFirstEmptyOrStackSlot()
	{
		if ( _inventory is null || _held.IsEmpty )
			return 0;

		if ( _inventory.TryFindStackSlot( _held.ResourceId, out var stackIndex ) )
			return stackIndex;

		if ( _inventory.TryFindFirstEmptySlot( out var emptyIndex ) )
			return emptyIndex;

		return 0;
	}

	void UpdateDropHoverSlot()
	{
		var hover = FindSlotIndexAtScreenPosition( GetDropProbeScreenPosition() );
		if ( hover >= 0 )
			_dropHoverSlot = hover;
	}

	int ResolveDropTargetSlotIndex()
	{
		var hit = FindSlotIndexAtScreenPosition( GetDropProbeScreenPosition() );
		if ( hit >= 0 )
			return hit;

		return _dropHoverSlot;
	}

	Vector2 GetDropProbeScreenPosition() => Mouse.Position;

	int FindSlotIndexAtScreenPosition( Vector2 screenPosition )
	{
		for ( var i = _slots.Count - 1; i >= 0; i-- )
		{
			var slot = _slots[i];
			if ( slot is null || !slot.IsValid() )
				continue;

			if ( SlotContainsScreenPoint( slot, screenPosition ) )
				return slot.SlotIndex;
		}

		return -1;
	}

	static bool SlotContainsScreenPoint( InventorySlotPanel slot, Vector2 screenPosition )
	{
		if ( !slot.IsValid() )
			return false;

		var topLeft = slot.PanelPositionToScreenPosition( Vector2.Zero );
		var bottomRight = slot.PanelPositionToScreenPosition(
			new Vector2( InventoryMenuSection.SlotSize, InventoryMenuSection.SlotSize ) );

		if ( bottomRight.x > topLeft.x && bottomRight.y > topLeft.y )
		{
			if ( screenPosition.x >= topLeft.x && screenPosition.x <= bottomRight.x
			     && screenPosition.y >= topLeft.y && screenPosition.y <= bottomRight.y )
				return true;
		}

		if ( slot.IsInside( screenPosition ) )
			return true;

		var rect = slot.Box.Rect;
		if ( rect.Width <= 0f || rect.Height <= 0f )
			return false;

		return screenPosition.x >= rect.Left && screenPosition.x <= rect.Right
		       && screenPosition.y >= rect.Top && screenPosition.y <= rect.Bottom;
	}

	void ShowDragGhost()
	{
		if ( _dragGhost is null )
			return;

		_grabOffset = GetDragGrabOffsetBottomLeft();
		_dragGhost.Style.Set( "display", "flex" );
		ResourceCatalog.ApplyStackVisual( _dragIcon, _dragCount, new InventorySlot
		{
			ResourceId = _held.ResourceId,
			Count = _held.Count
		} );
	}

	void HideDragGhost()
	{
		if ( _dragGhost is null )
			return;

		_dragGhost.Style.Set( "display", "none" );
	}

	void UpdateDragGhostVisibility()
	{
		if ( _held.IsEmpty )
			HideDragGhost();
		else
			ShowDragGhost();
	}

	Vector2 ScreenToDragLayerLocal( Vector2 screenPosition )
	{
		if ( _dragLayer is null || !_dragLayer.IsValid() )
			return screenPosition;

		var rect = _dragLayer.Box.Rect;
		var origin = new Vector2( rect.Left, rect.Top );
		if ( rect.Width < 1f || rect.Height < 1f )
			origin = _dragLayer.PanelPositionToScreenPosition( Vector2.Zero );

		var local = screenPosition - origin;
		var scale = _dragLayer.ScaleToScreen;
		if ( scale > 0.001f )
			local /= scale;

		return local;
	}

	/// <summary>Screen-space offset so the ghost's bottom-left corner sits on <see cref="Mouse.Position"/>.</summary>
	Vector2 GetDragGrabOffsetBottomLeft() => new Vector2( 0f, GetDragGhostHeightScreen() );

	float GetDragGhostHeightScreen()
	{
		if ( _dragGhost is not null && _dragGhost.IsValid() )
		{
			var rect = _dragGhost.Box.Rect;
			var h = rect.Bottom - rect.Top;
			if ( h > 0f )
				return h;
		}

		if ( _dragLayer is not null && _dragLayer.IsValid() )
		{
			var scale = _dragLayer.ScaleToScreen;
			if ( scale > 0.001f )
				return InventoryMenuSection.SlotSize * scale;
		}

		for ( var i = 0; i < _slots.Count; i++ )
		{
			var slot = _slots[i];
			if ( slot is null || !slot.IsValid() )
				continue;

			var h = slot.Box.Rect.Bottom - slot.Box.Rect.Top;
			if ( h > 0f )
				return h;
		}

		return InventoryMenuSection.SlotSize;
	}

	void UpdateDragGhostPosition()
	{
		if ( _dragGhost is null || _held.IsEmpty )
			return;

		var ghostTopLeftScreen = Mouse.Position - _grabOffset;
		var layerPos = ScreenToDragLayerLocal( ghostTopLeftScreen );

		_dragGhost.Style.Left = Length.Pixels( layerPos.x );
		_dragGhost.Style.Top = Length.Pixels( layerPos.y );

		ResourceCatalog.ApplyStackVisual( _dragIcon, _dragCount, new InventorySlot
		{
			ResourceId = _held.ResourceId,
			Count = _held.Count
		} );
	}

	bool CanInteract()
	{
		if ( _vitals is null )
			_vitals = Components.Get<PlayerVitals>();
		if ( _inventory is null )
			_inventory = Components.Get<PlayerInventory>();
		if ( _menu is null )
			_menu = Components.Get<PlayerGameMenuController>();

		return _vitals is not null && _vitals.IsLocalInputOwnedPawn()
		       && _menu is not null && _menu.IsMenuOpen
		       && _inventory is not null && _inventory.IsLocalManagingClient();
	}
}
