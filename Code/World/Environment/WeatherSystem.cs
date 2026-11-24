using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>One weather state from <c>data/weather.json</c>.</summary>
public sealed class WeatherStateData
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	/// <summary>A look from <c>sky_presets.json</c>.</summary>
	public string SkyPresetId { get; set; } = SkyPresetCatalog.DefaultId;
	/// <summary>The steady wind 0–1 (<see cref="WindSystem.BaseStrength01"/>): the sea's swell and the grass orbs follow it.</summary>
	public float WindBase { get; set; } = 0.25f;
	/// <summary>Peak of the gusts on top (<see cref="WindSystem.GustStrength01"/>).</summary>
	public float GustStrength { get; set; } = 0.8f;
	/// <summary>How often it is picked next, relative to the others; 0 = only by the console.</summary>
	public float Weight { get; set; } = 1f;
	public float MinMinutes { get; set; } = 5f;
	public float MaxMinutes { get; set; } = 10f;
}

/// <summary>The weather states (<c>Assets/data/weather.json</c>).</summary>
public static class WeatherCatalog
{
	const string FilePath = "data/weather.json";
	public const string DefaultId = "clear";

	static readonly List<WeatherStateData> States = new();
	static bool _loaded;

	/// <summary>Seconds the wind takes to reach a new state's strength.</summary>
	public static float TransitionSeconds { get; private set; } = 60f;

	public static IReadOnlyList<WeatherStateData> All
	{
		get
		{
			EnsureLoaded();
			return States;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;
		_loaded = true;
		States.Clear();
		try
		{
			if ( FileSystem.Mounted.FileExists( FilePath ) )
			{
				var file = JsonSerializer.Deserialize<WeatherFile>( FileSystem.Mounted.ReadAllText( FilePath ), JsonOptions );
				TransitionSeconds = Math.Max( 0f, file?.TransitionSeconds ?? 60f );
				foreach ( var state in file?.States ?? new() )
				{
					if ( state is null || string.IsNullOrWhiteSpace( state.Id ) )
						continue;
					state.Id = state.Id.Trim();
					States.Add( state );
				}
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[WeatherCatalog] Failed to load {FilePath}: {ex.Message}" );
		}
		if ( States.Count == 0 )
		{
			Log.Warning( "[WeatherCatalog] No weather.json — the weather stays clear." );
			States.Add( new WeatherStateData { Id = DefaultId, DisplayName = "Clear" } );
		}
	}

	public static void Reload()
	{
		_loaded = false;
		EnsureLoaded();
	}

	/// <summary>State by id, else the default id, else the first one (never null).</summary>
	public static WeatherStateData Resolve( string id )
	{
		EnsureLoaded();
		WeatherStateData fallback = null;
		foreach ( var state in States )
		{
			if ( string.Equals( state.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase ) )
				return state;
			if ( string.Equals( state.Id, DefaultId, StringComparison.OrdinalIgnoreCase ) )
				fallback = state;
		}
		return fallback ?? States[0];
	}

	public static bool Has( string id )
	{
		EnsureLoaded();
		foreach ( var state in States )
		{
			if ( string.Equals( state.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase ) )
				return true;
		}
		return false;
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class WeatherFile
	{
		public float TransitionSeconds { get; set; } = 60f;
		public List<WeatherStateData> States { get; set; } = new();
	}
}

/// <summary>
/// The weather (Mark 2026-11-18: "clean up the wave and weather system so a thunderstorm would cause massive waves on the
/// ocean"). Until now "weather" was two unconnected knobs — the wind's base strength (console only) and a sky preset fixed
/// per scene — so the sea never left its everyday swell. This component owns both: the host holds one
/// <see cref="WeatherStateData"/> at a time (synced by id), moves to the next by weight when its time is up, and every peer
/// applies the state it is told: the wind's base and gust strength ease to the state's over the catalog's transition time
/// (host; the wind syncs its own values) and the day / night cycle cross-fades to the state's sky preset. Nothing else is
/// driven from here on purpose: the sea already follows the wind (<c>TerrainWorldManager.SeaWater</c> — swell height and
/// wavelength, chop, whitecaps), the clouds scroll with it, and the grass's gust orbs blow harder in it. So a thunderstorm
/// is wind 1.0 under the storm sky, and wind 1.0 is the tallest, longest swell the sea raises.
/// </summary>
[Title( "Weather System" )]
[Icon( "thunderstorm" )]
public sealed class WeatherSystem : Component
{
	public static WeatherSystem Current { get; private set; }

	[Property, Group( "Weather" ), Title( "Start Weather" ), Description( "State id from data/weather.json the session opens in." )]
	public string StartWeatherId { get; set; } = WeatherCatalog.DefaultId;

	[Property, Group( "Weather" ), Title( "Auto Cycle" ), Description( "The host moves to another state (picked by weight) when the current one's time is up. Off = the weather only changes by the console (weather <id>)." )]
	public bool AutoCycle { get; set; } = true;

	/// <summary>Host → clients: the state everyone is in.</summary>
	[Sync( SyncFlags.FromHost )]
	public string CurrentWeatherId { get; set; } = WeatherCatalog.DefaultId;

	/// <summary>The state in force (never null).</summary>
	public WeatherStateData State => WeatherCatalog.Resolve( CurrentWeatherId );

	float _secondsLeft;
	string _appliedSkyFor;
	EnvironmentDayNightCycle _cycle;

	bool IsAuthority => !Networking.IsActive || Networking.IsHost;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		Current = this;
		_appliedSkyFor = null;
		if ( IsAuthority )
			HostEnter( WeatherCatalog.Resolve( StartWeatherId ), snapWind: true );
	}

	protected override void OnDisabled()
	{
		if ( Current == this )
			Current = null;
		base.OnDisabled();
	}

	/// <summary>Host: this state from now on, for a length rolled between its min and max.</summary>
	void HostEnter( WeatherStateData state, bool snapWind = false )
	{
		CurrentWeatherId = state.Id;
		_secondsLeft = 60f * Random.Shared.Float( Math.Max( 0.1f, Math.Min( state.MinMinutes, state.MaxMinutes ) ), Math.Max( 0.1f, Math.Max( state.MinMinutes, state.MaxMinutes ) ) );
		if ( snapWind && WindSystem.Current.IsValid() )
		{
			WindSystem.Current.BaseStrength01 = Math.Clamp( state.WindBase, 0f, 1f );
			WindSystem.Current.GustStrength01 = Math.Clamp( state.GustStrength, 0f, 1f );
		}
		Log.Info( $"[Weather] {state.DisplayName} (wind {state.WindBase:0.00}, sky '{state.SkyPresetId}') for {_secondsLeft / 60f:0.#} min" );
	}

	/// <summary>Host: the next state by weight, never the current one when another can be picked.</summary>
	WeatherStateData PickNext()
	{
		var total = 0f;
		foreach ( var s in WeatherCatalog.All )
		{
			if ( !string.Equals( s.Id, CurrentWeatherId, StringComparison.OrdinalIgnoreCase ) )
				total += Math.Max( 0f, s.Weight );
		}
		if ( total <= 0f )
			return State;
		var roll = Random.Shared.Float( 0f, total );
		foreach ( var s in WeatherCatalog.All )
		{
			if ( string.Equals( s.Id, CurrentWeatherId, StringComparison.OrdinalIgnoreCase ) )
				continue;
			roll -= Math.Max( 0f, s.Weight );
			if ( roll <= 0f )
				return s;
		}
		return State;
	}

	protected override void OnUpdate()
	{
		var state = State;

		if ( IsAuthority )
		{
			if ( AutoCycle )
			{
				_secondsLeft -= Time.Delta;
				if ( _secondsLeft <= 0f )
					HostEnter( state = PickNext() );
			}
			// The wind eases to the state's: a constant rate that crosses the whole 0–1 range in the transition time
			var wind = WindSystem.Current;
			if ( wind.IsValid() )
			{
				var step = WeatherCatalog.TransitionSeconds <= 0f ? 1f : Time.Delta / WeatherCatalog.TransitionSeconds;
				wind.BaseStrength01 = Approach( wind.BaseStrength01, Math.Clamp( state.WindBase, 0f, 1f ), step );
				wind.GustStrength01 = Approach( wind.GustStrength01, Math.Clamp( state.GustStrength, 0f, 1f ), step );
			}
		}

		// Every peer: the sky of the state it is told (the cycle fades to it)
		if ( _appliedSkyFor != state.Id )
		{
			if ( !_cycle.IsValid() )
				_cycle = Scene.GetAllComponents<EnvironmentDayNightCycle>().FirstOrDefault();
			if ( _cycle.IsValid() )
			{
				_cycle.SkyPresetId = state.SkyPresetId;
				_appliedSkyFor = state.Id;
			}
		}
	}

	static float Approach( float value, float target, float step )
		=> value < target ? Math.Min( target, value + step ) : Math.Max( target, value - step );

	/// <summary>Usage: <c>weather [id|auto|reload]</c> — no argument lists the states; an id makes the host switch to it now (its usual length, then the cycle carries on); <c>auto</c> flips Auto Cycle; <c>reload</c> re-reads data/weather.json.</summary>
	[ConCmd( "weather" )]
	public static void ConCmdWeather( string id = "" )
	{
		var weather = Current;
		if ( !weather.IsValid() )
		{
			Log.Info( "[Weather] no WeatherSystem in the active scene" );
			return;
		}
		if ( string.IsNullOrWhiteSpace( id ) )
		{
			foreach ( var s in WeatherCatalog.All )
				Log.Info( $"[Weather] {(string.Equals( s.Id, weather.CurrentWeatherId, StringComparison.OrdinalIgnoreCase ) ? "*" : " ")} {s.Id}: wind {s.WindBase:0.00}, gusts {s.GustStrength:0.00}, sky '{s.SkyPresetId}', weight {s.Weight:0.#}, {s.MinMinutes:0.#}–{s.MaxMinutes:0.#} min" );
			Log.Info( $"[Weather] auto cycle {(weather.AutoCycle ? "on" : "off")}{(weather.IsAuthority && weather.AutoCycle ? $", {weather._secondsLeft / 60f:0.#} min left" : "")}" );
			return;
		}
		if ( !weather.IsAuthority )
		{
			Log.Info( "[Weather] only the host changes the weather" );
			return;
		}
		if ( string.Equals( id, "reload", StringComparison.OrdinalIgnoreCase ) )
		{
			WeatherCatalog.Reload();
			Log.Info( $"[Weather] reloaded: {WeatherCatalog.All.Count} states" );
			return;
		}
		if ( string.Equals( id, "auto", StringComparison.OrdinalIgnoreCase ) )
		{
			weather.AutoCycle = !weather.AutoCycle;
			Log.Info( $"[Weather] auto cycle {(weather.AutoCycle ? "on" : "off")}" );
			return;
		}
		if ( !WeatherCatalog.Has( id ) )
		{
			Log.Info( $"[Weather] no state '{id}' (weather lists them)" );
			return;
		}
		weather.HostEnter( WeatherCatalog.Resolve( id ) );
	}
}
