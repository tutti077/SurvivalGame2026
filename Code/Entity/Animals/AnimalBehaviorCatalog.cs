using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads per-species animal behavior tuning from <c>data/animal_behaviors.json</c>.</summary>
public static class AnimalBehaviorCatalog
{
	const string FilePath = "data/animal_behaviors.json";

	static readonly Dictionary<string, AnimalBehaviorData> Rows =
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

	public static AnimalBehaviorProfile Resolve( AnimalSpecies species )
	{
		EnsureLoaded();
		if ( Rows.TryGetValue( species.ToString(), out var row ) && row is not null )
			return AnimalBehaviorProfile.FromData( row );

		Log.Warning( $"[AnimalBehaviorCatalog] No row for '{species}' — using fallback (skittish prey)." );
		return AnimalBehaviorProfile.CreateFallback();
	}

	static void ReloadFromDisk()
	{
		_loaded = true;
		Rows.Clear();

		if ( TryLoadFromFile() )
			return;

		Log.Warning( "[AnimalBehaviorCatalog] data/animal_behaviors.json missing — all species use the fallback profile." );
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

				var file = JsonSerializer.Deserialize<AnimalBehaviorFile>( json );
				if ( file?.Animals is null || file.Animals.Count == 0 )
					continue;

				foreach ( var (key, value) in file.Animals )
				{
					if ( string.IsNullOrWhiteSpace( key ) || value is null )
						continue;
					Rows[key.Trim()] = value;
				}

				Log.Info( $"[AnimalBehaviorCatalog] Loaded {Rows.Count} species from '{path}'." );
				return Rows.Count > 0;
			}
			catch ( Exception e )
			{
				Log.Warning( $"[AnimalBehaviorCatalog] Failed reading '{path}': {e.Message}" );
			}
		}

		return false;
	}

	static IEnumerable<string> GetPathCandidates()
	{
		yield return FilePath;
		yield return "Assets/" + FilePath;
	}
}
