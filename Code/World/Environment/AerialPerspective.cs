using System;
using System.Linq;
using Sandbox;
using Sandbox.Rendering;

namespace Survival;

/// <summary>
/// The fog between the camera and the world. Mark 2026-11-10: "make it like real fog and not just ruin the color scheme"
/// (the 2026-11-08 pass desaturated everything past 1 km but hazed only a tenth of it at 2 km, so the world went grey and
/// stayed sharp, and its 90 % cap left the caldera foot paler than the sky behind it, which read as see-through rock).
/// One full-screen pass (<c>shaders/environment/aerial_perspective.shader</c>) before post-processing on whichever camera
/// is live: exponential height fog, thick at sea level and thinning with altitude, no cap and no desaturation, whose
/// colour is the <see cref="SkyDome"/>'s own colour along each view ray (gradient, sun-side band, halo). A fully fogged
/// object is therefore exactly the sky behind it: the far world fades out instead of greying, the caldera foot melts into
/// the horizon and the high crests stand out of the fog. Depth is the engine's scene depth (the prepass chain):
/// a grabbed copy of the live depth buffer came back banded in the live viewport. The FarTrees ring is not in that depth,
/// so it is drawn after this pass and fogs itself in pixel_lit.shader with the same numbers (<see cref="TryGetFog"/>).
/// The translucent sea is not in that depth either: the shader clamps a downward ray at sea level (the <c>SeaFogPlane</c>
/// scene attribute from TerrainWorldManager.SeaWater), or the open sea was fogged at the floor behind it (2026-11-20 film).
/// <see cref="EnvironmentDayNightCycle"/> hands it the preset sky each frame via <see cref="Apply"/>; without a cycle, the
/// fog is <see cref="FogColor"/>.
/// On top of that world fog sits the air of the biome the camera stands in (Mark 2026-11-23: "it needs to be biome
/// specific": a light fogginess in Clover Hills, dark under the trees in Redwood Forest): a near mist whose colour turns
/// to the sun's toward the sun, a shade (<see cref="BiomeShade"/>: real sky ambient, not a screen dim), fog that keeps part
/// of a thing's own hue, and sun shafts, all from
/// <c>data/biome_atmosphere.json</c> (<see cref="BiomeAtmosphereCatalog"/>, <see cref="UpdateBiomeAir"/>). The sky is
/// never shaded or misted, so the gaps in the canopy stay bright. Console <c>atmosphere</c> / <c>atmosphere reload</c>.
/// </summary>
[Title( "Aerial Perspective (Fog)" )]
public sealed class AerialPerspective : Component
{
	const string ShaderPath = "shaders/environment/aerial_perspective.shader";

	[Property, Title( "Strength (0–1)" ), Range( 0f, 1f ), Description( "Overall amount; 0 switches the pass off." )]
	public float Strength01 { get; set; } = 1f;

	[Property, Group( "Fog" ), Title( "Fog Distance (m)" ), Description( "Sea-level distance at which 63 % of a thing is fog. 4500 m: a fifth at 1 km, two fifths at 2 km, nearly gone by 10 km at sea level." )]
	public float FogDistanceMeters { get; set; } = 4500f;

	[Property, Group( "Fog" ), Title( "Fog Start (m)" ), Range( 0f, 10000f ), Step( 10f ), Description( "Clear air: nothing closer than this is fogged at all, and the fog builds up slowly from there. 2500 since 2026-11-23 (Mark, from Enshrouded: 'they dont use fog until its like thousands of meters away'); was 100, then 300." )]
	public float FogStartMeters { get; set; } = 2500f;

	[Property, Group( "Fog" ), Title( "Fog Scale Height (m)" ), Description( "Altitude at which the air is 1/e as thick: valleys and the sea fog first, high ground and the caldera crests stand out of it." )]
	public float FogScaleHeightMeters { get; set; } = 1200f;

	[Property, Group( "Fog" ), Title( "Fog Colour (no cycle)" ), Description( "Used only when no day/night cycle drives this component; the cycle hands over the preset sky each frame." )]
	public Color FogColor { get; set; } = new( 0.72f, 0.76f, 0.84f, 1f );

	CommandList _commands;
	CameraComponent _camera;
	Material _material;
	readonly RenderAttributes _attributes = new();
	SkyDome _dome;
	float _domeLookupAt = float.MinValue;
	EnvironmentDayNightCycle _cycle;
	float _cycleLookupAt = float.MinValue;

	Vector3 _zenith = new( 0.72f, 0.76f, 0.84f );
	Vector3 _horizon = new( 0.72f, 0.76f, 0.84f );
	Vector4 _sunGlow = new( 1f, 0.9f, 0.7f, 0f );
	Vector3 _sunDirection = Vector3.Up;
	bool _applied;

	/// <summary>The live fog, for draws that fog themselves (see <see cref="TryGetFog"/>).</summary>
	static AerialPerspective _live;
	Vector4 _shape;
	float _startUnits;

	// The camera's biome air (data/biome_atmosphere.json): sampled a few times a second, eased every frame.
	const float BiomeSampleSeconds = 0.25f;
	TerrainWorldManager _terrain;
	float _terrainLookupAt = float.MinValue;
	float _biomeSampledAt = float.MinValue;
	BiomeAtmosphereMix _biomeTarget = BiomeAtmosphereMix.None;
	BiomeAtmosphereMix _biome = BiomeAtmosphereMix.None;
	bool _biomeSnap = true;
	Vector4 _mist;
	/// <summary>x = mist clear distance (engine units), y = mist cap, z = the ring's colour hold, w = 1 / mist distance.</summary>
	Vector4 _mistShape = new( 0f, 1f, 0f, 0f );
	Vector3 _mistSun;
	/// <summary>Falling snow 0-1 as drawn: the camera's biome share, eased over about a second.</summary>
	float _snow;

	/// <summary>
	/// How much sky light the camera's biome lets through (1 = all of it): <see cref="EnvironmentDayNightCycle"/> scales
	/// the sun light's ambient fill by it, so the dark under the Redwood canopy is real light and the sun's patches stay
	/// bright. 1 when no fog is live.
	/// </summary>
	/// <summary>What the live biome mist covers where it begins, for the far-tree ring's own fog.</summary>
	public static float BiomeMistFloor => _live is { } live && live.IsValid() && live.Active ? live._biome.MistFloor : 0f;

	public static float BiomeShade => _live is { } fog && fog.IsValid() && fog.Active ? fog._biome.Shade : 1f;

	/// <summary>
	/// This frame's fog numbers, for a draw that comes after the pass and is not in the scene depth (the far-tree ring):
	/// the attributes pixel_lit.shader's RingFog reads. False when no fog is live or its strength is 0.
	/// </summary>
	public static bool TryGetFog( out Vector4 shape, out Vector3 zenith, out Vector3 horizon, out Vector4 sunGlow, out Vector3 sunDirection, out Vector4 mist, out Vector4 mistShape, out Vector3 mistSun )
	{
		var fog = _live;
		if ( fog is null || !fog.IsValid() || !fog.Active || fog.Strength01 <= 0f )
		{
			shape = default;
			zenith = default;
			horizon = default;
			sunGlow = default;
			sunDirection = Vector3.Up;
			mist = default;
			mistShape = new Vector4( 0f, 1f, 0f, 0f );
			mistSun = default;
			return false;
		}

		// The ring has no sky cut (it is never sky): w carries the clear distance instead.
		shape = new Vector4( fog._shape.x, fog._shape.y, fog._shape.z, fog._startUnits );
		zenith = fog._zenith;
		horizon = fog._horizon;
		sunGlow = fog._sunGlow;
		sunDirection = fog._sunDirection;
		mist = fog._mist;
		mistShape = fog._mistShape;
		mistSun = fog._mistSun;
		return true;
	}

	/// <summary>The live biome air, as one line for the console (<c>atmosphere</c>).</summary>
	public static string DescribeBiomeAir()
	{
		var fog = _live;
		if ( fog is null || !fog.IsValid() )
			return "no AerialPerspective in the active scene";
		var b = fog._biome;
		var mist = b.InverseMistMeters > 0f ? $"mist 63 % at {1f / b.InverseMistMeters:0} m from {b.MistStartMeters:0} m, floor {b.MistFloor:0.00}, max {b.MistMax:0.00}, tint {b.MistTint.x:0.00} {b.MistTint.y:0.00} {b.MistTint.z:0.00}, sun side {b.MistSunTint.x:0.00} {b.MistSunTint.y:0.00} {b.MistSunTint.z:0.00}" : "no mist";
		return $"{mist}, snow {fog._snow:0.00}, shade (sky ambient) {b.Shade:0.00}, colour hold ground {BiomeAtmosphereCatalog.GroundColorHold:0.00} / trees {BiomeAtmosphereCatalog.TreeColorHold:0.00}, sun shafts {BiomeAtmosphereCatalog.SunShafts:0.00}, world fog max {BiomeAtmosphereCatalog.FogMax:0.00}{(fog._terrain.IsValid() ? "" : " (no terrain world: no biome air)")}";
	}

	[ConCmd( "atmosphere" )]
	public static void ConCmdAtmosphere( string action = "" )
	{
		if ( string.Equals( action, "reload", StringComparison.OrdinalIgnoreCase ) )
		{
			BiomeAtmosphereCatalog.Reload();
			if ( _live.IsValid() )
				_live._biomeSampledAt = float.MinValue;
			Log.Info( "[Atmosphere] reloaded data/biome_atmosphere.json" );
			return;
		}
		Log.Info( $"[Atmosphere] {DescribeBiomeAir()}" );
	}

	/// <summary>
	/// The air of the biome the camera stands in (<see cref="BiomeAtmosphereCatalog"/>): one terrain sample every
	/// <see cref="BiomeSampleSeconds"/>, eased over the catalog's blend time so a border or a teleport never pops.
	/// </summary>
	void UpdateBiomeAir( CameraComponent cam )
	{
		if ( !_terrain.IsValid() && Time.Now - _terrainLookupAt > 3f )
		{
			_terrainLookupAt = Time.Now;
			_terrain = Scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault( t => t.Active );
		}

		if ( Time.Now - _biomeSampledAt >= BiomeSampleSeconds )
		{
			_biomeSampledAt = Time.Now;
			var settings = _terrain.IsValid() ? _terrain.LoadSettings : null;
			if ( settings is null )
			{
				_biomeTarget = BiomeAtmosphereMix.None;
				_biomeSnap = true;
			}
			else
			{
				var at = TerrainWorldUnits.EngineToMeters( cam.WorldPosition );
				var sample = TerrainPreviewPipeline.Sample( settings, at.x, at.y );
				// Off the land (afloat, past the edge) the catalog gives the Clover Hills baseline
				_biomeTarget = BiomeAtmosphereCatalog.Mix(
					sample.IsInsideWorld ? TerrainPreviewBiomeResolver.SampleLandBiomeWeights( settings, sample, at.x, at.y ) : default,
					sample.IsInsideWorld ? at.z - sample.HeightMeters : 0f );
			}
		}

		var blend = BiomeAtmosphereCatalog.BlendSeconds;
		var t = _biomeSnap || blend <= 0f ? 1f : 1f - MathF.Exp( -Time.Delta * 3f / blend );
		_biome = BiomeAtmosphereMix.Lerp( _biome, _biomeTarget, t );
		if ( _terrain.IsValid() && _terrain.LoadSettings is not null )
			_biomeSnap = false;

		// Handed to the shaders as separate scalar attributes (OnPreRender, FarTrees): the density first rode in the tint's alpha
		// and did not arrive as set - no mist at all in the pass, ring trees at the mist cap (Mark 2026-11-23: "something broke").
		_mist = new Vector4( _biome.MistTint.x, _biome.MistTint.y, _biome.MistTint.z, BiomeAtmosphereCatalog.FogMax );   // w = the world fog's cap
		_mistSun = _biome.MistSunTint;
		_mistShape = new Vector4( TerrainWorldUnits.MetersToEngine( _biome.MistStartMeters ), _biome.MistMax, BiomeAtmosphereCatalog.TreeColorHold,
			_biome.InverseMistMeters > 0f ? _biome.InverseMistMeters / TerrainWorldUnits.MetersToEngine( 1f ) : 0f );
	}

	protected override void OnEnabled() => _live = this;

	/// <summary>Push this frame's sky look. Called by <see cref="EnvironmentDayNightCycle"/> next to <see cref="SkyDome.Apply"/>.</summary>
	public void Apply( Color zenith, Color horizon, Color sunGlow, Vector3 sunDirection )
	{
		_zenith = new Vector3( zenith.r, zenith.g, zenith.b );
		_horizon = new Vector3( horizon.r, horizon.g, horizon.b );
		_sunGlow = new Vector4( sunGlow.r, sunGlow.g, sunGlow.b, sunGlow.a );
		_sunDirection = sunDirection.Normal;
		_applied = true;
	}

	protected override void OnPreRender()
	{
		var cam = Scene.Camera;
		_commands ??= new CommandList( "AerialPerspective" );
		if ( cam != _camera )
		{
			Detach();
			if ( cam is not null && cam.IsValid() )
			{
				// BEFORE the far-tree ring (FarTrees, -100 on this stage): the ring is not in the scene depth this pass reads,
				// so it draws on top of the fogged frame and fogs itself. Before the underwater view (1000) too: under water
				// the fog is already in the frame it tints.
				cam.AddCommandList( _commands, Stage.BeforePostProcess, -200 );
				_camera = cam;
			}
		}
		_commands.Reset();
		if ( cam is null || !cam.IsValid() || Strength01 <= 0f )
			return;

		_material ??= Material.FromShader( ShaderPath );
		if ( _material is null )
			return;

		if ( !_applied )
		{
			_horizon = _zenith = new Vector3( FogColor.r, FogColor.g, FogColor.b );
			_sunGlow = Vector4.Zero;
		}

		// Everything at or past the sky dome is sky and keeps its colour; without a dome, the far plane is.
		if ( (_dome is null || !_dome.IsValid()) && Time.Now - _domeLookupAt > 3f )
		{
			_domeLookupAt = Time.Now;
			_dome = Scene.GetAllComponents<SkyDome>().FirstOrDefault( d => d.Active );
		}
		var skyCut = _dome is not null && _dome.IsValid()
			? TerrainWorldUnits.MetersToEngine( _dome.RadiusMeters ) * 0.95f
			: cam.ZFar * 0.9f;
		// The sun and moon stand inside the dome, round the camera: they are sky too, not far terrain to be fogged
		if ( (_cycle is null || !_cycle.IsValid()) && Time.Now - _cycleLookupAt > 3f )
		{
			_cycleLookupAt = Time.Now;
			_cycle = Scene.GetAllComponents<EnvironmentDayNightCycle>().FirstOrDefault( c => c.Active );
		}
		if ( _cycle is not null && _cycle.IsValid() )
			skyCut = Math.Min( skyCut, _cycle.DiskOrbitRadiusUnits * 0.9f );

		_shape = new Vector4(
			1f / TerrainWorldUnits.MetersToEngine( Math.Max( 100f, FogDistanceMeters ) ),
			1f / TerrainWorldUnits.MetersToEngine( Math.Max( 50f, FogScaleHeightMeters ) ),
			Math.Clamp( Strength01, 0f, 1f ),
			skyCut );
		_attributes.Set( "FogShape", _shape );
		_startUnits = TerrainWorldUnits.MetersToEngine( Math.Max( 0f, FogStartMeters ) );
		_attributes.Set( "FogStart", _startUnits );
		_attributes.Set( "FogZenith", _zenith );
		_attributes.Set( "FogHorizon", _horizon );
		_attributes.Set( "FogSunGlow", _sunGlow );
		_attributes.Set( "FogSunDirection", _sunDirection );

		UpdateBiomeAir( cam );
		_attributes.Set( "FogMistTint", new Vector3( _mist.x, _mist.y, _mist.z ) );
		_attributes.Set( "FogMistInverse", _mistShape.w );
		_attributes.Set( "FogMistStart", _mistShape.x );
		_attributes.Set( "FogMistMax", _mistShape.y );
		_attributes.Set( "FogMistFloor", _biome.MistFloor );
		_attributes.Set( "FogMistSunTint", _mistSun );
		_attributes.Set( "FogColorHold", BiomeAtmosphereCatalog.GroundColorHold );
		_attributes.Set( "FogMax", _mist.w );
		_attributes.Set( "FogTint", BiomeAtmosphereCatalog.FogTint );

		// Falling snow (the Ivory Alps): drawn by the pass in world-anchored layers, drifting with the wind
		var wind = WindSystem.Current;
		// The wind only slants the fall (0.15-0.65 m/s against 1.1 m/s down): at up to 3 m/s the snow blew flat and fast
		// (Mark 2026-11-23: "going like completely sideways and super fast").
		var windVelocity = wind is not null ? wind.Direction.WithZ( 0f ).Normal * TerrainWorldUnits.MetersToEngine( 0.15f + (0.5f * wind.Strength01) ) : Vector3.Zero;
		// Snow belongs to where the camera stands: it follows the biome's raw share, cut to on / off between 30 % and 60 %
		// of it, in about a second - not the 4 s ease of the air, on which it hung on long after the mountain was left
		// ("when we leave the snow sticks around for a way while longer").
		var snowShare = Math.Clamp( (_biomeTarget.Snow - 0.3f) / 0.3f, 0f, 1f );
		var snowTarget = snowShare * snowShare * (3f - (2f * snowShare));
		_snow = _biomeSnap ? snowTarget : MathX.Lerp( _snow, snowTarget, 1f - MathF.Exp( -Time.Delta * 3f ) );
		_attributes.Set( "FogSnow", _snow );


		_commands.Attributes.GrabFrameTexture( "ColorBuffer" );
		_commands.Blit( _material, _attributes );
	}

	protected override void OnDisabled()
	{
		if ( _live == this )
			_live = null;
		Detach();
	}

	protected override void OnDestroy() => Detach();

	void Detach()
	{
		if ( _camera is not null && _camera.IsValid() && _commands is not null )
			_camera.RemoveCommandList( _commands );
		_camera = null;
	}
}
