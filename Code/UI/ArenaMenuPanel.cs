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
		Style.BackgroundColor = new Color( 0f, 0f, 0f, 0.45f );
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
		_card.Style.BackgroundColor = new Color( 0.08f, 0.09f, 0.11f, 0.94f );
		_card.Style.Set( "border-radius", "8px" );
		_card.Style.Set( "border", "1px solid #3a3f4a" );

		var title = new Label { Parent = _card, Text = "Arena" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( 22f );

		_status = new Label { Parent = _card, Text = "" };
		_status.Style.FontColor = new Color( 0.75f, 0.8f, 0.85f );
		_status.Style.FontSize = Length.Pixels( 13f );

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
				MakeButton( _footer, "Cancel Queue", new Color( 0.65f, 0.25f, 0.22f ), () =>
				{
					_interaction?.OwnerArenaCancelQueue();
					_nextPollAt = 0;
				} );
			}

			MakeButton( _footer, "Close", new Color( 0.2f, 0.22f, 0.26f ), () => _interaction?.SetArenaMenuOpen( false ) );
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
			var chip = MakeButton( _modeGrid, ArenaModeInfo.Display( mode ),
				enabled ? new Color( 0.2f, 0.45f, 0.85f ) : new Color( 0.16f, 0.17f, 0.2f ),
				enabled
					? () =>
					{
						_interaction?.OwnerArenaQueue( mode );
						// Queued — close so the player can run around while waiting.
						_interaction?.SetArenaMenuOpen( false );
					}
					: null );
			if ( !enabled )
				chip.Style.Opacity = 0.4f;
		}

		MakeButton( _footer, "Close", new Color( 0.2f, 0.22f, 0.26f ), () => _interaction?.SetArenaMenuOpen( false ) );
	}

	static Panel MakeButton( Panel parent, string text, Color background, Action onClick )
	{
		var btn = new TimeTrialClickPanel { Parent = parent };
		btn.Clicked = onClick;
		btn.Style.PaddingLeft = Length.Pixels( 12f );
		btn.Style.PaddingRight = Length.Pixels( 12f );
		btn.Style.PaddingTop = Length.Pixels( 8f );
		btn.Style.PaddingBottom = Length.Pixels( 8f );
		btn.Style.BackgroundColor = background;
		btn.Style.Set( "border-radius", "4px" );
		if ( onClick is not null )
			btn.Style.Set( "cursor", "pointer" );

		var label = new Label { Parent = btn, Text = text };
		label.Style.FontColor = Color.White;
		label.Style.FontSize = Length.Pixels( 14f );
		label.Style.Set( "pointer-events", "none" );
		return btn;
	}
}
