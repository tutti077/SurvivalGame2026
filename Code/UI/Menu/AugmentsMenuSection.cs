using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Augments tab page: the same paper doll as the station (<see cref="AugmentPaperdollView"/>), view
/// only — what is installed, pending and open right now. No Enhance row, no Augment button, no bank;
/// sockets are a read-only grid so they hover for tooltips but never pick up or drop.
/// </summary>
public sealed class AugmentsMenuSection : IPlayerMenuSection
{
	const float HeaderFont = 20f;

	public string SectionId => "augments";

	static readonly Color PanelBg = new( 0.07f, 0.08f, 0.10f, 0.92f );

	readonly PlayerAugments _augments;
	readonly PlayerAugmentInstalledGridHost _viewHost;
	readonly AugmentPaperdollView _doll;

	Panel _sectionRoot;
	Label _hint;
	bool _menuOpen;
	bool _panelVisible;
	int _lastAugmentVersion = -1;

	public AugmentsMenuSection( PlayerAugments augments, PlayerInventory inventory, PlayerInventoryInteraction interaction )
	{
		_augments = augments;
		_viewHost = augments is not null && inventory is not null
			? new PlayerAugmentInstalledGridHost( augments, inventory, readOnly: true )
			: null;
		_doll = new AugmentPaperdollView( augments, _viewHost, interaction, interactive: false );

		if ( _viewHost is not null )
			interaction?.RegisterGrid( _viewHost );
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

		var title = new Label { Parent = _sectionRoot, Text = "Augments" };
		title.Style.FontColor = AugmentPaperdollView.TitleColor;
		title.Style.FontSize = Length.Pixels( HeaderFont );
		title.Style.Set( "width", "100%" );
		title.Style.Set( "text-align", "center" );
		title.Style.Set( "flex-shrink", "0" );
		title.Style.Set( "pointer-events", "none" );

		_doll.Build( _sectionRoot );

		_hint = new Label { Parent = _sectionRoot, Text = "Visit an augment station to enhance, install or remove augments." };
		_hint.Style.FontColor = AugmentPaperdollView.MutedColor;
		_hint.Style.FontSize = Length.Pixels( AugmentPaperdollView.SmallFont + 1f );
		_hint.Style.Set( "flex-shrink", "0" );
		_hint.Style.Set( "pointer-events", "none" );

		Refresh();
		UpdateVisibility();
	}

	public void Refresh()
	{
		_doll.Refresh();
		_lastAugmentVersion = _augments?.ContentsVersion ?? -1;
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

	public void OnMenuGlobalMouseUp() { }

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}
}
