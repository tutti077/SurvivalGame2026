using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>One time-of-day look inside a <see cref="SkyPresetData"/>. Colours are "r,g,b[,a]".</summary>
public sealed class SkyKeyframeData
{
	public string Zenith { get; set; } = "0.24,0.46,0.86";
	public string Horizon { get; set; } = "0.68,0.80,0.95";
	/// <summary>"r,g,b,strength" — sun halo and the warm horizon band on the sun's side.</summary>
	public string SunGlow { get; set; } = "1,0.92,0.75,0.45";
	public string CloudLit { get; set; } = "1,1,1";
	public string CloudShade { get; set; } = "0.62,0.68,0.80";
	/// <summary>0 = clear, 1 = solid deck.</summary>
	public float CloudCover { get; set; } = 0.38f;
	/// <summary>Multiplier on the sun's light colour (overcast dims the sun).</summary>
	public float SunLight { get; set; } = 1f;
}

/// <summary>One weather look from <c>data/sky_presets.json</c>: five keyframes blended through the day.</summary>
public sealed class SkyPresetData
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	/// <summary>Cloud projection size — bigger = smaller clouds.</summary>
	public float CloudScale { get; set; } = 0.35f;
	/// <summary>Multiplier on the wind-driven cloud scroll.</summary>
	public float CloudSpeed { get; set; } = 1f;
	public SkyKeyframeData Night { get; set; } = new();
	public SkyKeyframeData Dawn { get; set; } = new();
	public SkyKeyframeData Day { get; set; } = new();
	public SkyKeyframeData Noon { get; set; } = new();
	public SkyKeyframeData Dusk { get; set; } = new();
}

/// <summary>A resolved, blendable keyframe (parsed colours) — what <see cref="SkyDome"/> renders.</summary>
public struct SkyLook
{
	public Color Zenith;
	public Color Horizon;
	public Color SunGlow;
	public Color CloudLit;
	public Color CloudShade;
	public float CloudCover;
	public float SunLight;

	public static SkyLook From( SkyKeyframeData k ) => new()
	{
		Zenith = SkyPresetCatalog.ParseColor( k?.Zenith, new Color( 0.24f, 0.46f, 0.86f ) ),
		Horizon = SkyPresetCatalog.ParseColor( k?.Horizon, new Color( 0.68f, 0.80f, 0.95f ) ),
		SunGlow = SkyPresetCatalog.ParseColor( k?.SunGlow, new Color( 1f, 0.92f, 0.75f, 0.45f ) ),
		CloudLit = SkyPresetCatalog.ParseColor( k?.CloudLit, Color.White ),
		CloudShade = SkyPresetCatalog.ParseColor( k?.CloudShade, new Color( 0.62f, 0.68f, 0.80f ) ),
		CloudCover = Math.Clamp( k?.CloudCover ?? 0.38f, 0f, 1f ),
		SunLight = Math.Max( 0f, k?.SunLight ?? 1f ),
	};

	public static SkyLook Lerp( in SkyLook a, in SkyLook b, float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return new SkyLook
		{
			Zenith = Color.Lerp( a.Zenith, b.Zenith, t ),
			Horizon = Color.Lerp( a.Horizon, b.Horizon, t ),
			SunGlow = Color.Lerp( a.SunGlow, b.SunGlow, t ),
			CloudLit = Color.Lerp( a.CloudLit, b.CloudLit, t ),
			CloudShade = Color.Lerp( a.CloudShade, b.CloudShade, t ),
			CloudCover = MathX.Lerp( a.CloudCover, b.CloudCover, t ),
			SunLight = MathX.Lerp( a.SunLight, b.SunLight, t ),
		};
	}
}

/// <summary>Loads <c>data/sky_presets.json</c>. Adding a weather look is a JSON edit.</summary>
public static class SkyPresetCatalog
{
	const string FilePath = "data/sky_presets.json";
	public const string DefaultId = "clear";

	static readonly List<SkyPresetData> Presets = new();
	static readonly Dictionary<string, SkyPresetData> ById = new( StringComparer.OrdinalIgnoreCase );
	static bool _loaded;

	public static IReadOnlyList<SkyPresetData> All
	{
		get
		{
			EnsureLoaded();
			return Presets;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		Presets.Clear();
		ById.Clear();

		if ( !TryLoadFromFile() )
			Log.Warning( "[SkyPresetCatalog] No sky_presets.json — sky uses built-in defaults." );
	}

	/// <summary>Preset by id, else the default id, else built-in defaults (never null).</summary>
	public static SkyPresetData Resolve( string id )
	{
		EnsureLoaded();
		if ( !string.IsNullOrWhiteSpace( id ) && ById.TryGetValue( id.Trim(), out var preset ) )
			return preset;
		if ( ById.TryGetValue( DefaultId, out preset ) )
			return preset;
		return Fallback;
	}

	static readonly SkyPresetData Fallback = new() { Id = DefaultId, DisplayName = "Clear" };

	public static Color ParseColor( string text, Color fallback )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return fallback;

		var parts = text.Split( ',' );
		if ( parts.Length < 3
		     || !float.TryParse( parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r )
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
			var file = JsonSerializer.Deserialize<SkyPresetsFile>( json, JsonOptions );
			if ( file?.Presets is null || file.Presets.Count == 0 )
				return false;

			foreach ( var entry in file.Presets )
			{
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Id ) )
					continue;

				entry.Id = entry.Id.Trim();
				Presets.Add( entry );
				ById[entry.Id] = entry;
			}

			return Presets.Count > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SkyPresetCatalog] Failed to load {FilePath}: {ex.Message}" );
			return false;
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class SkyPresetsFile
	{
		public List<SkyPresetData> Presets { get; set; } = new();
	}
}
