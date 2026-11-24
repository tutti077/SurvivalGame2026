using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// One melee weapon class row (oneHanded / twoHanded / spear / dagger …). Edited in the inspector on
/// <see cref="MeleeWeaponTimingLibrary"/>; <c>data/melee_weapon_classes.json</c> is the fallback for
/// scenes without that component. Directional exchange (Mark 2026-11-14, Last Oasis spec v0.1): every swing is
/// windup → release (the only damaging phase) → recovery. A chained follow-up skips the recovery and winds up in
/// <c>comboWindupSeconds</c> when it changes direction; the third chained swing ends in <c>comboEndRecoverySeconds</c>.
/// Held past the light windup a swing turns heavy (its own windup / release / recovery, ×<c>heavyDamageMultiplier</c>).
/// A blocked or chambered swing stops on contact and recovers for <c>recoveryBlockedSeconds</c> (a heavy for
/// <c>recoveryBlockedHeavySeconds</c>). Hits stagger the victim (<c>hitStaggerSeconds</c> / <c>heavyHitStaggerSeconds</c>),
/// a blocked heavy staggers the blocker (<c>heavyBlockStaggerSeconds</c>), and the blocker of a light gets one counter
/// swing that winds up in <c>counterWindupSeconds</c>. The rules that do not depend on the weapon live on
/// <see cref="PlayerCombat"/> (PlayerCombat.Exchange.cs).
/// </summary>
public sealed class MeleeWeaponClassData
{
	[KeyProperty]
	public string ClassId { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	// --- Light swing: windup → release → recovery ---

	/// <summary>Click → release, for a swing from neutral (and a chained one in the same direction).</summary>
	public float WindupSeconds { get; set; } = 0.2f;

	/// <summary>Windup of a chained follow-up that changes direction (right → left → overhead).</summary>
	public float ComboWindupSeconds { get; set; } = 0.15f;

	/// <summary>Release: the blade sweeps the attack path and deals damage for this long.</summary>
	public float ActiveSeconds { get; set; } = 0.18f;

	/// <summary>Recovery after an unchained swing (hit or miss).</summary>
	public float RecoverySeconds { get; set; } = 0.25f;

	/// <summary>Recovery after the last swing of a full combo (it cannot be chained out of).</summary>
	public float ComboEndRecoverySeconds { get; set; } = 0.4f;

	/// <summary>Recovery after a light swing was blocked or chambered (it stops on contact).</summary>
	public float RecoveryBlockedSeconds { get; set; } = 0.25f;

	// --- Heavy (held past the light windup) ---

	/// <summary>Click → heavy release, at the earliest (the strike comes on the button release).</summary>
	public float HeavyWindupSeconds { get; set; } = 0.35f;

	public float HeavyActiveSeconds { get; set; } = 0.22f;

	public float HeavyRecoverySeconds { get; set; } = 0.4f;

	/// <summary>Recovery after a heavy was blocked — shorter than the blocker's stagger, so the attacker keeps the initiative.</summary>
	public float RecoveryBlockedHeavySeconds { get; set; } = 0.15f;

	/// <summary>Heavy damage = light damage × this.</summary>
	public float HeavyDamageMultiplier { get; set; } = 1.5f;

	// --- What the swing does to the other side ---

	/// <summary>A landed light swing staggers the victim this long (and interrupts its attack).</summary>
	public float HitStaggerSeconds { get; set; } = 0.15f;

	/// <summary>A landed heavy staggers the victim this long (a hard stagger).</summary>
	public float HeavyHitStaggerSeconds { get; set; } = 0.3f;

	/// <summary>A blocked heavy staggers the blocker this long (no attacks; the guard stays up; a hard stagger).</summary>
	public float HeavyBlockStaggerSeconds { get; set; } = 0.25f;

	/// <summary>The first swing after blocking a light (inside the counter window) winds up this fast.</summary>
	public float CounterWindupSeconds { get; set; } = 0.15f;

	// --- Reach and arcs ---

	/// <summary>Blade reach for the overhead/forward attack, in meters (converted once per pawn via BodyHeight/1.8).</summary>
	public float ReachForwardMeters { get; set; } = 1.54f;

	/// <summary>Blade reach for left/right slashes, in meters.</summary>
	public float ReachLateralMeters { get; set; } = 1.54f;

	/// <summary>Stamina spent per light swing.</summary>
	public float StaminaCost { get; set; } = 10f;

	/// <summary>Total stamina a heavy costs (the light cost on the click, the rest when it turns heavy).</summary>
	public float StaminaHeavyCost { get; set; } = 20f;

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
	public float? ComboWindupSeconds { get; set; }
	public float? ActiveSeconds { get; set; }
	public float? RecoverySeconds { get; set; }
	public float? ComboEndRecoverySeconds { get; set; }
	public float? RecoveryBlockedSeconds { get; set; }
	public float? HeavyWindupSeconds { get; set; }
	public float? HeavyActiveSeconds { get; set; }
	public float? HeavyRecoverySeconds { get; set; }
	public float? RecoveryBlockedHeavySeconds { get; set; }
	public float? HeavyDamageMultiplier { get; set; }
	public float? HitStaggerSeconds { get; set; }
	public float? HeavyHitStaggerSeconds { get; set; }
	public float? HeavyBlockStaggerSeconds { get; set; }
	public float? CounterWindupSeconds { get; set; }
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
	public float ComboWindupSeconds { get; init; }
	public float ActiveSeconds { get; init; }
	public float RecoverySeconds { get; init; }
	public float ComboEndRecoverySeconds { get; init; }
	public float RecoveryBlockedSeconds { get; init; }
	public float HeavyWindupSeconds { get; init; }
	public float HeavyActiveSeconds { get; init; }
	public float HeavyRecoverySeconds { get; init; }
	public float RecoveryBlockedHeavySeconds { get; init; }
	public float HeavyDamageMultiplier { get; init; }
	public float HitStaggerSeconds { get; init; }
	public float HeavyHitStaggerSeconds { get; init; }
	public float HeavyBlockStaggerSeconds { get; init; }
	public float CounterWindupSeconds { get; init; }
	public float ReachForwardMeters { get; init; }
	public float ReachLateralMeters { get; init; }
	public float StaminaCost { get; init; }
	public float StaminaHeavyCost { get; init; }
	public float LateralArcDegrees { get; init; }
	public float ForwardArcTotalDegrees { get; init; }
	public float ForwardArcStartDegrees { get; init; }

	/// <summary>Release length for a light or a heavy swing.</summary>
	public float ActiveFor( bool heavy ) => heavy ? HeavyActiveSeconds : ActiveSeconds;
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
		var windup = Math.Max( 0f, overrides?.WindupSeconds ?? data.WindupSeconds );

		return new MeleeWeaponTimings
		{
			ClassId = data.ClassId,
			WindupSeconds = windup,
			ComboWindupSeconds = Math.Max( 0f, overrides?.ComboWindupSeconds ?? data.ComboWindupSeconds ),
			ActiveSeconds = Math.Max( 0.04f, overrides?.ActiveSeconds ?? data.ActiveSeconds ),
			RecoverySeconds = Math.Max( 0f, overrides?.RecoverySeconds ?? data.RecoverySeconds ),
			ComboEndRecoverySeconds = Math.Max( 0f, overrides?.ComboEndRecoverySeconds ?? data.ComboEndRecoverySeconds ),
			RecoveryBlockedSeconds = Math.Max( 0f, overrides?.RecoveryBlockedSeconds ?? data.RecoveryBlockedSeconds ),
			// A heavy can never wind up faster than the light it grows out of.
			HeavyWindupSeconds = Math.Max( windup, overrides?.HeavyWindupSeconds ?? data.HeavyWindupSeconds ),
			HeavyActiveSeconds = Math.Max( 0.04f, overrides?.HeavyActiveSeconds ?? data.HeavyActiveSeconds ),
			HeavyRecoverySeconds = Math.Max( 0f, overrides?.HeavyRecoverySeconds ?? data.HeavyRecoverySeconds ),
			RecoveryBlockedHeavySeconds = Math.Max( 0f, overrides?.RecoveryBlockedHeavySeconds ?? data.RecoveryBlockedHeavySeconds ),
			HeavyDamageMultiplier = Math.Max( 0f, overrides?.HeavyDamageMultiplier ?? data.HeavyDamageMultiplier ),
			HitStaggerSeconds = Math.Max( 0f, overrides?.HitStaggerSeconds ?? data.HitStaggerSeconds ),
			HeavyHitStaggerSeconds = Math.Max( 0f, overrides?.HeavyHitStaggerSeconds ?? data.HeavyHitStaggerSeconds ),
			HeavyBlockStaggerSeconds = Math.Max( 0f, overrides?.HeavyBlockStaggerSeconds ?? data.HeavyBlockStaggerSeconds ),
			CounterWindupSeconds = Math.Max( 0f, overrides?.CounterWindupSeconds ?? data.CounterWindupSeconds ),
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
			ComboWindupSeconds = 0.5f,
			ActiveSeconds = 0.2f,
			RecoverySeconds = 0.5f,
			ComboEndRecoverySeconds = 0.75f,
			RecoveryBlockedSeconds = 0.2f,
			HeavyWindupSeconds = 0.9f,
			HeavyActiveSeconds = 0.25f,
			HeavyRecoverySeconds = 0.75f,
			RecoveryBlockedHeavySeconds = 0.4f,
			HeavyDamageMultiplier = 1.5f,
			HitStaggerSeconds = 0.4f,
			HeavyHitStaggerSeconds = 0.75f,
			HeavyBlockStaggerSeconds = 0.6f,
			CounterWindupSeconds = 0.5f,
			ReachForwardMeters = 1.54f,
			ReachLateralMeters = 1.54f,
			StaminaCost = 10f,
			StaminaHeavyCost = 20f,
			LateralArcDegrees = 150f,
			ForwardArcTotalDegrees = 158f,
			ForwardArcStartDegrees = 146f,
		};

		yield return new MeleeWeaponClassData
		{
			ClassId = "twoHanded",
			DisplayName = "Two-Handed",
			WindupSeconds = 0.9f,
			ComboWindupSeconds = 0.75f,
			ActiveSeconds = 0.25f,
			RecoverySeconds = 0.7f,
			ComboEndRecoverySeconds = 0.95f,
			RecoveryBlockedSeconds = 0.3f,
			HeavyWindupSeconds = 1.3f,
			HeavyActiveSeconds = 0.3f,
			HeavyRecoverySeconds = 0.95f,
			RecoveryBlockedHeavySeconds = 0.55f,
			HeavyDamageMultiplier = 1.5f,
			HitStaggerSeconds = 0.55f,
			HeavyHitStaggerSeconds = 0.85f,
			HeavyBlockStaggerSeconds = 0.7f,
			CounterWindupSeconds = 0.75f,
			ReachForwardMeters = 1.78f,
			ReachLateralMeters = 1.78f,
			StaminaCost = 14f,
			StaminaHeavyCost = 28f,
			LateralArcDegrees = 170f,
			ForwardArcTotalDegrees = 165f,
			ForwardArcStartDegrees = 150f,
		};

		yield return new MeleeWeaponClassData
		{
			ClassId = "spear",
			DisplayName = "Spear",
			WindupSeconds = 0.8f,
			ComboWindupSeconds = 0.65f,
			ActiveSeconds = 0.2f,
			RecoverySeconds = 0.6f,
			ComboEndRecoverySeconds = 0.85f,
			RecoveryBlockedSeconds = 0.25f,
			HeavyWindupSeconds = 1.15f,
			HeavyActiveSeconds = 0.25f,
			HeavyRecoverySeconds = 0.85f,
			RecoveryBlockedHeavySeconds = 0.5f,
			HeavyDamageMultiplier = 1.5f,
			HitStaggerSeconds = 0.4f,
			HeavyHitStaggerSeconds = 0.75f,
			HeavyBlockStaggerSeconds = 0.6f,
			CounterWindupSeconds = 0.65f,
			ReachForwardMeters = 2.11f,
			ReachLateralMeters = 1.94f,
			StaminaCost = 12f,
			StaminaHeavyCost = 24f,
			LateralArcDegrees = 34f,
			ForwardArcTotalDegrees = 110f,
			ForwardArcStartDegrees = 125f,
		};

		yield return new MeleeWeaponClassData
		{
			ClassId = "trainingDummy",
			DisplayName = "Training Dummy",
			WindupSeconds = 0.6f,
			ComboWindupSeconds = 0.5f,
			ActiveSeconds = 0.2f,
			RecoverySeconds = 0.5f,
			ComboEndRecoverySeconds = 0.75f,
			RecoveryBlockedSeconds = 0.2f,
			HeavyWindupSeconds = 0.9f,
			HeavyActiveSeconds = 0.25f,
			HeavyRecoverySeconds = 0.75f,
			RecoveryBlockedHeavySeconds = 0.4f,
			HeavyDamageMultiplier = 1.5f,
			HitStaggerSeconds = 0.4f,
			HeavyHitStaggerSeconds = 0.75f,
			HeavyBlockStaggerSeconds = 0.6f,
			CounterWindupSeconds = 0.5f,
			ReachForwardMeters = 1.71f,
			ReachLateralMeters = 1.71f,
			StaminaCost = 10f,
			StaminaHeavyCost = 20f,
			LateralArcDegrees = 150f,
			ForwardArcTotalDegrees = 168f,
			ForwardArcStartDegrees = 118f,
		};

		yield return new MeleeWeaponClassData
		{
			ClassId = "dagger",
			DisplayName = "Dagger",
			WindupSeconds = 0.45f,
			ComboWindupSeconds = 0.35f,
			ActiveSeconds = 0.15f,
			RecoverySeconds = 0.35f,
			ComboEndRecoverySeconds = 0.55f,
			RecoveryBlockedSeconds = 0.15f,
			HeavyWindupSeconds = 0.7f,
			HeavyActiveSeconds = 0.15f,
			HeavyRecoverySeconds = 0.55f,
			RecoveryBlockedHeavySeconds = 0.3f,
			HeavyDamageMultiplier = 1.5f,
			HitStaggerSeconds = 0.3f,
			HeavyHitStaggerSeconds = 0.65f,
			HeavyBlockStaggerSeconds = 0.5f,
			CounterWindupSeconds = 0.35f,
			ReachForwardMeters = 1.05f,
			ReachLateralMeters = 0.97f,
			StaminaCost = 7f,
			StaminaHeavyCost = 14f,
			LateralArcDegrees = 110f,
			ForwardArcTotalDegrees = 120f,
			ForwardArcStartDegrees = 130f,
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
