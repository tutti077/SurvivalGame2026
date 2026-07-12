using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Client-side store for UI images pushed from the host during local/listen MP
/// (when <see cref="FileSystem.Mounted"/> does not contain the host project assets).
/// Files live under <c>FileSystem.Data/SyncedUi/…</c>.
/// </summary>
public static class SyncedUiContent
{
	public const string DataRoot = "SyncedUi";

	static readonly HashSet<string> WrittenPaths = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<string, Texture> TextureCache = new( StringComparer.OrdinalIgnoreCase );

	public static bool IsReady { get; private set; }
	public static int FileCount => WrittenPaths.Count;

	/// <summary>Raised on the owning client after a host sync pass finishes.</summary>
	public static event Action Ready;

	public static string ToDataPath( string projectRelativePath )
	{
		var normalized = Normalize( projectRelativePath );
		return string.IsNullOrWhiteSpace( normalized ) ? null : $"{DataRoot}/{normalized}";
	}

	public static bool HasFile( string projectRelativePath )
	{
		var dataPath = ToDataPath( projectRelativePath );
		if ( string.IsNullOrWhiteSpace( dataPath ) )
			return false;

		if ( WrittenPaths.Contains( Normalize( projectRelativePath ) ) )
			return true;

		try
		{
			return FileSystem.Data.FileExists( dataPath );
		}
		catch
		{
			return false;
		}
	}

	public static void WriteFile( string projectRelativePath, byte[] bytes )
	{
		if ( bytes is null || bytes.Length == 0 )
			return;

		var normalized = Normalize( projectRelativePath );
		var dataPath = ToDataPath( normalized );
		if ( string.IsNullOrWhiteSpace( dataPath ) )
			return;

		EnsureParentDirectory( dataPath );
		FileSystem.Data.WriteAllBytes( dataPath, bytes );
		WrittenPaths.Add( normalized );
		TextureCache.Remove( normalized );
	}

	public static void MarkReady()
	{
		IsReady = true;
		Ready?.Invoke();
	}

	public static void ResetSession()
	{
		IsReady = false;
		WrittenPaths.Clear();
		TextureCache.Clear();
	}

	public static Texture TryLoadTexture( string projectRelativePath )
	{
		var normalized = Normalize( projectRelativePath );
		if ( string.IsNullOrWhiteSpace( normalized ) )
			return null;

		if ( TextureCache.TryGetValue( normalized, out var cached ) && cached is not null && cached.IsValid() )
			return cached;

		var dataPath = ToDataPath( normalized );
		if ( string.IsNullOrWhiteSpace( dataPath ) )
			return null;

		try
		{
			if ( !FileSystem.Data.FileExists( dataPath ) )
				return null;

			var texture = Texture.LoadFromFileSystem( dataPath, FileSystem.Data, warnOnMissing: false );
			if ( texture is not null && texture.IsValid() )
			{
				TextureCache[normalized] = texture;
				return texture;
			}
		}
		catch
		{
			// ignored — caller falls through to Mounted
		}

		return null;
	}

	public static string Normalize( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return string.Empty;

		path = path.Replace( '\\', '/' ).Trim();
		while ( path.StartsWith( "/" ) )
			path = path[1..];

		if ( path.StartsWith( "assets/", StringComparison.OrdinalIgnoreCase ) )
			path = path[7..];

		return path;
	}

	static void EnsureParentDirectory( string relativeFilePath )
	{
		var slash = relativeFilePath.LastIndexOf( '/' );
		if ( slash <= 0 )
			return;

		var dir = relativeFilePath[..slash];
		if ( FileSystem.Data.DirectoryExists( dir ) )
			return;

		// Create nested segments (CreateDirectory may not be recursive on all builds).
		var parts = dir.Split( '/', StringSplitOptions.RemoveEmptyEntries );
		var built = string.Empty;
		foreach ( var part in parts )
		{
			built = string.IsNullOrEmpty( built ) ? part : $"{built}/{part}";
			if ( !FileSystem.Data.DirectoryExists( built ) )
				FileSystem.Data.CreateDirectory( built );
		}
	}
}
