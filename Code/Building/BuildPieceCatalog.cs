using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads build piece metadata from JSON.</summary>
public static class BuildPieceCatalog
{
	const string BuildPiecesFilePath = "data/build_pieces.json";

	static readonly List<BuildPieceData> Pieces = new();
	static readonly Dictionary<string, BuildPieceData> ById =
		new( StringComparer.OrdinalIgnoreCase );

	static bool _loaded;
	static int _loadedJsonHash;

	public static IReadOnlyList<BuildPieceData> All
	{
		get
		{
			EnsureLoaded();
			return Pieces;
		}
	}

	public static void ForceReload()
	{
		_loaded = false;
		_loadedJsonHash = 0;
		EnsureLoaded();
		BuildSnapPlacement.InvalidatePieceCache();
	}

	public static void EnsureLoaded()
	{
		var jsonHash = TryReadJsonHash();
		if ( _loaded && jsonHash == _loadedJsonHash )
			return;

		_loaded = true;
		_loadedJsonHash = jsonHash;
		Pieces.Clear();
		ById.Clear();

		if ( TryLoadFromFile() )
			return;

		Pieces.AddRange( CreateFallbackPieces() );
		RebuildLookup();
		Log.Warning( "[BuildPieceCatalog] Using built-in fallback build pieces." );
	}

	public static bool IsRepairTool( string pieceId ) =>
		TryGet( pieceId, out var data ) && data.IsRepairTool;

	public static bool TryGet( string pieceId, out BuildPieceData data )
	{
		EnsureLoaded();
		data = null;
		if ( string.IsNullOrWhiteSpace( pieceId ) )
			return false;

		return ById.TryGetValue( pieceId, out data );
	}

	public static Color ParseFallbackColor( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			return new Color( 0.55f, 0.52f, 0.48f );

		var parts = value.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if ( parts.Length < 3 )
			return new Color( 0.55f, 0.52f, 0.48f );

		if ( !float.TryParse( parts[0], out var r ) || !float.TryParse( parts[1], out var g ) || !float.TryParse( parts[2], out var b ) )
			return new Color( 0.55f, 0.52f, 0.48f );

		var a = parts.Length > 3 && float.TryParse( parts[3], out var alpha ) ? alpha : 1f;
		return new Color( r, g, b, a );
	}

	static int TryReadJsonHash()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( BuildPiecesFilePath ) )
				return 0;

			return StringComparer.Ordinal.GetHashCode( FileSystem.Mounted.ReadAllText( BuildPiecesFilePath ) );
		}
		catch
		{
			return 0;
		}
	}

	static bool TryLoadFromFile()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( BuildPiecesFilePath ) )
				return false;

			var json = FileSystem.Mounted.ReadAllText( BuildPiecesFilePath );
			var file = JsonSerializer.Deserialize<BuildPiecesFile>( json, JsonOptions );
			if ( file?.BuildPieces is null || file.BuildPieces.Count == 0 )
				return false;

			for ( var i = 0; i < file.BuildPieces.Count; i++ )
			{
				var entry = file.BuildPieces[i];
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Id ) )
					continue;

				if ( string.Equals( entry.Id, "45roofCorner", StringComparison.OrdinalIgnoreCase ) )
					continue;

				ApplyStandardHalfExtents( entry );
				BuildSnapDefaults.EnsureDefaults( entry );
				Pieces.Add( entry );
			}

			if ( Pieces.Count == 0 )
				return false;

			RebuildLookup();
			return true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[BuildPieceCatalog] Failed to load {BuildPiecesFilePath}: {ex.Message}" );
			return false;
		}
	}

	static void ApplyStandardHalfExtents( BuildPieceData entry )
	{
		if ( entry is null || !BuildModuleDimensions.TryGetHalfExtents( entry.Id, out var half ) )
			return;

		entry.HalfWidth = half.x;
		entry.HalfHeight = half.y;
		entry.HalfDepth = half.z;
	}

	static void RebuildLookup()
	{
		ById.Clear();
		for ( var i = 0; i < Pieces.Count; i++ )
		{
			var entry = Pieces[i];
			if ( entry is null || string.IsNullOrWhiteSpace( entry.Id ) )
				continue;

			ById[entry.Id] = entry;
		}
		for ( var i = 0; i < Pieces.Count; i++ )
		{
			var entry = Pieces[i];
			if ( entry is null )
				continue;

			BuildSnapDefaults.EnsureDefaults( entry );
		}
	}

	static IEnumerable<BuildPieceData> CreateFallbackPieces()
	{
		var floorHalf = BuildModuleDimensions.FloorHalfExtents;
		yield return new BuildPieceData
		{
			Id = "foundation",
			DisplayName = "Floor",
			Icon = "ui/build/foundation.png",
			Prefab = "prefabs/build/foundation.prefab",
			HalfWidth = floorHalf.x,
			HalfHeight = floorHalf.y,
			HalfDepth = floorHalf.z,
			FallbackColor = "0.52,0.48,0.42,1",
			Costs = { new BuildPieceCost { ResourceId = "wood", Amount = 5 } },
		};
		var wallHalf = BuildModuleDimensions.WallHalfExtents;
		yield return new BuildPieceData
		{
			Id = "wall",
			DisplayName = "Wall",
			Icon = "ui/build/wall.png",
			Prefab = "prefabs/build/wall.prefab",
			HalfWidth = wallHalf.x,
			HalfHeight = wallHalf.y,
			HalfDepth = wallHalf.z,
			FallbackColor = "0.62,0.58,0.52,1",
			Costs = { new BuildPieceCost { ResourceId = "wood", Amount = 3 } },
		};
		var roofHalf = BuildModuleDimensions.RoofHalfExtents;
		yield return new BuildPieceData
		{
			Id = "45roof",
			DisplayName = "45° Roof",
			Icon = "ui/build/45roof.png",
			Prefab = "prefabs/build/45roof.prefab",
			HalfWidth = roofHalf.x,
			HalfHeight = roofHalf.y,
			HalfDepth = roofHalf.z,
			FallbackColor = "0.48,0.36,0.28,1",
			Costs = { new BuildPieceCost { ResourceId = "wood", Amount = 2 } },
		};
		yield return new BuildPieceData
		{
			Id = "repair",
			DisplayName = "Repair",
			Icon = "ui/build/repair.png",
			FallbackColor = "0.72,0.58,0.28,1",
			IsRepairTool = true,
			AllowTerrainPlacement = false,
			Costs = { new BuildPieceCost { ResourceId = "wood", Amount = 1 } },
		};
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class BuildPiecesFile
	{
		public List<BuildPieceData> BuildPieces { get; set; } = new();
	}
}
