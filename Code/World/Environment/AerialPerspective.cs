using System;
using System.Linq;
using Sandbox;
using Sandbox.Rendering;

namespace Survival;

/// <summary>
/// The air between the camera and the world (Mark 2026-11-08: "things that are far away seem like they're far away …
/// colors become more white instead of being totally saturated … shouldn't make stuff impossible to see, just hard to
/// discern using colors"). One full-screen pass (<c>shaders/environment/aerial_perspective.shader</c>) before
/// post-processing on whichever camera is live — pawn or fly camera — from the scene depth, so every material, the
/// water, the far cards and the <see cref="CalderaRing"/> sit in the same atmosphere: colour first slides toward its own
/// luminance with distance, then toward the sky's horizon colour, through air that thins with altitude, and the haze is
/// capped so the far world is pale, never gone. It replaced the scene GradientFog (which hid everything past 12 km and
/// started only at 1 km) and the ring's private haze. <see cref="EnvironmentDayNightCycle"/> hands it the preset horizon /
/// sun glow each frame via <see cref="Apply"/>; without a cycle, <see cref="HazeColor"/> is used.
/// </summary>
[Title( "Aerial Perspective" )]
public sealed class AerialPerspective : Component
{
	const string ShaderPath = "shaders/environment/aerial_perspective.shader";

	[Property, Title( "Strength (0–1)" ), Range( 0f, 1f ), Description( "Overall amount; 0 switches the pass off." )]
	public float Strength01 { get; set; } = 1f;

	[Property, Group( "Desaturation" ), Title( "Desaturate Distance (m)" ), Description( "Sea-level distance over which colour loses 63 % of its saturation (toward its own brightness). 1500 m: half gone at 1 km, nearly all by 4 km." )]
	public float DesaturateDistanceMeters { get; set; } = 1500f;

	[Property, Group( "Desaturation" ), Title( "Max Desaturation (0–1)" ), Range( 0f, 1f ), Description( "Far things keep at least this much hue: 0.85 leaves a trace of the colour." )]
	public float MaxDesaturation01 { get; set; } = 0.85f;

	[Property, Group( "Haze" ), Title( "Haze Distance (m)" ), Description( "Sea-level distance over which colour slides 63 % toward the horizon colour. 20 km: a third hazed at 8 km (the far tier's edge), two thirds at the caldera foot." )]
	public float HazeDistanceMeters { get; set; } = 20000f;

	[Property, Group( "Haze" ), Title( "Haze Scale Height (m)" ), Description( "Altitude at which the air is 1/e as dense — valleys fill first, high ground and the caldera crest stay crisper." )]
	public float HazeScaleHeightMeters { get; set; } = 1000f;

	[Property, Group( "Haze" ), Title( "Max Haze (0–1)" ), Range( 0f, 1f ), Description( "The haze never hides more than this, so the far world is pale, never gone." )]
	public float MaxHaze01 { get; set; } = 0.9f;

	[Property, Group( "Haze" ), Title( "Sun Warmth (0–1)" ), Range( 0f, 1f ), Description( "How much of the preset's sun glow tints the haze on the sun's side (low sun behind the rim)." )]
	public float SunWarmth01 { get; set; } = 0.6f;

	[Property, Group( "Haze" ), Title( "Haze Colour (no cycle)" ), Description( "Used only when no day/night cycle drives this component; the cycle hands over the preset horizon each frame." )]
	public Color HazeColor { get; set; } = new( 0.72f, 0.76f, 0.84f, 1f );

	CommandList _commands;
	CameraComponent _camera;
	Material _material;
	readonly RenderAttributes _attributes = new();
	SkyDome _dome;
	float _domeLookupAt = float.MinValue;

	Vector3 _horizon = new( 0.72f, 0.76f, 0.84f );
	Vector4 _sunGlow = new( 1f, 0.9f, 0.7f, 0f );
	Vector3 _sunDirection = Vector3.Up;
	bool _applied;

	/// <summary>Push this frame's sky look. Called by <see cref="EnvironmentDayNightCycle"/> next to <see cref="SkyDome.Apply"/>.</summary>
	public void Apply( Color horizon, Color sunGlow, Vector3 sunDirection )
	{
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
				// After the far-tree ring (FarTrees, -100 on this stage) so the distant trees breathe the same air, before the
				// underwater view (1000): under water the haze is already in the frame it tints.
				cam.AddCommandList( _commands, Stage.BeforePostProcess, 500 );
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
			_horizon = new Vector3( HazeColor.r, HazeColor.g, HazeColor.b );

		// Everything at or past the sky dome is sky and keeps its colour; without a dome, the far plane is.
		if ( (_dome is null || !_dome.IsValid()) && Time.Now - _domeLookupAt > 3f )
		{
			_domeLookupAt = Time.Now;
			_dome = Scene.GetAllComponents<SkyDome>().FirstOrDefault( d => d.Active );
		}
		var skyCut = _dome is not null && _dome.IsValid()
			? TerrainWorldUnits.MetersToEngine( _dome.RadiusMeters ) * 0.95f
			: cam.ZFar * 0.9f;

		_attributes.Set( "AerialHaze", new Vector4(
			1f / TerrainWorldUnits.MetersToEngine( Math.Max( 100f, HazeDistanceMeters ) ),
			1f / TerrainWorldUnits.MetersToEngine( Math.Max( 50f, HazeScaleHeightMeters ) ),
			Math.Clamp( MaxHaze01, 0f, 1f ),
			skyCut ) );
		_attributes.Set( "AerialDesat", new Vector4(
			1f / TerrainWorldUnits.MetersToEngine( Math.Max( 50f, DesaturateDistanceMeters ) ),
			Math.Clamp( MaxDesaturation01, 0f, 1f ),
			Math.Clamp( SunWarmth01, 0f, 1f ),
			Math.Clamp( Strength01, 0f, 1f ) ) );
		_attributes.Set( "AerialHorizon", _horizon );
		_attributes.Set( "AerialSunGlow", _sunGlow );
		_attributes.Set( "AerialSunDirection", _sunDirection );

		_commands.Attributes.GrabFrameTexture( "ColorBuffer" );
		_commands.Blit( _material, _attributes );
	}

	protected override void OnDisabled() => Detach();
	protected override void OnDestroy() => Detach();

	void Detach()
	{
		if ( _camera is not null && _camera.IsValid() && _commands is not null )
			_camera.RemoveCommandList( _commands );
		_camera = null;
	}
}
