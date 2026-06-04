using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Centered world map panel (placeholder image until exploration is implemented).</summary>
public sealed class MapMenuSection : IPlayerMenuSection
{
	public string SectionId => "map";

	Panel _sectionRoot;
	Panel _mapImage;
	Label _placeholderLabel;
	bool _menuOpen;
	bool _panelVisible;

	public void Build( Panel menuColumn )
	{
		_sectionRoot = new Panel { Parent = menuColumn };
		_sectionRoot.Style.Set( "position", "relative" );
		_sectionRoot.Style.Width = Length.Percent( 100 );
		_sectionRoot.Style.Height = Length.Percent( 100 );
		_sectionRoot.Style.Set( "pointer-events", "auto" );
		_sectionRoot.Style.Set( "flex-direction", "column" );
		_sectionRoot.Style.Set( "align-items", "stretch" );
		_sectionRoot.Style.PaddingTop = Length.Pixels( 10f );
		_sectionRoot.Style.PaddingBottom = Length.Pixels( 10f );
		_sectionRoot.Style.PaddingLeft = Length.Pixels( 12f );
		_sectionRoot.Style.PaddingRight = Length.Pixels( 12f );
		_sectionRoot.Style.BackgroundColor = new Color( 0.03f, 0.04f, 0.06f, 0.92f );
		_sectionRoot.Style.Set( "border-radius", "10px" );
		_sectionRoot.Style.Set( "border-width", "1px" );
		_sectionRoot.Style.Set( "border-color", "#3a4250" );

		var header = new Panel { Parent = _sectionRoot };
		header.Style.Set( "flex-direction", "row" );
		header.Style.Set( "align-items", "center" );
		header.Style.Set( "justify-content", "space-between" );
		header.Style.Set( "width", "100%" );
		header.Style.Set( "margin-bottom", "8px" );
		header.Style.Set( "flex-shrink", "0" );

		var title = new Label { Parent = header, Text = "Map" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( CraftingMenuSection.CraftingTitleFontSize );

		var hint = new Label { Parent = header, Text = "Exploration coming soon" };
		hint.Style.FontColor = new Color( 0.55f, 0.58f, 0.64f );
		hint.Style.FontSize = Length.Pixels( 13f );

		_mapImage = new Panel { Parent = _sectionRoot };
		_mapImage.Style.Set( "position", "relative" );
		_mapImage.Style.Set( "flex-grow", "1" );
		_mapImage.Style.Width = Length.Percent( 100 );
		_mapImage.Style.Set( "min-height", "280px" );
		_mapImage.Style.Set( "overflow", "hidden" );
		_mapImage.Style.Set( "border-radius", "6px" );
		_mapImage.Style.Set( "border-width", "1px" );
		_mapImage.Style.Set( "border-color", "#2e3540" );
		_mapImage.Style.BackgroundColor = new Color( 0.12f, 0.22f, 0.32f, 1f );
		_mapImage.Style.Set( "background-size", "cover" );
		_mapImage.Style.Set( "background-repeat", "no-repeat" );
		_mapImage.Style.Set( "background-position", "center" );

		_placeholderLabel = new Label { Parent = _mapImage, Text = "World Map" };
		_placeholderLabel.Style.Set( "position", "absolute" );
		_placeholderLabel.Style.Set( "width", "100%" );
		_placeholderLabel.Style.Set( "height", "100%" );
		_placeholderLabel.Style.Set( "align-items", "center" );
		_placeholderLabel.Style.Set( "justify-content", "center" );
		_placeholderLabel.Style.FontColor = new Color( 0.75f, 0.78f, 0.82f, 0.35f );
		_placeholderLabel.Style.FontSize = Length.Pixels( 28f );
		_placeholderLabel.Style.Set( "pointer-events", "none" );

		ApplyMapImage();
		UpdateVisibility();
	}

	void ApplyMapImage()
	{
		if ( _mapImage is null )
			return;

		_mapImage.Style.BackgroundImage = null;
		_mapImage.Style.Set( "background-image", "none" );

		if ( _placeholderLabel is not null )
			_placeholderLabel.Style.Set( "display", "flex" );
	}

	public void Refresh() { }

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
			ApplyMapImage();

		UpdateVisibility();
	}

	public void SetPanelVisible( bool visible )
	{
		_panelVisible = visible;
		UpdateVisibility();
	}

	public void TickMenu( bool menuOpen ) { }

	public void OnMenuGlobalMouseUp() { }

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}
}
