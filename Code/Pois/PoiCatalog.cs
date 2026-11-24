using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads <c>data/pois.json</c>: structure rows, their layouts, landmark rows and loot tables. Adding or changing a point of interest is a JSON edit.</summary>
public static class PoiCatalog
{
	const string FilePath = "data/pois.json";

	static readonly List<PoiStructureData> StructureList = new();
	static readonly List<PoiLandmarkData> LandmarkList = new();
	static readonly Dictionary<string, PoiLayoutData> LayoutById = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<string, PoiLootTable> LootById = new( StringComparer.OrdinalIgnoreCase );
	static bool _loaded;

	public static IReadOnlyList<PoiStructureData> Structures
	{
		get
		{
			EnsureLoaded();
			return StructureList;
		}
	}

	public static IReadOnlyList<PoiLandmarkData> Landmarks
	{
		get
		{
			EnsureLoaded();
			return LandmarkList;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		StructureList.Clear();
		LandmarkList.Clear();
		LayoutById.Clear();
		LootById.Clear();

		if ( !TryLoadFromFile() )
			Log.Warning( "[PoiCatalog] No pois.json - points of interest unavailable." );
	}

	/// <summary>Read the file again (once per world load - <see cref="TerrainWorldManager"/>).</summary>
	public static void Reload()
	{
		_loaded = false;
		EnsureLoaded();
	}

	/// <summary>Is this scatter kind label one of the landmark rows (their id is the kind)?</summary>
	public static bool IsLandmark( string id )
	{
		EnsureLoaded();
		for ( var i = 0; i < LandmarkList.Count; i++ )
		{
			if ( string.Equals( LandmarkList[i].Id, id, StringComparison.OrdinalIgnoreCase ) )
				return true;
		}
		return false;
	}

	public static bool TryGetLayout( string layoutId, out PoiLayoutData layout )
	{
		EnsureLoaded();
		layout = null;
		return !string.IsNullOrWhiteSpace( layoutId ) && LayoutById.TryGetValue( layoutId.Trim(), out layout );
	}

	public static bool TryGetLootTable( string tableId, out PoiLootTable table )
	{
		EnsureLoaded();
		table = null;
		return !string.IsNullOrWhiteSpace( tableId ) && LootById.TryGetValue( tableId.Trim(), out table );
	}

	static bool TryLoadFromFile()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( FilePath ) )
				return false;

			var file = JsonSerializer.Deserialize<PoiFile>( FileSystem.Mounted.ReadAllText( FilePath ), JsonOptions );
			if ( file is null )
				return false;

			foreach ( var layout in file.Layouts ?? [] )
			{
				if ( layout is null || string.IsNullOrWhiteSpace( layout.Id ) )
					continue;
				layout.Id = layout.Id.Trim();
				layout.Pieces ??= new List<PoiPieceData>();
				LayoutById[layout.Id] = layout;
			}

			foreach ( var table in file.LootTables ?? [] )
			{
				if ( table is not null && !string.IsNullOrWhiteSpace( table.Id ) )
					LootById[table.Id.Trim()] = table;
			}

			foreach ( var structure in file.Structures ?? [] )
			{
				if ( structure is null || string.IsNullOrWhiteSpace( structure.Id ) )
					continue;
				structure.Id = structure.Id.Trim();
				structure.Layouts ??= new List<string>();
				StructureList.Add( structure );
			}

			foreach ( var landmark in file.Landmarks ?? [] )
			{
				if ( landmark is null || string.IsNullOrWhiteSpace( landmark.Id ) || string.IsNullOrWhiteSpace( landmark.Prefab ) )
					continue;
				landmark.Id = landmark.Id.Trim();
				LandmarkList.Add( landmark );
			}

			return StructureList.Count > 0 || LandmarkList.Count > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[PoiCatalog] Failed to load {FilePath}: {ex.Message}" );
			return false;
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class PoiFile
	{
		public List<PoiStructureData> Structures { get; set; } = new();
		public List<PoiLayoutData> Layouts { get; set; } = new();
		public List<PoiLandmarkData> Landmarks { get; set; } = new();
		public List<PoiLootTable> LootTables { get; set; } = new();
	}
}
