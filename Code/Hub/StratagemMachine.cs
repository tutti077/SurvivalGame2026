using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;

namespace Survival;

/// <summary>
/// The games wing's arcade cabinet (beside a <see cref="HubKiosk"/> of kind Stratagem). The game itself runs on the
/// player's own machine (<see cref="StratagemGame"/>, drawn by <see cref="HubMenuPanel"/>); the host only takes the
/// final score, checks it is possible once, keeps the best per Steam ID in <see cref="HubStore"/>, and publishes the
/// high-score list of the players who are in this session right now (<see cref="ScoresBlob"/>).
/// </summary>
[Title( "Stratagem Machine" )]
public sealed class StratagemMachine : Component
{
	/// <summary>Lines <c>name|score</c>, best first, players present in this session only (host → all).</summary>
	[Sync( SyncFlags.FromHost )] public string ScoresBlob { get; private set; } = "";

	double _nextBoardAt;

	bool IsHost => GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnUpdate()
	{
		// Players come and go: rebuild the present-players board every few seconds (a handful of cached records).
		if ( !IsHost || Time.NowDouble < _nextBoardAt )
			return;
		_nextBoardAt = Time.NowDouble + 3.0;
		HostRebuildBoard();
	}

	/// <summary>Host: a finished run. Impossible scores (faster than ~2.5 codes a second, more points than the codes
	/// could give) are dropped — the one check that stops a hacked client topping the board.</summary>
	public void HostSubmitScore( GameObject pawn, int score, int codes, float seconds )
	{
		if ( !IsHost || pawn is null || score <= 0 || codes <= 0 )
			return;
		if ( codes > seconds * 2.5f + 2f || score > StratagemGame.MaxScoreFor( codes ) )
		{
			Log.Warning( $"[Stratagem] Rejected score {score} ({codes} codes in {seconds:0.0}s) from {HubIdentity.DisplayName( pawn )}." );
			return;
		}

		var record = HubStore.Get( HubIdentity.SteamKey( pawn ) );
		record.Name = HubIdentity.DisplayName( pawn );
		if ( score <= record.StratagemBest )
			return;

		record.StratagemBest = score;
		HubStore.Save( record );
		pawn.Components.Get<PlayerInventoryInteraction>()?.HostRefreshHubRecord();
		Log.Info( $"[Stratagem] New best for {record.Name}: {score}." );
		HostRebuildBoard();
	}

	void HostRebuildBoard()
	{
		var rows = new List<(string Name, int Score)>();
		foreach ( var pawn in Scene.GetAllComponents<PlayerInventoryInteraction>() )
		{
			if ( pawn is null || !pawn.IsValid() )
				continue;
			var rec = HubStore.Get( HubIdentity.SteamKey( pawn.GameObject ) );
			rows.Add( (HubIdentity.DisplayName( pawn.GameObject ), rec.StratagemBest) );
		}

		rows.Sort( static ( a, b ) => b.Score.CompareTo( a.Score ) );
		var sb = new StringBuilder();
		foreach ( var r in rows )
			sb.Append( r.Name.Replace( '|', '/' ) ).Append( '|' ).Append( r.Score ).Append( '\n' );
		var blob = sb.ToString();
		if ( blob != ScoresBlob )
			ScoresBlob = blob;
	}
}

/// <summary>
/// One run of Stratagem Hero, local to the player. Rounds of codes against a draining clock: each finished code adds a
/// little time, a wrong arrow restarts that code, clearing the round refills the clock for the next (longer) round.
/// The run ends when the clock runs out.
/// </summary>
public sealed class StratagemGame
{
	public const float RoundSeconds = 10f;
	public const float MaxSeconds = 12f;
	public const float SecondsPerCode = 1f;
	public const int PointsPerArrow = 5;
	public const int PerfectBonus = 100;

	public static int CodesInRound( int round ) => Math.Min( 12, 4 + round );
	public static int RoundBonus( int round ) => 50 + 25 * round;

	/// <summary>Upper bound for a run of this many codes (host validation): every code 6 arrows long, every round
	/// as short as possible with full time and perfect bonuses.</summary>
	public static int MaxScoreFor( int codes )
	{
		var total = 0;
		var round = 1;
		var left = codes;
		while ( left > 0 )
		{
			var n = Math.Min( left, CodesInRound( round ) );
			total += n * 6 * PointsPerArrow + RoundBonus( round ) + (int)(MaxSeconds * 10) + PerfectBonus;
			left -= n;
			round++;
		}

		return total;
	}

	public int Round { get; private set; } = 1;
	public int Score { get; private set; }
	public int CodesDone { get; private set; }
	public float TimeLeft { get; private set; } = RoundSeconds;
	public float Elapsed { get; private set; }
	public bool Over { get; private set; }
	public bool RoundMistake { get; private set; }
	/// <summary>Arrows of the current code already entered.</summary>
	public int Progress { get; private set; }
	/// <summary>Seconds left on the red flash after a wrong arrow (for the UI).</summary>
	public float WrongFlash { get; private set; }
	/// <summary>Seconds left on the "round cleared" banner (the clock is paused meanwhile).</summary>
	public float RoundBanner { get; private set; }
	public string LastRoundText { get; private set; } = "";

	public readonly List<StratagemCode> Queue = new();
	readonly Random _rng = new();

	public StratagemGame()
	{
		FillRound();
	}

	public StratagemCode Current => Queue.Count > 0 ? Queue[0] : null;

	void FillRound()
	{
		Queue.Clear();
		var all = StratagemCatalog.All;
		// Early rounds stay on the short codes; the long ones open up as rounds go on.
		var maxLen = Round <= 1 ? 4 : Round <= 3 ? 5 : 6;
		var pool = new List<StratagemCode>();
		foreach ( var c in all )
		{
			if ( c.Code.Length <= maxLen )
				pool.Add( c );
		}

		if ( pool.Count == 0 )
			pool.AddRange( all );

		StratagemCode last = null;
		for ( var i = 0; i < CodesInRound( Round ); i++ )
		{
			StratagemCode pick;
			var tries = 0;
			do
			{
				pick = pool[_rng.Next( pool.Count )];
			} while ( pick == last && pool.Count > 1 && ++tries < 8 );

			Queue.Add( pick );
			last = pick;
		}

		Progress = 0;
		RoundMistake = false;
		TimeLeft = RoundSeconds;
	}

	public void Tick( float dt )
	{
		if ( Over )
			return;

		WrongFlash = MathF.Max( 0f, WrongFlash - dt );
		if ( RoundBanner > 0f )
		{
			RoundBanner = MathF.Max( 0f, RoundBanner - dt );
			if ( RoundBanner <= 0f )
				FillRound();
			return;
		}

		Elapsed += dt;
		TimeLeft -= dt;
		if ( TimeLeft <= 0f )
		{
			TimeLeft = 0f;
			Over = true;
		}
	}

	/// <summary>One arrow: 'U' 'D' 'L' 'R'.</summary>
	public void Press( char arrow )
	{
		if ( Over || RoundBanner > 0f || Current is null )
			return;

		var code = Current.Code;
		if ( code[Progress] != arrow )
		{
			Progress = 0;
			RoundMistake = true;
			WrongFlash = 0.25f;
			return;
		}

		Progress++;
		if ( Progress < code.Length )
			return;

		Score += code.Length * PointsPerArrow;
		CodesDone++;
		TimeLeft = MathF.Min( MaxSeconds, TimeLeft + SecondsPerCode );
		Queue.RemoveAt( 0 );
		Progress = 0;
		if ( Queue.Count > 0 )
			return;

		var timeBonus = (int)(TimeLeft * 10f);
		var perfect = RoundMistake ? 0 : PerfectBonus;
		Score += RoundBonus( Round ) + timeBonus + perfect;
		LastRoundText = $"Round {Round} cleared  +{RoundBonus( Round )} round  +{timeBonus} time{(perfect > 0 ? $"  +{perfect} perfect" : "")}";
		Round++;
		RoundBanner = 1.5f;
	}
}
