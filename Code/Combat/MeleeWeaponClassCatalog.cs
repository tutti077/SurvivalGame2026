using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sandbox;

namespace Survival;

/// <summary>
/// One melee weapon class row from <c>data/melee_weapon_classes.json</c> (oneHanded / twoHanded / spear / dagger).
/// All timings follow the attack pattern vocabulary: windup → charge → active → recovery.
/// </summary>
public sealed class MeleeWeaponClassData
{
	[JsonPropertyName( "classId" )]
	public string ClassId { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	/// <summary>Attack initiated → weapon raised. Elapses while the button is held; a quick click still plays the remainder before the sweep.</summary>
	public float WindupSeconds { get; set; } = 0.22f;

	/// <summary>Extra hold at the top of the windup to turn the release into a heavy attack (heavy at hold ≥ windup + charge).</summary>
	public float ChargeSeconds { get; set; } = 0.48f;

	/// <summary>Sweep duration while the blade deals damage (light attack).</summary>
	public float ActiveSeconds { get; set; } = 0.10f;

	public float HeavyActiveSeconds { get; set; } = 0.15f;

	/// <summary>Attacker lock after a completed light swing, added after the swing animation's return frames.</summary>
	public float RecoverySeconds { get; set; } = 0.10f;

	public float HeavyRecoverySeconds { get; set; } = 0.12f;

	/// <summary>Blade reach for the overhead/forward attack, in meters (converted once per pawn via BodyHeight/1.8).</summary>
	public float ReachForwardMeters { get; set; } = 1.9f;

	/// <summary>Blade reach for left/right slashes, in meters.</summary>
	public float ReachLateralMeters { get; set; } = 1.9f;

	public float StaminaLightCost { get; set; } = 8f;

	public float StaminaHeavyCost { get; set; } = 15f;
}

/// <summary>Optional per-weapon overrides in <c>equipment_profiles.json</c> — only set fields replace the class value.</summary>
public sealed class MeleeTimingOverridesData
{
	public float? WindupSeconds { get; set; }
	public float? ChargeSeconds { get; set; }
	public float? ActiveSeconds { get; set; }
	public float? HeavyActiveSeconds { get; set; }
	public float? RecoverySeconds { get; set; }
	public float? HeavyRecoverySeconds { get; set; }
	public float? ReachForwardMeters { get; set; }
	public float? ReachLateralMeters { get; set; }
	public float? StaminaLightCost { get; set; }
	public float? StaminaHeavyCost { get; set; }
}

/// <summary>Resolved timings for the weapon in hand (class values with per-weapon overrides applied).</summary>
public readonly struct MeleeWeaponTimings
{
	public string ClassId { get; init; }
	public float WindupSeconds { get; init; }
	public float ChargeSeconds { get; init; }
	public float ActiveSeconds { get; init; }
	public float HeavyActiveSeconds { get; init; }
	public float RecoverySeconds { get; init; }
	public float HeavyRecoverySeconds { get; init; }
	public float ReachForwardMeters { get; init; }
	public float ReachLateralMeters { get; init; }
	public float StaminaLightCost { get; init; }
	public float StaminaHeavyCost { get; init; }

	/// <summary>Total hold required for a heavy attack (windup + charge).</summary>
	public float HeavyHoldThresholdSeconds => WindupSeconds + ChargeSeconds;
}

/// <summary>Loads melee weapon class timing profiles from <c>data/melee_weapon_classes.json</c>.</summary>
public static class MeleeWeaponClassCatalog
{
	const string FilePath = "data/melee_weapon_classes.json";
	public const string DefaultClassId = "oneHanded";

	static readonly Dictionary<string, MeleeWeaponClassData> ByClassId =
		new( StringComparer.OrdinalIgnoreCase );

	static bool _loaded;
	static int _loadedJsonHash;

	/// <summary>Bumped on every reload so cached resolved timings can invalidate.</summary>
	public static int Version { get; private set; }

	public static void EnsureLoaded()
	{
		var jsonHash = TryReadJsonHash();
		if ( _loaded && jsonHash == _loadedJsonHash )
			return;

		ReloadFromDisk( jsonHash );
	}

	static void ReloadFromDisk( int jsonHash )
	{
		_loaded = true;
		_loadedJsonHash = jsonHash;
		Version++;
		ByClassId.Clear();

		if ( !TryLoadFromFile() )
			Log.Warning( "[MeleeWeaponClassCatalog] Using built-in fallback weapon class timings." );

		// Built-in defaults fill any class missing from JSON so a bad edit never leaves a weapon timing-less.
		foreach ( var fallback in CreateFallbackClasses() )
		{
			if ( !ByClassId.ContainsKey( fallback.ClassId ) )
				ByClassId[fallback.ClassId] = fallback;
		}
	}

	/// <summary>Resolved timings for a class id, with optional per-weapon overrides. Unknown/empty ids fall back to <see cref="DefaultClassId"/>.</summary>
	public static MeleeWeaponTimings Resolve( string classId, MeleeTimingOverridesData overrides = null )
	{
		EnsureLoaded();

		if ( string.IsNullOrWhiteSpace( classId ) || !ByClassId.TryGetValue( classId.Trim(), out var data ) )
			data = ByClassId[DefaultClassId];

		return new MeleeWeaponTimings
		{
			ClassId = data.ClassId,
			WindupSeconds = Math.Max( 0f, overrides?.WindupSeconds ?? data.WindupSeconds ),
			ChargeSeconds = Math.Max( 0.05f, overrides?.ChargeSeconds ?? data.ChargeSeconds ),
			ActiveSeconds = Math.Max( 0.04f, overrides?.ActiveSeconds ?? data.ActiveSeconds ),
			HeavyActiveSeconds = Math.Max( 0.04f, overrides?.HeavyActiveSeconds ?? data.HeavyActiveSeconds ),
			RecoverySeconds = Math.Max( 0f, overrides?.RecoverySeconds ?? data.RecoverySeconds ),
			HeavyRecoverySeconds = Math.Max( 0f, overrides?.HeavyRecoverySeconds ?? data.HeavyRecoverySeconds ),
			ReachForwardMeters = Math.Max( 0.3f, overrides?.ReachForwardMeters ?? data.ReachForwardMeters ),
			ReachLateralMeters = Math.Max( 0.3f, overrides?.ReachLateralMeters ?? data.ReachLateralMeters ),
			StaminaLightCost = Math.Max( 0f, overrides?.StaminaLightCost ?? data.StaminaLightCost ),
			StaminaHeavyCost = Math.Max( 0f, overrides?.StaminaHeavyCost ?? data.StaminaHeavyCost )
		};
	}

	static int TryReadJsonHash()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( FilePath ) )
				return 0;

			return StringComparer.Ordinal.GetHashCode( FileSystem.Mounted.ReadAllText( FilePath ) );
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
			if ( !FileSystem.Mounted.FileExists( FilePath ) )
				return false;

			var json = FileSystem.Mounted.ReadAllText( FilePath );
			var file = JsonSerializer.Deserialize<MeleeWeaponClassesFile>( json, JsonOptions );
			if ( file?.Classes is null || file.Classes.Count == 0 )
				return false;

			foreach ( var entry in file.Classes )
			{
				if ( entry is null || string.IsNullOrWhiteSpace( entry.ClassId ) )
					continue;

				ByClassId[entry.ClassId.Trim()] = entry;
			}

			return ByClassId.Count > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[MeleeWeaponClassCatalog] Failed to load {FilePath}: {ex.Message}" );
			return false;
		}
	}

	static IEnumerable<MeleeWeaponClassData> CreateFallbackClasses()
	{
		yield return new MeleeWeaponClassData
		{
			ClassId = "oneHanded",
			DisplayName = "One-Handed",
			WindupSeconds = 0.22f,
			ChargeSeconds = 0.48f,
			ActiveSeconds = 0.10f,
			HeavyActiveSeconds = 0.15f,
			RecoverySeconds = 0.10f,
			HeavyRecoverySeconds = 0.12f,
			ReachForwardMeters = 1.9f,
			ReachLateralMeters = 1.9f,
			StaminaLightCost = 8f,
			StaminaHeavyCost = 15f
		};

		yield return new MeleeWeaponClassData
		{
			ClassId = "twoHanded",
			DisplayName = "Two-Handed",
			WindupSeconds = 0.32f,
			ChargeSeconds = 0.55f,
			ActiveSeconds = 0.14f,
			HeavyActiveSeconds = 0.20f,
			RecoverySeconds = 0.25f,
			HeavyRecoverySeconds = 0.30f,
			ReachForwardMeters = 2.2f,
			ReachLateralMeters = 2.2f,
			StaminaLightCost = 12f,
			StaminaHeavyCost = 22f
		};

		yield return new MeleeWeaponClassData
		{
			ClassId = "spear",
			DisplayName = "Spear",
			WindupSeconds = 0.26f,
			ChargeSeconds = 0.50f,
			ActiveSeconds = 0.12f,
			HeavyActiveSeconds = 0.16f,
			RecoverySeconds = 0.18f,
			HeavyRecoverySeconds = 0.22f,
			ReachForwardMeters = 2.6f,
			ReachLateralMeters = 1.6f,
			StaminaLightCost = 9f,
			StaminaHeavyCost = 16f
		};

		yield return new MeleeWeaponClassData
		{
			ClassId = "dagger",
			DisplayName = "Dagger",
			WindupSeconds = 0.12f,
			ChargeSeconds = 0.35f,
			ActiveSeconds = 0.07f,
			HeavyActiveSeconds = 0.10f,
			RecoverySeconds = 0.05f,
			HeavyRecoverySeconds = 0.08f,
			ReachForwardMeters = 1.3f,
			ReachLateralMeters = 1.2f,
			StaminaLightCost = 5f,
			StaminaHeavyCost = 9f
		};
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class MeleeWeaponClassesFile
	{
		public List<MeleeWeaponClassData> Classes { get; set; } = new();
	}
}
