using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Centered world map panel — TerrainWorld biome preview + stream marker.</summary>
public sealed class MapMenuSection : IPlayerMenuSection
{
	public string SectionId => "map";

	readonly TerrainWorldMapFace _face = new();
	Panel _sectionRoot;
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

		var hint = new Label { Parent = header, Text = "World stream position" };
		hint.Style.FontColor = new Color( 0.55f, 0.58f, 0.64f );
		hint.Style.FontSize = Length.Pixels( 13f );

		_face.Build( _sectionRoot, sizePixels: 0f, fillParent: true );
		UpdateVisibility();
	}

	public void Refresh() { }

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
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

		_face.Tick();
	}

	public void OnMenuGlobalMouseUp() { }

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}
}
