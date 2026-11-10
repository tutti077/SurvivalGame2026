using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sandbox;

namespace Survival;

/// <summary>
/// Overhead nameplate tuning from <c>data/nameplates.json</c> (Mark 2026-11-04, Valheim-style overhead UI: "Make the
/// following configurable"). Sizes are UI pixels at the HUD's 1080p reference, distances are camera-to-entity meters,
/// times are seconds. Loaded once (<see cref="EnsureLoaded"/>); <c>nameplates_reload</c> re-reads the file.
/// </summary>
public sealed class NameplateTuning
{
	const string FilePath = "data/nameplates.json";

	[JsonPropertyName( "plateWidthPixels" )] public float PlateWidthPixels { get; set; } = 160f;
	[JsonPropertyName( "fontSizePixels" )] public float FontSizePixels { get; set; } = 15f;
	[JsonPropertyName( "barWidthPixels" )] public float BarWidthPixels { get; set; } = 84f;
	[JsonPropertyName( "barHeightPixels" )] public float BarHeightPixels { get; set; } = 3f;
	[JsonPropertyName( "liftPixels" )] public float LiftPixels { get; set; } = 4f;
	[JsonPropertyName( "scaleCurve" )] public List<float[]> ScaleCurve { get; set; } = new()
	{
		new[] { 3f, 1f }, new[] { 10f, 0.9f }, new[] { 20f, 0.75f }, new[] { 30f, 0.6f }, new[] { 40f, 0.5f }
	};
	[JsonPropertyName( "minScale" )] public float MinScale { get; set; } = 0.5f;
	[JsonPropertyName( "fadeStartMeters" )] public float FadeStartMeters { get; set; } = 30f;
	[JsonPropertyName( "fadeEndMeters" )] public float FadeEndMeters { get; set; } = 40f;
	[JsonPropertyName( "fadeSeconds" )] public float FadeSeconds { get; set; } = 0.2f;
	[JsonPropertyName( "combatVisibleSeconds" )] public float CombatVisibleSeconds { get; set; } = 1.5f;
	[JsonPropertyName( "targetHoldSeconds" )] public float TargetHoldSeconds { get; set; } = 0.3f;
	[JsonPropertyName( "targetingMeters" )] public float TargetingMeters { get; set; } = 30f;
	[JsonPropertyName( "playerNameMeters" )] public float PlayerNameMeters { get; set; } = 20f;
	[JsonPropertyName( "hostileColor" )] public string HostileColor { get; set; } = "#d8261e";
	[JsonPropertyName( "neutralColor" )] public string NeutralColor { get; set; } = "#e0b21d";
	[JsonPropertyName( "friendlyColor" )] public string FriendlyColor { get; set; } = "#46c246";

	static NameplateTuning _current;

	public static NameplateTuning Current
	{
		get
		{
			EnsureLoaded();
			return _current;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _current is not null )
			return;

		_current = Load() ?? new NameplateTuning();
	}

	public static void ForceReload()
	{
		_current = null;
		EnsureLoaded();
	}

	[ConCmd( "nameplates_reload", Help = "Re-read data/nameplates.json (overhead nameplate sizes, distances, fades)." )]
	public static void ReloadCommand()
	{
		ForceReload();
		Log.Info( "[Nameplates] reloaded data/nameplates.json" );
	}

	/// <summary>UI scale at <paramref name="meters"/> from the camera: linear between the curve points, clamped to <see cref="MinScale"/>.</summary>
	public float ScaleAt( float meters )
	{
		var curve = ScaleCurve;
		if ( curve is null || curve.Count == 0 )
			return 1f;

		if ( meters <= curve[0][0] )
			return Math.Max( MinScale, curve[0][1] );

		for ( var i = 1; i < curve.Count; i++ )
		{
			var a = curve[i - 1];
			var b = curve[i];
			if ( meters > b[0] )
				continue;

			var t = (meters - a[0]) / Math.Max( 1e-3f, b[0] - a[0] );
			return Math.Max( MinScale, a[1] + (b[1] - a[1]) * t );
		}

		return Math.Max( MinScale, curve[^1][1] );
	}

	/// <summary>Distance opacity: 1 until <see cref="FadeStartMeters"/>, 0 at <see cref="FadeEndMeters"/>.</summary>
	public float DistanceAlphaAt( float meters )
	{
		if ( meters <= FadeStartMeters )
			return 1f;
		if ( meters >= FadeEndMeters )
			return 0f;
		return 1f - (meters - FadeStartMeters) / Math.Max( 1e-3f, FadeEndMeters - FadeStartMeters );
	}

	static NameplateTuning Load()
	{
		foreach ( var path in new[] { FilePath, "Assets/" + FilePath } )
		{
			try
			{
				if ( !FileSystem.Mounted.FileExists( path ) )
					continue;

				var json = FileSystem.Mounted.ReadAllText( path );
				if ( string.IsNullOrWhiteSpace( json ) )
					continue;

				var tuning = JsonSerializer.Deserialize<NameplateTuning>( json );
				if ( tuning is not null )
					return tuning;
			}
			catch ( Exception e )
			{
				Log.Warning( $"[Nameplates] could not read {path}: {e.Message}" );
			}
		}

		Log.Warning( "[Nameplates] data/nameplates.json not found — using built-in defaults." );
		return null;
	}
}
