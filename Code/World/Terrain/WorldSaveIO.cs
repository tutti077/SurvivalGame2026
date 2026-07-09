using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game;

namespace Survival;

/// <summary>Persisted world recipe — seed + generation settings. Deltas added later.</summary>
public sealed class WorldSaveRecipe
{
	public string GameVersion { get; set; } = GameBuildLabel.Display;
	public string WorldName { get; set; } = "DefaultWorld";
	public int WorldSeed { get; set; } = 1337;
	public float WorldDiameterMeters { get; set; } = 20000f;
	public float OceanRingWidthMeters { get; set; } = 2500f;
	public float MaxTerrainHeightMeters { get; set; } = 700f;
	public float ChunkSizeMeters { get; set; } = 512f;
	public float BiomePreviewMetersPerPixel { get; set; } = 10f;
	public string FirstGeneratedUtc { get; set; } = "";
	public string LastLoadedUtc { get; set; } = "";
	public TerrainPreviewSettings PreviewSettings { get; set; } = new();

	[JsonIgnore]
	public static JsonSerializerOptions JsonOptions { get; } = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
}

public sealed class WorldSaveEntry
{
	public string WorldName { get; init; }
	public int WorldSeed { get; init; }
	public string FirstGeneratedUtc { get; init; } = "";
	public string LastLoadedUtc { get; init; } = "";
}

/// <summary>Reads/writes world folders under <c>WorldSaves/</c> via <see cref="FileSystem.Data"/>.</summary>
public static class WorldSaveIO
{
	const string SaveRootFolderName = "WorldSaves";
	const string RecipeFileName = "world.json";
	const string SeedFileName = "seed.txt";
	const string BiomeMapFileName = "biome_map.png";
	const string IndexFileName = "worlds.index";

	public static string GetRecipeRelativePath( string worldName )
		=> $"{GetWorldFolder( worldName )}/{RecipeFileName}";

	public static string GetSeedRelativePath( string worldName )
		=> $"{GetWorldFolder( worldName )}/{SeedFileName}";

	public static string GetBiomeMapRelativePath( string worldName )
		=> $"{GetWorldFolder( worldName )}/{BiomeMapFileName}";

	public static string GetWorldFolder( string worldName )
		=> $"{SaveRootFolderName}/{SanitizeWorldName( worldName )}";

	public static void EnsureWorldDirectory( string worldName )
		=> FileSystem.Data.CreateDirectory( GetWorldFolder( worldName ) );

	public static void WriteNewWorld( string worldName, int worldSeed )
	{
		var sanitized = SanitizeWorldName( worldName );
		var now = UtcNowString();
		var recipe = new WorldSaveRecipe
		{
			WorldName = sanitized,
			WorldSeed = worldSeed,
			FirstGeneratedUtc = now,
			LastLoadedUtc = now,
			PreviewSettings = new TerrainPreviewSettings
			{
				WorldSeed = worldSeed,
			},
		};

		WriteRecipe( recipe );
		WriteSeedText( sanitized, worldSeed );
		RegisterWorldInIndex( sanitized );
	}

	public static void WriteRecipe( WorldSaveRecipe recipe )
	{
		if ( recipe is null || string.IsNullOrWhiteSpace( recipe.WorldName ) )
			return;

		recipe.WorldName = SanitizeWorldName( recipe.WorldName );
		var path = GetRecipeRelativePath( recipe.WorldName );
		EnsureWorldDirectory( recipe.WorldName );
		var json = JsonSerializer.Serialize( recipe, WorldSaveRecipe.JsonOptions );
		FileSystem.Data.WriteAllText( path, json );
		WriteSeedText( recipe.WorldName, recipe.WorldSeed );
		RegisterWorldInIndex( recipe.WorldName );
	}

	public static void TouchLastLoaded( string worldName )
	{
		var sanitized = SanitizeWorldName( worldName );
		var recipe = TryReadRecipe( sanitized );
		if ( recipe is null )
		{
			var seed = TryReadSeed( sanitized );
			WriteNewWorld( sanitized, seed );
			return;
		}

		recipe.LastLoadedUtc = UtcNowString();
		if ( string.IsNullOrWhiteSpace( recipe.FirstGeneratedUtc ) )
			recipe.FirstGeneratedUtc = recipe.LastLoadedUtc;

		WriteRecipe( recipe );
	}

	public static void WriteBiomeMapPng( string worldName, Bitmap bitmap )
	{
		if ( string.IsNullOrWhiteSpace( worldName ) || bitmap is null )
			return;

		var path = GetBiomeMapRelativePath( worldName );
		EnsureWorldDirectory( worldName );
		FileSystem.Data.WriteAllBytes( path, bitmap.ToPng() );
	}

	public static WorldSaveRecipe TryReadRecipe( string worldName )
	{
		var path = GetRecipeRelativePath( worldName );
		if ( !FileSystem.Data.FileExists( path ) )
			return null;

		var json = FileSystem.Data.ReadAllText( path );
		if ( string.IsNullOrWhiteSpace( json ) )
			return null;

		return JsonSerializer.Deserialize<WorldSaveRecipe>( json, WorldSaveRecipe.JsonOptions );
	}

	public static WorldSaveEntry TryReadEntry( string worldName )
	{
		var sanitized = SanitizeWorldName( worldName );
		var recipe = TryReadRecipe( sanitized );
		if ( recipe is not null )
		{
			return new WorldSaveEntry
			{
				WorldName = sanitized,
				WorldSeed = recipe.WorldSeed,
				FirstGeneratedUtc = recipe.FirstGeneratedUtc,
				LastLoadedUtc = recipe.LastLoadedUtc,
			};
		}

		if ( !FileSystem.Data.FileExists( GetSeedRelativePath( sanitized ) ) )
			return null;

		return new WorldSaveEntry
		{
			WorldName = sanitized,
			WorldSeed = TryReadSeed( sanitized ),
		};
	}

	public static List<WorldSaveEntry> ListWorldSaves()
	{
		EnsureLegacySavesRegistered();
		var results = new List<WorldSaveEntry>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var name in ReadWorldIndex() )
		{
			if ( !seen.Add( name ) )
				continue;

			var entry = TryReadEntry( name );
			if ( entry is not null )
				results.Add( entry );
		}

		results.Sort( CompareByLastLoaded );
		return results;
	}

	static int CompareByLastLoaded( WorldSaveEntry a, WorldSaveEntry b )
	{
		var aTicks = ParseUtcTicks( a.LastLoadedUtc );
		var bTicks = ParseUtcTicks( b.LastLoadedUtc );
		return bTicks.CompareTo( aTicks );
	}

	static long ParseUtcTicks( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			return 0;

		return DateTime.TryParse( value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed )
			? parsed.Ticks
			: 0;
	}

	static void WriteSeedText( string worldName, int worldSeed )
	{
		EnsureWorldDirectory( worldName );
		FileSystem.Data.WriteAllText( GetSeedRelativePath( worldName ), worldSeed.ToString( CultureInfo.InvariantCulture ) );
	}

	static int TryReadSeed( string worldName )
	{
		var path = GetSeedRelativePath( worldName );
		if ( !FileSystem.Data.FileExists( path ) )
			return 1337;

		var text = FileSystem.Data.ReadAllText( path ).Trim();
		return int.TryParse( text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed )
			? seed
			: 1337;
	}

	static void EnsureLegacySavesRegistered()
	{
		if ( FileSystem.Data.FileExists( GetRecipeRelativePath( "TestWorld" ) ) )
			RegisterWorldInIndex( "TestWorld" );
	}

	static void RegisterWorldInIndex( string worldName )
	{
		var names = ReadWorldIndex();
		if ( names.Contains( worldName, StringComparer.OrdinalIgnoreCase ) )
			return;

		names.Add( worldName );
		WriteWorldIndex( names );
	}

	static List<string> ReadWorldIndex()
	{
		var path = $"{SaveRootFolderName}/{IndexFileName}";
		if ( !FileSystem.Data.FileExists( path ) )
			return new List<string>();

		var lines = FileSystem.Data.ReadAllText( path ).Split( '\n', StringSplitOptions.RemoveEmptyEntries );
		var names = new List<string>( lines.Length );
		foreach ( var line in lines )
		{
			var trimmed = line.Trim();
			if ( !string.IsNullOrWhiteSpace( trimmed ) )
				names.Add( trimmed );
		}

		return names;
	}

	static void WriteWorldIndex( List<string> names )
	{
		FileSystem.Data.CreateDirectory( SaveRootFolderName );
		var text = string.Join( "\n", names );
		FileSystem.Data.WriteAllText( $"{SaveRootFolderName}/{IndexFileName}", text );
	}

	static void RemoveFromIndex( string worldName )
	{
		var names = ReadWorldIndex();
		names.RemoveAll( n => string.Equals( n, worldName, StringComparison.OrdinalIgnoreCase ) );
		WriteWorldIndex( names );
	}

	public static bool WorldExists( string worldName )
	{
		var sanitized = SanitizeWorldName( worldName );
		if ( FileSystem.Data.DirectoryExists( GetWorldFolder( sanitized ) ) )
			return true;

		return FileSystem.Data.FileExists( GetRecipeRelativePath( sanitized ) )
			|| FileSystem.Data.FileExists( GetSeedRelativePath( sanitized ) );
	}

	public static bool TryDeleteWorld( string worldName )
	{
		var sanitized = SanitizeWorldName( worldName );
		if ( !WorldExists( sanitized ) )
			return false;

		var folder = GetWorldFolder( sanitized );
		if ( FileSystem.Data.DirectoryExists( folder ) )
			FileSystem.Data.DeleteDirectory( folder, true );
		else
		{
			DeleteFileIfExists( GetRecipeRelativePath( sanitized ) );
			DeleteFileIfExists( GetSeedRelativePath( sanitized ) );
			DeleteFileIfExists( GetBiomeMapRelativePath( sanitized ) );
		}

		RemoveFromIndex( sanitized );
		return true;
	}

	public static bool TryCopyWorld( string sourceName, string destName, out string sanitizedDest )
	{
		sanitizedDest = SanitizeWorldName( destName );
		var source = SanitizeWorldName( sourceName );
		if ( string.IsNullOrWhiteSpace( source ) || string.IsNullOrWhiteSpace( sanitizedDest ) )
			return false;

		if ( string.Equals( source, sanitizedDest, StringComparison.OrdinalIgnoreCase ) )
			return false;

		if ( !WorldExists( source ) || WorldExists( sanitizedDest ) )
			return false;

		CopyWorldFolder( source, sanitizedDest );

		var recipe = TryReadRecipe( sanitizedDest );
		if ( recipe is not null )
		{
			recipe.WorldName = sanitizedDest;
			WriteRecipe( recipe );
		}
		else
		{
			WriteSeedText( sanitizedDest, TryReadSeed( source ) );
			RegisterWorldInIndex( sanitizedDest );
		}

		return true;
	}

	public static bool TryRenameWorld( string sourceName, string destName, out string sanitizedDest )
	{
		sanitizedDest = SanitizeWorldName( destName );
		var source = SanitizeWorldName( sourceName );
		if ( string.IsNullOrWhiteSpace( source ) || string.IsNullOrWhiteSpace( sanitizedDest ) )
			return false;

		if ( string.Equals( source, sanitizedDest, StringComparison.OrdinalIgnoreCase ) )
			return false;

		if ( !WorldExists( source ) || WorldExists( sanitizedDest ) )
			return false;

		CopyWorldFolder( source, sanitizedDest );

		var recipe = TryReadRecipe( sanitizedDest );
		if ( recipe is not null )
		{
			recipe.WorldName = sanitizedDest;
			WriteRecipe( recipe );
		}
		else
		{
			WriteSeedText( sanitizedDest, TryReadSeed( source ) );
			RegisterWorldInIndex( sanitizedDest );
		}

		TryDeleteWorld( source );
		return true;
	}

	static void CopyWorldFolder( string sourceName, string destName )
	{
		EnsureWorldDirectory( destName );
		CopyFileIfExists( GetRecipeRelativePath( sourceName ), GetRecipeRelativePath( destName ) );
		CopyFileIfExists( GetSeedRelativePath( sourceName ), GetSeedRelativePath( destName ) );
		CopyFileIfExists( GetBiomeMapRelativePath( sourceName ), GetBiomeMapRelativePath( destName ) );
	}

	static void CopyFileIfExists( string sourcePath, string destPath )
	{
		if ( !FileSystem.Data.FileExists( sourcePath ) )
			return;

		var bytes = FileSystem.Data.ReadAllBytes( sourcePath );
		if ( bytes.Length == 0 )
			return;

		FileSystem.Data.WriteAllBytes( destPath, bytes.ToArray() );
	}

	static void DeleteFileIfExists( string path )
	{
		if ( FileSystem.Data.FileExists( path ) )
			FileSystem.Data.DeleteFile( path );
	}

	static string UtcNowString()
		=> DateTime.UtcNow.ToString( "o", CultureInfo.InvariantCulture );

	static string SanitizeWorldName( string worldName )
	{
		var trimmed = worldName.Trim();
		foreach ( var c in InvalidFileNameChars )
			trimmed = trimmed.Replace( c, '_' );

		return string.IsNullOrWhiteSpace( trimmed ) ? "DefaultWorld" : trimmed;
	}

	static readonly char[] InvalidFileNameChars =
	{
		'/', '\\', ':', '*', '?', '"', '<', '>', '|',
	};
}
