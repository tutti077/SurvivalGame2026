using System;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// Reads/writes one player's map markup for one world under
/// <c>FileSystem.Data/2Tgames/players/&lt;steamid&gt;/map/&lt;world&gt;.json</c> — the same
/// persistent per-player root as <see cref="QuestSaveStore"/>, rewritten in full on every change
/// (write-through, no shutdown hook to trust).
/// </summary>
public static class MapMarkupSaveStore
{
	public const string MapFolder = "map";

	public static string GetFolder( string playerKey ) => $"{QuestSaveStore.GetPlayerFolder( playerKey )}/{MapFolder}";
	public static string GetFilePath( string playerKey, string worldKey ) => $"{GetFolder( playerKey )}/{SanitizeKey( worldKey )}.json";

	public static MapMarkupSaveFile Load( string playerKey, string worldKey )
	{
		var path = GetFilePath( playerKey, worldKey );

		try
		{
			if ( !FileSystem.Data.FileExists( path ) )
				return null;

			var json = FileSystem.Data.ReadAllText( path );
			var file = JsonSerializer.Deserialize<MapMarkupSaveFile>( json, JsonOptions );
			if ( file is null )
				return null;

			file.Pins ??= new();
			file.Strokes ??= new();
			file.Pins.RemoveAll( p => p is null );
			file.Strokes.RemoveAll( s => s is null || s.Points is null || s.Points.Count < 2 );
			foreach ( var pin in file.Pins )
			{
				if ( pin.Id == Guid.Empty )
					pin.Id = Guid.NewGuid();
				if ( !MapPinCatalog.IsKnown( pin.Icon ) )
					pin.Icon = MapPinCatalog.DefaultIconId;
				pin.Name ??= "";
			}

			return file;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[MapMarkupSaveStore] Failed to load '{path}': {ex.Message}" );
			return null;
		}
	}

	public static bool Save( string playerKey, string worldKey, MapMarkupSaveFile file )
	{
		if ( file is null )
			return false;

		var folder = GetFolder( playerKey );
		var path = GetFilePath( playerKey, worldKey );

		try
		{
			if ( !FileSystem.Data.DirectoryExists( folder ) )
				FileSystem.Data.CreateDirectory( folder );

			file.Version = MapMarkupSaveFile.CurrentVersion;
			file.PlayerKey = playerKey ?? "";
			file.WorldKey = SanitizeKey( worldKey );
			file.UpdatedUtc = DateTime.UtcNow.ToString( "o" );

			var json = JsonSerializer.Serialize( file, JsonOptions );
			FileSystem.Data.WriteAllText( path, json );
			return true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[MapMarkupSaveStore] Failed to save '{path}': {ex.Message}" );
			return false;
		}
	}

	/// <summary>How pins were keyed before saves had a game ID (2026-11-10): world name + seed.</summary>
	public static string LegacyWorldKey( string worldName, int worldSeed ) => $"{worldName}_{worldSeed}";

	/// <summary>
	/// This player's markup for a world, keyed by the save's game ID. A file still under the old name + seed key is
	/// moved over on first load (written under the ID, the old file deleted).
	/// </summary>
	public static MapMarkupSaveFile LoadOrMigrate( string playerKey, string worldKey, string legacyKey )
	{
		var file = Load( playerKey, worldKey );
		if ( file is not null || string.IsNullOrWhiteSpace( legacyKey ) || legacyKey == worldKey )
			return file;

		file = Load( playerKey, legacyKey );
		if ( file is null )
			return null;

		if ( Save( playerKey, worldKey, file ) )
		{
			FileSystem.Data.DeleteFile( GetFilePath( playerKey, legacyKey ) );
			Log.Info( $"[MapMarkupSaveStore] Pins moved from '{legacyKey}' to game ID '{worldKey}' for player '{playerKey}'." );
		}

		return file;
	}

	/// <summary>Delete every player's markup for one world — under its game ID and the old name + seed key — and forget the loaded copy.</summary>
	public static void DeleteForAllPlayers( string worldId, string legacyKey )
	{
		try
		{
			if ( FileSystem.Data.DirectoryExists( QuestSaveStore.PlayersFolder ) )
			{
				foreach ( var player in FileSystem.Data.FindDirectory( QuestSaveStore.PlayersFolder ) )
				{
					foreach ( var key in new[] { worldId, legacyKey } )
					{
						if ( string.IsNullOrWhiteSpace( key ) )
							continue;
						var path = $"{QuestSaveStore.PlayersFolder}/{player}/{MapFolder}/{SanitizeKey( key )}.json";
						if ( FileSystem.Data.FileExists( path ) )
							FileSystem.Data.DeleteFile( path );
					}
				}
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[MapMarkupSaveStore] Failed to delete markup for '{worldId}' / '{legacyKey}': {ex.Message}" );
		}

		LocalMapMarkup.Forget();
	}

	static string SanitizeKey( string key )
	{
		if ( string.IsNullOrWhiteSpace( key ) )
			return "world";

		var chars = key.Trim().ToCharArray();
		for ( var i = 0; i < chars.Length; i++ )
		{
			var c = chars[i];
			if ( !char.IsLetterOrDigit( c ) && c != '_' && c != '-' )
				chars[i] = '_';
		}

		return new string( chars );
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		WriteIndented = false
	};
}
