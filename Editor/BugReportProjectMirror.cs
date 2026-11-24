using System.Text;

namespace Editor;

/// <summary>
/// Editor side of <see cref="BugReports"/>: when the host is this editor, every saved report is also written to
/// <c>bugReports/&lt;id&gt;.json</c> at the project root, with <c>&lt;id&gt;.host_log.txt</c> beside it holding the
/// last <see cref="LogTailLines"/> lines of the editor's own engine log (game code cannot read the console; the
/// editor can read the log file). Hooked in through <see cref="BugReports.ProjectMirror"/>; the frame check is one
/// null test and re-hooks after a hotload.
/// </summary>
public static class BugReportProjectMirror
{
	public const string FolderName = "bugReports";
	const int LogTailLines = 400;
	const int LogTailBytes = 256 * 1024;

	[EditorEvent.Frame]
	static void Hook()
	{
		if ( BugReports.ProjectMirror is null )
			BugReports.ProjectMirror = Write;
	}

	static void Write( string id, string json )
	{
		var folder = Path.Combine( FindProjectRoot(), FolderName );
		Directory.CreateDirectory( folder );
		File.WriteAllText( Path.Combine( folder, $"{id}.json" ), json );

		var tail = ReadLogTail();
		if ( !string.IsNullOrEmpty( tail ) )
			File.WriteAllText( Path.Combine( folder, $"{id}.host_log.txt" ), tail );
	}

	/// <summary>The end of <c>logs/sbox-dev.log</c> (opened shared: the engine keeps it open for writing).</summary>
	static string ReadLogTail()
	{
		var path = FindEditorLog();
		if ( path is null )
			return null;

		try
		{
			using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete );
			var start = Math.Max( 0L, stream.Length - LogTailBytes );
			stream.Seek( start, SeekOrigin.Begin );
			using var reader = new StreamReader( stream, Encoding.UTF8 );
			var text = reader.ReadToEnd();

			var lines = text.Split( '\n' );
			var skip = Math.Max( start > 0 ? 1 : 0, lines.Length - LogTailLines ); // first line of a mid-file read is partial
			return $"# last {lines.Length - skip} lines of {path}\n" + string.Join( "\n", lines.Skip( skip ) );
		}
		catch ( IOException e )
		{
			return $"# could not read {path}: {e.Message}";
		}
	}

	static string FindEditorLog()
	{
		foreach ( var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory } )
		{
			var dir = string.IsNullOrEmpty( start ) ? null : new DirectoryInfo( start );
			while ( dir is not null )
			{
				var candidate = Path.Combine( dir.FullName, "logs", "sbox-dev.log" );
				if ( File.Exists( candidate ) )
					return candidate;

				dir = dir.Parent;
			}
		}

		return null;
	}

	static string FindProjectRoot()
	{
		if ( Project.Current?.RootDirectory is { Exists: true } root )
			return root.FullName;

		return Directory.GetCurrentDirectory();
	}
}
