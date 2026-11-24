using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Settings → bug icon → "Report a bug": the form behind <see cref="BugReports.Submit"/>. Steam ID and name are
/// filled in and read-only; subject, category, severity and three detail boxes are the player's. Built once into
/// the settings sub-page body; every box has a fixed size so nothing reflows while typing or when the status
/// line changes. Clicks arrive as soft-cursor hit tests from <see cref="GameSettingsMenuSection"/>.
/// </summary>
public sealed class BugReportPage
{
	const float FormWidth = 660f;
	const float FieldGap = 6f;
	const float SingleLineHeight = 34f;
	const float SendButtonWidth = 200f;

	static float BodySize => CraftingMenuSection.SectionEntryFontSize;

	Label _reporterLabel;
	TextEntry _subject;
	TextEntry _happened;
	TextEntry _steps;
	TextEntry _expected;
	readonly List<TextEntry> _entries = new();

	readonly List<(Panel Button, string Value)> _categoryButtons = new();
	readonly List<(Panel Button, string Value)> _severityButtons = new();
	string _category = BugReports.Categories[0];
	string _severity = BugReports.Severities[0];

	Panel _sendButton;
	Label _status;
	Panel _hovered;
	int _shownResultVersion = -1;

	public void Build( Panel body )
	{
		var form = new Panel { Parent = body };
		form.Style.Set( "flex-direction", "column" );
		form.Style.Set( "align-items", "stretch" );
		form.Style.Width = Length.Pixels( FormWidth );

		_reporterLabel = new Label { Parent = form, Text = "" };
		UiStyle.Text( _reporterLabel, UiText.Secondary, BodySize );
		_reporterLabel.Style.Set( "margin-bottom", "10px" );

		_subject = AddEntry( form, "Subject", "One line: what went wrong", SingleLineHeight, BugReports.SubjectMaxLength, multiline: false );

		AddFieldLabel( form, "Category" );
		AddChoiceRow( form, BugReports.Categories, _categoryButtons );
		AddFieldLabel( form, "Severity" );
		AddChoiceRow( form, BugReports.Severities, _severityButtons );

		_happened = AddEntry( form, "What happened", "Describe the bug", 96f, BugReports.DetailMaxLength, multiline: true );
		_steps = AddEntry( form, "Steps to reproduce", "1. …  2. …  3. …", 76f, BugReports.DetailMaxLength, multiline: true );
		_expected = AddEntry( form, "What you expected", "What should have happened instead", 58f, BugReports.DetailMaxLength, multiline: true );

		var footer = new Panel { Parent = form };
		footer.Style.Set( "flex-direction", "row" );
		footer.Style.Set( "align-items", "center" );
		footer.Style.Set( "margin-top", "12px" );
		footer.Style.Height = Length.Pixels( 48f );

		_status = new Label { Parent = footer, Text = "" };
		UiStyle.Text( _status, UiText.Secondary, BodySize );
		_status.Style.Set( "flex-grow", "1" );
		_status.Style.Set( "flex-shrink", "1" );
		_status.Style.Set( "white-space", "normal" );
		_status.Style.Set( "margin-right", "12px" );

		_sendButton = new Panel { Parent = footer };
		_sendButton.Style.Width = Length.Pixels( SendButtonWidth );
		_sendButton.Style.Height = Length.Pixels( 48f );
		_sendButton.Style.Set( "flex-shrink", "0" );
		UiStyle.Button( _sendButton, UiButton.Primary );
		_sendButton.Style.Set( "pointer-events", "none" );
		var sendLabel = new Label { Parent = _sendButton, Text = "Send report" };
		UiStyle.ButtonLabel( sendLabel, UiButton.Primary, CraftingMenuSection.ItemNameFontSize );

		var note = new Label
		{
			Parent = form,
			Text = "Your build, position, vitals, held item, frame time and the host's recent log are attached.",
		};
		UiStyle.Text( note, UiText.Muted, BodySize );
		note.Style.Set( "white-space", "normal" );
		note.Style.Set( "margin-top", "6px" );

		RefreshChoiceStyles();
	}

	TextEntry AddEntry( Panel form, string title, string placeholder, float height, int maxLength, bool multiline )
	{
		AddFieldLabel( form, title );

		var entry = new TextEntry { Parent = form };
		entry.Style.Height = Length.Pixels( height );
		entry.Style.Set( "flex-shrink", "0" );
		entry.Style.PaddingLeft = Length.Pixels( 8f );
		entry.Style.PaddingRight = Length.Pixels( 8f );
		entry.Style.PaddingTop = Length.Pixels( 6f );
		entry.Style.Set( "overflow", "hidden" );
		UiStyle.Field( entry );
		entry.Style.FontSize = Length.Pixels( BodySize );
		if ( multiline )
		{
			entry.Multiline = true;
			entry.Style.Set( "align-items", "flex-start" );
			entry.Style.Set( "white-space", "pre-wrap" );
		}

		entry.Placeholder = placeholder;
		entry.MaxLength = maxLength;
		_entries.Add( entry );
		return entry;
	}

	static void AddFieldLabel( Panel form, string text )
	{
		var label = new Label { Parent = form, Text = text };
		UiStyle.Text( label, UiText.Header, BodySize );
		label.Style.Set( "margin-top", $"{FieldGap + 4f}px" );
		label.Style.Set( "margin-bottom", "4px" );
	}

	static void AddChoiceRow( Panel form, string[] options, List<(Panel Button, string Value)> into )
	{
		var row = new Panel { Parent = form };
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "flex-wrap", "wrap" );
		row.Style.Set( "gap", "6px" );

		foreach ( var option in options )
		{
			var button = new Panel { Parent = row };
			button.Style.Height = Length.Pixels( 34f );
			button.Style.PaddingLeft = Length.Pixels( 12f );
			button.Style.PaddingRight = Length.Pixels( 12f );
			UiStyle.Button( button, UiButton.Ghost );
			button.Style.Set( "pointer-events", "none" );

			var label = new Label { Parent = button, Text = option };
			UiStyle.ButtonLabel( label, UiButton.Ghost, BodySize );
			into.Add( ( button, option ) );
		}
	}

	// ------------------------------------------------------------------ lifecycle

	/// <summary>The page became visible: refresh the reporter line; typed text is kept as a draft.</summary>
	public void OnShown()
	{
		if ( _reporterLabel is not null && _reporterLabel.IsValid() )
			_reporterLabel.Text = $"Reporting as {BugReports.LocalDisplayName()}  ·  Steam ID {BugReports.LocalSteamId()}";

		if ( !BugReports.AwaitingResult )
			SetStatus( "", ok: true );

		_shownResultVersion = BugReports.ResultVersion;
		_subject?.Focus();
	}

	/// <summary>True while one of the form's boxes has the keyboard (menu page hotkeys stand down).</summary>
	public bool HasTextFocus
	{
		get
		{
			foreach ( var entry in _entries )
			{
				if ( entry is not null && entry.IsValid() && entry.HasFocus )
					return true;
			}

			return false;
		}
	}

	/// <summary>The page was left (Back, another page, menu closed): no entry may keep the keyboard.</summary>
	public void OnHidden()
	{
		SetHovered( null );
		foreach ( var entry in _entries )
		{
			if ( entry is not null && entry.IsValid() && entry.HasFocus )
				entry.Blur();
		}

		if ( InputFocus.Current is TextEntry focused && _entries.Contains( focused ) )
			InputFocus.Clear();
	}

	/// <summary>Per menu tick while the page is open: hover highlight and the host's answer.</summary>
	public void Tick( Vector2 screenPos )
	{
		SetHovered( FindClickable( screenPos ) );

		if ( _shownResultVersion == BugReports.ResultVersion )
			return;

		_shownResultVersion = BugReports.ResultVersion;
		SetStatus( BugReports.LastResultMessage ?? "", BugReports.LastResultOk || BugReports.AwaitingResult );

		if ( BugReports.LastResultOk && !BugReports.AwaitingResult )
		{
			foreach ( var entry in _entries )
				entry.Text = "";
		}
	}

	/// <summary>Soft-cursor click. Always true: a click anywhere on the page belongs to the page.</summary>
	public bool TryClick( Vector2 screenPos )
	{
		foreach ( var entry in _entries )
		{
			if ( !InventoryScreenPointer.PanelBoxContainsScreen( entry, screenPos ) )
				continue;

			entry.Focus();
			return true;
		}

		foreach ( var (button, value) in _categoryButtons )
		{
			if ( !InventoryScreenPointer.PanelBoxContainsScreen( button, screenPos ) )
				continue;

			_category = value;
			RefreshChoiceStyles();
			return true;
		}

		foreach ( var (button, value) in _severityButtons )
		{
			if ( !InventoryScreenPointer.PanelBoxContainsScreen( button, screenPos ) )
				continue;

			_severity = value;
			RefreshChoiceStyles();
			return true;
		}

		if ( InventoryScreenPointer.PanelBoxContainsScreen( _sendButton, screenPos ) )
		{
			Send();
			return true;
		}

		// Empty space drops the keyboard so menu keys work again.
		OnHidden();
		return true;
	}

	void Send()
	{
		if ( BugReports.AwaitingResult )
			return;

		if ( string.IsNullOrWhiteSpace( _subject?.Text ) )
		{
			SetStatus( "Add a subject first.", ok: false );
			_subject?.Focus();
			return;
		}

		BugReports.Submit( _subject.Text, _category, _severity, _happened?.Text, _steps?.Text, _expected?.Text );
	}

	// ------------------------------------------------------------------ styling

	Panel FindClickable( Vector2 screenPos )
	{
		foreach ( var (button, _) in _categoryButtons )
		{
			if ( InventoryScreenPointer.PanelBoxContainsScreen( button, screenPos ) )
				return button;
		}

		foreach ( var (button, _) in _severityButtons )
		{
			if ( InventoryScreenPointer.PanelBoxContainsScreen( button, screenPos ) )
				return button;
		}

		return InventoryScreenPointer.PanelBoxContainsScreen( _sendButton, screenPos ) ? _sendButton : null;
	}

	void SetHovered( Panel hit )
	{
		if ( ReferenceEquals( hit, _hovered ) )
			return;

		_hovered = hit;
		RefreshChoiceStyles();
	}

	void RefreshChoiceStyles()
	{
		StyleChoices( _categoryButtons, _category );
		StyleChoices( _severityButtons, _severity );

		if ( _sendButton is not null && _sendButton.IsValid() )
		{
			UiStyle.SetButtonState( _sendButton, UiButton.Primary, hovered: ReferenceEquals( _hovered, _sendButton ),
				pressed: false, disabled: BugReports.AwaitingResult );
		}
	}

	void StyleChoices( List<(Panel Button, string Value)> buttons, string selectedValue )
	{
		foreach ( var (button, value) in buttons )
		{
			if ( button is null || !button.IsValid() )
				continue;

			var selected = string.Equals( value, selectedValue, StringComparison.Ordinal );
			UiStyle.SetButtonState( button, UiButton.Ghost, hovered: ReferenceEquals( _hovered, button ), pressed: selected, disabled: false );
			if ( selected )
				button.Style.Set( "border-color", UiStyle.Css( UiTheme.Colors.AccentColor ) );
		}
	}

	void SetStatus( string text, bool ok )
	{
		if ( _status is null || !_status.IsValid() )
			return;

		_status.Text = text;
		_status.Style.FontColor = ok ? UiTheme.Colors.TextSecondaryColor : UiStyle.StatusColor( false );
		RefreshChoiceStyles();
	}
}
