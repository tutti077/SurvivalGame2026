using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads <c>data/bosses.json</c>. Adding or tuning a boss is a JSON edit.</summary>
public static class BossCatalog
{
	const string FilePath = "data/bosses.json";

	static readonly List<BossData> Bosses = new();
	static readonly Dictionary<string, BossData> ById = new( StringComparer.OrdinalIgnoreCase );
	static bool _loaded;

	public static IReadOnlyList<BossData> All
	{
		get
		{
			EnsureLoaded();
			return Bosses;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		Bosses.Clear();
		ById.Clear();

		if ( !TryLoadFromFile() )
			Log.Warning( "[BossCatalog] No bosses.json — bosses unavailable." );
	}

	public static bool TryGet( string bossId, out BossData boss )
	{
		EnsureLoaded();
		boss = null;
		return !string.IsNullOrWhiteSpace( bossId ) && ById.TryGetValue( bossId.Trim(), out boss );
	}

	static bool TryLoadFromFile()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( FilePath ) )
				return false;

			var json = FileSystem.Mounted.ReadAllText( FilePath );
			var file = JsonSerializer.Deserialize<BossesFile>( json, JsonOptions );
			if ( file?.Bosses is null || file.Bosses.Count == 0 )
				return false;

			for ( var i = 0; i < file.Bosses.Count; i++ )
			{
				var entry = file.Bosses[i];
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Id ) )
					continue;

				entry.Id = entry.Id.Trim();
				if ( string.IsNullOrWhiteSpace( entry.DisplayName ) )
					entry.DisplayName = entry.Id;
				Bosses.Add( entry );
				ById[entry.Id] = entry;
			}

			return Bosses.Count > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[BossCatalog] Failed to load {FilePath}: {ex.Message}" );
			return false;
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class BossesFile
	{
		public List<BossData> Bosses { get; set; } = new();
	}
}
