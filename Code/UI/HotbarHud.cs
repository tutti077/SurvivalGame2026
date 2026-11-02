using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Always-visible hotbar at the bottom of the screen. Two banks of ten (<see cref="PlayerHotbar.BankSize"/>):
/// the bank on screen is <see cref="PlayerHotbar.ActiveBank"/> (Alt swaps), two dots to its left say which one
/// is up, and the inventory / equipment pages show both banks stacked (<see cref="SetShowBothBanks"/>). The gold
/// square sits on the two SELECTED slots — <see cref="PlayerHotbar.PrimarySlotIndex"/> and
/// <see cref="PlayerHotbar.OffHandSlotIndex"/> — so a spear + shield pair reads as two lit slots; pressing a lit
/// slot's key again unequips it and the square goes dark (Mark 2026-11-02).
/// </summary>
public sealed class HotbarHud
{
	public const float Scale = 1.1f;
	public const float SlotSize = 52f * Scale;
	public const float SlotGap = 4f * Scale;
	public const float CountFontSize = 13f * Scale;
	const float DotSize = 9f * Scale;

	static readonly Color SlotBackground = new( 0.08f, 0.09f, 0.11f, 0.94f );
	const string BorderIdle = "#2a3140";
	const string BorderActive = "#c9a227";

	readonly List<SlotUi> _slots = new();
	readonly Panel[] _bankRows = new Panel[PlayerHotbar.BankCount];
	readonly Panel[] _bankDots = new Panel[PlayerHotbar.BankCount];

	Panel _host;
	PlayerHotbar _hotbar;
	PlayerInventoryInteraction _interaction;
	PlayerHotbarGridHost _gridHost;
	PlayerAmmoPreference _ammoPreference;
	bool _built;
	bool _showBothBanks;
	int _lastPrimarySlot = -1;
	int _lastOffHandSlot = -1;

	public void Build( Panel root, PlayerHotbar hotbar, PlayerInventoryInteraction interaction )
	{
		if ( _built || hotbar is null )
			return;

		_hotbar = hotbar;
		_interaction = interaction;
		_gridHost = new PlayerHotbarGridHost( hotbar );

		_host = new HotbarHostPanel { Parent = root };
		_host.Style.Set( "position", "absolute" );
		_host.Style.Set( "left", "0" );
		_host.Style.Set( "right", "0" );
		_host.Style.Set( "bottom", "18px" );
		_host.Style.Set( "flex-direction", "row" );
		_host.Style.Set( "gap", $"{SlotGap * 3f}px" );
		_host.Style.Set( "align-items", "flex-end" );
		_host.Style.Set( "justify-content", "center" );
		_host.Style.Set( "pointer-events", "none" );
		_host.Style.Set( "z-index", "2000" );

		// Bank dots: top dot = bank 1, bottom dot = bank 0, matching the stacked rows.
		var dotsHost = new HotbarHostPanel { Parent = _host };
		dotsHost.Style.Set( "flex-direction", "column" );
		dotsHost.Style.Set( "justify-content", "center" );
		dotsHost.Style.Set( "align-items", "center" );
		dotsHost.Style.Set( "gap", $"{SlotGap * 2f}px" );
		dotsHost.Style.Set( "height", $"{SlotSize}px" );
		dotsHost.Style.Set( "pointer-events", "none" );
		for ( var bank = PlayerHotbar.BankCount - 1; bank >= 0; bank-- )
		{
			var dot = new Panel { Parent = dotsHost };
			dot.Style.Width = Length.Pixels( DotSize );
			dot.Style.Height = Length.Pixels( DotSize );
			dot.Style.Set( "border-radius", "50%" );
			dot.Style.Set( "border-width", "2px" );
			dot.Style.Set( "pointer-events", "none" );
			_bankDots[bank] = dot;
		}

		// Bank rows, bank 1 above bank 0 (bank 0 is the one on screen by default, so it stays at the bottom).
		var banksHost = new HotbarHostPanel { Parent = _host };
		banksHost.Style.Set( "flex-direction", "column" );
		banksHost.Style.Set( "gap", $"{SlotGap}px" );
		banksHost.Style.Set( "pointer-events", "none" );

		_slots.Clear();
		for ( var bank = PlayerHotbar.BankCount - 1; bank >= 0; bank-- )
		{
			var row = new HotbarHostPanel { Parent = banksHost };
			row.Style.Set( "flex-direction", "row" );
			row.Style.Set( "gap", $"{SlotGap}px" );
			row.Style.Set( "pointer-events", "none" );
			_bankRows[bank] = row;
		}

		for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
		{
			var slotPanel = new InventorySlotPanel( i, _gridHost, interaction ) { Parent = _bankRows[PlayerHotbar.BankOf( i )] };
			StyleSlot( slotPanel );
			slotPanel.Style.Set( "pointer-events", "auto" );
			interaction?.RegisterSlot( slotPanel );
			_slots.Add( CreateSlotUi( slotPanel ) );
		}

		_hotbar.HotbarChanged += OnHotbarChanged;
		_hotbar.ActiveSlotChanged += OnActiveSlotChanged;
		_hotbar.ActiveBankChanged += OnActiveBankChanged;
		_ammoPreference = _hotbar.Components.Get<PlayerAmmoPreference>();
		if ( _ammoPreference is not null )
			_ammoPreference.PreferenceChanged += OnHotbarChanged;
		_built = true;
		_interaction?.SetHotbarHudDisplayed( true );
		Refresh();
		RefreshBankVisibility();
	}

	public void Dispose()
	{
		if ( _hotbar is not null )
		{
			_hotbar.HotbarChanged -= OnHotbarChanged;
			_hotbar.ActiveSlotChanged -= OnActiveSlotChanged;
			_hotbar.ActiveBankChanged -= OnActiveBankChanged;
		}

		if ( _ammoPreference is not null )
			_ammoPreference.PreferenceChanged -= OnHotbarChanged;
	}

	void OnHotbarChanged() => Refresh();

	void OnActiveSlotChanged( int _ ) => RefreshSelectionHighlights();

	void OnActiveBankChanged( int _ ) => RefreshBankVisibility();

	public bool IsDisplayed { get; private set; } = true;

	public void SetVisible( bool visible )
	{
		if ( _host is null || !_host.IsValid() )
			return;

		IsDisplayed = visible;
		_interaction?.SetHotbarHudDisplayed( visible );

		_host.Style.Set( "display", visible ? "flex" : "none" );
		_host.Style.Set( "pointer-events", "none" );
	}

	/// <summary>Inventory / equipment pages: both banks stacked. Otherwise only the bank on screen.</summary>
	public void SetShowBothBanks( bool both )
	{
		if ( _showBothBanks == both )
			return;

		_showBothBanks = both;
		RefreshBankVisibility();
	}

	public void Refresh()
	{
		if ( !_built || _hotbar is null )
			return;

		for ( var i = 0; i < _slots.Count; i++ )
			ApplySlot( _slots[i], i );

		RefreshSelectionHighlights();
	}

	void RefreshBankVisibility()
	{
		if ( !_built || _hotbar is null )
			return;

		var active = _hotbar.ActiveBank;
		for ( var bank = 0; bank < PlayerHotbar.BankCount; bank++ )
		{
			var shown = _showBothBanks || bank == active;
			_bankRows[bank]?.Style.Set( "display", shown ? "flex" : "none" );

			var dot = _bankDots[bank];
			if ( dot is null || !dot.IsValid() )
				continue;

			dot.Style.Set( "background-color", bank == active ? BorderActive : "rgba(255,255,255,0.08)" );
			dot.Style.Set( "border-color", bank == active ? BorderActive : BorderIdle );
			dot.Style.Set( "box-shadow", bank == active ? "0 0 8px rgba(201,162,39,0.5)" : "none" );
		}
	}

	/// <summary>Gold square on the primary and the offhand selection; everything else idle. Re-applied after Refresh() too.</summary>
	void RefreshSelectionHighlights()
	{
		if ( !_built || _hotbar is null )
			return;

		var primary = _hotbar.PrimarySlotIndex;
		var offhand = _hotbar.OffHandSlotIndex;

		if ( _lastPrimarySlot >= 0 && _lastPrimarySlot < _slots.Count && _lastPrimarySlot != primary && _lastPrimarySlot != offhand )
			SetSlotHighlighted( _slots[_lastPrimarySlot].Root, false );
		if ( _lastOffHandSlot >= 0 && _lastOffHandSlot < _slots.Count && _lastOffHandSlot != primary && _lastOffHandSlot != offhand )
			SetSlotHighlighted( _slots[_lastOffHandSlot].Root, false );

		_lastPrimarySlot = primary;
		_lastOffHandSlot = offhand;
		if ( primary >= 0 && primary < _slots.Count )
			SetSlotHighlighted( _slots[primary].Root, true );
		if ( offhand >= 0 && offhand < _slots.Count )
			SetSlotHighlighted( _slots[offhand].Root, true );
	}

	static void SetSlotHighlighted( Panel slotRoot, bool active )
	{
		if ( slotRoot is null || !slotRoot.IsValid() )
			return;

		slotRoot.Style.Set( "border-color", active ? BorderActive : BorderIdle );
		slotRoot.Style.Set( "box-shadow", active ? "0 0 10px rgba(201,162,39,0.45)" : "none" );
	}

	static void StyleSlot( Panel slotPanel )
	{
		slotPanel.Style.Width = Length.Pixels( SlotSize );
		slotPanel.Style.Height = Length.Pixels( SlotSize );
		slotPanel.Style.Set( "flex-shrink", "0" );
		slotPanel.Style.Set( "position", "relative" );
		slotPanel.Style.Set( "box-sizing", "border-box" );
		slotPanel.Style.BackgroundColor = SlotBackground;
		slotPanel.Style.Set( "border-width", "2px" );
		slotPanel.Style.Set( "border-color", BorderIdle );
		slotPanel.Style.Set( "border-radius", "5px" );
		slotPanel.Style.Set( "overflow", "hidden" );
	}

	static SlotUi CreateSlotUi( Panel parent )
	{
		var inset = 3f * Scale;
		var icon = new Panel { Parent = parent };
		icon.Style.Set( "position", "absolute" );
		icon.Style.Set( "left", $"{inset}px" );
		icon.Style.Set( "top", $"{inset}px" );
		icon.Style.Set( "right", $"{inset}px" );
		icon.Style.Set( "bottom", $"{inset}px" );
		icon.Style.Set( "background-size", "contain" );
		icon.Style.Set( "background-repeat", "no-repeat" );
		icon.Style.Set( "background-position", "center" );
		icon.Style.Set( "display", "none" );
		icon.Style.Set( "pointer-events", "none" );

		var count = new Label { Parent = parent };
		count.Style.Set( "position", "absolute" );
		count.Style.Set( "right", "3px" );
		count.Style.Set( "bottom", "1px" );
		count.Style.Set( "padding-left", "3px" );
		count.Style.Set( "padding-right", "3px" );
		count.Style.Set( "padding-top", "1px" );
		count.Style.Set( "padding-bottom", "1px" );
		count.Style.Set( "background-color", "rgba(0,0,0,0.65)" );
		count.Style.Set( "border-radius", "3px" );
		count.Style.FontColor = Color.White;
		count.Style.FontSize = Length.Pixels( CountFontSize );
		count.Style.Set( "text-shadow", "1px 1px 2px black" );
		count.Style.Set( "display", "none" );
		count.Style.Set( "pointer-events", "none" );

		return new SlotUi( parent, icon, count );
	}

	void ApplySlot( SlotUi ui, int slotIndex )
	{
		var slot = _hotbar.GetSlot( slotIndex );
		var binding = _hotbar.GetBinding( slotIndex );
		var showGhost = slot.IsEmpty && !string.IsNullOrWhiteSpace( binding );
		var resourceId = showGhost ? binding : ( slot.IsEmpty ? string.Empty : slot.ResourceId ?? string.Empty );
		var count = slot.IsEmpty ? 0 : slot.Count;
		var iconPath = string.IsNullOrWhiteSpace( resourceId ) ? string.Empty : ResourceCatalog.GetIconPath( resourceId );
		var ghostKey = showGhost ? "|ghost|" : string.Empty;
		var preferred = !showGhost && IsPreferredAmmoStack( resourceId );

		var wear = slot.IsEmpty ? 0 : slot.Wear;
		if ( ui.LastResourceId == resourceId && ui.LastCount == count && ui.LastIconPath == iconPath + ghostKey
		     && ui.LastPreferred == preferred && ui.LastWear == wear )
			return;

		ui.LastResourceId = resourceId;
		ui.LastCount = count;
		ui.LastIconPath = iconPath + ghostKey;
		ui.LastPreferred = preferred;
		ui.LastWear = wear;

		ui.Root.Style.BackgroundColor = preferred
			? new Color( 0.42f, 0.42f, 0.45f, 0.95f )
			: SlotBackground;

		if ( showGhost )
		{
			ResourceCatalog.ApplyBindingGhostVisual( ui.IconPanel, ui.CountLabel, binding );
			return;
		}

		ui.IconPanel.Style.Set( "opacity", "1" );
		ResourceCatalog.ApplyStackVisual( ui.IconPanel, ui.CountLabel, slot );
	}

	bool IsPreferredAmmoStack( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) || _hotbar is null )
			return false;

		var pref = _hotbar.Components.Get<PlayerAmmoPreference>();
		return pref is not null && pref.IsPreferredAmmo( resourceId );
	}

	sealed class SlotUi
	{
		public Panel Root { get; }
		public Panel IconPanel { get; }
		public Label CountLabel { get; }
		public string LastResourceId { get; set; }
		public string LastIconPath { get; set; }
		public int LastCount { get; set; } = -1;
		public bool LastPreferred { get; set; }
		public int LastWear { get; set; } = -1;

		public SlotUi( Panel root, Panel iconPanel, Label countLabel )
		{
			Root = root;
			IconPanel = iconPanel;
			CountLabel = countLabel;
		}
	}
}
