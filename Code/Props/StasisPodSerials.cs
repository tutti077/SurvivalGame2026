using System;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// Pod serial numbers, one world-wide counter the host owns. Mark 2026-11-09: "some huge number and all players
/// that spawn after that point get the next number in line ... somewhere in the 100k ranges, 885922 etc so then
/// eventually we can make the players find pod #1 and it'll mean something." The first pod a world hands out is
/// <see cref="FirstSerial"/>; the next free one persists in <c>WorldSaves/&lt;world&gt;/pod_serials.json</c>
/// (<see cref="FileSystem.Data"/>, beside world.json) so serials keep counting across sessions.
/// </summary>
public static class StasisPodSerials
{
	public const long FirstSerial = 885922;
	const string FileName = "pod_serials.json";

	sealed class SerialFile
	{
		public long NextSerial { get; set; } = FirstSerial;
	}

	/// <summary>Host: take the next serial for the world this scene plays, and advance the saved counter.</summary>
	public static long HostNext( Scene scene )
	{
		var worldName = ResolveWorldName( scene );
		var path = $"{WorldSaveIO.GetWorldFolder( worldName )}/{FileName}";
		var file = Read( path );
		var serial = Math.Max( FirstSerial, file.NextSerial );
		file.NextSerial = serial + 1;
		try
		{
			WorldSaveIO.EnsureWorldDirectory( worldName );
			FileSystem.Data.WriteAllText( path, JsonSerializer.Serialize( file, WorldSaveRecipe.JsonOptions ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[StasisPodSerials] could not save {path}: {e.Message} - serials restart at {FirstSerial} next session." );
		}

		return serial;
	}

	/// <summary>The next serial a pod would get, without taking it.</summary>
	public static long Peek( Scene scene )
	{
		var worldName = ResolveWorldName( scene );
		var file = Read( $"{WorldSaveIO.GetWorldFolder( worldName )}/{FileName}" );
		return Math.Max( FirstSerial, file.NextSerial );
	}

	static SerialFile Read( string path )
	{
		try
		{
			if ( FileSystem.Data.FileExists( path ) )
				return JsonSerializer.Deserialize<SerialFile>( FileSystem.Data.ReadAllText( path ), WorldSaveRecipe.JsonOptions ) ?? new SerialFile();
		}
		catch ( Exception e )
		{
			Log.Warning( $"[StasisPodSerials] could not read {path}: {e.Message}" );
		}

		return new SerialFile();
	}

	/// <summary>The world folder the counter lives in: the terrain world's name, else the network session's, else DefaultWorld.</summary>
	public static string ResolveWorldName( Scene scene )
	{
		if ( scene is { IsValid: true } )
		{
			foreach ( var manager in scene.GetAllComponents<TerrainWorldManager>() )
			{
				if ( manager is { IsValid: true } && !string.IsNullOrWhiteSpace( manager.WorldName ) )
					return manager.WorldName.Trim();
			}
		}

		var session = WorldNetworkSession.Instance;
		if ( session is { IsValid: true } && !string.IsNullOrWhiteSpace( session.WorldName ) )
			return session.WorldName.Trim();

		return "DefaultWorld";
	}
}
