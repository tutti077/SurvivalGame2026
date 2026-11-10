using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// One melee weapon class row (oneHanded / twoHanded / spear / dagger …). Edited in the inspector on
/// <see cref="MeleeWeaponTimingLibrary"/>; <c>data/melee_weapon_classes.json</c> is the fallback for
/// scenes without that component. Attack pattern (Mark 2026-11-04): click → windup (cocked, hanging) → active
/// (damage swoop) → the next attack may start at once; only a blocked / parried swing locks the attacker out.
/// Holding the button past the windup turns the swing heavy: it hangs until <c>heavyWindupSeconds</c>, hits for
/// <c>heavyDamageMultiplier</c>, costs <c>staminaHeavyCost</c>, breaks a guard that blocks it and is punished harder
/// when parried (Mark 2026-11-04).
/// </summary>
public sealed class MeleeWeaponClassData
{
	[KeyProperty]
	public string ClassId { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	// --- Combat timings (click → windup → active; no recovery after a miss or a hit) ---

	/// <summary>Click → damage starts. The sword snaps to its cocked pose on the click and hangs there this long.</summary>
	public float WindupSeconds { get; set; } = 0.6f;

	/// <summary>Damage swoop: the blade sweeps the attack path and deals damage for this long.</summary>
	public float ActiveSeconds { get; set; } = 0.2f;

	/// <summary>Attacker lockout after the swing was blocked (no attack / block until it ends).</summary>
	public float RecoveryBlockedSeconds { get; set; } = 0.1f;

	/// <summary>Attacker lockout after the swing was perfect-parried.</summary>
	public float RecoveryParriedSeconds { get; set; } = 0.2f;

	/// <summary>Click → damage for a heavy (button still held when the light windup ends; the cocked pose keeps hanging).</summary>
	public float HeavyWindupSeconds { get; set; } = 0.8f;

	/// <summary>Heavy damage = light damage × this.</summary>
	public float HeavyDamageMultiplier { get; set; } = 2f;

	/// <summary>Attacker lockout after a heavy was perfect-parried.</summary>
	public float RecoveryParriedHeavySeconds { get; set; } = 0.3f;

	/// <summary>A blocked heavy breaks the guard: the defender can neither block nor attack for this long.</summary>
	public float HeavyGuardBreakSeconds { get; set; } = 0.4f;

	/// <summary>Blade reach for the overhead/forward attack, in meters (converted once per pawn via BodyHeight/1.8).</summary>
	public float ReachForwardMeters { get; set; } = 1.54f;

	/// <summary>Blade reach for left/right slashes, in meters.</summary>
	public float ReachLateralMeters { get; set; } = 1.54f;

	/// <summary>Stamina spent per swing.</summary>
	public float StaminaCost { get; set; } = 8f;

	/// <summary>Total stamina a heavy costs (the light cost on the click, the rest when it turns heavy).</summary>
	public float StaminaHeavyCost { get; set; } = 16f;

	/// <summary>Total horizontal span (°) of left/right slashes — narrow for a spear jab, wide for a two-hander.</summary>
	public float LateralArcDegrees { get; set; } = 150f;

	/// <summary>Overhead/forward arc span (°); end = start − total.</summary>
	public float ForwardArcTotalDegrees { get; set; } = 158f;

	/// <summary>Start angle on the vertical overhead arc (0° = forward, 90° = up). Higher = more raised at windup.</summary>
	public float ForwardArcStartDegrees { get; set; } = 146f;

	/// <summary>
	/// Spear: the "up" cursor attack is a straight forward thrust (the stab pattern at light timings) instead of an
	/// overhead swing; left / right stay the narrow lateral swoops. Read from the JSON row as well as the inspector
	/// library (a library row saved before the flag existed keeps the JSON value).
	/// </summary>
	public bool ForwardAttackIsThrust { get; set; }

	// --- Special attack (Q on the ground, static combo mode only; Mark 2026-11-06) ---

	/// <summary>
	/// The weapon's Q special: <c>"stab"</c> (sword / club: a straight jab that winds up for
	/// <see cref="SpecialWindupSeconds"/> and hits for <see cref="SpecialDamageMultiplier"/>) or empty for none. Read from
	/// the JSON row as well as the inspector library (a library row saved before the field existed keeps the JSON value).
	/// </summary>
	public string SpecialAttack { get; set; } = string.Empty;

	/// <summary>Q → the special's damage starts (the cocked stab hangs this long). Mark 2026-11-06: 1.2 s.</summary>
	public float SpecialWindupSeconds { get; set; } = 1.2f;

	/// <summary>Special damage = light damage × this. Mark 2026-11-06: 2.</summary>
	public float SpecialDamageMultiplier { get; set; } = 2f;
}

/// <summary>Optional per-weapon overrides in <c>equipment_profiles.json</c> — only set fields replace the class value.</summary>
public sealed class MeleeTimingOverridesData
{
	public float? WindupSeconds { get; set; }
	public float? ActiveSeconds { get; set; }
	public float? RecoveryBlockedSeconds { get; set; }
	public float? RecoveryParriedSeconds { get; set; }
	public float? HeavyWindupSeconds { get; set; }
	public float? HeavyDamageMultiplier { get; set; }
	public float? RecoveryParriedHeavySeconds { get; set; }
	public float? HeavyGuardBreakSeconds { get; set; }
	public float? ReachForwardMeters { get; set; }
	public float? ReachLateralMeters { get; set; }
	public float? StaminaCost { get; set; }
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
	public float ActiveSeconds { get; init; }
	public float RecoveryBlockedSeconds { get; init; }
	public float RecoveryParriedSeconds { get; init; }
	public float HeavyWindupSeconds { get; init; }
	public float HeavyDamageMultiplier { get; init; }
	public float RecoveryParriedHeavySeconds { get; init; }
	public float HeavyGuardBreakSeconds { get; init; }
	public float ReachForwardMeters { get; init; }
	public float ReachLateralMeters { get; init; }
	public float StaminaCost { get; init; }
	public float StaminaHeavyCost { get; init; }
	public float LateralArcDegrees { get; init; }
	public float ForwardArcTotalDegrees { get; init; }
	public float ForwardArcStartDegrees { get; init; }
	/// <summary>Up-cursor attack is a forward thrust (spear).</summary>
	public bool ForwardAttackIsThrust { get; init; }
	/// <summary>Q special id (<see cref="MeleeWeaponClassData.SpecialAttack"/>), empty = none.</summary>
	public string SpecialAttack { get; init; }
	public float SpecialWindupSeconds { get; init; }
	public float SpecialDamageMultiplier { get; init; }
	/// <summary>Q on the ground is a stab (sword / club).</summary>
	public bool HasStabSpecial => string.Equals( SpecialAttack, MeleeSpecialAttacks.Stab, StringComparison.OrdinalIgnoreCase );
}

/// <summary>Ids for <see cref="MeleeWeaponClassData.SpecialAttack"/>.</summary>
public static class MeleeSpecialAttacks
{
	public const string Stab = "stab";
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
	// Same throttled change check as EquipmentCatalog: read + hash the JSON at most every JsonChangeCheckSeconds, never per call
	// (PlayerCombat calls EnsureLoaded every frame on the JSON-file path).
	const float JsonChangeCheckSeconds = 2f;
	static float _lastJsonChangeCheckTime = float.NegativeInfinity;

	/// <summary>Bumped on every reload so cached resolved timings can invalidate.</summary>
	public static int Version { get; private set; }

	public static void EnsureLoaded()
	{
		if ( _loaded && RealTime.Now - _lastJsonChangeCheckTime < JsonChangeCheckSeconds )
			return;

		_lastJsonChangeCheckTime = RealTime.Now;
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

		// Semantic flags live in the JSON row too: an inspector row saved before the flag existed holds false.
		EnsureLoaded();
		ByClassId.TryGetValue( data.ClassId ?? string.Empty, out var jsonRow );
		var forwardIsThrust = data.ForwardAttackIsThrust || jsonRow is { ForwardAttackIsThrust: true };
		var special = !string.IsNullOrWhiteSpace( data.SpecialAttack ) ? data.SpecialAttack.Trim() : jsonRow?.SpecialAttack?.Trim() ?? string.Empty;

		return new MeleeWeaponTimings
		{
			ClassId = data.ClassId,
			ForwardAttackIsThrust = forwardIsThrust,
			SpecialAttack = special,
			SpecialWindupSeconds = Math.Max( 0f, data.SpecialWindupSeconds ),
			SpecialDamageMultiplier = Math.Max( 0f, data.SpecialDamageMultiplier ),
			WindupSeconds = Math.Max( 0f, overrides?.WindupSeconds ?? data.WindupSeconds ),
			ActiveSeconds = Math.Max( 0.04f, overrides?.ActiveSeconds ?? data.ActiveSeconds ),
			RecoveryBlockedSeconds = Math.Max( 0f, overrides?.RecoveryBlockedSeconds ?? data.RecoveryBlockedSeconds ),
			RecoveryParriedSeconds = Math.Max( 0f, overrides?.RecoveryParriedSeconds ?? data.RecoveryParriedSeconds ),
			// A heavy can never wind up faster than the light it grows out of.
			HeavyWindupSeconds = Math.Max( Math.Max( 0f, overrides?.WindupSeconds ?? data.WindupSeconds ), overrides?.HeavyWindupSeconds ?? data.HeavyWindupSeconds ),
			HeavyDamageMultiplier = Math.Max( 0f, overrides?.HeavyDamageMultiplier ?? data.HeavyDamageMultiplier ),
			RecoveryParriedHeavySeconds = Math.Max( 0f, overrides?.RecoveryParriedHeavySeconds ?? data.RecoveryParriedHeavySeconds ),
			HeavyGuardBreakSeconds = Math.Max( 0f, overrides?.HeavyGuardBreakSeconds ?? data.HeavyGuardBreakSeconds ),
			ReachForwardMeters = Math.Max( 0.3f, overrides?.ReachForwardMeters ?? data.ReachForwardMeters ),
			ReachLateralMeters = Math.Max( 0.3f, overrides?.ReachLateralMeters ?? data.ReachLateralMeters ),
			StaminaCost = Math.Max( 0f, overrides?.StaminaCost ?? data.StaminaCost ),
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
			WindupSeconds = 0.6f,
			ActiveSeconds = 0.2f,
			RecoveryBlockedSeconds = 0.1f,
			RecoveryParriedSeconds = 0.2f,
			ReachForwardMeters = 1.54f,
			ReachLateralMeters = 1.54f,
			StaminaCost = 8f,
			StaminaHeavyCost = 16f,
			HeavyWindupSeconds = 0.8f,
			HeavyDamageMultiplier = 2f,
			RecoveryParriedHeavySeconds = 0.3f,
			HeavyGuardBreakSeconds = 0.4f,
			SpecialAttack = MeleeSpecialAttacks.Stab,
		};

		yield return new MeleeWeaponClassData
		{
			ClassId = "twoHanded",
			DisplayName = "Two-Handed",
			WindupSeconds = 0.32f,
			ActiveSeconds = 0.14f,
			RecoveryBlockedSeconds = 0.3f,
			RecoveryParriedSeconds = 0.6f,
			ReachForwardMeters = 1.78f,
			ReachLateralMeters = 1.78f,
			StaminaCost = 12f,
			StaminaHeavyCost = 24f,
			HeavyWindupSeconds = 0.52f,
			HeavyDamageMultiplier = 2f,
			RecoveryParriedHeavySeconds = 0.7f,
			HeavyGuardBreakSeconds = 0.4f,
			LateralArcDegrees = 170f,
			ForwardArcTotalDegrees = 165f,
			ForwardArcStartDegrees = 150f
		};

		yield return new MeleeWeaponClassData
		{
			ClassId = "spear",
			DisplayName = "Spear",
			WindupSeconds = 0.26f,
			ActiveSeconds = 0.12f,
			RecoveryBlockedSeconds = 0.25f,
			RecoveryParriedSeconds = 0.5f,
			ReachForwardMeters = 2.11f,
			ReachLateralMeters = 1.30f,
			StaminaCost = 9f,
			StaminaHeavyCost = 18f,
			HeavyWindupSeconds = 0.46f,
			HeavyDamageMultiplier = 2f,
			RecoveryParriedHeavySeconds = 0.6f,
			HeavyGuardBreakSeconds = 0.4f,
			LateralArcDegrees = 70f,
			ForwardArcTotalDegrees = 110f,
			ForwardArcStartDegrees = 125f
		};

		yield return new MeleeWeaponClassData
		{
			ClassId = "dagger",
			DisplayName = "Dagger",
			WindupSeconds = 0.12f,
			ActiveSeconds = 0.07f,
			RecoveryBlockedSeconds = 0.1f,
			RecoveryParriedSeconds = 0.25f,
			ReachForwardMeters = 1.05f,
			ReachLateralMeters = 0.97f,
			StaminaCost = 5f,
			StaminaHeavyCost = 10f,
			HeavyWindupSeconds = 0.32f,
			HeavyDamageMultiplier = 2f,
			RecoveryParriedHeavySeconds = 0.35f,
			HeavyGuardBreakSeconds = 0.4f,
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
