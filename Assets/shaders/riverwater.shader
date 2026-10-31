//
// riverwater — the terrain's river surface. Target: Valheim, done better (Mark 2026-10-22: "so completely realistic that
// it feels immersive"). CLEAR at your feet — the bed through the water — and a mirror toward the far bank, where the
// mirror holds the sky AND the dark bank / treeline silhouette, rippled so you never read it as a trick.
//
//   what you see  : the riverbed through the water — the frame-buffer copy behind the surface, darkened by the path the
//                   light took through the water (Beer-Lambert per channel). The absorption colour is the biome's:
//                   Clover / Amber clear, Redwood tannin (warm, brown in the deep), Mountain glacial (faint cold cyan).
//                   Water depth comes from the mesh (COLOR0.r — the real vertex colour; the engine's vVertexColor is
//                   the renderer tint, which made every pixel "8 m deep" until 2026-10-22).
//   reflection    : the SkyDome's own sky along the reflected ray (gradient, sun band, halo, both cloud layers), mixed
//                   by Schlick Fresnel. Where the reflected ray would hit the bank + treeline beside the river (their
//                   heights per side come from the mesh: UV1), the colour is read from the frame buffer at the screen
//                   position of that bank point — the real bank / trees (or the sky above them), rippled by the surface normal; a dimmed
//                   sky where that point is off screen. No planar render, no depth buffer.
//   lights        : every light — the sun (with its shadow cascades), the moon, torches, campfires — puts a GGX
//                   highlight on the ripples: a sun glitter path by day, a moon path by night, warm streaks under a
//                   torch. Roughness widens with distance, so far glitter is a soft band, never a sparkle field. In
//                   shade the sheen goes (ShadeReflection, Valheim reference): no light, nothing to reflect, so the surface
//                   turns see-through to the bed — never a darkened surface, never a crisp edge: the shade is the
//                   average sun visibility down the water column, and the bed blurs with the path through the water.
//   ripples       : a tileable two-layer slope texture (Blender/scripts/create_river_ripples.py) at three scales — 2 m,
//                   8 m sheared, 32 m transposed (all divide the 64 m along-river UV wrap) — advected with two-phase
//                   flow so the current can vary along the river without stretching. Mips flatten the far water.
//   current       : COLOR0.g = the local current from the slope (calm pools on the flats, quick and choppy on steep
//                   stretches); ripples, foam and chop scale with it.
//   foam          : soft patches of the dome's tileable cloud noise drifting with the current (more where fast), shore
//                   foam in the shallows, and foam where a pawn cuts the surface.
//   swimmers      : up to four pawns near the camera (WaterRipplerPos/Vel, published by TerrainWorldManager.RiverWater; water_common.hlsl):
//                   rings while treading water, a Kelvin wake when moving. A body only disturbs the water it cuts.
//   fog           : the frame-buffer copy is already fogged, so fog is applied only to what the surface adds.
//
// Built by TerrainWorldManager.RiverWater.cs as ONE mesh for all loaded chunks (one frame-buffer copy).
//
HEADER
{
	Description = "Flowing river water (clear below; sky, bank and lights mirrored by Fresnel; flow-mapped ripples)";
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

	// Mesh vertex colour: R = water depth / 8 m, G = current / 3, B = biome tint axis, A = half width / 64 m
	float4 vColor : COLOR0 < Semantic( Color ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	float4 vRiver : COLOR0;
	// UV1: bank + treeline height above the water on the -side / +side, metres
	float2 vOccluder : TEXCOORD8;
	// 1, falling to 0 across the river a tributary flows into (TerrainWorldManager.RiverJoinFade)
	float flJoin : TEXCOORD9;

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
		o.vRiver = i.vColor;
		// TexCoord1.y = +side occluder (m) + 100 × confluence fade step (0–15): TerrainWorldManager.RiverJoinFade
		float joinStep = floor( i.vTexCoord2.y / 100.0 );
		o.vOccluder = float2( i.vTexCoord2.x, i.vTexCoord2.y - joinStep * 100.0 );
		o.flJoin = joinStep / 15.0;
		// The mesh is raised 1 m so the engine's raw-geometry refraction stencil never z-fights a shallow bed
		// (TerrainWorldManager.SeaWater WaterStencilLiftMeters); back down to the real surface here
		o.vPositionWs.z -= 40.0;
		// Pulled 0.2 % of its distance toward the camera — same pixel, nearer depth — so the shallow bed cannot z-fight
		// the surface far away (as seawater.shader)
		o.vPositionPs.xyzw = Position3WsToPs( o.vPositionWs.xyz + (g_vCameraPositionWs - o.vPositionWs.xyz) * 0.002 );
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

	// Visible from below as well (a swimmer looks up through it)
	RenderState( CullMode, NONE );
	// Real alpha blending for the river-mouth fade only (alpha = riverAlpha, 1 everywhere else): the river is drawn after
	// the lake / sea surface and its frame grab does not contain it, so composing the fade onto the grab pasted bare
	// bed over the lake (2026-10-24). Blended, the lake already in the frame shows through as the river fades out.
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( SrcBlendAlpha, ONE );
	RenderState( DstBlendAlpha, INV_SRC_ALPHA );

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


	// Ripple slopes (river_ripples.png: RG = fine chop, BA = broad swells), stored 0.5 + 0.5 * slope
	CreateInputTexture2D( RippleSlopes, Linear, 8, "", "", "Flow,20/5", Default4( 0.5, 0.5, 0.5, 0.5 ) );
	Texture2D g_tRipples < Channel( RGBA, Box( RippleSlopes ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	SamplerState g_sRippleSampler < Filter( ANISOTROPIC ); MaxAniso( 8 ); AddressU( WRAP ); AddressV( WRAP ); >;

	// Must match TerrainWorldManager.RiverWater.cs (RiverRippleTileMeters / RiverDepthColorRangeMeters /
	// RiverSpeedMulMax / RiverHalfWidthColorRangeMeters)
	#define RIVER_RIPPLE_TILE_METERS 64.0
	#define RIVER_DEPTH_COLOR_RANGE_METERS 8.0
	#define RIVER_SPEED_MUL_MAX 3.0
	#define RIVER_HALF_WIDTH_RANGE_METERS 64.0
	#define UNITS_PER_METER 40.0

	// ---- water body: no colour of its own, only how the bed darkens with the path through the water.
	// Light that travels ClearDistance (3200 u = 80 m) keeps the tint of itself, per channel (Beer-Lambert). The tint is
	// the biome's (mesh COLOR0.b: 0 Redwood, 0.5 clear, 1 Mountain).
	float ClearDistance < Default( 160.0 ); Range( 8.0, 8000.0 ); UiGroup( "Water,10/10" ); >;
	float3 AbsorbTint < UiType( Color ); Default3( 0.45, 0.62, 0.66 ); UiGroup( "Water,10/20" ); >;
	// Redwood: tannin from the needle litter — the blue goes first, the deep water turns tea-brown
	float3 TanninTint < UiType( Color ); Default3( 0.22, 0.12, 0.05 ); UiGroup( "Water,10/21" ); >;
	// Mountain: glacial flour — the red goes first, a faint cold cyan in the deep
	float3 GlacialTint < UiType( Color ); Default3( 0.30, 0.60, 0.64 ); UiGroup( "Water,10/22" ); >;
	// How much of the sky's light the water itself scatters back where the absorption took the bed away
	float InScatter < Default( 0.14 ); Range( 0.0, 1.0 ); UiGroup( "Water,10/25" ); >;
	float RefractionStrength < Default( 0.20 ); Range( 0.0, 1.0 ); UiGroup( "Water,10/40" ); >;
	// Sunlight scattered back by the water column, × the sun's colour and its soft in-water shadow
	float SunInScatter < Default( 0.03 ); Range( 0.0, 0.5 ); UiGroup( "Water,10/27" ); >;
	// The bed blurs by up to this many pixels once the light has come BedBlurDistanceMeters through the water
	float BedBlurPixels < Default( 6.0 ); Range( 0.0, 24.0 ); UiGroup( "Water,10/50" ); >;
	float BedBlurDistanceMeters < Default( 6.0 ); Range( 0.5, 40.0 ); UiGroup( "Water,10/52" ); >;
	// At a mouth the river fades into the lake / sea surface over this many metres past the shore (Mark 2026-10-22: a
	// river strip lying on the lake should not show — "a kinda seamless edge")
	float MouthFadeMeters < Default( 8.0 ); Range( 0.5, 40.0 ); UiGroup( "Water,10/60" ); >;
	// The water fades in over this much real depth at the banks (soft edge against the terrain)
	float EdgeSoftnessMeters < Default( 0.25 ); Range( 0.01, 2.0 ); UiGroup( "Water,10/62" ); >;
	// Reflection roughness gained per metre of pixel footprint (distant water's sub-pixel ripples blur the mirror)
	float DistanceReflectionBlur < Default( 0.45 ); Range( 0.0, 1.0 ); UiGroup( "Reflection,15/38" ); >;

	// ---- reflection
	float ReflectionStrength < Default( 1.0 ); Range( 0.0, 2.0 ); UiGroup( "Reflection,15/10" ); >;
	// Fresnel at normal incidence: water is 0.02 — nearly all bed when you look down
	float FresnelF0 < Default( 0.02 ); Range( 0.0, 0.3 ); UiGroup( "Reflection,15/20" ); >;
	// 1 = the bank / treeline silhouette fully replaces the sky where the reflected ray meets it
	float BankReflection < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Reflection,15/30" ); >;
	// The mirrored treeline stands this far back from the water's edge (slide wall + setback), metres
	float BankSetbackMeters < Default( 10.0 ); Range( 0.0, 40.0 ); UiGroup( "Reflection,15/32" ); >;
	// How ragged the mirrored treeline top is (0 = a straight wall)
	float TreelineJitter < Default( 0.5 ); Range( 0.0, 1.0 ); UiGroup( "Reflection,15/34" ); >;
	// How far the ripples bend the mirrored bank (screen-space wobble)
	float BankRippleDistortion < Default( 0.035 ); Range( 0.0, 0.2 ); UiGroup( "Reflection,15/36" ); >;

	// ---- lights on the surface (sun, moon, torches): GGX highlight on the ripples
	float SurfaceRoughness < Default( 0.06 ); Range( 0.01, 0.5 ); UiGroup( "Lights,17/10" ); >;
	// Extra roughness per metre of pixel footprint: far glitter merges into one soft band
	float DistanceRoughness < Default( 0.5 ); Range( 0.0, 4.0 ); UiGroup( "Lights,17/20" ); >;
	float SpecularStrength < Default( 1.0 ); Range( 0.0, 4.0 ); UiGroup( "Lights,17/30" ); >;
	// In the sun's shade the reflection keeps only this much of itself: no light, no sheen — the bed shows through
	float ShadeReflection < Default( 0.3 ); Range( 0.0, 1.0 ); UiGroup( "Lights,17/40" ); >;

	// ---- flow
	float FlowSpeed < Default( 1.5 ); Range( 0.0, 10.0 ); UiGroup( "Flow,20/10" ); >;   // metres per second at the median slope
	float RippleStrength < Default( 1.0 ); Range( 0.0, 4.0 ); UiGroup( "Flow,20/40" ); >;
	// Extra chop where the current is fast (× the current multiple above 1)
	float FastWaterChop < Default( 0.6 ); Range( 0.0, 2.0 ); UiGroup( "Flow,20/60" ); >;
	// Rapids on the steepest stretches: more foam clumps and bubbles, milky, much less see-through
	float RapidsStrength < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Flow,20/70" ); >;

	// ---- foam (Valheim: soft white patches drifting with the current, foam along the shore). The mask is the dome's
	// tileable cloud noise in metres, so patches are organic blobs, never bars or bands.
	float FoamStrength < Default( 0.16 ); Range( 0.0, 1.0 ); UiGroup( "Foam,25/10" ); >;
	// Size of one noise tile in metres along the river; must divide 64 m so the along-river wrap is seamless
	float FoamScaleMeters < Default( 8.0 ); Range( 1.0, 64.0 ); UiGroup( "Foam,25/20" ); >;
	// How much of the surface carries patches (threshold on the noise): lower = more foam
	float FoamThreshold < Default( 0.68 ); Range( 0.2, 0.98 ); UiGroup( "Foam,25/30" ); >;
	// Threshold drop on the fastest water: rapids carry more foam
	float FastWaterFoam < Default( 0.18 ); Range( 0.0, 0.6 ); UiGroup( "Foam,25/35" ); >;
	float EdgeFoamStrength < Default( 0.6 ); Range( 0.0, 1.0 ); UiGroup( "Foam,25/40" ); >;
	// Shore foam reaches this far in from the bank, as a fraction of the water depth range (depth01 < this)
	float EdgeFoamDepth01 < Default( 0.10 ); Range( 0.01, 0.5 ); UiGroup( "Foam,25/50" ); >;

	// 0 off · 1 bank occlusion · 2 bank + treeline heights (/20 m, R = -side, G = +side) · 3 across direction ·
	// 4 mesh data (R depth, G current, B tint) · 5 half width (/20 m) and across coordinate ·
	// 6 mouth fade (R alpha, G open-water depth /4 m, B = no depth chain)
	float DebugView < Default( 0.0 ); Range( 0.0, 6.0 ); UiGroup( "Debug,99/10" ); >;

	// Sky reflection, light glint, swimmer rings / wakes — shared with seawater.shader
	#include "water_common.hlsl"
	#include "river_mouths.hlsl"

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

	// One ripple layer carried by the current. flowMeters.x is TRAVEL along the river (TerrainWorldManager.RiverWater:
	// metres ÷ the local current multiple, accumulated from the source), so scrolling it at a constant FlowSpeed moves
	// every ripple at its own local speed and nothing ever stretches — no two-phase cross-fade (its 3 s cycle made the
	// ripples surge forward and snap back, Mark 2026-10-25). Fast water shows the same pattern drawn out along the flow:
	// streaks. speed = scroll multiple of FlowSpeed (layers drift at slightly different rates). Returns (along, across).
	float2 RippleLayer( float2 flowMeters, float tileMeters, float shear, bool transposed, bool broad, float speed, float phaseNoise )
	{
		float along = flowMeters.x - g_flTime * FlowSpeed * speed;
		float2 uv = float2( (along + shear * flowMeters.y) / tileMeters, flowMeters.y / tileMeters ) + phaseNoise;
		if ( transposed )
			uv = uv.yx;
		float4 t = g_tRipples.Sample( g_sRippleSampler, uv );
		float2 s = (broad ? t.ba : t.rg) * 2.0 - 1.0;
		if ( transposed )
			s = s.yx;
		return s;
	}

	// 0..1 foam: soft cloud-noise patches carried by the current (travel coordinate, like the ripples), denser on fast
	// water, plus shore foam in the shallows. Tiles a whole number of times per 64 m along the river.
	float GetFoam( float2 flowMeters, float depth01, float speedMul )
	{
		float scale = max( 1.0, RIVER_RIPPLE_TILE_METERS / round( RIVER_RIPPLE_TILE_METERS / FoamScaleMeters ) );
		float along = flowMeters.x - g_flTime * FlowSpeed * 0.9;   // foam rides a little slower than the surface
		float n = g_tCloudNoise.Sample( g_sCloudSampler, float2( along, flowMeters.y ) / scale ).r;
		float gate = g_tCloudNoise.Sample( g_sCloudSampler, flowMeters / (scale * 4.0) + float2( 0.13, 0.71 ) ).g;
		float threshold = FoamThreshold - FastWaterFoam * saturate( (speedMul - 1.0) / (RIVER_SPEED_MUL_MAX - 1.0) );
		// A wide band: a narrow one on the 8-bit noise drew every patch with a contour-line outline
		float patches = smoothstep( threshold, threshold + 0.32, n * (0.55 + 0.45 * gate) );

		float shore = smoothstep( EdgeFoamDepth01, 0.0, depth01 ) * EdgeFoamStrength;
		// A continuous band, thicker and thinner along the bank (the lace breaks it up) — a hard threshold on the noise
		// drew evenly spaced white dashes down every bank
		float shoreFoam = shore * lerp( 0.3, 1.0, smoothstep( 0.2, 0.8, n ) );

		return saturate( patches * FoamStrength + shoreFoam );
	}

	float4 BubbleHash( float2 p )
	{
		float4 p4 = frac( p.xyxy * float4( 0.1031, 0.1030, 0.0973, 0.1099 ) );
		p4 += dot( p4, p4.wzxy + 33.33 );
		return frac( (p4.xxyz + p4.yzzw) * p4.zywx );
	}

	// One layer of round bubbles riding the current: at most one per cell (cell metres, divides the 64 m wrap), a bright
	// rim round a fainter middle. density 0..1 = share of cells with a bubble. Past a pixel or two a bubble becomes the
	// coverage it would average to, so far rapids read as a faint haze, never sparkling noise.
	float BubbleLayer( float2 flowMeters, float cell, float speed, float density, float footprintMeters, float seed )
	{
		float along = flowMeters.x - g_flTime * FlowSpeed * speed;
		float2 q = float2( along, flowMeters.y ) / cell;
		float2 id = floor( q );
		float2 f = q - id;
		float wrap = RIVER_RIPPLE_TILE_METERS / cell;
		id.x -= wrap * floor( id.x / wrap );
		float4 h = BubbleHash( id + seed );
		float present = step( h.x, density );
		float r = lerp( 0.12, 0.28, h.w );   // radius in cells: the disc stays inside its own cell
		float d = length( f - (0.3 + 0.4 * h.yz) ) / r;
		float aa = max( footprintMeters / (cell * r), 0.05 );
		// A bubble is a thin bright rim round a clear middle, with a glint toward the upstream bank side
		float2 off = (f - (0.3 + 0.4 * h.yz)) / r;
		float glint = 1.0 - smoothstep( 0.0, 0.35, length( off - float2( -0.35, 0.35 ) ) );
		float disc = (1.0 - smoothstep( 1.0 - aa, 1.0 + aa, d )) * max( lerp( 0.12, 1.0, smoothstep( 0.62, 0.95, d ) ), glint );
		// Unresolved: the layer's average cover, the same in every cell (gating it per cell drew white squares)
		float resolved = smoothstep( 0.8, 2.0, cell * r / max( footprintMeters, 1e-4 ) );
		return lerp( density * 0.08, present * disc, resolved );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 worldPos = i.vPositionWithOffsetWs.xyz + g_vCameraPositionWs;
		float3 lightPos = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float2 flowMeters = i.vTextureCoords.xy;

		// ---- everything derivative-based first, in uniform control flow
		float3 vReceiverNormalWs = ComputeShadowReceiverNormal( lightPos );
		float3 dpdx = ddx( worldPos );
		float3 dpdy = ddy( worldPos );
		float footprintMeters = max( length( dpdx ), length( dpdy ) ) / UNITS_PER_METER;
		// Out over open water (a mouth): distance past the shore = open-water depth over its slope
		float openDepth = OpenWaterDepthMeters( worldPos.xy / UNITS_PER_METER );
		// River mouths: over the hand-over stretch the lake / sea surface fades in up the channel and the river fades out
		// (river_mouths.hlsl); the strip itself ends at the shoreline. A mouth not listed: the old shore-distance fade.
		float riverAlpha = 1.0;
		float2 pm = worldPos.xy / UNITS_PER_METER;
		float4 mouth = RiverMouthAt( pm );
		if ( mouth.z > 0.01 )
			riverAlpha = 1.0 - mouth.w;
		else if ( openDepth > 0.0 )
		{
			float2 g = float2(
				OpenWaterDepthMeters( pm + float2( 1.0, 0.0 ) ) - OpenWaterDepthMeters( pm - float2( 1.0, 0.0 ) ),
				OpenWaterDepthMeters( pm + float2( 0.0, 1.0 ) ) - OpenWaterDepthMeters( pm - float2( 0.0, 1.0 ) ) ) * 0.5;
			riverAlpha = 1.0 - smoothstep( 0.0, MouthFadeMeters, openDepth / max( length( g ), 1e-3 ) );
		}
		// The across-river direction in the world: the gradient of the across coordinate (continuous everywhere; the
		// along coordinate wraps every 64 m, so its derivatives are not used). Along = across rotated a quarter turn.
		float2 dvds = float2( ddx( flowMeters.y ), ddy( flowMeters.y ) );
		float detJ = dpdx.x * dpdy.y - dpdx.y * dpdy.x;
		float2 acrossWs = float2( dvds.x * dpdy.y - dvds.y * dpdx.y, dvds.y * dpdx.x - dvds.x * dpdy.x ) / (abs( detJ ) > 1e-8 ? detJ : 1e-8);
		acrossWs = normalize( acrossWs + float2( 1e-6, 0 ) );
		float2 alongWs = float2( acrossWs.y, -acrossWs.x );

		float3 baseNormal = normalize( i.vNormalWs );
		if ( !i.bIsFrontface )
			baseNormal = -baseNormal;

		float depth01 = i.vRiver.r;
		float speedMul = i.vRiver.g * RIVER_SPEED_MUL_MAX;
		float tintAxis = i.vRiver.b;
		float halfWidth = i.vRiver.a * RIVER_HALF_WIDTH_RANGE_METERS;
		float drift = FlowSpeed * speedMul;   // m/s here (the lace below)
		// Rapids: the steepest stretches (current ≥ 2× the network's median-slope speed, full at the 3× cap) churn white
		float rapids = smoothstep( 1.8, RIVER_SPEED_MUL_MAX, speedMul ) * RapidsStrength;

		// ---- ripples: three layers of the slope texture carried by the current; choppier where it is fast
		float phaseNoise = g_tRipples.Sample( g_sRippleSampler, flowMeters / 64.0 + 0.5 ).b;
		float2 slope = RippleLayer( flowMeters, 2.0, 0.0, false, false, 1.0, phaseNoise ) * 0.10
			+ RippleLayer( flowMeters, 8.0, 0.6, false, true, 0.92, phaseNoise + 0.33 ) * 0.08
			+ RippleLayer( flowMeters, 32.0, 0.0, true, false, 0.8, phaseNoise + 0.66 ) * 0.05;
		slope *= RippleStrength * (1.0 + FastWaterChop * saturate( speedMul - 1.0 ));
		float2 slopeWs = slope.x * alongWs + slope.y * acrossWs;

		// Pawns cutting the surface
		float3 swim = WaterRings( worldPos );
		slopeWs += swim.xy * 0.12;

		float3 normal = normalize( baseNormal + float3( -slopeWs, 0 ) );

		bool bOrtho = g_matViewToProjection[3].w != 0;
		float3 vViewRayWs = bOrtho ? g_vCameraDirWs : normalize( i.vPositionWithOffsetWs.xyz );
		float3 V = -vViewRayWs;

		// ---- the bed through the water: depth from the mesh, the light path = depth stretched by the view angle
		// The real water behind this pixel from the scene depth (Hi-Z depth chain): the true light path and vertical depth,
		// so banks fade softly into the terrain mesh and the colour follows the real bed. Without it: the mesh's depth.
		float surfDist = dot( i.vPositionWithOffsetWs.xyz, g_vCameraDirWs );
		float sceneDist = Depth::GetLinear( i.vPositionSs.xy );
		float thicknessU = (sceneDist - surfDist) / max( dot( vViewRayWs, g_vCameraDirWs ), 0.05 );
		bool depthValid = sceneDist > 1.0 && thicknessU > -64.0;
		// Near the camera only (see seawater.shader): further out the mesh's own depth, the same in both terrain tiers
		float trust = depthValid ? 1.0 - smoothstep( 6000.0, 12000.0, surfDist ) : 0.0;
		float flNDotVFlat = max( dot( baseNormal, V ), 0.08 );
		float meshDepthU = depth01 * RIVER_DEPTH_COLOR_RANGE_METERS * UNITS_PER_METER;
		float flPathU = lerp( min( meshDepthU / flNDotVFlat, 2400.0 ), clamp( thicknessU, 0.0, 4000.0 ), trust );
		float waterDepthU = lerp( meshDepthU, clamp( thicknessU, 0.0, 4000.0 ) * saturate( V.z ), trust );

		float2 uv = i.vPositionSs.xy * g_vInvViewportSize;
		// Refraction bends the view by a world-space amount, so on screen it shrinks with distance (beyond 6 m); a fixed
		// screen offset dragged the bank into distant shallows in bands
		float2 uvR = uv + normal.xy * RefractionStrength * 0.05 * saturate( waterDepthU / 32.0 ) * min( 1.0, 240.0 / max( surfDist, 1.0 ) );
		// Water scatters: the further the light came through it, the blurrier the bed (Mark 2026-10-22: light must not
		// "cast cleanly across the water" — a tree's shadow on the bed reads soft, never a crisp edge). Five taps.
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

		// The sun inside the water column: shadow visibility at three depths along the refracted view ray, averaged —
		// the water's own glow is soft where a shadow crosses it, never a hard edge on the surface
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
		// How much of the day's sun this point is missing (0 at night: the moon's shade changes nothing)
		float shade = (1.0 - columnVis) * saturate( dot( sunInWater, float3( 0.30, 0.59, 0.11 ) ) );

		// Caustics on the bed where the sun reaches it (Valheim's sunlit shallows): the bed point from the real light
		// path, its own shadow, the sun's strength
		if ( g_DirectionalLightEnabled && CausticStrength > 0.0 )
		{
			float3 bedOffset = vViewRayWs * flPathU;
			float bedVis = g_DirectionalLightCascadeCount > 0 ? WaterShadow( lightPos + bedOffset + normalize( -g_DirectionalLightDirection.xyz ) * 40.0, vReceiverNormalWs, i.vPositionSs ) : 1.0;
			float bedDepthM = flPathU * saturate( V.z ) / UNITS_PER_METER;
			float c = WaterCaustics( (worldPos.xy + bedOffset.xy) / UNITS_PER_METER, bedDepthM, g_flTime, footprintMeters );
			sceneColor *= 1.0 + c * CausticStrength * bedVis * saturate( dot( sunInWater, float3( 0.30, 0.59, 0.11 ) ) );
		}

		float3 tint = tintAxis < 0.5 ? lerp( TanninTint, AbsorbTint, tintAxis * 2.0 ) : lerp( AbsorbTint, GlacialTint, tintAxis * 2.0 - 1.0 );
		float3 transmittance = pow( max( tint, 0.001 ), flPathU / ClearDistance );
		// Rapids are full of air and churned sediment: far less see-through (Mark 2026-10-25: on a steep stretch he
		// could see clean through the water to the ground behind)
		transmittance *= 1.0 - 0.45 * rapids;
		float3 skyUp = SkyReflection( float3( 0, 0, 1 ) );
		// What the water scatters back has been through the water too: tinted by its own absorption (red goes first — clear
		// water reads teal-blue in the deep, tannin water brown), normalised so the brightest channel keeps the light
		float3 scatterTint = tint * tint;
		scatterTint /= max( max( scatterTint.r, scatterTint.g ), max( scatterTint.b, 1e-3 ) );
		// …and strongly absorbing water scatters little at all: tannin water is dark tea, not a glowing beige
		scatterTint *= saturate( dot( tint, float3( 0.333, 0.333, 0.333 ) ) * 2.0 );
		float3 seen = lerp( (skyUp * InScatter + sunInWater * (columnVis * SunInScatter)) * scatterTint, sceneColor, transmittance );
		// Aerated water scatters light back white whatever its tint: rapids read milky, not dark tea
		float3 milky = (skyUp * 0.32 + sunInWater * (0.08 * columnVis)) * float3( 0.86, 0.9, 0.92 );
		seen = lerp( seen, milky, rapids * 0.3 );

		// ---- reflection: the sky, unless the reflected ray meets the bank + treeline first
		float3 R = reflect( vViewRayWs, normal );
		float reflectRough = saturate( footprintMeters * DistanceReflectionBlur );
		float3 reflection = SkyReflectionRough( R, reflectRough );

		float rs = dot( R.xy, acrossWs );
		float toEdge = rs >= 0.0 ? halfWidth - flowMeters.y : halfWidth + flowMeters.y;
		float wallDist = max( toEdge, 0.0 ) + BankSetbackMeters;
		float wallHeight = rs >= 0.0 ? i.vOccluder.y : i.vOccluder.x;
		float tWall = wallDist / max( abs( rs ), 1e-3 );   // metres along the reflected ray to the wall
		float hitAlong = flowMeters.x + tWall * dot( R.xy, alongWs );
		float jag = g_tCloudNoise.Sample( g_sCloudSampler, float2( hitAlong / 16.0, rs >= 0.0 ? 0.25 : 0.75 ) ).g;
		wallHeight = 1.0 + max( wallHeight - 1.0, 0.0 ) * lerp( 1.0, 0.55 + 0.9 * jag, TreelineJitter );
		float rayHeight = tWall * R.z;
		float occluded = R.z <= 0.0 ? 1.0 : saturate( (wallHeight - rayHeight) / (0.15 * wallHeight + 0.3) );
		occluded *= BankReflection;
		if ( occluded > 0.001 )
		{
			// The real bank / trees — or the real sky above them, the wall is deliberately tall: the frame buffer at the
			// screen position of the point the ray meets, wobbled by the ripples. Where that point is off screen or behind
			// the camera nothing is known: the sky, dimmed as if partly under canopy.
			float3 hitWs = worldPos + R * (min( tWall, 400.0 ) * UNITS_PER_METER);
			float4 ps = Position3WsToPs( hitWs );
			float3 silhouette = reflection * 0.6;
			float3 bank = silhouette;
			if ( ps.w > 0.0 )
			{
				float2 suv = float2( ps.x / ps.w * 0.5 + 0.5, 0.5 - ps.y / ps.w * 0.5 );
				suv += normal.xy * BankRippleDistortion;
				float2 edge = saturate( min( suv, 1.0 - suv ) * 12.0 );
				float onScreen = edge.x * edge.y;
				float3 fb = g_tFrameBufferCopyTexture.SampleLevel( g_sTrilinearMirror, saturate( suv ) * float2( 1.0, 1.0 ), 0 ).rgb;
				bank = lerp( silhouette, fb, onScreen );
			}
			reflection = lerp( reflection, bank, occluded );
		}

		// ---- Fresnel: straight down it is the bed you see, at a grazing angle the mirror
		float flNDotV = saturate( dot( normal, V ) );
		float fresnel = FresnelRough( flNDotV, FresnelF0, reflectRough );
		fresnel = saturate( fresnel * ReflectionStrength );
		// Shade (Valheim reference, Mark 2026-10-22): where the sun is missing the sheen is missing — the surface turns
		// see-through and the bed shows; lit water carries the sky. Soft, because the shade is the water column's.
		fresnel *= 1.0 - (1.0 - ShadeReflection) * saturate( shade );

		// ---- lights: GGX glint from the sun (shadowed), the moon and local lights; their diffuse light on the foam
		float alpha = min( sqrt( SurfaceRoughness * SurfaceRoughness + DistanceRoughness * DistanceRoughness * footprintMeters * footprintMeters ), 0.45 );
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
			glint += col * (SpecularGGX( normal, V, L, alpha, FresnelF0 ) * vis);
			foamLight += col * (saturate( L.z ) * columnVis);   // soft, like the water around it
		}

		uint nLightCount = Light::Count( i.vPositionSs );
		[loop]
		for ( uint n = 0; n < nLightCount; n++ )
		{
			Light light = Light::From( lightPos, i.vPositionSs, n, 0.0f, vReceiverNormalWs );
			bool bDirectional = light.LightData.Type == LightType::LightTypeDirectional;
			if ( bDirectional && g_DirectionalLightEnabled && dot( light.Direction, fastSunDir ) > 0.9999 )
				continue;   // the fast-path sun above
			if ( !bDirectional )
			{
				float3 vToLight = light.Position - lightPos;
				if ( dot( vToLight, vToLight ) > light.LightData.RadiusSquared )
					continue;
			}
			float f = light.Attenuation * light.Visibility;
			if ( f <= 0.0 )
				continue;
			glint += light.Color * (SpecularGGX( normal, V, light.Direction, alpha, FresnelF0 ) * f);
			foamLight += light.Color * (saturate( dot( baseNormal, light.Direction ) ) * f);
		}
		// Far / grazing glints (Mark 2026-10-25: a distant river on a hillside showed two blinding white blobs in a purple
		// bloom): one far pixel covers many ripples, so its sun sparkle spreads into a dim sheen instead of a 40x spike -
		// the cap falls with the pixel's footprint - and the GGX peak at a grazing view is faded out. A NaN (grazing
		// normal / view) is zeroed: bloomed, it smeared colour across the hill.
		float glintCap = lerp( 40.0, 4.0, saturate( footprintMeters / 1.5 ) );
		glint = min( glint * SpecularStrength * smoothstep( 0.02, 0.15, flNDotV ), glintCap );
		if ( any( isnan( glint ) ) || any( isinf( glint ) ) )
			glint = 0;

		// ---- foam: lit white — the sky's light plus the lights that reach it
		// Through the lace (water_common.hlsl), drifting with the current: thick foam solid, thin foam wisps and holes
		float2 laceP = worldPos.xy / UNITS_PER_METER - alongWs * (g_flTime * FlowSpeed * 0.8);
		// Bank foam follows the REAL water depth too: where a tributary flows in, the bank line of this strip crosses deep
		// water, and a foam line drawn there cut the confluence off (Mark 2026-10-25)
		float foamAmount = GetFoam( flowMeters, max( depth01, waterDepthU / (RIVER_DEPTH_COLOR_RANGE_METERS * UNITS_PER_METER) ), speedMul );
		// White water (Mark 2026-10-25, a painting of a mountain river: "should be horizontal white"; foam clumps on 5 m
		// tiles repeated in columns): STANDING WAVES — broken white bands ACROSS the channel that stay where they are (the
		// current piles up over the bed there; it does not carry them), churning inside, shedding foam that streams away
		// downstream and thins out, with clear water between the bands. A stationary world-space field makes some
		// stretches rough and others glassy, so nothing repeats down the river. Bubbles ride only in the white.
		float bubbles = 0.0;
		float whiteFoam = 0.0;
		{
			float fast = smoothstep( 1.15, 2.4, speedMul );
			float2 pm = worldPos.xy / UNITS_PER_METER;
			float rough = g_tCloudNoise.Sample( g_sCloudSampler, pm / 170.0 + float2( 0.31, 0.77 ) ).r;
			float thr = lerp( 0.64, 0.46, rapids ) - (rough - 0.5) * 0.3;
			// Bands: short along the flow (16 m tile on the travel coordinate, which wraps every 64 m), wide across it
			// (45 m and 23 m tiles: wider than the river, so no columns). Not scrolled: they stand.
			float2 bandUv = float2( flowMeters.x / 16.0 + 0.17, flowMeters.y / 45.0 + 0.61 );
			float bandN = g_tCloudNoise.Sample( g_sCloudSampler, bandUv ).r * 0.6
				+ g_tCloudNoise.Sample( g_sCloudSampler, float2( flowMeters.x / 32.0 + 0.53, flowMeters.y / 23.0 + 0.29 ) ).g * 0.4;
			float band = smoothstep( thr - 0.06, thr + 0.3, bandN );
			// The foam a band sheds, a few metres downstream of it (the travel coordinate grows downstream)
			float shed = smoothstep( thr - 0.06, thr + 0.3, g_tCloudNoise.Sample( g_sCloudSampler, bandUv - float2( 3.0 / 16.0, 0.0 ) ).r * 0.6
				+ g_tCloudNoise.Sample( g_sCloudSampler, float2( (flowMeters.x - 3.0) / 32.0 + 0.53, flowMeters.y / 23.0 + 0.29 ) ).g * 0.4 );
			float shedFar = smoothstep( thr - 0.06, thr + 0.3, g_tCloudNoise.Sample( g_sCloudSampler, bandUv - float2( 7.0 / 16.0, 0.0 ) ).r * 0.6
				+ g_tCloudNoise.Sample( g_sCloudSampler, float2( (flowMeters.x - 7.0) / 32.0 + 0.53, flowMeters.y / 23.0 + 0.29 ) ).g * 0.4 );
			// Churn: turbulence racing downstream through the bands and the trails
			float alongC = flowMeters.x - g_flTime * FlowSpeed * 1.4;
			float churn = g_tCloudNoise.Sample( g_sCloudSampler, float2( alongC / 4.0, flowMeters.y / 3.7 + 0.41 ) ).r * 0.6
				+ g_tCloudNoise.Sample( g_sCloudSampler, float2( alongC / 2.0 + 0.3, flowMeters.y / 1.9 + 0.83 ) ).g * 0.4;
			float inBand = band * band * smoothstep( 0.15, 0.6, churn + band * 0.3 );
			float trail = max( shed * shed * 0.6, shedFar * shedFar * 0.3 ) * smoothstep( 0.4, 0.8, churn );
			// Fed to the shared foam lace with the rest of the foam below: thick white solid, its edges torn into wisps
			whiteFoam = saturate( max( inBand, trail ) ) * fast;
			foamAmount = max( foamAmount, whiteFoam * 0.9 );
			// Bubbles in the white water and its trail only
			float density = saturate( max( band, trail ) * lerp( 0.35, 0.8, rapids ) ) * fast;
			bubbles = max( BubbleLayer( flowMeters, 0.4, 1.15, density, footprintMeters, 0.0 ),
				BubbleLayer( flowMeters, 0.2, 1.22, density * 0.9, footprintMeters, 17.0 ) );
		}
		float foam = saturate( FoamThroughLace( foamAmount, FoamLace( laceP, g_flTime ) ) + swim.z + bubbles * 0.9 );
		float skyLum = dot( skyUp, float3( 0.30, 0.59, 0.11 ) );
		// Foam is a near-white diffuse surface: the sky's light plus every light that reaches it, at full strength (scaled
		// down it came out no brighter than the sunlit sand under it, and rings / surf vanished against the bed)
		float3 foamColor = (lerp( skyLum.xxx, skyUp, 0.2 ) + foamLight) * 0.9;

		// ---- compose. The bed (frame-buffer copy) is fogged already; only what the surface adds gets fog here.
		float3 fog0 = Fog::Apply( worldPos, i.vPositionSs.xy, float3( 0, 0, 0 ) );
		float3 fogKeep = Fog::Apply( worldPos, i.vPositionSs.xy, float3( 1, 1, 1 ) ) - fog0;
		float surfaceWeight = saturate( fresnel * (1.0 - foam) + foam );
		float3 surface = reflection * fresnel * (1.0 - foam) + foamColor * foam;
		float3 color = seen * (1.0 - surfaceWeight) + surface * fogKeep + fog0 * surfaceWeight + glint * fogKeep;

		if ( DebugView > 0.5 )
		{
			if ( DebugView < 1.5 ) color = occluded.xxx;
			else if ( DebugView < 2.5 ) color = float3( i.vOccluder / 20.0, 0 );
			else if ( DebugView < 3.5 ) color = float3( acrossWs * 0.5 + 0.5, 0 );
			else if ( DebugView < 4.5 ) color = i.vRiver.rgb;
			else if ( DebugView < 5.5 ) color = float3( halfWidth / 20.0, saturate( flowMeters.y / 20.0 ), saturate( -flowMeters.y / 20.0 ) );
			else color = float3( riverAlpha, saturate( openDepth / 4.0 ), depthValid ? 0.0 : 1.0 );
		}

		// Soft banks: the water fades in over EdgeSoftnessMeters of real depth
		float edgeAlpha = lerp( 1.0, smoothstep( 0.0, EdgeSoftnessMeters * UNITS_PER_METER, waterDepthU ), trust );
		// Fades against the copied scene, alpha 1 (see seawater.shader: hardware alpha broke the frame-buffer copy)
		color = lerp( CopyTap( uv ).rgb, color, edgeAlpha );
		return float4( color, saturate( riverAlpha * i.flJoin ) );
	}
}
