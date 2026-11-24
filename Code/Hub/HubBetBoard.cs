using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;

namespace Survival;

/// <summary>
/// The bar's bookie (on the bet NPC, beside a <see cref="HubKiosk"/> of kind Bets). Host-side it:
/// <list type="bullet">
/// <item>publishes <see cref="BoardBlob"/>: the fight being announced / fought (teams, fighters and their arena
/// records, the token pool on each team) and the crews still waiting in the queue;</item>
/// <item>takes bets in hub tokens while the coliseum's <see cref="ArenaSession"/> is in its betting window;</item>
/// <item>on the result, records every fighter's fight (and win) in <see cref="HubStore"/> and pays the pool out:
/// winners split everything staked in proportion to their stake (pari-mutuel). A void match, or nobody on the winning
/// team, refunds every bet.</item>
/// </list>
/// </summary>
[Title( "Hub Bet Board" )]
public sealed class HubBetBoard : Component
{
	[Property] public ArenaSession Arena { get; set; }

	/// <summary>Lines: <c>phase|announced|fighting|idle|secondsLeft</c>, <c>team|i|name|pool</c>,
	/// <c>fighter|team|name|record</c>, <c>queue|mode|names</c>, <c>bet|steamKey|team|amount</c>.</summary>
	[Sync( SyncFlags.FromHost )] public string BoardBlob { get; private set; } = "";

	sealed class Bet
	{
		public GameObject Pawn;
		public string SteamKey;
		public string Name;
		public int Team;
		public int Amount;
	}

	readonly List<Bet> _bets = new();
	double _nextPublishAt;

	bool IsHost => GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		ArenaSession.HostMatchResolved += OnMatchResolved;
	}

	protected override void OnDisabled()
	{
		ArenaSession.HostMatchResolved -= OnMatchResolved;
		base.OnDisabled();
	}

	protected override void OnUpdate()
	{
		if ( !IsHost || Time.NowDouble < _nextPublishAt )
			return;
		_nextPublishAt = Time.NowDouble + 1.0;
		HostPublish();
	}

	public bool BetsOpen => Arena is { IsValid: true } a && a.Phase == ArenaPhase.Announced;

	/// <summary>Host: a bet from this pawn (validated here, once).</summary>
	public void HostPlaceBet( GameObject pawn, int team, int amount )
	{
		if ( !IsHost || pawn is null || !BetsOpen || amount <= 0 )
			return;
		if ( !Arena.HostTryDescribeCurrent( out var teams, out var fighters ) || team < 0 || team >= teams.Count )
			return;

		// Fighters do not bet on their own match.
		foreach ( var f in fighters )
		{
			if ( f.Root == pawn )
				return;
		}

		var key = HubIdentity.SteamKey( pawn );
		var record = HubStore.Get( key );
		Bet existing = null;
		foreach ( var b in _bets )
		{
			if ( b.SteamKey == key )
				existing = b;
		}

		// One side per match: you can add to your bet, not hedge it.
		if ( existing is not null && existing.Team != team )
			return;
		if ( amount > record.Tokens )
			return;

		record.Tokens -= amount;
		HubStore.Save( record );
		if ( existing is null )
			_bets.Add( new Bet { Pawn = pawn, SteamKey = key, Name = HubIdentity.DisplayName( pawn ), Team = team, Amount = amount } );
		else
			existing.Amount += amount;

		pawn.Components.Get<PlayerInventoryInteraction>()?.HostRefreshHubRecord();
		Log.Info( $"[Hub] {HubIdentity.DisplayName( pawn )} bet {amount} on {teams[team]}." );
		HostPublish();
	}

	void OnMatchResolved( ArenaSession session, ArenaMatchResult result )
	{
		if ( !IsHost || session != Arena )
			return;

		if ( result.WinningTeam >= 0 )
		{
			foreach ( var f in result.Fighters )
			{
				if ( f.Root is not { IsValid: true } )
					continue;
				var rec = HubStore.Get( HubIdentity.SteamKey( f.Root ) );
				rec.Name = f.Name;
				rec.ArenaFights++;
				if ( f.Team == result.WinningTeam )
					rec.ArenaWins++;
				HubStore.Save( rec );
			}
		}

		var pool = 0;
		var winningStake = 0;
		foreach ( var b in _bets )
		{
			pool += b.Amount;
			if ( b.Team == result.WinningTeam )
				winningStake += b.Amount;
		}

		var refund = result.WinningTeam < 0 || winningStake == 0;
		foreach ( var b in _bets )
		{
			var payout = refund ? b.Amount
				: b.Team == result.WinningTeam ? (int)((long)pool * b.Amount / winningStake) : 0;
			if ( payout <= 0 )
				continue;
			var rec = HubStore.Get( b.SteamKey );
			rec.Tokens += payout;
			HubStore.Save( rec );
			if ( b.Pawn is { IsValid: true } )
				b.Pawn.Components.Get<PlayerInventoryInteraction>()?.HostRefreshHubRecord();
			Log.Info( $"[Hub] {b.Name} {(refund ? "refunded" : "won")} {payout} tokens." );
		}

		_bets.Clear();
		HostPublish();
	}

	void HostPublish()
	{
		var sb = new StringBuilder();
		var arena = Arena;
		if ( arena is { IsValid: true } && arena.HostTryDescribeCurrent( out var teams, out var fighters ) )
		{
			var phase = arena.Phase == ArenaPhase.Announced ? "announced" : "fighting";
			sb.Append( "phase|" ).Append( phase ).Append( '|' ).Append( (int)MathF.Ceiling( arena.AnnouncedSecondsLeft ) ).Append( '\n' );
			for ( var t = 0; t < teams.Count; t++ )
			{
				var teamPool = 0;
				foreach ( var b in _bets )
				{
					if ( b.Team == t )
						teamPool += b.Amount;
				}

				sb.Append( "team|" ).Append( t ).Append( '|' ).Append( Clean( teams[t] ) ).Append( '|' ).Append( teamPool ).Append( '\n' );
			}

			foreach ( var f in fighters )
			{
				var rec = f.Root is { IsValid: true } ? HubStore.Get( HubIdentity.SteamKey( f.Root ) ) : null;
				sb.Append( "fighter|" ).Append( f.Team ).Append( '|' ).Append( Clean( f.Name ) ).Append( '|' )
					.Append( rec?.ArenaRecordText ?? "" ).Append( '\n' );
			}
		}
		else
		{
			sb.Append( "phase|idle|0\n" );
		}

		if ( arena is { IsValid: true } )
		{
			foreach ( var (mode, names) in arena.HostDescribeQueue() )
				sb.Append( "queue|" ).Append( ArenaModeInfo.Display( mode ) ).Append( '|' ).Append( Clean( string.Join( ", ", names ) ) ).Append( '\n' );
		}

		foreach ( var b in _bets )
			sb.Append( "bet|" ).Append( b.SteamKey ).Append( '|' ).Append( b.Team ).Append( '|' ).Append( b.Amount ).Append( '\n' );

		var blob = sb.ToString();
		if ( blob != BoardBlob )
			BoardBlob = blob;
	}

	static string Clean( string s ) => (s ?? "").Replace( '|', '/' ).Replace( '\n', ' ' );
}
