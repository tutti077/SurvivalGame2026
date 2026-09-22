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
//   * a travelling wave along the wind direction ripples the field instead of flapping
//     every clump in unison; a small cross-wind flutter breaks up the remaining regularity.
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

	#if ( PROGRAM == VFX_PROGRAM_PS )
		bool bIsFrontface : SV_IsFrontFace;
	#endif
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
	// How fast gust bands travel downwind, in engine units per second (200 = 5 m/s).
	float GustSpeed < Default( 200.0 ); Range( 0.0, 1000.0 ); UiGroup( "Wind,10/20" ); >;
	// Distance between gust bands along the wind (480 = 12 m).
	float GustLength < Default( 480.0 ); Range( 40.0, 4000.0 ); UiGroup( "Wind,10/30" ); >;
	// 1 = soft rolling waves, 4 = narrow sharp gust bands with calm between them.
	float GustSharpness < Default( 2.0 ); Range( 1.0, 6.0 ); UiGroup( "Wind,10/40" ); >;
	float FlutterAmount < Default( 0.8 ); Range( 0.0, 10.0 ); UiGroup( "Wind,10/50" ); >;
	float FlutterSpeed < Default( 4.0 ); Range( 0.0, 20.0 ); UiGroup( "Wind,10/60" ); >;
	float BendPower < Default( 2.0 ); Range( 1.0, 4.0 ); UiGroup( "Wind,10/70" ); >;

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		o.vColor = i.vColor;

		float3x4 matObjectToWorld = GetTransformMatrix( i.nInstanceTransformID );
		float3 vRootWs = mul( matObjectToWorld, float4( 0.0, 0.0, 0.0, 1.0 ) );
		float3 vPositionWs = mul( matObjectToWorld, float4( i.vPositionOs.xyz, 1.0 ) );

		float flHeight01 = saturate( i.vColor.r );
		float flWeight = pow( flHeight01, BendPower );
		float flPhase = i.vColor.g * 6.2831853;

		float2 vWind = normalize( WindDirection.xy + float2( 1e-4, 0.0 ) );
		float2 vSide = float2( -vWind.y, vWind.x );
		float t = g_flTime;

		// Gust bands travelling downwind: the band position is the clump's distance along the wind
		// minus how far the wind has travelled, so the same band sweeps across neighbouring clumps
		// one after another. Two wavelengths so the bands are not evenly spaced.
		float flAlong = dot( vRootWs.xy, vWind ) - t * GustSpeed;
		float flWave = sin( flAlong / GustLength * 6.2831853 + flPhase * 0.15 )
		             + 0.5 * sin( flAlong / ( GustLength * 0.41 ) * 6.2831853 + 1.3 );
		float flFront = pow( saturate( 0.5 + 0.5 * flWave * 0.6667 ), GustSharpness );

		// Steady lean from the base breeze (breathing a little) plus the gust band on top.
		float flStrength = saturate( WindStrength * ( 0.7 + 0.3 * flWave * 0.6667 ) + WindGust * flFront );
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

	// Both faces of every blade: the clump is a single plane and must read from behind.
	RenderState( CullMode, NONE );

	float3 BaseColor < UiType( Color ); Default3( 0.13, 0.30, 0.07 ); UiGroup( "Grass,10/10" ); >;
	float3 TipColor < UiType( Color ); Default3( 0.56, 0.74, 0.24 ); UiGroup( "Grass,10/20" ); >;
	float ColorVariation < Default( 0.15 ); Range( 0.0, 1.0 ); UiGroup( "Grass,10/30" ); >;
	// 1 = shade every blade as if it faced straight up (soft, matches the ground); 0 = true card normal.
	float NormalUp < Default( 0.75 ); Range( 0.0, 1.0 ); UiGroup( "Grass,10/40" ); >;
	float GrassRoughness < Default( 0.85 ); Range( 0.0, 1.0 ); UiGroup( "Grass,10/50" ); >;
	float RootDarkening < Default( 0.45 ); Range( 0.0, 1.0 ); UiGroup( "Grass,10/60" ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::Init( i );

		float3 vNormal = normalize( i.vNormalWs );
		if ( !i.bIsFrontface )
			vNormal = -vNormal;
		m.Normal = normalize( lerp( vNormal, float3( 0.0, 0.0, 1.0 ), NormalUp ) );

		float flHeight01 = saturate( i.vColor.r );
		float3 vAlbedo = lerp( BaseColor, TipColor, flHeight01 );
		vAlbedo *= 1.0 + ( i.vColor.b - 0.5 ) * 2.0 * ColorVariation;

		m.Albedo = vAlbedo;
		m.Metalness = 0.0;
		m.Roughness = GrassRoughness;
		m.AmbientOcclusion = lerp( 1.0 - RootDarkening, 1.0, flHeight01 );
		m.Opacity = 1.0;

		// Shade() already applies scene fog (DoAtmospherics) — do not fog again here.
		return ShadingModelStandard::Shade( i, m );
	}
}
