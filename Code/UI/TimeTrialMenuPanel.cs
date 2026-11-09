using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Center-screen time trial lobby: mode, variation, Start/Join/Leave.</summary>
public sealed class TimeTrialMenuPanel : Panel
{
	readonly PlayerInventoryInteraction _interaction;
	Panel _card;
	Panel _modeRow;
	Panel _variationList;
	Label _status;
	Label _primaryLabel;
	Panel _primaryBtn;
	Panel _leaveBtn;
	TimeTrialMode _selectedMode = TimeTrialMode.Solo;
	string _selectedVariationId = "";
	bool _built;
	bool _open;

	TimeTrialPhase _lastPhase = (TimeTrialPhase)(-1);
	int _lastReadyCount = -1;
	Guid _lastLeaderId;
	string _lastVariationId = "\0";
	TimeTrialMode _lastBuiltMode = (TimeTrialMode)(-1);
	string _lastBuiltVariationSelection = "\0";

	public TimeTrialMenuPanel( PlayerInventoryInteraction interaction )
	{
		_interaction = interaction;
		Style.Set( "position", "absolute" );
		Style.Set( "left", "0" );
		Style.Set( "top", "0" );
		Style.Set( "right", "0" );
		Style.Set( "bottom", "0" );
		Style.Set( "align-items", "center" );
		Style.Set( "justify-content", "center" );
		Style.Set( "pointer-events", "all" );
		Style.Set( "display", "none" );
		UiStyle.Backdrop( this );
		AcceptsFocus = false;
	}

	public override bool WantsMouseInput() => _open;

	public void SetOpen( bool open )
	{
		_open = open;
		Style.Set( "display", open ? "flex" : "none" );
		if ( open )
		{
			EnsureBuilt();
			// Force a full rebuild on open.
			_lastPhase = (TimeTrialPhase)(-1);
			Refresh( forceRebuild: true );
		}
	}

	/// <summary>Light per-frame update — rebuild UI only when lobby state or selection changes.</summary>
	public void TickOpen()
	{
		if ( !_open || !_built )
			return;

		var session = _interaction?.FocusedTimeTrialStand ?? TimeTrialSession.Instance;
		var phase = session?.Phase ?? TimeTrialPhase.Idle;
		var ready = session?.ReadyCount ?? 0;
		var leader = session?.QueueLeaderId ?? default;
		var activeVar = session?.ActiveVariationId ?? "";

		var dirty = phase != _lastPhase
		            || ready != _lastReadyCount
		            || leader != _lastLeaderId
		            || !string.Equals( activeVar, _lastVariationId, StringComparison.Ordinal )
		            || _selectedMode != _lastBuiltMode
		            || !string.Equals( _selectedVariationId, _lastBuiltVariationSelection, StringComparison.Ordinal );

		if ( dirty )
			Refresh( forceRebuild: true );
		else
			UpdateFooter();
	}

	public void Refresh( bool forceRebuild = false )
	{
		if ( !_built )
			return;

		EnsureVariationDefault();

		if ( forceRebuild
		     || _selectedMode != _lastBuiltMode
		     || !string.Equals( _selectedVariationId, _lastBuiltVariationSelection, StringComparison.Ordinal ) )
		{
			RebuildModeRow();
			RebuildVariationList();
			_lastBuiltMode = _selectedMode;
			_lastBuiltVariationSelection = _selectedVariationId ?? "";
		}

		var session = _interaction?.FocusedTimeTrialStand ?? TimeTrialSession.Instance;
		_lastPhase = session?.Phase ?? TimeTrialPhase.Idle;
		_lastReadyCount = session?.ReadyCount ?? 0;
		_lastLeaderId = session?.QueueLeaderId ?? default;
		_lastVariationId = session?.ActiveVariationId ?? "";

		UpdateFooter();
	}

	void EnsureBuilt()
	{
		if ( _built )
			return;
		_built = true;

		_card = new Panel { Parent = this };
		_card.Style.Set( "flex-direction", "column" );
		_card.Style.Set( "gap", "10px" );
		_card.Style.Width = Length.Pixels( 360f );
		_card.Style.PaddingLeft = Length.Pixels( 16f );
		_card.Style.PaddingRight = Length.Pixels( 16f );
		_card.Style.PaddingTop = Length.Pixels( 14f );
		_card.Style.PaddingBottom = Length.Pixels( 14f );
		UiStyle.Surface( _card, UiSurface.Frame );

		UiStyle.AddTitle( _card, "Time Trial", 22f );

		_status = new Label { Parent = _card, Text = "" };
		UiStyle.Text( _status, UiText.Secondary );

		var modeHeader = new Label { Parent = _card, Text = "Mode" };
		UiStyle.Text( modeHeader, UiText.Header, 13f );

		_modeRow = new Panel { Parent = _card };
		_modeRow.Style.Set( "flex-direction", "row" );
		_modeRow.Style.Set( "gap", "8px" );

		var varHeader = new Label { Parent = _card, Text = "Race" };
		UiStyle.Text( varHeader, UiText.Header, 13f );

		_variationList = new Panel { Parent = _card };
		_variationList.Style.Set( "flex-direction", "column" );
		_variationList.Style.Set( "gap", "4px" );
		_variationList.Style.MaxHeight = Length.Pixels( 160f );

		var footer = new Panel { Parent = _card };
		footer.Style.Set( "flex-direction", "row" );
		footer.Style.Set( "gap", "8px" );
		footer.Style.Set( "justify-content", "flex-end" );
		footer.Style.MarginTop = Length.Pixels( 6f );

		_leaveBtn = MakeButton( footer, "Leave Queue", UiButton.Danger, () =>
		{
			_interaction?.OwnerMenuLeave();
			Refresh( forceRebuild: true );
		} );
		_primaryBtn = MakeButton( footer, "Start", UiButton.Primary, OnPrimary, out _primaryLabel );

		var backRow = new Panel { Parent = _card };
		backRow.Style.Set( "flex-direction", "row" );
		backRow.Style.Set( "justify-content", "flex-start" );
		backRow.Style.MarginTop = Length.Pixels( 4f );
		MakeButton( backRow, "Back", UiButton.Default, () => _interaction?.SetTimeTrialMenuOpen( false ) );
	}

	Panel MakeButton( Panel parent, string text, UiButton kind, Action onClick ) =>
		MakeButton( parent, text, kind, onClick, out _ );

	Panel MakeButton( Panel parent, string text, UiButton kind, Action onClick, out Label label )
	{
		var btn = new TimeTrialClickPanel { Parent = parent };
		btn.Clicked = onClick;
		btn.Style.PaddingLeft = Length.Pixels( 12f );
		btn.Style.PaddingRight = Length.Pixels( 12f );
		btn.Style.PaddingTop = Length.Pixels( 8f );
		btn.Style.PaddingBottom = Length.Pixels( 8f );
		btn.SetLook( kind, selected: false, disabled: false );

		label = new Label { Parent = btn, Text = text };
		UiStyle.ButtonLabel( label, kind, 14f );
		return btn;
	}

	void EnsureVariationDefault()
	{
		TimeTrialVariationCatalog.EnsureLoaded();
		if ( string.IsNullOrWhiteSpace( _selectedVariationId )
		     && TimeTrialVariationCatalog.All.Count > 0 )
			_selectedVariationId = TimeTrialVariationCatalog.All[0].Id;

		var session = _interaction?.FocusedTimeTrialStand ?? TimeTrialSession.Instance;
		if ( session is { Phase: TimeTrialPhase.WaitingForPlayers }
		     && !string.IsNullOrWhiteSpace( session.ActiveVariationId ) )
			_selectedVariationId = session.ActiveVariationId;
	}

	void RebuildModeRow()
	{
		_modeRow.DeleteChildren( true );
		var session = _interaction?.FocusedTimeTrialStand ?? TimeTrialSession.Instance;
		var lockedTwoPlayer = session is { Phase: TimeTrialPhase.WaitingForPlayers };

		AddModeChip( "Solo", TimeTrialMode.Solo, !lockedTwoPlayer );
		AddModeChip( "1v1", TimeTrialMode.TwoPlayer, true );

		if ( lockedTwoPlayer )
			_selectedMode = TimeTrialMode.TwoPlayer;
	}

	void AddModeChip( string label, TimeTrialMode mode, bool enabled )
	{
		var chip = new TimeTrialClickPanel { Parent = _modeRow };
		chip.Style.PaddingLeft = Length.Pixels( 10f );
		chip.Style.PaddingRight = Length.Pixels( 10f );
		chip.Style.PaddingTop = Length.Pixels( 6f );
		chip.Style.PaddingBottom = Length.Pixels( 6f );
		var selected = _selectedMode == mode;

		if ( enabled )
		{
			chip.Clicked = () =>
			{
				if ( _selectedMode == mode )
					return;
				_selectedMode = mode;
				Refresh( forceRebuild: true );
			};
		}

		chip.SetLook( UiButton.Default, selected, disabled: !enabled );

		var text = new Label { Parent = chip, Text = label };
		UiStyle.ButtonLabel( text, UiButton.Default, 13f );
	}

	void RebuildVariationList()
	{
		_variationList.DeleteChildren( true );
		var session = _interaction?.FocusedTimeTrialStand ?? TimeTrialSession.Instance;
		var canPick = session is null
		              || session.CanSelectVariation( _interaction?.GameObject );

		foreach ( var v in TimeTrialVariationCatalog.All )
		{
			var row = new TimeTrialClickPanel { Parent = _variationList };
			row.Style.PaddingLeft = Length.Pixels( 8f );
			row.Style.PaddingRight = Length.Pixels( 8f );
			row.Style.PaddingTop = Length.Pixels( 6f );
			row.Style.PaddingBottom = Length.Pixels( 6f );
			var selected = string.Equals( _selectedVariationId, v.Id, StringComparison.OrdinalIgnoreCase );

			if ( canPick )
			{
				var id = v.Id;
				row.Clicked = () =>
				{
					if ( string.Equals( _selectedVariationId, id, StringComparison.OrdinalIgnoreCase ) )
						return;
					_selectedVariationId = id;
					Refresh( forceRebuild: true );
				};
			}

			// Locked list (someone else picked): the active race stays readable, the rest dim.
			row.SetLook( UiButton.Default, selected, disabled: !canPick && !selected );

			var text = new Label { Parent = row, Text = v.DisplayName };
			UiStyle.ButtonLabel( text, UiButton.Default, 13f );
		}
	}

	void UpdateFooter()
	{
		if ( _leaveBtn is null || _primaryLabel is null || _status is null )
			return;

		var session = _interaction?.FocusedTimeTrialStand ?? TimeTrialSession.Instance;
		var inQueue = session is not null && _interaction is not null
		              && session.IsPlayerInQueue( _interaction.GameObject );
		var waiting = session is { Phase: TimeTrialPhase.WaitingForPlayers };
		var isLeader = session is not null && _interaction is not null
		               && session.QueueLeaderId == TimeTrialSession.ResolvePlayerKey( _interaction.GameObject );

		_leaveBtn.Style.Set( "display", waiting && inQueue ? "flex" : "none" );

		if ( waiting && !isLeader && !inQueue )
		{
			_primaryLabel.Text = "Join Time Trial";
			_status.Text = string.IsNullOrWhiteSpace( session.ActiveVariationId )
				? $"Waiting for players ({session.ReadyCount}/2)"
				: $"{TimeTrialVariationCatalog.GetOrDefault( session.ActiveVariationId )?.DisplayName} — Join ({session.ReadyCount}/2)";
		}
		else if ( waiting && inQueue )
		{
			_primaryLabel.Text = "Waiting…";
			_status.Text = $"In queue ({session.ReadyCount}/2) — Leave to cancel";
		}
		else
		{
			_primaryLabel.Text = _selectedMode == TimeTrialMode.Solo ? "Start Solo" : "Start 1v1 Queue";
			_status.Text = "Pick a race, then Start";
		}
	}

	void OnPrimary()
	{
		var session = _interaction?.FocusedTimeTrialStand ?? TimeTrialSession.Instance;
		if ( session is { Phase: TimeTrialPhase.WaitingForPlayers } )
		{
			var inQueue = _interaction is not null && session.IsPlayerInQueue( _interaction.GameObject );
			var isLeader = _interaction is not null
			               && session.QueueLeaderId == TimeTrialSession.ResolvePlayerKey( _interaction.GameObject );
			if ( !isLeader && !inQueue )
			{
				_interaction?.OwnerMenuJoin();
				return;
			}

			return;
		}

		_interaction?.OwnerMenuStart( _selectedMode, _selectedVariationId );
	}
}

/// <summary>
/// Clickable row/chip with a depress visual: mouse-down squashes the button, the action fires on
/// mouse-UP. Firing on release also catches a first click whose DOWN got swallowed while the
/// menu was still taking over the cursor (the "have to click twice" complaint). The look comes
/// from the theme (<see cref="SetLook"/>); hover restyles through the real mouse events this
/// panel already receives.
/// </summary>
sealed class TimeTrialClickPanel : Panel
{
	public Action Clicked;

	UiButton _kind = UiButton.Default;
	bool _selected;
	bool _disabled;

	public override bool WantsMouseInput() => true;

	static bool IsClickButton( string button ) => button is "mouseleft" or "mouse1" or "Attack1";

	/// <summary>Theme button surface for this kind; a selected chip reads as pressed with the accent border.</summary>
	public void SetLook( UiButton kind, bool selected, bool disabled )
	{
		_kind = kind;
		_selected = selected;
		_disabled = disabled;
		UiStyle.Button( this, kind );
		ApplyState( hovered: false );
	}

	void ApplyState( bool hovered )
	{
		var canHover = hovered && !_disabled && Clicked is not null;
		UiStyle.SetButtonState( this, _kind, canHover, _selected, _disabled );
		if ( _selected )
			Style.Set( "border-color", UiStyle.Css( UiTheme.Colors.AccentColor ) );
	}

	protected override void OnMouseOver( MousePanelEvent e )
	{
		base.OnMouseOver( e );
		ApplyState( hovered: true );
	}

	protected override void OnMouseOut( MousePanelEvent e )
	{
		base.OnMouseOut( e );
		ApplyState( hovered: false );
	}

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		if ( !IsClickButton( e.Button ) )
			return;

		if ( Clicked is not null )
		{
			Style.Set( "transform", "scale(0.93)" );
			Style.Opacity = 0.8f;
		}

		e.StopPropagation();
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		base.OnMouseUp( e );
		if ( !IsClickButton( e.Button ) )
			return;

		if ( Clicked is not null )
		{
			Style.Set( "transform", "scale(1)" );
			Style.Opacity = 1f;
		}

		Clicked?.Invoke();
		e.StopPropagation();
	}
}
