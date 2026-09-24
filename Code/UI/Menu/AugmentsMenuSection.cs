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

		// Key binds 1–6.
		var bindTitle = new Label { Parent = _sectionRoot, Text = "Key binds — drag a trigger augment from the doll onto a key" };
		bindTitle.Style.FontColor = AugmentPaperdollView.LabelColor;
		bindTitle.Style.FontSize = Length.Pixels( AugmentPaperdollView.SmallFont + 1f );
		bindTitle.Style.Set( "flex-shrink", "0" );
		bindTitle.Style.Set( "pointer-events", "none" );

		var bindRow = new Panel { Parent = _sectionRoot };
		bindRow.Style.Set( "flex-direction", "row" );
		bindRow.Style.Set( "gap", $"{AugmentPaperdollView.SlotGap * 2f}px" );
		bindRow.Style.Set( "flex-shrink", "0" );

		_bindUi.Clear();
		for ( var i = 0; i < PlayerAugments.BindCount; i++ )
		{
			var host = new Panel { Parent = bindRow };
			host.Style.Set( "flex-direction", "column" );
			host.Style.Set( "align-items", "center" );
			host.Style.Set( "gap", "2px" );

			var slotPanel = new InventorySlotPanel( i, _bindHost, _interaction ) { Parent = host };
			AugmentPaperdollView.StyleSlot( slotPanel );
			_interaction?.RegisterSlot( slotPanel );
			_bindUi.Add( AugmentPaperdollView.CreateSlotUi( slotPanel ) );

			var key = new Label { Parent = host, Text = (i + 1).ToString() };
			key.Style.FontColor = AugmentPaperdollView.LabelColor;
			key.Style.FontSize = Length.Pixels( AugmentPaperdollView.SmallFont );
			key.Style.Set( "pointer-events", "none" );
		}

		var hint = new Label { Parent = _sectionRoot, Text = "Wheel augments are picked by holding F. Visit an augment station to enhance, install or remove augments." };
		hint.Style.FontColor = AugmentPaperdollView.MutedColor;
		hint.Style.FontSize = Length.Pixels( AugmentPaperdollView.SmallFont + 1f );
		hint.Style.Set( "flex-shrink", "0" );
		hint.Style.Set( "pointer-events", "none" );

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
