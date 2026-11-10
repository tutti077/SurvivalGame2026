using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads biome entity density tables from <c>data/biome_population.json</c>.</summary>
public static class BiomePopulationCatalog
{
	const string FilePath = "data/biome_population.json";

	static readonly Dictionary<TerrainPreviewBiomeId, List<BiomePopulationEntry>> ByBiome = new();
	static bool _loaded;

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;
		ReloadFromDisk();
	}

	/// <summary>Read the table again (once per world load — <see cref="TerrainWorldManager"/>).</summary>
	public static void Reload() => ReloadFromDisk();

	public static IReadOnlyList<BiomePopulationEntry> GetEntries( TerrainPreviewBiomeId biome )
	{
		EnsureLoaded();
		return ByBiome.TryGetValue( biome, out var list ) ? list : Array.Empty<BiomePopulationEntry>();
	}

	static void ReloadFromDisk()
	{
		_loaded = true;
		ByBiome.Clear();

		if ( TryLoadFromFile() )
			return;

		SeedFallbacks();
		Log.Warning( "[BiomePopulationCatalog] Using built-in fallback population table." );
	}

	static bool TryLoadFromFile()
	{
		foreach ( var path in GetPathCandidates() )
		{
			try
			{
				if ( !FileSystem.Mounted.FileExists( path ) )
					continue;

				var json = FileSystem.Mounted.ReadAllText( path );
				if ( string.IsNullOrWhiteSpace( json ) )
					continue;

				var file = JsonSerializer.Deserialize<BiomePopulationFile>( json );
				if ( file?.Biomes is null || file.Biomes.Count == 0 )
					continue;

				foreach ( var (biomeName, biomeData) in file.Biomes )
				{
					if ( !TryParseBiome( biomeName, out var biomeId ) || biomeData?.Entries is null )
						continue;

					var list = new List<BiomePopulationEntry>();
					foreach ( var row in biomeData.Entries )
					{
						if ( row is null || (string.IsNullOrWhiteSpace( row.Prefab ) && string.IsNullOrWhiteSpace( row.Camp )) )
							continue;

						if ( !TryParseEnemyType( row.EnemyType, out var enemyType ) )
							enemyType = EnemyType.Scav;

						AnimalSpecies? species = null;
						if ( !string.IsNullOrWhiteSpace( row.Species ) && Enum.TryParse<AnimalSpecies>( row.Species.Trim(), ignoreCase: true, out var parsedSpecies ) )
							species = parsedSpecies;

						list.Add( new BiomePopulationEntry
						{
							EntityId = string.IsNullOrWhiteSpace( row.EntityId )
								? (species is { } sp ? sp.ToString().ToLowerInvariant() : EntityPerceptionCatalog.BuildEntityId( enemyType, row.Tier ))
								: row.EntityId.Trim(),
							PrefabPath = NormalizePrefabPath( row.Prefab ),
							EnemyType = enemyType,
							Species = species,
							Tier = Math.Max( 1, row.Tier ),
							SpacingMeters = Math.Clamp( row.SpacingMeters, 16f, 4000f ),
							SpawnWeight = Math.Clamp( row.SpawnWeight, 0f, 1f ),
							Respawn = row.Respawn,
							RespawnDelaySeconds = Math.Max( 0f, row.RespawnDelaySeconds ),
							Near = string.IsNullOrWhiteSpace( row.Near ) ? null : row.Near.Trim(),
							CampId = string.IsNullOrWhiteSpace( row.Camp ) ? null : row.Camp.Trim(),
							GroupChance = Math.Clamp( row.GroupChance, 0f, 1f ),
							GroupMin = Math.Clamp( row.GroupMin, 1, 8 ),
							GroupMax = Math.Clamp( Math.Max( row.GroupMin, row.GroupMax ), 1, 8 ),
							GroupRadiusMeters = Math.Clamp( row.GroupRadiusMeters, 1f, 40f ),
						} );
					}

					if ( list.Count > 0 )
						ByBiome[biomeId] = list;
				}

				Log.Info( $"[BiomePopulationCatalog] Loaded {ByBiome.Count} biomes from '{path}'." );
				return ByBiome.Count > 0;
			}
			catch ( Exception e )
			{
				Log.Warning( $"[BiomePopulationCatalog] Failed reading '{path}': {e.Message}" );
			}
		}

		return false;
	}

	static IEnumerable<string> GetPathCandidates()
	{
		yield return FilePath;
		yield return "Assets/" + FilePath;
	}

	static void SeedFallbacks()
	{
		ByBiome[TerrainPreviewBiomeId.CloverHills] =
		[
			new BiomePopulationEntry
			{
				EntityId = "scavT1",
				PrefabPath = "prefabs/entity/scavT1.prefab",
				EnemyType = EnemyType.Scav,
				Tier = 1,
				SpacingMeters = 250f,
				SpawnWeight = 1f,
				Respawn = true,
				RespawnDelaySeconds = 90f,
			}
		];
	}

	static bool TryParseBiome( string name, out TerrainPreviewBiomeId id )
	{
		id = TerrainPreviewBiomeId.None;
		if ( string.IsNullOrWhiteSpace( name ) )
			return false;

		return Enum.TryParse( name.Trim(), ignoreCase: true, out id )
		       && id != TerrainPreviewBiomeId.None
		       && id != TerrainPreviewBiomeId.Water;
	}

	static bool TryParseEnemyType( string name, out EnemyType type )
	{
		type = EnemyType.Scav;
		if ( string.IsNullOrWhiteSpace( name ) )
			return false;
		return Enum.TryParse( name.Trim(), ignoreCase: true, out type );
	}

	static string NormalizePrefabPath( string path )
	{
		path = (path ?? string.Empty).Trim().Replace( '\\', '/' );
		if ( path.StartsWith( "assets/", StringComparison.OrdinalIgnoreCase ) )
			path = path[7..];
		return path;
	}
}
