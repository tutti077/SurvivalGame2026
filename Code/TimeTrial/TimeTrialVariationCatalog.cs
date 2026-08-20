using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

public sealed class TimeTrialVariationData
{
	public string Id { get; set; } = "";
	public string DisplayName { get; set; } = "";
	public List<int> CheckpointOrders { get; set; } = new();
}

/// <summary>Loads race variations from <c>data/time_trial_variations.json</c>.</summary>
public static class TimeTrialVariationCatalog
{
	const string FilePath = "data/time_trial_variations.json";

	static readonly List<TimeTrialVariationData> Variations = new();
	static readonly Dictionary<string, TimeTrialVariationData> ById =
		new( StringComparer.OrdinalIgnoreCase );
	static bool _loaded;

	public static IReadOnlyList<TimeTrialVariationData> All
	{
		get
		{
			EnsureLoaded();
			return Variations;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		Variations.Clear();
		ById.Clear();

		if ( !TryLoadFromFile() )
		{
			Log.Warning( "[TimeTrialVariationCatalog] Missing/invalid time_trial_variations.json — using Race 1 fallback." );
			var fallback = new TimeTrialVariationData
			{
				Id = "race1_bunny_slope",
				DisplayName = "Race 1 - Bunny slope",
				CheckpointOrders = new List<int> { 0, 1 },
			};
			Variations.Add( fallback );
			ById[fallback.Id] = fallback;
		}
	}

	public static bool TryGet( string id, out TimeTrialVariationData variation )
	{
		EnsureLoaded();
		variation = null;
		if ( string.IsNullOrWhiteSpace( id ) )
			return false;
		return ById.TryGetValue( id.Trim(), out variation );
	}

	public static TimeTrialVariationData GetOrDefault( string id )
	{
		if ( TryGet( id, out var v ) )
			return v;
		EnsureLoaded();
		return Variations.Count > 0 ? Variations[0] : null;
	}

	static bool TryLoadFromFile()
	{
		try
		{
			foreach ( var path in PathCandidates() )
			{
				if ( !FileSystem.Mounted.FileExists( path ) )
					continue;

				var json = FileSystem.Mounted.ReadAllText( path );
				if ( string.IsNullOrWhiteSpace( json ) )
					continue;

				var file = JsonSerializer.Deserialize<FileRoot>( json, new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true,
				} );
				if ( file?.Variations is null || file.Variations.Count == 0 )
					continue;

				foreach ( var v in file.Variations )
				{
					if ( v is null || string.IsNullOrWhiteSpace( v.Id ) )
						continue;
					if ( v.CheckpointOrders is null || v.CheckpointOrders.Count < 2 )
					{
						Log.Warning( $"[TimeTrialVariationCatalog] '{v.Id}' needs ≥2 checkpointOrders — skipped." );
						continue;
					}

					v.Id = v.Id.Trim();
					if ( string.IsNullOrWhiteSpace( v.DisplayName ) )
						v.DisplayName = v.Id;
					Variations.Add( v );
					ById[v.Id] = v;
				}

				if ( Variations.Count > 0 )
				{
					Log.Info( $"[TimeTrialVariationCatalog] Loaded {Variations.Count} variation(s) from {path}." );
					return true;
				}
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[TimeTrialVariationCatalog] Load failed: {ex.Message}" );
		}

		return false;
	}

	static IEnumerable<string> PathCandidates()
	{
		yield return FilePath;
		yield return $"/{FilePath}";
		yield return $"assets/{FilePath}";
	}

	sealed class FileRoot
	{
		public List<TimeTrialVariationData> Variations { get; set; }
	}
}
