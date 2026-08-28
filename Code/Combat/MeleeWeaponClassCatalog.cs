using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// One melee weapon class row (oneHanded / twoHanded / spear / dagger …). Edited in the inspector on
/// <see cref="MeleeWeaponTimingLibrary"/>; <c>data/melee_weapon_classes.json</c> is the fallback for
/// scenes without that component. All timings follow the attack pattern: windup → charge → active → recovery.
/// </summary>
public sealed class MeleeWeaponClassData
{
	[KeyProperty]
	public string ClassId { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	// --- Combat timings (attack pattern: windup → charge → active → outcome recovery) ---

	/// <summary>Attack initiated → weapon raised. Elapses while the button is held; a quick click still plays the remainder before the sweep.</summary>
	public float WindupSeconds { get; set; } = 0.2f;

	/// <summary>Windup used instead of <see cref="WindupSeconds"/> while initiative is armed (a recent clean hit).</summary>
	public float InitiativeWindupSeconds { get; set; } = 0.1f;

	/// <summary>Extra hold at the top of the windup to turn the release into a heavy attack (heavy at hold ≥ windup + charge).</summary>
	public float ChargeSeconds { get; set; } = 0.2f;

	/// <summary>Sweep duration while the blade deals damage (light attack). Sheet column: "Action time".</summary>
	public float ActiveSeconds { get; set; } = 0.1f;

	public float HeavyActiveSeconds { get; set; } = 0.1f;

	// --- Combat timings: outcome recoveries (attacker lock after the swing resolves; added after return frames) ---

	public float RecoveryMissSeconds { get; set; } = 0.3f;

	public float RecoveryHitSeconds { get; set; } = 0.2f;

	public float RecoveryBlockedSeconds { get; set; } = 0.2f;

	public float RecoveryParriedSeconds { get; set; } = 0.4f;

	// --- Combat timings: special attack (Q — straight stab in the look direction with a short lunge) ---

	public float SpecialWindupSeconds { get; set; } = 0.3f;

	public float SpecialInitiativeWindupSeconds { get; set; } = 0.2f;

	public float SpecialActiveSeconds { get; set; } = 0.25f;

	public float SpecialStaminaCost { get; set; } = 18f;

	/// <summary>Combat damage multiplier for the special attack (light = 1.0, heavy = 1.0 + heavy bonus).</summary>
	public float SpecialDamageMultiplier { get; set; } = 1.5f;

	/// <summary>Extra thrust reach beyond <see cref="ReachForwardMeters"/>, in meters — the stab pokes a tad past where the swing reaches.</summary>
	public float SpecialReachBonusMeters { get; set; } = 0.3f;

	// --- Combat timings: light combo (STAGED — combo chaining is not implemented; read by nothing) ---

	public float ComboWindupSeconds { get; set; } = 0.1f;

	public float ComboActiveSeconds { get; set; } = 0.2f;

	public float ComboLastHitExtraRecoverySeconds { get; set; } = 0.1f;

	/// <summary>Blade reach for the overhead/forward attack, in meters (converted once per pawn via BodyHeight/1.8).</summary>
	public float ReachForwardMeters { get; set; } = 1.9f;

	/// <summary>Blade reach for left/right slashes, in meters.</summary>
	public float ReachLateralMeters { get; set; } = 1.9f;

	public float StaminaLightCost { get; set; } = 8f;

	public float StaminaHeavyCost { get; set; } = 15f;

	/// <summary>Total horizontal span (°) of left/right slashes — narrow for a spear jab, wide for a two-hander.</summary>
	public float LateralArcDegrees { get; set; } = 150f;

	/// <summary>Overhead/forward arc span (°); end = start − total.</summary>
	public float ForwardArcTotalDegrees { get; set; } = 158f;

	/// <summary>Start angle on the vertical overhead arc (0° = forward, 90° = up). Higher = more raised at windup.</summary>
	public float ForwardArcStartDegrees { get; set; } = 146f;
}

/// <summary>Optional per-weapon overrides in <c>equipment_profiles.json</c> — only set fields replace the class value.</summary>
public sealed class MeleeTimingOverridesData
{
	public float? WindupSeconds { get; set; }
	public float? InitiativeWindupSeconds { get; set; }
	public float? ChargeSeconds { get; set; }
	public float? ActiveSeconds { get; set; }
	public float? HeavyActiveSeconds { get; set; }
	public float? RecoveryMissSeconds { get; set; }
	public float? RecoveryHitSeconds { get; set; }
	public float? RecoveryBlockedSeconds { get; set; }
	public float? RecoveryParriedSeconds { get; set; }
	public float? SpecialWindupSeconds { get; set; }
	public float? SpecialInitiativeWindupSeconds { get; set; }
	public float? SpecialActiveSeconds { get; set; }
	public float? SpecialStaminaCost { get; set; }
	public float? SpecialDamageMultiplier { get; set; }
	public float? SpecialReachBonusMeters { get; set; }
	public float? ComboWindupSeconds { get; set; }
	public float? ComboActiveSeconds { get; set; }
	public float? ComboLastHitExtraRecoverySeconds { get; set; }
	public float? ReachForwardMeters { get; set; }
	public float? ReachLateralMeters { get; set; }
	public float? StaminaLightCost { get; set; }
	public float? StaminaHeavyCost { get; set; }
	public float? LateralArcDegrees { get; set; }
	public float? ForwardArcTotalDegrees { get; set; }
	public float? ForwardArcStartDegrees { get; set; }
}

/// <summary>Resolved timings for the weapon in hand (class values with per-weapon overrides applied).</summary>
public readonly struct MeleeWeaponTimings
{
	public string ClassId { get; init; }
	public float WindupSeconds { get; init; }
	public float InitiativeWindupSeconds { get; init; }
	public float ChargeSeconds { get; init; }
	public float ActiveSeconds { get; init; }
	public float HeavyActiveSeconds { get; init; }
	public float RecoveryMissSeconds { get; init; }
	public float RecoveryHitSeconds { get; init; }
	public float RecoveryBlockedSeconds { get; init; }
	public float RecoveryParriedSeconds { get; init; }
	public float SpecialWindupSeconds { get; init; }
	public float SpecialInitiativeWindupSeconds { get; init; }
	public float SpecialActiveSeconds { get; init; }
	public float SpecialStaminaCost { get; init; }
	public float SpecialDamageMultiplier { get; init; }
	public float SpecialReachBonusMeters { get; init; }
	public float ComboWindupSeconds { get; init; }
	public float ComboActiveSeconds { get; init; }
	public float ComboLastHitExtraRecoverySeconds { get; init; }
	public float ReachForwardMeters { get; init; }
	public float ReachLateralMeters { get; init; }
	public float StaminaLightCost { get; init; }
	public float StaminaHeavyCost { get; init; }
	public float LateralArcDegrees { get; init; }
	public float ForwardArcTotalDegrees { get; init; }
	public float ForwardArcStartDegrees { get; init; }

	/// <summary>Total hold required for a heavy attack (windup + charge).</summary>
	public float HeavyHoldThresholdSeconds => WindupSeconds + ChargeSeconds;
}

/// <summary>
/// Designer home for ALL melee combat timings: every weapon class row, editable in the inspector
/// (place one on the scene's NetworkManager, next to <see cref="CombatAuthority"/>). Rows resolve
/// LIVE — tweak values in play mode and the very next swing uses them. Scenes without this
/// component fall back to <c>data/melee_weapon_classes.json</c>, then built-in defaults.
/// </summary>
[Title( "Melee Weapon Timings" )]
public sealed class MeleeWeaponTimingLibrary : Component
{
	public static MeleeWeaponTimingLibrary Instance { get; private set; }

	[Property, Group( "Combat Timings" ), Title( "Weapon class timings" )]
	public List<MeleeWeaponClassData> Classes { get; set; } = new();

	protected override void OnEnabled()
	{
		if ( Instance is not null && Instance != this )
			Log.Warning( "[MeleeWeaponTimingLibrary] Multiple enabled libraries — Instance points at the last enabled." );
		Instance = this;
	}

	protected override void OnDisabled()
	{
		if ( Instance == this )
			Instance = null;
	}

	internal bool TryGetClass( string classId, out MeleeWeaponClassData data )
	{
		data = null;
		if ( Classes is null || string.IsNullOrWhiteSpace( classId ) )
			return false;

		foreach ( var row in Classes )
		{
			if ( row is not null && string.Equals( row.ClassId?.Trim(), classId.Trim(), StringComparison.OrdinalIgnoreCase ) )
			{
				data = row;
				return true;
			}
		}

		return false;
	}
}

/// <summary>Resolves weapon class timings: inspector <see cref="MeleeWeaponTimingLibrary"/> first, then <c>data/melee_weapon_classes.json</c>, then built-ins.</summary>
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

	/// <summary>Resolved timings for a class id, with optional per-weapon overrides. Inspector library first, then JSON; unknown/empty ids fall back to <see cref="DefaultClassId"/>.</summary>
	public static MeleeWeaponTimings Resolve( string classId, MeleeTimingOverridesData overrides = null )
	{
		MeleeWeaponClassData data = null;

		if ( MeleeWeaponTimingLibrary.Instance is { } library && library.IsValid()
		     && !library.TryGetClass( classId, out data ) )
			library.TryGetClass( DefaultClassId, out data );

		if ( data is null )
		{
			EnsureLoaded();
			if ( string.IsNullOrWhiteSpace( classId ) || !ByClassId.TryGetValue( classId.Trim(), out data ) )
				data = ByClassId[DefaultClassId];
		}

		return new MeleeWeaponTimings
		{
			ClassId = data.ClassId,
			WindupSeconds = Math.Max( 0f, overrides?.WindupSeconds ?? data.WindupSeconds ),
			InitiativeWindupSeconds = Math.Max( 0f, overrides?.InitiativeWindupSeconds ?? data.InitiativeWindupSeconds ),
			ChargeSeconds = Math.Max( 0.05f, overrides?.ChargeSeconds ?? data.ChargeSeconds ),
			ActiveSeconds = Math.Max( 0.04f, overrides?.ActiveSeconds ?? data.ActiveSeconds ),
			HeavyActiveSeconds = Math.Max( 0.04f, overrides?.HeavyActiveSeconds ?? data.HeavyActiveSeconds ),
			RecoveryMissSeconds = Math.Max( 0f, overrides?.RecoveryMissSeconds ?? data.RecoveryMissSeconds ),
			RecoveryHitSeconds = Math.Max( 0f, overrides?.RecoveryHitSeconds ?? data.RecoveryHitSeconds ),
			RecoveryBlockedSeconds = Math.Max( 0f, overrides?.RecoveryBlockedSeconds ?? data.RecoveryBlockedSeconds ),
			RecoveryParriedSeconds = Math.Max( 0f, overrides?.RecoveryParriedSeconds ?? data.RecoveryParriedSeconds ),
			SpecialWindupSeconds = Math.Max( 0f, overrides?.SpecialWindupSeconds ?? data.SpecialWindupSeconds ),
			SpecialInitiativeWindupSeconds = Math.Max( 0f, overrides?.SpecialInitiativeWindupSeconds ?? data.SpecialInitiativeWindupSeconds ),
			SpecialActiveSeconds = Math.Max( 0.04f, overrides?.SpecialActiveSeconds ?? data.SpecialActiveSeconds ),
			SpecialStaminaCost = Math.Max( 0f, overrides?.SpecialStaminaCost ?? data.SpecialStaminaCost ),
			SpecialDamageMultiplier = Math.Max( 0f, overrides?.SpecialDamageMultiplier ?? data.SpecialDamageMultiplier ),
			SpecialReachBonusMeters = Math.Max( 0f, overrides?.SpecialReachBonusMeters ?? data.SpecialReachBonusMeters ),
			ComboWindupSeconds = Math.Max( 0f, overrides?.ComboWindupSeconds ?? data.ComboWindupSeconds ),
			ComboActiveSeconds = Math.Max( 0.04f, overrides?.ComboActiveSeconds ?? data.ComboActiveSeconds ),
			ComboLastHitExtraRecoverySeconds = Math.Max( 0f, overrides?.ComboLastHitExtraRecoverySeconds ?? data.ComboLastHitExtraRecoverySeconds ),
			ReachForwardMeters = Math.Max( 0.3f, overrides?.ReachForwardMeters ?? data.ReachForwardMeters ),
			ReachLateralMeters = Math.Max( 0.3f, overrides?.ReachLateralMeters ?? data.ReachLateralMeters ),
			StaminaLightCost = Math.Max( 0f, overrides?.StaminaLightCost ?? data.StaminaLightCost ),
			StaminaHeavyCost = Math.Max( 0f, overrides?.StaminaHeavyCost ?? data.StaminaHeavyCost ),
			LateralArcDegrees = Math.Clamp( overrides?.LateralArcDegrees ?? data.LateralArcDegrees, 20f, 340f ),
			ForwardArcTotalDegrees = Math.Clamp( overrides?.ForwardArcTotalDegrees ?? data.ForwardArcTotalDegrees, 90f, 180f ),
			ForwardArcStartDegrees = overrides?.ForwardArcStartDegrees ?? data.ForwardArcStartDegrees
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
			WindupSeconds = 0.2f,
			InitiativeWindupSeconds = 0.1f,
			ChargeSeconds = 0.2f,
			ActiveSeconds = 0.1f,
			HeavyActiveSeconds = 0.1f,
			RecoveryMissSeconds = 0.3f,
			RecoveryHitSeconds = 0.2f,
			RecoveryBlockedSeconds = 0.2f,
			RecoveryParriedSeconds = 0.4f,
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
			InitiativeWindupSeconds = 0.16f,
			ChargeSeconds = 0.55f,
			ActiveSeconds = 0.14f,
			HeavyActiveSeconds = 0.20f,
			RecoveryMissSeconds = 0.45f,
			RecoveryHitSeconds = 0.3f,
			RecoveryBlockedSeconds = 0.3f,
			RecoveryParriedSeconds = 0.6f,
			ReachForwardMeters = 2.2f,
			ReachLateralMeters = 2.2f,
			StaminaLightCost = 12f,
			StaminaHeavyCost = 22f,
			LateralArcDegrees = 170f,
			ForwardArcTotalDegrees = 165f,
			ForwardArcStartDegrees = 150f
		};

		yield return new MeleeWeaponClassData
		{
			ClassId = "spear",
			DisplayName = "Spear",
			WindupSeconds = 0.26f,
			InitiativeWindupSeconds = 0.13f,
			ChargeSeconds = 0.50f,
			ActiveSeconds = 0.12f,
			HeavyActiveSeconds = 0.16f,
			RecoveryMissSeconds = 0.35f,
			RecoveryHitSeconds = 0.25f,
			RecoveryBlockedSeconds = 0.25f,
			RecoveryParriedSeconds = 0.5f,
			ReachForwardMeters = 2.6f,
			ReachLateralMeters = 1.6f,
			StaminaLightCost = 9f,
			StaminaHeavyCost = 16f,
			LateralArcDegrees = 70f,
			ForwardArcTotalDegrees = 110f,
			ForwardArcStartDegrees = 125f
		};

		yield return new MeleeWeaponClassData
		{
			ClassId = "dagger",
			DisplayName = "Dagger",
			WindupSeconds = 0.12f,
			InitiativeWindupSeconds = 0.06f,
			ChargeSeconds = 0.35f,
			ActiveSeconds = 0.07f,
			HeavyActiveSeconds = 0.10f,
			RecoveryMissSeconds = 0.15f,
			RecoveryHitSeconds = 0.1f,
			RecoveryBlockedSeconds = 0.1f,
			RecoveryParriedSeconds = 0.25f,
			ReachForwardMeters = 1.3f,
			ReachLateralMeters = 1.2f,
			StaminaLightCost = 5f,
			StaminaHeavyCost = 9f,
			LateralArcDegrees = 110f,
			ForwardArcTotalDegrees = 120f,
			ForwardArcStartDegrees = 130f
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
