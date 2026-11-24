using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// The hub's per-player record, keyed by Steam ID: hub tokens (the betting currency — never touches the survival
/// save), arena fight record and the stratagem high score. Host-only.
/// <para>
/// Stand-in for the company database (Mark 2026-11-10): one JSON file per player under
/// <c>FileSystem.Data/2Tgames/hub/players/&lt;steamid&gt;.json</c>, written through on every change. Everything that
/// reads or writes hub records goes through this class, so swapping it for a web backend changes nothing else.
/// </para>
/// </summary>
public static class HubStore
{
	public const string Folder = QuestSaveStore.RootFolder + "/hub/players";

	/// <summary>Free tokens granted once per UTC day, the first time the player shows up in a hub that day.</summary>
	public const int DailyTokenAllowance = 100;

	static readonly Dictionary<string, HubPlayerRecord> Cache = new( StringComparer.Ordinal );

	static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	static string PathFor( string steamKey ) => $"{Folder}/{QuestSaveStore.SanitizeKey( steamKey )}.json";

	/// <summary>Host: the record for this Steam key (created empty on first use, cached after the first read).</summary>
	public static HubPlayerRecord Get( string steamKey )
	{
		if ( string.IsNullOrEmpty( steamKey ) )
			steamKey = "local";

		if ( Cache.TryGetValue( steamKey, out var cached ) )
			return cached;

		HubPlayerRecord record = null;
		var path = PathFor( steamKey );
		try
		{
			if ( FileSystem.Data.FileExists( path ) )
				record = JsonSerializer.Deserialize<HubPlayerRecord>( FileSystem.Data.ReadAllText( path ), JsonOptions );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[Hub] Could not read '{path}': {ex.Message}" );
		}

		record ??= new HubPlayerRecord();
		record.SteamKey = steamKey;
		Cache[steamKey] = record;
		return record;
	}

	/// <summary>Host: write this record through to disk.</summary>
	public static void Save( HubPlayerRecord record )
	{
		if ( record is null || string.IsNullOrEmpty( record.SteamKey ) )
			return;

		try
		{
			if ( !FileSystem.Data.DirectoryExists( Folder ) )
				FileSystem.Data.CreateDirectory( Folder );
			FileSystem.Data.WriteAllText( PathFor( record.SteamKey ), JsonSerializer.Serialize( record, JsonOptions ) );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[Hub] Could not save hub record {record.SteamKey}: {ex.Message}" );
		}
	}

	/// <summary>Host: grant today's allowance if this player has not had it yet. Returns true when tokens were added.</summary>
	public static bool TryGrantDailyAllowance( HubPlayerRecord record )
	{
		var today = DateTime.UtcNow.ToString( "yyyy-MM-dd" );
		if ( record is null || record.AllowanceDay == today )
			return false;

		record.AllowanceDay = today;
		record.Tokens += DailyTokenAllowance;
		Save( record );
		return true;
	}

	/// <summary>Drops the cache (console <c>hub_tokens</c> edits go through <see cref="Get"/> and stay cached).</summary>
	public static void ClearCache() => Cache.Clear();
}

/// <summary>One player's hub record (JSON shape of the store file).</summary>
public sealed class HubPlayerRecord
{
	public string SteamKey { get; set; } = "";
	public string Name { get; set; } = "";
	public int Tokens { get; set; }
	public string AllowanceDay { get; set; } = "";
	public int ArenaFights { get; set; }
	public int ArenaWins { get; set; }
	public int StratagemBest { get; set; }

	public int ArenaLosses => Math.Max( 0, ArenaFights - ArenaWins );

	public string ArenaRecordText =>
		ArenaFights == 0 ? "no fights yet" : $"{ArenaWins}W {ArenaLosses}L ({ArenaWins * 100 / Math.Max( 1, ArenaFights )}%)";
}

/// <summary>Steam identity of a pawn (the hub's player key). Host side: the owning connection; offline: the local user.</summary>
public static class HubIdentity
{
	public static string SteamKey( GameObject pawn )
	{
		var connection = pawn?.Network is { Active: true, Owner: { } owner } ? owner : Connection.Local;
		var id = connection?.SteamId.ValueUnsigned ?? 0UL;
		return id != 0UL ? id.ToString() : "local";
	}

	public static string DisplayName( GameObject pawn )
	{
		var connection = pawn?.Network is { Active: true, Owner: { } owner } ? owner : Connection.Local;
		var name = connection?.DisplayName;
		return string.IsNullOrWhiteSpace( name ) ? "Player" : name;
	}
}
