//
// seawater — the rim ocean and every lake (Mark 2026-10-22: "have our ocean water do the same thing as the river … big
// ass waves out deep that rock the boat, less near the shore, and at the shore just a little wake and white bubbles").
// All open water sits at Sea Level, and all dry land stands above it, so ONE surface at sea level serves the whole world:
// the terrain hides it wherever there is ground. The look is the river's (water_common.hlsl): clear over the bed,
// the dome's sky by Fresnel, a GGX glint per light, no sheen in the shade, swimmer rings.
//
//   mesh   : a radial grid that follows the camera (TerrainWorldManager.SeaWater.cs) — dense underfoot, coarse at the
//            horizon, no seams. Each wave fades out where the grid gets too coarse to carry it.
//   depth  : recomputed here exactly as the terrain carve does it (TerrainPreviewPipeline LakeFloorMeters /
//            SampleRimOcean): the lake mask raster (SeaLakeMask) past the shore threshold for lakes, the shelf past the
//            land edge for the ocean. Shore foam, wave height and clarity all come from it.
//   waves  : sea_waves.hlsl — 14 swells on the travel field (crests refract to the beach, shoal, break at 0.39 x depth)
//            and 10 chop waves from the live wind; SeaWaves.cs evaluates the same sum on the CPU for things that float.
//   shore  : laps timed from the arriving crests running up the sand, breaking white water, a lacy waterline.
//
HEADER
{
	Description = "Ocean and lake water at sea level (Gerstner waves by depth and weather, the river's look)";
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
	// Depth, swell travel field, Gerstner sum, shore laps — shared with the foam memory pass (SeaWaves.cs mirrors it)
	#include "sea_waves.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	// xy = undisplaced position (m), z = water depth (m), w = openness
	float4 vSea : TEXCOORD8;

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
		// The mesh is raised WATER_STENCIL_LIFT_UNITS so the engine's raw-geometry refraction stencil never z-fights a
		// shallow bed (TerrainWorldManager.SeaWater WaterStencilLiftMeters); back down to the real surface here
		float3 ws = o.vPositionWs.xyz - float3( 0, 0, WATER_STENCIL_LIFT_UNITS );
		float2 p = ws.xy / SEA_UNITS_PER_METER;
		// A tarn on a mountain top: the sheet rides up to its level there (TerrainPreviewMountainLakes)
		ws.z += SeaLakeLevelLiftMeters( p ) * SEA_UNITS_PER_METER;
		float openness;
		float depth = SeaDepthMeters( p, openness );
		float camDist = length( ws.xy - g_vCameraPositionWs.xy ) / SEA_UNITS_PER_METER;
		// The radial grid's spacing grows with the distance from the camera (≈ 2π r / segments)
		float spacing = max( 0.5, camDist * 0.0123 );
		float3 n;
		float folding;
		float breaking;
		float3 disp = SeaWaveSum( p, depth, openness, spacing, n, folding, breaking );
		ws += disp * SEA_UNITS_PER_METER;
		// The shore: breakers rolling in over the shallows, laps running up the sand. Within the run-up reach the land
		// keeps the surface (the terrain hides it where the ground is higher); further in the grid ducks under the ground
		// (lying a few centimetres under it, it z-fought the sand)
		float energy = SeaShoreEnergy( p );
		float t = g_vSeaClock.x;
		if ( depth > 0.0 )
		{
			if ( depth < 6.0 )
			{
				float x = SeaShoreX( p );
				float crest;
				ws.z += (SeaShoreLaps( p, x, energy, t ).x + SeaBreakers( p, x, depth, energy, t, crest )) * SEA_UNITS_PER_METER;
			}
		}
		else
		{
			float land = SeaLandMeters( p );
			float reach = g_vSeaShore.w * clamp( energy, 0.3, 1.5 ) + 2.0;
			if ( land < reach )
			{
				float lap = SeaShoreLaps( p, -land, energy, t ).x;
				// No lap here: sink a little from a metre in, so a resting surface never z-fights the wet sand
				ws.z += (lap - 0.4 * smoothstep( 1.0, 3.0, land ) * (1.0 - smoothstep( 0.0, 0.08, lap ))) * SEA_UNITS_PER_METER;
			}
			else
				ws.z -= 0.6 * SEA_UNITS_PER_METER * saturate( (land - reach) / max( spacing, 1.0 ) + 0.7 );
		}
		o.vPositionWs.xyz = ws;
		// ProcessVertex already projected the flat grid: project the displaced point (as foliage.shader does). The
		// projected point is pulled 0.2 % of its distance toward the camera — same pixel, nearer depth — so a bed a few
		// decimetres under the surface cannot z-fight it far away (stripes across a shallow lake seen from a hill)
		o.vPositionPs.xyzw = Position3WsToPs( ws + (g_vCameraPositionWs - ws) * 0.002 );
		o.vSea = float4( p, depth, openness );
		return FinalizeVertex( o );
	}
}

PS
{
	#define DEPTH_STATE_ALREADY_SET 1
	// Translucent water never writes depth: the river strip and the lake / sea surface lie at the same level, and whichever
	// drew first hid the other by depth wherever it was nearer by a hair — where the river faded out at a mouth only the
	// bed was left (2026-10-24, sand bands across river mouths)
	RenderState( DepthWriteEnable, false );
	#define BLEND_MODE_ALREADY_SET 1
	#define S_TRANSLUCENT 1

	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );

	// The frame behind the water, grabbed right before this draw (TerrainWorldManager.SeaWater WaterGrabCommands) —
	// the engine's stencil-masked refraction copy left rows uncopied
	Texture2D g_tFrameBufferCopyTexture < Attribute( "WaterSceneColor" ); SrgbRead( false ); >;

	// One tap of the frame-buffer copy, and whether it holds a colour. The engine copies only the pixels its
	// "Refraction Stencil Layer" pass marks, and over shallow beds that pass leaves thin rows unmarked: those come back
	// exactly black (2026-10-22, seen with the raw-copy debug view as black stripes across shallow lakes).
	float4 CopyTap( float2 uv )
	{
		float3 c = g_tFrameBufferCopyTexture.SampleLevel( g_sTrilinearMirror, uv * float2( 1.0, 1.0 ), 0 ).rgb;
		return float4( c, dot( c, float3( 1, 1, 1 ) ) > 1e-5 ? 1.0 : 0.0 );
	}

	CreateInputTexture2D( RippleSlopes, Linear, 8, "", "", "Ripples,20/5", Default4( 0.5, 0.5, 0.5, 0.5 ) );
	Texture2D g_tRipples < Channel( RGBA, Box( RippleSlopes ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	SamplerState g_sRippleSampler < Filter( ANISOTROPIC ); MaxAniso( 8 ); AddressU( WRAP ); AddressV( WRAP ); >;

	#define UNITS_PER_METER 40.0

	// ---- water body (as the river: no colour of its own; the bed darkens with the path through the water)
	float ClearDistance < Default( 120.0 ); Range( 8.0, 8000.0 ); UiGroup( "Water,10/10" ); >;
	float3 AbsorbTint < UiType( Color ); Default3( 0.35, 0.55, 0.62 ); UiGroup( "Water,10/20" ); >;
	// Deep water fills with the sky's light (and the sun's) — the ocean's blue is the sky's
	float InScatter < Default( 0.12 ); Range( 0.0, 1.0 ); UiGroup( "Water,10/25" ); >;
	float SunInScatter < Default( 0.03 ); Range( 0.0, 0.5 ); UiGroup( "Water,10/27" ); >;
	float RefractionStrength < Default( 0.25 ); Range( 0.0, 1.0 ); UiGroup( "Water,10/40" ); >;
	float BedBlurPixels < Default( 6.0 ); Range( 0.0, 24.0 ); UiGroup( "Water,10/50" ); >;
	float BedBlurDistanceMeters < Default( 6.0 ); Range( 0.5, 40.0 ); UiGroup( "Water,10/52" ); >;

	// ---- reflection / lights
	float ReflectionStrength < Default( 1.0 ); Range( 0.0, 2.0 ); UiGroup( "Reflection,15/10" ); >;
	float FresnelF0 < Default( 0.02 ); Range( 0.0, 0.3 ); UiGroup( "Reflection,15/20" ); >;
	float SurfaceRoughness < Default( 0.06 ); Range( 0.01, 0.5 ); UiGroup( "Lights,17/10" ); >;
	float DistanceRoughness < Default( 0.5 ); Range( 0.0, 4.0 ); UiGroup( "Lights,17/20" ); >;
	float SpecularStrength < Default( 1.0 ); Range( 0.0, 4.0 ); UiGroup( "Lights,17/30" ); >;
	float ShadeReflection < Default( 0.3 ); Range( 0.0, 1.0 ); UiGroup( "Lights,17/40" ); >;
	// Reflection roughness gained per metre of pixel footprint (distant water's sub-pixel ripples blur the mirror)
	float DistanceReflectionBlur < Default( 0.45 ); Range( 0.0, 1.0 ); UiGroup( "Reflection,15/30" ); >;
	// The water fades in over this much depth at the waterline (soft edge against the terrain)
	float EdgeSoftnessMeters < Default( 0.25 ); Range( 0.01, 2.0 ); UiGroup( "Water,10/60" ); >;

	// ---- small ripples on top of the waves, drifting downwind
	float RippleStrength < Default( 1.0 ); Range( 0.0, 4.0 ); UiGroup( "Ripples,20/10" ); >;
	float RippleDrift < Default( 0.6 ); Range( 0.0, 4.0 ); UiGroup( "Ripples,20/20" ); >;   // m/s at full wind
	// How far the ripples have drifted so far (TerrainWorldManager.SeaWater, integrated each frame from the wind)
	float4 g_vSeaRippleTravel < Attribute( "SeaRippleTravel" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// ---- foam
	float ShoreFoamStrength < Default( 0.65 ); Range( 0.0, 1.0 ); UiGroup( "Foam,25/20" ); >;
	// Whitecaps where crests fold over (rough / stormy open water)
	float WhitecapStrength < Default( 0.7 ); Range( 0.0, 1.0 ); UiGroup( "Foam,25/30" ); >;
	float WhitecapThreshold < Default( 0.45 ); Range( 0.0, 1.0 ); UiGroup( "Foam,25/40" ); >;
	// Foam remembered from the last seconds (TerrainWorldManager.SeaFoamMemory): breaking crests and lap trails linger
	float MemoryFoamStrength < Default( 0.6 ); Range( 0.0, 1.5 ); UiGroup( "Foam,25/50" ); >;
	Texture2D g_tSeaFoamMemory < Attribute( "SeaFoamMemory" ); SrgbRead( false ); >;
	SamplerState g_sFoamMemorySampler < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	// min x, min y, size (engine units), live
	float4 g_vSeaFoamRect < Attribute( "SeaFoamRect" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float DebugView < Default( 0.0 ); Range( 0.0, 10.0 ); UiGroup( "Debug,99/10" ); >;

	// Sky reflection, light glint, swimmer rings / wakes — shared with riverwater.shader
	#include "water_common.hlsl"

	// Sun visibility at a point of the water: the cascades only. GetVisibility also multiplies in the screen-space
	// contact-shadow mask, which is computed from the depth buffer — the bed or the sea floor behind the water, not the
	// surface — and read the open sea as shadowed past a straight line (2026-10-22: the sheen went, the far sea dark).
	float WaterShadow( float3 pos, float3 receiverNormal, float4 vPositionSs )
	{
		if ( g_DirectionalLightCascadeCount == 0 )
			return 1.0;
		float3 posLs;
		int cascade = FindCascade( pos, posLs );
		if ( cascade < 0 )
			return 1.0;
		return DirectionalLightShadow::SampleCascade( cascade, pos, receiverNormal, vPositionSs.xy );
	}

	// One shadow tap of the water column (sheen loss in the shade): the tap is moved up the sun ray to 1 m above the
	// surface, so only what stands above the water (banks, trees) can shade it.
	float WaterColumnTap( float3 surfacePos, float3 tapOffset, float3 Ls, float3 receiverNormal, float4 vPositionSs )
	{
		float below = max( -tapOffset.z, 0.0 );
		float3 pos = surfacePos + tapOffset + Ls * ((below + 40.0) / max( Ls.z, 0.2 ));
		return WaterShadow( pos, receiverNormal, vPositionSs );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 worldPos = i.vPositionWithOffsetWs.xyz + g_vCameraPositionWs;
		float3 lightPos = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float2 p = i.vSea.xy;

		// ---- derivative-based work first
		float3 vReceiverNormalWs = ComputeShadowReceiverNormal( lightPos );
		float footprintMeters = max( length( ddx( worldPos ) ), length( ddy( worldPos ) ) ) / UNITS_PER_METER;

		// Depth per pixel (the vertex value is too coarse for the shoreline), then the waves' exact normal here
		float openness;
		float depth = SeaDepthMeters( p, openness );
		// Water only where a lake or the ocean is, and the run-up strip just beyond it (the laps running up the sand; the
		// terrain hides whatever the ground stands above): land that happens to dip below sea level elsewhere (a river cut,
		// a ledge foot) stays dry - the surface showed there as dark foamy strips beside the lakes (2026-10-22)
		float shoreEnergy = SeaShoreEnergy( p );
		float shoreX;
		if ( depth <= 0.0 )
		{
			float land = SeaLandMeters( p );
			if ( land > g_vSeaShore.w * clamp( shoreEnergy, 0.3, 1.5 ) + 2.0 )
				discard;
			shoreX = -land;
			depth = 0.02;
		}
		else
			shoreX = SeaShoreX( p );
		float shoreDistance = max( shoreX, 0.0 );
		float3 waveNormal;
		float folding;
		float breaking;
		// A wave is drawn in the normal only while it spans many pixels: short chop fades out well before it could read
		// as regular parallel bands across distant water (the sub-pixel rest is the reflection blur's job)
		SeaWaveSum( p, depth, openness, max( footprintMeters * 4.0, 0.05 ), waveNormal, folding, breaking );

		// Fine ripples drifting downwind on top (two scales, world metres)
		float2 wind = normalize( g_vSeaWindDirection.xy + float2( 1e-4, 0.0 ) );
		float windAmt = saturate( g_flSeaWindStrength + g_flSeaWindGust * 0.5 );
		// Integrated on the CPU: time × a gust-dependent speed jumped the pattern back and forth whenever a gust changed
		float2 drift = g_vSeaRippleTravel.xy * RippleDrift;
		// No visible tiling (Mark 2026-10-22: "a repeating texture on the water"): every layer has its own rotation and
		// an off-ratio scale, all of them are warped by a slow noise field so no tile matches its neighbour, and a very
		// large wind-slick pattern varies the roughness itself — glassy patches beside rough ones, as on real water
		float2 warp = (g_tCloudNoise.Sample( g_sCloudSampler, p / 173.0 + 0.31 ).rg - 0.5) * 9.0;
		float2 pw = p - drift + warp;
		float2x2 rot1 = float2x2( 0.878, -0.479, 0.479, 0.878 );    // 0.5 rad
		float2x2 rot2 = float2x2( 0.622, 0.783, -0.783, 0.622 );    // -0.9 rad
		float2x2 rot3 = float2x2( -0.505, -0.863, 0.863, -0.505 );  // 2.1 rad
		float2 r1 = mul( rot1, g_tRipples.Sample( g_sRippleSampler, mul( rot1, pw ) / 3.1 ).rg * 2.0 - 1.0 );
		float2 r2 = mul( rot2, g_tRipples.Sample( g_sRippleSampler, mul( rot2, pw + drift * 0.3 ) / 11.3 + 0.37 ).ba * 2.0 - 1.0 );
		float2 r3 = mul( rot3, g_tRipples.Sample( g_sRippleSampler, mul( rot3, pw ) / 41.7 + 0.71 ).rg * 2.0 - 1.0 );
		float slick = smoothstep( 0.2, 0.8, g_tCloudNoise.Sample( g_sCloudSampler, p / 430.0 + g_vSeaClock.x * 0.0007 ).r );
		float2 slope = (r1 * 0.08 + r2 * 0.06 + r3 * 0.04) * RippleStrength * (0.4 + windAmt) * lerp( 0.3, 1.25, slick );

		float3 swim = WaterRings( worldPos );
		slope += swim.xy * 0.12;

		float3 normal = normalize( waveNormal + float3( -slope, 0 ) );
		if ( !i.bIsFrontface )
			normal = -normal;

		bool bOrtho = g_matViewToProjection[3].w != 0;
		float3 vViewRayWs = bOrtho ? g_vCameraDirWs : normalize( i.vPositionWithOffsetWs.xyz );
		float3 V = -vViewRayWs;

		// ---- the real water behind this pixel, from the scene depth (Hi-Z depth chain): the true light path to whatever
		// lies behind the surface and the vertical depth there — so the waterline, the colour and the shore foam follow
		// the actual terrain mesh, not the carve's coarser lake field. Without a depth chain: the carve's depth.
		float surfDist = dot( i.vPositionWithOffsetWs.xyz, g_vCameraDirWs );
		float sceneDist = Depth::GetLinear( i.vPositionSs.xy );
		float rayCos = max( dot( vViewRayWs, g_vCameraDirWs ), 0.05 );
		float thicknessU = (sceneDist - surfDist) / rayCos;
		bool depthValid = sceneDist > 1.0 && thicknessU > -64.0;
		// Trust the depth chain near the camera only: a few hundred metres out it cannot resolve centimetres of water and
		// came back in steps (stripes across a shallow lake seen from a hill), and the near / far terrain tiers differ
		// under it. Further out the carve's own depth — the same in both tiers — takes over.
		float trust = depthValid ? 1.0 - smoothstep( 6000.0, 12000.0, surfDist ) : 0.0;
		float carvePathU = min( depth * UNITS_PER_METER / max( V.z, 0.08 ), 4000.0 );
		float flPathU = lerp( carvePathU, clamp( thicknessU, 0.0, 8000.0 ), trust );
		float realDepth = lerp( depth, clamp( thicknessU, 0.0, 8000.0 ) * saturate( V.z ) / UNITS_PER_METER, trust );
		float waterDepthU = realDepth * UNITS_PER_METER;
		float2 uv = i.vPositionSs.xy * g_vInvViewportSize;
		// Refraction bends the view by a world-space amount, so on screen it shrinks with distance (beyond 6 m); a fixed
		// screen offset dragged the bank into distant shallows in bands
		float2 uvR = uv + normal.xy * RefractionStrength * 0.05 * saturate( waterDepthU / 40.0 ) * min( 1.0, 240.0 / max( surfDist, 1.0 ) );
		// Never refract something that stands in front of the water (a leg, a rock): keep the straight view there
		if ( trust > 0.0 && Depth::GetLinear( uvR / g_vInvViewportSize ) < surfDist )
			uvR = uv;
		float2 blur = g_vInvViewportSize * BedBlurPixels * saturate( flPathU / (BedBlurDistanceMeters * UNITS_PER_METER) );
		float2 fbScale = float2( 1.0, 1.0 );
		// The bed, blurred by the path through the water: a centre tap plus a ring, plus two taps a few pixels above
		// and below so a missing copy row is always bridged; taps that came back uncopied (black) are skipped
		float2 px = g_vInvViewportSize;
		float4 acc = CopyTap( uvR ) * 0.30
			+ CopyTap( uvR + blur * float2( 0.8, 0.6 ) ) * 0.14
			+ CopyTap( uvR + blur * float2( -0.6, 0.8 ) ) * 0.14
			+ CopyTap( uvR + blur * float2( -0.8, -0.6 ) ) * 0.14
			+ CopyTap( uvR + blur * float2( 0.6, -0.8 ) ) * 0.14
			+ CopyTap( uvR + float2( 0.0, 3.0 ) * px ) * 0.07
			+ CopyTap( uvR - float2( 0.0, 3.0 ) * px ) * 0.07;
		float3 sceneColor = acc.a > 1e-3 ? acc.rgb / acc.a : CopyTap( uvR + float2( 0.0, 6.0 ) * px ).rgb;

		float3 sunInWater = 0;
		float columnVis = 1.0;
		if ( g_DirectionalLightEnabled )
		{
			float3 Ls = normalize( -g_DirectionalLightDirection.xyz );
			sunInWater = g_DirectionalLightColor.rgb * saturate( Ls.z * 4.0 );
			if ( g_DirectionalLightCascadeCount > 0 )
			{
				float3 refr = refract( vViewRayWs, normal, 0.75 );
				// Taps stay above the bed and are lifted 1 m toward the sun: against a shallow bed they otherwise read the
				// ground's own shadow-map texels (acne), which switched the sheen on and off in stripes across distant shallows
				float reach = min( flPathU * 0.8, 400.0 );
				columnVis = ( WaterColumnTap( lightPos, 0.0, Ls, vReceiverNormalWs, i.vPositionSs )
					+ WaterColumnTap( lightPos, refr * (reach * 0.2), Ls, vReceiverNormalWs, i.vPositionSs )
					+ WaterColumnTap( lightPos, refr * (reach * 0.5), Ls, vReceiverNormalWs, i.vPositionSs )
					+ WaterColumnTap( lightPos, refr * (reach * 0.9), Ls, vReceiverNormalWs, i.vPositionSs ) ) * 0.25;
			}
		}
		float shade = (1.0 - columnVis) * saturate( dot( sunInWater, float3( 0.30, 0.59, 0.11 ) ) );

		// Caustics on the bed where the sun reaches it (Valheim's sunlit shallows): the bed point from the real light
		// path, its own shadow, the sun's strength
		if ( g_DirectionalLightEnabled && CausticStrength > 0.0 )
		{
			float3 bedOffset = vViewRayWs * flPathU;
			float bedVis = g_DirectionalLightCascadeCount > 0 ? WaterShadow( lightPos + bedOffset + normalize( -g_DirectionalLightDirection.xyz ) * 40.0, vReceiverNormalWs, i.vPositionSs ) : 1.0;
			float bedDepthM = flPathU * saturate( V.z ) / UNITS_PER_METER;
			float c = WaterCaustics( (worldPos.xy + bedOffset.xy) / UNITS_PER_METER, bedDepthM, g_vSeaClock.x, footprintMeters );
			sceneColor *= 1.0 + c * CausticStrength * bedVis * saturate( dot( sunInWater, float3( 0.30, 0.59, 0.11 ) ) );
		}

		float3 transmittance = pow( max( AbsorbTint, 0.001 ), flPathU / ClearDistance );
		float3 skyUp = SkyReflection( float3( 0, 0, 1 ) );
		// What the water scatters back has been through the water too: tinted by its own absorption (red goes first — clear
		// water reads teal-blue in the deep, tannin water brown), normalised so the brightest channel keeps the light
		// Deep water only scatters back the light that survives the trip down and up again: red and most green are gone,
		// so the body of deep water is a saturated blue-teal, darker than the sky (a pale tint read as grey from above)
		float3 scatterTint = pow( max( AbsorbTint, 1e-3 ), 4.0 );
		scatterTint /= max( max( scatterTint.r, scatterTint.g ), max( scatterTint.b, 1e-3 ) );
		// …and strongly absorbing water scatters little at all: tannin water is dark tea, not a glowing beige
		scatterTint *= saturate( dot( AbsorbTint, float3( 0.333, 0.333, 0.333 ) ) * 2.0 );
		float3 seen = lerp( (skyUp * InScatter + sunInWater * (columnVis * SunInScatter)) * scatterTint, sceneColor, transmittance );

		// ---- reflection: the sky by Fresnel; no sheen in the shade
		float3 R = reflect( vViewRayWs, normal );
		R.z = abs( R.z );   // a wave face tilted past the view ray still mirrors sky, never the ground below the horizon
		// Far water: its ripples are below a pixel, so the mirror blurs and never reaches full strength
		// Open water is never a crisp mirror either: the chop smears reflected clouds into soft streaks (sharp clouds
		// through the fine ripples broke into grey shards across the sea, 2026-10-22). Calm lakes stay near-sharp.
		float minRough = lerp( 0.08, 0.45, saturate( openness * 2.0 ) ) * (0.6 + windAmt);
		float reflectRough = saturate( max( footprintMeters * DistanceReflectionBlur, minRough ) );
		float3 reflection = SkyReflectionRough( R, reflectRough );
		float flNDotV = saturate( dot( normal, V ) );
		float fresnel = FresnelRough( flNDotV, FresnelF0, reflectRough );
		fresnel = saturate( fresnel * ReflectionStrength );
		fresnel *= 1.0 - (1.0 - ShadeReflection) * saturate( shade );

		// ---- lights
		float alpha = min( sqrt( SurfaceRoughness * SurfaceRoughness + DistanceRoughness * DistanceRoughness * footprintMeters * footprintMeters ), 0.45 );
		// Per-light cap as a multiple of the light's own colour (as riverwater.shader): an absolute cap let a dim moon / torch
		// make a 4.0 HDR blob on far water once the grazing 1 / (4 VdotH²) term ran away
		float specCap = lerp( 40.0, 1.5, saturate( footprintMeters / 1.5 ) );
		float3 glint = 0;
		float3 foamLight = 0;
		float3 fastSunDir = 0;
		if ( g_DirectionalLightEnabled )
		{
			float3 L = normalize( -g_DirectionalLightDirection.xyz );
			fastSunDir = L;
			float vis = 1.0;
			if ( g_DirectionalLightCascadeCount > 0 )
				vis = WaterShadow( lightPos + L * 40.0, vReceiverNormalWs, i.vPositionSs );
			float3 col = g_DirectionalLightColor.rgb;
			glint += col * (min( SpecularGGX( normal, V, L, alpha, FresnelF0 ), specCap ) * vis);
			foamLight += col * (saturate( L.z ) * columnVis);
		}
		uint nLightCount = Light::Count( i.vPositionSs );
		[loop]
		for ( uint n = 0; n < nLightCount; n++ )
		{
			Light light = Light::From( lightPos, i.vPositionSs, n, 0.0f, vReceiverNormalWs );
			bool bDirectional = light.LightData.Type == LightType::LightTypeDirectional;
			if ( bDirectional && g_DirectionalLightEnabled && dot( light.Direction, fastSunDir ) > 0.9999 )
				continue;
			if ( !bDirectional )
			{
				float3 vToLight = light.Position - lightPos;
				if ( dot( vToLight, vToLight ) > light.LightData.RadiusSquared )
					continue;
			}
			float f = light.Attenuation * light.Visibility;
			if ( f <= 0.0 )
				continue;
			glint += light.Color * (min( SpecularGGX( normal, V, light.Direction, alpha, FresnelF0 ), specCap ) * f);
			foamLight += light.Color * (saturate( light.Direction.z ) * f);
		}
		// Far / grazing glints (Mark 2026-10-25: a distant river on a hillside showed two blinding white blobs in a purple
		// bloom): one far pixel covers many ripples, so its sun sparkle spreads into a dim sheen instead of a 40x spike -
		// the cap falls with the pixel's footprint - and the GGX peak at a grazing view is faded out. A NaN (grazing
		// normal / view) is zeroed: bloomed, it smeared colour across the hill.
		float glintCap = lerp( 40.0, 4.0, saturate( footprintMeters / 1.5 ) );
		glint = min( glint * SpecularStrength * smoothstep( 0.02, 0.15, flNDotV ), glintCap );
		if ( any( isnan( glint ) ) || any( isinf( glint ) ) )
			glint = 0;

		// ---- foam (Mark 2026-10-22: "REAL … identical to Valheim"): the surf comes in as laps — a bright, thin
		// front running up the shallows to the waterline, a lace trail behind it that spreads and dissolves as it ages,
		// two laps out of phase — plus a lacy line at the waterline, whitecaps where crests fold, and swimmers. Every
		// amount goes through the same lace, so foam thins into wisps and holes instead of fading like a decal.
		float t = g_vSeaClock.x;
		// The lace is laid out where the water IS (the displaced point): taken at the grid point it was dragged into
		// marbled swirls by the Gerstner sideways displacement (2026-10-26, a few metres on a breaking crest)
		float lace = FoamLace( worldPos.xy / UNITS_PER_METER, t );
		// The real distance to the waterline: the real depth over the carve's slope here (the carve's depth and distance
		// only give the slope — the terrain mesh decides where the water actually ends)
		float realShore = shoreX > 0.0 ? realDepth * shoreDistance / max( depth, 0.02 ) : shoreX;
		float breakerFoam;
		SeaBreakers( p, shoreX, depth, shoreEnergy, t, breakerFoam );
		// The ocean swell breaking over the shallows: white where its sharpened crests fold
		// The swell breaking: where the set stands over the breaking height (SeaWaveSum), white on the crests
		float swellBreak = smoothstep( 0.0, 0.6, breaking ) * smoothstep( 0.1, 0.45, folding ) * saturate( shoreEnergy );
		float surf = max( max( SeaShoreLaps( p, realShore, shoreEnergy, t ).y, breakerFoam ), swellBreak );
		// The waterline itself: a near-continuous lacy line in the last metre before the sand (distance, not depth — a
		// flat shelf a few centimetres deep would otherwise foam across all of it)
		float waterline = (1.0 - smoothstep( 0.0, 1.2, realShore )) * (1.0 - smoothstep( 0.1, 0.5, realDepth )) * 1.2;
		// Surf needs wave energy: open-ocean beaches break, a calm lake only shows a faint line where it meets the sand
		// (Valheim lakes have no surf — only rings round whoever wades in)
		float surfEnergy = saturate( shoreEnergy );
		float shoreAmount = saturate( (surf * surfEnergy + waterline * lerp( 0.35, 1.0, surfEnergy )) * ShoreFoamStrength );
		// Whitecaps where crests fold, but not on every crest of the leading swell (that drew them in parallel rows): a
		// broad field raises and lowers the threshold so crests break here and there, in patches that drift downwind.
		// Small lakes stay clean; open water whitens.
		float capVar = g_tCloudNoise.Sample( g_sCloudSampler, p / 260.0 + float2( 0.31, 0.77 ) - g_vSeaWindDirection.xy * (t * 0.0015) ).r;
		float capThreshold = WhitecapThreshold + (capVar - 0.5) * 0.45;
		float capAmount = smoothstep( capThreshold, capThreshold + 0.25, folding ) * WhitecapStrength * smoothstep( 0.25, 0.7, openness );
		// More caps as the sea builds (the weather's height factor: calm 0.3 … default 1 … storm 2.8), none once a pixel
		// covers metres of water — the lace averaged to flat white slabs on the horizon (2026-10-26)
		float seaHeight = g_vSeaSwellD.w / max( g_vSeaWaves.x * 1.56, 0.01 );
		capAmount *= saturate( (seaHeight - 0.5) / 1.5 ) * 1.5 * (1.0 - smoothstep( 1.5, 6.0, footprintMeters ));
		// Remembered foam: what broke here in the last seconds (one frame behind; the rect follows the camera)
		float memFoam = 0.0;
		if ( g_vSeaFoamRect.w > 0.5 )
		{
			float2 fuv = (worldPos.xy - g_vSeaFoamRect.xy) / g_vSeaFoamRect.z;
			// Fades out over the last 40 m of the rect, so its edge never draws a line across the water
			float2 edge = min( fuv, 1.0 - fuv ) * g_vSeaFoamRect.z / UNITS_PER_METER;
			float inside = smoothstep( 0.0, 40.0, min( edge.x, edge.y ) );
			if ( inside > 0.0 )
				memFoam = g_tSeaFoamMemory.SampleLevel( g_sFoamMemorySampler, fuv, 0 ).r * MemoryFoamStrength * inside * (1.0 - smoothstep( 2.0, 8.0, footprintMeters ));
		}
		// River mouths: no surf where a river runs in, and the surface fades in past the mouth line (the river fades out)
		float4 mouth = RiverMouthAt( p );
		shoreAmount *= mouth.x * mouth.x * RiverMouthFoamKeep( p );
		float foam = saturate( FoamThroughLace( max( max( shoreAmount, capAmount ), memFoam * mouth.x ), lace ) + swim.z );
		float skyLum = dot( skyUp, float3( 0.30, 0.59, 0.11 ) );
		// Foam is a near-white diffuse surface: the sky's light plus every light that reaches it, at full strength (scaled
		// down it came out no brighter than the sunlit sand under it, and rings / surf vanished against the bed)
		float3 foamColor = (lerp( skyLum.xxx, skyUp, 0.2 ) + foamLight) * 0.9;

		// ---- compose: the frame-buffer copy is fogged already; only what the surface adds gets fog
		float3 fog0 = Fog::Apply( worldPos, i.vPositionSs.xy, float3( 0, 0, 0 ) );
		float3 fogKeep = Fog::Apply( worldPos, i.vPositionSs.xy, float3( 1, 1, 1 ) ) - fog0;
		float surfaceWeight = saturate( fresnel * (1.0 - foam) + foam );
		float3 surface = reflection * fresnel * (1.0 - foam) + foamColor * foam;
		float3 color = seen * (1.0 - surfaceWeight) + surface * fogKeep + fog0 * surfaceWeight + glint * fogKeep;
		// Soft waterline: the water fades in over EdgeSoftnessMeters of real depth
		float edgeAlpha = lerp( 1.0, smoothstep( 0.0, EdgeSoftnessMeters, realDepth ), trust ) * mouth.x;
		if ( DebugView > 0.5 )
		{
			// 1: real vertical depth (red 0..4 m, green 0..40 m, blue = no depth chain); 2: reflection roughness; 3: ring trail
			if ( DebugView < 1.5 ) color = float3( saturate( realDepth / 4.0 ), saturate( realDepth / 40.0 ), depthValid ? 0.0 : 1.0 );
			else if ( DebugView < 2.5 ) color = reflectRough.xxx;
			else if ( DebugView < 3.5 ) color = float3( g_vWaterTrailInfo.x / 48.0, frac( g_vWaterTrailInfo.y ), swim.z );   // 3: ring trail feed
			else if ( DebugView < 4.5 ) color = float3( shade, columnVis, 0.0 );          // 4: shade / column visibility
			else if ( DebugView < 5.5 ) color = fresnel.xxx;                               // 5: fresnel
			else if ( DebugView < 6.5 ) color = sceneColor;                                // 6: what is behind the water
			else if ( DebugView < 7.5 ) color = float3( foam, edgeAlpha, trust );          // 7: foam / edge alpha / depth trust
			else if ( DebugView < 8.5 ) color = waveNormal * 0.5 + 0.5;                    // 8: wave normal
			else if ( DebugView < 9.5 ) color = g_tFrameBufferCopyTexture.SampleLevel( g_sTrilinearMirror, uv * fbScale, 0 ).rgb;   // 9: raw copy at the pixel
			else color = float3( frac( i.vPositionSs.y / 8.0 ), saturate( (uvR.y - uv.y) * 200.0 + 0.5 ), saturate( length( blur ) * 200.0 ) );   // 10: refraction / blur
			edgeAlpha = 1.0;
		}
		// Fades are done here against the copied scene (no hardware alpha: with the frame-buffer copy the water must
		// write alpha 1 — engine glass.shader: "FBCopy Glass shouldn't write to alpha"; blending broke the copy into
		// black rows, 2026-10-22)
		color = lerp( CopyTap( uv ).rgb, color, edgeAlpha );
		return float4( color, 1.0f );
	}
}
