using System;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// One player's state in one save: <c>FileSystem.Data/2Tgames/players/&lt;steamid&gt;/worlds/&lt;game ID&gt;.json</c>
/// (the save's <see cref="WorldSaveRecipe.WorldId"/>), beside their quests and map pins - player-scoped, so every
/// player keeps their own. Holds where the pawn stood (Mark 2026-11-11: "add player position saver"); written by
/// <see cref="WorldPlayerSpawn"/> while playing and on leaving, read once when the world loads. No file = the
/// player has not been to this world yet, so they arrive by stasis pod.
/// </summary>
public sealed class PlayerWorldSave
{
	public const int CurrentVersion = 1;
	public const string WorldsFolder = "worlds";

	public int Version { get; set; } = CurrentVersion;
	public string PlayerKey { get; set; } = "";
	public string WorldId { get; set; } = "";
	/// <summary>The save's name when written - for a human reading the file; the game ID is the key.</summary>
	public string WorldName { get; set; } = "";
	public string SavedUtc { get; set; } = "";

	/// <summary>Pawn root (feet), engine units.</summary>
	public float X { get; set; }
	public float Y { get; set; }
	public float Z { get; set; }
	public float Yaw { get; set; }

	public Vector3 Position => new( X, Y, Z );

	static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public static string GetFilePath( string playerKey, string worldId )
		=> $"{QuestSaveStore.GetPlayerFolder( playerKey )}/{WorldsFolder}/{SanitizeKey( worldId )}.json";

	public static PlayerWorldSave Load( string playerKey, string worldId )
	{
		if ( string.IsNullOrWhiteSpace( worldId ) )
			return null;

		var path = GetFilePath( playerKey, worldId );
		try
		{
			if ( !FileSystem.Data.FileExists( path ) )
				return null;
			return JsonSerializer.Deserialize<PlayerWorldSave>( FileSystem.Data.ReadAllText( path ), JsonOptions );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[PlayerWorldSave] Failed to read '{path}': {ex.Message}" );
			return null;
		}
	}

	public static bool Save( string playerKey, string worldId, string worldName, Vector3 position, float yaw )
	{
		if ( string.IsNullOrWhiteSpace( worldId ) )
			return false;

		var path = GetFilePath( playerKey, worldId );
		try
		{
			FileSystem.Data.CreateDirectory( $"{QuestSaveStore.GetPlayerFolder( playerKey )}/{WorldsFolder}" );
			var file = new PlayerWorldSave
			{
				PlayerKey = playerKey ?? "",
				WorldId = worldId,
				WorldName = worldName ?? "",
				SavedUtc = DateTime.UtcNow.ToString( "o" ),
				X = position.x,
				Y = position.y,
				Z = position.z,
				Yaw = yaw,
			};
			FileSystem.Data.WriteAllText( path, JsonSerializer.Serialize( file, JsonOptions ) );
			return true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[PlayerWorldSave] Failed to write '{path}': {ex.Message}" );
			return false;
		}
	}

	/// <summary>Every player's file for one save (menu Delete, scratch-world wipe).</summary>
	public static void DeleteForAllPlayers( string worldId )
	{
		if ( string.IsNullOrWhiteSpace( worldId ) )
			return;

		try
		{
			if ( !FileSystem.Data.DirectoryExists( QuestSaveStore.PlayersFolder ) )
				return;
			foreach ( var player in FileSystem.Data.FindDirectory( QuestSaveStore.PlayersFolder ) )
			{
				var path = $"{QuestSaveStore.PlayersFolder}/{player}/{WorldsFolder}/{SanitizeKey( worldId )}.json";
				if ( FileSystem.Data.FileExists( path ) )
					FileSystem.Data.DeleteFile( path );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[PlayerWorldSave] Failed to delete saves for '{worldId}': {ex.Message}" );
		}
	}

	static string SanitizeKey( string key )
	{
		var chars = key.Trim().ToCharArray();
		for ( var i = 0; i < chars.Length; i++ )
		{
			if ( !char.IsLetterOrDigit( chars[i] ) && chars[i] != '_' && chars[i] != '-' )
				chars[i] = '_';
		}
		return new string( chars );
	}
}
