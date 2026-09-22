using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads <c>data/enemy_camps.json</c>. Adding or changing a camp layout is a JSON edit.</summary>
public static class EnemyCampCatalog
{
	const string FilePath = "data/enemy_camps.json";

	static readonly List<EnemyCampData> Camps = new();
	static readonly Dictionary<string, EnemyCampData> ById = new( StringComparer.OrdinalIgnoreCase );
	static bool _loaded;

	public static IReadOnlyList<EnemyCampData> All
	{
		get
		{
			EnsureLoaded();
			return Camps;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		Camps.Clear();
		ById.Clear();

		if ( !TryLoadFromFile() )
			Log.Warning( "[EnemyCampCatalog] No enemy_camps.json — enemy camps unavailable." );
	}

	public static bool TryGet( string campId, out EnemyCampData camp )
	{
		EnsureLoaded();
		camp = null;
		return !string.IsNullOrWhiteSpace( campId ) && ById.TryGetValue( campId.Trim(), out camp );
	}

	static bool TryLoadFromFile()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( FilePath ) )
				return false;

			var json = FileSystem.Mounted.ReadAllText( FilePath );
			var file = JsonSerializer.Deserialize<EnemyCampsFile>( json, JsonOptions );
			if ( file?.Camps is null || file.Camps.Count == 0 )
				return false;

			for ( var i = 0; i < file.Camps.Count; i++ )
			{
				var entry = file.Camps[i];
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Id ) )
					continue;

				entry.Id = entry.Id.Trim();
				entry.Pieces ??= new List<EnemyCampPieceData>();
				entry.Entities ??= new List<EnemyCampEntityData>();
				Camps.Add( entry );
				ById[entry.Id] = entry;
			}

			return Camps.Count > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[EnemyCampCatalog] Failed to load {FilePath}: {ex.Message}" );
			return false;
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class EnemyCampsFile
	{
		public List<EnemyCampData> Camps { get; set; } = new();
	}
}
