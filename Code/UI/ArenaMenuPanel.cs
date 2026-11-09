using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Center-screen arena queue menu: mode buttons (1v1 … 4v4v4), greyed when the crew doesn't
/// fit, leader-only queue/cancel, "waiting on more players" while queued.
/// </summary>
public sealed class ArenaMenuPanel : Panel
{
	readonly PlayerInventoryInteraction _interaction;
	Panel _card;
	Label _status;
	Panel _modeGrid;
	Panel _footer;
	bool _built;
	bool _open;
	double _nextPollAt;

	// Rebuild only when lobby/crew state actually changes — destroying buttons under the
	// cursor on a timer eats clicks (same reason TimeTrialMenuPanel dirty-checks).
	ArenaPhase _lastPhase = (ArenaPhase)(-1);
	string _lastQueueBlob = "\0";
	string _lastCrewBlob = "\0";

	public ArenaMenuPanel( PlayerInventoryInteraction interaction )
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
			Rebuild();
		}
	}

	public void TickOpen()
	{
		if ( !_open || !_built || Time.NowDouble < _nextPollAt )
			return;

		_nextPollAt = Time.NowDouble + 0.2;

		var session = _interaction?.ResolveArenaSession();
		var phase = session?.Phase ?? ArenaPhase.Idle;
		var queueBlob = session?.QueueBlob ?? "";
		var crewBlob = _interaction?.Components.Get<PlayerCrew>()?.MyCrewBlob ?? "";

		if ( phase == _lastPhase
		     && ReferenceEquals( queueBlob, _lastQueueBlob )
		     && ReferenceEquals( crewBlob, _lastCrewBlob ) )
			return;

		Rebuild();
	}

	void EnsureBuilt()
	{
		if ( _built )
			return;
		_built = true;

		_card = new Panel { Parent = this };
		_card.Style.Set( "flex-direction", "column" );
		_card.Style.Set( "gap", "10px" );
		_card.Style.Width = Length.Pixels( 380f );
		_card.Style.PaddingLeft = Length.Pixels( 16f );
		_card.Style.PaddingRight = Length.Pixels( 16f );
		_card.Style.PaddingTop = Length.Pixels( 14f );
		_card.Style.PaddingBottom = Length.Pixels( 14f );
		UiStyle.Surface( _card, UiSurface.Frame );

		UiStyle.AddTitle( _card, "Arena", 22f );

		_status = new Label { Parent = _card, Text = "" };
		UiStyle.Text( _status, UiText.Secondary );

		_modeGrid = new Panel { Parent = _card };
		_modeGrid.Style.Set( "flex-direction", "row" );
		_modeGrid.Style.Set( "flex-wrap", "wrap" );
		_modeGrid.Style.Set( "gap", "8px" );

		_footer = new Panel { Parent = _card };
		_footer.Style.Set( "flex-direction", "row" );
		_footer.Style.Set( "gap", "8px" );
		_footer.Style.Set( "justify-content", "space-between" );
		_footer.Style.MarginTop = Length.Pixels( 6f );
	}

	void Rebuild()
	{
		var session = _interaction?.ResolveArenaSession();
		var playerCrew = _interaction?.Components.Get<PlayerCrew>();
		var localKey = TimeTrialSession.ResolvePlayerKey( _interaction?.GameObject );
		var crew = playerCrew?.GetMyCrew();
		var crewKey = crew?.Key ?? localKey;
		var crewSize = crew?.Members.Count ?? 1;
		var isLeader = crew is null || crew.LeaderId == localKey;
		var queued = session is not null && session.TryGetQueuedMode( crewKey, out var queuedMode );
		ArenaMode queuedModeValue = default;
		if ( queued )
			session.TryGetQueuedMode( crewKey, out queuedModeValue );

		_lastPhase = session?.Phase ?? ArenaPhase.Idle;
		_lastQueueBlob = session?.QueueBlob ?? "";
		_lastCrewBlob = playerCrew?.MyCrewBlob ?? "";

		_modeGrid.DeleteChildren( true );
		_footer.DeleteChildren( true );

		if ( session is null )
		{
			_status.Text = "No arena session in this world.";
			return;
		}

		if ( queued )
		{
			var current = session.GetQueuedPlayerCount( queuedModeValue );
			var needed = ArenaModeInfo.TeamSize( queuedModeValue ) * ArenaModeInfo.TeamCount( queuedModeValue );
			_status.Text = $"Waiting on more players… {current}/{needed} ({ArenaModeInfo.Display( queuedModeValue )})";
			if ( isLeader )
			{
				MakeButton( _footer, "Cancel Queue", UiButton.Danger, () =>
				{
					_interaction?.OwnerArenaCancelQueue();
					_nextPollAt = 0;
				} );
			}

			MakeButton( _footer, "Close", UiButton.Default, () => _interaction?.SetArenaMenuOpen( false ) );
			return;
		}

		_status.Text = !isLeader
			? "Only the crew leader can start arena battles."
			: crewSize > 1
				? $"Your crew of {crewSize} — pick a mode."
				: "Pick a mode to queue solo.";

		if ( session.Phase != ArenaPhase.Idle )
			_status.Text += " A battle is in progress — queues start after it ends.";

		foreach ( var mode in ArenaModeInfo.All )
		{
			var fits = crewSize <= ArenaModeInfo.TeamSize( mode );
			var enabled = fits && isLeader;
			MakeButton( _modeGrid, ArenaModeInfo.Display( mode ), UiButton.Default,
				enabled
					? () =>
					{
						_interaction?.OwnerArenaQueue( mode );
						// Queued — close so the player can run around while waiting.
						_interaction?.SetArenaMenuOpen( false );
					}
					: null,
				disabled: !enabled );
		}

		MakeButton( _footer, "Close", UiButton.Default, () => _interaction?.SetArenaMenuOpen( false ) );
	}

	static Panel MakeButton( Panel parent, string text, UiButton kind, Action onClick, bool disabled = false )
	{
		var btn = new TimeTrialClickPanel { Parent = parent };
		btn.Clicked = onClick;
		btn.Style.PaddingLeft = Length.Pixels( 12f );
		btn.Style.PaddingRight = Length.Pixels( 12f );
		btn.Style.PaddingTop = Length.Pixels( 8f );
		btn.Style.PaddingBottom = Length.Pixels( 8f );
		btn.SetLook( kind, selected: false, disabled: disabled );

		var label = new Label { Parent = btn, Text = text };
		UiStyle.ButtonLabel( label, kind, 14f );
		return btn;
	}
}
