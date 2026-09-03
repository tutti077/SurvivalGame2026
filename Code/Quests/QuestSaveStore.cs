using System;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// Reads/writes one player's quest progress under <c>FileSystem.Data/2Tgames/players/&lt;steamid&gt;/quests.json</c>.
/// <para>
/// s&amp;box sandboxes file IO to <see cref="FileSystem.Data"/> (on Windows:
/// <c>sbox/data/local/survivalgamebasics#local/</c>), so the <c>2Tgames</c> root lives there rather
/// than in AppData. It is persistent, not temp storage. The file is a few hundred bytes and is
/// rewritten in full on every progress change — there is no shutdown hook to rely on, so
/// write-through is what makes a crash or alt-F4 lose nothing.
/// </para>
/// </summary>
public static class QuestSaveStore
{
	public const string RootFolder = "2Tgames";
	public const string PlayersFolder = RootFolder + "/players";
	public const string FileName = "quests.json";

	public static string GetPlayerFolder( string playerKey ) => $"{PlayersFolder}/{SanitizeKey( playerKey )}";
	public static string GetFilePath( string playerKey ) => $"{GetPlayerFolder( playerKey )}/{FileName}";

	public static QuestSaveFile Load( string playerKey )
	{
		var path = GetFilePath( playerKey );

		try
		{
			if ( !FileSystem.Data.FileExists( path ) )
				return null;

			var json = FileSystem.Data.ReadAllText( path );
			var file = JsonSerializer.Deserialize<QuestSaveFile>( json, JsonOptions );
			if ( file is null )
				return null;

			file.Quests ??= new( StringComparer.OrdinalIgnoreCase );
			return file;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[QuestSaveStore] Failed to load '{path}': {ex.Message}" );
			return null;
		}
	}

	public static bool Save( string playerKey, QuestSaveFile file )
	{
		if ( file is null )
			return false;

		var folder = GetPlayerFolder( playerKey );
		var path = GetFilePath( playerKey );

		try
		{
			if ( !FileSystem.Data.DirectoryExists( folder ) )
				FileSystem.Data.CreateDirectory( folder );

			file.Version = QuestSaveFile.CurrentVersion;
			file.PlayerKey = SanitizeKey( playerKey );
			file.UpdatedUtc = DateTime.UtcNow.ToString( "o" );

			var json = JsonSerializer.Serialize( file, JsonOptions );
			FileSystem.Data.WriteAllText( path, json );
			return true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[QuestSaveStore] Failed to save '{path}': {ex.Message}" );
			return false;
		}
	}

	public static void Delete( string playerKey )
	{
		var path = GetFilePath( playerKey );

		try
		{
			if ( FileSystem.Data.FileExists( path ) )
				FileSystem.Data.DeleteFile( path );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[QuestSaveStore] Failed to delete '{path}': {ex.Message}" );
		}
	}

	static string SanitizeKey( string key )
	{
		if ( string.IsNullOrWhiteSpace( key ) )
			return "local";

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
		WriteIndented = true
	};
}
