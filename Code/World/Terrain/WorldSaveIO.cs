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
	public float MaxTerrainHeightMeters { get; set; } = 6000f;
	public float ChunkSizeMeters { get; set; } = 512f;
	public float BiomePreviewMetersPerPixel { get; set; } = 10f;
	public TerrainPreviewSettings PreviewSettings { get; set; } = new();

	[JsonIgnore]
	public static JsonSerializerOptions JsonOptions { get; } = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
}

/// <summary>Reads/writes world folders under <c>WorldSaves/</c> via <see cref="FileSystem.Data"/>.</summary>
public static class WorldSaveIO
{
	const string SaveRootFolderName = "WorldSaves";
	const string RecipeFileName = "world.json";
	const string BiomeMapFileName = "biome_map.png";

	public static string GetRecipeRelativePath( string worldName )
		=> $"{GetWorldFolder( worldName )}/{RecipeFileName}";

	public static string GetBiomeMapRelativePath( string worldName )
		=> $"{GetWorldFolder( worldName )}/{BiomeMapFileName}";

	public static string GetWorldFolder( string worldName )
		=> $"{SaveRootFolderName}/{SanitizeWorldName( worldName )}";

	public static void EnsureWorldDirectory( string worldName )
		=> FileSystem.Data.CreateDirectory( GetWorldFolder( worldName ) );

	public static void WriteRecipe( WorldSaveRecipe recipe )
	{
		if ( recipe is null || string.IsNullOrWhiteSpace( recipe.WorldName ) )
			return;

		var path = GetRecipeRelativePath( recipe.WorldName );
		EnsureWorldDirectory( recipe.WorldName );
		var json = JsonSerializer.Serialize( recipe, WorldSaveRecipe.JsonOptions );
		FileSystem.Data.WriteAllText( path, json );
	}

	public static void WriteBiomeMapPng( string worldName, Bitmap bitmap )
	{
		if ( string.IsNullOrWhiteSpace( worldName ) || bitmap is null )
			return;

		// Display export only — generation never reads this file back.

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

	static string SanitizeWorldName( string worldName )
	{
		var trimmed = worldName.Trim();
		foreach ( var c in InvalidFileNameChars )
		{
			trimmed = trimmed.Replace( c, '_' );
		}

		return string.IsNullOrWhiteSpace( trimmed ) ? "DefaultWorld" : trimmed;
	}

	static readonly char[] InvalidFileNameChars =
	{
		'/', '\\', ':', '*', '?', '"', '<', '>', '|',
	};
}
