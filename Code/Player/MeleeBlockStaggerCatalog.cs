using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads block stagger outcomes from <c>data/melee_block_stagger.json</c>.</summary>
public static class MeleeBlockStaggerCatalog
{
	const string FilePath = "data/melee_block_stagger.json";

	static readonly Dictionary<string, MeleeBlockStaggerOutcomeData> Outcomes =
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

	public static MeleeBlockOutcome Resolve( bool attackWasHeavy, bool perfectParry )
	{
		EnsureLoaded();
		var id = (attackWasHeavy, perfectParry) switch
		{
			(false, false) => "lightBlock",
			(false, true) => "lightParry",
			(true, false) => "heavyBlock",
			(true, true) => "heavyParry",
		};

		if ( !Outcomes.TryGetValue( id, out var row ) || row is null )
			row = CreateFallback( id );

		return new MeleeBlockOutcome
		{
			OutcomeId = id,
			Tier = ParseTier( row.Tier ),
			DurationSeconds = Math.Max( 0f, row.DurationSeconds ),
			HealthDamage = Math.Max( 0f, row.HealthDamage ),
			StaminaCost = Math.Max( 0f, row.StaminaCost ),
			WasPerfectParry = perfectParry,
		};
	}

	static void ReloadFromDisk()
	{
		_loaded = true;
		Outcomes.Clear();

		if ( TryLoadFromFile() )
			return;

		SeedFallbacks();
		Log.Warning( "[MeleeBlockStaggerCatalog] Using built-in fallback block stagger outcomes." );
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

				var file = JsonSerializer.Deserialize<MeleeBlockStaggerFile>( json );
				if ( file?.Outcomes is null || file.Outcomes.Count == 0 )
					continue;

				foreach ( var (key, value) in file.Outcomes )
				{
					if ( string.IsNullOrWhiteSpace( key ) || value is null )
						continue;
					Outcomes[key.Trim()] = value;
				}

				EnsureRequiredKeys();
				Log.Info( $"[MeleeBlockStaggerCatalog] Loaded {Outcomes.Count} outcomes from '{path}'." );
				return Outcomes.Count > 0;
			}
			catch ( Exception e )
			{
				Log.Warning( $"[MeleeBlockStaggerCatalog] Failed reading '{path}': {e.Message}" );
			}
		}

		return false;
	}

	static IEnumerable<string> GetPathCandidates()
	{
		yield return FilePath;
		yield return "Assets/" + FilePath;
	}

	static void EnsureRequiredKeys()
	{
		foreach ( var id in new[] { "lightBlock", "lightParry", "heavyBlock", "heavyParry" } )
		{
			if ( !Outcomes.ContainsKey( id ) )
				Outcomes[id] = CreateFallback( id );
		}
	}

	static void SeedFallbacks()
	{
		Outcomes["lightBlock"] = CreateFallback( "lightBlock" );
		Outcomes["lightParry"] = CreateFallback( "lightParry" );
		Outcomes["heavyBlock"] = CreateFallback( "heavyBlock" );
		Outcomes["heavyParry"] = CreateFallback( "heavyParry" );
	}

	static MeleeBlockStaggerOutcomeData CreateFallback( string id ) => id switch
	{
		"lightParry" => new MeleeBlockStaggerOutcomeData { Tier = "Light", DurationSeconds = 0f, HealthDamage = 0f, StaminaCost = 0f },
		"heavyBlock" => new MeleeBlockStaggerOutcomeData { Tier = "Light", DurationSeconds = 0.55f, HealthDamage = 2f, StaminaCost = 12f },
		"heavyParry" => new MeleeBlockStaggerOutcomeData { Tier = "Light", DurationSeconds = 0.25f, HealthDamage = 1f, StaminaCost = 6f },
		_ => new MeleeBlockStaggerOutcomeData { Tier = "Light", DurationSeconds = 0.35f, HealthDamage = 0f, StaminaCost = 8f },
	};

	static MeleeStaggerTier ParseTier( string raw )
	{
		if ( string.IsNullOrWhiteSpace( raw ) )
			return MeleeStaggerTier.Light;

		if ( raw.Equals( "Heavy", StringComparison.OrdinalIgnoreCase ) )
			return MeleeStaggerTier.Heavy;
		if ( raw.Equals( "None", StringComparison.OrdinalIgnoreCase ) )
			return MeleeStaggerTier.None;
		return MeleeStaggerTier.Light;
	}
}
