using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Always-visible 10-slot hotbar at the bottom of the screen.</summary>
public sealed class HotbarHud
{
	public const float Scale = 1.1f;
	public const float SlotSize = 52f * Scale;
	public const float SlotGap = 4f * Scale;
	public const float KeyHintFontSize = 11f * Scale;
	public const float CountFontSize = 13f * Scale;

	static readonly string[] KeyHints = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };

	readonly List<SlotUi> _slots = new();

	Panel _host;
	PlayerHotbar _hotbar;
	PlayerInventoryInteraction _interaction;
	PlayerHotbarGridHost _gridHost;
	PlayerAmmoPreference _ammoPreference;
	bool _built;
	int _lastActiveSlot = -1;

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
		_host.Style.Set( "gap", $"{SlotGap}px" );
		_host.Style.Set( "align-items", "center" );
		_host.Style.Set( "justify-content", "center" );
		_host.Style.Set( "pointer-events", "none" );
		_host.Style.Set( "z-index", "2000" );

		_slots.Clear();
		for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
		{
			var slotPanel = new InventorySlotPanel( i, _gridHost, interaction ) { Parent = _host };
			slotPanel.Style.Width = Length.Pixels( SlotSize );
			slotPanel.Style.Height = Length.Pixels( SlotSize );
			slotPanel.Style.Set( "pointer-events", "auto" );
			slotPanel.Style.Set( "flex-shrink", "0" );
			slotPanel.Style.Set( "position", "relative" );
			slotPanel.Style.Set( "box-sizing", "border-box" );
			slotPanel.Style.BackgroundColor = new Color( 0.08f, 0.09f, 0.11f, 0.94f );
			slotPanel.Style.Set( "border-width", "2px" );
			slotPanel.Style.Set( "border-color", "#2a3140" );
			slotPanel.Style.Set( "border-radius", "5px" );
			slotPanel.Style.Set( "overflow", "hidden" );

			interaction?.RegisterSlot( slotPanel );

			var keyHint = new Label { Parent = slotPanel, Text = KeyHints[i] };
			keyHint.Style.Set( "position", "absolute" );
			keyHint.Style.Set( "right", "2px" );
			keyHint.Style.Set( "top", "1px" );
			keyHint.Style.FontColor = new Color( 0.75f, 0.78f, 0.82f, 0.9f );
			keyHint.Style.FontSize = Length.Pixels( KeyHintFontSize );
			keyHint.Style.Set( "text-shadow", "1px 1px 2px rgba(0,0,0,0.85)" );
			keyHint.Style.Set( "pointer-events", "none" );

			_slots.Add( CreateSlotUi( slotPanel ) );
		}

		_hotbar.HotbarChanged += OnHotbarChanged;
		_hotbar.ActiveSlotChanged += OnActiveSlotChanged;
		_ammoPreference = _hotbar.Components.Get<PlayerAmmoPreference>();
		if ( _ammoPreference is not null )
			_ammoPreference.PreferenceChanged += OnHotbarChanged;
		_built = true;
		_interaction?.SetHotbarHudDisplayed( true );
		Refresh();
	}

	public void Dispose()
	{
		if ( _hotbar is not null )
		{
			_hotbar.HotbarChanged -= OnHotbarChanged;
			_hotbar.ActiveSlotChanged -= OnActiveSlotChanged;
		}

		if ( _ammoPreference is not null )
			_ammoPreference.PreferenceChanged -= OnHotbarChanged;
	}

	void OnHotbarChanged() => Refresh();

	void OnActiveSlotChanged( int index ) => RefreshActiveHighlight( index );

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

	public void Refresh()
	{
		if ( !_built || _hotbar is null )
			return;

		for ( var i = 0; i < _slots.Count; i++ )
			ApplySlot( _slots[i], i );

		RefreshActiveHighlight( _hotbar.ActiveSlotIndex );
	}

	void RefreshActiveHighlight( int activeIndex )
	{
		// Always re-apply borders — Refresh() resets backgrounds for preferred ammo.
		if ( _lastActiveSlot >= 0 && _lastActiveSlot < _slots.Count && _lastActiveSlot != activeIndex )
			SetSlotHighlighted( _slots[_lastActiveSlot].Root, false );

		_lastActiveSlot = activeIndex;
		if ( activeIndex >= 0 && activeIndex < _slots.Count )
			SetSlotHighlighted( _slots[activeIndex].Root, true );
	}

	static void SetSlotHighlighted( Panel slotRoot, bool active )
	{
		if ( slotRoot is null || !slotRoot.IsValid() )
			return;

		slotRoot.Style.Set( "border-color", active ? "#c9a227" : "#2a3140" );
		slotRoot.Style.Set( "box-shadow", active ? "0 0 10px rgba(201,162,39,0.45)" : "none" );
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

		if ( ui.LastResourceId == resourceId && ui.LastCount == count && ui.LastIconPath == iconPath + ghostKey
		     && ui.LastPreferred == preferred )
			return;

		ui.LastResourceId = resourceId;
		ui.LastCount = count;
		ui.LastIconPath = iconPath + ghostKey;
		ui.LastPreferred = preferred;

		ui.Root.Style.BackgroundColor = preferred
			? new Color( 0.42f, 0.42f, 0.45f, 0.95f )
			: new Color( 0.08f, 0.09f, 0.11f, 0.94f );

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

		public SlotUi( Panel root, Panel iconPanel, Label countLabel )
		{
			Root = root;
			IconPanel = iconPanel;
			CountLabel = countLabel;
		}
	}
}
