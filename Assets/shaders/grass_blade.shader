//
// grass_blade — single-plane grass clumps for SurvivalGame2026.
//
// Geometry blades (no alpha test), rendered double-sided so a clump vanishes edge-on and
// shows mirrored from behind. Wind is a vertex-shader bend driven by the scene-wide
// attributes that WindSystem publishes every frame (WindDirection / WindStrength / WindGust):
//
//   * vertex colour R = height along the blade (0 root → 1 tip). Displacement scales by
//     R^BendPower, so roots are pinned and tips carry the motion.
//   * vertex colour G = per-blade phase, B = per-clump random (colour variation).
//   * a noise field (three octaves of value noise) drifting along the wind makes patches of gust
//     sweep the field instead of flapping every clump in unison — no visible stripes or spacing;
//     a small cross-wind flutter breaks up the remaining regularity.
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
	float WindStrength < Attribute( "WindStrength" ); Default( 0.25 ); >;
	float WindGust < Attribute( "WindGust" ); Default( 0.5 ); >;

	// Material tuning. Distances are engine units (40 u/m): SwayAmount 24 = a 60 cm tip lean at full strength.
	float SwayAmount < Default( 24.0 ); Range( 0.0, 60.0 ); UiGroup( "Wind,10/10" ); >;
	// How fast the gust patches drift downwind, in engine units per second (400 = 10 m/s). Faster than ~half a patch
	// per second and the blades read as twitching rather than rolling.
	float GustSpeed < Default( 400.0 ); Range( 0.0, 2000.0 ); UiGroup( "Wind,10/20" ); >;
	// Size of the largest gust patches (800 = 20 m). Smaller detail rides on top at 2.3x this frequency.
	float GustLength < Default( 800.0 ); Range( 40.0, 4000.0 ); UiGroup( "Wind,10/30" ); >;
	// 1 = soft rolling patches, 4 = small sharp gust patches with calm between them.
	float GustSharpness < Default( 2.0 ); Range( 1.0, 6.0 ); UiGroup( "Wind,10/40" ); >;
	float FlutterAmount < Default( 0.8 ); Range( 0.0, 10.0 ); UiGroup( "Wind,10/50" ); >;
	float FlutterSpeed < Default( 4.0 ); Range( 0.0, 20.0 ); UiGroup( "Wind,10/60" ); >;
	float BendPower < Default( 2.0 ); Range( 1.0, 4.0 ); UiGroup( "Wind,10/70" ); >;

	// Distance thinning, in metres from the camera (one density is planted everywhere; this is what the eye sees).
	// Every clump stays inside Full Range; between Full and Thin Range the kept share falls to Far Keep; past
	// Cull Range nothing. A dropped clump collapses to its root (no pixels) — smooth, per clump, moves with you.
	float ThinFullMeters < Default( 80.0 ); Range( 0.0, 600.0 ); UiGroup( "Distance,20/10" ); >;
	float ThinFarMeters < Default( 170.0 ); Range( 0.0, 600.0 ); UiGroup( "Distance,20/20" ); >;
	float ThinFarKeep < Default( 0.15 ); Range( 0.0, 1.0 ); UiGroup( "Distance,20/30" ); >;
	float ThinCullMeters < Default( 300.0 ); Range( 0.0, 1000.0 ); UiGroup( "Distance,20/40" ); >;

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

		float flHeight01 = saturate( i.vColor.r );
		float flWeight = pow( flHeight01, BendPower );
		float flPhase = i.vColor.g * 6.2831853;

		// Distance thinning: B is the per-clump random, so the kept share is exact and never flickers.
		float flCamDist = distance( vRootWs, g_vCameraPositionWs ) / 40.0;
		float flKeep = lerp( 1.0, ThinFarKeep, smoothstep( ThinFullMeters, max( ThinFarMeters, ThinFullMeters + 1.0 ), flCamDist ) );
		flKeep *= 1.0 - smoothstep( ThinCullMeters * 0.85, max( ThinCullMeters, 1.0 ), flCamDist );
		// Dropped clumps collapse onto their root: zero area, nothing rasterised — and nothing else computed. The branch is
		// uniform per clump, so the warp takes it as a whole; from 500 m up every clump is culled and the ring cost ~0.
		if ( i.vColor.b >= flKeep )
		{
			o.vPositionWs.xyz = vRootWs;
			o.vPositionPs.xyzw = Position3WsToPs( vRootWs );
			return FinalizeVertex( o );
		}

		float2 vWind = normalize( WindDirection.xy + float2( 1e-4, 0.0 ) );
		float2 vSide = float2( -vWind.y, vWind.x );
		float t = g_flTime;

		// Gust field: value noise over the ground, drifting downwind at GustSpeed (and slowly sideways so the
		// same patch never retraces its path). Three octaves: big patches, mid detail, fine ripples. The same
		// patch sweeps across neighbouring clumps one after another, but no two patches look alike.
		float2 vFieldPos = ( vRootWs.xy - vWind * ( t * GustSpeed ) + vSide * ( t * GustSpeed * 0.11 ) ) / max( GustLength, 1.0 );
		// Two octaves only: a third, finer one made every blade flick as the field slid past (Mark 2026-10-13: twitching).
		float flNoise = ValueNoise( vFieldPos ) * 0.65
		              + ValueNoise( vFieldPos * 2.3 + 7.1 ) * 0.35;
		float flWave = flNoise * 2.0 - 1.0;                                    // -1 … 1, like the old sine
		float flFront = pow( saturate( ( flNoise - 0.3 ) / 0.5 ), GustSharpness ); // gusts live in the upper half of the field

		// Steady lean from the base breeze (patchy) plus the gust on top.
		float flStrength = saturate( WindStrength * ( 0.6 + 0.8 * flNoise ) + WindGust * flFront );
		float flLean = flStrength * SwayAmount;
		// Small cross-wind shimmer per blade; strongest inside a gust.
		float flFlutter = sin( t * FlutterSpeed + flPhase * 2.0 + vRootWs.x * 0.05 ) * FlutterAmount * flStrength;

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

	float3 BaseColor < UiType( Color ); Default3( 0.13, 0.30, 0.07 ); UiGroup( "Grass,10/10" ); >;
	float3 TipColor < UiType( Color ); Default3( 0.56, 0.74, 0.24 ); UiGroup( "Grass,10/20" ); >;
	float ColorVariation < Default( 0.15 ); Range( 0.0, 1.0 ); UiGroup( "Grass,10/30" ); >;
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
		vAlbedo *= 1.0 + ( i.vColor.b - 0.5 ) * 2.0 * ColorVariation;

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
		lt.vIndirectSpecular.rgb *= CalculateSpecularAmbientOcclusion( ci, lt );
		vDirectDiffuse *= lerp( float3( 1.0, 1.0, 1.0 ), vDiffuseAO, ci.flAmbientOcclusionDirectDiffuse );

		float3 vColor = ( vDirectDiffuse + lt.vIndirectDiffuse.rgb ) * ci.vDiffuseColor.rgb + lt.vIndirectSpecular.rgb;
		return DoAtmospherics( m.WorldPosition, m.ScreenPosition.xy, float4( vColor, 1.0 ) );
	}
}
