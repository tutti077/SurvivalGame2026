using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Augments tab page: the same paper doll as the station (<see cref="AugmentPaperdollView"/>), view
/// only — what is installed, pending and open right now — plus the six key-bind slots (1–6) under
/// it. Drag a trigger augment from the doll into a bind slot to put it on that key; drag a bind out
/// to clear it. No Enhance row, no Augment button, no bank; sockets are a read-only grid so they
/// hover for tooltips but never pick up or drop.
/// </summary>
public sealed class AugmentsMenuSection : IPlayerMenuSection
{
	const float HeaderFont = 20f;

	public string SectionId => "augments";

	static readonly Color PanelBg = new( 0.07f, 0.08f, 0.10f, 0.92f );

	readonly PlayerAugments _augments;
	readonly PlayerInventoryInteraction _interaction;
	readonly PlayerAugmentInstalledGridHost _viewHost;
	readonly PlayerAugmentBindGridHost _bindHost;
	readonly AugmentPaperdollView _doll;
	readonly List<AugmentPaperdollView.SlotUi> _bindUi = new();
	readonly List<Panel> _bindSlotPanels = new();
	readonly List<Label> _bindKeyLabels = new();
	Panel _bindRow;

	// Column fit: bind title + slots + key labels + hint scale with the doll; page title, gaps and padding do not.
	const float ExtraScalableNominal = (AugmentPaperdollView.SmallFont + 1f) + AugmentPaperdollView.SlotSize + 2f + AugmentPaperdollView.SmallFont + (AugmentPaperdollView.SmallFont + 1f);
	const float FixedColumnHeight = HeaderFont + 8f + 4f * 8f + 20f;
	Label _bindTitle;
	Label _hint;

	Panel _sectionRoot;
	bool _menuOpen;
	bool _panelVisible;
	int _lastAugmentVersion = -1;

	public AugmentsMenuSection( PlayerAugments augments, PlayerInventory inventory, PlayerInventoryInteraction interaction )
	{
		_augments = augments;
		_interaction = interaction;
		_viewHost = augments is not null && inventory is not null
			? new PlayerAugmentInstalledGridHost( augments, inventory, readOnly: true )
			: null;
		_bindHost = augments is not null && inventory is not null
			? new PlayerAugmentBindGridHost( augments, inventory )
			: null;
		_doll = new AugmentPaperdollView( augments, _viewHost, interaction, interactive: false );

		if ( _viewHost is not null )
			interaction?.RegisterGrid( _viewHost );
		if ( _bindHost is not null )
			interaction?.RegisterGrid( _bindHost );

		if ( _augments is not null )
			_augments.BindsChanged += RefreshBinds;
	}

	public void Build( Panel parent )
	{
		_sectionRoot = new Panel { Parent = parent };
		_sectionRoot.Style.Set( "position", "relative" );
		_sectionRoot.Style.Set( "width", "60%" );
		_sectionRoot.Style.Set( "height", "100%" );
		_sectionRoot.Style.Set( "flex-direction", "column" );
		_sectionRoot.Style.Set( "align-items", "center" );
		_sectionRoot.Style.Set( "gap", "8px" );
		_sectionRoot.Style.Set( "padding", "10px" );
		_sectionRoot.Style.Set( "overflow", "hidden" );
		_sectionRoot.Style.BackgroundColor = PanelBg;
		_sectionRoot.Style.Set( "border-radius", "6px" );
		_sectionRoot.Style.Set( "pointer-events", "auto" );
		_sectionRoot.Style.Set( "display", "none" );

		AddTitle( "Augments" );

		_doll.Build( _sectionRoot );
		_doll.ScaleApplied += ApplyExtraScale;

		// Key binds 1–6.
		_bindTitle = new Label { Parent = _sectionRoot, Text = "Key binds — drag a trigger augment from the doll onto a key" };
		_bindTitle.Style.FontColor = AugmentPaperdollView.LabelColor;
		_bindTitle.Style.FontSize = Length.Pixels( AugmentPaperdollView.SmallFont + 1f );
		_bindTitle.Style.Set( "flex-shrink", "0" );
		_bindTitle.Style.Set( "pointer-events", "none" );

		_bindRow = new Panel { Parent = _sectionRoot };
		_bindRow.Style.Set( "flex-direction", "row" );
		_bindRow.Style.Set( "gap", $"{AugmentPaperdollView.SlotGap * 2f}px" );
		_bindRow.Style.Set( "flex-shrink", "0" );

		_bindUi.Clear();
		_bindSlotPanels.Clear();
		_bindKeyLabels.Clear();
		for ( var i = 0; i < PlayerAugments.BindCount; i++ )
		{
			var host = new Panel { Parent = _bindRow };
			host.Style.Set( "flex-direction", "column" );
			host.Style.Set( "align-items", "center" );
			host.Style.Set( "gap", "2px" );

			var slotPanel = new InventorySlotPanel( i, _bindHost, _interaction ) { Parent = host };
			AugmentPaperdollView.StyleSlot( slotPanel );
			_interaction?.RegisterSlot( slotPanel );
			_bindSlotPanels.Add( slotPanel );
			_bindUi.Add( AugmentPaperdollView.CreateSlotUi( slotPanel ) );

			var key = new Label { Parent = host, Text = (i + 1).ToString() };
			key.Style.FontColor = AugmentPaperdollView.LabelColor;
			key.Style.FontSize = Length.Pixels( AugmentPaperdollView.SmallFont );
			key.Style.Set( "pointer-events", "none" );
			_bindKeyLabels.Add( key );
		}

		_hint = new Label { Parent = _sectionRoot, Text = "Wheel augments are picked by holding C. Visit an augment station to enhance, install or remove augments." };
		_hint.Style.FontColor = AugmentPaperdollView.MutedColor;
		_hint.Style.FontSize = Length.Pixels( AugmentPaperdollView.SmallFont + 1f );
		_hint.Style.Set( "flex-shrink", "0" );
		_hint.Style.Set( "pointer-events", "none" );

		Refresh();
		UpdateVisibility();
	}

	void AddTitle( string text )
	{
		var title = new Label { Parent = _sectionRoot, Text = text };
		title.Style.FontColor = AugmentPaperdollView.TitleColor;
		title.Style.FontSize = Length.Pixels( HeaderFont );
		title.Style.Set( "width", "100%" );
		title.Style.Set( "text-align", "center" );
		title.Style.Set( "flex-shrink", "0" );
		title.Style.Set( "pointer-events", "none" );
	}

	/// <summary>Doll fit changed: the bind row, its labels and the hint follow the same scale.</summary>
	void ApplyExtraScale( float s )
	{
		if ( _bindTitle is not null && _bindTitle.IsValid() )
			_bindTitle.Style.FontSize = Length.Pixels( (AugmentPaperdollView.SmallFont + 1f) * s );
		if ( _hint is not null && _hint.IsValid() )
			_hint.Style.FontSize = Length.Pixels( (AugmentPaperdollView.SmallFont + 1f) * s );
		if ( _bindRow is not null && _bindRow.IsValid() )
			_bindRow.Style.Set( "gap", $"{AugmentPaperdollView.SlotGap * 2f * s:0.#}px" );

		for ( var i = 0; i < _bindSlotPanels.Count; i++ )
		{
			var slot = _bindSlotPanels[i];
			if ( slot is null || !slot.IsValid() )
				continue;

			slot.Style.Width = Length.Pixels( AugmentPaperdollView.SlotSize * s );
			slot.Style.Height = Length.Pixels( AugmentPaperdollView.SlotSize * s );
		}

		for ( var i = 0; i < _bindKeyLabels.Count; i++ )
		{
			if ( _bindKeyLabels[i] is { } key && key.IsValid() )
				key.Style.FontSize = Length.Pixels( AugmentPaperdollView.SmallFont * s );
		}
	}

	public void Refresh()
	{
		_doll.Refresh();
		RefreshBinds();
		_lastAugmentVersion = _augments?.ContentsVersion ?? -1;
	}

	void RefreshBinds()
	{
		for ( var i = 0; i < _bindUi.Count; i++ )
		{
			var stack = _bindHost?.GetSlot( i ) ?? InventorySlot.Empty;
			ResourceCatalog.ApplyStackVisual( _bindUi[i].IconPanel, _bindUi[i].CountLabel, stack );
		}
	}

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
			Refresh();

		UpdateVisibility();
	}

	public void SetPanelVisible( bool visible )
	{
		_panelVisible = visible;
		UpdateVisibility();
	}

	public void TickMenu( bool menuOpen )
	{
		if ( !menuOpen || !_panelVisible )
			return;

		if ( (_augments?.ContentsVersion ?? -1) != _lastAugmentVersion )
			Refresh();

		_doll.TickLayout( _sectionRoot, ExtraScalableNominal, FixedColumnHeight );
	}

	/// <summary>Overlay page drag (pointer + Attack1 held): click-drag on the preview spins the body.</summary>
	public void TickPointerDrag( Vector2 screenPos, bool held )
	{
		if ( !_menuOpen || !_panelVisible )
			return;

		_doll.TickPointerDrag( screenPos, held );
	}

	public void OnMenuGlobalMouseUp() { }

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}
}
