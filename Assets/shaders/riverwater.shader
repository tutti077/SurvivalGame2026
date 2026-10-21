//
// riverwater — the terrain's river surface, after Valheim (Mark 2026-10-21, with reference shots): looking down
// near the camera the water is CLEAR — sand and rocks on the bed — and toward the horizon it turns into a
// MIRROR of the sky: sky colour, clouds, the sun's glitter streak. White wake streaks ride the current and
// foam gathers along the banks. No tint colour of its own, no pattern lines.
//
//   what you see  : the riverbed through the water — the frame-buffer copy behind the surface, only faintly
//                   DARKENED by the path the light took through the water. The water depth comes from the
//                   mesh (vertex colour R = carved depth under the centre line, 0 at the banks), not from a
//                   depth buffer: the engine's depth chain only exists while a ScreenSpaceReflections pass is
//                   on the camera, and without it every depth read said "far" and the river went flat grey.
//   reflection    : the SkyDome's own sky, re-evaluated along the reflected ray — the same gradient, sun band
//                   and halo, and the same two cloud layers from sky_clouds.png on the same ceiling (the dome
//                   publishes its colours, cloud cover / scale / offset and sun direction scene-wide) — mixed
//                   in by full Schlick Fresnel (F0 0.02): nearly nothing straight down, the whole sky at a
//                   grazing angle. Nothing on the banks is mirrored (that would need a planar reflection).
//                   Without a dome: the scene's fog colour, else the camera background.
//   glint         : an explicit highlight of the dome's sun on the ripples (SunGlintPower / Strength); the
//                   standard shading model is NOT used — its indirect specular reaches 100 % at grazing angles
//                   no matter what, which read as an opaque grey-blue sheen
//   ripples       : a small normal wobble carried downstream at FlowSpeed. The along-river coordinate is metres
//                   modulo 64 (half-precision UVs), so every along-river frequency is a whole number of cycles
//                   per 64 m and the pattern is seamless at the wrap.
//   foam          : soft patches of the dome's tileable cloud noise drifting with the current, plus shore foam;
//                   white at the sky's brightness, so as dark as everything else at night
//
// Built by TerrainWorldManager.RiverWater.cs as ONE mesh for all loaded chunks (one frame-buffer copy).
//
HEADER
{
	Description = "Flowing river water (clear below, the dome's sky and clouds by Fresnel; depth from the mesh)";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
}

COMMON
{
	#define S_SPECULAR 1
	#define BLEND_MODE_ALREADY_SET 1
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	#if ( PROGRAM == VFX_PROGRAM_PS )
		bool bIsFrontface : SV_IsFrontFace;
	#endif
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		return FinalizeVertex( o );
	}
}

PS
{
	#define DEPTH_STATE_ALREADY_SET 1
	#define BLEND_MODE_ALREADY_SET 1
	#define S_TRANSLUCENT 1

	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	// Visible from below as well (a swimmer looks up through it)
	RenderState( CullMode, NONE );

	BoolAttribute( bWantsFBCopyTexture, true );
	Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;

	// The dome's cloud noise (sky_clouds.png: R = masses, G = wisps) — the same texture sky_dome.vmat uses
	CreateInputTexture2D( CloudNoise, Linear, 8, "", "", "Clouds,30/10", Default3( 0.5, 0.5, 0.0 ) );
	Texture2D g_tCloudNoise < Channel( RGBA, Box( CloudNoise ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	SamplerState g_sCloudSampler < Filter( BILINEAR ); AddressU( WRAP ); AddressV( WRAP ); >;

	// Must match TerrainWorldManager.RiverWater.cs (RiverRippleTileMeters / RiverDepthColorRangeMeters)
	#define RIVER_RIPPLE_TILE_METERS 64.0
	#define RIVER_DEPTH_COLOR_RANGE_METERS 8.0
	#define UNITS_PER_METER 40.0

	// ---- water body: no colour of its own, only how much the bed darkens with the path through the water
	// Light that travels ClearDistance (3200 u = 80 m) through the water keeps AbsorbTint of itself, per channel
	// (Beer-Lambert): a 4 m bed seen from above keeps ~97 %, a 30 m grazing path ~80 %
	float ClearDistance < Default( 3200.0 ); Range( 8.0, 8000.0 ); UiGroup( "Water,10/10" ); >;
	float3 AbsorbTint < UiType( Color ); Default3( 0.78, 0.86, 0.88 ); UiGroup( "Water,10/20" ); >;
	// How much of the sky's light the water itself scatters back where the absorption took the bed away
	// (Mark 2026-10-21: "white hue … I want it CLEAR like Valheim" — keep this near zero)
	float InScatter < Default( 0.05 ); Range( 0.0, 1.0 ); UiGroup( "Water,10/25" ); >;
	float RefractionStrength < Default( 0.20 ); Range( 0.0, 1.0 ); UiGroup( "Water,10/40" ); >;

	// ---- reflection: the dome's sky only
	// 1.0 = true Fresnel: 2 % straight down, the whole sky at a grazing angle (Valheim: clear at your feet, a mirror
	// toward the far bank). Lower it only if the distance ever reads as a sheet.
	float ReflectionStrength < Default( 1.0 ); Range( 0.0, 2.0 ); UiGroup( "Reflection,15/10" ); >;
	// Fresnel at normal incidence: water is 0.02 — nearly all bed when you look down
	float FresnelF0 < Default( 0.02 ); Range( 0.0, 0.3 ); UiGroup( "Reflection,15/20" ); >;
	// The sun's glitter on the ripples (the standard shading model is not used: its indirect specular went to 100 %
	// at grazing angles whatever the water asked for, a grey-blue sheen Mark could not see through)
	float SunGlintStrength < Default( 2.0 ); Range( 0.0, 8.0 ); UiGroup( "Reflection,15/52" ); >;
	float SunGlintPower < Default( 300.0 ); Range( 8.0, 2000.0 ); UiGroup( "Reflection,15/54" ); >;
	// Last-resort sky when neither a SkyDome nor gradient fog is in the scene: terrainTest's camera background
	float3 NoDomeSkyColor < UiType( Color ); Default3( 0.333, 0.463, 0.522 ); UiGroup( "Reflection,15/60" ); >;

	// Published scene-wide every frame by SkyDome (same names as sky_dome.shader): the sky the dome is drawing
	// right now. SkyDomeActive is 0 until a dome has published once.
	float3 g_vSkyZenith < Attribute( "SkyZenith" ); Default3( 0.25, 0.45, 0.85 ); >;
	float3 g_vSkyHorizon < Attribute( "SkyHorizon" ); Default3( 0.70, 0.80, 0.95 ); >;
	float4 g_vSkySunGlow < Attribute( "SkySunGlow" ); Default4( 1.0, 0.9, 0.7, 0.5 ); >;
	float3 g_vCloudLit < Attribute( "CloudLit" ); Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vCloudShade < Attribute( "CloudShade" ); Default3( 0.6, 0.65, 0.75 ); >;
	float g_flCloudCover < Attribute( "CloudCover" ); Default( 0.4 ); >;
	float g_flCloudScale < Attribute( "CloudScale" ); Default( 0.35 ); >;
	float2 g_vCloudOffset < Attribute( "CloudOffset" ); Default2( 0.0, 0.0 ); >;
	float3 g_vSunDirection < Attribute( "SunDirection" ); Default3( 0.0, 0.0, 1.0 ); >;
	float g_flSkyDomeActive < Attribute( "SkyDomeActive" ); Default( 0.0 ); >;

	// ---- flow
	float FlowSpeed < Default( 1.5 ); Range( 0.0, 10.0 ); UiGroup( "Flow,20/10" ); >;   // metres per second
	float RippleScale < Default( 2.5 ); Range( 0.25, 40.0 ); UiGroup( "Flow,20/20" ); >; // metres, across the river
	float RippleStrength < Default( 0.03 ); Range( 0.0, 1.0 ); UiGroup( "Flow,20/40" ); >;

	// ---- foam (Mark 2026-10-21: Valheim — soft white patches drifting with the current, foam along the shore;
	// the earlier long thin value-noise streaks read as "garbage flow lines"). The mask is the dome's tileable
	// cloud noise in metres, so patches are organic blobs, never bars or bands.
	float FoamStrength < Default( 0.35 ); Range( 0.0, 1.0 ); UiGroup( "Foam,25/10" ); >;
	// Size of one noise tile in metres along the river; must divide 64 m so the along-river wrap is seamless
	float FoamScaleMeters < Default( 8.0 ); Range( 1.0, 64.0 ); UiGroup( "Foam,25/20" ); >;
	// How much of the surface carries patches (threshold on the noise): lower = more foam
	float FoamThreshold < Default( 0.62 ); Range( 0.2, 0.98 ); UiGroup( "Foam,25/30" ); >;
	float EdgeFoamStrength < Default( 0.6 ); Range( 0.0, 1.0 ); UiGroup( "Foam,25/40" ); >;
	// Shore foam reaches this far in from the bank, as a fraction of the water depth range (depth01 < this)
	float EdgeFoamDepth01 < Default( 0.10 ); Range( 0.01, 0.5 ); UiGroup( "Foam,25/50" ); >;

	// Small ripples carried downstream. Along-river frequencies are whole cycles per RIVER_RIPPLE_TILE_METERS.
	float3 GetFlowNormal( float2 flowMeters, float3 baseNormal )
	{
		float t = g_flTime;
		float phase = (flowMeters.x - t * FlowSpeed) * (6.2831853 / RIVER_RIPPLE_TILE_METERS);
		float across = flowMeters.y / RippleScale;

		// Three incommensurate directions so the field never settles into rows or lines
		float nx = sin( phase * 8.0 + across * 2.1 ) + 0.5 * sin( phase * 17.0 - across * 4.3 + t * 0.9 ) + 0.35 * sin( phase * 5.0 + across * 9.1 - t * 0.4 );
		float ny = cos( across * 6.28 - phase * 4.0 ) + 0.5 * cos( across * 11.7 + phase * 9.0 - t * 0.6 ) + 0.35 * cos( across * 4.9 - phase * 21.0 + t * 0.5 );

		return normalize( baseNormal + float3( nx, ny, 0 ) * RippleStrength );
	}

	// 0..1 foam coverage: soft patches of the tileable cloud noise drifting with the current (a slower, larger
	// second sample gates them into sparse groups), plus foam along the banks where the water is shallow. Both
	// samples tile a whole number of times per 64 m along the river, so the UV wrap never shows.
	float GetFoam( float2 flowMeters, float depth01 )
	{
		float t = g_flTime;
		float scale = max( 1.0, RIVER_RIPPLE_TILE_METERS / round( RIVER_RIPPLE_TILE_METERS / FoamScaleMeters ) );
		float along = flowMeters.x - t * FlowSpeed * 0.8;   // foam rides a little slower than the ripples
		float2 uv = float2( along, flowMeters.y ) / scale;

		float n = g_tCloudNoise.Sample( g_sCloudSampler, uv ).r;
		float gate = g_tCloudNoise.Sample( g_sCloudSampler, uv * 0.25 + float2( 0.13, 0.71 ) ).g;
		// A wide band: a narrow one on the 8-bit noise drew every patch with a contour-line outline
		float patches = smoothstep( FoamThreshold, FoamThreshold + 0.32, n * (0.55 + 0.45 * gate) );

		// Shore foam: the shallow strip along each edge, broken up by the same noise
		float shore = smoothstep( EdgeFoamDepth01, 0.0, depth01 ) * EdgeFoamStrength;
		float shoreFoam = shore * smoothstep( 0.3, 0.85, n );

		return saturate( patches * FoamStrength + shoreFoam );
	}

	// Density of both cloud layers at a ceiling uv — identical to sky_dome.shader so the mirrored clouds are the
	// clouds overhead: masses carry the shape, wisps break the edges up.
	float CloudDensity( float2 uv )
	{
		float masses = g_tCloudNoise.Sample( g_sCloudSampler, uv ).r;
		float wisps = g_tCloudNoise.Sample( g_sCloudSampler, uv * 2.3 + g_vCloudOffset * 0.6 + 0.37 ).g;
		return masses * 0.75 + wisps * 0.35;
	}

	// The sky along a direction, as sky_dome.shader draws it (gradient, sun-side band, halo, clouds; no stars):
	// what the water mirrors. Without a dome, the scene's fog colour (the haze the player already sees); without
	// either, the camera background.
	float3 SkyReflection( float3 dir )
	{
		if ( g_flSkyDomeActive < 0.5 )
			return g_bGradientFogEnabled ? g_vGradientFogColor_Opacity.rgb : NoDomeSkyColor;

		float h = dir.z;
		float3 sunDir = normalize( g_vSunDirection );

		// Gradient: horizon colour hugs the rim, zenith fills the top.
		float3 sky = lerp( g_vSkyHorizon, g_vSkyZenith, pow( saturate( h ), 0.5 ) );

		// Warm band along the horizon on the sun's side (dawn / dusk does most of its work here).
		float2 flatDir = normalize( dir.xy + 1e-4 );
		float2 flatSun = normalize( sunDir.xy + 1e-4 );
		float sunSide = pow( saturate( dot( flatDir, flatSun ) * 0.5 + 0.5 ), 4.0 );
		float band = 1.0 - saturate( abs( h ) * 4.0 );
		sky = lerp( sky, g_vSkySunGlow.rgb, saturate( g_vSkySunGlow.a * sunSide * band ) );

		// Halo round the sun itself.
		float sunDot = saturate( dot( dir, sunDir ) );
		sky += g_vSkySunGlow.rgb * g_vSkySunGlow.a * ( pow( sunDot, 48.0 ) * 0.8 + pow( sunDot, 6.0 ) * 0.15 );

		// Below the horizon (a ripple can tilt the reflected ray down): darken toward the ground.
		sky = lerp( sky, g_vSkyHorizon * 0.55, saturate( -h * 4.0 ) );

		// Clouds on a flat ceiling: uv = direction projected through the ceiling plane.
		float ceilingH = max( h, 0.0 ) + 0.08;
		float2 uv = dir.xy / ceilingH * g_flCloudScale + g_vCloudOffset;
		float density = CloudDensity( uv );

		float cover = saturate( g_flCloudCover );
		float edge = 1.0 - cover;
		float cloud = smoothstep( edge, edge + 0.22, density );

		// Fake self-shadow: density a little toward the sun darkens the near side.
		float towardSun = CloudDensity( uv + flatSun * 0.035 );
		float lightAmt = saturate( 1.0 - ( towardSun - edge ) * 2.5 );
		float3 cloudColor = lerp( g_vCloudShade, g_vCloudLit, lightAmt );

		// Silver lining: thin cloud edges near the sun pick up the glow.
		cloudColor += g_vSkySunGlow.rgb * g_vSkySunGlow.a * pow( sunDot, 8.0 ) * ( 1.0 - cloud ) * 0.8;

		float horizonFade = smoothstep( 0.0, 0.22, h );
		sky = lerp( sky, cloudColor, cloud * horizonFade );

		return sky;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 worldPos = i.vPositionWithOffsetWs.xyz + g_vCameraPositionWs;

		float3 baseNormal = normalize( i.vNormalWs );
		if ( !i.bIsFrontface )
			baseNormal = -baseNormal;

		float3 normal = GetFlowNormal( i.vTextureCoords.xy, baseNormal );

		bool bOrtho = g_matViewToProjection[3].w != 0;
		float3 vViewRayWs = bOrtho ? g_vCameraDirWs : normalize( i.vPositionWithOffsetWs.xyz );

		// ---- the bed through the water. Depth under this point from the mesh; the path the light took through
		// the water is that depth stretched by the view angle (capped: a grazing view is not infinite water)
		float waterDepthU = i.vVertexColor.r * RIVER_DEPTH_COLOR_RANGE_METERS * UNITS_PER_METER;
		float flNDotVFlat = max( dot( baseNormal, -vViewRayWs ), 0.08 );
		float flPathU = min( waterDepthU / flNDotVFlat, 2400.0 );

		// Ripple-distorted screen UV; distortion eases out in shallow water so the banks don't smear
		float2 uv = i.vPositionSs.xy * g_vInvViewportSize;
		float2 uvR = uv + normal.xy * RefractionStrength * 0.05 * saturate( waterDepthU / 32.0 );
		float3 sceneColor = g_tFrameBufferCopyTexture.SampleLevel( g_sTrilinearMirror, uvR * g_vFrameBufferCopyInvSizeAndUvScale.zw, 0 ).rgb;

		// Beer-Lambert per channel; what the absorption took is replaced by the sky's light scattered inside the
		// water (the same sky the surface reflects, dimmed) — never black, never a colour of its own
		float3 transmittance = pow( max( AbsorbTint, 0.001 ), flPathU / ClearDistance );
		float3 skyUp = SkyReflection( float3( 0, 0, 1 ) );
		float3 seen = lerp( skyUp * InScatter, sceneColor, transmittance );

		// ---- reflection: the dome's sky along the reflected ray, nothing else
		float3 vReflectWs = reflect( vViewRayWs, normal );
		float3 reflection = SkyReflection( vReflectWs );

		// ---- Fresnel: straight down it is the bed you see, at a grazing angle the sky
		float flNDotV = saturate( dot( normal, -vViewRayWs ) );
		float fresnel = FresnelF0 + ((1.0 - FresnelF0) * pow( 1.0 - flNDotV, 5.0 ));
		fresnel = saturate( fresnel * ReflectionStrength );

		float3 color = lerp( seen, reflection, fresnel );

		// Foam patches and shore foam over the top. Foam is white at the sky's BRIGHTNESS (not its colour — tinted by
		// the zenith it came out blue), so at night it is as dark as everything else
		float foam = GetFoam( i.vTextureCoords.xy, i.vVertexColor.r );
		float skyLum = dot( skyUp, float3( 0.30, 0.59, 0.11 ) );
		float3 foamColor = saturate( lerp( skyLum.xxx, skyUp, 0.2 ) * 1.2 );
		color = lerp( color, foamColor, foam );

		// Sun glitter: the dome's sun mirrored on the ripples, scaled by the same Fresnel so it belongs to the surface
		if ( g_flSkyDomeActive > 0.5 )
		{
			float3 sun = normalize( g_vSunDirection );
			float glint = pow( saturate( dot( vReflectWs, sun ) ), SunGlintPower );
			color += g_vSkySunGlow.rgb * (glint * SunGlintStrength * max( fresnel, 0.05 ));
		}

		float4 output = float4( Fog::Apply( worldPos, i.vPositionSs.xy, color ), 1.0f );
		return output;
	}
}
