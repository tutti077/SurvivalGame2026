using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// Loads <c>data/raids.json</c>. Adding or tuning a raid is a JSON edit. Statics survive hotloads, so
/// <see cref="BaseRaidSession"/> calls <see cref="Reload"/> at every raid start — a tuning edit is live
/// on the next L press without restarting the editor (a raid start is rare; the re-read costs nothing).
/// </summary>
public static class BaseRaidCatalog
{
	const string FilePath = "data/raids.json";

	static readonly Dictionary<string, BaseRaidData> ById = new( StringComparer.OrdinalIgnoreCase );
	static bool _loaded;

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		ById.Clear();

		if ( !TryLoadFromFile() )
			Log.Warning( "[BaseRaidCatalog] No raids.json — base raids unavailable." );
	}

	/// <summary>Drop the cached rows and re-read the file.</summary>
	public static void Reload()
	{
		_loaded = false;
		EnsureLoaded();
	}

	public static bool TryGet( string raidId, out BaseRaidData raid )
	{
		EnsureLoaded();
		raid = null;
		return !string.IsNullOrWhiteSpace( raidId ) && ById.TryGetValue( raidId.Trim(), out raid );
	}

	static bool TryLoadFromFile()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( FilePath ) )
				return false;

			var json = FileSystem.Mounted.ReadAllText( FilePath );
			var file = JsonSerializer.Deserialize<RaidsFile>( json, JsonOptions );
			if ( file?.Raids is null || file.Raids.Count == 0 )
				return false;

			foreach ( var entry in file.Raids )
			{
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Id ) )
					continue;

				entry.Id = entry.Id.Trim();
				if ( string.IsNullOrWhiteSpace( entry.DisplayName ) )
					entry.DisplayName = entry.Id;
				entry.Enemies ??= new List<BaseRaidEnemyData>();
				ById[entry.Id] = entry;
			}

			return ById.Count > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[BaseRaidCatalog] Failed to load {FilePath}: {ex.Message}" );
			return false;
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class RaidsFile
	{
		public List<BaseRaidData> Raids { get; set; } = new();
	}
}
