using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Center-screen hub menu, one card that shows what the focused <see cref="HubKiosk"/> is:
/// <list type="bullet">
/// <item><b>Bets</b> — the fight being announced / fought (teams, fighters with their arena records, the token pool
/// per team), bet buttons while the betting window is open, the crews waiting in the queue, your tokens.</item>
/// <item><b>Stratagem</b> — Stratagem Hero (<see cref="StratagemGame"/>, WASD or arrow keys) and the high scores of the
/// players in this session.</item>
/// </list>
/// The card is a fixed size and its blocks keep their place (no reflow); it rebuilds only when its data changed, so
/// buttons are never destroyed under the cursor on a timer.
/// </summary>
public sealed class HubMenuPanel : Panel
{
	const float CardWidth = 760f;
	const float CardHeight = 560f;

	readonly PlayerInventoryInteraction _interaction;
	Panel _card;
	Label _title;
	Label _tokens;
	Panel _body;
	Panel _footer;
	bool _open;
	HubKioskKind _kind;
	string _lastKey = "\0";

	// Stratagem view: built once per open, then only text / widths change every frame.
	StratagemGame _game;
	bool _gameSubmitted;
	Label _codeName;
	Panel _arrowRow;
	readonly List<Label> _arrowLabels = new();
	Label _nextCodes;
	Panel _timerFill;
	Label _scoreLine;
	Label _roundLine;
	Panel _scores;
	Panel _gameButtons;
	string _builtCode = "\0";

	public HubMenuPanel( PlayerInventoryInteraction interaction )
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

		_card = new Panel { Parent = this };
		_card.Style.Set( "flex-direction", "column" );
		_card.Style.Set( "gap", "10px" );
		_card.Style.Width = Length.Pixels( CardWidth );
		_card.Style.Height = Length.Pixels( CardHeight );
		_card.Style.Set( "padding", "18px" );
		UiStyle.Surface( _card, UiSurface.Frame );

		var head = new Panel { Parent = _card };
		head.Style.Set( "flex-direction", "row" );
		head.Style.Set( "justify-content", "space-between" );
		head.Style.Set( "align-items", "center" );
		_title = new Label { Parent = head, Text = "" };
		UiStyle.Text( _title, UiText.Title, 22f );
		_tokens = new Label { Parent = head, Text = "" };
		UiStyle.Text( _tokens, UiText.Accent, 17f );

		_body = new Panel { Parent = _card };
		_body.Style.Set( "flex-grow", "1" );
		_body.Style.Set( "flex-direction", "column" );
		_body.Style.Set( "gap", "8px" );
		_body.Style.Set( "overflow", "hidden" );

		_footer = new Panel { Parent = _card };
		_footer.Style.Set( "flex-direction", "row" );
		_footer.Style.Set( "justify-content", "flex-end" );
		_footer.Style.Set( "gap", "8px" );
		MakeButton( _footer, "Close", UiButton.Default, () => _interaction?.SetHubMenuOpen( false ) );
	}

	public override bool WantsMouseInput() => _open;

	public void SetOpen( bool open )
	{
		_open = open;
		Style.Set( "display", open ? "flex" : "none" );
		_game = null;
		_lastKey = "\0";
		if ( !open )
			return;

		_kind = _interaction?.FocusedHubKiosk?.Kind ?? HubKioskKind.Bets;
		_title.Text = _kind == HubKioskKind.Bets ? "Arena Bets" : "Stratagem Hero";
		_interaction?.OwnerRequestHubRefresh();
		if ( _kind == HubKioskKind.Stratagem )
			BuildStratagem();
		TickOpen();
	}

	/// <summary>Every frame while open (the stratagem game needs frame input; the bets view dirty-checks its data).</summary>
	public void TickOpen()
	{
		if ( !_open )
			return;

		_tokens.Text = $"{_interaction?.HubTokens ?? 0} tokens";
		if ( _kind == HubKioskKind.Bets )
			TickBets();
		else
			TickStratagem();
	}

	// ── Bets ────────────────────────────────────────────────────────────────────

	HubBetBoard Board => _interaction?.FocusedHubKiosk?.Components.Get<HubBetBoard>();

	void TickBets()
	{
		var board = Board;
		var key = (board?.BoardBlob ?? "") + "#" + (_interaction?.HubTokens ?? 0);
		if ( key == _lastKey )
			return;
		_lastKey = key;
		RebuildBets( board );
	}

	void RebuildBets( HubBetBoard board )
	{
		_body.DeleteChildren( true );
		if ( board is null )
		{
			UiStyle.AddLabel( _body, "The bookie is not taking bets right now.", UiText.Secondary, 17f );
			return;
		}

		var phase = "idle";
		var secondsLeft = 0;
		var teams = new List<(string Name, int Pool)>();
		var fighters = new List<(int Team, string Name, string Record)>();
		var queue = new List<(string Mode, string Names)>();
		var myKey = HubIdentity.SteamKey( _interaction?.GameObject );
		var myTeam = -1;
		var myAmount = 0;

		foreach ( var line in (board.BoardBlob ?? "").Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var f = line.Split( '|' );
			switch ( f[0] )
			{
				case "phase" when f.Length >= 3:
					phase = f[1];
					int.TryParse( f[2], out secondsLeft );
					break;
				case "team" when f.Length >= 4:
					int.TryParse( f[3], out var pool );
					teams.Add( (f[2], pool) );
					break;
				case "fighter" when f.Length >= 4:
					int.TryParse( f[1], out var team );
					fighters.Add( (team, f[2], f[3]) );
					break;
				case "queue" when f.Length >= 3:
					queue.Add( (f[1], f[2]) );
					break;
				case "bet" when f.Length >= 4 && f[1] == myKey:
					int.TryParse( f[2], out myTeam );
					int.TryParse( f[3], out myAmount );
					break;
			}
		}

		var status = phase switch
		{
			"announced" => $"Bets are open. The fight starts in {secondsLeft} s.",
			"fighting" => "Fight in progress: bets are closed. Watch it on the big screen!",
			_ => "No fight announced yet. Queue up at the arena kiosk in the hall.",
		};
		UiStyle.AddLabel( _body, status, UiText.Body, 17f );

		if ( teams.Count > 0 )
		{
			var row = new Panel { Parent = _body };
			row.Style.Set( "flex-direction", "row" );
			row.Style.Set( "gap", "10px" );
			var totalPool = 0;
			foreach ( var t in teams )
				totalPool += t.Pool;

			for ( var i = 0; i < teams.Count; i++ )
			{
				var col = new Panel { Parent = row };
				col.Style.Set( "flex-direction", "column" );
				col.Style.Set( "gap", "4px" );
				col.Style.Set( "flex-grow", "1" );
				col.Style.Set( "flex-basis", "0" );
				col.Style.Set( "padding", "10px" );
				UiStyle.Surface( col, UiSurface.Raised );

				UiStyle.AddLabel( col, teams[i].Name, UiText.Header, 20f );
				var odds = teams[i].Pool > 0 ? $"pays {(float)totalPool / teams[i].Pool:0.0}x" : "no bets yet";
				UiStyle.AddLabel( col, $"Pool {teams[i].Pool} tokens ({odds})", UiText.Secondary, 15f );
				foreach ( var f in fighters )
				{
					if ( f.Team == i )
						UiStyle.AddLabel( col, $"{f.Name}: {f.Record}", UiText.Body, 17f );
				}

				if ( phase != "announced" )
					continue;

				var buttons = new Panel { Parent = col };
				buttons.Style.Set( "flex-direction", "row" );
				buttons.Style.Set( "gap", "6px" );
				buttons.Style.MarginTop = Length.Pixels( 6f );
				var team = i;
				var lockedOut = myTeam >= 0 && myTeam != i;
				foreach ( var amount in new[] { 10, 50, 100 } )
				{
					var can = !lockedOut && (_interaction?.HubTokens ?? 0) >= amount;
					MakeButton( buttons, $"+{amount}", UiButton.Primary,
						can ? () => _interaction?.OwnerPlaceBet( Board, team, amount ) : null, disabled: !can );
				}
			}
		}

		if ( myAmount > 0 && myTeam >= 0 && myTeam < teams.Count )
			UiStyle.AddLabel( _body, $"Your bet: {myAmount} tokens on {teams[myTeam].Name}.", UiText.Accent, 17f );

		UiStyle.AddLabel( _body, "Waiting in the queue", UiText.Header, 20f );
		if ( queue.Count == 0 )
			UiStyle.AddLabel( _body, "Nobody is queued.", UiText.Secondary, 17f );
		foreach ( var q in queue )
			UiStyle.AddLabel( _body, $"{q.Mode}: {q.Names}", UiText.Body, 17f );

		UiStyle.AddLabel( _body, "Winners split the whole pool by stake. A cancelled fight refunds every bet.", UiText.Muted, 14f );
	}

	// ── Stratagem ───────────────────────────────────────────────────────────────

	StratagemMachine Machine => _interaction?.FocusedHubKiosk?.Components.Get<StratagemMachine>();

	void BuildStratagem()
	{
		_body.DeleteChildren( true );
		_arrowLabels.Clear();
		_builtCode = "\0";

		var split = new Panel { Parent = _body };
		split.Style.Set( "flex-direction", "row" );
		split.Style.Set( "gap", "14px" );
		split.Style.Set( "flex-grow", "1" );

		var game = new Panel { Parent = split };
		game.Style.Set( "flex-direction", "column" );
		game.Style.Set( "align-items", "center" );
		game.Style.Set( "gap", "10px" );
		game.Style.Width = Length.Pixels( 450f );
		game.Style.Set( "padding", "14px" );
		UiStyle.Surface( game, UiSurface.Sunken );

		_roundLine = UiStyle.AddLabel( game, "", UiText.Secondary, 17f );
		_codeName = UiStyle.AddLabel( game, "", UiText.Header, 24f );
		_arrowRow = new Panel { Parent = game };
		_arrowRow.Style.Set( "flex-direction", "row" );
		_arrowRow.Style.Set( "gap", "10px" );
		_arrowRow.Style.Height = Length.Pixels( 60f );
		_arrowRow.Style.Set( "align-items", "center" );

		var track = new Panel { Parent = game };
		track.Style.Width = Length.Pixels( 400f );
		track.Style.Height = Length.Pixels( 14f );
		_timerFill = new Panel { Parent = track };
		_timerFill.Style.Height = Length.Percent( 100f );
		UiStyle.Bar( track, _timerFill, UiTheme.Colors.AccentColor );

		_scoreLine = UiStyle.AddLabel( game, "", UiText.Value, 20f );
		_nextCodes = UiStyle.AddLabel( game, "", UiText.Secondary, 15f );
		_gameButtons = new Panel { Parent = game };
		_gameButtons.Style.Set( "flex-direction", "row" );
		_gameButtons.Style.Set( "gap", "8px" );
		_gameButtons.Style.Height = Length.Pixels( 44f );

		var side = new Panel { Parent = split };
		side.Style.Set( "flex-direction", "column" );
		side.Style.Set( "gap", "4px" );
		side.Style.Set( "flex-grow", "1" );
		side.Style.Set( "padding", "12px" );
		UiStyle.Surface( side, UiSurface.Raised );
		UiStyle.AddLabel( side, "High scores here now", UiText.Header, 20f );
		_scores = new Panel { Parent = side };
		_scores.Style.Set( "flex-direction", "column" );
		_scores.Style.Set( "gap", "3px" );

		ShowIdleButtons( "Start" );
	}

	void ShowIdleButtons( string startText )
	{
		_gameButtons.DeleteChildren( true );
		MakeButton( _gameButtons, startText, UiButton.Primary, () =>
		{
			_game = new StratagemGame();
			_gameSubmitted = false;
			_gameButtons.DeleteChildren( true );
		} );
	}

	void TickStratagem()
	{
		// High score board (players present in this session), rebuilt when it changes.
		var blob = Machine?.ScoresBlob ?? "";
		if ( blob != _lastKey )
		{
			_lastKey = blob;
			_scores.DeleteChildren( true );
			var myName = HubIdentity.DisplayName( _interaction?.GameObject );
			var rank = 1;
			foreach ( var line in blob.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
			{
				var f = line.Split( '|' );
				if ( f.Length < 2 )
					continue;
				UiStyle.AddLabel( _scores, $"{rank++}. {f[0]}   {f[1]}", f[0] == myName ? UiText.Accent : UiText.Body, 17f );
			}

			if ( rank == 1 )
				UiStyle.AddLabel( _scores, "No scores yet.", UiText.Secondary, 17f );
		}

		if ( _game is null )
		{
			_roundLine.Text = "Enter each code with WASD or the arrow keys.";
			_codeName.Text = "Stratagem Hero";
			SetArrows( "", 0, false );
			_scoreLine.Text = $"Your best: {_interaction?.HubStratagemBest ?? 0}";
			_nextCodes.Text = "Codes act out their names: Purple Portal goes all the way round.";
			_timerFill.Style.Width = Length.Percent( 100f );
			return;
		}

		ReadArrows( _game );
		_game.Tick( Time.Delta );

		if ( _game.Over )
		{
			if ( !_gameSubmitted )
			{
				_gameSubmitted = true;
				_interaction?.OwnerSubmitStratagemScore( Machine, _game.Score, _game.CodesDone, _game.Elapsed );
				ShowIdleButtons( "Play Again" );
			}

			_roundLine.Text = $"Out of time on round {_game.Round}.";
			_codeName.Text = $"Final score {_game.Score}";
			SetArrows( "", 0, false );
			_scoreLine.Text = $"{_game.CodesDone} codes   ·   best {Math.Max( _game.Score, _interaction?.HubStratagemBest ?? 0 )}";
			_nextCodes.Text = "";
			_timerFill.Style.Width = Length.Percent( 0f );
			return;
		}

		var current = _game.Current;
		if ( _game.RoundBanner > 0f || current is null )
		{
			_roundLine.Text = _game.LastRoundText;
			_codeName.Text = $"Round {_game.Round}";
			SetArrows( "", 0, false );
		}
		else
		{
			_roundLine.Text = $"Round {_game.Round}   ·   {_game.Queue.Count} left";
			_codeName.Text = current.Name;
			SetArrows( current.Code, _game.Progress, _game.WrongFlash > 0f );
		}

		_scoreLine.Text = $"Score {_game.Score}";
		var next = new List<string>();
		for ( var i = 1; i < Math.Min( 4, _game.Queue.Count ); i++ )
			next.Add( _game.Queue[i].Name );
		_nextCodes.Text = next.Count > 0 ? "Next: " + string.Join( ", ", next ) : "";
		_timerFill.Style.Width = Length.Percent( 100f * _game.TimeLeft / StratagemGame.MaxSeconds );
	}

	static void ReadArrows( StratagemGame game )
	{
		if ( Input.Pressed( "Forward" ) || Input.Pressed( "ArrowUp" ) ) game.Press( 'U' );
		if ( Input.Pressed( "Backward" ) || Input.Pressed( "ArrowDown" ) ) game.Press( 'D' );
		if ( Input.Pressed( "Left" ) || Input.Pressed( "ArrowLeft" ) ) game.Press( 'L' );
		if ( Input.Pressed( "Right" ) || Input.Pressed( "ArrowRight" ) ) game.Press( 'R' );
	}

	void SetArrows( string code, int progress, bool wrong )
	{
		if ( code != _builtCode )
		{
			_builtCode = code;
			_arrowRow.DeleteChildren( true );
			_arrowLabels.Clear();
			foreach ( var c in code )
			{
				var l = new Label { Parent = _arrowRow, Text = StratagemCatalog.Arrows( c.ToString() ) };
				l.Style.FontSize = Length.Pixels( 44f );
				_arrowLabels.Add( l );
			}
		}

		var colors = UiTheme.Colors;
		for ( var i = 0; i < _arrowLabels.Count; i++ )
		{
			_arrowLabels[i].Style.FontColor = wrong ? colors.NegativeColor
				: i < progress ? colors.AccentBrightColor
				: colors.TextMutedColor;
		}
	}

	static Panel MakeButton( Panel parent, string text, UiButton kind, Action onClick, bool disabled = false )
	{
		var btn = new TimeTrialClickPanel { Parent = parent };
		btn.Clicked = onClick;
		btn.Style.PaddingLeft = Length.Pixels( 14f );
		btn.Style.PaddingRight = Length.Pixels( 14f );
		btn.Style.PaddingTop = Length.Pixels( 8f );
		btn.Style.PaddingBottom = Length.Pixels( 8f );
		btn.SetLook( kind, selected: false, disabled: disabled );
		var label = new Label { Parent = btn, Text = text };
		UiStyle.ButtonLabel( label, kind, 17f );
		return btn;
	}
}
