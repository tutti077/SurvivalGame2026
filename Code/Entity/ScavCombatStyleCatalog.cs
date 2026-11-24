using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>One step of the scav difficulty ladder (a row of <c>Assets/data/scav_combat_styles.json</c>; the file documents each field).</summary>
public sealed class ScavCombatStyle
{
	/// <summary>Shown on the Dev tools button.</summary>
	public string Name { get; set; } = "Medium";
	public float ReactionMinSeconds { get; set; } = 0.3f;
	public float ReactionMaxSeconds { get; set; } = 0.5f;
	public float BlockChance { get; set; } = 0.15f;
	public float WrongGuardChance { get; set; } = 0.3f;
	public float DefensiveSeconds { get; set; } = 1.4f;
	public float DefensiveBlockChance { get; set; } = 0.82f;
	public float DefensiveReactionMinSeconds { get; set; } = 0.19f;
	public float DefensiveReactionMaxSeconds { get; set; } = 0.31f;
	public float ChamberChance { get; set; }
	public float BackstepChance { get; set; } = 0.35f;
	public float BackstepReactionMinSeconds { get; set; } = 0.1f;
	public float BackstepReactionMaxSeconds { get; set; } = 0.22f;
	public float BackstepMeters { get; set; } = 0.6f;
	public float BackstepSeconds { get; set; } = 0.5f;
	public float SpacingChance { get; set; } = 0.25f;
	public float CounterChance { get; set; } = 0.67f;
	public float HeavyChance { get; set; } = 0.08f;
	public float FeintChance { get; set; }
	public float SecondHitChance { get; set; } = 0.2f;
	public float ThirdHitChance { get; set; }
	public float ThinkMinSeconds { get; set; } = 0.9f;
	public float ThinkMaxSeconds { get; set; } = 2f;
	public float GuardLingerSeconds { get; set; } = 0.2f;
	public float StaminaReserve { get; set; }
	public float BreachSwingGapSeconds { get; set; } = 0.6f;
	public float AfterBlockSwingMinSeconds { get; set; } = 0.35f;
	public float AfterBlockSwingMaxSeconds { get; set; } = 0.7f;
	public float TakeoverPauseMinSeconds { get; set; } = 0.5f;
	public float TakeoverPauseMaxSeconds { get; set; } = 1.0f;
}

/// <summary>
/// The scav difficulty ladder from <c>Assets/data/scav_combat_styles.json</c>, easiest first, loaded once. Level 1 is the
/// scav's real difficulty; <see cref="Current"/> follows the host's Dev tools level (<see cref="DevToolsSettings.ScavDifficulty"/>).
/// <see cref="Get(int)"/> is the hook for other melee fighters to pick their own level later.
/// </summary>
public static class ScavCombatStyleCatalog
{
	const string FilePath = "data/scav_combat_styles.json";

	static readonly List<ScavCombatStyle> Levels = new();
	static bool _loaded;

	/// <summary>How many levels the ladder has (at least 1).</summary>
	public static int LevelCount
	{
		get { EnsureLoaded(); return Math.Max( 1, Levels.Count ); }
	}

	/// <summary>The level scavs fight at right now (host: its own Dev tools level).</summary>
	public static ScavCombatStyle Current => Get( DevToolsSettings.ScavDifficulty );

	/// <summary>Level <paramref name="level"/> (1 = easiest), clamped to the ladder.</summary>
	public static ScavCombatStyle Get( int level )
	{
		EnsureLoaded();
		if ( Levels.Count == 0 )
			return Fallback;
		return Levels[Math.Clamp( level, 1, Levels.Count ) - 1];
	}

	static readonly ScavCombatStyle Fallback = new();

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		Levels.Clear();
		foreach ( var path in new[] { FilePath, "Assets/" + FilePath } )
		{
			try
			{
				if ( !FileSystem.Mounted.FileExists( path ) )
					continue;

				var file = JsonSerializer.Deserialize<LadderFile>( FileSystem.Mounted.ReadAllText( path ), JsonOptions );
				if ( file?.Levels is null || file.Levels.Count == 0 )
					continue;

				foreach ( var level in file.Levels )
				{
					if ( level is not null )
						Levels.Add( level );
				}
				return;
			}
			catch ( Exception e )
			{
				Log.Warning( $"[ScavCombatStyleCatalog] Failed reading '{path}': {e.Message}" );
			}
		}

		Log.Warning( "[ScavCombatStyleCatalog] No difficulty file - scavs use the built-in medium level." );
	}

	[ConCmd( "scav_styles_reload" )]
	public static void ConCmdReload()
	{
		_loaded = false;
		EnsureLoaded();
		Log.Info( $"[ScavCombatStyleCatalog] {Levels.Count} levels loaded; scavs fight at {DevToolsSettings.ScavDifficulty} {Current.Name}." );
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	sealed class LadderFile
	{
		public List<ScavCombatStyle> Levels { get; set; }
	}
}
