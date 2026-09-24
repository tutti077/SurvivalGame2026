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
