using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

public sealed class StratagemCode
{
	public string Id { get; set; } = "";
	public string Name { get; set; } = "";
	/// <summary>Arrows as letters: U D L R.</summary>
	public string Code { get; set; } = "";
	public string Why { get; set; } = "";
}

/// <summary>The stratagem minigame's codes, from <c>data/stratagems.json</c>.</summary>
public static class StratagemCatalog
{
	const string FilePath = "data/stratagems.json";
	static readonly List<StratagemCode> Codes = new();
	static bool _loaded;

	public static IReadOnlyList<StratagemCode> All
	{
		get
		{
			EnsureLoaded();
			return Codes;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;
		_loaded = true;
		Codes.Clear();

		try
		{
			if ( FileSystem.Mounted.FileExists( FilePath ) )
			{
				var file = JsonSerializer.Deserialize<FileRoot>( FileSystem.Mounted.ReadAllText( FilePath ),
					new JsonSerializerOptions { PropertyNameCaseInsensitive = true } );
				foreach ( var c in file?.Codes ?? new List<StratagemCode>() )
				{
					c.Code = (c.Code ?? "").ToUpperInvariant();
					if ( c.Code.Length > 0 && c.Code.Trim( 'U', 'D', 'L', 'R' ).Length == 0 )
						Codes.Add( c );
					else
						Log.Warning( $"[Stratagem] '{c.Id}' has a bad code '{c.Code}' (only U D L R) — skipped." );
				}
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[Stratagem] Could not read {FilePath}: {ex.Message}" );
		}

		if ( Codes.Count == 0 )
			Codes.Add( new StratagemCode { Id = "purple_portal", Name = "Purple Portal", Code = "URDL" } );
	}

	public static void ForceReload()
	{
		_loaded = false;
		EnsureLoaded();
	}

	public static string Arrows( string code )
	{
		var chars = new char[code.Length * 2];
		for ( var i = 0; i < code.Length; i++ )
		{
			chars[i * 2] = code[i] switch { 'U' => '↑', 'D' => '↓', 'L' => '←', _ => '→' };
			chars[i * 2 + 1] = ' ';
		}

		return new string( chars ).TrimEnd();
	}

	sealed class FileRoot
	{
		public List<StratagemCode> Codes { get; set; }
	}
}
