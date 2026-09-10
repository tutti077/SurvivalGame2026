using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// One buff / debuff definition from <c>data/status_effects.json</c>. Every modifier is a stat
/// change the pawn feels while the effect is active; a zero field means "no change".
/// </summary>
public sealed class StatusEffectData
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	/// <summary>Tooltip flavour line under the name.</summary>
	public string Description { get; set; } = string.Empty;
	/// <summary>Project-relative icon (e.g. <c>ui/status/rested.png</c>).</summary>
	public string Icon { get; set; } = string.Empty;
	/// <summary>Slot tint behind the icon, "r,g,b[,a]" in 0–1.</summary>
	public string FallbackColor { get; set; } = "0.5,0.5,0.5,1";
	/// <summary>True = buff (green-ish framing), false = debuff (red framing).</summary>
	public bool IsBuff { get; set; } = true;

	/// <summary>Percent change to max health (20 = +20%).</summary>
	public float MaxHealthPercent { get; set; }
	/// <summary>Percent change to max stamina.</summary>
	public float MaxStaminaPercent { get; set; }
	/// <summary>Percent change to how long eaten food lasts (20 = food buffs last 20% longer).</summary>
	public float FoodDurationPercent { get; set; }
	/// <summary>Health per second while active (negative = damage over time).</summary>
	public float HealthPerSecond { get; set; }
	/// <summary>Stamina per second while active.</summary>
	public float StaminaPerSecond { get; set; }

	/// <summary>Human-readable modifier lines for the HUD tooltip.</summary>
	public IEnumerable<string> DescribeModifiers()
	{
		if ( MaxHealthPercent != 0f )
			yield return $"{Signed( MaxHealthPercent )}% max health";
		if ( MaxStaminaPercent != 0f )
			yield return $"{Signed( MaxStaminaPercent )}% max stamina";
		if ( FoodDurationPercent != 0f )
			yield return $"Food lasts {Signed( FoodDurationPercent )}% longer";
		if ( HealthPerSecond != 0f )
			yield return $"{Signed( HealthPerSecond )} HP/s";
		if ( StaminaPerSecond != 0f )
			yield return $"{Signed( StaminaPerSecond )} stamina/s";
	}

	static string Signed( float value ) => value > 0f ? $"+{value:0.#}" : $"{value:0.#}";
}

/// <summary>Loads <c>data/status_effects.json</c>. Adding a buff or debuff is a JSON edit.</summary>
public static class StatusEffectCatalog
{
	const string FilePath = "data/status_effects.json";

	/// <summary>Comfort / shelter buff id (see <see cref="PlayerVitals"/> comfort partial).</summary>
	public const string RestedId = "rested";

	static readonly List<StatusEffectData> Effects = new();
	static readonly Dictionary<string, StatusEffectData> ById = new( StringComparer.OrdinalIgnoreCase );
	static bool _loaded;

	public static IReadOnlyList<StatusEffectData> All
	{
		get
		{
			EnsureLoaded();
			return Effects;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		Effects.Clear();
		ById.Clear();

		if ( !TryLoadFromFile() )
			Log.Warning( "[StatusEffectCatalog] No status_effects.json — buffs / debuffs unavailable." );
	}

	public static bool TryGet( string id, out StatusEffectData effect )
	{
		EnsureLoaded();
		effect = null;
		return !string.IsNullOrWhiteSpace( id ) && ById.TryGetValue( id.Trim(), out effect );
	}

	public static Color ResolveFallbackColor( StatusEffectData effect )
	{
		var fallback = new Color( 0.5f, 0.5f, 0.5f );
		if ( effect is null || string.IsNullOrWhiteSpace( effect.FallbackColor ) )
			return fallback;

		var parts = effect.FallbackColor.Split( ',' );
		if ( parts.Length < 3 )
			return fallback;

		if ( !float.TryParse( parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r )
		     || !float.TryParse( parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var g )
		     || !float.TryParse( parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b ) )
			return fallback;

		var a = 1f;
		if ( parts.Length >= 4 )
			float.TryParse( parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a );

		return new Color( r, g, b, a );
	}

	static bool TryLoadFromFile()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( FilePath ) )
				return false;

			var json = FileSystem.Mounted.ReadAllText( FilePath );
			var file = JsonSerializer.Deserialize<StatusEffectsFile>( json, JsonOptions );
			if ( file?.Effects is null || file.Effects.Count == 0 )
				return false;

			for ( var i = 0; i < file.Effects.Count; i++ )
			{
				var entry = file.Effects[i];
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Id ) )
					continue;

				entry.Id = entry.Id.Trim();
				Effects.Add( entry );
				ById[entry.Id] = entry;
			}

			return Effects.Count > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[StatusEffectCatalog] Failed to load {FilePath}: {ex.Message}" );
			return false;
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class StatusEffectsFile
	{
		public List<StatusEffectData> Effects { get; set; } = new();
	}
}
