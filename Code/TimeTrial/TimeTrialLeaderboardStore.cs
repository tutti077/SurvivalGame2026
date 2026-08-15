using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Host-writable top-10 times per variation (<see cref="FileSystem.Data"/>).</summary>
public static class TimeTrialLeaderboardStore
{
	public const int MaxEntries = 10;

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
	};

	static bool _purgedLegacy;

	static string FileName( string variationId )
	{
		var id = Sanitize( variationId );
		return $"time_trial_lb_{id}.json";
	}

	static string Sanitize( string variationId )
	{
		if ( string.IsNullOrWhiteSpace( variationId ) )
			return "unknown";
		var chars = variationId.Trim().ToLowerInvariant().ToCharArray();
		for ( var i = 0; i < chars.Length; i++ )
		{
			var c = chars[i];
			if ( char.IsLetterOrDigit( c ) || c is '_' or '-' )
				continue;
			chars[i] = '_';
		}

		return new string( chars );
	}

	/// <summary>Deletes pre-variation leaderboard filenames. Safe every session start.</summary>
	public static void DiscardLegacyBoards()
	{
		if ( _purgedLegacy )
			return;

		_purgedLegacy = true;
		try
		{
			foreach ( var name in new[]
			         {
				         "time_trial_leaderboard.json",
				         "time_trial_leaderboard_v1.json",
				         "time_trial_leaderboard_v2.json",
			         } )
			{
				if ( !FileSystem.Data.FileExists( name ) )
					continue;
				FileSystem.Data.DeleteFile( name );
				Log.Info( $"[TimeTrialLeaderboard] Discarded legacy file {name}." );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[TimeTrialLeaderboard] Legacy purge failed: {ex.Message}" );
		}
	}

	public static IReadOnlyList<TimeTrialLeaderboardEntry> Load( string variationId )
	{
		DiscardLegacyBoards();
		try
		{
			var path = FileName( variationId );
			if ( !FileSystem.Data.FileExists( path ) )
				return Array.Empty<TimeTrialLeaderboardEntry>();

			var json = FileSystem.Data.ReadAllText( path );
			var file = JsonSerializer.Deserialize<LeaderboardFile>( json, JsonOptions );
			if ( file?.Entries is null || file.Entries.Count == 0 )
				return Array.Empty<TimeTrialLeaderboardEntry>();

			file.Entries.Sort( static ( a, b ) => a.TimeSeconds.CompareTo( b.TimeSeconds ) );
			if ( file.Entries.Count > MaxEntries )
				file.Entries.RemoveRange( MaxEntries, file.Entries.Count - MaxEntries );

			return file.Entries;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[TimeTrialLeaderboard] Load failed: {ex.Message}" );
			return Array.Empty<TimeTrialLeaderboardEntry>();
		}
	}

	public static void Clear( string variationId )
	{
		try
		{
			DiscardLegacyBoards();
			var json = JsonSerializer.Serialize(
				new LeaderboardFile { VariationId = variationId ?? "", Entries = new() },
				JsonOptions );
			FileSystem.Data.WriteAllText( FileName( variationId ), json );
			Log.Info( $"[TimeTrialLeaderboard] Cleared ({variationId})." );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[TimeTrialLeaderboard] Clear failed: {ex.Message}" );
		}
	}

	/// <summary>Inserts a finish if it ranks in the top 10. Returns true when the board changed.</summary>
	public static bool TryRecord( string variationId, string displayName, float timeSeconds, out int rank )
	{
		rank = -1;
		if ( timeSeconds <= 0f || float.IsNaN( timeSeconds ) || float.IsInfinity( timeSeconds ) )
			return false;

		var name = string.IsNullOrWhiteSpace( displayName ) ? "Player" : displayName.Trim();
		var list = new List<TimeTrialLeaderboardEntry>( Load( variationId ) );
		var entry = new TimeTrialLeaderboardEntry
		{
			DisplayName = name,
			TimeSeconds = timeSeconds,
			RecordedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
		};
		list.Add( entry );
		list.Sort( static ( a, b ) => a.TimeSeconds.CompareTo( b.TimeSeconds ) );

		var index = list.IndexOf( entry );
		if ( index < 0 || index >= MaxEntries )
			return false;

		rank = index + 1;
		if ( list.Count > MaxEntries )
			list.RemoveRange( MaxEntries, list.Count - MaxEntries );

		try
		{
			var json = JsonSerializer.Serialize(
				new LeaderboardFile { VariationId = variationId ?? "", Entries = list },
				JsonOptions );
			FileSystem.Data.WriteAllText( FileName( variationId ), json );
			return true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[TimeTrialLeaderboard] Save failed: {ex.Message}" );
			return false;
		}
	}

	public sealed class TimeTrialLeaderboardEntry
	{
		public string DisplayName { get; set; } = "Player";
		public float TimeSeconds { get; set; }
		public long RecordedAtUnix { get; set; }
	}

	sealed class LeaderboardFile
	{
		public string VariationId { get; set; } = "";
		public List<TimeTrialLeaderboardEntry> Entries { get; set; } = new();
	}
}
