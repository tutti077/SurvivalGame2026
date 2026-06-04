using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Left-side quests panel (same column/size as crafting): detail on top, quest list below.</summary>
public sealed class QuestMenuSection : IPlayerMenuSection
{
	public string SectionId => "quests";

	readonly List<QuestRowPanel> _rows = new();

	Panel _sectionRoot;
	Label _detailName;
	Panel _descriptionEntries;
	Panel _taskEntries;
	Panel _rewardsEntries;
	Panel _questList;

	string _selectedQuestId;
	bool _menuOpen;
	bool _panelVisible;

	public void Build( Panel menuColumn )
	{
		QuestCatalog.EnsureLoaded();

		_sectionRoot = new Panel { Parent = menuColumn };
		_sectionRoot.Style.Set( "position", "relative" );
		_sectionRoot.Style.Set( "z-index", "1" );
		_sectionRoot.Style.Set( "pointer-events", "auto" );
		_sectionRoot.Style.Set( "flex-direction", "column" );
		_sectionRoot.Style.Set( "align-items", "stretch" );
		_sectionRoot.Style.Set( "gap", $"{10f * CraftingMenuSection.LayoutScale}px" );
		_sectionRoot.Style.Width = Length.Percent( 100 );
		_sectionRoot.Style.MinHeight = Length.Pixels( CraftingMenuSection.MinSectionHeight );
		_sectionRoot.Style.MaxHeight = Length.Pixels( CraftingMenuSection.MinSectionHeight );
		_sectionRoot.Style.Set( "overflow", "hidden" );
		_sectionRoot.Style.Set( "flex-shrink", "0" );

		var title = new Label { Parent = _sectionRoot, Text = "Quests" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( CraftingMenuSection.CraftingTitleFontSize );
		title.Style.Set( "width", "100%" );

		var detail = new Panel { Parent = _sectionRoot };
		detail.Style.Set( "flex-direction", "column" );
		detail.Style.Set( "gap", $"{10f * CraftingMenuSection.LayoutScale}px" );
		detail.Style.Set( "width", "100%" );
		detail.Style.PaddingTop = Length.Pixels( 4f * CraftingMenuSection.LayoutScale );
		detail.Style.PaddingBottom = Length.Pixels( 8f * CraftingMenuSection.LayoutScale );
		detail.Style.Set( "border-bottom-width", "1px" );
		detail.Style.Set( "border-bottom-color", "#383d47" );
		detail.Style.Set( "flex-shrink", "1" );
		detail.Style.Set( "min-height", "0" );
		detail.Style.Set( "max-height", $"{CraftingMenuSection.DetailAreaMaxHeight}px" );
		detail.Style.Set( "overflow-y", "scroll" );

		_detailName = new Label { Parent = detail, Text = "Select a quest" };
		_detailName.Style.FontColor = Color.White;
		_detailName.Style.FontSize = Length.Pixels( CraftingMenuSection.ItemNameFontSize );
		_detailName.Style.Set( "white-space", "normal" );

		CreateDetailBlock( detail, "Description", out _descriptionEntries );
		CreateDetailBlock( detail, "Task", out _taskEntries );
		CreateDetailBlock( detail, "Rewards", out _rewardsEntries );

		_questList = new Panel { Parent = _sectionRoot };
		_questList.Style.Set( "flex-direction", "column" );
		_questList.Style.Set( "gap", $"{4f * CraftingMenuSection.LayoutScale}px" );
		_questList.Style.Set( "width", "100%" );
		_questList.Style.Set( "overflow-y", "scroll" );
		_questList.Style.Set( "max-height", $"{CraftingMenuSection.RecipeListMaxHeight}px" );
		_questList.Style.Set( "flex-shrink", "0" );

		BuildQuestRows();

		if ( QuestCatalog.All.Count > 0 )
			SelectQuest( QuestCatalog.All[0].Id );

		UpdateVisibility();
	}

	void BuildQuestRows()
	{
		if ( _questList is null )
			return;

		_questList.DeleteChildren();
		_rows.Clear();

		foreach ( var quest in QuestCatalog.All )
		{
			if ( quest is null || string.IsNullOrWhiteSpace( quest.Id ) )
				continue;

			var row = new QuestRowPanel
			{
				Parent = _questList,
				Section = this,
				QuestId = quest.Id
			};
			row.Style.Set( "flex-direction", "row" );
			row.Style.Set( "align-items", "center" );
			row.Style.Set( "width", "100%" );
			row.Style.PaddingTop = Length.Pixels( 6f * CraftingMenuSection.LayoutScale );
			row.Style.PaddingBottom = Length.Pixels( 6f * CraftingMenuSection.LayoutScale );
			row.Style.PaddingLeft = Length.Pixels( 8f * CraftingMenuSection.LayoutScale );
			row.Style.PaddingRight = Length.Pixels( 8f * CraftingMenuSection.LayoutScale );
			row.Style.BackgroundColor = new Color( 0.10f, 0.11f, 0.13f, 0.9f );
			row.Style.Set( "border-width", "1px" );
			row.Style.Set( "border-color", "#383d47" );
			row.Style.Set( "border-radius", "4px" );
			row.Style.Set( "pointer-events", "auto" );

			var name = new Label { Parent = row, Text = quest.DisplayName };
			name.Style.FontColor = Color.White;
			name.Style.FontSize = Length.Pixels( 15f * CraftingMenuSection.TextScale );
			name.Style.Set( "pointer-events", "none" );
			name.Style.Set( "white-space", "normal" );

			_rows.Add( row );
		}
	}

	public void SelectQuest( string questId )
	{
		_selectedQuestId = questId;
		Refresh();
	}

	public void Refresh()
	{
		UpdateRowHighlights();

		var quest = QuestCatalog.Get( _selectedQuestId );
		if ( quest is null )
		{
			if ( _detailName is not null )
				_detailName.Text = "Select a quest";
			PopulateLines( _descriptionEntries, Array.Empty<string>() );
			PopulateLines( _taskEntries, Array.Empty<string>() );
			PopulateLines( _rewardsEntries, Array.Empty<string>() );
			return;
		}

		if ( _detailName is not null )
			_detailName.Text = quest.DisplayName;

		PopulateLines( _descriptionEntries,
			string.IsNullOrWhiteSpace( quest.Description ) ? Array.Empty<string>() : new[] { quest.Description } );

		PopulateLines( _taskEntries,
			string.IsNullOrWhiteSpace( quest.Task ) ? Array.Empty<string>() : new[] { quest.Task } );

		PopulateLines( _rewardsEntries, quest.Rewards );
	}

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
		{
			QuestCatalog.ForceReload();
			BuildQuestRows();
			if ( QuestCatalog.All.Count > 0 && string.IsNullOrWhiteSpace( _selectedQuestId ) )
				SelectQuest( QuestCatalog.All[0].Id );
			else
				Refresh();
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
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}

	void UpdateRowHighlights()
	{
		for ( var i = 0; i < _rows.Count; i++ )
		{
			var row = _rows[i];
			if ( row is null || !row.IsValid() )
				continue;

			var selected = string.Equals( row.QuestId, _selectedQuestId, StringComparison.OrdinalIgnoreCase );
			row.Style.Set( "border-color", selected ? "#8ab4f8" : "#383d47" );
			row.Style.BackgroundColor = selected
				? new Color( 0.16f, 0.19f, 0.24f, 0.95f )
				: new Color( 0.10f, 0.11f, 0.13f, 0.9f );
		}
	}

	static Panel CreateDetailBlock( Panel parent, string headingText, out Panel entriesHost )
	{
		var block = new Panel { Parent = parent };
		block.Style.Set( "flex-direction", "column" );
		block.Style.Set( "gap", $"{4f * CraftingMenuSection.LayoutScale}px" );
		block.Style.Set( "width", "100%" );

		var heading = new Label { Parent = block, Text = headingText };
		heading.Style.FontColor = Color.White;
		heading.Style.FontSize = Length.Pixels( CraftingMenuSection.SectionHeaderFontSize );

		entriesHost = new Panel { Parent = block };
		entriesHost.Style.Set( "flex-direction", "column" );
		entriesHost.Style.Set( "gap", $"{3f * CraftingMenuSection.LayoutScale}px" );
		entriesHost.Style.Set( "width", "100%" );
		entriesHost.Style.PaddingLeft = Length.Pixels( 6f * CraftingMenuSection.LayoutScale );

		return block;
	}

	static void PopulateLines( Panel host, IReadOnlyList<string> lines )
	{
		if ( host is null )
			return;

		host.DeleteChildren();

		if ( lines is null || lines.Count == 0 )
		{
			AddLine( host, "—" );
			return;
		}

		for ( var i = 0; i < lines.Count; i++ )
			AddLine( host, lines[i] );
	}

	static void AddLine( Panel host, string text )
	{
		var label = new Label { Parent = host, Text = text };
		label.Style.FontColor = new Color( 0.82f, 0.84f, 0.88f );
		label.Style.FontSize = Length.Pixels( CraftingMenuSection.SectionEntryFontSize );
		label.Style.Set( "white-space", "normal" );
	}
}
