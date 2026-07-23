using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads per-entity sight / alert-meter / chase / retreat tuning from <c>data/entity_perception.json</c>.</summary>
public static class EntityPerceptionCatalog
{
	const string FilePath = "data/entity_perception.json";

	static readonly Dictionary<string, EntityPerceptionData> Rows =
		new( StringComparer.OrdinalIgnoreCase );

	static bool _loaded;

	public static void ForceReload()
	{
		_loaded = false;
		ReloadFromDisk();
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;
		ReloadFromDisk();
	}

	public static string BuildEntityId( EnemyType type, int tier ) =>
		$"{type.ToString().ToLowerInvariant()}T{Math.Max( 1, tier )}";

	public static EntityPerceptionProfile Resolve( string entityId )
	{
		EnsureLoaded();
		if ( string.IsNullOrWhiteSpace( entityId ) )
			return EntityPerceptionProfile.CreateFallback();

		if ( Rows.TryGetValue( entityId.Trim(), out var row ) && row is not null )
			return EntityPerceptionProfile.FromData( row );

		return EntityPerceptionProfile.CreateFallback();
	}

	public static EntityPerceptionProfile Resolve( EnemyType type, int tier ) =>
		Resolve( BuildEntityId( type, tier ) );

	static void ReloadFromDisk()
	{
		_loaded = true;
		Rows.Clear();

		if ( TryLoadFromFile() )
			return;

		SeedFallbacks();
		Log.Warning( "[EntityPerceptionCatalog] Using built-in fallback perception rows." );
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

				var file = JsonSerializer.Deserialize<EntityPerceptionFile>( json );
				if ( file?.Entities is null || file.Entities.Count == 0 )
					continue;

				foreach ( var (key, value) in file.Entities )
				{
					if ( string.IsNullOrWhiteSpace( key ) || value is null )
						continue;
					Rows[key.Trim()] = value;
				}

				Log.Info( $"[EntityPerceptionCatalog] Loaded {Rows.Count} entities from '{path}'." );
				return Rows.Count > 0;
			}
			catch ( Exception e )
			{
				Log.Warning( $"[EntityPerceptionCatalog] Failed reading '{path}': {e.Message}" );
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
		Rows["scavT1"] = new EntityPerceptionData();
	}
}
