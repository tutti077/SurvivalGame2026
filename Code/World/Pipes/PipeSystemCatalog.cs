using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>The pieces of the pipe kit (<c>Blender/scripts/create_pipe_kit.py</c>): start joint at the origin, pipe along local +X.</summary>
public enum PipePieceKind
{
	Straight8,
	Straight4,
	Bend,
	Support,
	Footing,
	GroundEntry,
	CrushedEnd,
	CementCube,
}

/// <summary><c>data/pipe_systems.json</c>: how many pipe systems the world gets and the shape rules of a route. Metres.</summary>
public sealed class PipeSystemConfig
{
	public bool Enabled { get; set; } = true;
	public float CellMeters { get; set; } = 2000f;
	public float Chance { get; set; } = 0.5f;
	public int CandidatesPerCell { get; set; } = 8;
	public float SpawnClearanceMeters { get; set; } = 150f;
	public float LengthMinMeters { get; set; } = 90f;
	public float LengthMaxMeters { get; set; } = 220f;
	public float ClearanceMeters { get; set; } = 4f;
	public float ClearanceMinMeters { get; set; } = 2.8f;
	public float ClearanceMaxMeters { get; set; } = 5.6f;
	public float MaxTurnDegrees { get; set; } = 30f;
	public float MaxJointDegrees { get; set; } = 8f;
	public float MaxPitchDegrees { get; set; } = 22f;
	public int SupportEverySegments { get; set; } = 2;
	public float CrushedJointDepthMeters { get; set; } = 1.8f;
	public float DeepEndDepthMeters { get; set; } = 20f;
	public float VegetationClearanceMeters { get; set; } = 3f;
	public List<string> Biomes { get; set; } = new();
	public Dictionary<string, string> Prefabs { get; set; } = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Parsed <see cref="Biomes"/>; empty = every land biome.</summary>
	internal HashSet<TerrainPreviewBiomeId> BiomeSet { get; } = new();

	public bool AllowsBiome( TerrainPreviewBiomeId biome )
		=> biome is not (TerrainPreviewBiomeId.None or TerrainPreviewBiomeId.Water) && (BiomeSet.Count == 0 || BiomeSet.Contains( biome ));

	public string PrefabFor( PipePieceKind kind )
		=> Prefabs.TryGetValue( kind.ToString(), out var path ) ? path : null;
}

/// <summary>Loads <c>data/pipe_systems.json</c>. Re-read once per world load (<see cref="TerrainWorldManager"/>) and by <c>pipe_info reload</c>.</summary>
public static class PipeSystemCatalog
{
	const string FilePath = "data/pipe_systems.json";

	static PipeSystemConfig _config;

	public static PipeSystemConfig Config
	{
		get
		{
			EnsureLoaded();
			return _config;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _config is not null )
			return;
		_config = Load() ?? new PipeSystemConfig { Enabled = false };
	}

	public static void Reload()
	{
		_config = null;
		EnsureLoaded();
		PipeRoutePlanner.ClearCache();
	}

	static PipeSystemConfig Load()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( FilePath ) )
			{
				Log.Warning( "[Pipes] No pipe_systems.json - no pipe systems in this world." );
				return null;
			}

			var config = JsonSerializer.Deserialize<PipeSystemConfig>( FileSystem.Mounted.ReadAllText( FilePath ), JsonOptions );
			if ( config is null )
				return null;

			// A route must stay inside its own cell (start in the middle half, run + dives under a quarter cell)
			config.CellMeters = Math.Clamp( config.CellMeters, 1200f, 20000f );
			config.LengthMinMeters = Math.Clamp( config.LengthMinMeters, 48f, 240f );
			config.LengthMaxMeters = Math.Clamp( config.LengthMaxMeters, config.LengthMinMeters, 240f );
			config.CandidatesPerCell = Math.Clamp( config.CandidatesPerCell, 1, 32 );
			config.SupportEverySegments = Math.Max( 1, config.SupportEverySegments );
			config.Prefabs = new Dictionary<string, string>( config.Prefabs ?? new(), StringComparer.OrdinalIgnoreCase );
			foreach ( var name in config.Biomes ?? new() )
			{
				if ( Enum.TryParse<TerrainPreviewBiomeId>( name, true, out var biome ) )
					config.BiomeSet.Add( biome );
				else
					Log.Warning( $"[Pipes] {FilePath}: unknown biome '{name}'" );
			}
			return config;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[Pipes] Failed to load {FilePath}: {ex.Message}" );
			return null;
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};
}
