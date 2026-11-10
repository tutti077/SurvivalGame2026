using Sandbox.Rendering;

namespace Survival;

/// <summary>
/// The underwater view (Mark 2026-10-22: "the whole camera to have a blue hue when it goes under water, the same hue as
/// the water … bluer and bluer the deeper you go"). Each frame: is the scene camera below the sea / lake surface (waves
/// included, <see cref="SeaSurfaceHeightAt"/>) or a river's level? If so, a full-screen pass (<c>shaders/underwater.shader</c>)
/// runs before post-processing from a command list on the scene camera — it follows whichever camera is active, the
/// fly camera or the pawn's, so no prefab needs a component for it.
/// </summary>
public sealed partial class TerrainWorldManager
{
	const string UnderwaterShaderPath = "shaders/underwater.shader";
	/// <summary>
	/// Underwater visibility, metres: the same Beer-Lambert the water surfaces use, sized for a swimmer's view (6 m read as
	/// murk — Mark 2026-10-25: "view distance under water should be increased by quite a bit").
	/// </summary>
	const float UnderwaterClearMeters = 20f;
	/// <summary>How far daylight reaches down (m) before only the absorption colour is left: bluer and dimmer with depth.</summary>
	const float UnderwaterLightClearMeters = 40f;
	/// <summary>The water's absorption colour (seawater.vmat AbsorbTint): what light keeps after the clear distance.</summary>
	static readonly Vector3 UnderwaterTint = new( 0.35f, 0.55f, 0.62f );
	/// <summary>The caustic net on the bed, as seawater.vmat / riverwater.vmat draw it from above (Caustic Scale / Strength).</summary>
	const string UnderwaterCausticsPath = "materials/water/water_caustics.png";
	const float UnderwaterCausticMeters = 4.5f;
	const float UnderwaterCausticStrength = 0.8f;

	CommandList _underwaterCommands;
	CameraComponent _underwaterCamera;
	Material _underwaterMaterial;
	Texture _underwaterCaustics;
	readonly RenderAttributes _underwaterAttributes = new();
	DirectionalLight _underwaterSun;
	float _underwaterSunLookupAt = float.MinValue;

	void TickUnderwater()
	{
		var cam = Scene.Camera;
		_underwaterCommands ??= new CommandList( "Underwater" );
		if ( cam != _underwaterCamera )
		{
			if ( _underwaterCamera is not null && _underwaterCamera.IsValid() )
				_underwaterCamera.RemoveCommandList( _underwaterCommands );
			_underwaterCamera = null;
			if ( cam is not null && cam.IsValid() )
			{
				// After the far-tree ring (FarTrees, -100 on the same stage): drawn before it, the distant trees landed on top
				// of the finished underwater view and stood unfogged in the blue (Mark 2026-10-25)
				cam.AddCommandList( _underwaterCommands, Stage.BeforePostProcess, 1000 );
				_underwaterCamera = cam;
			}
		}
		_underwaterCommands.Reset();
		if ( cam is null || !cam.IsValid() || _loadSettings is null )
			return;

		var depth = CameraWaterDepthMeters( cam.WorldPosition );
		if ( depth <= -0.05f )
			return;

		_underwaterMaterial ??= Material.FromShader( UnderwaterShaderPath );
		if ( _underwaterMaterial is null )
			return;

		if ( (_underwaterSun is null || !_underwaterSun.IsValid()) && Time.Now - _underwaterSunLookupAt > 3f )
		{
			_underwaterSunLookupAt = Time.Now;
			_underwaterSun = Scene.GetAllComponents<DirectionalLight>().FirstOrDefault( l => l.Active );
		}
		var daylight = 1f;
		if ( _underwaterSun is not null && _underwaterSun.IsValid() )
		{
			var c = _underwaterSun.LightColor;
			daylight = Math.Clamp( ((c.r * 0.3f) + (c.g * 0.59f) + (c.b * 0.11f)) * 0.8f, 0.04f, 1.5f );
		}

		_underwaterAttributes.Set( "Underwater", new Vector4( Math.Max( 0f, depth ), Math.Clamp( (depth + 0.05f) / 0.25f, 0f, 1f ), UnderwaterClearMeters, daylight ) );
		_underwaterAttributes.Set( "UnderwaterTint", UnderwaterTint );
		_underwaterAttributes.Set( "UnderwaterLightClear", UnderwaterLightClearMeters );
		// The surface overhead (Snell's window is drawn at it) + the caustic net the surface shaders put on the bed
		var surfaceZ = cam.WorldPosition.z + TerrainWorldUnits.MetersToEngine( Math.Max( 0f, depth ) );
		_underwaterAttributes.Set( "UnderwaterSurface", new Vector4( surfaceZ, Time.Now, UnderwaterCausticMeters, UnderwaterCausticStrength ) );
		_underwaterCaustics ??= Texture.Load( UnderwaterCausticsPath );
		if ( _underwaterCaustics is not null )
			_underwaterAttributes.Set( "UnderwaterCaustics", _underwaterCaustics );
		_underwaterCommands.Attributes.GrabFrameTexture( "ColorBuffer" );
		_underwaterCommands.Blit( _underwaterMaterial, _underwaterAttributes );
	}

	/// <summary>
	/// Metres of water above a world point (engine units): the sea / lake surface with its waves, else a river's level
	/// where the point is inside a channel. Negative = above the water (or no water here).
	/// </summary>
	float CameraWaterDepthMeters( Vector3 worldPosition )
	{
		var z = TerrainWorldUnits.EngineToMeters( worldPosition.z );
		var best = float.MinValue;
		if ( SeaSurfaceHeightAt( worldPosition ) is { } seaZ )
			best = TerrainWorldUnits.EngineToMeters( seaZ ) - z;
		var settings = _loadSettings;
		if ( settings is not null && settings.EnableRivers )
		{
			var x = TerrainWorldUnits.EngineToMeters( worldPosition.x );
			var y = TerrainWorldUnits.EngineToMeters( worldPosition.y );
			var hit = TerrainPreviewLandDiskFields.SampleRiver( settings, x, y );
			if ( hit.Near && hit.Distance <= hit.HalfWidth )
				best = Math.Max( best, hit.Level + RiverSurfaceLiftMeters - z );
			var partner = TerrainPreviewLandDiskFields.SampleRiverPartner( settings, x, y, hit );
			if ( partner.Near && partner.Distance <= partner.HalfWidth )
				best = Math.Max( best, partner.Level + RiverSurfaceLiftMeters - z );
		}
		return best;
	}

	static TerrainWorldManager _waterProbeManager;
	static Scene _waterProbeScene;
	static float _waterProbeLookupAt = float.MinValue;

	/// <summary>
	/// Metres of water above a world point in the streamed world (sea, lakes, tarns, rivers), negative above the
	/// water. False when the scene has no terrain world (test scenes - see <see cref="WaterProbe"/> for the trigger
	/// fallback). The manager lookup is cached per scene and retried every 2 s.
	/// </summary>
	public static bool TryGetWaterDepthMeters( Scene scene, Vector3 worldPosition, out float depthMeters )
	{
		depthMeters = float.MinValue;
		if ( scene is null || !scene.IsValid() )
			return false;

		if ( _waterProbeScene != scene || _waterProbeManager is null || !_waterProbeManager.IsValid() )
		{
			if ( _waterProbeScene == scene && Time.Now - _waterProbeLookupAt < 2f )
				return false;

			_waterProbeScene = scene;
			_waterProbeLookupAt = Time.Now;
			_waterProbeManager = scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		}

		if ( _waterProbeManager is null || !_waterProbeManager.IsValid() || _waterProbeManager._loadSettings is null )
			return false;

		depthMeters = _waterProbeManager.CameraWaterDepthMeters( worldPosition );
		return true;
	}

	void DestroyUnderwater()
	{
		if ( _underwaterCamera is not null && _underwaterCamera.IsValid() && _underwaterCommands is not null )
			_underwaterCamera.RemoveCommandList( _underwaterCommands );
		_underwaterCamera = null;
	}
}
