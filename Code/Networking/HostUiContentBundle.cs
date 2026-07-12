using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Host-only: collects UI image bytes from <see cref="FileSystem.Mounted"/> for client sync.</summary>
public static class HostUiContentBundle
{
	static readonly string[] ScanRoots = { "ui", "assets/ui" };
	static readonly string[] Patterns = { "*.png", "*.jpg", "*.jpeg", "*.webp" };

	static List<Entry> _cached;

	public readonly struct Entry
	{
		public Entry( string path, byte[] bytes )
		{
			Path = path;
			Bytes = bytes;
		}

		public string Path { get; }
		public byte[] Bytes { get; }
	}

	/// <summary>Build once per host session — UI icons are immutable for a play session.</summary>
	public static IReadOnlyList<Entry> GetOrBuild()
	{
		if ( _cached is not null )
			return _cached;

		var byPath = new Dictionary<string, byte[]>( StringComparer.OrdinalIgnoreCase );

		foreach ( var root in ScanRoots )
			ScanMountedFolder( root, byPath );

		AddIfReadable( byPath, "ui/items/rock.jpg" );

		_cached = new List<Entry>( byPath.Count );
		foreach ( var (path, bytes) in byPath )
			_cached.Add( new Entry( path, bytes ) );

		_cached.Sort( ( a, b ) => string.Compare( a.Path, b.Path, StringComparison.OrdinalIgnoreCase ) );
		return _cached;
	}

	static void ScanMountedFolder( string root, Dictionary<string, byte[]> byPath )
	{
		foreach ( var pattern in Patterns )
		{
			IEnumerable<string> names;
			try
			{
				names = FileSystem.Mounted.FindFile( root, pattern, recursive: true );
			}
			catch
			{
				continue;
			}

			if ( names is null )
				continue;

			foreach ( var name in names )
			{
				if ( string.IsNullOrWhiteSpace( name ) )
					continue;

				var relative = name.Replace( '\\', '/' );
				while ( relative.StartsWith( "/" ) )
					relative = relative[1..];

				// FindFile may return bare names or paths relative to <paramref name="root"/>.
				string mountedPath;
				if ( relative.StartsWith( "assets/", StringComparison.OrdinalIgnoreCase )
				     || relative.StartsWith( "ui/", StringComparison.OrdinalIgnoreCase ) )
					mountedPath = relative;
				else
					mountedPath = $"{root.TrimEnd( '/' )}/{relative}";

				TryAddMounted( byPath, mountedPath );
			}
		}
	}

	static void AddIfReadable( Dictionary<string, byte[]> byPath, string path ) => TryAddMounted( byPath, path );

	static void TryAddMounted( Dictionary<string, byte[]> byPath, string mountedPath )
	{
		var canonical = SyncedUiContent.Normalize( mountedPath );
		if ( string.IsNullOrWhiteSpace( canonical ) || byPath.ContainsKey( canonical ) )
			return;

		if ( !TryReadBytes( mountedPath, out var bytes ) && !TryReadBytes( canonical, out bytes ) )
			return;

		if ( bytes is null || bytes.Length == 0 )
			return;

		byPath[canonical] = bytes;
	}

	static bool TryReadBytes( string path, out byte[] bytes )
	{
		bytes = null;
		path = SyncedUiContent.Normalize( path );
		if ( string.IsNullOrWhiteSpace( path ) )
			return false;

		try
		{
			if ( !FileSystem.Mounted.FileExists( path ) )
			{
				var withAssets = path.StartsWith( "assets/", StringComparison.OrdinalIgnoreCase )
					? path
					: $"assets/{path}";
				if ( !FileSystem.Mounted.FileExists( withAssets ) )
					return false;
				path = withAssets;
			}

			bytes = FileSystem.Mounted.ReadAllBytes( path ).ToArray();
			return bytes is { Length: > 0 };
		}
		catch
		{
			bytes = null;
			return false;
		}
	}
}
