using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Local inventory cursor: drag ghost, click rules, and menu-open mouse unlock.
/// Supports dragging between the player bag, hotbar, and an opened world container
/// (see <c>PlayerInventoryInteraction.Container.cs</c>).
/// </summary>
[Title( "Player Inventory Interaction" )]
public sealed partial class PlayerInventoryInteraction : Component
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
	InventoryPlayerDropZonePanel _playerDropZone;
	bool _playerDropZoneHighlighted;
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

		InitializeContainerGrid();
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

	public void RegisterPlayerDropZone( InventoryPlayerDropZonePanel zone ) => _playerDropZone = zone;

	public bool IsOverPlayerDropZone( Vector2 screenPosition ) =>
		_playerDropZone is not null
		&& _playerDropZone.IsValid()
		&& _playerDropZone.IsDisplayed
		&& PanelContainsScreenPoint( _playerDropZone, screenPosition );

	public void NotifyPlayerDropZoneHover( InventoryPlayerDropZonePanel zone )
	{
		if ( zone is null || zone != _playerDropZone || _held.IsEmpty || _dragBindingOnly )
			return;

		_playerDropZoneHighlighted = PanelContainsScreenPoint( zone, GetDropProbeScreenPosition() );
		zone.SetHighlighted( _playerDropZoneHighlighted );
	}

	public void TryReleaseOnPlayerDropZone()
	{
		if ( _held.IsEmpty || _dragBindingOnly || !IsOverPlayerDropZone( InventoryScreenPointer.GetMenuOrMousePosition() ) )
			return;

		var sourceHost = _dragSourceHost;
		var sourceSlot = _dragSourceSlot;
		_leftDragActive = false;

		if ( !TryPlayerDropHeld( sourceHost, sourceSlot, ref _held ) )
			return;

		_dragSourceHost = null;
		_dragSourceSlot = -1;
		_dropHoverSlot = null;

		if ( _held.IsEmpty )
			ClearHeldVisualState();
		else
			RefreshHeldCursorVisual();
	}

	public void TryReleaseOneOnPlayerDropZone()
	{
		if ( _held.IsEmpty || _dragBindingOnly || !IsOverPlayerDropZone( InventoryScreenPointer.GetMenuOrMousePosition() ) )
			return;

		if ( !TryPlayerDropHeld( _dragSourceHost, _dragSourceSlot, ref _held, dropCount: 1 ) )
			return;

		if ( _held.IsEmpty )
		{
			_leftDragActive = false;
			_dragSourceHost = null;
			_dragSourceSlot = -1;
			_dropHoverSlot = null;
			ClearHeldVisualState();
		}
		else
		{
			RefreshHeldCursorVisual();
		}
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
		UpdatePlayerDropZone();
		TickContainerAccess();

		// While the game menu is open, Attack1 drag finish is owned by InventoryMenuInputOverlay
		// (soft cursor). Finishing here with Mouse.Position cancels bag→hotbar drops.
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		if ( !menuOpen && _leftDragActive && Input.Released( "Attack1" ) )
			FinishActiveDrag( ResolveDropTargetSlot() );

		if ( _held.IsEmpty )
		{
			_playerDropZoneHighlighted = false;
			return;
		}

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
		CloseContainer();
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

	/// <summary>
	/// Single right-click entry point. Menu open: <see cref="InventoryMenuInputOverlay"/> calls this
	/// on Attack2 press. Menu closed: the hotbar poll calls it on release. Never both for one click.
	/// </summary>
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
			if ( slot.IsHotbarSlot && !CanBeginDragFromHotbarSlot( slot ) )
			{
				SelectHotbarSlot( slot.SlotIndex );
				return;
			}

			BeginDragFromSlot( slot );
			return;
		}

		TryPlaceAllHeldIntoSlot( slot.GridHost, slot.SlotIndex );
	}

	void SelectHotbarSlot( int slotIndex )
	{
		if ( _hotbar is null || slotIndex < 0 || slotIndex >= PlayerHotbar.SlotCount )
			return;

		_hotbar.SetActiveSlot( slotIndex );
	}

	bool CanBeginDragFromHotbarSlot( InventorySlotPanel slot )
	{
		if ( slot?.GridHost is null || slot.GridHost.GridId != "hotbar" )
			return false;

		if ( !slot.GridHost.GetSlot( slot.SlotIndex ).IsEmpty )
			return true;

		return _hotbar is not null && !string.IsNullOrWhiteSpace( _hotbar.GetBinding( slot.SlotIndex ) );
	}

	void ProcessSlotLeftRelease( InventorySlotPanel slot )
	{
		if ( !_leftDragActive )
			return;

		if ( _dragBindingOnly )
		{
			FinishActiveDrag( null );
			return;
		}

		// Prefer the slot under the soft cursor. ResolveDropTargetSlot() can hit stale/hidden bag rects
		// and was overriding a valid hotbar target — bag→hotbar drops then bounced back.
		FinishActiveDrag( slot ?? ResolveDropTargetSlot() );
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

	/// <summary>Player bag / paperdoll / open-container slots (not hotbar). MainHand is display-only and skipped.</summary>
	public InventorySlotPanel FindPlayerBagSlotAtScreenPosition( Vector2 screenPosition )
	{
		for ( var i = _slots.Count - 1; i >= 0; i-- )
		{
			var slot = _slots[i];
			if ( slot is null || !slot.IsValid() || slot.IsHotbarSlot )
				continue;

			if ( slot.GridHost?.GridId == "paperdoll" && slot.SlotIndex == (int)EquipmentSlot.MainHand )
				continue;

			if ( IsClosedContainerSlot( slot ) )
				continue;

			if ( SlotContainsScreenPoint( slot, screenPosition ) )
				return slot;
		}

		return null;
	}

	static bool IsClosedContainerSlot( InventorySlotPanel slot ) =>
		slot?.GridHost is ContainerInventoryGridHost { IsActive: false };

	void PollHotbarPointerInput()
	{
		if ( !_hotbarHudDisplayed )
			return;

		var pointerOk = IsHotbarPointerUnlocked() || _leftDragActive;
		if ( !pointerOk )
			return;

		var menuBlocksPress = _menu is not null && _menu.IsMenuOpen;
		// Menu soft-cursor overlay owns Attack1 press/release while open — do not cancel drags with null.
		if ( menuBlocksPress )
			return;

		var slot = FindHotbarSlotAtScreenPosition( InventoryScreenPointer.GetMenuOrMousePosition() );

		if ( WasPrimaryMousePressed() && slot is not null )
			ProcessSlotLeftPress( slot );

		if ( Input.Released( "Attack1" ) && _leftDragActive )
			FinishActiveDrag( ResolveDropTargetSlot() ?? slot );

		if ( Input.Released( "Attack2" ) && slot is not null )
			ProcessSlotRightClick( slot );
	}

	bool IsHotbarPointerUnlocked() =>
		Mouse.Visibility != MouseVisibility.Hidden
		|| ( _menu is not null && _menu.IsMenuOpen );

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

		// Explicit bag/hotbar target wins over the "Drop item" zone.
		if ( targetSlot is not null && targetSlot.GridHost is not null )
		{
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
			return;
		}

		if ( TryPlayerDropHeld( sourceHost, sourceSlot, ref _held ) )
		{
			RefreshHeldCursorVisual();
			return;
		}

		if ( TryWorldDropFromDrag( sourceHost, sourceSlot, ref _held ) )
		{
			RefreshHeldCursorVisual();
			return;
		}

		ReturnHeldToSourceSlot( sourceHost, sourceSlot );
		RefreshHeldCursorVisual();
	}

	bool TryPlayerDropHeld( IInventoryGridHost sourceHost, int sourceSlot, ref InventoryCursorStack held, int dropCount = -1 )
	{
		if ( held.IsEmpty || _dragBindingOnly || !IsOverPlayerDropZone( GetDropProbeScreenPosition() ) )
			return false;

		if ( dropCount < 0 )
			dropCount = held.Count;
		else
			dropCount = Math.Clamp( dropCount, 1, held.Count );

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
			return HeldStackWorldDrop.TryDropAtPlayer( GameObject, ref held, dropCount );

		RpcRequestPlayerWorldDrop( sourceHost?.GridId ?? string.Empty, sourceSlot, held.ResourceId, dropCount );
		held.Count -= dropCount;
		if ( held.Count <= 0 )
			held.Clear();
		return true;
	}

	[Rpc.Host]
	void RpcRequestPlayerWorldDrop( string sourceGridId, int sourceSlot, string resourceId, int count )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( resourceId, count );
		if ( HeldStackWorldDrop.TryDropAtPlayer( GameObject, ref held, count ) )
			return;

		RestoreHeldAfterFailedDrop( sourceGridId, sourceSlot, ref held );
	}

	void RestoreHeldAfterFailedDrop( string sourceGridId, int sourceSlot, ref InventoryCursorStack held )
	{
		if ( held.IsEmpty )
			return;

		if ( string.Equals( sourceGridId, "hotbar", StringComparison.OrdinalIgnoreCase ) )
		{
			var hotbar = Components.Get<PlayerHotbar>();
			if ( hotbar is not null && sourceSlot >= 0 )
				hotbar.HostTryPlaceHeld( sourceSlot, ref held );
			return;
		}

		if ( string.Equals( sourceGridId, "player", StringComparison.OrdinalIgnoreCase ) )
		{
			var inventory = Components.Get<PlayerInventory>();
			if ( inventory is not null && sourceSlot >= 0 )
			{
				var host = new PlayerInventoryGridHost( "player", inventory );
				host.OwnerTryPlaceHeld( sourceSlot, ref held );
			}
		}
	}

	void UpdatePlayerDropZone()
	{
		if ( _playerDropZone is null || !_playerDropZone.IsValid() )
			return;

		if ( _menu is null )
			_menu = Components.Get<PlayerGameMenuController>();

		var show = !_held.IsEmpty
		           && !_dragBindingOnly
		           && _menu is not null
		           && _menu.IsMenuOpen
		           && IsBagPanelVisible( _menu.VisiblePanels );

		_playerDropZone.SetDisplayed( show );
		if ( !show )
		{
			_playerDropZoneHighlighted = false;
			_playerDropZone.SetHighlighted( false );
		}
	}

	bool TryWorldDropFromDrag( IInventoryGridHost sourceHost, int sourceSlot, ref InventoryCursorStack held )
	{
		if ( sourceHost?.GridId != "hotbar" || held.IsEmpty )
			return false;

		if ( FindSlotAtScreenPosition( GetDropProbeScreenPosition() ) is not null )
			return false;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
			return HeldStackWorldDrop.TryDrop( GameObject, ref held, GetDropProbeScreenPosition() );

		RpcRequestHotbarWorldDrop( sourceSlot, held.ResourceId, held.Count, GetDropProbeScreenPosition() );
		held.Clear();
		return true;
	}

	[Rpc.Host]
	void RpcRequestHotbarWorldDrop( int sourceSlot, string resourceId, int count, Vector2 screenPosition )
	{
		if ( !Networking.IsHost )
			return;

		var held = new InventoryCursorStack();
		held.Set( resourceId, count );
		if ( HeldStackWorldDrop.TryDrop( GameObject, ref held, screenPosition ) )
			return;

		RestoreHeldAfterFailedDrop( "hotbar", sourceSlot, ref held );
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

	/// <summary>
	/// Shift+click: an open container wins first (bag/hotbar → container, container → bag);
	/// otherwise hotbar-equipables (weapons/tools) go bag↔hotbar and armor/etc → paperdoll.
	/// MainHand is not a storage destination — it mirrors the selected hotbar slot.
	/// </summary>
	void TryQuickMoveToExternalStorage( InventorySlotPanel fromSlot )
	{
		if ( fromSlot?.GridHost is null )
			return;

		var fromHost = fromSlot.GridHost;
		var fromIndex = fromSlot.SlotIndex;
		var source = fromHost.GetSlot( fromIndex );
		if ( source.IsEmpty )
			return;

		// Open container takes priority: bag/hotbar → container, container → bag.
		if ( _containerGrid is { IsActive: true } )
		{
			if ( ReferenceEquals( fromHost, _containerGrid ) )
			{
				TryQuickMoveToPlayerBag( fromHost, fromIndex );
				return;
			}

			if ( fromHost.GridId is "player" or "hotbar"
				&& TryCrossGridQuickMove( fromHost, fromIndex, _containerGrid ) )
				return;
		}

		var hotbarEquipable = EquipmentCatalog.TryGet( source.ResourceId, out var profile )
			&& EquipmentCatalog.IsHotbarMainHandItem( profile );

		if ( hotbarEquipable )
		{
			TryQuickMoveHotbarEquipable( fromHost, fromIndex );
			return;
		}

		foreach ( var grid in _grids )
		{
			if ( grid is null || !IsExternalStorageGrid( grid ) )
				continue;

			if ( TryCrossGridQuickMove( fromHost, fromIndex, grid ) )
				return;
		}

		// Paperdoll / container → bag
		if ( fromHost.GridId is not "player" )
			TryQuickMoveToPlayerBag( fromHost, fromIndex );
	}

	bool TryQuickMoveToPlayerBag( IInventoryGridHost fromHost, int fromIndex )
	{
		for ( var i = 0; i < _grids.Count; i++ )
		{
			var grid = _grids[i];
			if ( grid is null || grid.GridId is not "player" )
				continue;

			if ( TryCrossGridQuickMove( fromHost, fromIndex, grid ) )
				return true;
		}

		return false;
	}

	void TryQuickMoveHotbarEquipable( IInventoryGridHost fromHost, int fromIndex )
	{
		if ( fromHost.GridId == "hotbar" )
		{
			for ( var i = 0; i < _grids.Count; i++ )
			{
				var grid = _grids[i];
				if ( grid is null || grid.GridId is not "player" )
					continue;

				TryCrossGridQuickMove( fromHost, fromIndex, grid );
				return;
			}

			return;
		}

		if ( fromHost.GridId == "player" )
		{
			for ( var i = 0; i < _grids.Count; i++ )
			{
				var grid = _grids[i];
				if ( grid is null || grid.GridId is not "hotbar" )
					continue;

				TryCrossGridQuickMove( fromHost, fromIndex, grid );
				return;
			}
		}
	}

	static bool IsExternalStorageGrid( IInventoryGridHost grid ) =>
		grid is not null && grid.GridId is not "player" and not "hotbar";

	bool TryCrossGridQuickMove( IInventoryGridHost fromHost, int fromIndex, IInventoryGridHost toHost )
	{
		var source = fromHost.GetSlot( fromIndex );
		if ( source.IsEmpty || toHost is null )
			return false;

		// -1: the source index belongs to the other grid — never exclude a destination slot.
		if ( !toHost.TryFindQuickMoveTarget( source, -1, out _ ) )
			return false;

		if ( !fromHost.OwnerTryPickupAll( fromIndex, out var picked ) || picked.IsEmpty )
			return false;

		var held = new InventoryCursorStack();
		held.Set( picked.ResourceId, picked.Count );
		var movedAny = false;

		// Distribute across merge targets, then empties (finders only return slots with room).
		for ( var guard = 0; guard <= toHost.SlotCount && !held.IsEmpty; guard++ )
		{
			var probe = new InventorySlot { ResourceId = held.ResourceId, Count = held.Count };
			if ( !toHost.TryFindQuickMoveTarget( probe, -1, out var targetIndex ) )
				break;

			var beforeCount = held.Count;
			if ( !toHost.OwnerTryPlaceHeld( targetIndex, ref held ) )
				break;

			// No progress (e.g. destination rejected the merge) — stop instead of looping.
			if ( !held.IsEmpty && held.Count >= beforeCount )
				break;

			movedAny = true;
		}

		if ( !held.IsEmpty )
			fromHost.OwnerTryPlaceHeld( fromIndex, ref held );

		// Take-only sources (death loot bag) refuse the return — never let the remainder evaporate.
		if ( !held.IsEmpty && _inventory is not null )
			_inventory.OwnerTryAbsorbCursorStackIntoBag( ref held );

		if ( !held.IsEmpty )
			HeldStackWorldDrop.TryDropAtPlayer( GameObject, ref held );

		return movedAny;
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
		if ( IsOverPlayerDropZone( GetDropProbeScreenPosition() ) )
		{
			_dropHoverSlot = null;
			return;
		}

		var hover = FindSlotAtScreenPosition( GetDropProbeScreenPosition() );
		if ( hover is not null )
			_dropHoverSlot = hover;
	}

	InventorySlotPanel ResolveDropTargetSlot()
	{
		if ( IsOverPlayerDropZone( GetDropProbeScreenPosition() ) )
			return null;

		var hit = FindSlotAtScreenPosition( GetDropProbeScreenPosition() );
		if ( hit is not null )
			return hit;

		return _dropHoverSlot;
	}

	Vector2 GetDropProbeScreenPosition() => InventoryScreenPointer.GetMenuOrMousePosition();

	InventorySlotPanel FindSlotAtScreenPosition( Vector2 screenPosition )
	{
		for ( var i = _slots.Count - 1; i >= 0; i-- )
		{
			var slot = _slots[i];
			if ( slot is null || !slot.IsValid() )
				continue;

			if ( slot.GridHost?.GridId == "paperdoll" && slot.SlotIndex == (int)EquipmentSlot.MainHand )
				continue;

			if ( IsClosedContainerSlot( slot ) )
				continue;

			if ( SlotContainsScreenPoint( slot, screenPosition ) )
				return slot;
		}

		return null;
	}

	static bool SlotContainsScreenPoint( InventorySlotPanel slot, Vector2 screenPosition )
	{
		if ( slot is null || !slot.IsValid() )
			return false;

		// Panel-local designer size — avoids empty Box.Rect rejecting soft-cursor pickups.
		var local = ResolveSlotDesignerSize( slot );
		return PanelContainsScreenPointLocal( slot, screenPosition, new Vector2( local, local ) );
	}

	static float ResolveSlotDesignerSize( InventorySlotPanel slot )
	{
		if ( slot.IsHotbarSlot )
			return HotbarHud.SlotSize;

		if ( slot.GridHost?.GridId == "paperdoll" )
			return EquipmentPaperdollSection.SlotSize;

		return InventoryMenuSection.SlotSize;
	}

	static bool PanelContainsScreenPoint( Panel panel, Vector2 screenPosition, Vector2? sizeOverride = null )
	{
		if ( panel is null || !panel.IsValid() )
			return false;

		var scale = MathF.Max( 0.001f, panel.ScaleToScreen );
		Vector2 localSize;
		if ( sizeOverride is { } sized && sized.x > 0f && sized.y > 0f )
			localSize = new Vector2( sized.x / scale, sized.y / scale );
		else
		{
			var rect = panel.Box.Rect;
			localSize = rect.Width > 1f && rect.Height > 1f
				? new Vector2( rect.Width / scale, rect.Height / scale )
				: new Vector2( InventoryMenuSection.SlotSize, InventoryMenuSection.SlotSize );
		}

		return PanelContainsScreenPointLocal( panel, screenPosition, localSize );
	}

	static bool PanelContainsScreenPointLocal( Panel panel, Vector2 screenPosition, Vector2 localSize )
	{
		if ( panel is null || !panel.IsValid() )
			return false;

		var topLeft = panel.PanelPositionToScreenPosition( Vector2.Zero );
		var bottomRight = panel.PanelPositionToScreenPosition( localSize );

		if ( bottomRight.x < topLeft.x )
			(topLeft.x, bottomRight.x) = (bottomRight.x, topLeft.x);
		if ( bottomRight.y < topLeft.y )
			(topLeft.y, bottomRight.y) = (bottomRight.y, topLeft.y);

		if ( bottomRight.x - topLeft.x > 1f && bottomRight.y - topLeft.y > 1f
		     && screenPosition.x >= topLeft.x && screenPosition.x <= bottomRight.x
		     && screenPosition.y >= topLeft.y && screenPosition.y <= bottomRight.y )
			return true;

		var rect = panel.Box.Rect;
		if ( rect.Width <= 1f || rect.Height <= 1f )
			return false;

		return screenPosition.x >= rect.Left && screenPosition.x <= rect.Right
		       && screenPosition.y >= rect.Top && screenPosition.y <= rect.Bottom;
	}

	static Vector2 GetPanelScreenSize( Panel panel )
	{
		var rect = panel.Box.Rect;
		var w = rect.Right - rect.Left;
		var h = rect.Bottom - rect.Top;
		if ( w > 0f && h > 0f )
			return new Vector2( w, h );

		var scale = panel.ScaleToScreen;
		if ( scale > 0.001f )
			return new Vector2( 64f * scale, 64f * scale );

		return new Vector2( 64f, 64f );
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

		var ghostTopLeftScreen = InventoryScreenPointer.GetMenuOrMousePosition() - _grabOffset;
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

		// MainHand is display-only (mirrors selected hotbar). Not a drag/place destination.
		if ( slot.GridHost.GridId == "paperdoll" && slot.SlotIndex == (int)EquipmentSlot.MainHand )
			return false;

		if ( IsClosedContainerSlot( slot ) )
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
