//
// grass_blade — low-poly grass tufts for SurvivalGame2026 (Blender/scripts/create_grass_clump.py).
//
// Geometry blades (no alpha test), folded along the midrib and standing round the root, rendered double-sided. Wind is a
// vertex-shader bend driven by what WindSystem publishes every frame:
//
//   * vertex colour R = height along the blade (0 root → 1 tip). Displacement scales by
//     R^BendPower, so roots are pinned and tips carry the motion.
//   * UV: u across the blade (0 edge, 0.5 midrib, 1 edge), v up it. TextureDetail (grass_blade_detail.png, 128 px,
//     point sampled) multiplies the Base -> Tip colour x2: lengthwise fibres, a pale midrib, dry nicks (Mark 2026-11-23:
//     "theres no textures on the grass either, can you add some?").
//   * vertex colour G = per-blade phase. Everything "per clump" (size, colour, distance thinning) is hashed from the
//     instance's root position here, so two copies of one model never look alike (B is no longer read: it was baked per
//     MODEL, so each of the four models had one colour and one thinning rank - Mark 2026-11-18: "too similar").
//   * the breeze (WindStrength) is a gentle patchy lean: a value-noise field sliding downwind by WindFieldOffset, which
//     WindSystem accumulates (the old t x direction product swung the whole field round whenever the heading wandered).
//   * gusts are ORBS (Mark 2026-11-18: "invisible orbs that float through the fields and make the grass wiggle when it
//     hits them"): up to eight drifting discs (WindOrb0..7: centre xy, radius, strength). Grass inside one leans with
//     the wind and shivers; grass outside only has the breeze. No scene-wide gust: that pumped every clump at once.
//
// Lit like the ground it stands on (Mark 2026-10-21: "it should have the same properties as the terrain so it
// doesn't stand out"): GrassScatterer tilts every instance onto the ground normal, the vertex stage stands the
// blades back upright about their root and hands that normal to the pixel stage, which lights it the way
// pixel_lit.shader lights terrain_ground.vmat — Lambert sun / moon + local lights (6 bands, like the ground), no
// specular, roughness 1, the engine's sky ambient and AO. Only the albedo (Base → Tip) and the root AO differ.
//
// Written against the "common/*.hlsl" path like tempwater.shader; the vertex block follows
// the core foliage.shader pattern (bend in world space after the instance transform, so the
// same shader works on Clutter batches and on ordinary ModelRenderers).
//
HEADER
{
	Description = "Grass blade clump with wind sway";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"

	float4 vColor : COLOR0 < Semantic( Color ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	float4 vColor : COLOR0;
	// The ground normal under the clump (the instance's up axis) — the pixel stage lights the blade with it.
	float3 vGroundNormalWs : TEXCOORD9;
};

VS
{
	#include "common/vertex.hlsl"

	// Set once per frame by Survival.WindSystem — on Scene.RenderAttributes and directly on the grass material.
	float3 WindDirection < Attribute( "WindDirection" ); Default3( 1.0, 0.0, 0.0 ); >;
	float WindStrength < Attribute( "WindStrength" ); Default( 0.0 ); >;
	// How far the breeze field has slid downwind so far, engine units (WindSystem adds direction x speed every frame)
	float3 WindFieldOffset < Attribute( "WindFieldOffset" ); Default3( 0.0, 0.0, 0.0 ); >;
	// Gust orbs: xy = centre (engine units), z = radius (engine units), w = strength (0 = unused)
	float4 WindOrb0 < Attribute( "WindOrb0" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 WindOrb1 < Attribute( "WindOrb1" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 WindOrb2 < Attribute( "WindOrb2" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 WindOrb3 < Attribute( "WindOrb3" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 WindOrb4 < Attribute( "WindOrb4" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 WindOrb5 < Attribute( "WindOrb5" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 WindOrb6 < Attribute( "WindOrb6" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 WindOrb7 < Attribute( "WindOrb7" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;

	// Material tuning. Distances are engine units (40 u/m): SwayAmount 24 = a 60 cm tip lean at full strength.
	float SwayAmount < Default( 24.0 ); Range( 0.0, 60.0 ); UiGroup( "Wind,10/10" ); >;
	// Size of the breeze's patches (800 = 20 m). Smaller detail rides on top at 2.3x this frequency.
	float BreezePatchLength < Default( 800.0 ); Range( 40.0, 4000.0 ); UiGroup( "Wind,10/30" ); >;
	// Tip lean inside a gust orb at strength 1, as a multiple of SwayAmount
	float OrbLean < Default( 1.0 ); Range( 0.0, 3.0 ); UiGroup( "Wind,10/40" ); >;
	// The shiver inside an orb: blades whip side to side this much (x SwayAmount) at OrbWiggleSpeed
	float OrbWiggle < Default( 0.35 ); Range( 0.0, 2.0 ); UiGroup( "Wind,10/42" ); >;
	float OrbWiggleSpeed < Default( 9.0 ); Range( 0.0, 30.0 ); UiGroup( "Wind,10/44" ); >;
	// The breeze rocks every tuft the way it rocks the trees (pixel_lit F_WIND: lean downwind, swing back past upright):
	// RockAmount of the lean comes and goes at RockHz; neighbours within RockWaveMeters swing nearly together (the phase is
	// a noise field over the ground, not a wave along the wind: that swung the whole field whenever the heading wandered)
	float RockAmount < Default( 0.8 ); Range( 0.0, 1.5 ); UiGroup( "Wind,10/20" ); >;
	float RockHz < Default( 0.45 ); Range( 0.0, 3.0 ); UiGroup( "Wind,10/22" ); >;
	float RockWaveMeters < Default( 9.0 ); Range( 1.0, 80.0 ); UiGroup( "Wind,10/24" ); >;
	float FlutterAmount < Default( 0.8 ); Range( 0.0, 10.0 ); UiGroup( "Wind,10/50" ); >;
	float FlutterSpeed < Default( 4.0 ); Range( 0.0, 20.0 ); UiGroup( "Wind,10/60" ); >;
	float BendPower < Default( 2.0 ); Range( 1.0, 4.0 ); UiGroup( "Wind,10/70" ); >;

	// Distance thinning, in metres from the camera (one density is planted everywhere; this is what the eye sees).
	// Every clump stays inside Full Range; between Full and Thin Range the kept share falls to Far Keep; past
	// Cull Range nothing. A dropped clump collapses to its root (no pixels) — smooth, per clump, moves with you.
	// Cull Range has to sit inside the clutter ring (Grass — Range (m), 77 m since 2026-11-19) or the ring's edge pops.
	float ThinFullMeters < Default( 35.0 ); Range( 0.0, 600.0 ); UiGroup( "Distance,20/10" ); >;
	float ThinFarMeters < Default( 58.0 ); Range( 0.0, 600.0 ); UiGroup( "Distance,20/20" ); >;
	float ThinFarKeep < Default( 0.35 ); Range( 0.0, 1.0 ); UiGroup( "Distance,20/30" ); >;
	float ThinCullMeters < Default( 68.0 ); Range( 0.0, 1000.0 ); UiGroup( "Distance,20/40" ); >;
	// A clump about to be kept grows out of the ground instead of appearing whole: this much of the keep share
	// (0-1) is spent rising from nothing to full height. 0 = the old hard pop.
	float ThinGrowBand < Default( 0.15 ); Range( 0.0, 0.5 ); UiGroup( "Distance,20/50" ); >;

	// Per-clump size, hashed from the root position: every tuft gets its own height and girth on top of the scatterer's
	// uniform scale, so the same model reads as a squat tuft here and a tall thin one there.
	float HeightVariation < Default( 0.35 ); Range( 0.0, 0.9 ); UiGroup( "Shape,30/10" ); >;
	float WidthVariation < Default( 0.3 ); Range( 0.0, 0.9 ); UiGroup( "Shape,30/20" ); >;

	// How deep inside one gust orb a root stands, times the orb's strength (0 outside it)
	float OrbAmount( float4 orb, float2 root )
	{
		float d = length( root - orb.xy ) / max( orb.z, 1.0 );
		float f = saturate( 1.0 - d );
		return f * f * ( 3.0 - 2.0 * f ) * orb.w;
	}

	// Cheap 2D value noise for the gust field — three octaves of this read as drifting patches of wind
	// instead of evenly spaced sine stripes (Mark 2026-10-13: the bands were too regular).
	float Hash21( float2 p )
	{
		p = frac( p * float2( 123.34, 456.21 ) );
		p += dot( p, p + 45.32 );
		return frac( p.x * p.y );
	}

	float ValueNoise( float2 p )
	{
		float2 i = floor( p );
		float2 f = frac( p );
		f = f * f * ( 3.0 - 2.0 * f );
		float a = Hash21( i );
		float b = Hash21( i + float2( 1.0, 0.0 ) );
		float c = Hash21( i + float2( 0.0, 1.0 ) );
		float d = Hash21( i + float2( 1.0, 1.0 ) );
		return lerp( lerp( a, b, f.x ), lerp( c, d, f.x ), f.y );
	}

	// Rotate v by the shortest arc that takes the unit vector vUp onto +Z (Rodrigues; vUp.z > 0 — grass never
	// grows past Max Slope).
	float3 RotateUpToZ( float3 v, float3 vUp )
	{
		float3 vAxis = cross( vUp, float3( 0.0, 0.0, 1.0 ) );
		return v * vUp.z + cross( vAxis, v ) + vAxis * ( dot( vAxis, v ) / ( 1.0 + vUp.z ) );
	}

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		o.vColor = i.vColor;

		float3x4 matObjectToWorld = GetTransformMatrix( i.nInstanceTransformID );
		float3 vRootWs = mul( matObjectToWorld, float4( 0.0, 0.0, 0.0, 1.0 ) );
		float3 vPositionWs = mul( matObjectToWorld, float4( i.vPositionOs.xyz, 1.0 ) );

		// The instance leans with the ground only to carry its normal: stand the blades back up about the root.
		float3 vUpWs = normalize( mul( matObjectToWorld, float4( 0.0, 0.0, 1.0, 0.0 ) ) );
		o.vGroundNormalWs = vUpWs;
		vPositionWs = vRootWs + RotateUpToZ( vPositionWs - vRootWs, vUpWs );
		o.vNormalWs = RotateUpToZ( o.vNormalWs, vUpWs );

		// Three per-clump randoms from where the clump stands (stable: a root never moves)
		float flRandA = Hash21( vRootWs.xy * 0.0731 );
		float flRandB = Hash21( vRootWs.yx * 0.0417 + 19.7 );
		float flRandC = Hash21( vRootWs.xy * 0.0293 + 61.3 );
		o.vColor.b = flRandC;   // the pixel stage's colour variation

		// Its own height and girth
		float3 vLocal = vPositionWs - vRootWs;
		vLocal.xy *= 1.0 + ( flRandA * 2.0 - 1.0 ) * WidthVariation;
		vLocal.z *= 1.0 + ( flRandB * 2.0 - 1.0 ) * HeightVariation;
		vPositionWs = vRootWs + vLocal;

		float flHeight01 = saturate( i.vColor.r );
		float flWeight = pow( flHeight01, BendPower );
		float flPhase = i.vColor.g * 6.2831853;

		// Distance thinning by the per-clump random, so the kept share is exact and never flickers.
		float flCamDist = distance( vRootWs, g_vCameraPositionWs ) / 40.0;
		float flKeep = lerp( 1.0, ThinFarKeep, smoothstep( ThinFullMeters, max( ThinFarMeters, ThinFullMeters + 1.0 ), flCamDist ) );
		flKeep *= 1.0 - smoothstep( ThinCullMeters * 0.85, max( ThinCullMeters, 1.0 ), flCamDist );
		// Dropped clumps collapse onto their root: zero area, nothing rasterised — and nothing else computed. The branch is
		// uniform per clump, so the warp takes it as a whole; from 500 m up every clump is culled and the ring cost ~0.
		// The keep share is stretched by the grow band so that at full keep every clump stands at full height.
		float flBand = max( ThinGrowBand, 1e-4 );
		float flKeepSoft = flKeep * ( 1.0 + flBand );
		if ( flRandC >= flKeepSoft )
		{
			o.vPositionWs.xyz = vRootWs;
			o.vPositionPs.xyzw = Position3WsToPs( vRootWs );
			return FinalizeVertex( o );
		}

		// Fade in: a clump that has just crossed its keep threshold rises out of the ground (and fills out) over the
		// band, so walking forward grows the far grass in rather than popping whole tufts (Mark 2026-11-19).
		float flGrow = saturate( ( flKeepSoft - flRandC ) / flBand );
		flGrow = flGrow * flGrow * ( 3.0 - 2.0 * flGrow );
		vLocal.xy *= lerp( 0.5, 1.0, flGrow );
		vLocal.z *= flGrow;
		vPositionWs = vRootWs + vLocal;

		float2 vWind = normalize( WindDirection.xy + float2( 1e-4, 0.0 ) );
		float2 vSide = float2( -vWind.y, vWind.x );
		float t = g_flTime;

		// The breeze: value noise over the ground, sliding downwind (WindFieldOffset). Two octaves: big patches and mid
		// detail - a third, finer one made every blade flick as the field slid past (Mark 2026-10-13: twitching).
		float2 vFieldPos = ( vRootWs.xy - WindFieldOffset.xy ) / max( BreezePatchLength, 1.0 );
		float flNoise = ValueNoise( vFieldPos ) * 0.65
		              + ValueNoise( vFieldPos * 2.3 + 7.1 ) * 0.35;
		// The lean answers a light wind strongly and levels off in a gale (0.25 -> 0.42, 1.0 -> 0.89): linear in the wind, a
		// clear day's 0.25 moved the tips a few centimetres while the trees beside them swung
		float flBreeze = ( 1.0 - exp( -2.2 * max( WindStrength, 0.0 ) ) ) * ( 0.35 + 0.9 * flNoise );
		// Held still, that lean is a field of bent but motionless grass under swinging trees (Mark 2026-11-18: "grass is
		// barely moving at all while trees are really going"). Rock it on the trees' beat, patch by patch, each
		// tuft a little off its neighbours.
		float flRockPhase = 6.2831853 * ( t * RockHz + 2.0 * ValueNoise( vRootWs.xy / max( RockWaveMeters * 40.0, 1.0 ) + 3.7 ) ) + flRandA * 2.2;
		float flRock = 1.0 + RockAmount * ( sin( flRockPhase ) * 0.75 + sin( flRockPhase * 2.7 + flRandB * 6.2831853 ) * 0.25 );

		// Gust orbs: only grass an orb is passing over gets more than the breeze
		float flOrb = OrbAmount( WindOrb0, vRootWs.xy ) + OrbAmount( WindOrb1, vRootWs.xy )
		            + OrbAmount( WindOrb2, vRootWs.xy ) + OrbAmount( WindOrb3, vRootWs.xy )
		            + OrbAmount( WindOrb4, vRootWs.xy ) + OrbAmount( WindOrb5, vRootWs.xy )
		            + OrbAmount( WindOrb6, vRootWs.xy ) + OrbAmount( WindOrb7, vRootWs.xy );
		flOrb = min( flOrb, 2.5 );

		float flLean = ( flBreeze * flRock + flOrb * OrbLean ) * SwayAmount;
		// Small cross-wind shimmer per blade in the breeze; inside an orb every blade whips on its own beat
		float flFlutter = sin( t * FlutterSpeed + flPhase * 2.0 + flRandA * 6.2831853 ) * FlutterAmount * flBreeze
		                + sin( t * OrbWiggleSpeed * ( 0.8 + 0.4 * flRandB ) + flPhase * 3.0 ) * OrbWiggle * SwayAmount * flOrb;
		// ...and nods along the wind as well, so the patch boils instead of combing flat
		flLean += sin( t * OrbWiggleSpeed * 0.63 * ( 0.8 + 0.4 * flRandA ) + flPhase * 2.0 + 1.7 ) * OrbWiggle * 0.6 * SwayAmount * flOrb;

		float3 vOffset = float3( vWind * flLean + vSide * flFlutter, 0.0 ) * flWeight;
		// A leaning blade does not get longer: drop the tip a little as it moves sideways.
		vOffset.z -= length( vOffset.xy ) * 0.15;

		o.vPositionWs.xyz = vPositionWs + vOffset;
		o.vPositionPs.xyzw = Position3WsToPs( o.vPositionWs.xyz );

		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/pixel.hlsl"
	#include "common/classes/Light.hlsl"

	// Both faces of every blade: the clump is a single plane and must read from behind.
	RenderState( CullMode, NONE );

	// The blade's texture: a tint over Base -> Tip, x2 (linear 0.5 = unchanged). Nearest texel, like every other surface.
	CreateInputTexture2D( TextureDetail, Srgb, 8, "", "_color", "Grass,10/5", Default3( 0.735, 0.735, 0.735 ) );
	Texture2D g_tDetail < Channel( RGB, Box( TextureDetail ), Srgb ); OutputFormat( RGBA8888 ); SrgbRead( true ); >;
	SamplerState g_sDetailSampler < Filter( POINT ); AddressU( WRAP ); AddressV( WRAP ); >;
	float DetailStrength < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Grass,10/6" ); >;

	float3 BaseColor < UiType( Color ); Default3( 0.13, 0.30, 0.07 ); UiGroup( "Grass,10/10" ); >;
	float3 TipColor < UiType( Color ); Default3( 0.56, 0.74, 0.24 ); UiGroup( "Grass,10/20" ); >;
	float ColorVariation < Default( 0.15 ); Range( 0.0, 1.0 ); UiGroup( "Grass,10/30" ); >;
	// Some clumps dry out: the per-clump random drifts the hue toward this (yellow-green straw), up to DryAmount
	float3 DryColor < UiType( Color ); Default3( 0.13, 0.13, 0.045 ); UiGroup( "Grass,10/40" ); >;
	float DryAmount < Default( 0.35 ); Range( 0.0, 1.0 ); UiGroup( "Grass,10/50" ); >;
	// Low-poly facets: each flat face is this much brighter / darker by which way it faces round the compass
	float FacetShade < Default( 0.22 ); Range( 0.0, 1.0 ); UiGroup( "Grass,10/70" ); >;
	float RootDarkening < Default( 0.45 ); Range( 0.0, 1.0 ); UiGroup( "Grass,10/60" ); >;
	// Local lights (torches, fires) in brightness bands — pixel_lit's PixelLightSteps, which the ground uses at 6.
	float LightSteps < Default( 6.0 ); Range( 0.0, 16.0 ); UiGroup( "Lighting,20/10" ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::Init( i );
		// Before any branch: derivatives.
		float3 vReceiverNormalWs = ComputeShadowReceiverNormal( m.WorldPosition );

		// The ground's normal, not the blade's: a clump is lit exactly like the terrain under it.
		m.Normal = normalize( i.vGroundNormalWs );

		float flHeight01 = saturate( i.vColor.r );
		float3 vAlbedo = lerp( BaseColor, TipColor, flHeight01 );
		vAlbedo *= lerp( float3( 1.0, 1.0, 1.0 ), g_tDetail.Sample( g_sDetailSampler, i.vTextureCoords.xy ).rgb * 2.0, DetailStrength );
		// Per clump: brightness, and a drift toward dry straw on the clumps whose random lands high
		vAlbedo *= 1.0 + ( i.vColor.b - 0.5 ) * 2.0 * ColorVariation;
		vAlbedo = lerp( vAlbedo, DryColor * ( 0.55 + 0.45 * flHeight01 ), saturate( ( frac( i.vColor.b * 7.31 ) - 0.6 ) * 2.5 ) * DryAmount );
		// Facets: the face's own direction (from the surface derivatives - the vertex normal is the ground's for lighting)
		// against a fixed compass bearing, so the two halves of a folded blade are two tones in any light
		float3 vFace = normalize( cross( ddx( m.WorldPosition ), ddy( m.WorldPosition ) ) + float3( 0.0, 0.0, 1e-6 ) );
		vAlbedo *= 1.0 + ( abs( dot( vFace.xy, float2( 0.8, 0.6 ) ) ) - 0.5 ) * 2.0 * FacetShade;

		m.Albedo = vAlbedo;
		m.Metalness = 0.0;
		m.Roughness = 1.0;   // the ground's PixelSpecular 0: no highlight, flattest sky reflection
		m.AmbientOcclusion = lerp( 1.0 - RootDarkening, 1.0, flHeight01 );
		m.Opacity = 1.0;

		// Direct light as pixel_lit.shader does it for terrain_ground.vmat: Lambert, no specular.
		float3 N = m.Normal;
		float3 vDirectDiffuse = 0.0;
		bool bSunDone = false;
		if ( g_DirectionalLightEnabled )
		{
			bSunDone = true;
			float3 L = normalize( -g_DirectionalLightDirection.xyz );
			float flVis = 1.0;
			if ( g_DirectionalLightCascadeCount > 0 )
				flVis = DirectionalLightShadow::GetVisibility( m.WorldPosition, vReceiverNormalWs, i.vPositionSs );
			vDirectDiffuse += g_DirectionalLightColor.rgb * ( saturate( dot( N, L ) ) * flVis );
		}

		uint nLightCount = Light::Count( i.vPositionSs );
		[loop]
		for ( uint n = 0; n < nLightCount; n++ )
		{
			Light light = Light::From( m.WorldPosition, i.vPositionSs, n, i.vLightmapUV, vReceiverNormalWs );
			bool bDirectional = light.LightData.Type == LightType::LightTypeDirectional;
			if ( bDirectional && bSunDone )
				continue; // already lit through the constants above
			if ( !bDirectional )
			{
				float3 vToLight = light.Position - m.WorldPosition;
				if ( dot( vToLight, vToLight ) > light.LightData.RadiusSquared )
					continue;
			}
			float f = saturate( dot( N, light.Direction ) ) * light.Attenuation * light.Visibility;
			if ( !bDirectional && LightSteps >= 1.0 )
				f = floor( f * LightSteps + 0.5 ) / LightSteps;
			vDirectDiffuse += light.Color * f;
		}

		// Indirect (sky ambient, probes, AO) from the engine, like the ground.
		CombinerInput ci = ShadingModelStandard::MaterialToCombinerInput( m );
		LightingTerms_t lt = InitLightingTerms();
		CalculateIndirectLighting( lt, ci );
		float3 vDiffuseAO = CalculateDiffuseAmbientOcclusion( ci, lt );
		lt.vIndirectDiffuse.rgb *= vDiffuseAO;
		vDirectDiffuse *= lerp( float3( 1.0, 1.0, 1.0 ), vDiffuseAO, ci.flAmbientOcclusionDirectDiffuse );

		// No indirect specular: at roughness 1 it was still a flat grey-blue sky term added on top, which washed every blade
		// out to a pale sage beside the ground it stands on (Mark 2026-11-18: "kinda the wrong color")
		float3 vColor = ( vDirectDiffuse + lt.vIndirectDiffuse.rgb ) * ci.vDiffuseColor.rgb;
		return DoAtmospherics( m.WorldPosition, m.ScreenPosition.xy, float4( vColor, 1.0 ) );
	}
}
