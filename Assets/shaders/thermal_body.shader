//
// thermal_body — Thermal Eye augment, creature pass.
// Unlit heat signature: the colour comes from where the pixel sits on the body in BIND-POSE
// space (raw vertex position, before skinning), so a T-posed citizen reads head + torso hot
// (red / orange / yellow) and arms, hands, legs and feet cooler (green / blue) whatever pose
// it is animating in. Translucent so it draws after thermal_view's opaque-stage tint.
//
// Per-renderer attributes (set by ThermalVision): ThermalHeight / ThermalRadius / ThermalBaseZ
// from the model bounds, so animals and robots of any size map onto the same 0..1 body space.
//
HEADER
{
	Description = "Thermal Eye — heat signature body";
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
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		// Smuggle the bind-pose position through the vertex colour interpolator — no lighting reads it here.
		o.vVertexColor.rgb = i.vPositionOs.xyz;
		o.vVertexColor.a = 1.0;
		return FinalizeVertex( o );
	}
}

PS
{
	#define BLEND_MODE_ALREADY_SET 1
	#define S_TRANSLUCENT 1

	#include "common/pixel.hlsl"

	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );

	float g_flThermalHeight < Attribute( "ThermalHeight" ); Default( 72.0f ); >;
	float g_flThermalRadius < Attribute( "ThermalRadius" ); Default( 14.0f ); >;
	float g_flThermalBaseZ < Attribute( "ThermalBaseZ" ); Default( 0.0f ); >;

	// Classic FLIR ramp: blue → cyan → green → yellow → orange → red → white-hot.
	float3 HeatPalette( float t )
	{
		const float3 c0 = float3( 0.05, 0.05, 0.45 );
		const float3 c1 = float3( 0.00, 0.55, 1.00 );
		const float3 c2 = float3( 0.00, 0.90, 0.50 );
		const float3 c3 = float3( 1.00, 0.95, 0.10 );
		const float3 c4 = float3( 1.00, 0.35, 0.00 );
		const float3 c5 = float3( 0.85, 0.05, 0.05 );
		const float3 c6 = float3( 1.00, 0.95, 0.90 );

		t = saturate( t );
		if ( t < 0.20 ) return lerp( c0, c1, t / 0.20 );
		if ( t < 0.40 ) return lerp( c1, c2, ( t - 0.20 ) / 0.20 );
		if ( t < 0.60 ) return lerp( c2, c3, ( t - 0.40 ) / 0.20 );
		if ( t < 0.75 ) return lerp( c3, c4, ( t - 0.60 ) / 0.15 );
		if ( t < 0.90 ) return lerp( c4, c5, ( t - 0.75 ) / 0.15 );
		return lerp( c5, c6, ( t - 0.90 ) / 0.10 );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 os = i.vVertexColor.rgb;

		// 0 = soles, 1 = crown; 0 = spine, ~1 = torso edge, >1.5 = along the arms in bind pose.
		float h = saturate( ( os.z - g_flThermalBaseZ ) / max( 1.0, g_flThermalHeight ) );
		float r = length( os.xy ) / max( 1.0, g_flThermalRadius );

		// Height bands, cumulative: feet ~0.25 → legs ~0.40 → torso ~0.75 → head ~0.95.
		float heat = 0.0;
		heat += smoothstep( 0.00, 0.12, h ) * 0.25;
		heat += smoothstep( 0.12, 0.48, h ) * 0.15;
		heat += smoothstep( 0.48, 0.60, h ) * 0.35;
		heat += smoothstep( 0.84, 0.92, h ) * 0.20;

		// Limbs cool with distance from the core: arms out to the sides, spread legs.
		float limb = smoothstep( 0.9, 1.6, r );
		heat = lerp( heat, min( heat, 0.35 ), limb );

		// Hands and fingertips are the coldest extremity.
		heat -= smoothstep( 1.6, 2.6, r ) * 0.10;

		// Skin edges read cooler on a thermal camera.
		float3 n = normalize( i.vNormalWs );
		float3 v = normalize( -i.vPositionWithOffsetWs.xyz );
		float rim = 1.0 - saturate( dot( n, v ) );
		heat -= rim * rim * 0.12;

		// A little per-vertex mottling so the body is not a flat gradient.
		float noise = frac( sin( dot( os.xy, float2( 12.9898, 78.233 ) ) ) * 43758.5453 );
		heat += ( noise - 0.5 ) * 0.04;

		return float4( HeatPalette( heat ), 1.0 );
	}
}
