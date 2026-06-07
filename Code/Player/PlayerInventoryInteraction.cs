using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Local inventory cursor: drag ghost, click rules, and menu-open mouse unlock.
/// Supports dragging between the player bag and hotbar.
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
	PlayerHotbar _hotbar;
	PlayerGameMenuController _menu;

	Panel _dragLayerRoot;
	Panel _dragLayer;
	Panel _dragGhost;
	Panel _dragIcon;
	Label _dragCount;

	InventoryCursorStack _held;
	Vector2 _grabOffset;
	bool _leftDragActive;
	bool _dragBindingOnly;
	IInventoryGridHost _dragSourceHost;
	int _dragSourceSlot = -1;
	InventorySlotPanel _dropHoverSlot;
	bool _hotbarHudDisplayed = true;
	double _lastRightPressTime = -1;

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		_inventory = Components.Get<PlayerInventory>();
		_hotbar = Components.Get<PlayerHotbar>();
		_menu = Components.Get<PlayerGameMenuController>();

		if ( _inventory is not null )
			_grids.Add( new PlayerInventoryGridHost( "player", _inventory ) );

		if ( _hotbar is not null )
			_grids.Add( new PlayerHotbarGridHost( _hotbar ) );
	}

	protected override void OnDestroy()
	{
		if ( _menu is not null )
		{
			_menu.MenuOpenChanged -= OnMenuOpenChanged;
			_menu.MenuLayoutChanged -= OnMenuLayoutChanged;
		}

		base.OnDestroy();
	}

	public void BindMenu( PlayerGameMenuController menu )
	{
		if ( _menu == menu )
			return;

		if ( _menu is not null )
		{
			_menu.MenuOpenChanged -= OnMenuOpenChanged;
			_menu.MenuLayoutChanged -= OnMenuLayoutChanged;
		}

		_menu = menu;
		if ( _menu is not null )
		{
			_menu.MenuOpenChanged += OnMenuOpenChanged;
			_menu.MenuLayoutChanged += OnMenuLayoutChanged;
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

	/// <summary>HUD root used for the drag ghost layer (created lazily on first drag).</summary>
	public void SetDragLayerRoot( Panel dragRoot ) => _dragLayerRoot = dragRoot;

	/// <summary>Legacy alias for <see cref="SetDragLayerRoot"/>.</summary>
	public void BindDragLayer( Panel dragRoot ) => SetDragLayerRoot( dragRoot );

	void EnsureDragLayer()
	{
		if ( _dragLayer is not null && _dragLayer.IsValid() )
			return;

		if ( _dragLayerRoot is null || !_dragLayerRoot.IsValid() )
			return;

		_dragLayer = new InventoryDragLayerPanel { Parent = _dragLayerRoot };
		_dragLayer.Style.Set( "position", "absolute" );
		_dragLayer.Style.Set( "left", "0" );
		_dragLayer.Style.Set( "top", "0" );
		_dragLayer.Style.Set( "right", "0" );
		_dragLayer.Style.Set( "bottom", "0" );
		_dragLayer.Style.Set( "pointer-events", "none" );
		_dragLayer.Style.Set( "z-index", "5000" );

		_dragGhost = new Panel { Parent = _dragLayer };
		_dragGhost.Style.Set( "position", "absolute" );
		_dragGhost.Style.Width = Length.Pixels( InventoryMenuSection.SlotSize );
		_dragGhost.Style.Height = Length.Pixels( InventoryMenuSection.SlotSize );
		_dragGhost.Style.Set( "display", "none" );
		_dragGhost.Style.Set( "pointer-events", "none" );

		var iconInset = 4f * InventoryMenuSection.Scale;
		_dragIcon = new Panel { Parent = _dragGhost };
		_dragIcon.Style.Set( "position", "absolute" );
		_dragIcon.Style.Set( "left", $"{iconInset}px" );
		_dragIcon.Style.Set( "top", $"{iconInset}px" );
		_dragIcon.Style.Set( "right", $"{iconInset}px" );
		_dragIcon.Style.Set( "bottom", $"{iconInset}px" );
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
		_dragCount.Style.FontSize = Length.Pixels( InventoryMenuSection.CountFontSize );
		_dragCount.Style.Set( "text-shadow", "1px 1px 2px black" );
	}

	public void SetHotbarHudDisplayed( bool displayed ) => _hotbarHudDisplayed = displayed;

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( !IsLocalInputOwnedPawn() )
			return;

		PollHotbarPointerInput();

		if ( _leftDragActive && Input.Released( "Attack1" ) )
			FinishActiveDrag( ResolveDropTargetSlot() );

		if ( _held.IsEmpty )
			return;

		RefreshHeldCursorVisual();
		if ( _leftDragActive )
			UpdateDropHoverSlot();
	}

	public void NotifyDropHover( InventorySlotPanel slot )
	{
		if ( !_leftDragActive || slot is null || !slot.IsValid() )
			return;

		if ( SlotContainsScreenPoint( slot, GetDropProbeScreenPosition() ) )
			_dropHoverSlot = slot;
	}

	void OnMenuOpenChanged( bool open )
	{
		if ( open )
			return;

		ReturnHeldOnMenuClose();
	}

	void OnMenuLayoutChanged()
	{
		if ( _menu is null || !_menu.IsMenuOpen || _held.IsEmpty )
			return;

		if ( !IsBagPanelVisible( _menu.VisiblePanels ) )
			ReturnHeldOnMenuClose();
	}

	static bool IsBagPanelVisible( MenuPanelFlags panels ) =>
		(panels & MenuPanelFlags.Inventory) != 0
		|| (panels & MenuPanelFlags.Crafting) != 0
		|| (panels & MenuPanelFlags.Quests) != 0;

	void ReturnHeldOnMenuClose()
	{
		if ( _held.IsEmpty )
		{
			ClearHeldVisualState();
			return;
		}

		if ( !_dragBindingOnly )
			AbsorbHeldIntoBagOrDropRemainder();

		ClearHeldVisualState();
	}

	void AbsorbHeldIntoBagOrDropRemainder()
	{
		if ( _held.IsEmpty || _inventory is null )
			return;

		var heldCopy = _held;
		_inventory.OwnerTryAbsorbCursorStackIntoBag( ref heldCopy );
		_held = heldCopy;

		if ( _held.IsEmpty )
			return;

		HeldStackWorldDrop.TryDrop( GameObject, ref _held );

		if ( !_held.IsEmpty )
			ReturnHeldToSource();
	}

	void CancelActiveDrag( bool returnHeld )
	{
		if ( returnHeld && !_dragBindingOnly )
			ReturnHeldToSource();

		ClearHeldVisualState();
	}

	void ClearHeldVisualState()
	{
		_leftDragActive = false;
		_dragBindingOnly = false;
		_dragSourceHost = null;
		_dragSourceSlot = -1;
		_dropHoverSlot = null;
		_held.Clear();
		HideDragGhost();
	}

	public void ProcessSlotPress( InventorySlotPanel slot, string button, bool pressed )
	{
		if ( !IsPrimaryMouseButton( button ) )
			return;

		if ( pressed )
		{
			if ( slot?.GridHost is not null )
				ProcessSlotLeftPress( slot );
			return;
		}

		ProcessSlotLeftRelease( slot );
	}

	/// <summary>Right-click release only — s&amp;box fires <c>OnRightClick</c> on release, not press.</summary>
	public void ProcessSlotRightClick( InventorySlotPanel slot ) => ProcessSlotRightPress( slot );

	static bool IsPrimaryMouseButton( string button ) =>
		string.Equals( button, "mouseleft", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( button, "mouse1", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( button, "Attack1", StringComparison.OrdinalIgnoreCase );

	static bool IsSecondaryMouseButton( string button ) =>
		string.Equals( button, "mouseright", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( button, "mouse2", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( button, "Attack2", StringComparison.OrdinalIgnoreCase );

	static bool WasPrimaryMousePressed() => Input.Pressed( "Attack1" );

	static bool IsPrimaryMouseDown() => Input.Down( "Attack1" );

	static bool IsSecondaryMouseDown() => Input.Down( "Attack2" );

	public InventorySlotPanel FindHotbarSlotAtScreenPosition( Vector2 screenPosition )
	{
		for ( var i = _slots.Count - 1; i >= 0; i-- )
		{
			var slot = _slots[i];
			if ( slot is null || !slot.IsValid() || !slot.IsHotbarSlot )
				continue;

			if ( SlotContainsScreenPoint( slot, screenPosition ) )
				return slot;
		}

		return null;
	}

	static bool IsStackMouseChord() => IsPrimaryMouseDown() && IsSecondaryMouseDown();

	void ProcessSlotLeftPress( InventorySlotPanel slot )
	{
		if ( !CanInteractSlot( slot ) )
			return;

		if ( IsStackMouseChord() )
			return;

		if ( Input.Down( "Run" ) )
		{
			if ( !_held.IsEmpty )
				return;

			TryQuickMoveToExternalStorage( slot );
			return;
		}

		if ( _held.IsEmpty )
		{
			BeginDragFromSlot( slot );
			return;
		}

		TryPlaceAllHeldIntoSlot( slot.GridHost, slot.SlotIndex );
	}

	void ProcessSlotLeftRelease( InventorySlotPanel slot )
	{
		if ( !_leftDragActive )
			return;

		if ( _dragBindingOnly )
			FinishActiveDrag( null );
		else
			FinishActiveDrag( ResolveDropTargetSlot() ?? slot );
	}

	void ProcessSlotRightPress( InventorySlotPanel slot )
	{
		if ( !CanInteractSlot( slot ) )
			return;

		if ( IsDuplicatePointerOp() )
			return;

		var host = slot.GridHost;
		var shift = Input.Down( "Run" );

		if ( !_held.IsEmpty )
		{
			if ( _leftDragActive )
			{
				if ( shift )
					TryPlaceHalfIntoSlot( host, slot.SlotIndex );
				else
					TryDropOneIntoSlot( host, slot.SlotIndex );

				return;
			}

			var slotStack = host.GetSlot( slot.SlotIndex );
			if ( !slotStack.IsEmpty && _held.CanStack( slotStack.ResourceId ) )
			{
				if ( shift )
					TryTakeHalfFromSlot( host, slot.SlotIndex );
				else
					TryTakeOneFromSlot( host, slot.SlotIndex );

				return;
			}

			if ( shift )
				TryPlaceHalfIntoSlot( host, slot.SlotIndex );
			else
				TryDropOneIntoSlot( host, slot.SlotIndex );

			return;
		}

		if ( IsStackMouseChord() || _leftDragActive )
			return;

		if ( shift )
			TryTakeHalfFromSlot( host, slot.SlotIndex );
		else
			TryTakeOneFromSlot( host, slot.SlotIndex );
	}

	bool IsDuplicatePointerOp()
	{
		var now = Time.NowDouble;
		if ( now - _lastRightPressTime < 0.001 )
			return true;

		_lastRightPressTime = now;
		return false;
	}

	/// <summary>Player bag slots only (not hotbar).</summary>
	public InventorySlotPanel FindPlayerBagSlotAtScreenPosition( Vector2 screenPosition )
	{
		for ( var i = _slots.Count - 1; i >= 0; i-- )
		{
			var slot = _slots[i];
			if ( slot is null || !slot.IsValid() || slot.IsHotbarSlot )
				continue;

			if ( SlotContainsScreenPoint( slot, screenPosition ) )
				return slot;
		}

		return null;
	}

	void PollMenuInventoryPointerInput()
	{
		if ( _menu is null )
			_menu = Components.Get<PlayerGameMenuController>();

		if ( _menu is null || !_menu.IsMenuOpen )
			return;

		var bagSlot = FindPlayerBagSlotAtScreenPosition( Mouse.Position );

		if ( _leftDragActive && Input.Released( "Attack2" ) && bagSlot is not null )
		{
			ProcessSlotRightClick( bagSlot );
			return;
		}

		if ( _held.IsEmpty )
			return;

		if ( bagSlot is null )
			return;

		if ( WasPrimaryMousePressed() )
			ProcessSlotLeftPress( bagSlot );
	}

	void PollHotbarPointerInput()
	{
		if ( !_hotbarHudDisplayed )
			return;

		var pointerOk = IsHotbarPointerUnlocked() || _leftDragActive;
		if ( !pointerOk )
			return;

		var menuBlocksPress = _menu is not null && _menu.IsMenuOpen;
		var slot = FindHotbarSlotAtScreenPosition( Mouse.Position );

		if ( !menuBlocksPress && WasPrimaryMousePressed() && slot is not null )
			ProcessSlotLeftPress( slot );

		if ( Input.Released( "Attack1" ) && _leftDragActive )
			FinishActiveDrag( menuBlocksPress ? null : ResolveDropTargetSlot() ?? slot );

		if ( !menuBlocksPress && Input.Released( "Attack2" ) && slot is not null )
			ProcessSlotRightClick( slot );
	}

	static bool IsHotbarPointerUnlocked() => Mouse.Visibility != MouseVisibility.Hidden;

	public void PollInventoryInput( MenuPanelFlags visiblePanels )
	{
		if ( !IsLocalInputOwnedPawn() )
			return;

		if ( _menu is null )
			_menu = Components.Get<PlayerGameMenuController>();

		if ( _menu is null || !_menu.IsMenuOpen )
			return;

		var showBag = (visiblePanels & MenuPanelFlags.Inventory) != 0
		              || (visiblePanels & MenuPanelFlags.Crafting) != 0
		              || (visiblePanels & MenuPanelFlags.Quests) != 0;

		if ( !showBag )
			return;

		PollMenuInventoryPointerInput();
	}

	public void OnGlobalMouseUp()
	{
		if ( !_leftDragActive )
			return;

		FinishActiveDrag( ResolveDropTargetSlot() );
	}

	void BeginDragFromSlot( InventorySlotPanel slot )
	{
		EnsureDragLayer();

		var host = slot.GridHost;
		if ( host is null )
			return;

		_dragBindingOnly = false;

		if ( host.OwnerTryPickupAll( slot.SlotIndex, out var picked ) && !picked.IsEmpty )
		{
			_held.Set( picked.ResourceId, picked.Count );
			_dragSourceHost = host;
			_dragSourceSlot = slot.SlotIndex;
			_dropHoverSlot = slot;
			_leftDragActive = true;
			ShowDragGhost();
			UpdateDragGhostPosition();
			return;
		}

		if ( !TryBeginBindingDragFromSlot( slot ) )
			return;

		_dragSourceHost = host;
		_dragSourceSlot = slot.SlotIndex;
		_dropHoverSlot = slot;
		_leftDragActive = true;
		ShowDragGhost();
		UpdateDragGhostPosition();
	}

	bool TryBeginBindingDragFromSlot( InventorySlotPanel slot )
	{
		if ( slot is null || slot.GridHost?.GridId != "hotbar" || _hotbar is null )
			return false;

		var slotIndex = slot.SlotIndex;
		var binding = _hotbar.GetBinding( slotIndex );
		if ( string.IsNullOrWhiteSpace( binding ) )
			return false;

		var stack = slot.GridHost.GetSlot( slotIndex );
		if ( !stack.IsEmpty )
			return false;

		_dragBindingOnly = true;
		_held.Set( binding, 1 );
		return true;
	}

	void FinishActiveDrag( InventorySlotPanel targetSlot )
	{
		if ( !_leftDragActive )
			return;

		UpdateDragGhostPosition();

		if ( _dragBindingOnly )
		{
			FinishBindingDrag();
			return;
		}

		FinishItemDrag( targetSlot );
	}

	void FinishItemDrag( InventorySlotPanel targetSlot )
	{
		if ( !_leftDragActive )
			return;

		_leftDragActive = false;

		if ( _held.IsEmpty )
		{
			HideDragGhost();
			_dragSourceHost = null;
			_dragSourceSlot = -1;
			_dropHoverSlot = null;
			return;
		}

		var sourceHost = _dragSourceHost;
		var sourceSlot = _dragSourceSlot;
		_dragSourceHost = null;
		_dragSourceSlot = -1;
		_dropHoverSlot = null;

		if ( targetSlot is null || targetSlot.GridHost is null )
		{
			ReturnHeldToSourceSlot( sourceHost, sourceSlot );
			RefreshHeldCursorVisual();
			return;
		}

		var destHost = targetSlot.GridHost;
		var destSlot = targetSlot.SlotIndex;

		var heldCopy = _held;
		var ok = sourceHost is not null && sourceHost.GridId == destHost.GridId
			? sourceHost.OwnerTryFinishDragDrop( sourceSlot, destSlot, ref heldCopy )
			: TryCrossGridDrop( sourceHost, sourceSlot, destHost, destSlot, ref heldCopy );

		if ( !ok )
		{
			ReturnHeldToSourceSlot( sourceHost, sourceSlot );
			RefreshHeldCursorVisual();
			return;
		}

		_held = heldCopy;
		RefreshHeldCursorVisual();
	}

	void FinishBindingDrag()
	{
		var bindingSlot = _dragSourceSlot;
		// Only the live cursor position counts — not _dropHoverSlot / ResolveDropTargetSlot fallback.
		var releasedOnHotbar = IsHotbarSlotAtScreenPosition( GetDropProbeScreenPosition() );

		_leftDragActive = false;
		_dragBindingOnly = false;
		_dragSourceHost = null;
		_dragSourceSlot = -1;
		_dropHoverSlot = null;
		_held.Clear();
		HideDragGhost();

		if ( !releasedOnHotbar && bindingSlot >= 0 && _hotbar is not null )
			_hotbar.OwnerClearBinding( bindingSlot );
	}

	bool IsHotbarSlotAtScreenPosition( Vector2 screenPosition ) =>
		FindHotbarSlotAtScreenPosition( screenPosition ) is not null;

	static bool TryCrossGridDrop(
		IInventoryGridHost sourceHost,
		int sourceSlot,
		IInventoryGridHost destHost,
		int destSlot,
		ref InventoryCursorStack held )
	{
		if ( destHost is null || held.IsEmpty )
			return false;

		var heldCopy = held;
		if ( !destHost.OwnerTryPlaceHeld( destSlot, ref heldCopy ) )
			return false;

		held = heldCopy;
		if ( !held.IsEmpty && sourceHost is not null && sourceSlot >= 0 )
		{
			var remainder = held;
			sourceHost.OwnerTryPlaceHeld( sourceSlot, ref remainder );
			held = remainder;
		}

		return true;
	}

	void ReturnHeldToSourceSlot( IInventoryGridHost sourceHost, int sourceSlotIndex )
	{
		if ( _held.IsEmpty )
			return;

		if ( sourceHost is not null && sourceSlotIndex >= 0 )
		{
			var heldCopy = _held;
			sourceHost.OwnerTryPlaceHeld( sourceSlotIndex, ref heldCopy );
			_held = heldCopy;
		}

		if ( !_held.IsEmpty )
			ReturnHeldToInventory();

		if ( _held.IsEmpty )
			HideDragGhost();
	}

	void ReturnHeldToSource()
	{
		if ( _held.IsEmpty )
			return;

		if ( _dragSourceHost is not null && _dragSourceSlot >= 0 )
		{
			var heldCopy = _held;
			_dragSourceHost.OwnerTryPlaceHeld( _dragSourceSlot, ref heldCopy );
			_held = heldCopy;
		}
		else
		{
			ReturnHeldToInventory();
		}

		if ( _held.IsEmpty )
			HideDragGhost();
	}

	void TryTakeOneFromSlot( IInventoryGridHost host, int slotIndex )
	{
		var source = host.GetSlot( slotIndex );
		if ( source.IsEmpty )
			return;

		if ( !_held.IsEmpty && !_held.CanStack( source.ResourceId ) )
			return;

		if ( !host.OwnerTryTakeOne( slotIndex, out var taken ) || taken.IsEmpty )
			return;

		var wasEmpty = _held.IsEmpty;
		if ( wasEmpty )
		{
			_held.Set( taken.ResourceId, taken.Count );
			RememberHeldReturnSlot( host, slotIndex );
		}
		else
			_held.Count += taken.Count;

		RefreshHeldCursorVisual();
	}

	void TryDropOneIntoSlot( IInventoryGridHost host, int slotIndex )
	{
		if ( _held.IsEmpty )
			return;

		var heldCopy = _held;
		if ( !host.OwnerTryDropOne( slotIndex, heldCopy, out var placed ) || placed <= 0 )
			return;

		_held.Count -= placed;
		if ( _held.Count <= 0 )
			_held.Clear();

		RefreshHeldCursorVisual();
	}

	void TryTakeHalfFromSlot( IInventoryGridHost host, int slotIndex )
	{
		var source = host.GetSlot( slotIndex );
		if ( source.IsEmpty )
			return;

		if ( !_held.IsEmpty && !_held.CanStack( source.ResourceId ) )
			return;

		if ( !host.OwnerTryTakeHalf( slotIndex, out var taken ) || taken.IsEmpty )
			return;

		var wasEmpty = _held.IsEmpty;
		if ( wasEmpty )
		{
			_held.Set( taken.ResourceId, taken.Count );
			RememberHeldReturnSlot( host, slotIndex );
		}
		else
			_held.Count += taken.Count;

		RefreshHeldCursorVisual();
	}

	void TryPlaceHalfIntoSlot( IInventoryGridHost host, int slotIndex )
	{
		if ( _held.IsEmpty )
			return;

		var half = _held.Count / 2;
		if ( half <= 0 )
			return;

		var dest = host.GetSlot( slotIndex );
		if ( !dest.IsEmpty && !string.Equals( dest.ResourceId, _held.ResourceId, System.StringComparison.OrdinalIgnoreCase ) )
			return;

		var heldCopy = _held;
		if ( !host.OwnerTryPlaceHalf( slotIndex, ref heldCopy ) )
			return;

		_held = heldCopy;
		RefreshHeldCursorVisual();
	}

	void RememberHeldReturnSlot( IInventoryGridHost host, int slotIndex )
	{
		if ( host is null || slotIndex < 0 )
			return;

		_dragSourceHost = host;
		_dragSourceSlot = slotIndex;
	}

	void TryPlaceAllHeldIntoSlot( IInventoryGridHost host, int slotIndex )
	{
		if ( host is null || _held.IsEmpty )
			return;

		var heldCopy = _held;
		if ( !host.OwnerTryPlaceHeld( slotIndex, ref heldCopy ) )
			return;

		_held = heldCopy;
		if ( _held.IsEmpty )
		{
			_leftDragActive = false;
			_dragSourceHost = null;
			_dragSourceSlot = -1;
			_dropHoverSlot = null;
		}

		RefreshHeldCursorVisual();
	}

	/// <summary>Shift+click transfer into a separate storage grid (chest, etc.) — not player bag / hotbar shuffles.</summary>
	void TryQuickMoveToExternalStorage( InventorySlotPanel fromSlot )
	{
		if ( fromSlot?.GridHost is null )
			return;

		var fromHost = fromSlot.GridHost;
		var fromIndex = fromSlot.SlotIndex;
		if ( fromHost.GetSlot( fromIndex ).IsEmpty )
			return;

		foreach ( var grid in _grids )
		{
			if ( grid is null || !IsExternalStorageGrid( grid ) )
				continue;

			if ( TryCrossGridQuickMove( fromHost, fromIndex, grid ) )
				return;
		}
	}

	static bool IsExternalStorageGrid( IInventoryGridHost grid ) =>
		grid is not null && grid.GridId is not "player" and not "hotbar";

	bool TryCrossGridQuickMove( IInventoryGridHost fromHost, int fromIndex, IInventoryGridHost toHost )
	{
		var source = fromHost.GetSlot( fromIndex );
		if ( source.IsEmpty || toHost is null )
			return false;

		if ( !toHost.TryFindQuickMoveTarget( source, fromIndex, out var targetIndex ) )
			return false;

		if ( !fromHost.OwnerTryPickupAll( fromIndex, out var picked ) || picked.IsEmpty )
			return false;

		var held = new InventoryCursorStack();
		held.Set( picked.ResourceId, picked.Count );
		if ( !toHost.OwnerTryPlaceHeld( targetIndex, ref held ) )
		{
			fromHost.OwnerTryPlaceHeld( fromIndex, ref held );
			return false;
		}

		if ( !held.IsEmpty )
			fromHost.OwnerTryPlaceHeld( fromIndex, ref held );

		return true;
	}

	void ReturnHeldToInventory()
	{
		if ( _held.IsEmpty || _inventory is null )
			return;

		var heldCopy = _held;
		_inventory.OwnerTryAbsorbCursorStackIntoBag( ref heldCopy );
		_held = heldCopy;

		if ( _held.IsEmpty )
			HideDragGhost();
	}

	void UpdateDropHoverSlot()
	{
		var hover = FindSlotAtScreenPosition( GetDropProbeScreenPosition() );
		if ( hover is not null )
			_dropHoverSlot = hover;
	}

	InventorySlotPanel ResolveDropTargetSlot()
	{
		var hit = FindSlotAtScreenPosition( GetDropProbeScreenPosition() );
		if ( hit is not null )
			return hit;

		return _dropHoverSlot;
	}

	Vector2 GetDropProbeScreenPosition() => Mouse.Position;

	InventorySlotPanel FindSlotAtScreenPosition( Vector2 screenPosition )
	{
		for ( var i = _slots.Count - 1; i >= 0; i-- )
		{
			var slot = _slots[i];
			if ( slot is null || !slot.IsValid() )
				continue;

			if ( SlotContainsScreenPoint( slot, screenPosition ) )
				return slot;
		}

		return null;
	}

	static bool SlotContainsScreenPoint( InventorySlotPanel slot, Vector2 screenPosition )
	{
		if ( !slot.IsValid() )
			return false;

		var size = GetSlotScreenSize( slot );

		var topLeft = slot.PanelPositionToScreenPosition( Vector2.Zero );
		var bottomRight = slot.PanelPositionToScreenPosition( size );

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

	static Vector2 GetSlotScreenSize( InventorySlotPanel slot )
	{
		var rect = slot.Box.Rect;
		var w = rect.Right - rect.Left;
		var h = rect.Bottom - rect.Top;
		if ( w > 0f && h > 0f )
			return new Vector2( w, h );

		var isHotbar = slot.GridHost?.GridId == "hotbar";
		var size = isHotbar ? HotbarHud.SlotSize : InventoryMenuSection.SlotSize;
		var scale = slot.ScaleToScreen;
		if ( scale > 0.001f )
			size *= scale;

		return new Vector2( size, size );
	}

	void RefreshHeldCursorVisual()
	{
		if ( _held.IsEmpty )
		{
			HideDragGhost();
			return;
		}

		EnsureDragLayer();
		ShowDragGhost();
		UpdateDragGhostPosition();
	}

	void ShowDragGhost()
	{
		EnsureDragLayer();
		if ( _dragGhost is null )
			return;

		_grabOffset = GetDragGrabOffsetBottomLeft();
		_dragGhost.Style.Set( "display", "flex" );

		if ( _dragBindingOnly )
			ResourceCatalog.ApplyBindingGhostVisual( _dragIcon, _dragCount, _held.ResourceId );
		else
		{
			ResourceCatalog.ApplyStackVisual( _dragIcon, _dragCount, new InventorySlot
			{
				ResourceId = _held.ResourceId,
				Count = _held.Count
			} );
		}
	}

	void HideDragGhost()
	{
		if ( _dragGhost is null )
			return;

		_dragGhost.Style.Set( "display", "none" );
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

		if ( _dragBindingOnly )
			ResourceCatalog.ApplyBindingGhostVisual( _dragIcon, _dragCount, _held.ResourceId );
		else
		{
			ResourceCatalog.ApplyStackVisual( _dragIcon, _dragCount, new InventorySlot
			{
				ResourceId = _held.ResourceId,
				Count = _held.Count
			} );
		}
	}

	bool IsLocalInputOwnedPawn()
	{
		if ( _vitals is null )
			_vitals = Components.Get<PlayerVitals>();
		return _vitals is not null && _vitals.IsLocalInputOwnedPawn();
	}

	bool CanInteractSlot( InventorySlotPanel slot )
	{
		if ( slot?.GridHost is null )
			return false;

		if ( _vitals is null )
			_vitals = Components.Get<PlayerVitals>();
		if ( _inventory is null )
			_inventory = Components.Get<PlayerInventory>();
		if ( _hotbar is null )
			_hotbar = Components.Get<PlayerHotbar>();
		if ( _menu is null )
			_menu = Components.Get<PlayerGameMenuController>();

		if ( _vitals is null || !_vitals.IsLocalInputOwnedPawn() )
			return false;

		if ( slot.GridHost.GridId == "hotbar" )
		{
			return _hotbarHudDisplayed
			       && IsHotbarPointerUnlocked()
			       && _hotbar is not null
			       && _hotbar.IsLocalManagingClient();
		}

		return _menu is not null && _menu.IsMenuOpen
		       && _inventory is not null && _inventory.IsLocalManagingClient();
	}
}
