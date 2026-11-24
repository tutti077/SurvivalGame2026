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

	public const string BlockId = "block";
	/// <summary>A blocked heavy swing (its own row; the blocker's stagger comes from the attacker's weapon class).</summary>
	public const string BlockHeavyId = "blockHeavy";
	/// <summary>Outcome id of a chamber (built in code — it costs nothing and is not a row).</summary>
	public const string ChamberId = "chamber";

	/// <summary>The outcome for a stopped hit: a heavy swing's block row, else the light one.</summary>
	public static MeleeBlockOutcome Resolve( bool heavy = false )
	{
		EnsureLoaded();
		var id = heavy ? BlockHeavyId : BlockId;

		if ( !Outcomes.TryGetValue( id, out var row ) || row is null )
			row = CreateFallback( id );

		return new MeleeBlockOutcome
		{
			OutcomeId = id,
			Tier = ParseTier( row.Tier ),
			DurationSeconds = Math.Max( 0f, row.DurationSeconds ),
			HealthDamage = Math.Max( 0f, row.HealthDamage ),
			StaminaCost = Math.Max( 0f, row.StaminaCost ),
		};
	}

	/// <summary>A chamber: the defender's swing intercepted the blade — no damage, no stamina, no stagger.</summary>
	public static MeleeBlockOutcome Chamber() => new()
	{
		OutcomeId = ChamberId,
		Tier = MeleeStaggerTier.None,
		WasChambered = true,
	};

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
		foreach ( var id in new[] { BlockId, BlockHeavyId } )
		{
			if ( !Outcomes.ContainsKey( id ) )
				Outcomes[id] = CreateFallback( id );
		}
	}

	static void SeedFallbacks()
	{
		Outcomes[BlockId] = CreateFallback( BlockId );
		Outcomes[BlockHeavyId] = CreateFallback( BlockHeavyId );
	}

	static MeleeBlockStaggerOutcomeData CreateFallback( string id ) => id switch
	{
		BlockId => new MeleeBlockStaggerOutcomeData { Tier = "Light", DurationSeconds = 0f, HealthDamage = 0f, StaminaCost = 8f },
		BlockHeavyId => new MeleeBlockStaggerOutcomeData { Tier = "Heavy", DurationSeconds = 0f, HealthDamage = 0f, StaminaCost = 18f },
		_ => new MeleeBlockStaggerOutcomeData { Tier = "Light", DurationSeconds = 0f, HealthDamage = 0f, StaminaCost = 8f },
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
