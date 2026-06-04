using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Centered skill web + right-side detail panel for the skills menu page.</summary>
public sealed class SkillsMenuSection : IPlayerMenuSection
{
	public const float NodeSize = 56f;
	public const float DetailTitleFontSize = 22f;
	public const float DetailBodyFontSize = 16f;

	public string SectionId => "skills";

	readonly Panel _detailHost;
	readonly Dictionary<string, SkillNodePanel> _nodes = new( StringComparer.OrdinalIgnoreCase );
	readonly List<Panel> _connectors = new();

	Panel _webRoot;
	Panel _webCanvas;
	Panel _detailRoot;
	Label _detailTitle;
	Label _detailBody;
	Label _detailHint;

	string _selectedSkillId;
	bool _menuOpen;
	bool _panelVisible;

	public SkillsMenuSection( Panel detailHost )
	{
		_detailHost = detailHost;
	}

	public void Build( Panel webHost )
	{
		SkillCatalog.EnsureLoaded();

		_webRoot = new Panel { Parent = webHost };
		_webRoot.Style.Set( "position", "relative" );
		_webRoot.Style.Width = Length.Percent( 100 );
		_webRoot.Style.Height = Length.Percent( 100 );
		_webRoot.Style.Set( "pointer-events", "auto" );
		_webRoot.Style.Set( "flex-direction", "column" );
		_webRoot.Style.Set( "align-items", "stretch" );
		_webRoot.Style.PaddingTop = Length.Pixels( 12f );
		_webRoot.Style.PaddingBottom = Length.Pixels( 12f );
		_webRoot.Style.PaddingLeft = Length.Pixels( 14f );
		_webRoot.Style.PaddingRight = Length.Pixels( 14f );
		_webRoot.Style.BackgroundColor = new Color( 0.04f, 0.05f, 0.07f, 0.9f );
		_webRoot.Style.Set( "border-radius", "10px" );
		_webRoot.Style.Set( "border-width", "1px" );
		_webRoot.Style.Set( "border-color", "#3a4250" );

		var title = new Label { Parent = _webRoot, Text = "Skills" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( CraftingMenuSection.CraftingTitleFontSize );
		title.Style.Set( "margin-bottom", "8px" );
		title.Style.Set( "text-align", "center" );

		_webCanvas = new Panel { Parent = _webRoot };
		_webCanvas.Style.Set( "position", "relative" );
		_webCanvas.Style.Set( "flex-grow", "1" );
		_webCanvas.Style.Width = Length.Percent( 100 );
		_webCanvas.Style.Set( "min-height", "320px" );
		_webCanvas.Style.Set( "overflow", "hidden" );
		_webCanvas.Style.Set( "pointer-events", "auto" );

		BuildDetailPanel();
		BuildWeb();
		UpdateVisibility();
	}

	void BuildDetailPanel()
	{
		if ( _detailHost is null )
			return;

		_detailRoot = new Panel { Parent = _detailHost };
		_detailRoot.Style.Set( "position", "relative" );
		_detailRoot.Style.Width = Length.Percent( 100 );
		_detailRoot.Style.Height = Length.Percent( 100 );
		_detailRoot.Style.Set( "pointer-events", "auto" );
		_detailRoot.Style.Set( "flex-direction", "column" );
		_detailRoot.Style.Set( "gap", "10px" );
		_detailRoot.Style.PaddingTop = Length.Pixels( 16f );
		_detailRoot.Style.PaddingBottom = Length.Pixels( 16f );
		_detailRoot.Style.PaddingLeft = Length.Pixels( 16f );
		_detailRoot.Style.PaddingRight = Length.Pixels( 16f );
		_detailRoot.Style.BackgroundColor = new Color( 0.05f, 0.06f, 0.08f, 0.9f );
		_detailRoot.Style.Set( "border-radius", "10px" );
		_detailRoot.Style.Set( "border-width", "1px" );
		_detailRoot.Style.Set( "border-color", "#3a4250" );

		_detailTitle = new Label { Parent = _detailRoot, Text = "Select a skill" };
		_detailTitle.Style.FontColor = Color.White;
		_detailTitle.Style.FontSize = Length.Pixels( DetailTitleFontSize );
		_detailTitle.Style.Set( "white-space", "normal" );

		_detailBody = new Label { Parent = _detailRoot, Text = "Click a node in the web to read its description." };
		_detailBody.Style.FontColor = new Color( 0.82f, 0.84f, 0.88f );
		_detailBody.Style.FontSize = Length.Pixels( DetailBodyFontSize );
		_detailBody.Style.Set( "white-space", "normal" );

		_detailHint = new Label { Parent = _detailRoot, Text = "Progression hooks coming soon." };
		_detailHint.Style.FontColor = new Color( 0.55f, 0.58f, 0.64f );
		_detailHint.Style.FontSize = Length.Pixels( 13f );
		_detailHint.Style.Set( "white-space", "normal" );
	}

	void BuildWeb()
	{
		if ( _webCanvas is null )
			return;

		_webCanvas.DeleteChildren();
		_nodes.Clear();
		_connectors.Clear();

		SkillCatalog.ForEachGraphLink( ( parentId, childId ) =>
		{
			var parent = SkillCatalog.Get( parentId );
			var child = SkillCatalog.Get( childId );
			if ( parent is null || child is null )
				return;

			AddConnector( parent.X, parent.Y, child.X, child.Y );
		} );

		var skills = SkillCatalog.All;
		for ( var i = 0; i < skills.Count; i++ )
			AddNode( skills[i] );

		if ( skills.Count > 0 )
			SelectSkill( skills[0].Id );
	}

	void AddConnector( float x0, float y0, float x1, float y1 )
	{
		var dx = x1 - x0;
		var dy = y1 - y0;
		var length = MathF.Sqrt( dx * dx + dy * dy );
		if ( length < 0.01f )
			return;

		var angleDeg = MathF.Atan2( dy, dx ) * (180f / MathF.PI);
		var midX = (x0 + x1) * 0.5f;
		var midY = (y0 + y1) * 0.5f;

		var line = new Panel { Parent = _webCanvas };
		line.Style.Set( "position", "absolute" );
		line.Style.Set( "left", $"{midX * 100f}%" );
		line.Style.Set( "top", $"{midY * 100f}%" );
		line.Style.Width = Length.Pixels( length * 480f );
		line.Style.Height = Length.Pixels( 2f );
		line.Style.Set( "transform-origin", "center center" );
		line.Style.Set( "transform", $"translate(-50%, -50%) rotate({angleDeg:0.##}deg)" );
		line.Style.BackgroundColor = new Color( 0.45f, 0.52f, 0.62f, 0.55f );
		line.Style.Set( "pointer-events", "none" );
		line.Style.Set( "z-index", "1" );
		_connectors.Add( line );
	}

	void AddNode( SkillDefinition skill )
	{
		if ( skill is null || string.IsNullOrWhiteSpace( skill.Id ) )
			return;

		var node = new SkillNodePanel( skill.Id, this ) { Parent = _webCanvas };
		node.Style.Set( "position", "absolute" );
		node.Style.Width = Length.Pixels( NodeSize );
		node.Style.Height = Length.Pixels( NodeSize );
		node.Style.Set( "margin-left", $"{-NodeSize * 0.5f}px" );
		node.Style.Set( "margin-top", $"{-NodeSize * 0.5f}px" );
		node.Style.Set( "left", $"{skill.X * 100f}%" );
		node.Style.Set( "top", $"{skill.Y * 100f}%" );
		node.Style.Set( "z-index", "4" );
		node.Style.Set( "pointer-events", "auto" );
		node.Style.Set( "box-sizing", "border-box" );
		node.Style.BackgroundColor = new Color( 0.14f, 0.16f, 0.20f, 0.95f );
		node.Style.Set( "border-width", "2px" );
		node.Style.Set( "border-color", "#5a6478" );
		node.Style.Set( "border-radius", "8px" );
		node.Style.Set( "overflow", "hidden" );

		var icon = new Panel { Parent = node };
		icon.Style.Set( "position", "absolute" );
		icon.Style.Set( "left", "4px" );
		icon.Style.Set( "top", "4px" );
		icon.Style.Set( "right", "4px" );
		icon.Style.Set( "bottom", "4px" );
		icon.Style.Set( "background-size", "contain" );
		icon.Style.Set( "background-repeat", "no-repeat" );
		icon.Style.Set( "background-position", "center" );
		icon.Style.Set( "pointer-events", "none" );
		MenuUiTextures.ApplyBackground( icon, skill.Icon );

		_nodes[skill.Id] = node;
	}

	public void SelectSkill( string skillId )
	{
		_selectedSkillId = skillId;
		Refresh();
	}

	public void Refresh()
	{
		UpdateNodeHighlights();

		if ( _detailTitle is null || _detailBody is null )
			return;

		var skill = SkillCatalog.Get( _selectedSkillId );
		if ( skill is null )
		{
			_detailTitle.Text = "Select a skill";
			_detailBody.Text = "Click a node in the web to read its description.";
			return;
		}

		_detailTitle.Text = skill.DisplayName;
		_detailBody.Text = string.IsNullOrWhiteSpace( skill.Description )
			? "No description yet."
			: skill.Description;

		if ( skill.Parents is { Count: > 0 } )
			_detailBody.Text += $"\n\nParents: {FormatSkillNames( skill.Parents )}";

		if ( skill.Children is { Count: > 0 } )
			_detailBody.Text += $"\n\nChildren: {FormatSkillNames( skill.Children )}";
	}

	static string FormatSkillNames( List<string> ids )
	{
		if ( ids is null || ids.Count == 0 )
			return "—";

		var names = new List<string>();
		for ( var i = 0; i < ids.Count; i++ )
		{
			var linked = SkillCatalog.Get( ids[i] );
			names.Add( linked?.DisplayName ?? ids[i] );
		}

		return string.Join( ", ", names );
	}

	void UpdateNodeHighlights()
	{
		foreach ( var pair in _nodes )
		{
			var node = pair.Value;
			if ( node is null || !node.IsValid() )
				continue;

			var selected = string.Equals( pair.Key, _selectedSkillId, StringComparison.OrdinalIgnoreCase );
			node.Style.Set( "border-color", selected ? "#9ec5ff" : "#5a6478" );
			node.Style.Set( "border-width", selected ? "3px" : "2px" );
			node.Style.BackgroundColor = selected
				? new Color( 0.22f, 0.28f, 0.36f, 0.98f )
				: new Color( 0.14f, 0.16f, 0.20f, 0.95f );
		}
	}

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
		{
			SkillCatalog.ForceReload();
			BuildWeb();
		}

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
		var show = _menuOpen && _panelVisible;
		if ( _webRoot is not null )
			_webRoot.Style.Set( "display", show ? "flex" : "none" );
		if ( _detailRoot is not null )
			_detailRoot.Style.Set( "display", show ? "flex" : "none" );
	}
}
