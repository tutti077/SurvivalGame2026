using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Left-side quests panel (same column/size as crafting): detail on top, quest list below.
/// Rows show Locked / Active / Done from <see cref="QuestTracker"/>. Locked main quests hide even their
/// title; side quests are always visible under a "Side Quests" header.
/// </summary>
public sealed class QuestMenuSection : IPlayerMenuSection
{
	public string SectionId => "quests";

	static readonly Color LockedText = new( 0.55f, 0.57f, 0.62f );
	static readonly Color ActiveText = new( 0.55f, 0.72f, 0.98f );
	static readonly Color DoneText = new( 0.55f, 0.85f, 0.55f );
	static readonly Color BodyText = new( 0.82f, 0.84f, 0.88f );

	/// <summary>Main quests unlock one after another; a locked quest reveals nothing, not even its name.</summary>
	const string HiddenName = "??????????????";

	readonly List<QuestRowPanel> _rows = new();

	Panel _sectionRoot;
	Label _title;
	Label _detailName;
	Label _detailStatus;
	Panel _descriptionBlock;
	Panel _descriptionEntries;
	Panel _taskBlock;
	Panel _taskEntries;
	Panel _objectivesBlock;
	Panel _objectivesEntries;
	Panel _rewardsBlock;
	Panel _rewardsEntries;
	Panel _questList;
	Panel _questContent;
	Panel _scrollTrack;
	Panel _scrollThumb;
	float _questScrollY;
	bool _draggingThumb;
	float _dragStartMouseY;
	float _dragStartScrollY;
	float _lastThumbTop = -1f;
	float _lastThumbHeight = -1f;

	const float ScrollBarWidth = CraftingRecipeListPanel.ScrollBarWidth;
	const float MinThumbHeight = 24f;

	// Deterministic list layout (style px): every item has a known height so clicks and scroll
	// range come from arithmetic, not from Box.Rect — which lags/misreports once the content
	// panel is offset by `top` for scrolling (the same quirk the crafting list works around).
	const float RowHeight = CraftingMenuSection.RecipeRowHeight;
	const float ItemGap = 4f * CraftingMenuSection.LayoutScale;
	const float HeaderHeight = 36f;

	readonly List<float> _rowTops = new();
	float _layoutCursor;
	float _contentHeightStyle;

	/// <summary>
	/// Deliberately not a whole number of rows: the crafting height ends flush with a row, which
	/// hid that the list continues. +15px leaves about half of the next quest peeking under the fold.
	/// </summary>
	const float ViewHeight = CraftingMenuSection.RecipeListMaxHeight + 15f;

	string _selectedQuestId;
	bool _menuOpen;
	bool _panelVisible;
	bool _subscribed;
	int _builtContentVersion = -1;
	int _builtOrderSignature = -1;

	public void Build( Panel menuColumn )
	{
		QuestCatalog.EnsureLoaded();
		QuestTracker.EnsureLoaded();

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

		_title = new Label { Parent = _sectionRoot, Text = "Quests" };
		_title.Style.FontColor = Color.White;
		_title.Style.FontSize = Length.Pixels( CraftingMenuSection.CraftingTitleFontSize );
		_title.Style.Set( "width", "100%" );

		var detail = new Panel { Parent = _sectionRoot };
		detail.Style.Set( "flex-direction", "column" );
		detail.Style.Set( "gap", $"{10f * CraftingMenuSection.LayoutScale}px" );
		detail.Style.Set( "width", "100%" );
		detail.Style.PaddingTop = Length.Pixels( 4f * CraftingMenuSection.LayoutScale );
		detail.Style.PaddingBottom = Length.Pixels( 8f * CraftingMenuSection.LayoutScale );
		detail.Style.Set( "border-bottom-width", "1px" );
		detail.Style.Set( "border-bottom-color", "#383d47" );
		// Fixed height (not max-height): showing/hiding detail blocks must never move the list below.
		detail.Style.Height = Length.Pixels( CraftingMenuSection.DetailAreaMaxHeight );
		detail.Style.MinHeight = Length.Pixels( CraftingMenuSection.DetailAreaMaxHeight );
		detail.Style.MaxHeight = Length.Pixels( CraftingMenuSection.DetailAreaMaxHeight );
		detail.Style.Set( "flex-shrink", "0" );
		detail.Style.Set( "overflow-y", "scroll" );

		var header = new Panel { Parent = detail };
		header.Style.Set( "flex-direction", "row" );
		header.Style.Set( "align-items", "center" );
		header.Style.Set( "justify-content", "space-between" );
		header.Style.Set( "width", "100%" );

		_detailName = new Label { Parent = header, Text = "Select a quest" };
		_detailName.Style.FontColor = Color.White;
		_detailName.Style.FontSize = Length.Pixels( CraftingMenuSection.ItemNameFontSize );
		_detailName.Style.Set( "white-space", "normal" );
		_detailName.Style.Set( "flex-shrink", "1" );

		_detailStatus = new Label { Parent = header, Text = string.Empty };
		_detailStatus.Style.FontSize = Length.Pixels( 13f * CraftingMenuSection.TextScale );
		_detailStatus.Style.Set( "flex-shrink", "0" );
		_detailStatus.Style.PaddingLeft = Length.Pixels( 8f * CraftingMenuSection.LayoutScale );

		_descriptionBlock = CreateDetailBlock( detail, "Description", out _descriptionEntries );
		_taskBlock = CreateDetailBlock( detail, "Task", out _taskEntries );
		_objectivesBlock = CreateDetailBlock( detail, "Objectives", out _objectivesEntries );
		_rewardsBlock = CreateDetailBlock( detail, "Rewards", out _rewardsEntries );

		// Viewport (fixed height, clips) + absolutely positioned content moved by wheel scroll.
		// The soft-cursor menu never gets real mouse events, so CSS overflow scrolling is inert here.
		_questList = new Panel { Parent = _sectionRoot };
		_questList.Style.Set( "position", "relative" );
		_questList.Style.Set( "width", "100%" );
		_questList.Style.Set( "overflow", "hidden" );
		_questList.Style.Height = Length.Pixels( ViewHeight );
		_questList.Style.MinHeight = Length.Pixels( ViewHeight );
		_questList.Style.MaxHeight = Length.Pixels( ViewHeight );
		_questList.Style.Set( "flex-shrink", "0" );

		_questContent = new Panel { Parent = _questList };
		_questContent.Style.Set( "position", "absolute" );
		_questContent.Style.Set( "left", "0" );
		_questContent.Style.Set( "right", $"{ScrollBarWidth + 4f}px" );
		_questContent.Style.Set( "top", "0px" );
		_questContent.Style.Set( "flex-direction", "column" );
		_questContent.Style.Set( "gap", $"{4f * CraftingMenuSection.LayoutScale}px" );
		_questContent.Style.Set( "flex-shrink", "0" );

		// Visible scrollbar, same chrome as the crafting list (track + draggable thumb).
		_scrollTrack = new Panel { Parent = _questList };
		_scrollTrack.Style.Set( "position", "absolute" );
		_scrollTrack.Style.Set( "top", "0" );
		_scrollTrack.Style.Set( "bottom", "0" );
		_scrollTrack.Style.Set( "right", "0" );
		_scrollTrack.Style.Width = Length.Pixels( ScrollBarWidth );
		_scrollTrack.Style.Set( "z-index", "30" );
		_scrollTrack.Style.BackgroundColor = new Color( 0.08f, 0.09f, 0.11f, 0.95f );
		_scrollTrack.Style.Set( "border-radius", "4px" );
		_scrollTrack.Style.Set( "pointer-events", "none" );

		_scrollThumb = new Panel { Parent = _scrollTrack };
		_scrollThumb.Style.Set( "position", "absolute" );
		_scrollThumb.Style.Set( "left", "2px" );
		_scrollThumb.Style.Set( "right", "2px" );
		_scrollThumb.Style.Set( "top", "0px" );
		_scrollThumb.Style.Height = Length.Pixels( MinThumbHeight );
		_scrollThumb.Style.Set( "z-index", "31" );
		_scrollThumb.Style.BackgroundColor = new Color( 0.55f, 0.60f, 0.68f, 0.95f );
		_scrollThumb.Style.Set( "border-radius", "3px" );
		_scrollThumb.Style.Set( "pointer-events", "none" );

		BuildQuestRows();

		if ( QuestCatalog.All.Count > 0 )
			SelectQuest( QuestCatalog.All[0].Id );

		if ( !_subscribed )
		{
			QuestTracker.Changed += OnTrackerChanged;
			_subscribed = true;
		}

		UpdateVisibility();
	}

	void OnTrackerChanged()
	{
		if ( _sectionRoot is null || !_sectionRoot.IsValid() )
		{
			QuestTracker.Changed -= OnTrackerChanged;
			_subscribed = false;
			return;
		}

		RebuildRowsIfCatalogChanged();

		if ( _menuOpen && _panelVisible )
			Refresh();
	}

	/// <summary>
	/// Rows are rebuilt only when the catalog version changes (JSON edit / hot reload / reset) or a
	/// quest unlocks — row order depends on lock state, and plain progress ticks never reorder.
	/// </summary>
	void RebuildRowsIfCatalogChanged()
	{
		if ( _builtContentVersion == QuestCatalog.ContentVersion
		     && _builtOrderSignature == ComputeOrderSignature() )
			return;

		BuildQuestRows();

		if ( QuestCatalog.Get( _selectedQuestId ) is null && QuestCatalog.All.Count > 0 )
			_selectedQuestId = QuestCatalog.All[0].Id;
	}

	void BuildQuestRows()
	{
		if ( _questList is null )
			return;

		if ( _questContent is null )
			return;

		_questContent.DeleteChildren();
		_rows.Clear();
		_rowTops.Clear();
		_layoutCursor = 0f;
		_contentHeightStyle = 0f;
		_builtContentVersion = QuestCatalog.ContentVersion;
		_builtOrderSignature = ComputeOrderSignature();
		SetScrollY( 0f );

		// Main Quests read as a timeline: done ones, then the one you're working on directly
		// under the last completed, then the locked "?????" rows (no extra label).
		// Side Quests: always unlocked, listed after.
		BuildListHeader( "Main Quests" );
		BuildMainRowsInState( QuestState.Completed );
		BuildMainRowsInState( QuestState.Active );
		BuildMainRowsInState( QuestState.Locked );

		var hasSide = false;
		foreach ( var quest in QuestCatalog.All )
		{
			if ( quest is not null && quest.Side )
			{
				hasSide = true;
				break;
			}
		}

		if ( !hasSide )
			return;

		BuildListHeader( "Side Quests" );
		foreach ( var quest in QuestCatalog.All )
		{
			if ( quest is null || string.IsNullOrWhiteSpace( quest.Id ) || !quest.Side )
				continue;

			BuildQuestRow( quest );
		}
	}

	void BuildMainRowsInState( QuestState state )
	{
		foreach ( var quest in QuestCatalog.All )
		{
			if ( quest is null || string.IsNullOrWhiteSpace( quest.Id ) || quest.Side )
				continue;

			if ( QuestTracker.GetState( quest.Id ) != state )
				continue;

			BuildQuestRow( quest );
		}
	}

	/// <summary>Row order depends on which quests are active/done/locked — rebuild only when that mix changes.</summary>
	static int ComputeOrderSignature() =>
		QuestTracker.CountByState( QuestState.Locked ) * 1000 + QuestTracker.CountByState( QuestState.Completed );

	// ---- Scrollbar (soft-cursor: track/thumb are hit-tested by rect, not by panel events) ----

	/// <summary>Overlay Attack1 routed here while the quests page is open: press to jump/drag, release to end.</summary>
	public bool TryHandleScrollbarPointer( Vector2 screenPos, bool pressed )
	{
		if ( !pressed )
		{
			if ( !_draggingThumb )
				return false;

			_draggingThumb = false;
			return true;
		}

		if ( _draggingThumb )
		{
			UpdateDragFromScreenY( screenPos.y );
			return true;
		}

		if ( !_menuOpen || !_panelVisible || GetMaxScrollY() <= 1f )
			return false;

		if ( !IsOverScrollbar( screenPos ) )
			return false;

		if ( !IsOverThumb( screenPos ) )
			JumpToTrackAtScreenY( screenPos.y );

		_draggingThumb = true;
		_dragStartMouseY = screenPos.y;
		_dragStartScrollY = _questScrollY;
		return true;
	}

	bool IsOverScrollbar( Vector2 screenPos )
	{
		if ( _scrollTrack is null || !_scrollTrack.IsValid() )
			return false;

		var track = _scrollTrack.Box.Rect;
		if ( track.Width <= 1f || track.Height <= 1f )
			return false;

		var pad = 4f * MathF.Max( 1f, ScreenScale() );
		return screenPos.x >= track.Left - pad && screenPos.x <= track.Right + pad
		       && screenPos.y >= track.Top - pad && screenPos.y <= track.Bottom + pad;
	}

	bool IsOverThumb( Vector2 screenPos )
	{
		if ( _scrollTrack is null || !_scrollTrack.IsValid() )
			return false;

		var track = _scrollTrack.Box.Rect;
		var scale = MathF.Max( 1f, ScreenScale() );
		var thumbH = GetThumbHeightStyle() * scale;
		var travel = MathF.Max( 0f, track.Height - thumbH );
		var maxY = GetMaxScrollY();
		var t = maxY > 0f ? Math.Clamp( _questScrollY / maxY, 0f, 1f ) : 0f;
		var thumbTop = track.Top + t * travel;
		return screenPos.y >= thumbTop - 2f && screenPos.y <= thumbTop + thumbH + 2f;
	}

	void JumpToTrackAtScreenY( float screenY )
	{
		if ( _scrollTrack is null || !_scrollTrack.IsValid() )
			return;

		var scale = MathF.Max( 0.001f, ScreenScale() );
		var thumbH = GetThumbHeightStyle();
		var travel = MathF.Max( 1f, ViewHeight - thumbH );
		var localYStyle = ( screenY - _scrollTrack.Box.Rect.Top ) / scale - thumbH * 0.5f;
		var t = Math.Clamp( localYStyle / travel, 0f, 1f );
		SetScrollY( t * GetMaxScrollY() );
	}

	void UpdateDragFromScreenY( float screenY )
	{
		var scale = MathF.Max( 0.001f, ScreenScale() );
		var travelStyle = MathF.Max( 1f, ViewHeight - GetThumbHeightStyle() );
		var dyStyle = ( screenY - _dragStartMouseY ) / scale;
		SetScrollY( _dragStartScrollY + dyStyle / travelStyle * GetMaxScrollY() );
	}

	float ScreenScale() => _questList is not null && _questList.IsValid() ? _questList.ScaleToScreen : 1f;

	/// <summary>Known at build time from item heights — valid before the first layout pass.</summary>
	float GetContentHeightStyle() => _contentHeightStyle;

	float GetThumbHeightStyle()
	{
		var contentH = GetContentHeightStyle();
		if ( contentH <= ViewHeight + 1f )
			return ViewHeight;

		return Math.Clamp( ViewHeight / contentH * ViewHeight, MinThumbHeight, ViewHeight );
	}

	/// <summary>Cheap per-frame sync while the page is open — content height is only known after layout.</summary>
	void UpdateScrollbarVisual()
	{
		if ( _scrollTrack is null || !_scrollTrack.IsValid() || _scrollThumb is null || !_scrollThumb.IsValid() )
			return;

		var maxY = GetMaxScrollY();
		var canScroll = maxY > 1f;
		var thumbH = canScroll ? GetThumbHeightStyle() : ViewHeight;
		var travel = MathF.Max( 0f, ViewHeight - thumbH );
		var t = canScroll ? Math.Clamp( _questScrollY / maxY, 0f, 1f ) : 0f;
		var thumbTop = t * travel;

		if ( MathF.Abs( thumbTop - _lastThumbTop ) < 0.25f && MathF.Abs( thumbH - _lastThumbHeight ) < 0.25f )
			return;

		_lastThumbTop = thumbTop;
		_lastThumbHeight = thumbH;
		_scrollTrack.Style.Set( "opacity", canScroll ? "1" : "0.35" );
		_scrollThumb.Style.Height = Length.Pixels( thumbH );
		_scrollThumb.Style.Set( "top", $"{thumbTop:0.##}px" );
	}

	/// <summary>Wheel over the quests page: same notch feel as the crafting list.</summary>
	public void ApplyListWheel( Vector2 wheel )
	{
		if ( !_menuOpen || !_panelVisible )
			return;

		var delta = wheel.y;
		if ( MathF.Abs( wheel.x ) > MathF.Abs( wheel.y ) )
			delta = wheel.x;

		if ( MathF.Abs( delta ) < 0.01f )
			return;

		var notches = MathF.Abs( delta ) < 2f
			? MathF.Sign( delta ) * MathF.Max( 1f, MathF.Abs( delta ) )
			: MathF.Sign( delta );

		SetScrollY( _questScrollY + notches * CraftingRecipeListPanel.GetNotchStep() );
	}

	void SetScrollY( float y )
	{
		_questScrollY = Math.Clamp( y, 0f, GetMaxScrollY() );
		_questContent?.Style.Set( "top", $"{-_questScrollY:0.##}px" );
		UpdateScrollbarVisual();
	}

	/// <summary>Content height comes from the laid-out box (rows + headers), so no per-row bookkeeping.</summary>
	float GetMaxScrollY() => Math.Max( 0f, GetContentHeightStyle() - ViewHeight );

	void BuildListHeader( string text )
	{
		var header = new Label { Parent = _questContent, Text = text };
		header.Style.FontColor = new Color( 0.85f, 0.87f, 0.9f );
		header.Style.FontSize = Length.Pixels( CraftingMenuSection.SectionHeaderFontSize );
		header.Style.Set( "pointer-events", "none" );
		header.Style.Set( "flex-shrink", "0" );
		header.Style.Set( "align-items", "flex-end" );
		header.Style.Height = Length.Pixels( HeaderHeight );
		header.Style.MinHeight = Length.Pixels( HeaderHeight );
		header.Style.MaxHeight = Length.Pixels( HeaderHeight );
		header.Style.PaddingBottom = Length.Pixels( 2f * CraftingMenuSection.LayoutScale );

		AdvanceLayout( HeaderHeight );
	}

	void AdvanceLayout( float itemHeight )
	{
		_contentHeightStyle = _layoutCursor + itemHeight;
		_layoutCursor = _contentHeightStyle + ItemGap;
	}

	void BuildQuestRow( QuestDefinition quest )
	{
		var row = new QuestRowPanel
		{
			Parent = _questContent,
			Section = this,
			QuestId = quest.Id
		};
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "align-items", "center" );
		row.Style.Set( "justify-content", "space-between" );
		row.Style.Set( "width", "100%" );
		row.Style.Height = Length.Pixels( CraftingMenuSection.RecipeRowHeight );
		row.Style.MinHeight = Length.Pixels( CraftingMenuSection.RecipeRowHeight );
		row.Style.MaxHeight = Length.Pixels( CraftingMenuSection.RecipeRowHeight );
		row.Style.Set( "overflow", "hidden" );
		row.Style.Set( "flex-shrink", "0" );
		row.Style.PaddingLeft = Length.Pixels( 8f * CraftingMenuSection.LayoutScale );
		row.Style.PaddingRight = Length.Pixels( 8f * CraftingMenuSection.LayoutScale );
		row.Style.BackgroundColor = new Color( 0.10f, 0.11f, 0.13f, 0.9f );
		row.Style.Set( "border-width", "1px" );
		row.Style.Set( "border-color", "#383d47" );
		row.Style.Set( "border-radius", "4px" );
		row.Style.Set( "pointer-events", "none" );

		var name = new Label { Parent = row, Text = quest.DisplayName };
		name.Style.FontColor = Color.White;
		name.Style.FontSize = Length.Pixels( 15f * CraftingMenuSection.TextScale );
		name.Style.Set( "pointer-events", "none" );
		name.Style.Set( "white-space", "nowrap" );
		name.Style.Set( "overflow", "hidden" );
		name.Style.Set( "flex-shrink", "1" );
		row.NameLabel = name;

		var status = new Label { Parent = row, Text = string.Empty };
		status.Style.FontSize = Length.Pixels( 12f * CraftingMenuSection.TextScale );
		status.Style.Set( "pointer-events", "none" );
		status.Style.Set( "flex-shrink", "0" );
		status.Style.PaddingLeft = Length.Pixels( 8f * CraftingMenuSection.LayoutScale );
		row.StatusLabel = status;

		_rows.Add( row );
		_rowTops.Add( _layoutCursor );
		AdvanceLayout( RowHeight );
	}

	public void SelectQuest( string questId )
	{
		_selectedQuestId = questId;
		Refresh();
	}

	/// <summary>Soft-cursor Attack1 — OS mouse is Hidden while the menu is open.</summary>
	public bool TrySelectQuestAtScreen( Vector2 screenPos )
	{
		if ( !_menuOpen || !_panelVisible )
			return false;

		// Pick by scroll math inside the viewport rect. Row Box.Rects are not used: they lag the
		// content `top` offset, which selected the row above/below the cursor after scrolling.
		if ( _questList is null || !_questList.IsValid() )
			return false;

		var view = _questList.Box.Rect;
		if ( view.Width <= 1f || view.Height <= 1f )
			return false;

		var scale = _questList.ScaleToScreen > 0.001f ? _questList.ScaleToScreen : 1f;
		var scrollbarLeft = view.Right - ( ScrollBarWidth + 4f ) * scale;

		if ( screenPos.x < view.Left || screenPos.x >= scrollbarLeft
		     || screenPos.y < view.Top || screenPos.y > view.Bottom )
			return false;

		var localY = ( screenPos.y - view.Top ) / scale + _questScrollY;

		for ( var i = 0; i < _rows.Count && i < _rowTops.Count; i++ )
		{
			var top = _rowTops[i];
			if ( localY < top || localY >= top + RowHeight )
				continue;

			var row = _rows[i];
			if ( row is null || !row.IsValid() )
				return false;

			SelectQuest( row.QuestId );
			return true;
		}

		return false;
	}

	public void Refresh()
	{
		UpdateRows();
		UpdateTitle();

		var quest = QuestCatalog.Get( _selectedQuestId );
		if ( quest is null )
		{
			if ( _detailName is not null )
				_detailName.Text = "Select a quest";
			if ( _detailStatus is not null )
				_detailStatus.Text = string.Empty;
			SetBlockVisible( _descriptionBlock, false );
			SetBlockVisible( _taskBlock, false );
			SetBlockVisible( _objectivesBlock, false );
			SetBlockVisible( _rewardsBlock, false );
			return;
		}

		var state = QuestTracker.GetState( quest.Id );

		if ( _detailName is not null )
			_detailName.Text = state == QuestState.Locked ? HiddenName : quest.DisplayName;

		if ( _detailStatus is not null )
		{
			_detailStatus.Text = state == QuestState.Locked
				? string.Empty
				: quest.Side ? $"Side · {StatusText( state )}" : StatusText( state );
			_detailStatus.Style.FontColor = StatusColor( state );
		}

		if ( state == QuestState.Locked )
		{
			SetBlockVisible( _descriptionBlock, true );
			PopulateLines( _descriptionEntries, new[] { "Locked — complete the previous quest to reveal." }, LockedText );
			SetBlockVisible( _taskBlock, false );
			SetBlockVisible( _objectivesBlock, false );
			SetBlockVisible( _rewardsBlock, false );
			return;
		}

		SetBlockVisible( _descriptionBlock, true );
		PopulateLines( _descriptionEntries,
			string.IsNullOrWhiteSpace( quest.Description ) ? Array.Empty<string>() : new[] { quest.Description }, BodyText );

		SetBlockVisible( _taskBlock, true );
		PopulateLines( _taskEntries,
			string.IsNullOrWhiteSpace( quest.Summary ) ? Array.Empty<string>() : new[] { quest.Summary }, BodyText );

		SetBlockVisible( _objectivesBlock, quest.Objectives.Count > 0 );
		PopulateObjectives( quest );

		var hasRewards = quest.Rewards is { Count: > 0 };
		SetBlockVisible( _rewardsBlock, hasRewards );
		if ( hasRewards )
			PopulateLines( _rewardsEntries, quest.Rewards, BodyText );
	}

	void PopulateObjectives( QuestDefinition quest )
	{
		if ( _objectivesEntries is null )
			return;

		_objectivesEntries.DeleteChildren();

		for ( var i = 0; i < quest.Objectives.Count; i++ )
		{
			var objective = quest.Objectives[i];
			var required = objective.RequiredCount;
			var progress = Math.Min( required, QuestTracker.GetProgress( quest.Id, i ) );
			var done = progress >= required;

			var label = string.IsNullOrWhiteSpace( objective.Label ) ? objective.Event : objective.Label;
			var text = required > 1 || !done
				? $"{( done ? "✓" : "•" )} {label}  {progress}/{required}"
				: $"✓ {label}";

			AddLine( _objectivesEntries, text, done ? DoneText : BodyText );
		}
	}

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
		{
			// Opening the menu is the one cheap moment to notice a JSON edit or hot reload.
			QuestCatalog.ReloadIfChanged();
			QuestTracker.EnsureLoaded();
			RebuildRowsIfCatalogChanged();
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
		if ( visible && _menuOpen )
			Refresh();
		UpdateVisibility();
	}

	public void TickMenu( bool menuOpen )
	{
		if ( !menuOpen || !_panelVisible )
			return;

		UpdateScrollbarVisual();
	}

	public void OnMenuGlobalMouseUp() { }

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
	}

	void UpdateTitle()
	{
		if ( _title is null )
			return;

		var total = QuestCatalog.All.Count;
		var done = QuestTracker.CountByState( QuestState.Completed );
		_title.Text = total > 0 ? $"Quests  {done}/{total}" : "Quests";
	}

	void UpdateRows()
	{
		for ( var i = 0; i < _rows.Count; i++ )
		{
			var row = _rows[i];
			if ( row is null || !row.IsValid() )
				continue;

			var state = QuestTracker.GetState( row.QuestId );
			var selected = string.Equals( row.QuestId, _selectedQuestId, StringComparison.OrdinalIgnoreCase );

			row.Style.Set( "border-color", selected ? "#8ab4f8" : "#383d47" );
			row.Style.BackgroundColor = selected
				? new Color( 0.16f, 0.19f, 0.24f, 0.95f )
				: new Color( 0.10f, 0.11f, 0.13f, 0.9f );

			if ( row.NameLabel is not null )
			{
				var locked = state == QuestState.Locked;
				row.NameLabel.Text = locked ? HiddenName : QuestCatalog.Get( row.QuestId )?.DisplayName ?? row.QuestId;
				row.NameLabel.Style.FontColor = locked ? LockedText : Color.White;
			}

			if ( row.StatusLabel is not null )
			{
				// Locked rows are already "?????" — no extra tag.
				row.StatusLabel.Text = state == QuestState.Locked ? string.Empty : StatusText( state );
				row.StatusLabel.Style.FontColor = StatusColor( state );
			}
		}
	}

	static string StatusText( QuestState state ) => state switch
	{
		QuestState.Completed => "Done",
		QuestState.Active => "Active",
		_ => "Locked",
	};

	static Color StatusColor( QuestState state ) => state switch
	{
		QuestState.Completed => DoneText,
		QuestState.Active => ActiveText,
		_ => LockedText,
	};

	static void SetBlockVisible( Panel block, bool visible )
	{
		block?.Style.Set( "display", visible ? "flex" : "none" );
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

	static void PopulateLines( Panel host, IReadOnlyList<string> lines, Color color )
	{
		if ( host is null )
			return;

		host.DeleteChildren();

		if ( lines is null || lines.Count == 0 )
		{
			AddLine( host, "—", color );
			return;
		}

		for ( var i = 0; i < lines.Count; i++ )
			AddLine( host, lines[i], color );
	}

	static void AddLine( Panel host, string text, Color color )
	{
		var label = new Label { Parent = host, Text = text };
		label.Style.FontColor = color;
		label.Style.FontSize = Length.Pixels( CraftingMenuSection.SectionEntryFontSize );
		label.Style.Set( "white-space", "normal" );
	}
}
