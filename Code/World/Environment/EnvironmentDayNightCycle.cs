using System;
using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Procedural day/night for <c>environmentTest</c> / <c>lightingTest</c>: sun/moon arc, a <see cref="SkyDome"/> fed
/// from the active <c>data/sky_presets.json</c> preset (gradient + sun glow + wind-scrolled
/// clouds and a procedural star layer, blended night → dawn → day → noon → dusk).
/// <para>
/// Intentionally does <b>not</b> use <see cref="SkyBox2D"/> — stock engine sky materials
/// (<c>materials/skybox/skybox_*.vmat</c>) hijack that component. The camera clear colour
/// follows the preset horizon as a fallback behind the dome.
/// </para>
/// </summary>
[Title( "Environment Day Night Cycle" )]
public sealed class EnvironmentDayNightCycle : Component
{
	public enum CyclePhase : byte
	{
		Day = 0,
		Dusk = 1,
		Night = 2,
		Dawn = 3
	}

	[Property, Group( "Timing" ), Title( "Day length (s)" )]
	public float DaySeconds { get; set; } = 30f;

	[Property, Group( "Timing" ), Title( "Night length (s)" )]
	public float NightSeconds { get; set; } = 30f;

	[Property, Group( "Timing" ), Title( "Dawn / dusk blend (s)" )]
	public float TransitionSeconds { get; set; } = 5f;

	[Property, Group( "Timing" ), Title( "Start hour (0–24)" ), Range( 0f, 24f ), Description( "Clock time the cycle starts at when the scene loads (held there until the world load screen lifts): 6 = sunrise, 12 = noon, 18 = sunset, 0 = midnight. The sun turns at a constant rate, so hour h is cycle point (h − 6) / 24 whatever the day / night lengths." )]
	public float StartHour { get; set; } = 6f;

	[Property, Group( "Timing" ), Title( "Paused" )]
	public bool Paused { get; set; }

	[Property, Group( "Timing" ), Title( "Sleep speed (x)" ), Range( 2f, 60f ), Step( 1f ), Description( "How many times faster the clock runs while every player is asleep in a bed. It stops at 06:00. Default 10." )]
	public float SleepTimeScale { get; set; } = 10f;

	/// <summary>
	/// The clock on this machine is running at <see cref="SleepTimeScale"/> because every player sleeps
	/// (<c>PlayerMovement.Bed.cs</c>). The host decides (<see cref="TickSleepAuthority"/>) and stops it at 06:00; a
	/// client runs fast alongside, holds just short of 06:00 if its own clock gets there first, and takes the host's
	/// time when the host stops (<see cref="ClientSetSleepFastForward"/>).
	/// </summary>
	public bool SleepFastForward { get; private set; }

	double _nextSleepCheckAt;

	[Property, Group( "Lighting" )]
	public DirectionalLight SunLight { get; set; }

	[Property, Group( "Lighting" )]
	public DirectionalLight MoonLight { get; set; }

	[Property, Group( "Lighting" ), Title( "Sun peak color" )]
	public Color SunPeakColor { get; set; } = new( 3.2f, 2.95f, 2.6f, 1f );   // blinding noon sun; shade falls to the sky ambient (Mark 2026-10-13)

	[Property, Group( "Lighting" ), Title( "Sun horizon color" )]
	public Color SunHorizonColor { get; set; } = new( 1.0f, 0.55f, 0.28f, 1f );

	[Property, Group( "Lighting" ), Title( "Moon color" )]
	public Color MoonColor { get; set; } = new( 0.74f, 0.86f, 1.15f, 1f );   // cool, slightly desaturated blue (Mark 2026-10-21)

	[Property, Group( "Lighting" ), Title( "Night ambient (vs day)" ), Range( 0.01f, 1f ), Step( 0.01f ), Description( "Base fill when the sun is down — what you see out of the moonlight. Default 0.6." )]
	public float NightAmbientBrightness { get; set; } = 0.6f;   // an item at your feet can be seen without a torch, in a tree's moon shadow too (Mark 2026-11-18: 0.06 -> 0.6; 0.28 left the shadow black, 0.8 is the next step up)

	[Property, Group( "Lighting" ), Title( "Moonlight (vs day)" ), Range( 0f, 1f ), Step( 0.01f ), Description( "Directional light from the moon on top of night ambient, at the moon's highest (a fifth of it at the horizon, like the sun). 0.9 since 2026-11-23, was 0.34." )]
	public float MoonDirectionalBrightness { get; set; } = 0.9f;   // moonlit ground readable, moon shadows dark

	[Property, Group( "Lighting" ), Title( "Night max exposure" ), Range( 0.25f, 3f ), Step( 0.05f ), Description( "Ceiling for the camera's auto-exposure at night, so the eye cannot brighten the dark back to dusk. Blends to the camera's own Maximum Exposure by day. Default 1 = no night boost." )]
	public float NightMaxExposure { get; set; } = 1f;

	[Property, Group( "Lighting" ), Title( "Sky ambient day" )]
	public Color DaySkyAmbient { get; set; } = new( 0.18f, 0.23f, 0.33f, 1f );   // ~1/14 of the sun so standing out of it is clearly darker

	[Property, Group( "Lighting" ), Title( "Sky ambient night" )]
	public Color NightSkyAmbient { get; set; } = new( 0.22f, 0.28f, 0.42f, 1f );

	[Property, Group( "Sky" ), Title( "Preset" ), Description( "Id from data/sky_presets.json (clear, overcast, …). Its five keyframes blend through the day." )]
	public string SkyPresetId { get; set; } = SkyPresetCatalog.DefaultId;

	[Property, Group( "Sky" ), Title( "Sky dome" )]
	public SkyDome SkyDome { get; set; }

	[Property, Group( "Sky" ), Title( "Aerial perspective" ), Description( "Optional. The scene-wide distance fog; it takes the preset sky each frame so the far world fades into the sky day and night." )]
	public AerialPerspective Haze { get; set; }

	[Property, Group( "Disks" )]
	public GameObject SunDisk { get; set; }

	[Property, Group( "Disks" )]
	public GameObject MoonDisk { get; set; }

	[Property, Group( "Disks" ), Title( "Disk orbit radius (m)" ), Description( "Sun / moon distance from the camera. Default ~5200 m — unreachable scenery, not a skybox; they ride with the camera since 2026-11-23." )]
	public float DiskOrbitRadiusMeters { get; set; } = 5200f;

	/// <summary>The disks' distance from the camera in engine units, for the fog pass's sky cut.</summary>
	public float DiskOrbitRadiusUnits => Math.Max( 1000f, TerrainWorldUnits.MetersToEngine( DiskOrbitRadiusMeters ) );

	[Property, Group( "Disks" ), Title( "Disk scale" ), Description( "World scale of both disks. Leave 0 to auto-match prior angular size at the orbit radius." )]
	public float DiskScale { get; set; } = 0f;

	/// <summary>Legacy reference: scale ~7.22 at 8000 u (~1/9 of original auto size).</summary>
	const float AutoScaleReferenceRadiusUnits = 8000f;
	const float AutoScaleReferenceScale = 7.22f;

	[Property, Group( "Disks" ), Title( "Sun disk material" )]
	public Material SunDiskMaterial { get; set; }

	[Property, Group( "Disks" ), Title( "Moon disk material" )]
	public Material MoonDiskMaterial { get; set; }

	[Property, Group( "Disks" ), Title( "Sun bloom brightness" ), Range( 1f, 8f ), Step( 0.1f ), Description( "HDR tint (>1) so Bloom can glow; stays spectrally white." )]
	public float SunBloomBrightness { get; set; } = 4f;

	public const string DefaultSunDiskMaterialPath = "materials/environment/sky/sun_disk.vmat";
	public const string DefaultMoonDiskMaterialPath = "materials/environment/sky/moon_disk.vmat";

	[Property, Group( "Path" ), Title( "Arc yaw (°)" ), Description( "Compass heading of the sun/moon rise→set plane." )]
	public float ArcYawDegrees { get; set; } = 0f;

	[Property, Group( "Path" ), Title( "Arc tilt (°)" ), Description( "Tilts the shared ring off zenith. 30° = peak is 60° altitude, never straight overhead." ), Range( 0f, 75f ), Step( 1f )]
	public float ArcTiltDegrees { get; set; } = 30f;

	[Property, Group( "Debug" ), Title( "Log phase changes" )]
	public bool LogPhaseChanges { get; set; }

	[Property, Group( "Calendar" ), Title( "World save name" ), Description( "Optional override for world.json. Empty = TerrainWorldManager / menu session world." )]
	public string WorldSaveName { get; set; } = "";

	[Property, Group( "Calendar" ), ReadOnly, Title( "Day number" ), Description( "Persisted as dayNumber in WorldSaves/<world>/world.json. +1 each dawn." )]
	public int DayNumber { get; private set; } = 1;

	[Property, Group( "Debug" ), ReadOnly, Title( "Phase" )]
	public CyclePhase Phase { get; private set; }

	[Property, Group( "Debug" ), ReadOnly, Title( "Cycle time (s)" )]
	public float CycleTimeSeconds { get; private set; }

	[Property, Group( "Debug" ), ReadOnly, Title( "Day weight 0-1" )]
	public float DayWeight { get; private set; } = 1f;

	float _cycleLength;
	CyclePhase _loggedPhase = (CyclePhase)255;
	CyclePhase _previousPhase = (CyclePhase)255;
	bool _calendarReady;
	string _presetId;
	SkyPresetData _preset;
	SkyLook _night, _dawn, _day, _noon, _dusk;
	readonly System.Collections.Generic.Dictionary<Tonemapping, float> _authoredMaxExposure = new();
	TerrainWorldManager _world;

	protected override void OnStart()
	{
		ResolveRefs();
		_world = Scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		BanEngineSkyBoxes();
		DisableFogOnLights();
		EnsureDiskVisualOnly( SunDisk );
		EnsureDiskVisualOnly( MoonDisk );
		RecalculateCycleLength();
		var startCycle01 = (StartHour - 6f) / 24f;
		CycleTimeSeconds = _cycleLength * (startCycle01 - MathF.Floor( startCycle01 ));
		LoadDayNumberFromWorldSave();
		EvaluatePhase( out var startPhase, out _, out _, out _ );
		Phase = startPhase;
		_previousPhase = startPhase;
		_calendarReady = true;
		ApplyVisuals( force: true );
	}

	protected override void OnUpdate()
	{
		// No single-key jumps (the Y / U hotkeys are gone, 2026-11-03): the U action fired while Mark flew the fly camera
		// and snapped the world to midnight. Console cycle_day / cycle_night still jump.
		// The clock holds behind the world load screen, so the scene opens on Start hour, not half an hour later.
		var worldLoading = _world is not null && _world.IsValid() && _world.IsLoadScreenUp;
		// Host-wide dev lock (Settings → Dev tools, synced to every peer): noon, held.
		if ( DevToolsSettings.DayLocked )
		{
			RecalculateCycleLength();
			if ( MathF.Abs( CycleTimeSeconds - _cycleLength * 0.25f ) > 1e-3f )
				JumpToNoon();
		}
		else if ( !Paused && !worldLoading )
		{
			RecalculateCycleLength();
			TickSleepAuthority( clockRuns: true );
			AdvanceClock();
		}

		if ( SleepFastForward && ( DevToolsSettings.DayLocked || Paused || worldLoading ) )
			TickSleepAuthority( clockRuns: false );

		ApplyVisuals( force: false );
	}

	/// <summary>One frame of clock. Asleep, it runs <see cref="SleepTimeScale"/> times faster and never passes 06:00.</summary>
	void AdvanceClock()
	{
		if ( !SleepFastForward )
		{
			CycleTimeSeconds += Time.Delta;
			if ( _cycleLength > 0.01f )
				CycleTimeSeconds %= _cycleLength;
			return;
		}

		// A scene saved before this property existed loads it as 0.
		var scale = SleepTimeScale > 1f ? SleepTimeScale : 10f;
		var next = CycleTimeSeconds + Time.Delta * scale;
		if ( next < _cycleLength )
		{
			CycleTimeSeconds = next;
			return;
		}

		if ( !IsSleepAuthority )
		{
			// A client whose clock ran ahead of the host's waits at the end of the night for the host's 06:00.
			CycleTimeSeconds = MathF.Max( 0f, _cycleLength - 0.01f );
			return;
		}

		// 06:00: the night is over. Everyone gets up and the clock is back to normal speed.
		CycleTimeSeconds = 0f;
		SleepFastForward = false;
		PlayerMovement.HostSetEveryoneSleeping( Scene, sleeping: false, hostCycle01: 0f, dawn: true );
		Log.Info( "[EnvironmentDayNight] everyone slept until 06:00." );
	}

	/// <summary>
	/// Host: start the fast night once every player is asleep, stop it the moment one of them is not (got up, was
	/// hit, a new player joined) or the clock cannot run. Checked four times a second while nobody sleeps - behind a
	/// counter that is zero unless someone lies in a bed - and every frame while the night runs fast.
	/// </summary>
	void TickSleepAuthority( bool clockRuns )
	{
		if ( !IsSleepAuthority )
			return;

		if ( !SleepFastForward )
		{
			if ( Time.NowDouble < _nextSleepCheckAt )
				return;
			_nextSleepCheckAt = Time.NowDouble + 0.25;
		}

		var everyoneAsleep = clockRuns && PlayerMovement.HostEveryoneAsleep( Scene );
		if ( everyoneAsleep == SleepFastForward )
			return;

		SleepFastForward = everyoneAsleep;
		PlayerMovement.HostSetEveryoneSleeping( Scene, everyoneAsleep, Cycle01, dawn: false );
	}

	/// <summary>
	/// This machine decides when the night runs fast: the host, or a game with no network. (Not
	/// <see cref="IsWorldAuthority"/>: that reads the scene object's own network state, which is not active on a
	/// client either, so a client passes it.)
	/// </summary>
	static bool IsSleepAuthority => !Networking.IsActive || Networking.IsHost;

	/// <summary>Where the clock stands in the day, 0–1 (0 = 06:00, 0.25 = noon, 0.5 = 18:00, 0.75 = midnight).</summary>
	float Cycle01 => _cycleLength > 0.01f ? Math.Clamp( CycleTimeSeconds / _cycleLength, 0f, 1f ) : 0f;

	/// <summary>
	/// Client: the host says everyone is asleep (run this machine's clock fast) or that the fast night is over (set
	/// this machine's clock to the host's <paramref name="hostCycle01"/>; the screen is black while it lands).
	/// </summary>
	public static void ClientSetSleepFastForward( bool sleeping, float hostCycle01 )
	{
		var cycle = FindActive();
		if ( cycle is null || IsSleepAuthority )
			return;

		cycle.SleepFastForward = sleeping;
		Log.Info( sleeping ? "[EnvironmentDayNight] everyone is asleep: clock running fast with the host." : $"[EnvironmentDayNight] sleep over: clock set to the host (cycle {hostCycle01:0.000})." );
		if ( sleeping )
			return;

		cycle.RecalculateCycleLength();
		cycle.CycleTimeSeconds = cycle._cycleLength * Math.Clamp( hostCycle01, 0f, 0.9999f );
		cycle.ApplyVisuals( force: true );
	}

	/// <summary>Dev readout for <c>bed_info</c>.</summary>
	public static string DescribeSleepState()
	{
		var cycle = FindActive();
		if ( cycle is null )
			return "no active day / night cycle in this scene (beds still work, the night cannot be slept through)";

		var hour = ( 6f + cycle.Cycle01 * 24f ) % 24f;
		return $"fastForward={cycle.SleepFastForward} x{cycle.SleepTimeScale:0.#} hour={hour:0.00} day={cycle.DayNumber} paused={cycle.Paused} dayLocked={DevToolsSettings.DayLocked}";
	}

	/// <summary>Snap to the top of the sun's arc (cycle 0.25). Local to this peer — the cycle is not synced.</summary>
	public void JumpToNoon()
	{
		RecalculateCycleLength();
		CycleTimeSeconds = _cycleLength * 0.25f;
		ApplyVisuals( force: true );
	}

	/// <summary>Snap to the top of the moon's arc (cycle 0.75).</summary>
	public void JumpToMidnight()
	{
		RecalculateCycleLength();
		CycleTimeSeconds = _cycleLength * 0.75f;
		ApplyVisuals( force: true );
	}

	/// <summary>
	/// Game-clock hours since day 1 dawn: (DayNumber − 1) × 24 + hours into the cycle. What timed stations (the
	/// drying rack) count in. False when the scene has no active cycle.
	/// </summary>
	public static bool TryGetGameHours( out double hours )
	{
		var cycle = FindActive();
		if ( cycle is null || cycle._cycleLength <= 0f )
		{
			hours = 0;
			return false;
		}

		hours = ( cycle.DayNumber - 1 ) * 24.0 + cycle.CycleTimeSeconds / cycle._cycleLength * 24.0;
		return true;
	}

	static EnvironmentDayNightCycle FindActive()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return null;
		return scene.GetAllComponents<EnvironmentDayNightCycle>().FirstOrDefault( c => c.IsValid() && c.Active );
	}

	/// <summary>
	/// Dev: switch the scene's day / night cycle on or off. Finds the cycle's GameObject even while it is disabled
	/// (the DayNightCycle group — cycle + sky dome + sun / moon disks + moon light; switched off, the plain sun stays).
	/// </summary>
	[ConCmd( "cycle_toggle" )]
	public static void ConCmdToggle()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null )
			return;

		// Walk the whole hierarchy including disabled objects and components: GetAllObjects( true ) did not
		// surface terrainTest's disabled DayNightCycle group in play (Mark 2026-10-13, "no cycle object").
		var cycle = scene.Components.GetAll<EnvironmentDayNightCycle>( FindMode.EverythingInSelfAndDescendants ).FirstOrDefault();
		var owner = cycle?.GameObject;
		if ( owner is null || !owner.IsValid() )
		{
			Log.Warning( $"[EnvironmentDayNight] no cycle object in the active scene '{scene.Name}' — add a DayNightCycle group (see terrainTest / testscene1)." );
			return;
		}

		owner.Enabled = !owner.Enabled;
		Log.Info( $"[EnvironmentDayNight] cycle {(owner.Enabled ? "ON" : "OFF")} ('{owner.Name}')." );
	}

	[ConCmd( "cycle_day" )]
	public static void ConCmdDay()
	{
		var cycle = FindActive();
		if ( cycle is null ) { Log.Warning( "[EnvironmentDayNight] no cycle in the active scene." ); return; }
		cycle.JumpToNoon();
		Log.Info( "[EnvironmentDayNight] noon." );
	}

	/// <summary>Usage: <c>cycle_pause</c> — freeze / resume the cycle where it is (the sun and sky stay lit; cycle_toggle switches the whole cycle off).</summary>
	[ConCmd( "cycle_pause" )]
	public static void ConCmdPause()
	{
		var cycle = FindActive();
		if ( cycle is null ) { Log.Warning( "[EnvironmentDayNight] no cycle in the active scene." ); return; }
		cycle.Paused = !cycle.Paused;
		Log.Info( $"[EnvironmentDayNight] {(cycle.Paused ? "paused" : "running")}." );
	}

	[ConCmd( "cycle_night" )]
	public static void ConCmdNight()
	{
		var cycle = FindActive();
		if ( cycle is null ) { Log.Warning( "[EnvironmentDayNight] no cycle in the active scene." ); return; }
		cycle.JumpToMidnight();
		Log.Info( "[EnvironmentDayNight] midnight." );
	}

	void ResolveRefs()
	{
		SkyDome ??= Scene.GetAllComponents<SkyDome>().FirstOrDefault();
		SunLight ??= Components.Get<DirectionalLight>( FindMode.EnabledInSelfAndDescendants );
		MoonLight ??= Components.GetAll<DirectionalLight>( FindMode.EnabledInSelfAndDescendants )
			.FirstOrDefault( l => l != SunLight );
	}

	/// <summary>
	/// Destroy every <see cref="SkyBox2D"/> in the scene once at start. Stock HDRI materials live on
	/// that component path; we never use it. The scenes no longer carry one — this only guards a
	/// prefab or editor default slipping back in.
	/// </summary>
	void BanEngineSkyBoxes()
	{
		foreach ( var sky in Scene.GetAllComponents<SkyBox2D>().ToArray() )
		{
			if ( sky is null || !sky.IsValid() )
				continue;

			var go = sky.GameObject;
			if ( go is not null && go.IsValid() )
				go.Destroy();
			else
				sky.Destroy();
		}
	}

	void DisableFogOnLights()
	{
		if ( SunLight is not null && SunLight.IsValid() )
			SunLight.FogStrength = 0f;

		if ( MoonLight is not null && MoonLight.IsValid() )
			MoonLight.FogStrength = 0f;
	}

	void RecalculateCycleLength()
	{
		var day = Math.Max( 0.1f, DaySeconds );
		var night = Math.Max( 0.1f, NightSeconds );
		var blend = Math.Max( 0.05f, TransitionSeconds );
		_cycleLength = day + blend + night + blend;
	}

	void ApplyVisuals( bool force )
	{
		EvaluatePhase( out var phase, out var dayWeight, out var sunElev01, out var moonElev01 );
		Phase = phase;
		DayWeight = dayWeight;

		if ( _calendarReady && phase != _previousPhase )
		{
			if ( phase == CyclePhase.Dawn )
				OnDawnBreaks();
			_previousPhase = phase;
		}

		if ( LogPhaseChanges && phase != _loggedPhase )
		{
			_loggedPhase = phase;
			Log.Info( $"[EnvironmentDayNight] phase={phase} day={DayNumber} dayWeight={dayWeight:0.00} t={CycleTimeSeconds:0.0}s" );
		}

		var look = SampleSkyLook( phase, dayWeight, sunElev01 );
		var sunDir = UpdateCelestial( SunLight, SunDisk, sunElev01, isSun: true, dayWeight, look.SunLight );
		UpdateCelestial( MoonLight, MoonDisk, moonElev01, isSun: false, dayWeight, 1f );
		UpdateSky( look, sunDir, StarVisibility( dayWeight ), dayWeight );
	}

	void LoadDayNumberFromWorldSave()
	{
		var worldName = ResolveWorldSaveName();
		DayNumber = WorldSaveIO.GetDayNumber( worldName );
	}

	void OnDawnBreaks()
	{
		if ( !IsWorldAuthority() )
			return;

		var worldName = ResolveWorldSaveName();
		if ( WorldSaveIO.TryIncrementDayNumber( worldName, out var newDay ) )
		{
			DayNumber = newDay;
			Log.Info( $"[EnvironmentDayNight] dawn — day {DayNumber} saved to WorldSaves/{worldName}/world.json" );
			return;
		}

		// No world.json yet (e.g. bare environmentTest) — keep an in-memory tally.
		DayNumber = WorldSaveIO.NormalizeDayNumber( DayNumber ) + 1;
		Log.Info( $"[EnvironmentDayNight] dawn — day {DayNumber} (no world.json for '{worldName}' yet)" );
	}

	string ResolveWorldSaveName()
	{
		if ( !string.IsNullOrWhiteSpace( WorldSaveName ) )
			return WorldSaveName.Trim();

		if ( Scene is not null && Scene.IsValid() )
		{
			foreach ( var manager in Scene.GetAllComponents<TerrainWorldManager>() )
			{
				if ( manager is not null && manager.IsValid() && !string.IsNullOrWhiteSpace( manager.WorldName ) )
					return manager.WorldName;
			}
		}

		return WorldSessionState.ActiveWorldName;
	}

	static bool IsWorldAuthority()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return true;

		return scene.Network?.Active != true || Networking.IsHost;
	}

	void EvaluatePhase( out CyclePhase phase, out float dayWeight, out float sunElev01, out float moonElev01 )
	{
		var len = Math.Max( 0.01f, _cycleLength );
		var t = CycleTimeSeconds % len;
		if ( t < 0f )
			t += len;

		// Shared ring: constant angular rate, always 180° apart.
		// cycle01 0 = sunrise, 0.5 = sunset, 1 = back to sunrise.
		var cycle01 = t / len;
		sunElev01 = cycle01 * 2f; // 0..2
		moonElev01 = sunElev01 + 1f;
		if ( moonElev01 >= 2f )
			moonElev01 -= 2f;

		// Stable phase selection based on ring position (prevents jitter between phases).
		// TransitionSeconds is treated as the total blend duration in time, mapped into cycle-space.
		var horizonBlendU = Math.Clamp( TransitionSeconds / len, 0.01f, 0.25f );

		if ( cycle01 < horizonBlendU )
		{
			// Dawn: moon is disappearing as sun rises; ramp 0→1.
			phase = CyclePhase.Dawn;
			dayWeight = Smooth01( cycle01 / horizonBlendU );
		}
		else if ( cycle01 < 0.5f - horizonBlendU * 0.5f )
		{
			phase = CyclePhase.Day;
			dayWeight = 1f;
		}
		else if ( cycle01 < 0.5f + horizonBlendU * 0.5f )
		{
			// Dusk: sun dips below the horizon; ramp 1→0.
			phase = CyclePhase.Dusk;
			var u = (cycle01 - (0.5f - horizonBlendU * 0.5f)) / horizonBlendU; // 0..1
			dayWeight = 1f - Smooth01( Math.Clamp( u, 0f, 1f ) );
		}
		else
		{
			phase = CyclePhase.Night;
			dayWeight = 0f;
		}
	}

	/// <summary>Any channel bright enough to shade a pixel at 8 bits after tonemapping.</summary>
	static bool CastsVisibleLight( Color c ) => Math.Max( c.r, Math.Max( c.g, c.b ) ) > 0.002f;

	Vector3 UpdateCelestial( DirectionalLight light, GameObject disk, float elev01, bool isSun, float dayWeight, float lightScale )
	{
		// elev01 on [0,2): full shared ring. Angle 0→2π.
		var angle = elev01 * MathF.PI;
		var yaw = ArcYawDegrees * (MathF.PI / 180f);
		var tilt = Math.Clamp( ArcTiltDegrees, 0f, 75f ) * (MathF.PI / 180f);

		// Horizontal rise→set axis, then tilt the ring's "up" off world zenith.
		var east = new Vector3( MathF.Cos( yaw ), MathF.Sin( yaw ), 0f );
		var north = Vector3.Cross( Vector3.Up, east );
		if ( north.LengthSquared < 1e-6f )
			north = Vector3.Forward;
		else
			north = north.Normal;

		var ringUp = (Vector3.Up * MathF.Cos( tilt ) + north * MathF.Sin( tilt )).Normal;
		var dir = (east * MathF.Cos( angle ) + ringUp * MathF.Sin( angle )).Normal;
		var height = Vector3.Dot( dir, Vector3.Up ); // world altitude (tilt-aware)
		var aboveHorizon = height > 0.02f;
		// Keep disks enabled through below-horizon so they don't appear to "stop" at the rim.
		var diskVisible = height > -0.99f;

		if ( light is not null && light.IsValid() )
		{
			light.WorldRotation = Rotation.LookAt( -dir, Vector3.Up );
			light.FogStrength = 0f;
			light.Enabled = true;

			if ( isSun )
			{
				// Sun carries the ambient fill so dusk/dawn never go black (SkyColor alpha must be >0).
				var nightFill = (NightSkyAmbient * NightAmbientBrightness).WithAlpha( 1f );
				var dayFill = DaySkyAmbient.WithAlpha( 1f );
				// The camera's biome shades the fill (data/biome_atmosphere.json "shade"): under the Redwood canopy the sky
				// light drops and the sun's patches stay full bright - the dark comes from the light, not from a screen dim
				// (Mark 2026-11-23: "true directional light ... the shadows need to come from this sun's light"). By day
				// only: the night fill is already low ("its far too dark at night in clover hills and redwood").
				var shade = MathX.Lerp( 1f, AerialPerspective.BiomeShade, dayWeight );
				light.SkyColor = (Color.Lerp( nightFill, dayFill, dayWeight ) * shade).WithAlpha( 1f );

				var sunDir = aboveHorizon ? Math.Clamp( height, 0f, 1f ) : 0f;
				var color = Color.Lerp( SunHorizonColor, SunPeakColor, sunDir );
				light.LightColor = color * (MathX.Lerp( 0.2f, 1f, sunDir ) * lightScale);
			}
			else
			{
				// Ambient lives on the sun light — moon only adds directional moonlight.
				light.SkyColor = new Color( 0f, 0f, 0f, 0f );
				// The moon lights the night the way the sun lights the day (Mark 2026-11-23: "add a moon that provides light at
				// night the same as the sun"): the sun's own height curve, a fifth of its light already at the horizon and all
				// of it overhead, where it used to be nothing at the horizon and a third overhead.
				var moonDir = aboveHorizon ? Math.Clamp( height, 0f, 1f ) : 0f;
				light.LightColor = aboveHorizon ? MoonColor * (MoonDirectionalBrightness * MathX.Lerp( 0.2f, 1f, moonDir )) : Color.Black;
			}

			// A light whose colour has faded to black (the sun at night — it stays enabled for the ambient fill —
			// or the moon under the horizon by day) still rendered all its cascades every frame: 4 + 2 shadow
			// views of nothing (2026-11-10 fps routine). Its shadows are invisible, so they go off with it and come
			// back the frame it has light again.
			light.Shadows = !TerrainPerfBench.SunShadowsOff && CastsVisibleLight( light.LightColor );
		}

		if ( disk is not null && disk.IsValid() )
		{
			disk.Enabled = diskVisible;
			var radius = Math.Max( 1000f, TerrainWorldUnits.MetersToEngine( DiskOrbitRadiusMeters ) );
			// Round the camera, not the world's centre: the sun and moon are at no reachable distance, so they must not
			// slide against the sky as the player crosses the world - and the fog pass can then tell them from terrain by
			// distance alone (AerialPerspective's sky cut sits just inside this radius; fogged like a far hill, the sun
			// showed as a dull brown ball, Mark 2026-11-23).
			var viewer = Scene.Camera is { } diskCamera && diskCamera.IsValid() ? diskCamera.WorldPosition : Vector3.Zero;
			disk.WorldPosition = viewer + (dir * radius);

			var scale = DiskScale > 0.01f
				? DiskScale
				: AutoScaleReferenceScale * (radius / AutoScaleReferenceRadiusUnits);
			disk.WorldScale = Math.Max( 1f, scale );

			// Tidally locked: same face always toward orbit center (the ground / world origin).
			var towardGround = -dir;
			var arcNormal = Vector3.Cross( east, ringUp );
			if ( arcNormal.LengthSquared < 1e-6f )
				arcNormal = north;
			else
				arcNormal = arcNormal.Normal;
			disk.WorldRotation = Rotation.LookAt( towardGround, arcNormal );

			var diskRenderer = disk.Components.Get<ModelRenderer>();
			if ( diskRenderer is not null && diskRenderer.IsValid() )
			{
				EnsureDiskMaterials();
				diskRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;

				diskRenderer.RenderOptions.Bloom = isSun && aboveHorizon;
				diskRenderer.RenderOptions.Game = true;

				if ( isSun )
				{
					if ( SunDiskMaterial is not null )
						diskRenderer.MaterialOverride = SunDiskMaterial;
					// Pure white (HDR for bloom) — shader uses renderer Tint, not mesh vertex colors.
					var b = Math.Max( 1f, SunBloomBrightness );
					diskRenderer.Tint = new Color( b, b, b, 1f );
				}
				else
				{
					if ( MoonDiskMaterial is not null )
						diskRenderer.MaterialOverride = MoonDiskMaterial;
					diskRenderer.Tint = Color.White;
				}
			}
		}

		return dir;
	}

	/// <summary>Strip colliders so the disks are unreachable scenery, not physical objects.</summary>
	void EnsureDiskVisualOnly( GameObject disk )
	{
		if ( disk is null || !disk.IsValid() )
			return;

		foreach ( var col in disk.Components.GetAll<Collider>( FindMode.EverythingInSelf ).ToArray() )
		{
			if ( col is not null && col.IsValid() )
				col.Destroy();
		}
	}

	void EnsureDiskMaterials()
	{
		SunDiskMaterial ??= Material.Load( DefaultSunDiskMaterialPath );
		MoonDiskMaterial ??= Material.Load( DefaultMoonDiskMaterialPath );
	}

	void UpdateSky( in SkyLook look, Vector3 sunDir, float starVisibility, float dayWeight )
	{
		if ( SkyDome is not null && SkyDome.IsValid() )
			SkyDome.Apply( look, sunDir, _preset.CloudScale, _preset.CloudSpeed, starVisibility );

		if ( Haze is not null && Haze.IsValid() )
			Haze.Apply( look.Zenith, look.Horizon, look.SunGlow, sunDir );

		// Clear colour = horizon, so anything past the dome (or a scene without one) still matches.
		var clear = look.Horizon.WithAlpha( 1f );
		foreach ( var cam in Scene.GetAllComponents<CameraComponent>() )
		{
			if ( cam is null || !cam.IsValid() )
				continue;
			cam.BackgroundColor = clear;
			cam.ClearFlags = ClearFlags.All;

			// Auto-exposure would lift the night back up to ~3x; cap its ceiling as the sun goes down.
			var tonemap = cam.GetComponent<Tonemapping>();
			if ( tonemap is null || !tonemap.IsValid() )
				continue;
			if ( !_authoredMaxExposure.TryGetValue( tonemap, out var dayMax ) )
				_authoredMaxExposure[tonemap] = dayMax = tonemap.MaximumExposure;
			tonemap.MaximumExposure = MathX.Lerp( Math.Min( NightMaxExposure, dayMax ), dayMax, dayWeight );
		}
	}

	/// <summary>Parse the preset's keyframes once per preset change, not per frame.</summary>
	void EnsurePresetLooks()
	{
		var id = string.IsNullOrWhiteSpace( SkyPresetId ) ? SkyPresetCatalog.DefaultId : SkyPresetId.Trim();
		if ( _preset is not null && string.Equals( _presetId, id, StringComparison.OrdinalIgnoreCase ) )
			return;

		// A change of preset while running (the weather): fade from the sky as it is now instead of snapping
		if ( _preset is not null && _hasLastLook )
		{
			_blendFrom = _lastLook;
			_presetBlend = 0f;
		}
		_presetId = id;
		_preset = SkyPresetCatalog.Resolve( id );
		_night = SkyLook.From( _preset.Night );
		_dawn = SkyLook.From( _preset.Dawn );
		_day = SkyLook.From( _preset.Day );
		_noon = SkyLook.From( _preset.Noon );
		_dusk = SkyLook.From( _preset.Dusk );
	}

	/// <summary>Seconds a change of <see cref="SkyPresetId"/> takes to fade in (the weather turning).</summary>
	[Property, Group( "Sky" ), Title( "Preset Blend (s)" ), Range( 0f, 300f ), Step( 5f )]
	public float SkyPresetBlendSeconds { get; set; } = 45f;

	SkyLook _lastLook, _blendFrom;
	bool _hasLastLook;
	float _presetBlend = 1f;

	/// <summary>The sky right now: the preset's look for this point of the day, cross-faded from the previous preset while the weather turns.</summary>
	SkyLook SampleSkyLook( CyclePhase phase, float dayWeight, float sunElev01 )
	{
		var look = SamplePresetLook( phase, dayWeight, sunElev01 );
		if ( _presetBlend < 1f )
		{
			_presetBlend = SkyPresetBlendSeconds <= 0f ? 1f : Math.Min( 1f, _presetBlend + (Time.Delta / SkyPresetBlendSeconds) );
			look = SkyLook.Lerp( _blendFrom, look, Smooth01( _presetBlend ) );
		}
		_lastLook = look;
		_hasLastLook = true;
		return look;
	}

	SkyLook SamplePresetLook( CyclePhase phase, float dayWeight, float sunElev01 )
	{
		EnsurePresetLooks();

		switch ( phase )
		{
			case CyclePhase.Day:
			{
				// Shared-ring elev is 0..2; only the above-horizon half (0..1) maps to day sky.
				var dayElev = sunElev01 <= 1f ? sunElev01 : Math.Clamp( 2f - sunElev01, 0f, 1f );
				var noon = MathF.Sin( dayElev * MathF.PI );
				return SkyLook.Lerp( _day, _noon, noon );
			}
			case CyclePhase.Dusk:
			{
				if ( dayWeight > 0.5f )
					return SkyLook.Lerp( _dusk, _day, (dayWeight - 0.5f) * 2f );
				return SkyLook.Lerp( _night, _dusk, dayWeight * 2f );
			}
			case CyclePhase.Dawn:
			{
				if ( dayWeight < 0.5f )
					return SkyLook.Lerp( _night, _dawn, dayWeight * 2f );
				return SkyLook.Lerp( _dawn, _day, (dayWeight - 0.5f) * 2f );
			}
			default:
				return _night;
		}
	}

	/// <summary>0 by day, 1 deep in the night. Invisible through most of dusk / dawn; the dome shader draws the stars.</summary>
	static float StarVisibility( float dayWeight )
	{
		var night = Math.Clamp( 1f - dayWeight, 0f, 1f );
		return Smooth01( Math.Clamp( (night - 0.45f) / 0.55f, 0f, 1f ) );
	}

	static float Smooth01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t);
	}
}
