using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads build piece metadata from JSON with a built-in fallback.</summary>
public static class BuildPieceCatalog
{
	const string BuildPiecesFilePath = "data/build_pieces.json";

	static readonly List<BuildPieceData> Pieces = new();
	static readonly Dictionary<string, BuildPieceData> ById =
		new( StringComparer.OrdinalIgnoreCase );

	static bool _loaded;
	static int _loadedJsonHash;
	static bool _isFallbackOnly;
	static string _sourceJson = string.Empty;
	static int _contentVersion;
	static float _lastFallbackRetryTime = -100f;

	public static IReadOnlyList<BuildPieceData> All
	{
		get
		{
			EnsureLoaded();
			return Pieces;
		}
	}

	/// <summary>True when only the built-in fallback pieces are present (JSON load failed).</summary>
	public static bool IsFallbackOnly
	{
		get
		{
			EnsureLoaded();
			return _isFallbackOnly;
		}
	}

	/// <summary>Bumps when the piece list is replaced — UI rebuilds when this changes.</summary>
	public static int ContentVersion
	{
		get
		{
			EnsureLoaded();
			return _contentVersion;
		}
	}

	public static void ForceReload()
	{
		_loaded = false;
		_loadedJsonHash = 0;
		ReloadFromDisk();
		BuildSnapPlacement.InvalidatePieceCache();
	}

	/// <summary>Host-exported JSON for joining clients (empty if nothing loaded yet).</summary>
	public static string ExportSourceJson()
	{
		EnsureLoaded();
		if ( !string.IsNullOrWhiteSpace( _sourceJson ) )
			return _sourceJson;

		foreach ( var path in GetPathCandidates() )
		{
			try
			{
				var json = FileSystem.Mounted.ReadAllText( path );
				if ( !string.IsNullOrWhiteSpace( json ) )
					return json;
			}
			catch
			{
				// try next
			}
		}

		return string.Empty;
	}

	/// <summary>Replace local catalog from host-provided JSON (joining clients).</summary>
	public static bool ReplaceFromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			return false;

		if ( !TryParsePieces( json, out var parsed ) || parsed.Count == 0 )
			return false;

		Pieces.Clear();
		Pieces.AddRange( parsed );
		RebuildLookup();
		_sourceJson = json;
		_loadedJsonHash = StringComparer.Ordinal.GetHashCode( json );
		_isFallbackOnly = false;
		_loaded = true;
		_contentVersion++;
		BuildSnapPlacement.InvalidatePieceCache();
		Log.Info( $"[BuildPieceCatalog] Applied host build piece catalog ({Pieces.Count} pieces)." );
		return true;
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
		{
			// Joining clients often hit mounts too early — retry while stuck on fallback.
			if ( _isFallbackOnly )
				TryReloadIfFallback();
			return;
		}

		ReloadFromDisk();
	}

	static void TryReloadIfFallback()
	{
		if ( Time.Now - _lastFallbackRetryTime < 1f )
			return;

		_lastFallbackRetryTime = Time.Now;
		if ( TryLoadFromFile() )
		{
			_isFallbackOnly = false;
			_contentVersion++;
			BuildSnapPlacement.InvalidatePieceCache();
			Log.Info( $"[BuildPieceCatalog] Recovered full build piece list ({Pieces.Count} pieces)." );
		}
	}

	static void ReloadFromDisk()
	{
		Pieces.Clear();
		ById.Clear();
		_sourceJson = string.Empty;
		_isFallbackOnly = false;

		if ( TryLoadFromFile() )
		{
			_loaded = true;
			_contentVersion++;
			return;
		}

		Pieces.AddRange( CreateFallbackPieces() );
		RebuildLookup();
		_isFallbackOnly = true;
		_loaded = true;
		_loadedJsonHash = 0;
		_contentVersion++;
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

	static bool TryLoadFromFile()
	{
		foreach ( var path in GetPathCandidates() )
		{
			try
			{
				// Do NOT gate on FileExists — it returns false on joining clients while ReadAllText still works.
				var json = FileSystem.Mounted.ReadAllText( path );
				if ( string.IsNullOrWhiteSpace( json ) )
					continue;

				if ( !TryParsePieces( json, out var parsed ) || parsed.Count == 0 )
					continue;

				Pieces.Clear();
				Pieces.AddRange( parsed );
				RebuildLookup();
				_sourceJson = json;
				_loadedJsonHash = StringComparer.Ordinal.GetHashCode( json );
				return true;
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[BuildPieceCatalog] Failed to load '{path}': {ex.Message}" );
			}
		}

		return false;
	}

	static bool TryParsePieces( string json, out List<BuildPieceData> parsed )
	{
		parsed = null;
		try
		{
			var file = JsonSerializer.Deserialize<BuildPiecesFile>( json, JsonOptions );
			if ( file?.BuildPieces is null || file.BuildPieces.Count == 0 )
				return false;

			parsed = new List<BuildPieceData>();
			for ( var i = 0; i < file.BuildPieces.Count; i++ )
			{
				var entry = file.BuildPieces[i];
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Id ) )
					continue;

				if ( string.Equals( entry.Id, "45roofCorner", StringComparison.OrdinalIgnoreCase ) )
					continue;

				ApplyStandardHalfExtents( entry );
				BuildSnapDefaults.EnsureDefaults( entry );
				parsed.Add( entry );
			}

			return parsed.Count > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[BuildPieceCatalog] JSON parse failed: {ex.Message}" );
			return false;
		}
	}

	static IEnumerable<string> GetPathCandidates()
	{
		yield return BuildPiecesFilePath;
		yield return "assets/data/build_pieces.json";
		yield return "/data/build_pieces.json";
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
			Costs = { new BuildPieceCost { ResourceId = "resource_woodBasic", Amount = 5 } },
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
			Costs = { new BuildPieceCost { ResourceId = "resource_woodBasic", Amount = 3 } },
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
			Costs = { new BuildPieceCost { ResourceId = "resource_woodBasic", Amount = 2 } },
		};
		yield return new BuildPieceData
		{
			Id = "repair",
			DisplayName = "Repair",
			Icon = "ui/build/repair.png",
			FallbackColor = "0.72,0.58,0.28,1",
			IsRepairTool = true,
			AllowTerrainPlacement = false,
			Costs = { new BuildPieceCost { ResourceId = "resource_woodBasic", Amount = 1 } },
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
