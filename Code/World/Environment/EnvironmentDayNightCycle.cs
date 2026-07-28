using System;
using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Procedural day/night for <c>environmentTest</c>: sun/moon arc, camera clear-color sky,
/// and a camera-centered star shell.
/// <para>
/// Intentionally does <b>not</b> use <see cref="SkyBox2D"/> — stock engine sky materials
/// (<c>materials/skybox/skybox_*.vmat</c>) hijack that component. Sky color is the camera
/// background only; stars and celestial disks supply detail.
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

	[Property, Group( "Timing" ), Title( "Start at night" )]
	public bool StartAtNight { get; set; }

	[Property, Group( "Timing" ), Title( "Paused" )]
	public bool Paused { get; set; }

	[Property, Group( "Lighting" )]
	public DirectionalLight SunLight { get; set; }

	[Property, Group( "Lighting" )]
	public DirectionalLight MoonLight { get; set; }

	[Property, Group( "Lighting" ), Title( "Sun peak color" )]
	public Color SunPeakColor { get; set; } = new( 1.05f, 1.0f, 0.95f, 1f );

	[Property, Group( "Lighting" ), Title( "Sun horizon color" )]
	public Color SunHorizonColor { get; set; } = new( 1.0f, 0.55f, 0.28f, 1f );

	[Property, Group( "Lighting" ), Title( "Moon color" )]
	public Color MoonColor { get; set; } = new( 0.9f, 0.95f, 1.05f, 1f );

	[Property, Group( "Lighting" ), Title( "Night ambient (vs day)" ), Range( 0.05f, 1f ), Step( 0.01f ), Description( "Base fill when the sun is down — even out of moonlight. Default 0.30." )]
	public float NightAmbientBrightness { get; set; } = 0.30f;

	[Property, Group( "Lighting" ), Title( "Moonlight (vs day)" ), Range( 0f, 1f ), Step( 0.01f ), Description( "Extra directional light from the moon on top of night ambient. Default 0.15." )]
	public float MoonDirectionalBrightness { get; set; } = 0.15f;

	[Property, Group( "Lighting" ), Title( "Sky ambient day" )]
	public Color DaySkyAmbient { get; set; } = new( 0.4f, 0.5f, 0.7f, 1f );

	[Property, Group( "Lighting" ), Title( "Sky ambient night" )]
	public Color NightSkyAmbient { get; set; } = new( 0.22f, 0.28f, 0.42f, 1f );

	[Property, Group( "Sky" ), Title( "Day sky color" )]
	public Color DaySkyTint { get; set; } = new( 0.55f, 0.72f, 1.05f, 1f );

	[Property, Group( "Sky" ), Title( "Noon sky color" )]
	public Color NoonSkyTint { get; set; } = new( 0.7f, 0.85f, 1.15f, 1f );

	[Property, Group( "Sky" ), Title( "Dusk sky color" )]
	public Color DuskSkyTint { get; set; } = new( 1.05f, 0.45f, 0.22f, 1f );

	[Property, Group( "Sky" ), Title( "Dawn sky color" )]
	public Color DawnSkyTint { get; set; } = new( 1.0f, 0.55f, 0.35f, 1f );

	[Property, Group( "Sky" ), Title( "Night sky color" )]
	public Color NightSkyTint { get; set; } = new( 0.14f, 0.18f, 0.32f, 1f );

	[Property, Group( "Stars" )]
	public ProceduralStarField StarField { get; set; }

	[Property, Group( "Disks" )]
	public GameObject SunDisk { get; set; }

	[Property, Group( "Disks" )]
	public GameObject MoonDisk { get; set; }

	[Property, Group( "Disks" ), Title( "Disk orbit radius (m)" ), Description( "Shared sun/moon ring distance. Default ~5200 m (old 200 m + 5000 m) — unreachable scenery, not a skybox." )]
	public float DiskOrbitRadiusMeters { get; set; } = 5200f;

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

	public const string DefaultSunDiskMaterialPath = "materials/environment/sun_disk.vmat";
	public const string DefaultMoonDiskMaterialPath = "materials/environment/moon_disk.vmat";

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

	protected override void OnStart()
	{
		ResolveRefs();
		BanEngineSkyBoxes();
		EnsureStarField();
		CleanupLegacyRuntimeObjects();
		DisableFogOnLights();
		EnsureDiskVisualOnly( SunDisk );
		EnsureDiskVisualOnly( MoonDisk );
		RecalculateCycleLength();
		CycleTimeSeconds = StartAtNight ? DaySeconds + TransitionSeconds : 0f;
		LoadDayNumberFromWorldSave();
		EvaluatePhase( out var startPhase, out _, out _, out _ );
		Phase = startPhase;
		_previousPhase = startPhase;
		_calendarReady = true;
		ApplyVisuals( force: true );

		foreach ( var fly in Scene.GetAllComponents<TerrainTestFlyCamera>() )
		{
			if ( fly is not null && fly.IsValid() )
				fly.SetViewLookAt( Vector3.Zero );
		}
	}

	protected override void OnUpdate()
	{
		// Keep killing any SkyBox2D that appears (editor defaults / prefab leftovers).
		BanEngineSkyBoxes();

		if ( !Paused )
		{
			RecalculateCycleLength();
			CycleTimeSeconds += Time.Delta;
			if ( _cycleLength > 0.01f )
				CycleTimeSeconds %= _cycleLength;
		}

		ApplyVisuals( force: false );
	}

	void ResolveRefs()
	{
		SunLight ??= Components.Get<DirectionalLight>( FindMode.EnabledInSelfAndDescendants );
		MoonLight ??= Components.GetAll<DirectionalLight>( FindMode.EnabledInSelfAndDescendants )
			.FirstOrDefault( l => l != SunLight );

		StarField ??= Components.Get<ProceduralStarField>( FindMode.EnabledInSelfAndDescendants );
		StarField ??= Scene.GetAllComponents<ProceduralStarField>().FirstOrDefault();
	}

	/// <summary>
	/// Destroy every <see cref="SkyBox2D"/> in the scene. Stock HDRI materials live on that
	/// component path; we never use it.
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

	void EnsureStarField()
	{
		if ( StarField is not null && StarField.IsValid() )
			return;

		var go = new GameObject( true, "StarField" );
		go.Parent = GameObject;
		StarField = go.Components.Create<ProceduralStarField>();
		StarField.ShellRadiusMeters = Math.Max(
			ProceduralStarField.DefaultShellRadiusMeters,
			DiskOrbitRadiusMeters + 2000f );
		StarField.StarCount = ProceduralStarField.DefaultStarCount;
		StarField.StarScale = ProceduralStarField.DefaultStarScale;
		StarField.BrightStarScale = ProceduralStarField.DefaultBrightStarScale;
		StarField.MilkyWayFraction = 0.72f;
	}

	void CleanupLegacyRuntimeObjects()
	{
		foreach ( var mr in Scene.GetAllComponents<ModelRenderer>().ToArray() )
		{
			var go = mr?.GameObject;
			if ( go is null || !go.IsValid() )
				continue;
			if ( go.Name is "SkyDome" or "SkyProcedural" or "Sky" or "SkyDay" or "SkyNight" )
				go.Destroy();
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

		UpdateCelestial( SunLight, SunDisk, sunElev01, isSun: true, dayWeight );
		UpdateCelestial( MoonLight, MoonDisk, moonElev01, isSun: false, dayWeight );
		UpdateSkyColor( phase, dayWeight, sunElev01 );
		UpdateStars( dayWeight );
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

	void UpdateCelestial( DirectionalLight light, GameObject disk, float elev01, bool isSun, float dayWeight )
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
			light.Shadows = true;
			light.Enabled = true;

			if ( isSun )
			{
				// Sun carries the ambient fill so dusk/dawn never go black (SkyColor alpha must be >0).
				var nightFill = (NightSkyAmbient * NightAmbientBrightness).WithAlpha( 1f );
				var dayFill = DaySkyAmbient.WithAlpha( 1f );
				light.SkyColor = Color.Lerp( nightFill, dayFill, dayWeight ).WithAlpha( 1f );

				var sunDir = aboveHorizon ? Math.Clamp( height, 0f, 1f ) : 0f;
				var color = Color.Lerp( SunHorizonColor, SunPeakColor, sunDir );
				light.LightColor = color * MathX.Lerp( 0.2f, 1f, sunDir );
			}
			else
			{
				// Ambient lives on the sun light — moon only adds directional moonlight.
				light.SkyColor = new Color( 0f, 0f, 0f, 0f );
				var moonDir = aboveHorizon ? Math.Clamp( height, 0f, 1f ) : 0f;
				light.LightColor = MoonColor * (MoonDirectionalBrightness * moonDir);
			}
		}

		if ( disk is not null && disk.IsValid() )
		{
			disk.Enabled = diskVisible;
			var radius = Math.Max( 1000f, TerrainWorldUnits.MetersToEngine( DiskOrbitRadiusMeters ) );
			disk.WorldPosition = dir * radius;

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

	void UpdateSkyColor( CyclePhase phase, float dayWeight, float sunElev01 )
	{
		var sky = SampleSkyTint( phase, dayWeight, sunElev01 ).WithAlpha( 1f );

		foreach ( var cam in Scene.GetAllComponents<CameraComponent>() )
		{
			if ( cam is null || !cam.IsValid() )
				continue;
			cam.BackgroundColor = sky;
			cam.ClearFlags = ClearFlags.All;
		}
	}

	Color SampleSkyTint( CyclePhase phase, float dayWeight, float sunElev01 )
	{
		switch ( phase )
		{
			case CyclePhase.Day:
			{
				// Shared-ring elev is 0..2; only the above-horizon half (0..1) maps to day sky.
				var dayElev = sunElev01 <= 1f ? sunElev01 : Math.Clamp( 2f - sunElev01, 0f, 1f );
				var noon = MathF.Sin( dayElev * MathF.PI );
				return Color.Lerp( DaySkyTint, NoonSkyTint, noon );
			}
			case CyclePhase.Dusk:
			{
				if ( dayWeight > 0.5f )
					return Color.Lerp( DuskSkyTint, DaySkyTint, (dayWeight - 0.5f) * 2f );
				return Color.Lerp( NightSkyTint, DuskSkyTint, dayWeight * 2f );
			}
			case CyclePhase.Dawn:
			{
				if ( dayWeight < 0.5f )
					return Color.Lerp( NightSkyTint, DawnSkyTint, dayWeight * 2f );
				return Color.Lerp( DawnSkyTint, DaySkyTint, (dayWeight - 0.5f) * 2f );
			}
			default:
				return NightSkyTint;
		}
	}

	void UpdateStars( float dayWeight )
	{
		if ( StarField is null || !StarField.IsValid() )
			return;

		StarField.ShellRadiusMeters = Math.Max(
			ProceduralStarField.DefaultShellRadiusMeters,
			DiskOrbitRadiusMeters + 2000f );

		if ( StarField.StarCount < ProceduralStarField.DefaultStarCount )
			StarField.StarCount = ProceduralStarField.DefaultStarCount;

		var night = Math.Clamp( 1f - dayWeight, 0f, 1f );
		// Invisible through most of dusk/dawn; opacity only ramps once night is well underway.
		var fade = Smooth01( Math.Clamp( (night - 0.45f) / 0.55f, 0f, 1f ) );
		StarField.Visibility = fade;
	}

	static float Smooth01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t);
	}
}
